# MC# Language Guide

This guide covers the language features beyond plain variables and control flow. Use them for larger projects; [`~/Desktop/mcw/mcs`](../../mcw/mcs) (a full strategy game) is written entirely with them.

## Entity classes

An entity class turns entities into objects with fields and methods.

```csharp
[Entity("minecraft:marker", "war.prov")]          // entity type + tag that identifies instances
public partial class Province : EntityObject
{
    [Score("war.terr")] public int Terr;           // one scoreboard objective per field
    [Score("war.pop", Display = "Population", Color = "white")] public int Pop;
    public int Owner;                              // default objective: <namespace>.owner

    public void Claim(int nation)                  // instance method: runs as the entity (@s)
    {
        Owner = nation;
        AddTag("owned");
    }
}

[Entity("minecraft:player")]
public partial class Nation : Player               // players: inherit Player to keep its methods
{
    [Score("war.act", Criteria = "trigger")] public int Act;
}
```

| Piece | Compiles to |
|---|---|
| Field read/write `p.Terr` | Score of the entity: `scoreboard players ... <selector> war.terr` |
| `[Score(name, Display, Color, Criteria)]` | Objective created automatically in the load function |
| Instance method call `p.Claim(1)` | `execute as <p> at @s run function ...` (`function ...` when `p` is already `@s`) |
| `this`, implicit fields | `@s` |
| Entity parameter `void F(Province p)` | Passed by tagging the entity |
| Entity return value `Province Find(...)` | Returned by tagging; `null` when nothing was found |
| `p == null` / `p != null` | `execute if entity <p>` |

Built-in members of every entity: `AddTag`, `RemoveTag`, `HasTag`, `Kill`, `Merge(snbt)`, `Position`, `Teleport(pos)`, `Run(() => ...)`.

`partial` classes can be split across files.

## Entity queries

```csharp
foreach (var p in Entities.All<Province>())                        // execute as @e[type=marker,tag=war.prov]
foreach (var u in Entities.All<Unit>("scores={war.st=0}", at: true)) // + selector filter, + at @s
var p = Entities.Nearest<Province>(new Position(x, 101, z), 0.5);   // closest within 0.5 blocks
var r = Entities.Random<Province>("tag=war.capc");
var n = Entities.Spawn<Province>(Position.Here);
int c = Entities.Count<Province>("tag=war.land");
bool any = Entities.Any<Unit>("tag=war.ba");
var me = Entities.Self<Nation>();                                    // @s
```

- **Loop body:** a `foreach` over entities runs the body once as each entity. `continue` and `return` work there; `break` does not.
- **Nesting:** loop variables, `this` and parameters stay valid in nested loops and lambdas. The compiler tags the entity when it needs to.
- **Query results:** `Nearest`, `Random`, `First` and `Spawn` capture their result with a tag, so it stays valid after the context changes.

## Compile-time code (replaces code generators)

```csharp
static readonly string[] Banner = { "", "red", "blue" };       // table (compile-time constants)
static readonly int[] Cost = { 0, 20, 15 };

[Inline] static void Sb(int dx, int dy, int dz, string block)  // expanded at each call
{
    asm $"setblock ~{dx} ~{dy} ~{dz} minecraft:{block}"
}

foreach (int y in Unroll.Range(0, 3)) Sb(0, y, 0, "stone");    // unrolled: 3 setblock lines
foreach (string c in Banner) asm $"team add t_{c}"            // arrays unroll too
```

- **Constant arguments:** `[Inline]` methods are not functions. Their body is copied into the caller. Constant arguments stay constants, so `if`, `switch`, `?:`, string comparisons and loops over them are resolved at compile time. Methods with `string` parameters must be `[Inline]`.
- **Return values:** an `[Inline]` method may return a value at its last statement, including a `string`, `Text` or `Button`.
- **Tables:** `static readonly` arrays are tables. A constant index (`Cost[2]`) becomes a constant. A runtime index (`Cost[b]`) reads from storage through a macro (1.20.2+), and works for strings in text and `asm $""` too.

## switch

```csharp
switch (bld)
{
    case 1: Res(); break;
    case 2: case 3: Mine(); break;
    default: None(); break;
}
```

- **Integers and enums:** `switch` works on these, with one `execute if score ... matches` per case.
- **Constant values:** a `switch` on a constant (e.g. in an `[Inline]` method) keeps only the matching case.
- **`break`:** allowed only as the last statement of a section.

## Commands with runtime values

| Feature | Description |
|---|---|
| `asm $"tp @s {x} 100 {z}"` | Interpolated command. Constants are substituted; runtime values go through a macro (1.20.2+) |
| `m.Check("block ~ ~ ~ minecraft:stone")` | Raw `execute if` condition, usable in `if`, `&&`, `!` |
| `int n = m.Result("data get entity @s Pos[0]")` | Command result (`execute store result`); interpolation allowed |
| `bool ok = m.Success("...")` | Command success |
| `m.Random(0, 99)` | `random value 0..99` |
| `m.Schedule(Method, 20)` / `m.ClearSchedule(Method)` | `schedule function` |
| `Exec.Run("anchored eyes positioned ^ ^ ^", Step)` | Any execute subcommands + a lambda or method |
| `Exec.At(pos, () => { ... })` | Run at a position (may have runtime coordinates) |
| `new Position(x, y, z)` | Coordinates may be runtime values (macro) |

## Chat buttons that run a command

`new Button("[Build]", "trigger war.act set 3")` creates a button that runs the given command. `new Button(label, Method)` creates one that calls an MC# method.

## Resources

Put non-function files (worldgen presets, block tags, …) in a folder and set `"resources"` in `mcs.json`. They are copied into the datapack unchanged.

```json
{ "namespace": "war", "sources": ["src"], "resources": "res", "minecraft": "1.21.9-26.3" }
```
