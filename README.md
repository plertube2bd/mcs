# MC# (MCS)

A C#-like language that compiles to a Minecraft datapack (`.mcfunction`). Current language version: **MC# 1.0** ([specification](docs/ko/spec.md), `mcs --version`).

- **Front end:** Roslyn (the C# compiler) handles parsing, type checking, and constant folding.
- **Target:** Minecraft **1.20.1 – 26.3**. The build branches per version (`--mc` / `mcs.json`), and code can branch with `#if MC_1_21_OR_GREATER`. Other versions are a build error.
- **Dependencies:** none. The output uses only vanilla commands. Optionally, the [MC# runtime](docs/runtime.md) (`mcs.jar` for Fabric / NeoForge / Paper, 26.1+) adds fast paths, permission level 3–4 commands, `/<ns>:<function>` shortcuts, a debugger and an interpreter.

## Documentation
- **Development:** `python3 tests/run.py` runs the test suite in a simulator, and `--real` also on real Minecraft servers ([details](docs/compiling.md#9-tests-and-the-simulator)); `python3 editors/vscode/install.py` installs the VS Code extension (highlighting and errors while you type).
- [Compiling](docs/compiling.md)
- [Library reference](docs/libraries.md)
- [Runtime (mcs.jar)](docs/runtime.md) — the optional server mod: fast paths, `Runtime.Sudo`, shortcuts, debugger, `/mcsi` interpreter
- [API reference](docs/api.md) — every built-in type, member, default value and enum value (generated from the compiler)
- [Language guide](docs/language.md) — entity classes, queries, `switch`, `[Inline]`/`Unroll`, tables, `asm $""`
- 한국어: **[튜토리얼](docs/ko/tutorial/README.md)** · **[언어 명세](docs/ko/spec.md)** · [컴파일하기](docs/ko/compiling.md) · [런타임 (mcs.jar)](docs/ko/runtime.md) · [라이브러리 레퍼런스](docs/ko/libraries.md) · [언어 가이드](docs/ko/language.md)

## Usage

```sh
./mcs examples/hello.mcs -o out/hello        # copy out/hello into the world's datapacks folder
./mcs build examples/mcs.json                # build from a project file
./mcs hello.mcs --mc 1.21.4                  # single target version
./mcs a.mcs b.mcs -n mypack --emit-csharp    # print the preprocessed C#
./mcs run hello.mcs --do "0 join Steve"      # build, then run it in the simulator (no Minecraft)
```

Needs the .NET 8 SDK (installed at `~/.dotnet`).

## Syntax

```csharp
use EntityLib;                       // = using EntityLib;

static int counter = 0;              // static field → scoreboard (set once; keeps its value across reloads)

[Tick] public static void OnTick() { counter++; }   // #minecraft:tick
[Load] public static void Init() { }                // #minecraft:load

public static void Main() {          // entry point: runs automatically on load
    new Entity : Zombie x;           // = Entity x = new Entity(EntityType.Zombie);
    x.maxHealth = 100f;              // set before spawning → applied right after summon
    x.Health = 100f;
    if (m.SpawnEntityObject(x, GetWorldSpawn())) return;
    m.Console.WriteLine($"UUID : {string.join("", x.uuid)}");

    asm say hello;                   // one raw command (no braces needed)
    asm {                            // several commands
        say a
        tellraw @a {"text":"b"}
    }
}
```

Methods and fields can be written at the top level without a class. They are wrapped in `__Global` automatically.

### Type mapping

| MC# | Minecraft |
|---|---|
| `int`, `bool` | scoreboard (`mcs.<ns>` objective), or any score via `m.Mount` |
| `Milli` | scoreboard, fixed-point ×1000 (1/1000 precision) — `float` still works but is deprecated |
| `Q16` | scoreboard, Q15.16 fixed-point ×65536 (1/65536 precision, range ±32768) — `double` is deprecated |
| `int[]`, `bool[]`, `Milli[]`, `Q16[]` | one score per element; runtime indexes via a lookup tree |
| `NArray<T>` | runtime-length array in NBT storage (`Add`, `Insert`, `RemoveAt`, `foreach`, …) |
| `string` | compile-time constants |
| `NString` | runtime string in NBT storage (`+`, `$"…"`, `==`, `Substring`, `IndexOf`, …) |
| classes, structs, generics | fields flattened into scores; methods are functions; generics compiled per type argument |
| `Entity` | tag (`@e[tag=…,limit=1]`) |
| `Position` | execute prefix (`at …` / `positioned …`) |

### Supported constructs
`if/else`, `while`, `for`, `do-while`, `break`, `continue`, `return` (with a value), `?:`, `&&`/`||` (short-circuit), `+ - * / %` (C# semantics: `/` rounds toward zero, `%` takes the dividend's sign — or Minecraft's faster floor division with `[MinecraftDivision]` / `"division": "minecraft"`), compound assignment, `++/--`, static method calls, string interpolation (`Console.WriteLine`), `Wait()` (pause the method for a tick), arrays (`int[] a = new int[10]; a[i] += 1;`), classes with constructors / methods / inheritance / virtual calls, generic methods and classes, calls into other datapacks (`[External("ns:fn")] static extern void F();`).

### Built-in libraries
| Library | Contents |
|---|---|
| Core | `asm`, `Wait()`, player events (`[OnJoin]`, `[OnDeath]`, `[OnRightClick]`, `[OnTrigger]`, …), `m.Mount` (bind fields / `class X : dummy` to existing scores), `m.Console.WriteLine`, `Position` (`GetWorldSpawn()`, `.Offset()`), `SpawnEntity(id, pos, count)`, `SetWorldSpawn(pos)`, `m.SetBlock` / `m.FillBlock` / `m.IsBlock`, `[Load]`, `[Tick]`, `NString`, `NArray<T>`, `m.Call` |
| `use EntityLib;` | `new Entity : Zombie x;`, entity properties, `m.SpawnEntityObject`, interactions (`Damage`, `Ride`, `TeleportTo`, `LookAt`, `IsNear`), `Interaction` hitboxes, `m.MountEntity` |
| `use BlockLib;` | A class for every block with typed states (`new Block : OakStairs s; s.Facing = Facing.East;`), `m.SetBlockObject` / `m.FillBlockObject`, `Region` (`Fill`, `Clone`, `Move`, `Stack`, `Copy`/`Cut` → `Paste`), `m.MountBlock` |
| `use PlayerLib;` | `Player.All` / `Self` / `Named(...)`: messages, effects, xp, gamemode, **per-slot inventory control** (`SetSlot`, `SwapSlots`, `IsSlot`, …) |
| `use TuiLib;` | Clickable chat buttons wired to MC# methods (`new Button("[OK]", OnOk)`), styled text, menus |
| `use MathLib;` | `Math.Sin` / `Cos` / `Tan` (lookup table, Q15.16), `Math.FloorDiv` / `FloorMod`, `Math.PI`, `Deg2Rad` |
| `use DisplayLib;` | Block / item / text display entities: scale, rotation, animation, live text |
| `use ItemLib;` | Item objects with names/lore, `m.GiveObject`, `[OnRightClickObject(nameof(item))]` |
| `use GuiLib;` | `Gui.SetDisplay` / `ClearDisplay`, sidebar/tab-list `Board`, `BossBar` |
| `use WorldLib;` | `World.Time()`, `World.Weather`, difficulty, game rules, world border, dimensions (`World.Run(Dimension.Nether, …)`) |
| `use ParticleLib;` / `use SoundLib;` | `Particle.Spawn` / `Dust` / `Line` / `Circle`, `Sound.Play` / `PlayTo` / `Stop` |
| `use NbtLib;` | `Storage.Get<int>("path")` / `Set` / `Has` / …, entity and block NBT (`Nbt.Get` / `Set` / `Merge`) |
| `use TeamLib;` | `Team` (color, prefix, members), `Objective` (`kills[player] += 1`) |

Block/item/entity/effect IDs are validated per target version. See the [library reference](docs/libraries.md).

## Limitations (current version)
- Recursion works through a storage stack, but every recursive call costs a few commands per variable; deep recursion can hit `maxCommandChainLength`.
- Class references are fixed at compile time (no `null`, no choosing an object with `if`), and objects in fields are embedded (a class cannot contain itself). `T[]` arrays have a compile-time length; use `NArray<T>` for a runtime length.
- Joining `NString`s and runtime indexes into `NArray`s use function macros (1.20.2+).
- Runtime attribute values (`x.maxHealth = hp;`) need 1.20.2+ (macros).
- Objects (entity / block / item) are single use: spawning, placing or giving one twice is a compile error (or skipped at run time in loops); use `.Clone()`.
- `Wait()` loses the execution context (`@s`, position): the rest of the method runs from `/schedule`.
- `GetWorldSpawn()` uses a marker placed at load time. If `/setworldspawn` changes the spawn, run `/reload`.

## License

[MIT](LICENSE)
