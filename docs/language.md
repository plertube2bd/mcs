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

## Arrays

`int[]`, `bool[]`, `float[]` and `double[]` are writable arrays, as static fields or local variables. The length is fixed at compile time.

```csharp
static int[] grid = new int[20];          // zeros
static int[] primes = { 2, 3, 5, 7 };

public static void Main()
{
    for (int i = 0; i < grid.Length; i++) grid[i] = i * i;
    grid[k] += 100;
    grid[k]++;

    int[] a = { 1, 2, 3 };                // locals: elements may be runtime values
    int[] b = a;                          // the same array (reference, as in C#)
    b[0] = 9;                             // a[0] is 9 too

    foreach (int p in primes) { if (p > 5) break; total += p; }
    m.Console.WriteLine($"{a} {string.Join("-", primes)}");   // [9, 2, 3] 2-3-5-7
}
```

- **Storage:** each element is a score (`$static.grid[3]`, `$main.a[0]`), so you can inspect it with `/scoreboard players get`.
- **Constant index** (`grid[3]`, or anything that folds to a constant): a direct score access — no extra cost. Out of range is a compile error.
- **Runtime index** (`grid[i]`): a lookup through a binary tree of `execute if score … matches` functions (about log₂(n)+8 commands; no macros, so it works on 1.20.1). Out of range: reads give `0`, writes are ignored.
- **Supported:** `a[i]`, `=`, `+= -= *= /= %=`, `++`/`--`, `a.Length`, `foreach` (with `break`/`continue`), printing (`{a}` → `[1, 2, 3]`, `string.Join(sep, a)`).
- **Static arrays** are initialized once and keep their values across `/reload`, like other static fields. Their elements must be constants. **Local arrays** are created each time the declaration runs.
- **Length:** a compile-time constant, at most 16384. `new int[n]` with a runtime `n` is not supported.
- **Array variables** are bound at compile time: reassigning a local (`a = b;`, `a = new int[3];`) should not happen inside a branch. Static array fields cannot be reassigned.
- **Parameters:** methods can take arrays only if they are `[Inline]` (`[Inline] static int Sum(int[] xs)`). Methods cannot return arrays. Multi-dimensional and jagged arrays are not supported.
- `static readonly` arrays stay compile-time **tables** (below) — use them for constant data, including strings.

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

## Division and remainder

`/` and `%` follow C#: the quotient rounds toward zero and the remainder has the sign of the dividend.

```csharp
int a = -7;
m.Console.WriteLine($"{a / 2} {a % 2}");                          // -3 -1  (as in C#)
m.Console.WriteLine($"{Math.FloorDiv(a, 2)} {Math.FloorMod(a, 2)}"); // -4 1  (use MathLib;)
```

- Scoreboard `/=` and `%=` round toward negative infinity, so the compiler adds sign handling: dividing by a constant takes 4 commands, a runtime divisor calls a shared helper (`<ns>:__math/idiv` / `irem`).
- `Math.FloorDiv` / `Math.FloorMod` (MathLib) are the raw single-command floor versions — use them in hot code when the values are known to be non-negative, or when you want floor semantics.
- `float` / `double` division also rounds toward zero (to the last fixed-point digit), and `%` works the same way. `(int)x` on a `float`/`double` truncates toward zero, like C#.
- Division by zero cannot throw: the value is left unchanged.

## Wait()

`Wait()` pauses the current method for one tick; `Wait(n)` for `n` ticks. The rest of the method runs later through `/schedule`. **Only this method waits** — the caller continues immediately.

```csharp
static void Countdown()
{
    for (int i = 3; i > 0; i--)
    {
        m.Console.WriteLine($"{i}...");
        Wait(20);                     // one second
    }
    m.Console.WriteLine("Go!");
}

public static void Main()
{
    Countdown();                      // returns at the first Wait
    m.Console.WriteLine("printed before 2...");
}
```

- **Where:** as a statement in the method body, or inside `if`/`else`, `while`, `for`, `do-while` (nested any depth). Each `Wait` splits the method: the code after it becomes a continuation function.
- **Early `return`** works. `break` / `continue` cannot be used in a loop that contains `Wait()`; use the loop condition or a flag.
- **Not allowed** in `switch`, `foreach`, lambdas, `[Inline]` methods, entity instance methods, or methods that return a value.
- **Context:** scheduled code runs as the server at the world spawn, so `@s`, `Player.Self` and relative positions (`~ ~ ~`) do not carry over. Entity variables (tags), `static` fields and local numbers do.
- **Locals are shared:** local variables are per-method scores, so calling the method again while it is still waiting overwrites them (both runs continue with the new values).

## Objects: single use

Entity objects (`new Entity : Zombie x;`), block objects (`new Block : Stone s;`) and item objects (`new Item(…)`) stand for **one** thing in the world. These calls **consume** an object:

| Consumes | |
|---|---|
| `m.SpawnEntityObject(x, pos)` | the entity is summoned (you can still change `x`'s properties afterwards — it is now that entity) |
| `m.SetBlockObject(s, pos)`, `m.FillBlockObject(s, a, b)` | the block is placed |
| `m.GiveObject(item, players)` | the item is given |
| `x.Dispose()` | the object is discarded — after this, any use of `x` is an error |

```csharp
new Entity : Pig pig;
m.SpawnEntityObject(pig, here);
m.SpawnEntityObject(pig, there);           // error: 'pig' was already used (line 2)
m.SpawnEntityObject(pig.Clone(), there);   // ok: a new object with the same type and properties

if (ok) m.GiveObject(wand, p); else wand.Dispose();   // ok: one use per branch
```

- **Compile time:** a second consumption in the same method is an error. `if`/`else` and `switch` branches are checked separately.
- **Run time:** where one consumption can run several times — inside a loop that does not create the object, or a static object used from several methods — a flag score lets only the first run through. Later attempts do nothing and print `[MC#] 'x' was already used …`. Straight-line code gets no flag and no extra commands.
- **`Clone()`** makes another object: entities keep their type and pre-spawn properties, blocks their states, items their contents and identity.
- Pass `new …` or `x.Clone()` directly to use a temporary object once.

## Player events

Put an event attribute on a static method. It runs **as the player and at the player** (`@s`), once per player the event happened to.

```csharp
use PlayerLib;

[OnFirstJoin] static void Welcome(Player p) { p.Give("minecraft:bread", 5); }
[OnJoin]      static void Hello(Player p)   { p.SendMessage("Welcome back!"); }
[OnDeath]     static void Died()            { deaths++; }
[OnRespawn]   static void Back(Player p)    { p.Effect("minecraft:regeneration", 5); }
[OnKill]      static void Kills(Player p, int count) { p.SendMessage($"+{count} kills"); }
[OnRightClick("carrot_on_a_stick")] static void Wand(Player p) { asm summon minecraft:lightning_bolt; }
[OnTrigger("menu")] static void Menu(Player p, int choice) { /* /trigger menu set 2 */ }
[OnStat("minecraft.custom:minecraft.jump")] static void Jumped(int times) { }
```

| Attribute | When | `int` parameter |
|---|---|---|
| `[OnJoin]` | A player joins (every login, including the first) | — |
| `[OnFirstJoin]` | A player joins for the first time | — |
| `[OnDeath]` | A player died (runs at the death position, on the death screen) | deaths |
| `[OnRespawn]` | A player respawned after dying | — |
| `[OnKill]` | A player killed a mob or player | kills |
| `[OnRightClick(item)]` | A player used an item (default `carrot_on_a_stick`) | uses |
| `[OnTrigger("name")]` | A player ran `/trigger name` — **no operator permission needed** | the value (`1` for a plain `/trigger`) |
| `[OnStat("criterion")]` | A counter statistic went up, e.g. `minecraft.mined:minecraft.stone` | how much |
| `[OnRightClickObject(nameof(wand))]` | A player right-clicked holding this [item object](libraries.md#itemlib) (or a clone) | uses |

- **Parameters** are optional: a `Player` (that player) and an `int`. Several methods may share an event; they run in source order.
- **How it works:** one tick function (`<ns>:__events/tick`) checks the event objectives (`<ns>.ev.*`, created on load), runs the handlers with `execute as @a[scores={…=1..}] at @s` and resets the counters. Events are noticed within the same tick.
- **Right-click** detection only works for `carrot_on_a_stick` and `warped_fungus_on_a_stick`. Other items count when they are actually used (eaten, thrown, placed, …). Item IDs are checked per version.
- **Join:** players online when the pack is first installed count as first-time joiners. There is no "leave" event (nothing can run for a player who left).
- **Respawn** uses the fact that `@e` only selects living entities, so it also works with `doImmediateRespawn`.
- **Triggers:** every player is re-enabled each tick. The objective name is what players type, so pick a unique one.

## Commands with runtime values

| Feature | Description |
|---|---|
| `asm $"tp @s {x} 100 {z}"` | Interpolated command. Constants are substituted; runtime values go through a macro (1.20.2+) |
| `m.Check("block ~ ~ ~ minecraft:stone")` | Raw `execute if` condition, usable in `if`, `&&`, `!` |
| `int n = m.Result("data get entity @s Pos[0]")` | Command result (`execute store result`); interpolation allowed |
| `bool ok = m.Success("...")` | Command success |
| `m.Random(0, 99)` | `random value 0..99` |
| `m.Schedule(Method, 20)` / `m.ClearSchedule(Method)` | `schedule function` (see also [`Wait()`](#wait)) |
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
