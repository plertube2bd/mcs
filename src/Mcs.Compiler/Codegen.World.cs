using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Mcs.Compiler;

/// A box of blocks (BlockLib.Region) — compile-time corners.
sealed class RegionVar(Pos from, Pos to) : Var { public Pos From = from, To = to; }
/// A clipboard: the region to paste from (the source itself, or a buffer holding a snapshot).
sealed class ClipVar(RegionVar src) : Var { public RegionVar Src = src; }
/// A team (TeamLib.Team) or an objective (TeamLib.Objective), by name.
sealed class TeamVar(string name, bool isObjective) : Var { public string Name = name; public bool IsObjective = isObjective; }

/// An extern method: a function of another datapack.
sealed record ExternFn(string Path, string? McsNs, string Prefix);

/// WorldLib, BlockLib regions, ParticleLib, SoundLib, TeamLib, entity interactions, m.MountEntity / m.MountBlock,
/// calls into other datapacks, and Minecraft division.
public sealed partial class Codegen
{
    readonly Dictionary<string, string?> teams = new();                       // team name → display name
    readonly List<string> teamLoad = new();
    readonly Dictionary<IFieldSymbol, Var> mountedStatics = new(SymbolEqualityComparer.Default);   // m.MountEntity / m.MountBlock fields
    readonly Dictionary<IFieldSymbol, Var> teamStatics = new(SymbolEqualityComparer.Default);
    readonly Dictionary<IMethodSymbol, ExternFn> externs = new(SymbolEqualityComparer.Default);

    // ================================================================ division
    /// `/` and `%` compile to a single scoreboard operation ([MinecraftDivision] or "division": "minecraft").
    bool McDivision()
    {
        for (ISymbol? s = cur.Info.Sym; s != null; s = s.ContainingType)
        {
            if (HasAttr(s, "MinecraftDivisionAttribute")) return true;
            if (HasAttr(s, "CSharpDivisionAttribute")) return false;
        }
        return opts.MinecraftDivision;
    }

    // ================================================================ selectors of entity expressions
    /// Selector of an Entity object or entity reference (any number of entities).
    string AnySel(ExpressionSyntax e)
    {
        var k = Kind(cur.Model.GetTypeInfo(e).Type);
        if (k == VK.Entity) return Sel(GetEnt(e));
        if (k == VK.Ref) return GetRef(e).Selector;
        if (TryConstString(Unparen(e)) is string name) return name;   // a player name / selector string
        throw new UnsupportedException(e, "Expected an Entity or an entity reference (e.g. Player.Self).");
    }

    /// Selector that matches at most one entity.
    string SingleSel(ExpressionSyntax e)
    {
        string s = AnySel(e);
        return (s.StartsWith("@e") || s.StartsWith("@a")) && !s.Contains("limit=") ? SelWith(s, "limit=1") : s;
    }

    string RecvSel(InvocationExpressionSyntax inv) => inv.Expression is MemberAccessExpressionSyntax ma ? AnySel(ma.Expression) : Resolve(ThisRef(inv)).Selector;

    Operand StoreT(string mode, string tail)
    {
        string t = Temp();
        Emit($"scoreboard players set {t} 0");
        Emit($"execute store {mode} score {t} {tail}");
        return new ScoreOp(t);
    }

    static string Dim(IFieldSymbol f) => "minecraft:" + (McIdOf(f) ?? Snake(f.Name));

    string DimOf(ExpressionSyntax e) => cur.Model.GetSymbolInfo(e).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } f ? Dim(f)
        : throw new UnsupportedException(e, "The dimension must be a constant (Dimension.Nether).");

    string EnumId(ExpressionSyntax e, string what) => cur.Model.GetSymbolInfo(e).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } f ? McIdOf(f) ?? Snake(f.Name)
        : throw new UnsupportedException(e, $"{what} must be a constant.");

    // ================================================================ dispatch
    bool TryEmitWorldLib(string name, InvocationExpressionSyntax inv, out Operand? result)
    {
        result = null;
        string head = name.Contains('.') ? name[..name.IndexOf('.')] : name;
        switch (head)
        {
            case "world": result = EmitWorld(name, inv); return true;
            case "region" or "clip": result = EmitRegion(name, inv); return true;
            case "particle": EmitParticle(name, inv); return true;
            case "sound": EmitSound(name, inv); return true;
            case "team": result = EmitTeam(name, inv); return true;
            case "score": result = EmitScore(name, inv); return true;
            case "ex" or "interaction": result = EmitInteraction(name, inv); return true;
            case "nstr": result = EmitNStr(name, inv); return true;
            case "narr": result = EmitNArr(name, inv); return true;
            case "storage" or "nbt": result = EmitStorage(name, inv); return true;
            case "mount" when name is "mount.entity" or "mount.block": EmitMountLocal(name, inv); result = new ConstOp(1); return true;
            case "call":
            {
                string fn = ConstStr(inv.ArgumentList.Arguments[0].Expression).TrimStart('/');
                if (fn.StartsWith("function ")) fn = fn[9..];
                if (inv.Parent is ExpressionStatementSyntax) Emit($"function {fn}");   // result not used
                else result = StoreT("result", $"run function {fn}");
                return true;
            }
        }
        return false;
    }

    // ================================================================ WorldLib
    Operand? EmitWorld(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        switch (name)
        {
            case "world.time": return StoreT("result", prof.Timelines ? "run time query day" : "run time query daytime");
            case "world.day":
            {
                if (!prof.Timelines) return StoreT("result", "run time query day");
                var t = StoreT("result", "run time query time");   // 26.1+: the clock's total time
                RawOp(((ScoreOp)t).Holder, '/', 24000);
                return t;
            }
            case "world.gametime": return StoreT("result", "run time query gametime");
            case "world.settime": cur.Sink.AddRange(MacroCmd("time set $(t)", inv, ("t", EvalNum(Arg(0), VK.Int), VK.Int))); return null;
            case "world.addtime": cur.Sink.AddRange(MacroCmd("time add $(t)", inv, ("t", EvalNum(Arg(0), VK.Int), VK.Int))); return null;
            case "world.setweather":
            {
                string w = EnumId(Arg(0), "The weather");
                var secs = args.Count > 1 ? EvalNum(Arg(1), VK.Int) : new ConstOp(-1);
                if (secs is ConstOp { Value: < 0 }) Emit($"weather {w}");
                else cur.Sink.AddRange(MacroCmd($"weather {w} $(s)s", inv, ("s", secs, VK.Int)));
                return null;
            }
            case "world.gamerule":
            {
                var (rule, inverted) = GameRuleName(ConstStr(Arg(0)));
                var cv = cur.Model.GetConstantValue(Arg(1));
                if (cv.Value is bool b) { Emit($"gamerule {rule} {(b != inverted ? "true" : "false")}"); return null; }
                if (Kind(cur.Model.GetTypeInfo(Arg(1)).Type) == VK.Bool)
                {
                    var c = EvalCond(Arg(1));
                    if (inverted) c = Negate(c);
                    Emit($"execute {string.Join(" ", c.Parts)} run gamerule {rule} true");
                    Emit($"execute {string.Join(" ", Negate(c).Parts)} run gamerule {rule} false");
                    return null;
                }
                cur.Sink.AddRange(MacroCmd($"gamerule {rule} $(v)", inv, ("v", EvalNum(Arg(1), VK.Int), VK.Int)));
                return null;
            }
            case "world.border":
            {
                var size = EvalNum(Arg(0), VK.Double);
                var secs = args.Count > 1 ? EvalNum(Arg(1), VK.Int) : new ConstOp(0);
                cur.Sink.AddRange(MacroCmd(secs is ConstOp { Value: 0 } ? "worldborder set $(d)" : $"worldborder set $(d) $(s){(prof.WorldBorderTime ? "s" : "")}", inv, ("d", size, VK.Double), ("s", secs, VK.Int)));
                return null;
            }
            case "world.bordercenter":
                cur.Sink.AddRange(MacroCmd("worldborder center $(x) $(z)", inv, ("x", EvalNum(Arg(0), VK.Double), VK.Double), ("z", EvalNum(Arg(1), VK.Double), VK.Double)));
                return null;
            case "world.run":
                Emit($"execute in {DimOf(Arg(0))} run {ActionCommand(Arg(1), false)}");
                return null;
            case "world.isin": return StoreT("success", $"if dimension {DimOf(Arg(0))}");
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    /// Game rules renamed in 1.21.11 (camelCase → snake_case ids). The others only change case
    /// (keepInventory → keep_inventory). "!" marks a rule whose meaning was inverted (disableRaids → raids).
    static readonly Dictionary<string, string> RenamedRules = new()
    {
        ["announceAdvancements"] = "show_advancement_messages", ["commandBlocksEnabled"] = "command_blocks_work",
        ["commandModificationBlockLimit"] = "max_block_modifications", ["disableElytraMovementCheck"] = "!elytra_movement_check",
        ["disablePlayerMovementCheck"] = "!player_movement_check", ["disableRaids"] = "!raids",
        ["doDaylightCycle"] = "advance_time", ["doEntityDrops"] = "entity_drops", ["doImmediateRespawn"] = "immediate_respawn",
        ["doInsomnia"] = "spawn_phantoms", ["doLimitedCrafting"] = "limited_crafting", ["doMobLoot"] = "mob_drops",
        ["doMobSpawning"] = "spawn_mobs", ["doPatrolSpawning"] = "spawn_patrols", ["doTileDrops"] = "block_drops",
        ["doTraderSpawning"] = "spawn_wandering_traders", ["doVinesSpread"] = "spread_vines", ["doWardenSpawning"] = "spawn_wardens",
        ["doWeatherCycle"] = "advance_weather", ["maxCommandChainLength"] = "max_command_sequence_length",
        ["maxCommandForkCount"] = "max_command_forks", ["naturalRegeneration"] = "natural_health_regeneration",
        ["snowAccumulationHeight"] = "max_snow_accumulation_height", ["spawnRadius"] = "respawn_radius",
        ["spawnerBlocksEnabled"] = "spawner_blocks_work",
    };

    /// The game rule id for this version (either spelling is accepted) and whether the value must be inverted.
    (string name, bool inverted) GameRuleName(string rule)
    {
        string bare = rule.StartsWith("minecraft:") ? rule[10..] : rule;
        bool snake = bare.Contains('_') || bare.All(c => !char.IsUpper(c)) && RenamedRules.Values.Any(v => v.TrimStart('!') == bare);
        if (prof.Format >= 94)
        {
            if (snake || bare.All(char.IsLower)) return (bare, false);
            if (RenamedRules.TryGetValue(bare, out var n)) return (n.TrimStart('!'), n.StartsWith('!'));
            return (Snake(bare), false);
        }
        if (!snake) return (bare, false);
        var old = RenamedRules.FirstOrDefault(kv => kv.Value.TrimStart('!') == bare);
        if (old.Key != null) return (old.Key, old.Value.StartsWith('!'));
        var parts = bare.Split('_');
        return (parts[0] + string.Concat(parts.Skip(1).Select(p => p.Length > 0 ? char.ToUpperInvariant(p[0]) + p[1..] : "")), false);
    }

    /// Static properties with an [Intrinsic] (World.Weather = …, World.Difficulty = …).
    bool EmitStaticPropSet(IPropertySymbol p, AssignmentExpressionSyntax a)
    {
        switch (Intrinsic(p))
        {
            case "world.weather": Emit($"weather {EnumId(a.Right, "The weather")}"); return true;
            case "world.difficulty": Emit($"difficulty {EnumId(a.Right, "The difficulty")}"); return true;
        }
        return false;
    }

    // ================================================================ regions
    /// The shared execute prefix of several positions (absolute ones fit any prefix).
    static string CommonPrefix(SyntaxNode at, params Pos[] ps)
    {
        var prefixes = ps.Where(p => !p.IsAbsolute || p.Prefix.Length > 0).Select(p => p.Prefix).Distinct().ToList();
        if (ps.Any(p => p.Args.Length > 0)) throw new UnsupportedException(at, "Region corners need constant coordinates.");
        if (prefixes.Count > 1) throw new UnsupportedException(at, "All positions must be relative to the same position (e.g. p and p.Offset(...)) or absolute.");
        return prefixes.FirstOrDefault() ?? "";
    }

    static string Pre(string prefix) => prefix.Length > 0 ? prefix + " " : "";

    RegionVar GetRegion(ExpressionSyntax e) => EvalCtObj(e) as RegionVar ?? throw new UnsupportedException(e, "Expected a Region.");

    /// Lowest corner and size of a region, when they are known at compile time (same kind of coordinates).
    static (Pos min, int[] size)? RegionBox(RegionVar r)
    {
        string[] a = [r.From.X, r.From.Y, r.From.Z], b = [r.To.X, r.To.Y, r.To.Z];
        var min = new string[3]; var size = new int[3];
        for (int i = 0; i < 3; i++)
        {
            bool ra = a[i].StartsWith('~'), rb = b[i].StartsWith('~');
            if (ra != rb || a[i].StartsWith('^') || b[i].StartsWith('^')) return null;
            double va = ra ? (a[i].Length > 1 ? double.Parse(a[i][1..], CultureInfo.InvariantCulture) : 0) : double.Parse(a[i], CultureInfo.InvariantCulture);
            double vb = rb ? (b[i].Length > 1 ? double.Parse(b[i][1..], CultureInfo.InvariantCulture) : 0) : double.Parse(b[i], CultureInfo.InvariantCulture);
            double lo = Math.Min(va, vb);
            size[i] = (int)Math.Abs(Math.Floor(vb) - Math.Floor(va)) + 1;
            min[i] = ra ? (lo == 0 ? "~" : "~" + Fmt(lo)) : Fmt(lo);
        }
        var prefix = r.From.Prefix.Length > 0 ? r.From.Prefix : r.To.Prefix;
        return (new Pos(prefix, min[0], min[1], min[2]), size);
    }

    Operand Clone(RegionVar r, Pos dest, string tail, SyntaxNode at)
    {
        string prefix = CommonPrefix(at, r.From, r.To, dest);
        return StoreT("success", $"{Pre(prefix)}run clone {r.From.Coords} {r.To.Coords} {dest.Coords}{tail}");
    }

    Operand? EmitRegion(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        var recvExpr = ((MemberAccessExpressionSyntax)inv.Expression).Expression;
        bool Masked(int i) => args.Count > i && cur.Model.GetConstantValue(Arg(i)).Value is true;
        if (name == "clip.paste")
        {
            var clip = EvalCtObj(recvExpr) as ClipVar ?? throw new UnsupportedException(recvExpr, "Expected a Clipboard.");
            return Clone(clip.Src, EvalPos(Arg(0)), Masked(1) ? " masked" : "", inv);
        }
        var r = GetRegion(recvExpr);
        string prefix = CommonPrefix(inv, r.From, r.To);
        string box = $"{r.From.Coords} {r.To.Coords}";
        switch (name)
        {
            case "region.fill":
            {
                bool obj = Kind(cur.Model.GetTypeInfo(Arg(0)).Type) == VK.Block;
                return StoreT("success", $"{Pre(prefix)}run fill {box} {BlockArg(Arg(0), obj)}");
            }
            case "region.replace": return StoreT("success", $"{Pre(prefix)}run fill {box} {BlockArg(Arg(1), false)} replace {BlockArg(Arg(0), false)}");
            case "region.clear": return StoreT("success", $"{Pre(prefix)}run fill {box} minecraft:air");
            case "region.clone": return Clone(r, EvalPos(Arg(0)), Masked(1) ? " masked" : "", inv);
            case "region.move": return Clone(r, EvalPos(Arg(0)), " replace move", inv);
            case "region.same":
            {
                var other = EvalPos(Arg(0));
                return StoreT("success", $"{Pre(CommonPrefix(inv, r.From, r.To, other))}if blocks {box} {other.Coords} {(Masked(1) ? "masked" : "all")}");
            }
            case "region.stack":
            {
                var bx = RegionBox(r) ?? throw new UnsupportedException(inv, "Stack needs corners whose coordinates are both relative (~) or both absolute.");
                double dx, dy, dz; int count;
                if (args.Count >= 3)
                {
                    (dx, dy, dz) = (ConstDouble(Arg(0), "Offsets"), ConstDouble(Arg(1), "Offsets"), ConstDouble(Arg(2), "Offsets"));
                    count = args.Count > 3 ? ConstSlot(Arg(3)) : 1;
                }
                else
                {
                    var (sx, sy, sz) = (bx.size[0], bx.size[1], bx.size[2]);
                    (dx, dy, dz) = EnumId(Arg(0), "The direction") switch
                    {
                        "up" => (0d, (double)sy, 0d), "down" => (0d, (double)-sy, 0d), "north" => (0d, 0d, (double)-sz), "south" => (0d, 0d, (double)sz),
                        "east" => ((double)sx, 0d, 0d), _ => ((double)-sx, 0d, 0d),
                    };
                    count = args.Count > 1 ? ConstSlot(Arg(1)) : 1;
                }
                if (count is < 1 or > 64) throw new UnsupportedException(inv, "Stack count must be 1..64.");
                string t = Temp();
                Emit($"scoreboard players set {t} 1");
                for (int k = 1; k <= count; k++)
                {
                    var dest = bx.min.Offset(dx * k, dy * k, dz * k);
                    var ok = Clone(r, dest, "", inv);
                    Emit($"execute unless score {((ScoreOp)ok).Holder} matches 1 run scoreboard players set {t} 0");
                }
                return new ScoreOp(t);
            }
            case "region.copy" or "region.cut":
                EvalCtObj(inv);
                return null;
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    /// Compile-time library objects: Region, Clipboard, Team, Objective.
    Var EvalCtObj(ExpressionSyntax e)
    {
        e = Unparen(e);
        var sym = cur.Model.GetSymbolInfo(e).Symbol;
        switch (Intrinsic(sym))
        {
            case "region.new":
            {
                var args = ((BaseObjectCreationExpressionSyntax)e).ArgumentList!.Arguments;
                return new RegionVar(EvalPos(args[0].Expression), EvalPos(args[1].Expression));
            }
            case "region.copy" or "region.cut":
            {
                var inv = (InvocationExpressionSyntax)e;
                var r = GetRegion(((MemberAccessExpressionSyntax)inv.Expression).Expression);
                if (inv.ArgumentList.Arguments.Count == 0) return new ClipVar(r);
                var buf = EvalPos(inv.ArgumentList.Arguments[0].Expression);
                var bx = RegionBox(r) ?? throw new UnsupportedException(inv, "Copy/Cut with a buffer needs corners whose coordinates are both relative (~) or both absolute.");
                Clone(r, buf, Intrinsic(sym) == "region.cut" ? " replace move" : "", inv);
                return new ClipVar(new RegionVar(buf, buf.Offset(bx.size[0] - 1, bx.size[1] - 1, bx.size[2] - 1)));
            }
            case "team.new":
            {
                var args = ((BaseObjectCreationExpressionSyntax)e).ArgumentList!.Arguments;
                var tv = NewTeam(ConstStr(args[0].Expression), args.Count > 1 ? ConstString(args[1].Expression) : null, e);
                ApplyInitializer(tv, (BaseObjectCreationExpressionSyntax)e);
                return tv;
            }
            case "score.new":
            {
                var oc = (BaseObjectCreationExpressionSyntax)e;
                var args = oc.ArgumentList!.Arguments;
                return NewObjective(ConstStr(args[0].Expression), args.Count > 1 ? ConstStr(args[1].Expression) : "dummy", args.Count > 2 ? ConstString(args[2].Expression) : null, e);
            }
        }
        var v = GetVar(e);
        return v is RegionVar or ClipVar or TeamVar ? v : throw new UnsupportedException(e, "Expected a Region, Clipboard, Team or Objective.");
    }

    void ApplyInitializer(Var v, BaseObjectCreationExpressionSyntax oc)
    {
        if (oc.Initializer == null) return;
        foreach (var x in oc.Initializer.Expressions.OfType<AssignmentExpressionSyntax>())
            SetObjProp(v, (IPropertySymbol)cur.Model.GetSymbolInfo(x.Left).Symbol!, x.Right);
    }

    static readonly System.Text.RegularExpressions.Regex TeamName = new("^[A-Za-z0-9_.+-]+$");

    TeamVar NewTeam(string name, string? display, SyntaxNode at)
    {
        if (!TeamName.IsMatch(name)) throw new UnsupportedException(at, $"Invalid team name '{name}' (letters, digits and _ . + - only).");
        if (!teams.TryGetValue(name, out var d) || d == null) teams[name] = display;
        return new TeamVar(name, false);
    }

    TeamVar NewObjective(string name, string criteria, string? display, SyntaxNode at)
    {
        if (!ObjectiveName.IsMatch(name)) throw new UnsupportedException(at, $"Invalid objective name '{name}'.");
        if (!objectives.TryGetValue(name, out var o) || o == ("dummy", null, null)) objectives[name] = (criteria, display, null);
        return new TeamVar(name, true);
    }

    /// team modify … for a Team property (cmds go to the load function for static teams).
    List<string> TeamModify(TeamVar t, IPropertySymbol p, ExpressionSyntax value, SemanticModel model)
    {
        var cv = model.GetConstantValue(value);
        string Bool() => cv.Value is bool b ? (b ? "true" : "false") : throw new UnsupportedException(value, $"{p.Name} must be a constant.");
        string Str() => cv.Value as string ?? throw new UnsupportedException(value, $"{p.Name} must be a string constant.");
        string Enum() => model.GetSymbolInfo(value).Symbol is IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } f ? McIdOf(f) ?? Snake(f.Name)
            : throw new UnsupportedException(value, $"{p.Name} must be a constant.");
        if (t.IsObjective)
            return p.Name == "DisplayName" ? new() { $"scoreboard objectives modify {t.Name} displayname {Text(Str())}" } : throw new UnsupportedException(value, $"Cannot set {p.Name}.");
        return new()
        {
            p.Name switch
            {
                "Color" => $"team modify {t.Name} color {Enum()}",
                "FriendlyFire" => $"team modify {t.Name} friendlyFire {Bool()}",
                "SeeFriendlyInvisibles" => $"team modify {t.Name} seeFriendlyInvisibles {Bool()}",
                "Prefix" => $"team modify {t.Name} prefix {Text(Str())}",
                "Suffix" => $"team modify {t.Name} suffix {Text(Str())}",
                "DisplayName" => $"team modify {t.Name} displayName {Text(Str())}",
                "NameTags" => $"team modify {t.Name} nametagVisibility {Enum()}",
                "DeathMessages" => $"team modify {t.Name} deathMessageVisibility {Enum()}",
                "Collision" => $"team modify {t.Name} collisionRule {Enum()}",
                _ => throw new UnsupportedException(value, $"Cannot set {p.Name}."),
            },
        };
    }

    void DeclareTeamStatic(SemanticModel model, IFieldSymbol f, VariableDeclaratorSyntax v)
    {
        if (v.Initializer?.Value is not BaseObjectCreationExpressionSyntax oc) { Error(v, $"A static {f.Type.Name} needs an initializer: = new {f.Type.Name}(\"name\");"); return; }
        try
        {
            var args = oc.ArgumentList!.Arguments;
            string Str(int i, string? def) => args.Count > i ? model.GetConstantValue(args[i].Expression).Value as string ?? throw new UnsupportedException(args[i], "A string constant is required.") : def!;
            TeamVar tv = f.Type.Name == "Team" ? NewTeam(Str(0, null), args.Count > 1 ? Str(1, null) : null, oc)
                : NewObjective(Str(0, null), Str(1, "dummy"), args.Count > 2 ? Str(2, null) : null, oc);
            teamStatics[f] = tv;
            foreach (var x in oc.Initializer?.Expressions.OfType<AssignmentExpressionSyntax>() ?? [])
                teamLoad.AddRange(TeamModify(tv, (IPropertySymbol)model.GetSymbolInfo(x.Left).Symbol!, x.Right, model));
        }
        catch (UnsupportedException e) { Error(e.Node, e.Message); }
    }

    void FinishTeams(List<string> load)
    {
        foreach (var (name, display) in teams)
            load.Add($"team add {name}{(display != null ? " " + Text(display) : "")}");
        load.AddRange(teamLoad);
    }

    Operand? EmitTeam(string name, InvocationExpressionSyntax inv)
    {
        var t = (TeamVar)EvalCtObj(((MemberAccessExpressionSyntax)inv.Expression).Expression);
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "team.join": Emit($"team join {t.Name} {AnySel(args[0].Expression)}"); return null;
            case "team.leave": Emit($"team leave {AnySel(args[0].Expression)}"); return null;
            case "team.has": return StoreT("success", $"if entity {SelWith(AnySel(args[0].Expression), $"team={t.Name}")}");
            case "team.empty": Emit($"team empty {t.Name}"); return null;
            case "team.remove": Emit($"team remove {t.Name}"); return null;
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    /// Objective[who] → the score holder "who objective".
    ScoreVar ScoreIndex(ElementAccessExpressionSyntax ea)
    {
        var t = (TeamVar)EvalCtObj(ea.Expression);
        return new ScoreVar($"{HolderOf(ea.ArgumentList.Arguments[0].Expression)} {t.Name}", VK.Int);
    }

    string HolderOf(ExpressionSyntax e)
    {
        var k = Kind(cur.Model.GetTypeInfo(e).Type);
        if (k == VK.String) { var s = ConstStr(e); if (s.Length == 0 || s.Any(char.IsWhiteSpace)) throw new UnsupportedException(e, $"Invalid score holder '{s}'."); return s; }
        return AnySel(e);
    }

    Operand? EmitScore(string name, InvocationExpressionSyntax inv)
    {
        var t = (TeamVar)EvalCtObj(((MemberAccessExpressionSyntax)inv.Expression).Expression);
        var args = inv.ArgumentList.Arguments;
        switch (name)
        {
            case "score.add":
            {
                string h = $"{HolderOf(args[0].Expression)} {t.Name}";
                var n = EvalNum(args[1].Expression, VK.Int);
                if (n is ConstOp c) { if (c.Value != 0) Emit($"scoreboard players {(c.Value > 0 ? "add" : "remove")} {h} {Math.Abs(c.Value)}"); }
                else Emit($"scoreboard players operation {h} += {Holder(n)}");
                return null;
            }
            case "score.reset": Emit($"scoreboard players reset {HolderOf(args[0].Expression)} {t.Name}"); return null;
            case "score.has": return StoreT("success", $"if score {SingleHolder(args[0].Expression)} {t.Name} matches -2147483648..");
            case "score.show":
            {
                string slot = args.Count > 0 ? Snake(EnumMember(args[0].Expression, ((IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!).Parameters[0].Type)) : "sidebar";
                Emit($"scoreboard objectives setdisplay {(slot == "below_name" ? prof.BelowNameSlot : slot)} {t.Name}");
                return null;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    string SingleHolder(ExpressionSyntax e) => Kind(cur.Model.GetTypeInfo(e).Type) == VK.String ? HolderOf(e) : SingleSel(e);

    /// Property reads of library objects (Team.Count, Team.Players, Entity.Exists, Interaction.Clicker …).
    Operand? WorldPropGet(IPropertySymbol p, MemberAccessExpressionSyntax? ma)
    {
        switch (Intrinsic(p))
        {
            case "team.count":
            {
                var t = (TeamVar)EvalCtObj(ma!.Expression);
                return StoreT("result", $"if entity @e[team={t.Name}]");
            }
            case "ex.exists": return StoreT("success", $"if entity {AnySel(ma!.Expression)}");
            case "rt.available": return RuntimeAvailable();
            case "nstr.length": return StrLength(StrPath(ma!.Expression));
            case "nstr.isempty": { var l = StrLength(StrPath(ma!.Expression)); return StoreT("success", $"if score {Holder(l)} matches 0"); }
            case "narr.length":
            {
                string t = Temp();
                Emit($"scoreboard players set {t} 0");
                Emit($"execute store result score {t} run data get storage {Storage} {GetNArr(ma!.Expression).Path}");
                return new ScoreOp(t);
            }
        }
        return null;
    }

    /// Entity references made by library properties (Team.Players, Interaction.Clicker / Attacker).
    RefVar? WorldRef(ExpressionSyntax e, ISymbol? sym)
    {
        switch (Intrinsic(sym))
        {
            case "team.players":
                return new RefVar($"@a[team={((TeamVar)EvalCtObj(((MemberAccessExpressionSyntax)e).Expression)).Name}]") { Type = ((IPropertySymbol)sym!).Type };
            case "interaction.clicker" or "interaction.attacker":
            {
                string sel = SingleSel(((MemberAccessExpressionSyntax)e).Expression);
                string c = NewRefTag();
                Emit($"tag @e[tag={c}] remove {c}");
                Emit($"execute as {sel} on {(Intrinsic(sym) == "interaction.clicker" ? "target" : "attacker")} run tag @s add {c}");
                return new RefVar($"@a[tag={c},limit=1]") { Tag = c, Type = ((IPropertySymbol)sym!).Type };
            }
        }
        return null;
    }

    // ================================================================ particles and sounds
    static string Rl(string id) => id.Contains(':') ? id : "minecraft:" + id;

    void EmitParticle(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        bool B(int i) => args.Count > i && cur.Model.GetConstantValue(Arg(i)).Value is true;
        double D(int i, double def) => args.Count > i ? ConstDouble(Arg(i), "Particle values") : def;
        int I(int i, int def) => args.Count > i ? ConstSlot(Arg(i)) : def;
        switch (name)
        {
            case "particle.spawn":
            {
                var pos = EvalPos(Arg(1));
                EmitCmd(At(pos, c => $"particle {Rl(ConstStr(Arg(0)))} {c} 0 0 0 {Fmt(D(3, 0))} {I(2, 1)}{(B(4) ? " force" : "")}"), inv, pos);
                return;
            }
            case "particle.spawnd":
            {
                var pos = EvalPos(Arg(1));
                EmitCmd(At(pos, c => $"particle {Rl(ConstStr(Arg(0)))} {c} {Fmt(D(2, 0))} {Fmt(D(3, 0))} {Fmt(D(4, 0))} {Fmt(D(6, 0))} {I(5, 1)}{(B(7) ? " force" : "")}"), inv, pos);
                return;
            }
            case "particle.dust":
            {
                int rgb = ConstSlot(Arg(0));
                var pos = EvalPos(Arg(1));
                static string F1(double v) { string s = Fmt(v); return s.Contains('.') ? s : s + ".0"; }
                string r = F1(((rgb >> 16) & 255) / 255.0), g = F1(((rgb >> 8) & 255) / 255.0), b = F1((rgb & 255) / 255.0), size = F1(D(2, 1));
                // an NBT list must not mix ints and doubles (before 1.21.5): every component gets a decimal point + f
                string dust = prof.ItemPredicates ? $"minecraft:dust{{color:[{r}f,{g}f,{b}f],scale:{size}f}}" : $"minecraft:dust {r} {g} {b} {size}";
                EmitCmd(At(pos, c => $"particle {dust} {c} 0 0 0 0 {I(3, 1)}"), inv, pos);
                return;
            }
            case "particle.line":
            {
                var from = EvalPos(Arg(1)); var to = EvalPos(Arg(2));
                var bx = RegionBoxD(from, to) ?? throw new UnsupportedException(inv, "Line needs both ends with the same kind of coordinates (both relative to the same position, or both absolute).");
                double step = D(3, 0.25);
                double len = Math.Sqrt(bx.d.Sum(x => x * x));
                int n = Math.Max(1, (int)Math.Round(len / step));
                if (n > 512) throw new UnsupportedException(inv, $"Line has too many points ({n}); use a larger step.");
                string id = Rl(ConstStr(Arg(0)));
                for (int k = 0; k <= n; k++)
                {
                    var p = bx.start.Offset(bx.d[0] * k / n, bx.d[1] * k / n, bx.d[2] * k / n);
                    Emit(At(p, c => $"particle {id} {c} 0 0 0 0 1"));
                }
                return;
            }
            case "particle.circle":
            {
                var center = EvalPos(Arg(1));
                if (center.Args.Length > 0) throw new UnsupportedException(inv, "Circle needs a position with constant coordinates.");
                double radius = ConstDouble(Arg(2), "The radius");
                int n = I(3, 16);
                if (n is < 1 or > 512) throw new UnsupportedException(inv, "Circle points must be 1..512.");
                string id = Rl(ConstStr(Arg(0)));
                for (int k = 0; k < n; k++)
                {
                    double a = 2 * Math.PI * k / n;
                    var p = center.Offset(Math.Round(radius * Math.Cos(a), 4), 0, Math.Round(radius * Math.Sin(a), 4));
                    Emit(At(p, c => $"particle {id} {c} 0 0 0 0 1"));
                }
                return;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    /// Start and delta of two positions with the same kind of coordinates.
    static (Pos start, double[] d)? RegionBoxD(Pos a, Pos b)
    {
        if (a.Args.Length > 0 || b.Args.Length > 0) return null;
        if (a.Prefix.Length > 0 && b.Prefix.Length > 0 && a.Prefix != b.Prefix) return null;
        string[] ca = [a.X, a.Y, a.Z], cb = [b.X, b.Y, b.Z];
        var d = new double[3];
        for (int i = 0; i < 3; i++)
        {
            bool ra = ca[i].StartsWith('~'), rb = cb[i].StartsWith('~');
            if (ra != rb || ca[i].StartsWith('^') || cb[i].StartsWith('^')) return null;
            double Num(string s, bool rel) => rel ? (s.Length > 1 ? double.Parse(s[1..], CultureInfo.InvariantCulture) : 0) : double.Parse(s, CultureInfo.InvariantCulture);
            d[i] = Num(cb[i], rb) - Num(ca[i], ra);
        }
        return (a.Prefix.Length > 0 ? a : a with { Prefix = b.Prefix }, d);
    }

    void EmitSound(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        double D(int i, double def) => args.Count > i ? ConstDouble(Arg(i), "Volume and pitch") : def;
        string Source(int i) => args.Count > i ? Snake(EnumMember(Arg(i), ((IMethodSymbol)cur.Model.GetSymbolInfo(inv).Symbol!).Parameters[i].Type)) : "master";
        switch (name)
        {
            case "sound.play":
            {
                var pos = EvalPos(Arg(1));
                EmitCmd(At(pos, c => $"playsound {Rl(ConstStr(Arg(0)))} {Source(4)} @a {c} {Fmt(D(2, 1))} {Fmt(D(3, 1))}"), inv, pos);
                return;
            }
            case "sound.playto":
                Emit($"execute as {AnySel(Arg(0))} at @s run playsound {Rl(ConstStr(Arg(1)))} {Source(4)} @s ~ ~ ~ {Fmt(D(2, 1))} {Fmt(D(3, 1))}");
                return;
            case "sound.stop":
            {
                string? s = args.Count > 1 ? ConstString(Arg(1)) : null;
                Emit(s == null ? $"stopsound {AnySel(Arg(0))}" : $"stopsound {AnySel(Arg(0))} * {Rl(s)}");
                return;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    // ================================================================ entity interactions
    Operand? EmitInteraction(string name, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax Arg(int i) => args[i].Expression;
        string sel = RecvSel(inv);
        string one = (sel.StartsWith("@e") || sel.StartsWith("@a")) && !sel.Contains("limit=") ? SelWith(sel, "limit=1") : sel;
        switch (name)
        {
            case "ex.addtag": Emit($"tag {sel} add {ConstStr(Arg(0))}"); return null;
            case "ex.removetag": Emit($"tag {sel} remove {ConstStr(Arg(0))}"); return null;
            case "ex.hastag": return StoreT("success", $"if entity {SelWith(sel, "tag=" + ConstStr(Arg(0)))}");
            case "ex.damage":
            {
                var op = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)cur.Model.GetOperation(inv)!;
                string type = "generic"; ExpressionSyntax? by = null;
                foreach (var a in op.Arguments)
                {
                    if (a.ArgumentKind == Microsoft.CodeAnalysis.Operations.ArgumentKind.DefaultValue) continue;
                    var x = (ExpressionSyntax)a.Value.Syntax;
                    if (a.Parameter!.Name == "type") type = ConstStr(x);
                    if (a.Parameter.Name == "attacker" && !x.IsKind(SyntaxKind.NullLiteralExpression)) by = x;
                }
                string tail = by != null ? $" by {SingleSel(by)}" : "";
                cur.Sink.AddRange(MacroCmd($"damage {one} $(a) {Rl(type)}{tail}", inv, ("a", EvalNum(Arg(0), VK.Fixed), VK.Fixed)));
                return null;
            }
            case "ex.effect":
            {
                string eff = ResolveId("effects", ConstStr(Arg(0)), Arg(0));
                var amp = args.Count > 2 ? EvalNum(Arg(2), VK.Int) : new ConstOp(0);
                bool hide = args.Count > 3 && cur.Model.GetConstantValue(Arg(3)).Value is true;
                cur.Sink.AddRange(MacroCmd($"effect give {sel} {eff} $(s) $(a){(hide ? " true" : "")}", inv, ("s", EvalNum(Arg(1), VK.Int), VK.Int), ("a", amp, VK.Int)));
                return null;
            }
            case "ex.cleareffects": Emit($"effect clear {sel}"); return null;
            case "ex.ride": Emit($"ride {one} mount {SingleSel(Arg(0))}"); return null;
            case "ex.dismount": Emit($"ride {one} dismount"); return null;
            case "ex.tpto": Emit($"tp {sel} {SingleSel(Arg(0))}"); return null;
            case "ex.lookat":
            {
                string facing;
                if (Kind(cur.Model.GetTypeInfo(Arg(0)).Type) == VK.Position)
                {
                    var p = EvalPos(Arg(0));
                    if (p.Args.Length > 0) throw new UnsupportedException(Arg(0), "LookAt needs a position with constant coordinates.");
                    if (p.IsAbsolute && p.Prefix.Length == 0) facing = $"facing {p.Coords}";
                    else if (p.Prefix.StartsWith("at ") && p.Coords == "~ ~ ~") facing = $"facing entity {p.Prefix[3..]} feet";
                    else throw new UnsupportedException(Arg(0), "LookAt needs an absolute position or an entity's position.");
                }
                else facing = $"facing entity {SingleSel(Arg(0))} eyes";
                Emit($"execute as {sel} at @s {facing} run tp @s ~ ~ ~ ~ ~");
                return null;
            }
            case "ex.motion":
            {
                var vals = Enumerable.Range(0, 3).Select(i => EvalNum(Arg(i), VK.Double)).ToArray();
                if (vals.All(v => v is ConstOp))
                    Emit($"data modify entity {one} Motion set value [{string.Join(",", vals.Select(v => FmtScaled(((ConstOp)v).Value, VK.Double) + "d"))}]");
                else
                    for (int i = 0; i < 3; i++)
                        Emit(vals[i] is ConstOp c
                            ? $"data modify entity {one} Motion[{i}] set value {FmtScaled(c.Value, VK.Double)}d"
                            : $"execute store result entity {one} Motion[{i}] double {InvScale(VK.Double)} run scoreboard players get {Holder(vals[i])}");
                return null;
            }
            case "ex.near":
            {
                double d = ConstDouble(Arg(1), "The distance");
                return StoreT("success", $"at {SingleSel(Arg(0))} if entity {SelWith(sel, $"distance=..{Fmt(d)}")}");
            }
            case "ex.isin": return StoreT("success", $"at {one} if dimension {DimOf(Arg(0))}");
            case "interaction.clicked" or "interaction.attacked":
            {
                string key = name == "interaction.clicked" ? "interaction" : "attack";
                var r = StoreT("success", $"if data entity {one} {key}");
                Emit($"execute if score {((ScoreOp)r).Holder} matches 1 run data remove entity {one} {key}");
                return r;
            }
        }
        throw new UnsupportedException(inv, $"Cannot use '{name}' here.");
    }

    // ================================================================ m.MountEntity / m.MountBlock
    /// "boss" (a tag), "@e[...]" (a selector) or a UUID → a selector for that entity.
    static string MountSelector(string target) =>
        target.StartsWith('@') ? target
        : target.Length == 36 && target.Count(c => c == '-') == 4 ? target
        : $"@e[tag={target},limit=1]";

    Var MountedEntity(ITypeSymbol type, string target, SyntaxNode at)
    {
        if (target.Length == 0 || target.Any(char.IsWhiteSpace) && !target.StartsWith('@')) throw new UnsupportedException(at, $"Invalid entity target '{target}'.");
        string sel = MountSelector(target);
        return Kind(type) == VK.Entity ? new EntVar(target) { Selector = sel, Spawned = true } : new RefVar(sel) { Type = type };
    }

    BlockVar MountedBlock(ITypeSymbol type, Pos pos, SyntaxNode at)
    {
        if (type.Name == "Block") throw new UnsupportedException(at, "Mount a typed block variable (e.g. OakDoor door;), so its states are known.");
        if (pos.Args.Length > 0) throw new UnsupportedException(at, "MountBlock needs a position with constant coordinates.");
        return new BlockVar(ResolveId("blocks", McIdOf(type) ?? Snake(type.Name), at)) { Mounted = pos };
    }

    /// m.MountEntity / m.MountBlock on static fields: collected before code generation.
    void CollectEntityMount(SemanticModel model, InvocationExpressionSyntax inv, string kind)
    {
        var args = inv.ArgumentList.Arguments;
        if (model.GetSymbolInfo(args[0].Expression).Symbol is not IFieldSymbol { IsStatic: true } f) return;   // locals: at the statement
        if (mountedStatics.ContainsKey(f)) throw new UnsupportedException(inv, $"{f.Name} is already mounted.");
        if (kind == "mount.entity")
        {
            string target = model.GetConstantValue(args[1].Expression).Value as string ?? throw new UnsupportedException(args[1], "MountEntity needs a string constant.");
            mountedStatics[f] = MountedEntity(f.Type, target, inv);
        }
        else mountedStatics[f] = MountedBlock(f.Type, ConstPos(model, args[1].Expression), inv);
    }

    /// A position written with constant coordinates (new Position(1, 64, 2)).
    static Pos ConstPos(SemanticModel model, ExpressionSyntax e)
    {
        if (e is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: { Count: 3 } a })
        {
            var v = a.Select(x => model.GetConstantValue(x.Expression)).ToList();
            if (v.All(c => c.HasValue)) return new Pos("", Fmt(System.Convert.ToDouble(v[0].Value, CultureInfo.InvariantCulture)), Fmt(System.Convert.ToDouble(v[1].Value, CultureInfo.InvariantCulture)), Fmt(System.Convert.ToDouble(v[2].Value, CultureInfo.InvariantCulture)));
        }
        throw new UnsupportedException(e, "A static mounted block needs a constant position: new Position(x, y, z).");
    }

    void EmitMountLocal(string kind, InvocationExpressionSyntax inv)
    {
        var args = inv.ArgumentList.Arguments;
        var sym = cur.Model.GetSymbolInfo(args[0].Expression).Symbol;
        if (sym is IFieldSymbol) return;   // static fields: bound at compile time (CollectEntityMount)
        if (sym is not ILocalSymbol local) throw new UnsupportedException(args[0], "Mount a local variable or a static field.");
        cur.Locals[local] = kind == "mount.entity"
            ? MountedEntity(local.Type, ConstStr(args[1].Expression), inv)
            : MountedBlock(local.Type, EvalPos(args[1].Expression), inv);
    }

    /// Setting a state of a mounted block. 1.20.2+: the other states are read from the world first, so only
    /// this one changes (a macro places the block). Older versions place it with the states set so far.
    void EmitMountedBlockSet(BlockVar bv, string state, string value)
    {
        var pos = bv.Mounted!;
        if (!prof.Macros) { Emit(At(pos, c => $"setblock {c} {bv}")); return; }
        int bit = 1 << McData.GroupIndex(prof);
        var others = McData.Blocks[bv.Id[10..]].Props.Where(p => p.Key != state && (p.Value.Mask & bit) != 0).OrderBy(p => p.Key).ToList();
        if (others.Count == 0) { Emit(At(pos, c => $"setblock {c} {bv.Id}[{state}={value}]")); return; }
        foreach (var (name, prop) in others)
            foreach (var v in prop.Values)
                Emit($"execute {Pre(pos.Prefix)}if block {pos.Coords} {bv.Id}[{name}={v}] run data modify storage {Storage} blk.{name} set value {Json.Str(v)}");
        string states = string.Join(",", others.Select(o => $"{o.Key}=$({o.Key})").Append($"{state}={value}"));
        string fn = MacroFn($"setblock {pos.Coords} {bv.Id}[{states}]");
        Emit($"execute {Pre(pos.Prefix)}if block {pos.Coords} {bv.Id} run function {fn} with storage {Storage} blk");
    }

    /// Reading a block state: a compile-time object knows its states; a mounted block checks the world.
    Operand BlockGet(IPropertySymbol p, MemberAccessExpressionSyntax ma)
    {
        var bv = EvalLibObject(ma.Expression, VK.Block) as BlockVar ?? throw new UnsupportedException(ma, "Expected a block.");
        var st = p.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "StateAttribute") ?? throw new UnsupportedException(ma, $"{p.Name} is not a block state.");
        string state = (string)st.ConstructorArguments[0].Value!;
        var values = ((string)st.ConstructorArguments[1].Value!).Split(',');
        int ValueOf(string v) => p.Type.SpecialType == SpecialType.System_Boolean ? (v == "true" ? 1 : 0)
            : p.Type.TypeKind == TypeKind.Enum ? System.Convert.ToInt32(p.Type.GetMembers().OfType<IFieldSymbol>().First(f => (McIdOf(f) ?? Snake(f.Name)) == v).ConstantValue)
            : int.Parse(v, CultureInfo.InvariantCulture);
        if (bv.Mounted == null)
            return bv.States.TryGetValue(state, out var known) ? new ConstOp(ValueOf(known))
                : throw new UnsupportedException(ma, $"{p.Name} was not set on this block object (mount it with m.MountBlock to read the world).");
        string t = Temp();
        Emit($"scoreboard players set {t} -1");
        foreach (var v in values)
            Emit($"execute {Pre(bv.Mounted.Prefix)}if block {bv.Mounted.Coords} {bv.Id}[{state}={v}] run scoreboard players set {t} {ValueOf(v)}");
        return new ScoreOp(t);
    }

    // ================================================================ other datapacks
    void RegisterExtern(IMethodSymbol sym, MethodDeclarationSyntax md)
    {
        string? path = sym.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "ExternalAttribute")?.ConstructorArguments[0].Value as string;
        var pack = sym.ContainingType.GetAttributes().FirstOrDefault(a => a.AttributeClass?.Name == "McsPackAttribute");
        if (pack != null)
        {
            string other = (string)pack.ConstructorArguments[0].Value!;
            string? cls = pack.ConstructorArguments.Length > 1 ? pack.ConstructorArguments[1].Value as string : null;
            string prefix = (cls != null ? Snake(cls) + "." : "") + Snake(sym.Name);
            if (path != null) prefix = path.Replace('/', '.');
            externs[sym] = new ExternFn($"{other}:{prefix.Replace('.', '/')}", other, prefix);
            foreach (var p in sym.Parameters)
                if (Kind(p.Type) is not (VK.Int or VK.Bool or VK.Fixed or VK.Double))
                    Error(md, $"Methods of another MC# pack take int/bool/Milli/Q16 parameters ({p.Name}).");
            return;
        }
        if (path == null) { Error(md.Identifier, "extern methods need [External(\"namespace:function\")] (or a [McsPack] class)."); return; }
        if (!System.Text.RegularExpressions.Regex.IsMatch(path, @"^#?[a-z0-9_.-]+:[a-z0-9_./-]+$")) { Error(md, $"Invalid function '{path}'."); return; }
        externs[sym] = new ExternFn(path, null, "");
        foreach (var p in sym.Parameters)
            if (Kind(p.Type) is not (VK.Int or VK.Bool or VK.Fixed or VK.Double or VK.NStr or VK.String))
                Error(md, $"External function parameters can be int, bool, Milli, Q16, string or NString ({p.Name}).");
    }

    Operand? EmitExternal(ExternFn ex, IMethodSymbol sym, InvocationExpressionSyntax inv, bool want)
    {
        var op = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)cur.Model.GetOperation(inv)!;
        var rk = Kind(sym.ReturnType);
        if (ex.McsNs != null)
        {
            string obj = $"mcs.{ex.McsNs}";
            foreach (var a in op.Arguments)
            {
                var p = a.Parameter!;
                var target = new ScoreVar($"${ex.Prefix}.{p.Name} {obj}", Kind(p.Type));
                if (a.ArgumentKind == Microsoft.CodeAnalysis.Operations.ArgumentKind.DefaultValue) SetHolder(target.Holder, new ConstOp(ScaleConst(p.ExplicitDefaultValue, target.Kind)));
                else AssignHolder(target, (ExpressionSyntax)a.Value.Syntax);
            }
            Emit($"function {ex.Path}");
            if (!want || sym.ReturnsVoid) return null;
            return new ScoreOp(ToTemp(new ScoreOp($"${ex.Prefix}#ret {obj}")));
        }
        string call = $"function {ex.Path}";
        if (op.Arguments.Length > 0)
        {
            if (!prof.Macros) throw new UnsupportedException(inv, "Passing arguments to another datapack's function needs function macros (1.20.2+).");
            if (ex.Path.StartsWith('#')) throw new UnsupportedException(inv, "Function tags cannot take arguments.");
            Emit($"data modify storage {Storage} ext set value {{}}");
            foreach (var a in op.Arguments)
            {
                var p = a.Parameter!;
                var k = Kind(p.Type);
                string path = $"ext.{p.Name}";
                if (a.ArgumentKind == Microsoft.CodeAnalysis.Operations.ArgumentKind.DefaultValue)
                {
                    Emit(p.ExplicitDefaultValue is string ds ? $"data modify storage {Storage} {path} set value {Json.Str(ds)}"
                        : StoreNumCmd(Storage, path, new ConstOp(ScaleConst(p.ExplicitDefaultValue, k)), k));
                    continue;
                }
                var x = (ExpressionSyntax)a.Value.Syntax;
                if (k is VK.String or VK.NStr)
                {
                    if (TryConstString(Unparen(x)) is string lit) Emit($"data modify storage {Storage} {path} set value {Json.Str(lit)}");
                    else Emit($"data modify storage {Storage} {path} set from storage {Storage} {StrPath(x)}");
                }
                else if (k == VK.Bool)
                {
                    var c = EvalCond(x);
                    Emit(c.Const is bool b ? $"data modify storage {Storage} {path} set value {(b ? 1 : 0)}" : $"execute store success storage {Storage} {path} int 1 {string.Join(" ", c.Parts)}");
                }
                else
                {
                    var v = EvalNum(x, k);
                    Emit(v is ConstOp c
                        ? $"data modify storage {Storage} {path} set value {(IsFrac(k) ? FmtScaled(c.Value, k) : c.Value.ToString(CultureInfo.InvariantCulture))}"
                        : $"execute store result storage {Storage} {path} {(k == VK.Fixed ? "float" : k == VK.Double ? "double" : "int")} {InvScale(k)} run scoreboard players get {Holder(v)}");
                }
            }
            call += $" with storage {Storage} ext";
        }
        if (!want || sym.ReturnsVoid) { Emit(call); return null; }
        var r = StoreT("result", "run " + call);
        return IsFrac(rk) ? Convert(r, VK.Int, rk, inv) : r;
    }
}
