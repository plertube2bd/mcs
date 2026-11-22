using System.Text;

namespace Mcs.Compiler;

public sealed class CompiledPack
{
    public Dictionary<string, List<string>> Functions { get; } = new();
    public List<string> LoadTag { get; } = new();
    public List<string> TickTag { get; } = new();

    public bool SameAs(CompiledPack o) =>
        LoadTag.SequenceEqual(o.LoadTag) && TickTag.SequenceEqual(o.TickTag)
        && Functions.Count == o.Functions.Count
        && Functions.All(kv => o.Functions.TryGetValue(kv.Key, out var l) && l.SequenceEqual(kv.Value));
}

/// <summary>Output for one contiguous version range (adjacent groups with identical output are merged).</summary>
public sealed record Layer(McVersion Min, McVersion Max, string FunctionDir, CompiledPack Pack)
{
    public string Name => Min.Id == Max.Id ? Min.Id : $"{Min.Id}-{Max.Id}";
}

public static class DatapackWriter
{
    /// Data pack formats below this use the legacy pack.mcmeta fields (pack_format/supported_formats/formats).
    const int NewMcmetaFormat = 82;

    public static List<Layer> Merge(IReadOnlyList<(TargetProfile profile, CompiledPack pack)> built)
    {
        var layers = new List<Layer>();
        foreach (var (p, pack) in built)
        {
            if (layers.Count > 0 && layers[^1].FunctionDir == p.FunctionDir && layers[^1].Pack.SameAs(pack))
                layers[^1] = layers[^1] with { Max = p.Last };
            else
                layers.Add(new Layer(p.First, p.Last, p.FunctionDir, pack));
        }
        return layers;
    }

    public static string OverlayDir(Layer l) => "mcs_" + l.Min.Id.Replace('.', '_');

    public static void Write(string outDir, string ns, string description, IReadOnlyList<Layer> layers)
    {
        // Only clean up folders that look like a previously generated pack
        if (Directory.Exists(outDir) && File.Exists(Path.Combine(outDir, "pack.mcmeta")))
            foreach (var d in Directory.GetDirectories(outDir)) Directory.Delete(d, true);
        Directory.CreateDirectory(outDir);

        var baseLayer = layers[0];
        for (int i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            string root = i == 0 ? outDir : Path.Combine(outDir, OverlayDir(layer));
            var functions = new Dictionary<string, List<string>>(layer.Pack.Functions);
            bool loadTag = true, tickTag = layer.Pack.TickTag.Count > 0;
            if (i > 0 && baseLayer.FunctionDir == layer.FunctionDir)
            {
                // Overlays can't delete base-layer files, so blank out base-only functions/tags
                foreach (var f in baseLayer.Pack.Functions.Keys) functions.TryAdd(f, new());
                tickTag |= baseLayer.Pack.TickTag.Count > 0;
            }
            foreach (var (path, lines) in functions)
            {
                int idx = path.IndexOf(':');
                string file = Path.Combine(root, "data", path[..idx], layer.FunctionDir, path[(idx + 1)..] + ".mcfunction");
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, lines.Count == 0 ? "" : string.Join("\n", lines) + "\n");
            }
            string tagDir = Path.Combine(root, "data", "minecraft", "tags", layer.FunctionDir);
            Directory.CreateDirectory(tagDir);
            if (loadTag) WriteTag(Path.Combine(tagDir, "load.json"), layer.Pack.LoadTag);
            if (tickTag) WriteTag(Path.Combine(tagDir, "tick.json"), layer.Pack.TickTag);
        }

        File.WriteAllText(Path.Combine(outDir, "pack.mcmeta"), BuildMcmeta(description, layers));
    }

    static void WriteTag(string file, List<string> values) =>
        File.WriteAllText(file, "{\n  \"values\": [" + string.Join(", ", values.Select(v => $"\"{v}\"")) + "]\n}\n");

    /// <summary>
    /// pack.mcmeta rules:
    ///  - min_format/max_format: always written (required in 1.21.9+, ignored by older versions)
    ///  - range includes formats below 82: pack_format + supported_formats are also required, and every overlay needs formats
    ///  - range only covers 82+: pack_format/supported_formats/formats must not be present
    /// </summary>
    static string BuildMcmeta(string description, IReadOnlyList<Layer> layers)
    {
        int min = layers[0].Min.Format, max = layers[^1].Max.Format;
        bool legacy = min < NewMcmetaFormat;
        var sb = new StringBuilder();
        sb.Append("{\n  \"pack\": {\n");
        sb.Append($"    \"description\": {Json.Str(description)},\n");
        if (legacy)
        {
            sb.Append($"    \"pack_format\": {min},\n");
            if (max != min) sb.Append($"    \"supported_formats\": {{ \"min_inclusive\": {min}, \"max_inclusive\": {max} }},\n");
        }
        sb.Append($"    \"min_format\": {min},\n    \"max_format\": {max}\n  }}");
        var overlays = layers.Skip(1).ToList();
        if (overlays.Count > 0)
        {
            sb.Append(",\n  \"overlays\": {\n    \"entries\": [\n");
            for (int i = 0; i < overlays.Count; i++)
            {
                var o = overlays[i];
                int lo = o.Min.Format, hi = o.Max.Format;
                sb.Append($"      {{ \"directory\": \"{OverlayDir(o)}\", ");
                if (legacy) sb.Append($"\"formats\": {{ \"min_inclusive\": {lo}, \"max_inclusive\": {hi} }}, ");
                sb.Append($"\"min_format\": {lo}, \"max_format\": {hi} }}");
                sb.Append(i + 1 < overlays.Count ? ",\n" : "\n");
            }
            sb.Append("    ]\n  }");
        }
        sb.Append("\n}\n");
        return sb.ToString();
    }
}

public static class Json
{
    public static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append($"\\u{(int)c:x4}"); else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
