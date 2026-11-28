# MC# Library Reference

MC# ships with built-in libraries. All of them compile into the datapack itself, so the output pack never depends on other datapacks or mods.

> This page shows how to use them. Every type, member, parameter default and enum value is in the [API reference](api.md), generated from the compiler. The exact rules are in the [specification](ko/spec.md) (Korean).

| Library | How to load it | Contents |
|---|---|---|
| **Core** | Always available | `asm`, `m.Console`, `Position`, `GetWorldSpawn`, `SetWorldSpawn`, `SpawnEntity`, `m.SetBlock`/`FillBlock`/`IsBlock`, `[Load]`, `[Tick]`, `m.Mount`/`MountEntity`/`MountBlock`, `m.Call`, `NString`, `NArray<T>` |
| **EntityLib** | `use EntityLib;` | `Entity`, `EntityType`, `m.SpawnEntityObject`, interactions (`Damage`, `Ride`, `LookAt`, …), `Interaction` |
| **DisplayLib** | `use DisplayLib;` | `BlockDisplay`, `ItemDisplay`, `TextDisplay` (display entities: scale, rotation, animation, runtime text) |
| **BlockLib** | `use BlockLib;` | `Block` and one class per block (`OakStairs`, `Stone`, …), `m.SetBlockObject`/`FillBlockObject`/`IsBlockObject`, `Region` (`Clone`/`Move`/`Stack`/`Copy`/`Cut`/`Paste`) |
| **PlayerLib** | `use PlayerLib;` | `Player` (control, inventory slots, effects, …), `Slot`, `GameMode` |
| **TuiLib** | `use TuiLib;` | Clickable chat `Button`, styled `Text`, `Tui.Menu`/`Print`/`Title` |
| **MathLib** | `use MathLib;` | `Math.Sin`/`Cos`/`Tan`, `Math.FloorDiv`/`FloorMod`, `Math.PI`/`Tau`/`Deg2Rad`/`Rad2Deg` |
| **ItemLib** | `use ItemLib;` | Item objects (`new Item(id) { Name = … }`), `m.GiveObject`, `[OnRightClickObject]` |
| **GuiLib** | `use GuiLib;` | `Gui.SetDisplay`/`ClearDisplay`, `Board` (sidebar / tab list / below name), `BossBar` |
| **WorldLib** | `use WorldLib;` | `World.Time()`/`SetTime`, `World.Weather`, `Difficulty`, `GameRule`, world border, `Dimension`, `World.Run(dimension, …)` |
| **ParticleLib** | `use ParticleLib;` | `Particle.Spawn`, `Dust`, `Line`, `Circle` |
| **SoundLib** | `use SoundLib;` | `Sound.Play`, `PlayTo`, `Stop` |
| **NbtLib** | `use NbtLib;` | `Storage.Get`/`Set`/`Has`/`Remove`/…, `Nbt.Get`/`Set`/`Merge` for entities and block entities |
| **TeamLib** | `use TeamLib;` | `Team` (colors, prefixes, members), `Objective` (`kills[player] += 1`) |

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
| `Milli` | `-12.345` (3 decimal places) |
| `Q16` | `-12.34567` (5 decimal places) |
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

### `m.MountEntity` / `m.MountBlock` — bind variables to existing entities and blocks

Like `m.Mount` for scores, these bind a variable to something that already exists in the world (compile-time, no command):

```csharp
use EntityLib;
use BlockLib;

static Entity boss;
m.MountEntity(boss, "boss");                           // the entity tagged "boss"
static OakDoor door;
m.MountBlock(door, new Position(5, 64, 5));            // the oak door at 5 64 5

public static void Main() {
    boss.Glowing = true;                               // data modify entity @e[tag=boss,limit=1] Glowing …
    door.Open = !door.Open;                            // reads the world, sets only `open`
    if (door.Facing == Facing.North) { … }
}
```

- **Entity targets:** a tag (`"boss"`), a selector (`"@e[type=minecraft:zombie,limit=1]"`) or a UUID. The variable can be an `Entity` or an entity class / `Player` reference. Locals can be mounted too (`Entity e; m.MountEntity(e, "x");`).
- **Blocks:** the variable needs a typed block class (`OakDoor`, not `Block`). Reading a state checks the world (`execute if block …`, one command per possible value). Setting a state reads the other states first and re-places the block with only that state changed (1.20.2+; on 1.20.1 the other states reset). `setblock` replaces block entity data (e.g. chest contents).

### `m.Call(string function)`

Runs any function or `#tag` by name and returns its result. See [calling other datapacks](language.md#calling-other-datapacks) for typed declarations.

### Runtime strings and arrays

`NString` and `NArray<T>` are always available — see the [language guide](language.md#runtime-strings-and-arrays-nstring-narrayt).

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
| `Health` | `Milli` | ✅ | ✅ | NBT `Health` |
| `maxHealth` / `MaxHealth` | `Milli` | ✅ | ✅ ¹ | Attribute `max_health` |
| `MovementSpeed` | `Milli` | ✅ | ✅ ¹ | Attribute `movement_speed` |
| `AttackDamage` | `Milli` | ✅ | ✅ ¹ | Attribute `attack_damage` |
| `Armor` | `Milli` | ✅ | ✅ ¹ | Attribute `armor` |
| `Fire`, `Air` | `int` | ✅ | ✅ | NBT |
| `NoAI`, `Silent`, `Glowing`, `Invulnerable`, `NoGravity`, `PersistenceRequired` | `bool` | ✅ | ✅ | NBT |
| `uuid` / `UUID` | `int[]` | Output only | ❌ | NBT `UUID` |
| `Position` | `Position` | ✅ | ❌ | Entity position |

- **Before spawning:** writes are applied right after the summon, in order. Set `maxHealth` before `Health`.
- **¹ Attributes:** constants work on every version. Runtime values need function macros, so they work on 1.20.2+ only (`#if MC_1_20_2_OR_GREATER`).

Methods: `Kill()`, `Teleport(Position pos)`. Entities can be passed to methods (`void Buff(Entity e)`).

### Interactions

These work on `Entity` objects and on entity class / `Player` references (`EntityObject`). An argument named `other` / `target` / `vehicle` / `attacker` takes either kind.

| Method | Command |
|---|---|
| `Damage(Milli amount, string type = "generic", object attacker = null)` | `damage <e> <amount> <type> [by <attacker>]` (runtime amounts: 1.20.2+) |
| `Ride(object vehicle)` / `Dismount()` | `ride <e> mount <vehicle>` / `ride <e> dismount` |
| `TeleportTo(object target)` | `tp <e> <target>` |
| `LookAt(Position pos)` / `LookAt(object target)` | turns to face a position or another entity's eyes |
| `SetMotion(x, y, z)` | sets `Motion` (runtime values allowed) |
| `IsNear(object other, Q16 distance)` | is `other` within `distance` blocks? |
| `IsIn(Dimension d)` | is the entity in this dimension (WorldLib)? |
| `Effect(id, seconds, amplifier = 0, hide = false)`, `ClearEffects()` | `effect give` / `effect clear` (Entity) |
| `AddTag`, `RemoveTag`, `HasTag` | tags (Entity; entity classes already had them) |
| `Exists` | is the entity still there? |

### `Interaction`

An interaction entity is an invisible hitbox that remembers clicks (1.19.4+):

```csharp
var box = new Interaction { Width = 2f, Height = 1f, Response = true };
m.SpawnEntityObject(box, pos);
...
if (box.Clicked()) box.Clicker.SendMessage("Hi!");    // right-click since the last check (clears it)
if (box.Attacked()) box.Attacker.SendMessage("Ouch"); // left-click
```

`Clicker` / `Attacker` are `Player` references (`execute on target` / `on attacker`).

---

## DisplayLib

Block, item and text **display entities** — models and floating text that don't move, collide or tick. They are entities, so everything from [EntityLib](#entitylib) works too (`m.SpawnEntityObject`, `Kill()`, `Teleport()`, `Glowing`, `Clone()`, the [single-use rule](language.md#objects-single-use)).

```csharp
use DisplayLib;
use BlockLib;

var label = new TextDisplay("Hello") { Billboard = Billboard.Center, Background = 0x40000000 };
m.SpawnEntityObject(label, Position.Here.Offset(0, 2, 0));
label.Text = $"Score: {score}";              // runtime values are written into the text (1.20.2+)

new Block : OakStairs stairs;
stairs.Facing = Facing.East;
var model = new BlockDisplay(stairs) { Scale = 0.5f, Brightness = 15 };
m.SpawnEntityObject(model, Position.Here);
model.RotationY = 90f;                       // degrees
model.Scale = 2f;
model.Animate(20);                           // the two changes above play over 20 ticks

var gem = new ItemDisplay("diamond") { Mode = ItemDisplayMode.Ground };
```

The constructor argument sets the content (`Text`, `Block`, `Item`); the `{ … }` initializer sets other properties. Both are applied right after the summon, like other pre-spawn writes.

**All displays**

| Property | Type | NBT | |
|---|---|---|---|
| `Billboard` | `Billboard` (`Fixed`, `Vertical`, `Horizontal`, `Center`) | `billboard` | Turn to face the player |
| `Scale` / `ScaleX`, `ScaleY`, `ScaleZ` | `Milli` | `transformation.scale` | Uniform / per axis |
| `OffsetX`, `OffsetY`, `OffsetZ` | `Milli` | `transformation.translation` | Model offset in blocks |
| `RotationY`, `RotationX` | `Milli` (degrees, write-only) | `transformation.left_rotation` / `right_rotation` | Around Y (counterclockwise seen from above), then X |
| `Brightness` | `int` 0–15 | `brightness` | Fixed light level (block and sky) |
| `GlowColor` | `int` (RGB) | `glow_color_override` | Outline color with `Glowing = true` |
| `ViewRange`, `ShadowRadius`, `ShadowStrength`, `Width`, `Height` | `Milli` | same names | |
| `InterpolationDuration`, `InterpolationDelay` | `int` ticks | `interpolation_duration`, `start_interpolation` | |
| `TeleportDuration` | `int` ticks | `teleport_duration` | Smooth teleports (1.20.2+) |

`Animate(int ticks)` animates this tick's transformation changes (`Scale`, `Offset…`, `Rotation…`) over `ticks` ticks.

**TextDisplay**: `Text` (string, write-only), `LineWidth` (`int`), `Background` (`int` ARGB, e.g. `0x40000000`), `TextOpacity` (`int` 0–255), `Shadow`, `SeeThrough`, `DefaultBackground` (`bool`), `Alignment` (`TextAlignment.Center/Left/Right`).
**BlockDisplay**: `Block` (a [block object](#blocklib), write-only; the constructor also takes an ID string such as `"oak_stairs[facing=east]"`).
**ItemDisplay**: `Item` (item ID, write-only), `Mode` (`ItemDisplayMode`: `None`, `Head`, `Gui`, `Ground`, `Fixed`, `ThirdPersonLeftHand`, …).

- **Runtime text:** text displays don't resolve score components, so `$"…{x}…"` is turned into a function macro that writes the current values into the text (numbers formatted like `m.Console.WriteLine`). This needs 1.20.2+; constant text works everywhere. It shows the value at the time of the assignment — assign again to update it.
- **Runtime rotation** (`model.RotationY = angle;`) also uses a macro (1.20.2+). Other properties work with runtime values on every version.
- **Versions:** text is written as JSON (≤1.21.4) or SNBT (1.21.5+), items with `Count` (≤1.20.4) or `count`, and the block state as `{Name, Properties}` (≤26.2) or `{id, properties}` (26.3+) — handled per version.
- Reading works for number properties (`model.ScaleX`, `label.LineWidth`, …); `Text`, `Block`, `Item` and the rotations are write-only.

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

### Regions: `Region` and `Clipboard`

```csharp
var r = new Region(new Position(0, 64, 0), new Position(3, 66, 1));
r.Fill("minecraft:stone");
r.Replace("minecraft:stone", "minecraft:dirt");
r.Clone(new Position(10, 64, 0));            // copy (masked: true skips air)
r.Move(new Position(40, 64, 0));             // move; the region becomes air
r.Stack(Direction.Up, 3);                    // 3 copies on top (offset = region size)
r.Stack(0, 0, 5, 2);                         // 2 copies, 5 blocks apart along z
Clipboard snap = r.Copy(new Position(100, 0, 100));   // snapshot into a free buffer area
Clipboard cut = r.Cut(new Position(100, 0, 100));     // move into the buffer
snap.Paste(new Position(30, 64, 0));
bool same = r.Matches(new Position(10, 64, 0));        // execute if blocks
```

- Corners and destinations must be relative to the same position (`p`, `p.Offset(…)`) or absolute, with constant coordinates. Every method returns whether it succeeded.
- `r.Copy()` without a buffer pastes the region as it is **when pasted**; with a buffer it pastes the blocks as they were when copied. Minecraft has no block memory besides the world, so a snapshot needs a free area (in loaded chunks).
- `Stack(Direction, n)` and buffers need the region size at compile time: both corners relative (`~`) or both absolute.
- Minecraft limits `clone` / `fill` to 32768 blocks per command.

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
| `Health` | `Milli` | ✅ | |
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

Q16 a = 30 * Math.Deg2Rad;
Q16 s = Math.Sin(a);          // 0.5
Milli  c = Math.Cos(1.0f);       // Milli overload → Milli result
```

| Member | Description |
|---|---|
| `Math.Sin(x)`, `Math.Cos(x)`, `Math.Tan(x)` | Radians. `Q16` and `Milli` overloads |
| `Math.PI`, `Math.Tau`, `Math.Deg2Rad`, `Math.Rad2Deg` | Constants (folded at compile time) |
| `Math.FloorDiv(a, b)`, `Math.FloorMod(a, b)` | `int` division rounded down / remainder with the divisor's sign. One scoreboard command each (the `/` and `%` operators follow C# instead — see the [language guide](language.md#division-and-remainder)) |

How it works:
- **Number format:** `Q16` is Q15.16 fixed point (score = value × 65536, precision 1/65536, range about ±32768). `Milli` stays ×1000. Mixing them converts automatically; a `Q16` → `Milli` assignment needs a cast, as in C#.
- **Lookup table, not a Taylor series:** the angle is reduced modulo 2π, turned into a position on a 1024-step circle, and looked up in a 257-entry quarter-wave sine table (with symmetry for the other quadrants) plus linear interpolation between neighbours. The table is a binary tree of `execute if score … matches` functions, so it works on every version (no macros). Error is within about 2/65536 over the whole range.
- `Cos(x)` is the same table shifted by a quarter turn; `Tan(x)` is `Sin/Cos` with a Q15.16 division and saturates to ±32767.99 near ±π/2.
- Constant arguments are computed at compile time.
- `Q16 * Q16` and `Q16 / Q16` need more than 32 bits, so they call shared helpers (`<ns>:__math/dmul`, `__math/ddiv`) that split the operands into 16-bit halves (multiply) or do 16-step long division (divide). Multiplication is floored, division rounds toward zero (like C#), and both are exact; a division whose result does not fit saturates to ±2147483647/65536. Multiplying or dividing by a whole-number constant stays a single scoreboard operation.
- The helpers and the table are only emitted when used.
- **26.3+ (`/compute`):** the operations above become single commands that evaluate a number-provider expression — `Q16 *` (≈5× faster), `Q16 /` (≈11× faster when the divisor is below 128.0; larger divisors keep the long division), `int` `/` and `%` (≈2.7× faster) and `Sin`/`Cos`/`Tan` (≈13× faster). Results of `*`, `/` and `%` are bit-for-bit the same as on older versions. The trig functions read the game's own 65536-entry sine table and correct to first order, which is a bit more accurate (worst case about 1.6/65536 instead of 1.8/65536), so their last digit can differ from older versions. One difference: a `Q16` multiplication whose result does not fit (beyond ±32768) gives 0 on 26.3 instead of a wrapped value.

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

## WorldLib

```csharp
use WorldLib;

int t = World.Time();                 // time of day 0–23999 (also Day(), GameTime())
World.SetTime(6000); World.AddTime(100);
World.Weather = Weather.Rain;          // Clear / Rain / Thunder
World.SetWeather(Weather.Clear, 600);  // for 600 seconds
World.Difficulty = Difficulty.Hard;
World.GameRule("keepInventory", true);
World.SetBorder(500, 30); World.SetBorderCenter(0, 0);
World.Run(Dimension.Nether, () => m.Console.WriteLine("nether"));   // execute in minecraft:the_nether
if (World.IsIn(Dimension.End)) { … }
var p = new Position(0, 64, 0).In(Dimension.Nether);                 // a position in another dimension
```

`Dimension`: `Overworld`, `Nether`, `End`. Commands that differ between versions are adapted for you: `time query` on 26.1+, world border durations on 1.21.11+, and game rule names — `GameRule` takes the old (`keepInventory`) or new (`keep_inventory`) name and writes the right one for each version, including the 1.21.11 renames (`doDaylightCycle` ↔ `advance_time`, `disableRaids` ↔ `raids` with the value inverted).

---

## ParticleLib

```csharp
use ParticleLib;

Particle.Spawn("flame", pos, 5);                          // count, speed = 0, force = false
Particle.Spawn("end_rod", pos, 0.5, 0.5, 0.5, 20, 0.1);   // spread box dx dy dz, count, speed
Particle.Dust(0xFF8000, pos, 1.5f);                       // colored dust (syntax per version)
Particle.Line("crit", from, to, 0.25);                    // points along a line
Particle.Circle("happy_villager", pos, 2, 16);            // radius, points
```

`Line` and `Circle` are computed at compile time (one command per point, up to 512), so their positions need constant coordinates.

---

## SoundLib

```csharp
use SoundLib;

Sound.Play("entity.experience_orb.pickup", pos, 1, 2);          // volume, pitch, source = Master
Sound.PlayTo(Player.All, "block.note_block.bell", 0.5f, 1, SoundSource.Record);
Sound.Stop(Player.All);                                          // or Stop(players, "sound.id")
```

| Call | Plays at | Heard by |
|---|---|---|
| `Play(id, pos, …)` | `pos` | every player in range of `pos` |
| `PlayTo(players, id, …)` | each player's own position (`execute as <players> at @s run playsound … @s ~ ~ ~`) | that player only |
| `Stop(players[, id])` | — | stops in every sound category |

`SoundSource`: `Master`, `Music`, `Record`, `Weather`, `Block`, `Hostile`, `Neutral`, `Player`, `Ambient`, `Voice`. Every parameter and default is in the [API reference](api.md#soundlib).

---

## NbtLib

```csharp
use NbtLib;

Storage.Set("cfg.level", 3);                  // <ns>:data cfg.level
Storage.Set("cfg.name", name);                // NString / string
Storage.SetNbt("cfg.list", "[1,2,3]");        // any SNBT
int lv = Storage.Get<int>("cfg.level");
NString n = Storage.Get<NString>("cfg.name");
bool has = Storage.Has("cfg.level");
int len = Storage.Count("cfg.list");          // list size / string length
Storage.Copy("cfg", "backup"); Storage.Remove("cfg.level"); Storage.Merge("{a:1}");
Storage.Get<int>("score", storage: "other:data");    // any storage id

Nbt.Set(e, "CustomName", "Boss");             // entities: an Entity or entity reference
int air = Nbt.Get<int>(e, "Air");
Nbt.Merge(new Position(1, 64, 2), "{Lock:\"key\"}");  // block entities: a Position
Nbt.Save(e, "Inventory", "saved.inv");        // copy NBT into storage
```

`T` is `int`, `bool`, `Milli`, `Q16` or `NString`. Numbers are read with `data get … <scale>`, which rounds down at the type's precision. Players' NBT can be read but not changed (Minecraft).

---

## TeamLib

```csharp
use TeamLib;

static Team red = new Team("red", "Red Team") { Color = Color.Red, FriendlyFire = false };
static Objective kills = new Objective("kills", "playerKillCount", "Kills");

red.Join(Player.Self);  red.Leave(Player.Self);
if (red.Has(Player.Self)) { … }
int n = red.Count;  red.Players.SendMessage("go!");
kills[Player.Self] += 1;       // any score: an entity, a player or a fake name ("#total")
kills.Add("#total", 5);  kills.Reset("#total");
kills.Show();                  // sidebar
```

- Teams and objectives are created on load with the exact names given. `Team` properties: `Color` (TuiLib `Color`), `FriendlyFire`, `SeeFriendlyInvisibles`, `Prefix`, `Suffix`, `DisplayName`, `NameTags`, `DeathMessages` (`Visibility`), `Collision`.
- Reading `kills[…]` needs a single target; writing with a selector such as `Player.All` changes every matching score.

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
