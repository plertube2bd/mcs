# MC# Library Reference

MC# ships with five built-in libraries. All of them compile into the datapack itself, so the output pack never depends on other datapacks or mods.

| Library | How to load it | Contents |
|---|---|---|
| **Core** | Always available | `asm`, `m.Console`, `Position`, `GetWorldSpawn`, `SetWorldSpawn`, `SpawnEntity`, `m.SetBlock`/`FillBlock`/`IsBlock`, `[Load]`, `[Tick]` |
| **EntityLib** | `use EntityLib;` | `Entity`, `EntityType`, `m.SpawnEntityObject` |
| **BlockLib** | `use BlockLib;` | `Block` and one class per block (`OakStairs`, `Stone`, …), `m.SetBlockObject`/`FillBlockObject`/`IsBlockObject` |
| **PlayerLib** | `use PlayerLib;` | `Player` (control, inventory slots, effects, …), `Slot`, `GameMode` |
| **TuiLib** | `use TuiLib;` | Clickable chat `Button`, styled `Text`, `Tui.Menu`/`Print`/`Title` |

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
| `float` / `double` | `-12.345` (3 decimal places) |
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
