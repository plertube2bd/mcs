using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// GuiLib object: a Board (objective "<ns>.board.<id>") or a BossBar ("<ns>:<id>"). Identity is the id.
sealed class GuiVar(bool isBar, string target) : Var
{
    public bool IsBar = isBar;
    public string Target = target;
}

/// GuiLib: scoreboard display slots, Board (sidebar / list / below name) and BossBar.
public sealed partial class Codegen
{
    readonly Dictionary<string, (bool isBar, string title, bool explicitTitle)> guiDefs = new();
    readonly Dictionary<IFieldSymbol, GuiVar> guiStatics = new(SymbolEqualityComparer.Default);

    /// new Board("id", "title") / new BossBar("id", "title"). Only needs constants, so it also works for static fields.
    GuiVar CreateGui(SemanticModel model, BaseObjectCreationExpressionSyntax oc)
    {
        bool isBar = Intrinsic(model.GetSymbolInfo(oc).Symbol) == "gui.bossbar";
        var args = oc.ArgumentList?.Arguments ?? default;
        string? Const(int i) => i < args.Count && model.GetConstantValue(args[i].Expression) is { HasValue: true } cv ? cv.Value as string : null;
        string id = Const(0) ?? throw new UnsupportedException(oc, "The id must be a string constant.");
        if (!Regex.IsMatch(id, "^[a-z0-9_.-]+$"))
            throw new UnsupportedException(args[0], $"Invalid id '{id}': use lowercase letters, digits, '_', '.' or '-'.");
        if (args.Count > 1 && Const(1) == null && !args[1].Expression.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.NullLiteralExpression))
            throw new UnsupportedException(args[1], "The title must be a string constant (set Title later for runtime text).");
        string? title = Const(1);
        string target = isBar ? $"{ns}:{id}" : $"{ns}.board.{id}";
        if (guiDefs.TryGetValue(target, out var old))
        {
            if (old.isBar != isBar) throw new UnsupportedException(oc, $"'{id}' is already used by a {(old.isBar ? "BossBar" : "Board")}.");
            if (title != null && old.explicitTitle && old.title != title)
                throw new UnsupportedException(args[1], $"'{id}' was already created with the title \"{old.title}\".");
        }
        if (!guiDefs.ContainsKey(target) || (title != null && !guiDefs[target].explicitTitle))
            guiDefs[target] = (isBar, title ?? id, title != null);
        return new GuiVar(isBar, target);
    }

    void DeclareGuiStatic(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        if (v.Initializer?.Value is not BaseObjectCreationExpressionSyntax { Initializer: null } oc)
        {
            Error(v, $"A static {f.Type.Name} needs an initializer like 'new {f.Type.Name}(\"id\", \"Title\")' (no {{ ... }} block — set properties in a method).");
            return;
        }
        try { guiStatics[f] = CreateGui(model, oc); }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
    }

    GuiVar GetGui(ExpressionSyntax e) => EvalLibObject(e, VK.Gui) as GuiVar ?? throw new UnsupportedException(e, "Expected a Board or BossBar.");

    string Slot(ExpressionSyntax? e, ITypeSymbol slotType) => (e == null ? "Sidebar" : EnumMember(e, slotType)) switch
    {
        "Sidebar" => "sidebar",
        "List" => "list",
        _ => prof.BelowNameSlot,
    };

    static string CheckLine(string line, SyntaxNode at) =>
        line.Length == 0 || line.Any(char.IsWhiteSpace) ? throw new UnsupportedException(at, $"Board line names cannot be empty or contain spaces: \"{line}\"") : line;

    Operand? EmitGui(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        var m = (IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!;
        ExpressionSyntax? Arg(int i) => i < args.Count ? args[i].Expression : null;
        GuiVar Recv() => GetGui(((MemberAccessExpressionSyntax)inv.Expression).Expression);
        switch (name)
        {
            case "gui.setdisplay":
            {
                string slot = Slot(Arg(0), m.Parameters[0].Type);
                string obj = Kind(m.Parameters[1].Type) == VK.String ? ConstStr(Arg(1)!) : GetGui(Arg(1)!) is { IsBar: false } b ? b.Target : throw new UnsupportedException(Arg(1)!, "Only a Board can be displayed in a slot.");
                Emit($"scoreboard objectives setdisplay {slot} {obj}");
                return null;
            }
            case "gui.cleardisplay":
                Emit($"scoreboard objectives setdisplay {Slot(Arg(0), m.Parameters[0].Type)}");
                return null;
            case "board.show":
                Emit($"scoreboard objectives setdisplay {Slot(Arg(0), m.Parameters[0].Type)} {Recv().Target}");
                return null;
            case "board.hide":
                Recv();
                Emit($"scoreboard objectives setdisplay {Slot(Arg(0), m.Parameters[0].Type)}");
                return null;
            case "board.set":
            {
                string obj = Recv().Target;
                string who = Kind(m.Parameters[0].Type) == VK.String ? CheckLine(ConstStr(Arg(0)!), Arg(0)!) : GetRef(Arg(0)!).Selector;
                var v = EvalNum(Arg(1)!, VK.Int);
                Emit(v is ConstOp c ? $"scoreboard players set {who} {obj} {c.Value}" : $"scoreboard players operation {who} {obj} = {((ScoreOp)v).Holder}");
                return null;
            }
            case "board.get":
            {
                string obj = Recv().Target;
                string t = Temp();
                Emit($"scoreboard players operation {t} = {CheckLine(ConstStr(Arg(0)!), Arg(0)!)} {obj}");
                return new ScoreOp(t);
            }
            case "board.remove":
                Emit($"scoreboard players reset {CheckLine(ConstStr(Arg(0)!), Arg(0)!)} {Recv().Target}");
                return null;
            case "board.clear":
                Emit($"scoreboard players reset * {Recv().Target}");
                return null;
            case "bossbar.show":
                Emit($"bossbar set {Recv().Target} players {GetRef(Arg(0)!).Selector}");
                return null;
            case "bossbar.hide":
                Emit($"bossbar set {Recv().Target} players");
                return null;
            case "bossbar.remove":
                Emit($"bossbar remove {Recv().Target}");
                return null;
        }
        throw new UnsupportedException(inv, $"Unknown GuiLib method: {name}");
    }

    void EmitGuiSet(GuiVar g, IPropertySymbol p, ExpressionSyntax value)
    {
        string id = g.Target;
        switch (p.Name)
        {
            case "Title" when !g.IsBar:
                Emit($"scoreboard objectives modify {id} displayname {Json.Str(ConstStr(value))}");
                return;
            case "Title":
                Emit($"bossbar set {id} name [\"\"{string.Concat(Components(value).Select(c => "," + c))}]");
                return;
            case "Value" or "Max":
            {
                string prop = p.Name.ToLowerInvariant();
                var v = EvalNum(value, VK.Int);
                Emit(v is ConstOp c ? $"bossbar set {id} {prop} {c.Value}" : $"execute store result bossbar {id} {prop} run scoreboard players get {((ScoreOp)v).Holder}");
                return;
            }
            case "Color" or "Style":
            {
                string member = EnumMember(value, p.Type);
                var field = p.Type.GetMembers(member).OfType<IFieldSymbol>().First();
                Emit($"bossbar set {id} {p.Name.ToLowerInvariant()} {McIdOf(field) ?? Snake(member)}");
                return;
            }
            case "Visible":
            {
                var c = EvalCond(value);
                if (c.Const is bool b) { Emit($"bossbar set {id} visible {(b ? "true" : "false")}"); return; }
                string t = Materialize(c);
                Emit($"bossbar set {id} visible false");
                Emit($"execute if score {t} matches 1 run bossbar set {id} visible true");
                return;
            }
        }
        throw new UnsupportedException(value, $"Cannot set {p.Name}.");
    }

    Operand GuiGet(IPropertySymbol p, MemberAccessExpressionSyntax? ma)
    {
        if (ma == null || p.Name is not ("Value" or "Max")) throw new UnsupportedException((SyntaxNode?)ma ?? cur.Info.Decl, $"{p.Name} cannot be read.");
        var g = GetGui(ma.Expression);
        string t = Temp();
        Emit($"execute store result score {t} run bossbar get {g.Target} {p.Name.ToLowerInvariant()}");
        return new ScoreOp(t);
    }

    void FinishGui(List<string> load)
    {
        foreach (var (target, (isBar, title, _)) in guiDefs)
        {
            if (isBar)
            {
                load.Add($"bossbar add {target} {Json.Str(title)}");
                load.Add($"bossbar set {target} name {Json.Str(title)}");   // update the title of an existing bar
            }
            else
            {
                load.Add($"scoreboard objectives add {target} dummy");
                load.Add($"scoreboard objectives modify {target} displayname {Json.Str(title)}");
            }
        }
    }
}
