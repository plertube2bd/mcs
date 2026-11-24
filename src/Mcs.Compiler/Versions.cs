namespace Mcs.Compiler;

/// <summary>A Minecraft release and its data pack format.</summary>
public sealed record McVersion(string Id, int Format, int FormatMinor)
{
    public override string ToString() => Id;
    public string Symbol => "MC_" + Id.Replace('.', '_') + "_OR_GREATER";
}

/// <summary>
/// One group of releases that share a data pack format. The compiler generates code once per group.
/// Version-specific behavior is decided here, and only here.
/// </summary>
public sealed record TargetProfile(McVersion First, McVersion Last)
{
    public int Format => First.Format;
    public string Name => First.Id == Last.Id ? First.Id : $"{First.Id}-{Last.Id}";

    /// data/<ns>/functions (≤1.20.6) → data/<ns>/function (1.21+)
    public string FunctionDir => Format >= 45 ? "function" : "functions";
    /// Attribute IDs: minecraft:generic.max_health (≤1.21.1) → minecraft:max_health (1.21.2+)
    public string AttributePrefix => Format >= 57 ? "" : "generic.";
    /// Function macros ($(var)) — 1.20.2+
    public bool Macros => Format >= 18;
    /// Spawn chunks were removed in 1.21.9, so the world spawn marker chunk must be force-loaded
    public bool ForceloadSpawn => Format >= 88;
    /// execute if items / item predicates, item "count" (1.20.5+)
    public bool ItemPredicates => Format >= 41;
    /// Text components: click_event/hover_event, run_command uses "command" (1.21.5+)
    public bool NewTextComponents => Format >= 71;
    /// Any item can be made usable (right-click detection): food component (1.20.5–1.21.1) → consumable (1.21.2+)
    public bool ConsumableComponent => Format >= 57;
    /// Block state NBT: {Name, Properties} → {id, properties} (26.3+; display entities' block_state)
    public bool NewBlockStateNbt => Format >= 121;
    /// /compute (number-provider expressions in one command) — 26.3+
    public bool Compute => Format >= 121;
    /// worldborder set: duration in whole seconds (≤1.21.10) → a time argument ("30s", 1.21.11+)
    public bool WorldBorderTime => Format >= 94;
    /// World clocks (26.1+): time of day = "time query day" (the day timeline), days = "time query time" / 24000
    public bool Timelines => Format >= 101;
    /// Scoreboard display slot: belowName (≤1.20.1) → below_name (1.20.2+)
    public string BelowNameSlot => Format >= 18 ? "below_name" : "belowName";

    /// #if symbols: MC_<v>_OR_GREATER for the first version of each group up to and including this one
    public IEnumerable<string> Symbols => Versions.Groups.TakeWhile(g => g.Format <= Format).Select(g => g.First.Symbol);

}

public static class Versions
{
    /// Supported releases (1.20.1 – 26.3). Source: https://minecraft.wiki/w/Pack_format
    public static readonly McVersion[] Releases =
    [
        new("1.20.1", 15, 0), new("1.20.2", 18, 0), new("1.20.3", 26, 0), new("1.20.4", 26, 0),
        new("1.20.5", 41, 0), new("1.20.6", 41, 0), new("1.21", 48, 0), new("1.21.1", 48, 0),
        new("1.21.2", 57, 0), new("1.21.3", 57, 0), new("1.21.4", 61, 0), new("1.21.5", 71, 0),
        new("1.21.6", 80, 0), new("1.21.7", 81, 0), new("1.21.8", 81, 0), new("1.21.9", 88, 0),
        new("1.21.10", 88, 0), new("1.21.11", 94, 1), new("26.1", 101, 1), new("26.1.1", 101, 1),
        new("26.1.2", 101, 1), new("26.2", 107, 1), new("26.3", 121, 0),
    ];

    public const string DefaultRange = "1.20.1-26.3";

    public static readonly TargetProfile[] Groups = Releases
        .GroupBy(r => r.Format)
        .Select(g => new TargetProfile(g.First(), g.Last()))
        .ToArray();

    public static McVersion Find(string id) =>
        Releases.FirstOrDefault(r => r.Id == id.Trim())
        ?? throw new CompileException($"Unsupported Minecraft version '{id.Trim()}'. Supported: {Releases[0]} – {Releases[^1]} ({string.Join(", ", Releases.Select(r => r.Id))})");

    /// "1.21.4" or "1.20.1-26.3"
    public static (McVersion min, McVersion max) ParseRange(string spec)
    {
        var parts = spec.Split('-', 2);
        var min = Find(parts[0]);
        var max = parts.Length > 1 ? Find(parts[1]) : min;
        if (Array.IndexOf(Releases, min) > Array.IndexOf(Releases, max))
            throw new CompileException($"Invalid version range '{spec}': the minimum is greater than the maximum.");
        return (min, max);
    }

    /// Groups covering the range; the first/last group is narrowed to the actual versions requested.
    public static List<TargetProfile> GroupsIn(McVersion min, McVersion max)
    {
        int lo = Array.IndexOf(Releases, min), hi = Array.IndexOf(Releases, max);
        return Releases[lo..(hi + 1)]
            .GroupBy(r => r.Format)
            .Select(g => new TargetProfile(g.First(), g.Last()))
            .ToList();
    }
}
