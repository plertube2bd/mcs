# MC# Library Reference

MC# ships with five built-in libraries. All of them compile into the datapack itself, so the output pack never depends on other datapacks or mods.

| Library | How to load it | Contents |
|---|---|---|
| **Core** | Always available | `asm`, `m.Console`, `Position`, `GetWorldSpawn`, `SetWorldSpawn`, `SpawnEntity`, `m.SetBlock`/`FillBlock`/`IsBlock`, `[Load]`, `[Tick]` |
| **EntityLib** | `use EntityLib;` | `Entity`, `EntityType`, `m.SpawnEntityObject` |
| **BlockLib** | `use BlockLib;` | `Block` and one class per block (`OakStairs`, `Stone`, …), `m.SetBlockObject`/`FillBlockObject`/`IsBlockObject` |
| **PlayerLib** | `use PlayerLib;` | `Player` (control, inventory slots, effects, …), `Slot`, `GameMode` |
| **TuiLib** | `use TuiLib;` | Clickable chat `Button`, styled `Text`, `Tui.Menu`/`Print`/`Title` |
| **MathLib** | `use MathLib;` | `Math.Sin`/`Cos`/`Tan`, `Math.FloorDiv`/`FloorMod`, `Math.PI`/`Tau`/`Deg2Rad`/`Rad2Deg` |
| **ItemLib** | `use ItemLib;` | Item objects (`new Item(id) { Name = … }`), `m.GiveObject`, `[OnRightClickObject]` |
| **GuiLib** | `use GuiLib;` | `Gui.SetDisplay`/`ClearDisplay`, `Board` (sidebar / tab list / below name), `BossBar` |

### IDs are checked per version

Every block, item, entity, and effect ID is checked against the real data of each target version. That data was extracted from Mojang's data generator for every release from 1.20.1 to 26.3, and content behind experimental feature flags was excluded.

```
error MCS: [1.20.1, …, 1.21.2-1.21.3] The block 'pale_oak_log' does not exist in this version (exists in: 1.21.4 – 26.3). Use #if or change the target versions.
error MCS: Only vanilla (minecraft:) block IDs are allowed — the datapack must not depend on other packs or mods: 'create:cogwheel'
```

- The `minecraft:` prefix is optional (`"stone"` = `"minecraft:stone"`).
- **Renamed IDs are mapped automatically:** `grass` ↔ `short_grass`, `chain` ↔ `iron_chain`, `boat` ↔ `oak_boat`, `scute` ↔ `turtle_scute`, …

---

## Core

### `asm` — run commands directly

```csharp
asm say hello;                         // one command: no braces, trailing ; optional
asm {                                  // several commands: one command per line
    effect give @a minecraft:speed 10 1
    tellraw @a {"text":"Braces in JSON work too","color":"gold"}
}
```

- A leading `/` is stripped automatically.
- Lines starting with `#` are kept as mcfunction comments.
- **At the top level** (outside any method), `asm` runs on load, in source order, before `[Load]` methods and `Main` — handy for creating objectives that `m.Mount` uses.
- MC# variables can't be used inside `asm` (it is raw text).

### `m.Console.WriteLine(value)`

Shows a message to every player (`tellraw @a`).

```csharp
m.Console.WriteLine($"Score: {score}, speed: {speed}, alive: {alive}");
m.Console.WriteLine("Total " + count + " entities");
m.Console.WriteLine($"UUID: {string.Join("", x.uuid)}");
```

| Inserted value type | How it's shown |
|---|---|
| Constants | Calculated at compile time, shown as text |
| `int` | Score value |
| `float` | `-12.345` (3 decimal places) |
| `double` | `-12.34567` (5 decimal places) |
| `bool` | `True` / `False` |
| `Entity`, `Player` | Name (selector) |
| `Button`, `Text` | Clickable button / styled text |

### `Position`

A compile-time value: an execution context plus coordinates.

| Expression | Meaning |
|---|---|
| `GetWorldSpawn()` | World spawn point |
| `new Position(x, y, z)` | Absolute coordinates (constants) |
| `Position.Here` | Current execution position |
| `entity.Position`, `player.Position` | Position of that entity/player |
| `pos.Offset(dx, dy, dz)` | `pos` moved by an offset (constants) |

```csharp
Position origin = GetWorldSpawn().Offset(3, 0, 3);
```

### `SpawnEntity(string id, Position pos, int count = 1)`

Summons `count` entities by ID. `count` can be a runtime value.

```csharp
SpawnEntity("minecraft:zombie", GetWorldSpawn(), 10);
SpawnEntity("cow", origin.Offset(0, 1, 0), herdSize);
```

### Blocks: `m.SetBlock`, `m.FillBlock`, `m.IsBlock`

| Method | Command | Returns |
|---|---|---|
| `m.SetBlock(string block, Position pos)` | `setblock` | `bool` success |
| `m.FillBlock(string block, Position from, Position to)` | `fill` | `bool` success |
| `m.IsBlock(string block, Position pos)` | `execute if block` | `bool` |

```csharp
m.SetBlock("minecraft:gold_block", origin);
m.SetBlock("oak_stairs[facing=east,half=top]", origin.Offset(0, 1, 0));   // states are validated
m.FillBlock("stone", origin, origin.Offset(4, 0, 4));
if (m.IsBlock("gold_block", origin)) { ... }
```

For `FillBlock`, both corners must be relative to the same position (e.g. `p` and `p.Offset(...)`) or be absolute.

### `SetWorldSpawn(Position pos)`

Sets the world spawn and **moves the marker that `GetWorldSpawn()` uses**. You don't need to run `/reload`.

```csharp
SetWorldSpawn(new Position(100, 70, -20));
```

- The marker moves after the target chunk has loaded, which takes at least one tick. Code later in the same tick still sees the old position.
- On 1.21.9+ the spawn chunk is force-loaded because spawn chunks no longer exist.

> **Why it doesn't run `/reload`:** reloading re-runs `Main` and `[Load]`. If `Main` calls `SetWorldSpawn`, that is an endless reload loop, and every reload also reloads every other datapack. Moving the marker directly has the same effect without those problems. If you really need a reload, call `m.Reload()`, but never from `Main` or `[Load]`.

### `[Load]`, `[Tick]`

```csharp
[Load] public static void Init() { ... }    // #minecraft:load
[Tick] public static void Update() { ... }  // #minecraft:tick
```

`Main()` is the entry point and runs automatically after the `[Load]` methods.

### `m.Mount` — bind variables to existing scores

```csharp
asm scoreboard objectives add test dummy      // an objective made by you or another datapack

static int hello;
m.Mount(hello, "test.hello");                 // hello ⇄ score of "hello" in objective "test"

public class test : dummy {}                  // a scoreboard class: one objective
m.Mount(test, "test");                        // test.<name> ⇄ score of "<name>" in "test"

public static void Main()
{
    hello += 10;                              // scoreboard players add hello test 10
    test.hello = 0;                           // same score as `hello`
    test.coins = test.coins + 5;              // fields are declared automatically on first use
}
```

| Form | Meaning |
|---|---|
| `m.Mount(field, "objective.holder")` | Binds a `static int` / `bool` field. The string is split at the **last** `.` |
| `m.Mount(field, "objective", "holder")` | Same, for holder names that contain a `.` |
| `public class X : dummy {}` | Scoreboard class. Every `static int`/`bool` field is a holder (the field name) in its objective. Writing `X.name` declares `public static int name` automatically (declare `bool` fields yourself) |
| `m.Mount(X, "objective")` | Sets the class's objective. Without it the objective is `<ns>.<x>` |

- **Compile-time and global:** a mount is a declaration — it applies to every use of the field in the whole program, wherever it is written (at the top level or inside a method). It emits no command.
- **Objectives:** scoreboard classes create their objective on load (`scoreboard objectives add <obj> dummy`; harmless if it exists). A field mount (`"test.hello"`) does not create anything — the objective must exist.
- **Initial values:** a mounted field is never reset. `static int x = 5;` sets the score only if it has no value yet.
- Only `static int` / `bool` fields (scores are integers).

---

## EntityLib

```csharp
use EntityLib;

new Entity : Zombie x;                       // = Entity x = new Entity(EntityType.Zombie);
x.maxHealth = 100f;
x.Health = 100f;
if (!m.SpawnEntityObject(x, GetWorldSpawn())) return;
```

`EntityType` lists every vanilla entity type (`EntityType.CaveSpider` → `minecraft:cave_spider`). Types that don't exist in a target version are a compile error.

### `Entity` properties

| Property | Type | Read | Write | Minecraft mapping |
|---|---|:-:|:-:|---|
| `Health` | `float` | ✅ | ✅ | NBT `Health` |
| `maxHealth` / `MaxHealth` | `float` | ✅ | ✅ ¹ | Attribute `max_health` |
| `MovementSpeed` | `float` | ✅ | ✅ ¹ | Attribute `movement_speed` |
| `AttackDamage` | `float` | ✅ | ✅ ¹ | Attribute `attack_damage` |
| `Armor` | `float` | ✅ | ✅ ¹ | Attribute `armor` |
| `Fire`, `Air` | `int` | ✅ | ✅ | NBT |
| `NoAI`, `Silent`, `Glowing`, `Invulnerable`, `NoGravity`, `PersistenceRequired` | `bool` | ✅ | ✅ | NBT |
| `uuid` / `UUID` | `int[]` | Output only | ❌ | NBT `UUID` |
| `Position` | `Position` | ✅ | ❌ | Entity position |

- **Before spawning:** writes are applied right after the summon, in order. Set `maxHealth` before `Health`.
- **¹ Attributes:** constants work on every version. Runtime values need function macros, so they work on 1.20.2+ only (`#if MC_1_20_2_OR_GREATER`).

Methods: `Kill()`, `Teleport(Position pos)`. Entities can be passed to methods (`void Buff(Entity e)`).

---

## BlockLib

Every block in Minecraft has its own class, generated from the game data, **with its block states as typed properties**.

```csharp
use BlockLib;

new Block : OakStairs stairs;                // = OakStairs stairs = new OakStairs();
stairs.Facing = Facing.East;                 // enum
stairs.Half = Half.Top;
stairs.Waterlogged = false;                  // bool
m.SetBlockObject(stairs, origin);

Wheat crop = new Wheat { Age = 7 };          // object initializer, int state
var lamp = new RedstoneLamp { Lit = true };

Block glass = new Block("minecraft:glass");  // generic block from an ID string
m.FillBlockObject(glass, origin, origin.Offset(2, 0, 2));
if (m.IsBlockObject(stairs, origin)) { ... }
```

| Method | Description |
|---|---|
| `m.SetBlockObject(Block b, Position pos)` | Place the block with its states |
| `m.FillBlockObject(Block b, Position from, Position to)` | Fill an area |
| `m.IsBlockObject(Block b, Position pos)` | Is this block (with these states) at `pos`? |

- **Class names:** IDs in PascalCase (`oak_stairs` → `OakStairs`, `redstone_lamp` → `RedstoneLamp`).
- **States:** `bool`, `int`, or an enum named after the state (`Facing`, `Half`, `Shape`, `Axis`, …). A state that doesn't exist on the block is a C# type error. An invalid value, or a state that doesn't exist in a target version, is an MC# error.
- **Compile-time values:** a block object is compiled into the command, so states must be constants.
- **Version checks:** blocks that only exist in some target versions (e.g. `PaleOakLog`, 1.21.4+) produce an error naming the versions. Use `#if`.

---

## PlayerLib

```csharp
use PlayerLib;
```

### Choosing players

| Expression | Selector |
|---|---|
| `Player.All` | `@a` |
| `Player.Self` | `@s` (the player running the code, e.g. inside a button callback) |
| `Player.Nearest` | `@p` |
| `Player.Random` | `@r` |
| `Player.Named("Steve")` | `@a[name=Steve]` |
| `Player.WithTag("vip")` | `@a[tag=vip]` |

`Player` values can be stored in variables and passed to methods (`void Reward(Player p)`).

### Properties

| Property | Type | Read | Write |
|---|---|:-:|:-:|
| `Count` | `int` | ✅ | |
| `Exists` | `bool` | ✅ | |
| `Health` | `float` | ✅ | |
| `Food` | `int` | ✅ | |
| `Level` | `int` | ✅ | ✅ |
| `SelectedSlot` | `int` | ✅ | |
| `GameMode` | `GameMode` | | ✅ |
| `Position` | `Position` | ✅ | |

Properties that read a value use the first matching player.

### Methods

| Method | Description |
|---|---|
| `SendMessage(text)`, `ActionBar(text)`, `Title(title, subtitle?)` | Messages (interpolation, `Button`, `Text` allowed) |
| `PlaySound(id, volume, pitch)` | Plays a sound at the player |
| `Teleport(pos)`, `Kill()`, `SetSpawn(pos)` | |
| `AddTag(tag)`, `RemoveTag(tag)`, `HasTag(tag)` | |
| `AddLevels(n)`, `AddXp(n)` | |
| `Effect(id, seconds, amplifier = 0, hideParticles = false)`, `ClearEffect(id)`, `ClearEffects()` | |
| `Run(Method)` | Runs `Method` once **as each player** (`void Method()` or `void Method(Player p)`) |

### Inventory

Slot numbers: **0–8 hotbar, 9–35 main inventory**, `Slot.Head`, `Slot.Chest`, `Slot.Legs`, `Slot.Feet`, `Slot.Offhand`.

| Method | Description |
|---|---|
| `Give(item, count = 1)` | Give items |
| `ClearInventory()` / `ClearItem(item, count = -1)` | Remove everything / one item type |
| `CountItem(item)` → `int`, `HasItem(item)` → `bool` | Count / check an item type |
| `SetSlot(slot, item, count = 1)` | **Put an item into a specific slot** |
| `ClearSlot(slot)` | Empty a slot |
| `GetSlotCount(slot)` → `int` | Stack size in a slot |
| `IsSlot(slot, item)` → `bool`, `IsSlotEmpty(slot)` → `bool` | Check a slot |
| `CopySlot(from, to)`, `MoveSlot(from, to)`, `SwapSlots(a, b)` | Copy / move / **swap** slots |

```csharp
Player p = Player.All;
p.SetSlot(0, "minecraft:diamond_sword");
p.SetSlot(Slot.Head, "minecraft:iron_helmet");
p.SwapSlots(0, 8);
if (p.IsSlotEmpty(Slot.Offhand)) p.SetSlot(Slot.Offhand, "shield");
```

- **Runtime values:** counts, seconds, levels and `SetSlot`/`ClearSlot` slots (0–35) can be runtime values on 1.20.2+ (macros).
- **Version-specific commands:** slot checks use `execute if items` on 1.20.5+ and NBT on older versions. The compiler picks automatically.

---

## TuiLib

Chat UI without writing `tellraw` JSON yourself.

```csharp
use PlayerLib;
use TuiLib;

static int votes = 0;

public static void Main() {
    Button yes = new Button("[Yes]", OnYes) { Color = Color.Green, Hover = "Vote yes" };
    Button no = new Button("[No]", OnNo) { Color = Color.Red };
    Tui.Menu(Player.All, new Text("Do you like MC#?") { Bold = true, Color = Color.Gold }, yes, no);
}

public static void OnYes(Player p) {        // runs as the player who clicked
    votes++;
    p.SendMessage($"Thanks! votes = {votes}");
}

public static void OnNo() {
    Player.Self.ActionBar("Maybe next time");
}
```

| API | Description |
|---|---|
| `new Button(label, Method)` | Clickable button. `Method` is `void M()` or `void M(Player clicker)` |
| `new Text(content)` | Styled text |
| `.Color`, `.Bold`, `.Italic`, `.Underlined`, `.Hover` | Style (object initializer or assignment) |
| `Tui.Print(parts...)` / `Tui.PrintTo(player, parts...)` | One chat line made of strings, values, buttons, text |
| `Tui.Menu(player, title, buttons...)` | Title line + a row of buttons |
| `Tui.Title(player, title, subtitle?)`, `Tui.ActionBar(player, text)` | Title / action bar |
| `Tui.Clear(player)` | Clears the chat |

How it works:
- **Clicks:** buttons use `/trigger` (objective `mcs.<ns>.btn`), so **any player can click them without operator permission**. A tick function dispatches clicks to your method.
- **Version-specific components:** text component formats change between versions (`clickEvent` → `click_event` on 1.21.5+), and the compiler writes the right one.

---

## MathLib

```csharp
use MathLib;

double a = 30 * Math.Deg2Rad;
double s = Math.Sin(a);          // 0.5
float  c = Math.Cos(1.0f);       // float overload → float result
```

| Member | Description |
|---|---|
| `Math.Sin(x)`, `Math.Cos(x)`, `Math.Tan(x)` | Radians. `double` and `float` overloads |
| `Math.PI`, `Math.Tau`, `Math.Deg2Rad`, `Math.Rad2Deg` | Constants (folded at compile time) |
| `Math.FloorDiv(a, b)`, `Math.FloorMod(a, b)` | `int` division rounded down / remainder with the divisor's sign. One scoreboard command each (the `/` and `%` operators follow C# instead — see the [language guide](language.md#division-and-remainder)) |

How it works:
- **Number format:** `double` is Q15.16 fixed point (score = value × 65536, precision 1/65536, range about ±32768). `float` stays ×1000. Mixing them converts automatically; a `double` → `float` assignment needs a cast, as in C#.
- **Lookup table, not a Taylor series:** the angle is reduced modulo 2π, turned into a position on a 1024-step circle, and looked up in a 257-entry quarter-wave sine table (with symmetry for the other quadrants) plus linear interpolation between neighbours. The table is a binary tree of `execute if score … matches` functions, so it works on every version (no macros). Error is within about 2/65536 over the whole range.
- `Cos(x)` is the same table shifted by a quarter turn; `Tan(x)` is `Sin/Cos` with a Q15.16 division and saturates to ±32767.99 near ±π/2.
- Constant arguments are computed at compile time.
- `double * double` and `double / double` need more than 32 bits, so they call shared helpers (`<ns>:__math/dmul`, `__math/ddiv`) that split the operands into 16-bit halves (multiply) or do 16-step long division (divide). Multiplication is floored, division rounds toward zero (like C#), and both are exact; a division whose result does not fit saturates to ±2147483647/65536. Multiplying or dividing by a whole-number constant stays a single scoreboard operation.
- The helpers and the table are only emitted when used.

## ItemLib

```csharp
use ItemLib;
use PlayerLib;

static Item wand = new Item("carrot_on_a_stick") { Name = "Magic Wand", Lore = "Right-click me\nfor sparks", Glint = true };

[OnFirstJoin] static void Kit(Player p) { m.GiveObject(wand.Clone(), p); }   // every player gets a copy
[OnRightClickObject(nameof(wand))] static void Zap(Player p) { p.SendMessage("Zap!"); }
```

| Member | Description |
|---|---|
| `new Item(id)` | An item object. Properties (compile-time constants): `Name`, `Lore` (`\n` = new line), `Unbreakable`, `Glint` |
| `m.GiveObject(item, players, count = 1)` | `/give` — **consumes** the object (see [objects](language.md#objects-single-use)) |
| `item.Clone()` | A new object with the same contents **and the same identity** |
| `item.Dispose()` | Discards the object |
| `[OnRightClickObject(nameof(field))]` | Event: a player right-clicked while holding this item (or one of its clones) |

- **Identity:** every item object carries custom data `mcs_obj:"<ns>:<field>"`. Clones keep it, so `[OnRightClickObject]` reacts to all copies.
- **Versions:** 1.20.1–1.20.4 write NBT (`{display:{Name:…}}`), 1.20.5+ item components, and 1.21.5+ text components as SNBT. Detection uses `nbt={SelectedItem:…}` before 1.20.5 and `execute if items` after.
- **Right-click on `carrot_on_a_stick` / `warped_fungus_on_a_stick`** works on every version (use statistic). The used item is the main-hand one if it is that item type, otherwise the off-hand one — like the game.
- **Right-click on any other item** needs **1.20.5+** (with an older version in the range it is a compile error — build with `--mc 1.20.5-…`). The item gets a use action that never finishes (`food` component on 1.20.5–1.21.1, a silent `consumable` on 1.21.2+), and a `using_item` advancement runs the handler. Holding the button down counts as one click. Limits: on 1.20.5–1.21.1 the player shows the eating animation and sound while holding the button; items with their own right-click action (bow, shield, spawn eggs, …) keep it; block items still place blocks when aimed at a block.

```csharp
// @mc 1.20.5-26.3
static Item staff = new Item("stick") { Name = "Staff" };
[OnRightClickObject(nameof(staff))] static void Cast(Player p) { p.SendMessage("Cast!"); }
```
- Static items are recreated on every load (so `m.GiveObject(wand, …)` works once per load); local items each time their declaration runs.

---

## GuiLib

```csharp
use GuiLib;
use PlayerLib;

static Board stats = new Board("stats", "Stats");      // objective <ns>.board.stats
static BossBar timer = new BossBar("timer", "Time");   // boss bar <ns>:timer

public static void Main()
{
    stats.Set("Kills", 0);
    stats.Show();                                       // sidebar
    Gui.SetDisplay(DisplaySlot.BelowName, "my.objective");

    timer.Max = 60;
    timer.Color = BarColor.Red;
    timer.Show(Player.All);
    for (int t = 60; t > 0; t--)
    {
        timer.Value = t;
        timer.Title = $"Time: {t}";
        Wait(20);
    }
    timer.Hide();
}
```

### `Gui`

| Method | Command |
|---|---|
| `Gui.SetDisplay(DisplaySlot slot, string objective)` | `scoreboard objectives setdisplay <slot> <objective>` (any objective, e.g. a `[Score]` field's `<ns>.<field>`) |
| `Gui.SetDisplay(DisplaySlot slot, Board board)` | Shows a board in a slot |
| `Gui.ClearDisplay(DisplaySlot slot)` | Empties the slot |

`DisplaySlot`: `Sidebar`, `List` (tab list), `BelowName` (written as `belowName` on 1.20.1 and `below_name` on 1.20.2+).

### `Board`

A scoreboard display with its own dummy objective `<ns>.board.<id>`, created on load. Boards with the same id are the same board, so `new Board("stats")` in any method refers to it; a `static` field works too.

| Member | Description |
|---|---|
| `new Board(id, title = id)` | `id`: lowercase letters, digits, `_ . -`. `title`: string constant |
| `Title = "..."` | Changes the display name |
| `Show(slot = Sidebar)` / `Hide(slot = Sidebar)` | Show it in / clear a slot |
| `Set(string line, int value)` | Sets a sidebar line (a fake player name without spaces); the value may be a runtime value |
| `Set(Player players, int value)` | Sets players' scores (what `List` / `BelowName` show) |
| `Get(string line)` | Reads a line's value |
| `Remove(string line)` / `Clear()` | Removes one line / all lines |

### `BossBar`

A boss bar `<ns>:<id>`, created on load (the title is refreshed on every load). Same id → same bar.

| Member | Description |
|---|---|
| `new BossBar(id, title = id)` | `title`: string constant |
| `Value`, `Max` | Read/write. Runtime values are written with `execute store result bossbar` |
| `Title = ...` | Text; interpolated values (`$"Time: {t}"`) are filled in when set |
| `Color = BarColor.Red` | `Blue Green Pink Purple Red White Yellow` |
| `Style = BarStyle.Notched10` | `Progress Notched6 Notched10 Notched12 Notched20` |
| `Visible = ...` | Constant or runtime `bool` |
| `Show(Player players)` / `Hide()` | Sets who sees it / hides it from everyone |
| `Remove()` | Deletes the bar (it comes back on the next `/reload`) |

---

## Adding to a library

The built-in declarations are in [`src/Mcs.Compiler/Prelude.cs`](../src/Mcs.Compiler/Prelude.cs). The block classes and `EntityType` are generated from the data file `src/Mcs.Compiler/Data/mcdata.json` by `PreludeGen` in [`McData.cs`](../src/Mcs.Compiler/McData.cs).

| Attribute | Meaning |
|---|---|
| `[Nbt(path, type)]` | Reads/writes an entity NBT path |
| `[McAttribute(id)]` | Entity attribute (written without `generic.`) |
| `[McId(id)]` | Minecraft ID of a block class / enum value |
| `[State(name, values)]` | Block state property |
| `[Intrinsic(name)]` | Handled by the compiler (`Codegen.cs` / `Codegen.Libs.cs`) |

### Updating the game data

When a new Minecraft version comes out:

1. Run the data generator for each version: `java -DbundlerMainClass=net.minecraft.data.Main -jar server.jar --reports --output <gen>/<ver>/out`.
2. Find feature-flagged IDs with `tools/probe_ids.sh`. It needs a server folder with an accepted EULA.
3. Run `python3 tools/gen_mcdata.py <gen>`.
