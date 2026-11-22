# MC# (MCS)

A C#-like language that compiles to a Minecraft datapack (`.mcfunction`).

- **Front end:** Roslyn (the C# compiler) handles parsing, type checking, and constant folding.
- **Target:** Minecraft **1.20.1 – 26.3**. The build branches per version (`--mc` / `mcs.json`), and code can branch with `#if MC_1_21_OR_GREATER`. Other versions are a build error.
- **Dependencies:** none. The output uses only vanilla commands.

## Documentation
- [Compiling](docs/compiling.md)
- [Library reference](docs/libraries.md)
- [Language guide](docs/language.md) — entity classes, queries, `switch`, `[Inline]`/`Unroll`, tables, `asm $""`
- 한국어: [컴파일하기](docs/ko/compiling.md) · [라이브러리 레퍼런스](docs/ko/libraries.md) · [언어 가이드](docs/ko/language.md)

## Usage

```sh
./mcs examples/hello.mcs -o out/hello        # copy out/hello into the world's datapacks folder
./mcs build examples/mcs.json                # build from a project file
./mcs hello.mcs --mc 1.21.4                  # single target version
./mcs a.mcs b.mcs -n mypack --emit-csharp    # print the preprocessed C#
```

Needs the .NET 8 SDK (installed at `~/.dotnet`).

## Syntax

```csharp
use EntityLib;                       // = using EntityLib;

static int counter = 0;              // static field → scoreboard (keeps its value across reloads)

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
| `int`, `bool` | scoreboard (`mcs.<ns>` objective) |
| `float`, `double` | scoreboard, fixed-point ×1000 (3 decimal places) |
| `string` | **compile-time constants only** (no runtime string processing) |
| `Entity` | tag (`@e[tag=…,limit=1]`) |
| `Position` | execute prefix (`at …` / `positioned …`) |

### Supported constructs
`if/else`, `while`, `for`, `do-while`, `break`, `continue`, `return` (with a value), `?:`, `&&`/`||` (short-circuit), `+ - * / %`, compound assignment, `++/--`, static method calls, string interpolation (`Console.WriteLine`).

### Built-in libraries
| Library | Contents |
|---|---|
| Core | `asm`, `m.Console.WriteLine`, `Position` (`GetWorldSpawn()`, `.Offset()`), `SpawnEntity(id, pos, count)`, `SetWorldSpawn(pos)`, `m.SetBlock` / `m.FillBlock` / `m.IsBlock`, `[Load]`, `[Tick]` |
| `use EntityLib;` | `new Entity : Zombie x;`, entity properties, `m.SpawnEntityObject` |
| `use BlockLib;` | A class for every block with typed states (`new Block : OakStairs s; s.Facing = Facing.East;`), `m.SetBlockObject` / `m.FillBlockObject` |
| `use PlayerLib;` | `Player.All` / `Self` / `Named(...)`: messages, effects, xp, gamemode, **per-slot inventory control** (`SetSlot`, `SwapSlots`, `IsSlot`, …) |
| `use TuiLib;` | Clickable chat buttons wired to MC# methods (`new Button("[OK]", OnOk)`), styled text, menus |

Block/item/entity/effect IDs are validated per target version. See the [library reference](docs/libraries.md).

## Limitations (current version)
- **No recursion**: variables are static per function.
- Plain (non-entity) instance classes, writable arrays and runtime strings are not supported.
- Runtime attribute values (`x.maxHealth = hp;`) need 1.20.2+ (macros).
- Scoreboard division rounds toward negative infinity (it differs from C# for negative numbers).
- `GetWorldSpawn()` uses a marker placed at load time. If `/setworldspawn` changes the spawn, run `/reload`.
