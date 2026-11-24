using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// Player events ([OnJoin], [OnDeath], [OnRightClick], [OnTrigger], …), all driven by one tick function
/// (<ns>:__events/tick) that finds the players whose event score changed and runs the handlers as them.
public sealed partial class Codegen
{
    static readonly string[] EventAttrs = ["OnJoin", "OnFirstJoin", "OnDeath", "OnRespawn", "OnKill", "OnRightClick", "OnRightClickObject", "OnTrigger", "OnStat"];
    static readonly HashSet<string> ReadOnlyCriteria = ["dummy", "trigger", "health", "food", "air", "armor", "level", "xp"];

    /// One score-based event source: an objective, its criteria, and its handlers.
    sealed class EventSource(string objective, string criteria)
    {
        public string Objective = objective, Criteria = criteria;
        public List<FnInfo> Handlers = new();
        public Dictionary<FnInfo, ItemVar> ObjectFilter = new();   // [OnRightClickObject]: only when holding this object
    }

    readonly Dictionary<string, EventSource> eventSources = new();   // key: kind + argument
    readonly List<FnInfo> joinHandlers = new(), firstJoinHandlers = new(), respawnHandlers = new();
    /// [OnRightClickObject] on items other than carrot/warped fungus on a stick (1.20.5+): the item gets a
    /// never-ending food/consumable component, and the using_item advancement trigger detects the use.
    readonly HashSet<string> clickObjects = new();                                        // ObjIds
    readonly Dictionary<string, (ItemVar Item, List<FnInfo> Handlers)> useEvents = new();   // ObjId → handlers

    static bool IsStickItem(string id) => id is "minecraft:carrot_on_a_stick" or "minecraft:warped_fungus_on_a_stick";

    /// Which item objects need the use component — known before any `give` is generated.
    void CollectClickObjects()
    {
        foreach (var info in fns.Values)
            foreach (var attr in info.Sym.GetAttributes())
                if (attr.AttributeClass?.Name == "OnRightClickObjectAttribute" && attr.ConstructorArguments.FirstOrDefault().Value is string name)
                {
                    var found = itemStatics.Where(kv => kv.Key.Name == name).Select(kv => kv.Value).ToList();
                    if (found.Count == 1 && !IsStickItem(found[0].Id) && prof.ItemPredicates) clickObjects.Add(found[0].ObjId);
                }
    }

    string JoinedTag => $"mcs.{ns}.joined";
    string DeadTag => $"mcs.{ns}.dead";

    void CollectEvents()
    {
        foreach (var info in fns.Values)
            foreach (var attr in info.Sym.GetAttributes())
            {
                string? kind = attr.AttributeClass?.Name is { } n && n.EndsWith("Attribute") ? n[..^9] : null;
                if (kind == null || !EventAttrs.Contains(kind)) continue;
                try { AddEvent(info, kind, attr.ConstructorArguments.FirstOrDefault().Value as string); }
                catch (UnsupportedException e) { Error(e.Node, e.Message); }
            }
    }

    void AddEvent(FnInfo info, string kind, string? arg)
    {
        SyntaxNode at = ((MethodDeclarationSyntax)info.Decl).Identifier.Parent!;
        if (!info.Sym.IsStatic || !info.Sym.ReturnsVoid)
            throw new UnsupportedException(info.Decl, $"[{kind}] methods must be static and return void.");
        bool hasInt = false;
        foreach (var (p, v) in info.Params)
        {
            if (v is ScoreVar { Kind: VK.Int } && !hasInt) { hasInt = true; continue; }
            if (v is RefVar && ClassOf(p.Type)?.IsPlayer == true) continue;
            throw new UnsupportedException(info.Decl, $"[{kind}] methods can take a Player and an int, not '{p.Type} {p.Name}'.");
        }
        if (hasInt && kind is "OnJoin" or "OnFirstJoin" or "OnRespawn")
            throw new UnsupportedException(info.Decl, $"[{kind}] has no value: remove the int parameter.");

        switch (kind)
        {
            case "OnJoin": joinHandlers.Add(info); Source("leave", $"{ns}.ev.leave", "minecraft.custom:minecraft.leave_game"); return;
            case "OnFirstJoin": firstJoinHandlers.Add(info); return;
            case "OnRespawn": respawnHandlers.Add(info); Source("death", $"{ns}.ev.death", "deathCount"); return;
            case "OnDeath": Source("death", $"{ns}.ev.death", "deathCount").Handlers.Add(info); return;
            case "OnKill": Source("kill", $"{ns}.ev.kill", "totalKillCount").Handlers.Add(info); return;
            case "OnRightClick":
            {
                string id = ResolveId("items", arg ?? "carrot_on_a_stick", at);
                Source("use:" + id, $"{ns}.ev.use.{id[10..]}", $"minecraft.used:{id.Replace(':', '.')}").Handlers.Add(info);
                return;
            }
            case "OnRightClickObject":
            {
                var item = ItemByName(arg ?? "", at);
                if (!IsStickItem(item.Id))
                {
                    if (!prof.ItemPredicates)
                        throw new UnsupportedException(at, $"Right-click detection for {item.Id} needs Minecraft 1.20.5+ (this build also targets older versions). " +
                            "Build for 1.20.5 and above (--mc 1.20.5-… or \"minecraft\" in mcs.json), or make the item from carrot_on_a_stick / warped_fungus_on_a_stick, which work on every version.");
                    if (!useEvents.TryGetValue(item.ObjId, out var ue)) useEvents[item.ObjId] = ue = (item, new());
                    ue.Handlers.Add(info);
                    return;
                }
                var src = Source("use:" + item.Id, $"{ns}.ev.use.{item.Id[10..]}", $"minecraft.used:{item.Id.Replace(':', '.')}");
                src.Handlers.Add(info);
                src.ObjectFilter[info] = item;
                return;
            }
            case "OnTrigger":
                if (arg == null || !Regex.IsMatch(arg, "^[A-Za-z0-9_.+-]+$")) throw new UnsupportedException(at, $"Invalid trigger objective name '{arg}'.");
                Source("trigger:" + arg, arg, "trigger").Handlers.Add(info);
                return;
            case "OnStat":
                if (arg == null || !Regex.IsMatch(arg, "^[A-Za-z0-9_.:-]+$")) throw new UnsupportedException(at, $"Invalid criterion '{arg}'.");
                // minecraft.used:minecraft:stone → minecraft.used:minecraft.stone (stat names use '.', not ':')
                if (arg.IndexOf(':') is var c && c >= 0) arg = arg[..(c + 1)] + arg[(c + 1)..].Replace(':', '.');
                if (ReadOnlyCriteria.Contains(arg) || arg.StartsWith("teamkill.") || arg.StartsWith("killedByTeam."))
                    throw new UnsupportedException(at, $"'{arg}' is not a counter statistic, so it cannot be an event (use a counter such as minecraft.custom:minecraft.jump).");
                Source("stat:" + arg, $"{ns}.ev.s{eventSources.Count(e => e.Key.StartsWith("stat:"))}", arg).Handlers.Add(info);
                return;
        }
    }

    EventSource Source(string key, string objective, string criteria)
    {
        if (!eventSources.TryGetValue(key, out var s)) eventSources[key] = s = new EventSource(objective, criteria);
        return s;
    }

    /// Commands that call a handler as @s: pass @s as the Player parameter and the score value as the int.
    IEnumerable<string> CallHandler(FnInfo h, string? valueHolder)
    {
        foreach (var v in h.Params.Values)
        {
            if (v is RefVar r) { yield return $"tag @a remove {r.Tag}"; yield return $"tag @s add {r.Tag}"; }
            else if (v is ScoreVar sv) yield return valueHolder == null ? $"scoreboard players set {sv.Holder} 0" : $"scoreboard players operation {sv.Holder} = {valueHolder}";
        }
        yield return $"function {h.Path}";
    }

    void FinishEvents(List<string> load)
    {
        if (eventSources.Count == 0 && joinHandlers.Count == 0 && firstJoinHandlers.Count == 0 && useEvents.Count == 0) return;
        string dir = $"{ns}:__events";
        var tick = new List<string>();
        string Fn(string name, List<string> cmds) { pack.Functions[$"{dir}/{name}"] = cmds; return $"{dir}/{name}"; }

        // Item objects with a use component: using_item fires every tick while the button is held, so a use
        // counts as a click only when the item was not being used in the previous tick.
        int u = 0;
        foreach (var (objId, (item, handlers)) in useEvents)
        {
            string adv = $"{ns}:__obj/use{u}", last = $"{ns}.ev.u{u}", now = H("$__ev.now"), d = H("$__ev.d");
            load.Add($"scoreboard objectives add {last} dummy");
            var run = new List<string> { $"scoreboard players set {H("$__ev.value")} 1" };
            foreach (var h in handlers) run.AddRange(CallHandler(h, H("$__ev.value")));
            string runFn = Fn($"use{u}_run", run);
            Fn($"use{u}", new()
            {
                $"advancement revoke @s only {adv}",
                $"execute unless score @s {last} = @s {last} run scoreboard players set @s {last} -100",
                $"execute store result score {now} run time query gametime",
                $"scoreboard players operation {d} = {now}",
                $"scoreboard players operation {d} -= @s {last}",
                $"scoreboard players operation @s {last} = {now}",
                $"execute unless score {d} matches 0..1 run function {runFn}",
            });
            pack.Advancements[adv] =
                "{\"criteria\":{\"use\":{\"trigger\":\"minecraft:using_item\",\"conditions\":{\"item\":{\"predicates\":{\"minecraft:custom_data\":"
                + Json.Str($"{{mcs_obj:{Json.Str(objId)}}}") + $"}}}}}}}}}},\"rewards\":{{\"function\":\"{dir}/use{u}\"}}}}";
            u++;
        }

        foreach (var s in eventSources.Values) load.Add($"scoreboard objectives add {s.Objective} {s.Criteria}");
        string value = H("$__ev.value");

        // Join: players never seen get a tag (first join); a leave_game count means they came back.
        if (joinHandlers.Count > 0 || firstJoinHandlers.Count > 0)
        {
            var first = new List<string> { $"tag @s add {JoinedTag}" };
            if (eventSources.TryGetValue("leave", out var leave)) first.Add($"scoreboard players set @s {leave.Objective} 0");
            foreach (var h in firstJoinHandlers) first.AddRange(CallHandler(h, null));
            if (joinHandlers.Count > 0)
            {
                var join = new List<string> { $"scoreboard players set @s {leave!.Objective} 0" };
                foreach (var h in joinHandlers) join.AddRange(CallHandler(h, null));
                string joinFn = Fn("join", join);
                first.Add($"function {joinFn}");
                tick.Add($"execute as @a[tag=!{JoinedTag}] at @s run function {Fn("first_join", first)}");
                tick.Add($"execute as @a[scores={{{leave.Objective}=1..}}] at @s run function {joinFn}");
            }
            else tick.Add($"execute as @a[tag=!{JoinedTag}] at @s run function {Fn("first_join", first)}");
        }

        // Counter events: copy the value, reset the counter, run the handlers. Death comes before respawn
        // (with doImmediateRespawn both happen in the same tick).
        int n = 0, objHandlers = 0;
        foreach (var (key, s) in eventSources.OrderBy(e => e.Key == "death" ? 0 : 1))
        {
            if (key == "leave") continue;
            bool isTrigger = s.Criteria == "trigger";
            var cmds = new List<string> { $"scoreboard players operation {value} = @s {s.Objective}", $"scoreboard players set @s {s.Objective} 0" };
            if (key == "death" && respawnHandlers.Count > 0) cmds.Add($"tag @s add {DeadTag}");
            foreach (var h in s.Handlers)
            {
                if (!s.ObjectFilter.TryGetValue(h, out var obj)) { cmds.AddRange(CallHandler(h, value)); continue; }
                // the used item is the main-hand one if it is this item type, otherwise the off-hand one
                string hit = H("$__ev.hit");
                var (main, off) = HoldsObject(obj);
                cmds.Add($"scoreboard players set {hit} 0");
                cmds.Add($"execute {main} run scoreboard players set {hit} 1");
                cmds.Add($"execute {off} run scoreboard players set {hit} 1");
                cmds.Add($"execute if score {hit} matches 1 run function {Fn($"obj{objHandlers++}", CallHandler(h, value).ToList())}");
            }
            string name = key == "death" ? "death" : key == "kill" ? "kill" : $"e{n++}";
            if (isTrigger) tick.Add($"scoreboard players enable @a {s.Objective}");
            tick.Add($"execute as @a[scores={{{s.Objective}=1..}}] at @s run function {Fn(name, cmds)}");
            // @e only selects living entities, so a tagged player found by @e has respawned
            if (key == "death" && respawnHandlers.Count > 0)
            {
                var resp = new List<string> { $"tag @s remove {DeadTag}" };
                foreach (var h in respawnHandlers) resp.AddRange(CallHandler(h, null));
                tick.Add($"execute as @e[type=minecraft:player,tag={DeadTag}] at @s run function {Fn("respawn", resp)}");
            }
        }
        if (tick.Count == 0) return;
        pack.Functions[$"{dir}/tick"] = tick;
        pack.TickTag.Add($"{dir}/tick");
    }
}
