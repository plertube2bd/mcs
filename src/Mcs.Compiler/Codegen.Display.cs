using System.Globalization;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// DisplayLib: block / item / text display entities. They are entities (EntVar) with extra NBT property kinds:
///   enum, text, block, item, brightness, scale3 (uniform scale), rot:<axis> (axis-angle rotation in degrees).
public sealed partial class Codegen
{
    static bool IsEntityType(ITypeSymbol? t)
    {
        for (var bt = t as INamedTypeSymbol; bt != null; bt = bt.BaseType)
            if (bt.Name == "Entity" && bt.ContainingNamespace?.ToDisplayString() == "EntityLib") return true;
        return false;
    }

    static string? DisplayTypeId(ITypeSymbol? t) => t?.Name switch
    {
        "BlockDisplay" => "minecraft:block_display",
        "ItemDisplay" => "minecraft:item_display",
        "TextDisplay" => "minecraft:text_display",
        _ => null,
    };

    /// `new TextDisplay("Hi") { Billboard = Billboard.Center }`: the constructor argument sets the content
    /// property, the initializer sets the others (all applied right after the summon).
    void InitEntityObject(EntVar ev, BaseObjectCreationExpressionSyntax oc)
    {
        var type = cur.Model.GetTypeInfo(oc).Type!;
        if (type.Name == "Interaction" && type.ContainingNamespace?.ToDisplayString() == "EntityLib") ev.TypeId = ResolveId("entities", "interaction", oc);
        else if (DisplayTypeId(type) is { } id)
        {
            ev.TypeId = id;
            string content = type.Name switch { "BlockDisplay" => "Block", "ItemDisplay" => "Item", _ => "Text" };
            var prop = type.GetMembers(content).OfType<IPropertySymbol>().First();
            EmitEntitySet(ev, prop, oc.ArgumentList!.Arguments[0].Expression);
        }
        if (oc.Initializer == null) return;
        foreach (var init in oc.Initializer.Expressions)
        {
            if (init is not Microsoft.CodeAnalysis.CSharp.Syntax.AssignmentExpressionSyntax { Left: IdentifierNameSyntax name } a
                || cur.Model.GetSymbolInfo(name).Symbol is not IPropertySymbol p)
                throw new UnsupportedException(init, "Only property assignments (Name = value) are allowed here.");
            EmitEntitySet(ev, p, a.Right);
        }
    }

    static readonly HashSet<string> DisplayNbtTypes = ["enum", "text", "block", "item", "brightness", "scale3"];
    static bool IsDisplayNbtType(string type) => DisplayNbtTypes.Contains(type) || type.StartsWith("rot:");

    /// Before the summon, commands are kept and run right after it; after it, they run now.
    void EntityCmds(EntVar ev, IEnumerable<string> cmds)
    {
        if (ev.Spawned) cur.Sink.AddRange(cmds); else ev.Deferred.AddRange(cmds);
    }

    void EmitDisplaySet(EntVar ev, IPropertySymbol p, string path, string type, ExpressionSyntax value)
    {
        var cv = cur.Model.GetConstantValue(value);
        string set = $"data modify entity {Sel(ev)} {path} set value";
        switch (type)
        {
            case "enum":
            {
                var fields = ((INamedTypeSymbol)p.Type).GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue).ToList();
                string Name(IFieldSymbol f) => McIdOf(f) ?? Snake(f.Name);
                if (cv.HasValue)
                {
                    var f = fields.First(f => Equals(f.ConstantValue, cv.Value));
                    EntityCmds(ev, [$"{set} {Json.Str(Name(f))}"]);
                }
                else
                {
                    string h = ev.Spawned ? Holder(EvalNum(value, VK.Int)) : StableCopy(ev, p, value, VK.Int);
                    EntityCmds(ev, fields.Select(f => $"execute if score {h} matches {f.ConstantValue} run {set} {Json.Str(Name(f))}"));
                }
                return;
            }
            case "block":
            {
                var b = cv.HasValue ? ParseBlockString((string)cv.Value!, value) : (BlockVar)EvalLibObject(value, VK.Block);
                // 26.3 renamed the fields: {Name, Properties} → {id, properties}
                var (idKey, propKey) = prof.NewBlockStateNbt ? ("id", "properties") : ("Name", "Properties");
                string props = b.States.Count == 0 ? "" : $",{propKey}:{{{string.Join(",", b.States.Select(kv => $"{kv.Key}:{Json.Str(kv.Value)}"))}}}";
                EntityCmds(ev, [$"{set} {{{idKey}:{Json.Str(b.Id)}{props}}}"]);
                return;
            }
            case "item":
            {
                if (!cv.HasValue) throw new UnsupportedException(value, "The item must be a constant ID, e.g. \"minecraft:diamond\".");
                string id = ResolveId("items", (string)cv.Value!, value);
                EntityCmds(ev, [$"{set} {(prof.ItemPredicates ? $"{{id:{Json.Str(id)},count:1}}" : $"{{id:{Json.Str(id)},Count:1b}}")}"]);
                return;
            }
            case "brightness":
            {
                if (cv.HasValue) { EntityCmds(ev, [$"{set} {{block:{cv.Value},sky:{cv.Value}}}"]); return; }
                // the compound must stay complete after every single write, so write a whole one first
                string h = ev.Spawned ? Holder(EvalNum(value, VK.Int)) : StableCopy(ev, p, value, VK.Int);
                EntityCmds(ev, [
                    $"{set} {{block:0,sky:0}}",
                    $"execute store result entity {Sel(ev)} {path}.block int 1 run scoreboard players get {h}",
                    $"execute store result entity {Sel(ev)} {path}.sky int 1 run scoreboard players get {h}",
                ]);
                return;
            }
            case "scale3":
            {
                if (cv.HasValue)
                {
                    string f = FormatNum(cv.Value!) + "f";
                    EntityCmds(ev, [$"{set} [{f},{f},{f}]"]);
                    return;
                }
                var kind = Kind(p.Type);
                string h = ev.Spawned ? Holder(EvalNum(value, kind)) : StableCopy(ev, p, value, kind);
                EntityCmds(ev, Enumerable.Range(0, 3).Select(i => $"execute store result entity {Sel(ev)} {path}[{i}] float {InvScale(kind)} run scoreboard players get {h}"));
                return;
            }
            case "text":
                EmitDisplayText(ev, path, value);
                return;
        }
        if (type.StartsWith("rot:"))
        {
            string axis = type[4..];
            if (cv.HasValue)
            {
                double rad = System.Convert.ToDouble(cv.Value) * Math.PI / 180;
                EntityCmds(ev, [$"{set} {{angle:{rad.ToString("0.#########", CultureInfo.InvariantCulture)}f,axis:[{axis}]}}"]);
                return;
            }
            // Runtime angle: degrees → radians while copying to storage, then a macro writes the axis-angle compound
            if (!prof.Macros)
                throw new UnsupportedException(value, $"Setting {p.Name} from a runtime value needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or a constant.");
            var kind = Kind(p.Type);
            Operand op = EvalNum(value, kind);
            string arg = $"disp.r{storageCounter++}";
            double scale = Math.PI / 180 / ScaleOf(kind);
            Emit($"execute store result storage {Storage} {arg}.v double {scale.ToString("0.####################", CultureInfo.InvariantCulture)} run scoreboard players get {Holder(op)}");
            string fn = MacroFn($"data modify entity {Sel(ev)} {path} set value {{angle:$(v)f,axis:[{axis}]}}");
            EntityCmds(ev, [$"function {fn} with storage {Storage} {arg}"]);
        }
    }

    readonly Dictionary<string, string> displayMacros = new();

    string MacroFn(string template)
    {
        if (!displayMacros.TryGetValue(template, out var fn))
        {
            fn = $"{ns}:__macro/m{macroCounter++}";
            displayMacros[template] = fn;
            pack.Functions[fn] = new() { "$" + template };
        }
        return fn;
    }

    /// Text displays do not resolve score / nbt components, so runtime parts are written into the text
    /// with a function macro: the same components as Console.WriteLine, with every score and storage part
    /// replaced by $(vN).
    void EmitDisplayText(EntVar ev, string path, ExpressionSyntax value)
    {
        var comps = Components(value);
        var parts = new List<string>();
        var setup = new List<string>();
        string arg = $"disp.t{storageCounter++}";
        int n = 0;
        foreach (var c in comps)
        {
            using var doc = JsonDocument.Parse(c);
            var o = doc.RootElement;
            if (o.TryGetProperty("score", out var sc))
            {
                string v = $"v{n++}";
                setup.Add($"execute store result storage {Storage} {arg}.{v} int 1 run scoreboard players get {sc.GetProperty("name").GetString()} {sc.GetProperty("objective").GetString()}");
                parts.Add(Text($"$({v})"));
            }
            else if (o.TryGetProperty("nbt", out var nb) && o.TryGetProperty("storage", out var st) && !o.TryGetProperty("separator", out _))
            {
                string v = $"v{n++}";
                setup.Add($"data modify storage {Storage} {arg}.{v} set from storage {st.GetString()} {nb.GetString()}");
                parts.Add(Text($"$({v})"));
            }
            else if (o.TryGetProperty("text", out _) && o.EnumerateObject().Count() == 1) parts.Add(c);
            else throw new UnsupportedException(value, "This value cannot be shown in a text display (entity names, NBT lists and selectors are not supported).");
        }
        string json = parts.Count == 1 ? parts[0] : $"[\"\"{string.Concat(parts.Select(x => "," + x))}]";
        string lit = prof.NewTextComponents ? json : SnbtSingle(json);
        string cmd = $"data modify entity {Sel(ev)} {path} set value {lit}";
        if (n == 0) { EntityCmds(ev, [cmd]); return; }
        if (!prof.Macros)
            throw new UnsupportedException(value, "Runtime values in display text need function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or a constant text.");
        cur.Sink.AddRange(setup);                 // capture the values now
        EntityCmds(ev, [$"function {MacroFn(cmd)} with storage {Storage} {arg}"]);
    }

    /// d.Animate(ticks): animate this tick's transformation changes over `ticks` ticks.
    void EmitAnimate(EntVar ev, ExpressionSyntax ticks)
    {
        var t = EvalNum(ticks, VK.Int);
        var cmds = new List<string> { $"data modify entity {Sel(ev)} start_interpolation set value 0" };
        cmds.Add(t is ConstOp c
            ? $"data modify entity {Sel(ev)} interpolation_duration set value {c.Value}"
            : $"execute store result entity {Sel(ev)} interpolation_duration int 1 run scoreboard players get {Holder(t)}");
        EntityCmds(ev, cmds);
    }

    /// [Since(format, version)] on a member: a compile error for older targets.
    void CheckSince(ISymbol s, SyntaxNode at)
    {
        var a = s.GetAttributes().FirstOrDefault(x => x.AttributeClass?.Name == "SinceAttribute");
        if (a != null && prof.Format < (int)a.ConstructorArguments[0].Value!)
            throw new UnsupportedException(at, $"{s.Name} needs Minecraft {a.ConstructorArguments[1].Value}+ (this build also targets older versions). Use '#if MC_{((string)a.ConstructorArguments[1].Value!).Replace('.', '_')}_OR_GREATER' or raise the minimum target version.");
    }
}
