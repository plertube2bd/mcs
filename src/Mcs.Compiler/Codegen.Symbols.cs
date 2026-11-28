using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Mcs.Compiler;

/// data/<ns>/mcs/symbols.json: where MC# names live at run time (score holders, storage paths, function paths).
/// The runtime mod reads it for its debugger (/mcs vars, watch, trace) and its interpreter (/mcsi), and to hide
/// compiler-generated functions from the /<ns>:<function> shortcuts. Minecraft itself ignores the file.
public sealed partial class Codegen
{
    readonly Dictionary<FnInfo, List<(string name, ScoreVar v)>> fnLocals = new();

    static string TypeName(VK k) => k switch
    {
        VK.Int => "int", VK.Bool => "bool", VK.Fixed => "milli", VK.Double => "q16",
        VK.NStr => "string", VK.NArr => "array", _ => k.ToString().ToLowerInvariant(),
    };

    static string QualifiedName(ISymbol s)
    {
        var parts = new List<string> { s.Name };
        for (var t = s.ContainingType; t != null; t = t.ContainingType)
            if (t.Name != Preprocessor.GlobalClass) parts.Insert(0, t.Name);
        return string.Join(".", parts);
    }

    /// "name objective" → (name, objective)
    static (string holder, string objective) SplitHolder(string h)
    {
        int i = h.LastIndexOf(' ');
        return (h[..i], h[(i + 1)..]);
    }

    void FinishSymbols()
    {
        if (opts.Runtime == RuntimeMode.Off || !prof.RuntimeMod) return;
        var o = new System.IO.MemoryStream();
        using (var w = new Utf8JsonWriter(o, new JsonWriterOptions { Indented = true }))
        {
            void Score(ScoreVar v)
            {
                var (h, obj) = SplitHolder(v.Holder);
                w.WriteString("type", TypeName(v.Kind));
                w.WriteString("holder", h);
                w.WriteString("objective", obj);
            }
            w.WriteStartObject();
            w.WriteNumber("format", 1);
            w.WriteString("language", Language.Version);
            w.WriteString("namespace", ns);
            w.WriteString("objective", Obj);
            w.WriteString("storage", Storage);
            w.WriteString("runtime", opts.Runtime.ToString().ToLowerInvariant());
            w.WriteNumber("protocol", RuntimeProtocol);

            w.WriteStartArray("statics");
            foreach (var (f, v) in statics.OrderBy(kv => QualifiedName(kv.Key)))
            {
                w.WriteStartObject(); w.WriteString("name", QualifiedName(f)); Score(v); w.WriteEndObject();
            }
            foreach (var (f, v) in nbtStatics.OrderBy(kv => QualifiedName(kv.Key)))
            {
                w.WriteStartObject();
                w.WriteString("name", QualifiedName(f));
                switch (v)
                {
                    case NStrVar s: w.WriteString("type", "string"); w.WriteString("storage", Storage); w.WriteString("path", s.Path); break;
                    case NArrVar a: w.WriteString("type", "array"); w.WriteString("element", TypeName(a.Elem)); w.WriteString("storage", Storage); w.WriteString("path", a.Path); break;
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            w.WriteStartArray("functions");
            foreach (var f in fns.Values.Where(f => !f.Sym.Name.StartsWith(Preprocessor.TopAsmPrefix)).OrderBy(f => f.Path, StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("name", QualifiedName(f.Sym));
                w.WriteString("path", f.Path);
                w.WriteBoolean("static", f.Sym.IsStatic);
                bool callable = f.Sym.IsStatic && f.ThisObj == null && f.RetVal == null && f.Params.Values.All(p => p is ScoreVar);
                w.WriteBoolean("callable", callable);   // the interpreter can call it (score parameters only)
                w.WriteStartArray("params");
                foreach (var p in f.Sym.Parameters)
                {
                    w.WriteStartObject();
                    w.WriteString("name", p.Name);
                    if (f.Params.TryGetValue(p, out var pv) && pv is ScoreVar sv) Score(sv);
                    else w.WriteString("type", TypeName(Kind(p.Type)));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                var rk = Kind(f.Sym.ReturnType);
                if (!f.Sym.ReturnsVoid && rk is VK.Int or VK.Bool or VK.Fixed or VK.Double)
                {
                    w.WriteStartObject("returns"); Score(new ScoreVar(f.Ret, rk)); w.WriteEndObject();
                }
                if (fnLocals.TryGetValue(f, out var locals) && locals.Count > 0)
                {
                    w.WriteStartArray("locals");
                    foreach (var (name, v) in locals.DistinctBy(l => l.v.Holder))
                    {
                        w.WriteStartObject(); w.WriteString("name", name); Score(v); w.WriteEndObject();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();

            // per-entity scores: [Entity] class fields
            w.WriteStartArray("entityFields");
            foreach (var tree in userTrees)
            {
                var model = comp.GetSemanticModel(tree);
                foreach (var fd in tree.GetRoot().DescendantNodes().OfType<Microsoft.CodeAnalysis.CSharp.Syntax.FieldDeclarationSyntax>())
                    foreach (var v in fd.Declaration.Variables)
                        if (model.GetDeclaredSymbol(v) is IFieldSymbol { IsStatic: false } f && IsEntityClass(f.ContainingType))
                        {
                            w.WriteStartObject();
                            w.WriteString("class", f.ContainingType.Name);
                            w.WriteString("name", f.Name);
                            w.WriteString("type", TypeName(Kind(f.Type)));
                            w.WriteString("objective", ObjectiveOf(f));
                            w.WriteEndObject();
                        }
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        pack.DataFiles[$"{ns}/mcs/symbols.json"] = System.Text.Encoding.UTF8.GetString(o.ToArray()) + "\n";
    }
}
