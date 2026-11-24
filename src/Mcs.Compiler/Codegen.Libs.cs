using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// Compile-time position: an execute context (e.g. "at @e[...]") plus coordinates ("~ ~ ~" or absolute).
sealed record Pos(string Prefix, string X, string Y, string Z)
{
    public static readonly Pos Here = new("", "~", "~", "~");
    public (string name, Operand op, VK kind)[] Args { get; init; } = [];   // runtime coordinates (macro)
    public string Coords => $"{X} {Y} {Z}";
    public string ExecPrefix => Prefix.Length > 0 ? Prefix + " " : "";
    public bool IsAbsolute => !X.StartsWith('~') && !Y.StartsWith('~') && !Z.StartsWith('~');

    public Pos Offset(double dx, double dy, double dz) => Args.Length > 0
        ? throw new InvalidOperationException("Offset() is not supported on positions with runtime coordinates.")
        : this with { X = Add(X, dx), Y = Add(Y, dy), Z = Add(Z, dz) };

    static string Add(string c, double d)
    {
        if (d == 0) return c;
        if (c.StartsWith('~'))
        {
            double v = (c.Length > 1 ? double.Parse(c[1..], CultureInfo.InvariantCulture) : 0) + d;
            return v == 0 ? "~" : "~" + F(v);
        }
        return F(double.Parse(c, CultureInfo.InvariantCulture) + d);
    }
    static string F(double d) => d.ToString("0.######", CultureInfo.InvariantCulture);
}

sealed class BlockVar(string id) : Var
{
    public string Id = id;                                    // minecraft:oak_stairs (resolved for this version)
    public Dictionary<string, string> States = new();         // facing=north
    public Pos? Mounted;                                      // m.MountBlock: the block at this position
    public override string ToString() => States.Count == 0 ? Id : $"{Id}[{string.Join(",", States.Select(kv => $"{kv.Key}={kv.Value}"))}]";
}

/// Reference to entities: a context-free selector, or "@s" inside a foreach body / instance method.
sealed class RefVar(string selector) : Var
{
    public string Selector = selector;
    public string? Tag;        // tag that also identifies the entity (params, loop variables, this)
    public int Depth = -1;     // >= 0: "@s" valid only at this execution depth (otherwise use Tag)
    public ITypeSymbol? Type;
}

class StyleVar : Var
{
    public string? Color, Hover;
    public bool Bold, Italic, Underlined;
}

sealed class ButtonVar(string label, string command) : StyleVar
{
    public string Label = label;
    public string Command = command;  // e.g. "trigger mcs.ns.btn set 3"
}

sealed class TextVar(List<string> comps) : StyleVar
{
    public List<string> Comps = comps;   // text components, evaluated when the Text is created
}

public sealed partial class Codegen
{
    string TrigObj => $"mcs.{ns}.btn";
    string TmpTag => $"mcs.{ns}.tmp";
    readonly List<(int id, string command)> buttons = new();
    readonly Dictionary<string, int> buttonIds = new();
    readonly Dictionary<string, string> macroFns = new();
    readonly Dictionary<string, string> callbackWrappers = new();
    bool usesWorldSpawnMove;

    // ================================================================ IDs (per-version validation)
    static string? McIdOf(ISymbol s) => s.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "McIdAttribute")?.ConstructorArguments[0].Value as string;

    /// Validates a vanilla ID for the current version and returns "minecraft:<id>" (renamed IDs are mapped automatically).
    string ResolveId(string kind, string raw, SyntaxNode at)
    {
        string id = raw.StartsWith("minecraft:") ? raw[10..] : raw;
        string what = kind switch { "blocks" => "block", "items" => "item", "entities" => "entity", _ => "effect" };
        if (id.Contains(':'))
            throw new UnsupportedException(at, $"Only vanilla (minecraft:) {what} IDs are allowed — the datapack must not depend on other packs or mods: '{raw}'");
        var reg = McData.Registries[kind];
        int bit = 1 << McData.GroupIndex(prof);
        if (reg.TryGetValue(id, out var mask) && (mask & bit) != 0) return "minecraft:" + id;
        if (McData.Aliases[kind].TryGetValue(id, out var alt) && reg.TryGetValue(alt, out var m2) && (m2 & bit) != 0) return "minecraft:" + alt;
        if (reg.ContainsKey(id))
            throw new UnsupportedException(at, $"The {what} '{id}' does not exist in this version (exists in: {McData.Describe(mask)}). Use #if or change the target versions.");
        throw new UnsupportedException(at, $"Unknown {what} ID '{raw}'.");
    }

    // ================================================================ macros (1.20.2+)
    /// Builds a command whose $(name) placeholders are filled with numbers. Constants are substituted directly;
    /// runtime values go through a function macro, which needs 1.20.2+.
    List<string> MacroCmd(string template, SyntaxNode at, params (string name, Operand op, VK kind)[] args) => MacroCmd(template, at, args, false);

    List<string> MacroCmd(string template, SyntaxNode at, (string name, Operand op, VK kind)[] args, bool force)
    {
        if (!force && args.All(a => a.op is ConstOp))
        {
            string s = template;
            foreach (var (name, op, kind) in args)
            {
                int v = ((ConstOp)op).Value;
                s = s.Replace($"$({name})", FmtScaled(v, kind));
            }
            return new() { s };
        }
        if (!prof.Macros)
            throw new UnsupportedException(at, "Runtime values here need function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER', a constant, or raise the minimum target version.");
        var cmds = new List<string>();
        foreach (var (name, op, kind) in args)
        {
            if (op is ConstOp c) cmds.Add($"data modify storage {Storage} arg.{name} set value {(IsFrac(kind) ? FmtScaled(c.Value, kind) + "d" : c.Value)}");
            else cmds.Add($"execute store result storage {Storage} arg.{name} {(IsFrac(kind) ? "double " + InvScale(kind) : "int 1")} run scoreboard players get {Holder(op)}");
        }
        if (!macroFns.TryGetValue(template, out var fn))
        {
            fn = $"{ns}:__macro/m{macroCounter++}";
            macroFns[template] = fn;
            pack.Functions[fn] = new() { "$" + template };
        }
        cmds.Add($"function {fn} with storage {Storage} arg");
        return cmds;
    }

    // ================================================================ library objects (Block / Player / Button / Text)
    Var EvalLibObject(ExpressionSyntax e, VK kind, ISymbol? declared = null)
    {
        if (e is ParenthesizedExpressionSyntax p) return EvalLibObject(p.Expression, kind);
        if (IsCloneCall(e, out var cloneSrc))
            return EvalLibObject(cloneSrc, kind) switch
            {
                BlockVar b => new BlockVar(b.Id) { States = new(b.States) },
                ItemVar it => it.Clone(),
                _ => throw new UnsupportedException(e, "Clone() works on entity, block and item objects."),
            };
        if (kind == VK.Ref && EvalRefExpr(e) is RefVar rr) return rr;
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        if (kind == VK.Ref && WorldRef(e, sym) is RefVar wr) return wr;
        switch (Intrinsic(sym))
        {
            case "player.all": return new RefVar("@a");
            case "player.self": return new RefVar("@s");
            case "player.nearest": return new RefVar("@p");
            case "player.random": return new RefVar("@r");
            case "player.named": return new RefVar($"@a[name={ConstStr(((InvocationExpressionSyntax)e).ArgumentList.Arguments[0].Expression)}]");
            case "player.tagged": return new RefVar($"@a[tag={ConstStr(((InvocationExpressionSyntax)e).ArgumentList.Arguments[0].Expression)}]");
        }
        if (e is BaseObjectCreationExpressionSyntax oc)
        {
            var args = oc.ArgumentList?.Arguments ?? default;
            Var v;
            switch (kind)
            {
                case VK.Block:
                    if (Intrinsic(sym) == "block.new") v = ParseBlockString(ConstStr(args[0].Expression), args[0]);
                    else
                    {
                        var cls = (sym as IMethodSymbol)?.ContainingType ?? throw new UnsupportedException(e, "Unknown block.");
                        v = new BlockVar(ResolveId("blocks", McIdOf(cls)!, e));
                    }
                    break;
                case VK.Button:
                    v = new ButtonVar(ConstStr(args[0].Expression), Kind(cur.Model.GetTypeInfo(args[1].Expression).Type) == VK.String
                        ? ConstStr(args[1].Expression).TrimStart('/')
                        : $"trigger {TrigObj} set {ButtonId(args[1].Expression)}");
                    break;
                case VK.Text:
                    v = new TextVar(Components(args[0].Expression));
                    break;
                case VK.Gui:
                    v = CreateGui(cur.Model, oc);
                    break;
                case VK.Item:
                    v = CreateItem(cur.Model, oc, $"{ns}:{(declared != null ? cur.Info.Prefix.Replace('.', '_') + "." + Snake(declared.Name) : "item")}", true);
                    return v;
                default: throw new UnsupportedException(e, $"Cannot create {kind} here.");
            }
            if (oc.Initializer != null)
                foreach (var init in oc.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
                    SetObjProp(v, (IPropertySymbol)cur.Model.GetSymbolInfo(init.Left).Symbol!, init.Right);
            return v;
        }
        if (e is InvocationExpressionSyntax ii && sym is IMethodSymbol im && HasAttr(im, "InlineAttribute"))
        {
            EmitInline(im, ii, true, out _, out var obj);
            return obj ?? throw new UnsupportedException(e, "This [Inline] method does not return an object.");
        }
        var existing = GetVar(e);
        if (existing is BlockVar or ItemVar) CheckNotDisposed(existing, e);
        if (existing is BlockVar or RefVar or ButtonVar or TextVar or GuiVar or ItemVar) return existing;
        throw new UnsupportedException(e, $"Expected a {kind} value.");
    }

    RefVar GetRef(ExpressionSyntax e) => Resolve(EvalLibObject(e, VK.Ref) as RefVar ?? throw new UnsupportedException(e, "Expected an entity reference."));

    string ConstStr(ExpressionSyntax e) => ConstString(e) ?? throw new UnsupportedException(e, "A string constant is required.");

    static string SelWith(string sel, string extra) => extra.Length == 0 ? sel : sel.EndsWith(']') ? sel[..^1] + "," + extra + "]" : sel + "[" + extra + "]";

    BlockVar ParseBlockString(string s, SyntaxNode at)
    {
        int br = s.IndexOf('[');
        var bv = new BlockVar(ResolveId("blocks", br < 0 ? s : s[..br], at));
        if (br >= 0)
            foreach (var kv in s[(br + 1)..].TrimEnd(']').Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = kv.Split('=', 2);
                if (parts.Length != 2) throw new UnsupportedException(at, $"Invalid block state: {kv}");
                CheckState(bv, parts[0].Trim(), parts[1].Trim(), at);
                bv.States[parts[0].Trim()] = parts[1].Trim();
            }
        return bv;
    }

    void CheckState(BlockVar bv, string name, string value, SyntaxNode at)
    {
        var info = McData.Blocks[bv.Id[10..]];
        int bit = 1 << McData.GroupIndex(prof);
        if (!info.Props.TryGetValue(name, out var prop) || (prop.Mask & bit) == 0)
            throw new UnsupportedException(at, $"Block {bv.Id} has no state '{name}' in this version" +
                (info.Props.TryGetValue(name, out var p2) ? $" (exists in: {McData.Describe(p2.Mask)})." : "."));
        if (!prop.Values.Contains(value))
            throw new UnsupportedException(at, $"Invalid value '{value}' for {bv.Id}[{name}]. Allowed: {string.Join(", ", prop.Values)}");
    }

    /// Writes a property of a compile-time object (block state, button/text style).
    void SetObjProp(Var v, IPropertySymbol p, ExpressionSyntax value)
    {
        var cv = cur.Model.GetConstantValue(value);
        switch (v)
        {
            case TeamVar tv: cur.Sink.AddRange(TeamModify(tv, p, value, cur.Model)); return;
            case GuiVar gv: EmitGuiSet(gv, p, value); return;
            case ItemVar iv:
                if (!cv.HasValue) throw new UnsupportedException(value, $"{p.Name} must be a compile-time constant.");
                if (itemStatics.ContainsValue(iv)) throw new UnsupportedException(value, "Set the properties of a static Item in its initializer ({ Name = … }).");
                SetItemProp(iv, p.Name, cv.Value, value);
                return;
            case BlockVar bv:
            {
                var st = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "StateAttribute")
                    ?? throw new UnsupportedException(value, $"{p.Name} is not a block state.");
                string name = (string)st.ConstructorArguments[0].Value!;
                string val;
                if (cur.Model.GetSymbolInfo(value).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } ef) val = McIdOf(ef) ?? Snake(ef.Name);
                else if (cv.HasValue) val = cv.Value is bool b ? (b ? "true" : "false") : System.Convert.ToString(cv.Value, CultureInfo.InvariantCulture)!;
                else throw new UnsupportedException(value, "Block states must be compile-time constants.");
                CheckState(bv, name, val, value);
                bv.States[name] = val;
                if (bv.Mounted != null) EmitMountedBlockSet(bv, name, val);
                return;
            }
            case StyleVar sv:
                switch (p.Name)
                {
                    case "Color": sv.Color = Snake(EnumMember(value, p.Type)); return;
                    case "Hover": sv.Hover = ConstStr(value); return;
                    case "Bold": sv.Bold = cv.Value is true; return;
                    case "Italic": sv.Italic = cv.Value is true; return;
                    case "Underlined": sv.Underlined = cv.Value is true; return;
                }
                break;
        }
        throw new UnsupportedException(value, $"Cannot set {p.Name}.");
    }

    /// Name of the enum member an expression evaluates to (constant or compile-time variable).
    string EnumMember(ExpressionSyntax value, ITypeSymbol enumType)
    {
        if (cur.Model.GetSymbolInfo(value).Symbol is IFieldSymbol f && f.ContainingType.TypeKind == TypeKind.Enum) return f.Name;
        int v = ConstSlot(value);
        return enumType.GetMembers().OfType<IFieldSymbol>().FirstOrDefault(m => m.HasConstantValue && System.Convert.ToInt32(m.ConstantValue) == v)?.Name
            ?? throw new UnsupportedException(value, $"No {enumType.Name} member with value {v}.");
    }

    bool EmitLibPropertySet(IPropertySymbol p, MemberAccessExpressionSyntax? target, AssignmentExpressionSyntax a)
    {
        if (target == null && (cur.This == null || Kind(p.ContainingType) != VK.Ref)) return false;
        var k = target == null ? VK.Ref : Kind(cur.Model.GetTypeInfo(target.Expression).Type);
        if (k is VK.Block or VK.Button or VK.Text or VK.Gui or VK.Item or VK.Team)
        {
            if (k == VK.Team) { if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new UnsupportedException(a, "Only '=' is supported here."); SetObjProp(EvalCtObj(target!.Expression), p, a.Right); return true; }
            if (!a.IsKind(SyntaxKind.SimpleAssignmentExpression)) throw new UnsupportedException(a, "Only '=' is supported here.");
            SetObjProp(EvalLibObject(target.Expression, k), p, a.Right);
            return true;
        }
        if (k != VK.Ref) return false;
        var pl = target == null ? Resolve(ThisRef(a)) : GetRef(target.Expression);
        switch (p.Name)
        {
            case "GameMode":
                var gm = cur.Model.GetSymbolInfo(a.Right).Symbol as IFieldSymbol ?? throw new UnsupportedException(a.Right, "GameMode must be a constant (e.g. GameMode.Creative).");
                Emit($"gamemode {Snake(gm.Name)} {pl.Selector}");
                return true;
            case "Level":
                cur.Sink.AddRange(MacroCmd($"xp set {pl.Selector} $(n) levels", a, ("n", EvalNum(a.Right, VK.Int), VK.Int)));
                return true;
        }
        throw new UnsupportedException(a, $"{p.Name} is read-only.");
    }

    Operand? TryLibPropertyGet(IPropertySymbol p, MemberAccessExpressionSyntax? ma, VK kind)
    {
        if (Kind(p.ContainingType) == VK.Gui) return GuiGet(p, ma);
        if (WorldPropGet(p, ma) is Operand wp) return wp;
        if (Kind(p.ContainingType) == VK.Block && ma != null && !p.IsStatic) return BlockGet(p, ma);
        if (Kind(p.ContainingType) != VK.Ref) return null;
        var pl = ma == null ? Resolve(ThisRef(cur.Info.Decl)) : GetRef(ma.Expression);
        string t = Temp();
        switch (Intrinsic(p))
        {
            case "player.count": Emit($"execute store result score {t} if entity {pl.Selector}"); return new ScoreOp(t);
            case "player.exists": Emit($"execute store success score {t} if entity {pl.Selector}"); return new ScoreOp(t);
        }
        var (path, _, _) = PropInfo(p);
        if (path == null) throw new UnsupportedException(ma, $"{p.Name} cannot be read.");
        Emit($"execute store result score {t} run data get entity {SelWith(pl.Selector, "limit=1")} {path} {ScaleOf(kind)}");
        return new ScoreOp(t);
    }

    // ================================================================ text components for library objects
    List<string>? LibComponents(ExpressionSyntax e)
    {
        switch (Kind(cur.Model.GetTypeInfo(e).Type))
        {
            case VK.Ref: return new() { $"{{\"selector\":{Json.Str(GetRef(e).Selector)}}}" };
            case VK.Block: return new() { Text(EvalLibObject(e, VK.Block).ToString()!) };
            case VK.Button:
            {
                var b = (ButtonVar)EvalLibObject(e, VK.Button);
                return new() { StyledJson($"\"text\":{Json.Str(b.Label)}", b, ClickRun(b.Command)) };
            }
            case VK.Text:
            {
                var tv = (TextVar)EvalLibObject(e, VK.Text);
                return new() { StyledJson($"\"text\":\"\",\"extra\":[{string.Join(",", tv.Comps)}]", tv, null) };
            }
        }
        return null;
    }

    string StyledJson(string body, StyleVar s, string? click)
    {
        var sb = new StringBuilder("{").Append(body);
        if (s.Color != null) sb.Append($",\"color\":\"{s.Color}\"");
        if (s.Bold) sb.Append(",\"bold\":true");
        if (s.Italic) sb.Append(",\"italic\":true");
        if (s.Underlined) sb.Append(",\"underlined\":true");
        if (click != null) sb.Append(',').Append(click);
        if (s.Hover != null)
            sb.Append(prof.NewTextComponents
                ? $",\"hover_event\":{{\"action\":\"show_text\",\"value\":{Text(s.Hover)}}}"
                : $",\"hoverEvent\":{{\"action\":\"show_text\",\"contents\":{Text(s.Hover)}}}");
        return sb.Append('}').ToString();
    }

    /// Click action that runs a command. 1.21.5+ uses click_event/command; older versions use clickEvent/value ("/" required).
    string ClickRun(string command) => prof.NewTextComponents
        ? $"\"click_event\":{{\"action\":\"run_command\",\"command\":{Json.Str(command)}}}"
        : $"\"clickEvent\":{{\"action\":\"run_command\",\"value\":{Json.Str("/" + command)}}}";

    string TextArray(IEnumerable<ExpressionSyntax> parts) => "[\"\"" + string.Concat(parts.SelectMany(Components).Select(c => "," + c)) + "]";

    // ================================================================ callbacks (buttons, Player.Run)
    /// Returns the command that calls a callback method as the current @s player.
    string CallbackCommand(ExpressionSyntax arg)
    {
        if (arg is LambdaExpressionSyntax) return ActionCommand(arg, true);
        var info = cur.Model.GetSymbolInfo(arg);
        var m = (info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
        if (m == null || !fns.TryGetValue(m.OriginalDefinition, out var cb))
            throw new UnsupportedException(arg, "The callback must be a static method declared in MC# code.");
        if (cb.Params.Count == 0) return $"function {cb.Path}";
        if (cb.Params.Count == 1 && cb.Params.Values.First() is RefVar pv)
        {
            if (!callbackWrappers.TryGetValue(cb.Path, out var w))
            {
                w = $"{ns}:__cb/{cb.Prefix.Replace('.', '/')}";
                callbackWrappers[cb.Path] = w;
                pack.Functions[w] = new() { $"tag @a remove {pv.Tag}", $"tag @s add {pv.Tag}", $"function {cb.Path}" };
            }
            return $"function {w}";
        }
        throw new UnsupportedException(arg, "Callbacks must take no parameters or a single Player parameter.");
    }

    int ButtonId(ExpressionSyntax callback)
    {
        string cmd = CallbackCommand(callback);
        if (!buttonIds.TryGetValue(cmd, out var id))
        {
            id = buttonIds.Count + 1;
            buttonIds[cmd] = id;
            buttons.Add((id, cmd));
        }
        return id;
    }

    // ================================================================ intrinsics
    Operand? EmitLibIntrinsic(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        RefVar Recv() => inv.Expression is MemberAccessExpressionSyntax rma ? GetRef(rma.Expression) : Resolve(ThisRef(inv));
        Operand Store(string mode, string tail, params Pos[] ps) { var t = Temp(); Emit($"scoreboard players set {t} 0"); EmitCmd($"execute store {mode} score {t} {tail}", inv, ps); return new ScoreOp(t); }

        if (name.StartsWith("gui.") || name.StartsWith("board.") || name.StartsWith("bossbar.")) return EmitGui(name, inv);
        switch (name)
        {
            // ---------------- world
            case "spawn.id":
            {
                string id = ResolveId("entities", ConstStr(Arg(0)), Arg(0));
                var pos = EvalPos(Arg(1));
                var count = args.Count > 2 ? EvalNum(Arg(2), VK.Int) : new ConstOp(1);
                string cmd = At(pos, c => $"summon {id} {c}");
                if (count is ConstOp { Value: <= 8 } c)
                {
                    for (int i = 0; i < c.Value; i++) EmitCmd(cmd, inv, pos);
                    return null;
                }
                if (pos.Args.Length > 0) throw new UnsupportedException(inv, "SpawnEntity with a runtime count needs a constant position.");
                string n = Temp(), loop = ReservePath("spawn");
                pack.Functions[loop] = new() { cmd, $"scoreboard players remove {n} 1", $"execute if score {n} matches 1.. run function {loop}" };
                SetHolder(n, count);
                Emit($"execute if score {n} matches 1.. run function {loop}");
                return null;
            }
            case "setworldspawn":
            {
                var pos = EvalPos(Arg(0));
                if (pos.Args.Length > 0) throw new UnsupportedException(inv, "SetWorldSpawn needs a constant position.");
                usesWorldSpawn = usesWorldSpawnMove = true;
                var bp = pos.IsAbsolute ? pos with
                {
                    X = Math.Floor(double.Parse(pos.X, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
                    Y = Math.Floor(double.Parse(pos.Y, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
                    Z = Math.Floor(double.Parse(pos.Z, CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture),
                } : pos;
                Emit(At(bp, c => $"setworldspawn {c}"));
                Emit(At(bp, _ => $"forceload add {bp.X} {bp.Z}"));      // make sure the new spawn chunk loads
                Emit($"schedule function {ns}:__ws/move 1t");          // re-place the spawn marker once loaded (no /reload needed)
                return null;
            }
            case "reload": Emit("reload"); return null;

            // ---------------- blocks
            case "block.set" or "block.setobj":
            {
                string block = BlockArg(Arg(0), name == "block.setobj");
                var pos = EvalPos(Arg(1));
                if (name == "block.set") return Store("success", $"{pos.ExecPrefix}run setblock {pos.Coords} {block}", pos);
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Consume(Arg(0), EvalLibObject(Arg(0), VK.Block), inv, () => { var r = Store("success", $"{pos.ExecPrefix}run setblock {pos.Coords} {block}", pos); Emit($"scoreboard players operation {t} = {((ScoreOp)r).Holder}"); });
                return new ScoreOp(t);
            }
            case "block.fill" or "block.fillobj":
            {
                string block = BlockArg(Arg(0), name == "block.fillobj");
                var from = EvalPos(Arg(1)); var to = EvalPos(Arg(2));
                string prefix = from.Prefix.Length > 0 ? from.Prefix : to.Prefix;
                if (from.Prefix.Length > 0 && to.Prefix.Length > 0 && from.Prefix != to.Prefix)
                    throw new UnsupportedException(inv, "FillBlock: both corners must be relative to the same position (e.g. p and p.Offset(...)) or absolute.");
                string cmd = $"{(prefix.Length > 0 ? prefix + " " : "")}run fill {from.Coords} {to.Coords} {block}";
                if (name == "block.fill") return Store("success", cmd, from, to);
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Consume(Arg(0), EvalLibObject(Arg(0), VK.Block), inv, () => { var r = Store("success", cmd, from, to); Emit($"scoreboard players operation {t} = {((ScoreOp)r).Holder}"); });
                return new ScoreOp(t);
            }
            case "block.is" or "block.isobj":
            {
                string block = BlockArg(Arg(0), name == "block.isobj");
                var pos = EvalPos(Arg(1));
                return Store("success", $"{pos.ExecPrefix}if block {pos.Coords} {block}", pos);
            }

            // ---------------- players
            case "player.message": Emit($"tellraw {Recv().Selector} {TextArray(args.Select(x => x.Expression))}"); return null;
            case "player.actionbar": Emit($"title {Recv().Selector} actionbar {TextArray(args.Select(x => x.Expression))}"); return null;
            case "player.title": EmitTitle(Recv(), Arg(0), args.Count > 1 ? Arg(1) : null); return null;
            case "player.sound":
            {
                string sound = ConstStr(Arg(0));
                if (!sound.Contains(':')) sound = "minecraft:" + sound;
                string vol = Fmt(args.Count > 1 ? ConstDouble(Arg(1), "Volume") : 1), pitch = Fmt(args.Count > 2 ? ConstDouble(Arg(2), "Pitch") : 1);
                Emit($"execute as {Recv().Selector} at @s run playsound {sound} master @s ~ ~ ~ {vol} {pitch}");
                return null;
            }
            case "player.kill": Emit($"kill {Recv().Selector}"); return null;
            case "player.setspawn": { var pl = Recv(); var sp = EvalPos(Arg(0)); EmitCmd(At(sp, c => $"spawnpoint {pl.Selector} {c}"), inv, sp); return null; }
            case "player.addtag": Emit($"tag {Recv().Selector} add {ConstStr(Arg(0))}"); return null;
            case "player.removetag": Emit($"tag {Recv().Selector} remove {ConstStr(Arg(0))}"); return null;
            case "player.hastag": return Store("success", $"if entity {SelWith(Recv().Selector, "tag=" + ConstStr(Arg(0)))}");
            case "player.addlevels": cur.Sink.AddRange(MacroCmd($"xp add {Recv().Selector} $(n) levels", inv, ("n", EvalNum(Arg(0), VK.Int), VK.Int))); return null;
            case "player.addxp": cur.Sink.AddRange(MacroCmd($"xp add {Recv().Selector} $(n) points", inv, ("n", EvalNum(Arg(0), VK.Int), VK.Int))); return null;
            case "player.effect":
            {
                var pl = Recv();
                string eff = ResolveId("effects", ConstStr(Arg(0)), Arg(0));
                var amp = args.Count > 2 ? EvalNum(Arg(2), VK.Int) : new ConstOp(0);
                bool hide = args.Count > 3 && cur.Model.GetConstantValue(Arg(3)).Value is true;
                cur.Sink.AddRange(MacroCmd($"effect give {pl.Selector} {eff} $(s) $(a){(hide ? " true" : "")}", inv,
                    ("s", EvalNum(Arg(1), VK.Int), VK.Int), ("a", amp, VK.Int)));
                return null;
            }
            case "player.cleareffect": Emit($"effect clear {Recv().Selector} {ResolveId("effects", ConstStr(Arg(0)), Arg(0))}"); return null;
            case "player.cleareffects": Emit($"effect clear {Recv().Selector}"); return null;
            case "player.run": Emit($"execute as {Recv().Selector} at @s run {CallbackCommand(Arg(0))}"); return null;

            // ---------------- inventory
            case "player.give":
            {
                var pl = Recv();
                string item = ResolveId("items", ConstStr(Arg(0)), Arg(0));
                cur.Sink.AddRange(MacroCmd($"give {pl.Selector} {item} $(n)", inv, ("n", args.Count > 1 ? EvalNum(Arg(1), VK.Int) : new ConstOp(1), VK.Int)));
                return null;
            }
            case "player.clearall": Emit($"clear {Recv().Selector}"); return null;
            case "player.clearitem":
            {
                var pl = Recv();
                string item = ResolveId("items", ConstStr(Arg(0)), Arg(0));
                var n = args.Count > 1 ? EvalNum(Arg(1), VK.Int) : new ConstOp(-1);
                if (n is ConstOp { Value: < 0 }) Emit($"clear {pl.Selector} {item}");
                else cur.Sink.AddRange(MacroCmd($"clear {pl.Selector} {item} $(n)", inv, ("n", n, VK.Int)));
                return null;
            }
            case "player.countitem": return Store("result", $"run clear {Recv().Selector} {ResolveId("items", ConstStr(Arg(0)), Arg(0))} 0");
            case "player.hasitem": return Store("success", $"run clear {Recv().Selector} {ResolveId("items", ConstStr(Arg(0)), Arg(0))} 0");
            case "player.setslot":
            {
                var pl = Recv();
                string item = ResolveId("items", ConstStr(Arg(1)), Arg(1));
                var count = args.Count > 2 ? EvalNum(Arg(2), VK.Int) : new ConstOp(1);
                var slot = EvalNum(Arg(0), VK.Int);
                if (slot is ConstOp sc)
                    cur.Sink.AddRange(MacroCmd($"item replace entity {pl.Selector} {SlotName(sc.Value, Arg(0))} with {item} $(n)", inv, ("n", count, VK.Int)));
                else
                    cur.Sink.AddRange(MacroCmd($"item replace entity {pl.Selector} container.$(slot) with {item} $(n)", inv, ("slot", slot, VK.Int), ("n", count, VK.Int)));
                return null;
            }
            case "player.clearslot":
            {
                var pl = Recv();
                var slot = EvalNum(Arg(0), VK.Int);
                if (slot is ConstOp sc) Emit($"item replace entity {pl.Selector} {SlotName(sc.Value, Arg(0))} with minecraft:air");
                else cur.Sink.AddRange(MacroCmd($"item replace entity {pl.Selector} container.$(slot) with minecraft:air", inv, ("slot", slot, VK.Int)));
                return null;
            }
            case "player.slotcount":
            {
                var pl = Recv(); int s = ConstSlot(Arg(0));
                return prof.ItemPredicates
                    ? Store("result", $"if items entity {SelWith(pl.Selector, "limit=1")} {SlotName(s, Arg(0))} *")
                    : Store("result", $"run data get entity {SelWith(pl.Selector, "limit=1")} Inventory[{{Slot:{s}b}}].Count");
            }
            case "player.slotis":
            {
                var pl = Recv(); int s = ConstSlot(Arg(0));
                string item = ResolveId("items", ConstStr(Arg(1)), Arg(1));
                return prof.ItemPredicates
                    ? Store("success", $"if items entity {SelWith(pl.Selector, "limit=1")} {SlotName(s, Arg(0))} {item}")
                    : Store("success", $"if entity {SelWith(pl.Selector, $"limit=1,nbt={{Inventory:[{{Slot:{s}b,id:\"{item}\"}}]}}")}");
            }
            case "player.slotempty":
            {
                var pl = Recv(); int s = ConstSlot(Arg(0));
                return prof.ItemPredicates
                    ? Store("success", $"unless items entity {SelWith(pl.Selector, "limit=1")} {SlotName(s, Arg(0))} *")
                    : Store("success", $"unless entity {SelWith(pl.Selector, $"limit=1,nbt={{Inventory:[{{Slot:{s}b}}]}}")}");
            }
            case "player.copyslot" or "player.moveslot":
            {
                var pl = Recv();
                string from = SlotName(ConstSlot(Arg(0)), Arg(0)), to = SlotName(ConstSlot(Arg(1)), Arg(1));
                if (from == to) return null;
                var cmds = new List<string> { $"item replace entity @s {to} from entity @s {from}" };
                if (name == "player.moveslot") cmds.Add($"item replace entity @s {from} with minecraft:air");
                EmitAsPlayers(pl, cmds, "slot");
                return null;
            }
            case "player.swapslots":
            {
                var pl = Recv();
                string a = SlotName(ConstSlot(Arg(0)), Arg(0)), b = SlotName(ConstSlot(Arg(1)), Arg(1));
                if (a == b) return null;
                // Hold one stack in a temporary invisible armor stand while swapping
                string tmp = $"@e[type=minecraft:armor_stand,tag={TmpTag},limit=1]";
                EmitAsPlayers(pl, new()
                {
                    $"summon minecraft:armor_stand ~ ~ ~ {{Tags:[\"{TmpTag}\"],Marker:1b,Invisible:1b,NoGravity:1b}}",
                    $"item replace entity {tmp} weapon.mainhand from entity @s {a}",
                    $"item replace entity @s {a} from entity @s {b}",
                    $"item replace entity @s {b} from entity {tmp} weapon.mainhand",
                    $"item replace entity {tmp} weapon.mainhand with minecraft:air",
                    $"kill @e[type=minecraft:armor_stand,tag={TmpTag}]",
                }, "swap");
                return null;
            }

            // ---------------- TUI
            case "tui.print": Emit($"tellraw @a {TextArray(args.Select(x => x.Expression))}"); return null;
            case "tui.printto": Emit($"tellraw {GetRef(Arg(0)).Selector} {TextArray(args.Skip(1).Select(x => x.Expression))}"); return null;
            case "tui.menu":
            {
                var pl = GetRef(Arg(0));
                var comps = Components(Arg(1)).ToList();
                int i = 0;
                foreach (var b in args.Skip(2)) { comps.Add(Text(i++ == 0 ? "\n" : "  ")); comps.AddRange(Components(b.Expression)); }
                Emit($"tellraw {pl.Selector} [\"\"{string.Concat(comps.Select(c => "," + c))}]");
                return null;
            }
            case "tui.clear": Emit($"tellraw {GetRef(Arg(0)).Selector} {Text(new string('\n', 100))}"); return null;
            case "tui.title": EmitTitle(GetRef(Arg(0)), Arg(1), args.Count > 2 ? Arg(2) : null); return null;
            case "tui.actionbar": Emit($"title {GetRef(Arg(0)).Selector} actionbar {TextArray(new[] { Arg(1) })}"); return null;
        }
        if (name.StartsWith("ent") || name is "check" or "result" or "success" or "random" or "schedule" or "schedule.clear" or "exec.run" or "exec.at" or "asmf" or "player.run")
            return EmitEntityIntrinsic(name, inv);
        throw new UnsupportedException(inv, $"Cannot use '{name}' in this position.");
    }

    void EmitTitle(RefVar pl, ExpressionSyntax title, ExpressionSyntax? subtitle)
    {
        if (subtitle != null && !(cur.Model.GetConstantValue(subtitle) is { HasValue: true, Value: null }))
            Emit($"title {pl.Selector} subtitle {TextArray(new[] { subtitle })}");
        Emit($"title {pl.Selector} title {TextArray(new[] { title })}");
    }

    void EmitAsPlayers(RefVar pl, List<string> cmds, string kind)
    {
        if (cmds.Count == 1) Emit($"execute as {pl.Selector} at @s run {cmds[0]}");
        else Emit($"execute as {pl.Selector} at @s run function {NewSub(kind, cmds)}");
    }

    string BlockArg(ExpressionSyntax e, bool isObject) =>
        isObject ? ((BlockVar)EvalLibObject(e, VK.Block)).ToString() : ParseBlockString(ConstStr(e), e).ToString();

    int ConstSlot(ExpressionSyntax e)
    {
        var cv = cur.Model.GetConstantValue(e);
        if (cv.HasValue) return System.Convert.ToInt32(cv.Value, CultureInfo.InvariantCulture);
        Operand? op = null;
        var cmds = Capture(() => op = EvalNum(e, VK.Int));
        if (cmds.Count == 0 && op is ConstOp c) return c.Value;
        throw new UnsupportedException(e, "A compile-time constant is required here.");
    }

    static string SlotName(int s, SyntaxNode at) => s switch
    {
        >= 0 and <= 35 => $"container.{s}",
        100 => "armor.feet", 101 => "armor.legs", 102 => "armor.chest", 103 => "armor.head",
        -106 => "weapon.offhand",
        _ => throw new UnsupportedException(at, $"Invalid slot {s}. Use 0–35, Slot.Head/Chest/Legs/Feet or Slot.Offhand."),
    };

    // ================================================================ load/tick additions
    void FinishLibs(List<string> load)
    {
        if (buttons.Count > 0)
        {
            load.Add($"scoreboard objectives add {TrigObj} trigger");
            pack.Functions[$"{ns}:__tui/tick"] = new()
            {
                $"execute as @a[scores={{{TrigObj}=1..}}] at @s run function {ns}:__tui/click",
                $"scoreboard players enable @a {TrigObj}",
            };
            var click = new List<string>
            {
                $"scoreboard players operation $tui#id {Obj} = @s {TrigObj}",
                $"scoreboard players set @s {TrigObj} 0",
            };
            foreach (var (id, cmd) in buttons) click.Add($"execute if score $tui#id {Obj} matches {id} run {cmd}");
            pack.Functions[$"{ns}:__tui/click"] = click;
            pack.TickTag.Add($"{ns}:__tui/tick");
        }
        if (usesWorldSpawnMove)
        {
            // Runs from schedule, so the execution position is the (new) world spawn
            pack.Functions[$"{ns}:__ws/move"] = new()
            {
                $"execute if loaded ~ ~ ~ run function {ns}:__ws/place",
                $"execute unless loaded ~ ~ ~ run schedule function {ns}:__ws/move 1t",
            };
            var place = new List<string>();
            if (prof.ForceloadSpawn) place.Add($"execute at @e[type=minecraft:marker,tag={WorldSpawnTag}] run forceload remove ~ ~");
            place.Add($"kill @e[type=minecraft:marker,tag={WorldSpawnTag}]");
            place.Add($"summon minecraft:marker ~ ~ ~ {{Tags:[\"{WorldSpawnTag}\"]}}");
            if (!prof.ForceloadSpawn) place.Add("forceload remove ~ ~"); // older versions keep spawn chunks loaded by themselves
            else place.Add("forceload add ~ ~");
            pack.Functions[$"{ns}:__ws/place"] = place;
        }
    }
}
