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

## Number types: Milli and Q16

MC# numbers are scores, so fractions are fixed point. The MC# names say exactly what you get:

| Type | Stored as | Precision | Range |
|---|---|---|---|
| `int` | score | 1 | ±2147483647 |
| `Milli` | score ×1000 | 0.001 | ±2147483.647 |
| `Q16` | score ×65536 (Q15.16) | 1/65536 ≈ 0.0000153 | ±32768 |

```csharp
Milli speed = 1.25f;      // literals keep C#'s f suffix
Q16 angle = 0.5;
Milli back = (Milli)(angle * 2);
```

`float` and `double` are the same two types under their old names. They still compile, but every use prints a warning, and they may be removed in any future version:

```
hello.mcs(3,8): warning MCS: 'float' is deprecated: use Milli. 'float' still works for now, but may be removed in any future version.
```

## Classes

Plain classes (and structs) work like in C#: fields, auto-properties, constructors, methods, `this`, inheritance, `virtual` / `abstract` / `override`, and a `ToString()` override for printing.

```csharp
class Vec {
    public int X, Y;
    public Vec(int x, int y) { X = x; Y = y; }
    public int Len2() => X * X + Y * Y;
    public void Add(Vec o) { X += o.X; Y += o.Y; }
    public override string ToString() => $"({X}, {Y})";
}

static Vec origin = new Vec(0, 0);         // static objects are built once, on the first load

public static void Main() {
    var a = new Vec(3, 4);
    Vec b = a;                              // the same object
    b.X = 10;
    a.Add(new Vec(1, 1));
    m.Console.WriteLine($"{a} {a.Len2()}");  // (11, 5) 146
}
```

How it compiles:

- **Fields are scores.** An object's fields are flattened into scores (`$main.a.X`), so reading or writing a field is one scoreboard command — no NBT, no macros, every version. NString / NArray fields live in storage.
- **Methods are functions** with a `this` frame: calling `a.Add(o)` copies `a` into the frame, runs `<ns>:vec/add`, and copies it back (only if the method can change the object). Each constructor is a function too.
- **References are fixed at compile time.** `Vec b = a;` makes `b` the same object, and passing an object to a method lets the method change it. A variable cannot point at a different object depending on a condition (`if (c) b = other;` is an error — assign the fields instead). There is no `null`.
- **Objects in fields are embedded.** `class Rect { public Vec Min; }` stores the `Vec`'s fields inside the `Rect`; assigning `r.Min = v` copies `v`'s fields. A class therefore cannot contain its own type (no linked lists).
- **Virtual calls are resolved at compile time** from the object's actual class. A method inherited from a base class gets one copy per class, so its calls reach that class's overrides.
- `ToString()` must be a single expression (`=> $"…"`); it is evaluated where the object is printed. Without it, objects print as `Vec(X: 3, Y: 4)`.
- Not supported: constructor chaining (`: this(…)` / `: base(…)`), `null`, computed properties (use methods), recursive methods that take or return objects.

## Generics

Generic methods and generic classes are compiled once for each set of type arguments (`Max<int>` → `<ns>:max_int`, `Box<Milli>` → `<ns>:box_milli/…`). Use the .NET generic-math interfaces for operators:

```csharp
using System.Numerics;

static T Max<T>(T a, T b) where T : INumber<T> => a > b ? a : b;

class Box<T> where T : INumber<T> {
    public T Value;
    public Box(T v) { Value = v; }
    public T Twice() => Value + Value;
}

m.Console.WriteLine($"{Max(3, 9)} {Max(1.5f, -2f)} {new Box<Q16>(0.25).Twice()}");   // 9 1.500 0.50000
```

- `T.Zero`, `T.One` and `default` work. Type arguments can be `int`, `bool`, `Milli`, `Q16`, classes, `NString` and `NArray<…>`.
- Recursive generic methods are not supported.

## Runtime strings and arrays: NString, NArray<T>

`string` is a compile-time constant. For text that changes at run time, use **`NString`**; for arrays whose length changes at run time, use **`NArray<T>`** (`T`: `int`, `bool`, `Milli`, `Q16`, `NString`). Both live in NBT storage (`<ns>:mcs`).

```csharp
NString name = "Steve";
int hp = 17;
NString line = $"{name} has {hp} HP";      // joined at run time
line += "!";
if (line.StartsWith("Steve")) m.Console.WriteLine(line);

var scores = new NArray<int> { 4, 9 };
scores.Add(hp);
scores[0] += 1;
int best = 0;
foreach (int s in scores) if (s > best) best = s;
m.Console.WriteLine($"{scores} best={best} n={scores.Length}");   // [5, 9, 17] best=17 n=3
```

| NString | | NArray<T> | |
|---|---|---|---|
| `"text"`, `$"… {x} …"`, `a + b`, `+=` | ① | `new NArray<T>()`, `new NArray<T>(n)`, `{ 1, 2 }` | |
| `==`, `!=` | | `a[i]`, `a[i] = v`, `a[i] += v` | ② |
| `Length`, `IsEmpty` | | `Length` / `Count` | |
| `Substring(start[, length])`, `s[i]` | ② | `Add(v)`, `Insert(i, v)` ②, `RemoveAt(i)` ②, `Pop()`, `Clear()` | |
| `StartsWith`, `EndsWith` | | `foreach` | |
| `IndexOf`, `Contains`, `ToInt()` | ① | printing: `{a}` → `[1, 2, 3]` | |

① needs function macros (1.20.2+). ② needs 1.20.2+ only when the index is a runtime value (constant indexes work everywhere).

- `NString` is a value: assigning copies the text. `NArray` is a reference like objects (`b = a` is the same array; passing it to a method lets the method change it).
- Joining strings goes through a macro, so a string that contains `"` or `\` cannot be joined.
- Out-of-range reads give 0 / leave the result unchanged; Minecraft cannot throw.
- `NArray<Milli>` stores the raw ×1000 integers (exact), so printing an `NArray<Milli>` shows those integers.

## Arrays

`int[]`, `bool[]`, `Milli[]` and `Q16[]` are writable arrays, as static fields or local variables. The length is fixed at compile time.

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
- `Milli` / `Q16` division also rounds toward zero (to the last fixed-point digit), and `%` works the same way. `(int)x` on a `Milli`/`Q16` truncates toward zero, like C#.
- Division by zero cannot throw: the value is left unchanged.

### Minecraft division (faster)

C# division needs extra commands for the sign. If floor semantics are fine, let `/` and `%` compile to Minecraft's own single scoreboard operation — they then round toward −∞ and `%` takes the divisor's sign (`-7 / 2 = -4`, `-7 % 2 = 1`):

```csharp
[MinecraftDivision] static int Cell(int x) => x / 16;      // one method (or a whole class)
[CSharpDivision] static int Exact(int x) => x / 16;        // opt out again
```

For the whole project, set `"division": "minecraft"` in `mcs.json` (or `--division minecraft`). `Q16 / Q16` keeps its exact helper either way.

## Recursion

Methods can call themselves, directly or through other methods:

```csharp
static int Fact(int n) { if (n <= 1) return 1; return n * Fact(n - 1); }
static bool IsEven(int n) => n == 0 ? true : IsOdd(n - 1);
static bool IsOdd(int n) => n == 0 ? false : IsEven(n - 1);
```

- **How:** a method's variables live in fixed scores, so the compiler finds the cycles in the call graph and, for the calls inside a cycle only, saves the caller's scores (parameters, locals, temporaries) on a storage stack (`<ns>:mcs stack`) before the call and restores them after it. Other calls are unchanged.
- **Cost:** about two commands per score of the method on every recursive call (save and restore). Everything runs in one tick, so deep recursion can hit `maxCommandChainLength` (65536 commands by default) — prefer loops for large inputs.
- Recursive methods cannot use `Wait()`. Entity parameters (tags) are not saved, so don't rely on an entity parameter after a recursive call.

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
- **`[OnRightClick(item)]`** detects a right-click only for `carrot_on_a_stick` and `warped_fungus_on_a_stick`; other items count when they are actually used (eaten, thrown, placed, …). Item IDs are checked per version. For a right-click on any item, use an [item object](libraries.md#itemlib) with `[OnRightClickObject]` (1.20.5+).
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

## Execute chains

`Execute` builds one `/execute` command fluently. Write the whole chain in one statement and end it with `.Run(...)` (a lambda or a parameterless static method):

```csharp
Execute.AsPlayerObject(Player.All)
    .At(Player.Self)
    .UnlessBlock("air", Position.Here.Offset(0, -1, 0))
    .Run(() => { Player.Self.SendMessage("on the ground"); });
// execute as @a at @s unless block ~ ~-1 ~ minecraft:air run tellraw @s ...
```

| Step | Subcommand |
|---|---|
| `As(target)` / `AsPlayerObject(players)` | `as` — inside, each target is `@s` (`Player.Self`) |
| `At(target)` / `At(pos)` / `Positioned(pos)` | `at` / `positioned` (positions may have runtime coordinates) |
| `In(Dimension.Nether)`, `Rotated(yaw, pitch)`, `RotatedAs(target)`, `Facing(pos)`, `Facing(target)`, `AnchoredEyes()`, `Align("xz")` | the matching subcommand |
| `IfBlock(id, pos)` / `UnlessBlock(id, pos)` (`IfBlockObject` / `UnlessBlockObject` for Block objects) | `if/unless block` |
| `IfEntity(target)` / `UnlessEntity(target)` | `if/unless entity` |
| `If(cond)` / `Unless(cond)` | any MC# bool, checked at that point of the chain (once per `As` target when it comes after `As`) |
| `Sub("raw subcommands")` | written as is |

Targets are an Entity, an entity reference (`Player.All`, an entity class object) or a selector string. Entity references from outside the chain keep pointing at the same entity inside `Run`. When a step needs commands of its own (a runtime `If`, a block test at an entity's position, an entity query), the rest of the chain moves into a sub-function so those commands run in the right context.

## Chat buttons that run a command

`new Button("[Build]", "trigger war.act set 3")` creates a button that runs the given command. `new Button(label, Method)` creates one that calls an MC# method.

## Calling other datapacks

MC# output never needs another pack — but you can choose to call one. Then your pack depends on it (your choice, not the compiler's).

```csharp
[External("other:api/ping")] static extern void Ping();                 // function other:api/ping
[External("other:api/add")] static extern int Add(int a, int b);        // function other:api/add {a:…, b:…}
[External("#other:hooks")] static extern void Hooks();                  // a function tag

[McsPack("lib")]                       // a pack compiled with MC# (namespace lib)
static class Lib {
    public static extern int Square(int x);   // sets $square.x mcs.lib, runs lib:square, reads $square#ret mcs.lib
}

int s = Add(2, 3) + Lib.Square(4);
m.Call("other:setup");                 // any function or #tag, by name
```

- `[External]` parameters become macro arguments (`$(a)`, 1.20.2+): `int`, `bool`, `Milli`, `Q16`, `string`, `NString`. An `int` / `bool` result is the function's `/return` value.
- `[McsPack("ns", "Class")]` calls methods declared in class `Class` of the other pack (`ns:class/method`). Its parameters and results use that pack's scores, so they must be `int`, `bool`, `Milli` or `Q16`.
- The other pack must be loaded first (its objectives exist only after its own load function ran).

## Resources

Put non-function files (worldgen presets, block tags, …) in a folder and set `"resources"` in `mcs.json`. They are copied into the datapack unchanged.

```json
{ "namespace": "war", "sources": ["src"], "resources": "res", "minecraft": "1.21.9-26.3" }
```
