# Compiling MC#

This guide covers turning an MC# source file (`.mcs`) into a Minecraft datapack and applying it to a world.

## 1. Requirements

| Item | Version | Notes |
|---|---|---|
| .NET SDK | 8.0 or later | Install it under your home directory with `dotnet-install.sh --channel 8.0 --install-dir ~/.dotnet` |
| Minecraft | Java Edition **1.20.1 – 26.3** | Works on servers and in single player. Other versions are a build error. |

The `./mcs` script looks in `~/.dotnet` automatically, so you don't need to change `PATH`.

## 2. Writing a source file

```csharp
// hello.mcs
use EntityLib;

public static void Main() {
    new Entity : Zombie x;
    x.maxHealth = 100f;
    x.Health = 100f;
    if (!m.SpawnEntityObject(x, GetWorldSpawn())) return;
    m.Console.WriteLine($"Spawned. UUID : {string.join("", x.uuid)}");
}
```

## 3. Compiling

There are two ways to build. Both produce the same result.

### a) Project file (`mcs.json`)

```json
{
  "namespace": "hello",
  "sources": ["src/*.mcs"],
  "output": "out/hello",
  "minecraft": "1.20.1-26.3"
}
```

```sh
./mcs build                 # uses ./mcs.json
./mcs build path/mcs.json   # uses another project file
./mcs build --mc 1.21.4     # command-line options override the project file
```

| Field | Description | Default |
|---|---|---|
| `namespace` | Datapack namespace (`a-z 0-9 _ . -`) | First source file name |
| `sources` | Files, folders (all `.mcs` inside), or `dir/*.mcs` patterns. Relative to `mcs.json`. | `*.mcs` |
| `output` | Output folder. Relative to `mcs.json`. | `out/<namespace>` |
| `minecraft` | Target version or range (see below) | `1.20.1-26.3` |
| `resources` | Folder copied into the datapack unchanged (worldgen, tags, …) | — |
| `description` | `pack.mcmeta` description | `MC# datapack: <ns> (...)` |

### b) Command line

```sh
./mcs hello.mcs -o out/hello --mc 1.20.1-26.3
```

| Option | Description | Default |
|---|---|---|
| `<file.mcs ...>` | Source files. If you pass several files, they compile into one datapack. | (required) |
| `--mc <version>` / `--mc <min>-<max>` | Target Minecraft version or range | `1.20.1-26.3` |
| `-o`, `--out <dir>` | Output datapack folder | `out/<namespace>` |
| `-n`, `--namespace <ns>` | Datapack namespace | First file name |
| `--versions` | Lists supported versions, pack formats, and `#if` symbols | — |
| `--emit-csharp` | Prints the C# that MC# syntax is converted into (for debugging) | — |
| `--check` | Only reports errors; writes nothing (used by the editor) | — |
| `--stdin <file>` | Reads the text of `<file>` from standard input instead of the disk (unsaved editor buffers) | — |

## 4. Target versions and branching

The build is **branched by version**. The compiler doesn't restrict itself to features every version shares. Instead, it compiles your code once per version group in the target range, using the best commands each group supports. Groups that produce identical output are merged into one.

```
$ ./mcs build
Build succeeded: out/versions (Minecraft 1.20.1 – 26.3)
  base           1.20.1             3 functions
  mcs_1_20_2     1.20.2-1.20.6      4 functions
  mcs_1_21       1.21-1.21.1        4 functions
  mcs_1_21_2     1.21.2-1.21.8      4 functions
  mcs_1_21_9     1.21.9-26.3        6 functions
```

- **A range (`1.20.1-26.3`)** produces one datapack. Each branch is stored in a version-specific overlay folder, and Minecraft picks the right branch automatically.
- **A single version (`1.21.4`)** produces a datapack for that version only, without overlays.
- **Versions outside 1.20.1 – 26.3** (e.g. `1.19.4`, `26.4`) are a build error.

### What the compiler branches on automatically

| Version | Difference | How MC# handles it |
|---|---|---|
| 1.20.2+ | Function macros | Runtime attribute values (`x.maxHealth = hp;`) become possible |
| 1.21+ | `functions/` folder renamed `function/` | Writes the right folder |
| 1.21.2+ | Attribute IDs lose `generic.`; `boat` → `oak_boat` | Writes the right IDs |
| 1.21.9+ | `pack.mcmeta` uses `min_format`/`max_format` | Writes the right fields |
| 1.21.9+ | Spawn chunks removed (nothing is loaded when `#load` runs) | If `GetWorldSpawn()` is used or the pack summons entities / places blocks, force-loads the spawn chunk and starts `Main` once it's loaded |
| 26.3+ | `/compute` (number-provider expressions) | `double` `*` `/`, C# `int` `/` `%` and `Math.Sin/Cos/Tan` become single commands instead of helper functions |

### Branching in your code (`#if`)

Each version group defines a symbol of the form `MC_<version>_OR_GREATER`, similar to .NET's `NET6_0_OR_GREATER`. Use it with C#'s `#if`:

```csharp
float hp = 30f;
#if MC_1_20_2_OR_GREATER
    hp = hp + 20f;
    h.maxHealth = hp;        // runtime value → uses a macro (1.20.2+)
#else
    h.maxHealth = 30f;       // 1.20.1 has no macros, so only constants work
#endif
```

If code uses a feature that some versions in the range don't have, the error message names those versions:

```
macro.mcs(5,19): error MCS: [1.20.1] Setting maxHealth from a runtime value needs function macros (1.20.2+). Use '#if MC_1_20_2_OR_GREATER' or raise the minimum target version.
```

There are two ways to fix it: wrap the code in `#if`, or raise the minimum version (`--mc 1.20.2-26.3`).

Symbols exist only for the **first version of each pack-format group**. Run `./mcs --versions` to list them:

```
MC_1_20_1_OR_GREATER  MC_1_20_2_OR_GREATER  MC_1_20_3_OR_GREATER  MC_1_20_5_OR_GREATER
MC_1_21_OR_GREATER    MC_1_21_2_OR_GREATER  MC_1_21_4_OR_GREATER  MC_1_21_5_OR_GREATER
MC_1_21_6_OR_GREATER  MC_1_21_7_OR_GREATER  MC_1_21_9_OR_GREATER  MC_1_21_11_OR_GREATER
MC_26_1_OR_GREATER    MC_26_2_OR_GREATER    MC_26_3_OR_GREATER
```

## 5. Errors

If the build fails, the compiler prints errors as `file(line,column): error MCS: message` and exits with code 1. No datapack is created.

- Errors starting with `CSxxxx` are C# type and syntax errors from Roslyn.
- All other errors mean you used a feature MC# can't express in Minecraft (or in some of the target versions).
- An error prefixed with `[1.20.1]` or similar applies only to the listed versions.

## 6. Applying the pack to a world

1. Copy the output folder (e.g. `out/hello`) into `<world>/datapacks/`.
2. In game, run `/reload`. On a new world, the pack loads automatically.
3. Check that it's enabled with `/datapack list enabled`. It shows up as `file/hello`.

When the pack loads:
- `Main()` runs automatically (on every `/reload` as well).
- Methods marked `[Load]` run at load time, and methods marked `[Tick]` run every tick.
- Every method can also be called by hand with `/function <ns>:<method name>`. Names are converted to snake_case: `SpawnBoss` → `/function hello:spawn_boss`.

## 7. Output structure

```
out/hello/
├── pack.mcmeta                    # Supported range + overlay list
├── data/                          # Base layer (lowest version group)
│   ├── hello/functions/
│   │   ├── __load.mcfunction      # Initialization (scoreboard, constants, static fields) + calls Main
│   │   ├── main.mcfunction
│   │   └── main/_if0.mcfunction   # Sub-functions generated for control flow
│   └── minecraft/tags/functions/load.json, tick.json
├── mcs_1_21/data/...              # Overlay for 1.21+ (function/ folder)
├── mcs_1_21_2/data/...            # Overlay for 1.21.2+
└── mcs_1_21_9/data/...            # Overlay for 1.21.9+
```

Which overlays appear depends on your code. Branches with identical output are merged.

### Name rules

| Item | Name |
|---|---|
| Scoreboard objective | `mcs.<ns>` |
| Local variable | `$<method>.<variable>` (e.g. `$main.sum`) |
| Static field | `$static.<field>` |
| Return value / temporary | `$<method>#ret`, `$<method>#t0` |
| Entity variable tag | `mcs.<ns>.<method>.<variable>` |
| Storage | `<ns>:mcs` |

To debug, check values in game with `/scoreboard players get $main.sum mcs.hello`.

## 8. Building the compiler yourself

```sh
cd src/Mcs.Compiler
dotnet build -c Release
dotnet bin/Release/net8.0/mcs.dll ../../examples/hello.mcs -o ../../out/hello
```

**Install as the `mcs` command:** `tools/install.sh` publishes the compiler to `~/.local/share/mcs` and creates `~/.local/bin/mcs` (run it again after pulling changes; `PREFIX=/usr/local` for another place, `--uninstall` to remove). `~/.local/bin` must be on your `PATH` (fish: `fish_add_path ~/.local/bin`).

| File | Role |
|---|---|
| `Versions.cs` | Version table and per-version features. **All version branching is decided here.** |
| `Preprocessor.cs` | Converts MC#-only syntax (`use`, `new T : K x;`, `asm`, top-level `asm` / `m.Mount`) to C# |
| `Prelude.cs` | C# declarations of the built-in libraries (see the [library reference](libraries.md)) |
| `Codegen.cs` | Syntax tree → mcfunction (core language) |
| `Codegen.Libs.cs` | BlockLib / PlayerLib / TuiLib / SpawnEntity / SetWorldSpawn |
| `Codegen.Math.cs` | `double` (Q15.16) multiply/divide helpers, C# integer division helpers, MathLib (sin/cos/tan lookup table) |
| `Codegen.Gui.cs` | GuiLib (display slots, `Board`, `BossBar`) |
| `Codegen.Array.cs` | Writable arrays (element scores, runtime-index lookup trees) |
| `Codegen.Objects.cs` | ItemLib, object single-use rule (`Clone`, `Dispose`, run-time guard) |
| `Codegen.Events.cs` | Player events (`[OnJoin]`, `[OnDeath]`, …) |
| `Codegen.Mount.cs`, `Mount.cs` | `m.Mount` bindings; scoreboard classes (`class X : dummy`, auto-declared fields) |
| `McData.cs`, `Data/mcdata.json` | Per-version block/item/entity/effect data; generates the block classes |
| `Datapack.cs` | Branch merging, overlays, pack.mcmeta, file output |
| `Program.cs` | CLI, `mcs.json` |
| `tools/mcsim.py`, `tools/mcreal.py`, `tests/run.py` | Simulator, real-server runner and test runner (section 9) |
| `editors/vscode/` | VS Code extension (section 10) |

To support a new Minecraft version, add a line to `Versions.Releases`. If the new version changes behavior, add a property to `TargetProfile`.

## 9. Tests and the simulator

```sh
python3 tests/run.py              # everything (builds the compiler first)
python3 tests/run.py arrays wait  # only cases whose name contains "arrays" or "wait"
python3 tests/run.py -u           # rewrite the expected outputs — review the git diff afterwards
```

| Folder | What is checked |
|---|---|
| `tests/cases/*.mcs` | Compiled for the whole version range, then **run in the simulator on every version layer** the build produced. The output must equal `<name>.out`. If layers behave differently (e.g. `belowName` vs `below_name`), the `.out` file has one `=== format N ===` section per layer. |
| `tests/errors/*.mcs` | Must fail to compile, and every `// @error <text>` line must appear in the errors. |
| `examples/*.mcs` | Must compile. |

Directives in a case: `// @mc 1.20.2-26.3` (version range), `// @ticks 100` (how long to simulate; the default is until nothing is scheduled, or 20 ticks when there are `[Tick]` methods), and `// @do 5 die Steve` (a scripted player action, repeatable — see below).

**Simulator** (`tools/mcsim.py`) runs a built datapack without Minecraft:

```sh
python3 tools/mcsim.py out/hello               # newest version layer
python3 tools/mcsim.py out/hello --format 15   # as 1.20.1
python3 tools/mcsim.py out/hello --ticks 100
```

- **Simulated:** scoreboards (32-bit wraparound, floor division), data storage and function macros, `compute` (26.3: 32-bit float math and the game's sine table, bit-for-bit), `function` / `schedule` / `#tick` / `#load`, `execute` (`if`/`unless score|entity|data`, `as`, `store …`), `tellraw` / `say`, a small entity model (`summon`, `tag`, `kill`, selectors with `type=`, `tag=`, `name=`, `scores=`, `limit=`), and scripted players. `maxCommandChainLength` is enforced, so endless loops show up.
- **Not simulated:** blocks, positions, entity NBT (players exist only when scripted). Such commands are printed as `> <command>`, so the output doubles as a trace. Commands that Minecraft would reject print as `! <error>`.
- Tick order follows the game: `#tick` functions first, then scheduled functions.
- **Scripted players** (`--do "TICK ACTION"` or `// @do` in tests; tick 0 = before load): `join NAME`, `leave NAME`, `die NAME`, `respawn NAME`, `kill NAME` (NAME killed something), `use NAME ITEM`, `stat NAME CRITERION [N]`, `trigger NAME OBJECTIVE [set|add N]`, `click NAME` (right-click with the held item), `hold NAME SLOT`, `offhand NAME SLOT`. Players have inventories (`/give` fills them; `if items` and `nbt={SelectedItem:…}` read them). Statistics update every objective with that criterion, `/trigger` needs `enable`, `@e` skips dead players, and messages to specific players print as `to Steve: …`.

### Real Minecraft servers

The simulator was written from the same understanding of the commands as the compiler, so a command the
compiler gets wrong can also pass in the simulator. `--real` also runs every case and example on **real
vanilla servers** (headless; `tools/mcreal.py`):

```sh
python3 tests/run.py --real                              # newest release of every pack format (15 servers)
python3 tests/run.py --real --versions=1.20.1,1.21.11    # just these
python3 tests/run.py --real --servers=2 arrays           # 2 servers at a time, only "arrays"
```

- Needs Java (21+, 25 for 26.x) and internet access the first time a version is used. Server jars are downloaded from Mojang once and kept in `~/.cache/mcs/servers`.
- Each pack is enabled alone, run, and disabled again. On 1.20.3+ the server is frozen and advanced with `tick step`, so it runs exactly as many ticks as the simulator; 1.20.1/1.20.2 run in real time.
- **Fails on** any ERROR/WARN line the server logs for the pack (a function that does not parse, a bad tag, advancement or overlay), and on **scores that differ from the simulator** (fake players such as `$x` / `#x`, read back with `scoreboard players list`).
- Cases with scripted players (`// @do`) are only load-checked (there are no players on the server). The same goes for `[Tick]` cases on 1.20.1/1.20.2.

## 10. Editor support (VS Code)

```sh
python3 editors/vscode/install.py              # packages a .vsix and installs it with `code`
```

**Errors while you type** (the extension runs the compiler with `--check` on the unsaved text; with an `mcs.json` the whole project is checked), highlighting for C# plus MC# syntax (`asm` contents as mcfunction, `use`, `new T : K x;`, `Wait`), snippets, `mcs.json` validation, and a `$mcs` problem matcher for build tasks. See [editors/vscode](../editors/vscode/README.md).
