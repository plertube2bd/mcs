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
| 1.21.9+ | Spawn chunks removed (nothing is loaded when `#load` runs) | If `GetWorldSpawn()` is used, force-loads the spawn chunk and starts `Main` once it's loaded |

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

| File | Role |
|---|---|
| `Versions.cs` | Version table and per-version features. **All version branching is decided here.** |
| `Preprocessor.cs` | Converts MC#-only syntax (`use`, `new T : K x;`, `asm`) to C# |
| `Prelude.cs` | C# declarations of the built-in libraries (see the [library reference](libraries.md)) |
| `Codegen.cs` | Syntax tree → mcfunction (core language) |
| `Codegen.Libs.cs` | BlockLib / PlayerLib / TuiLib / SpawnEntity / SetWorldSpawn |
| `McData.cs`, `Data/mcdata.json` | Per-version block/item/entity/effect data; generates the block classes |
| `Datapack.cs` | Branch merging, overlays, pack.mcmeta, file output |
| `Program.cs` | CLI, `mcs.json` |

To support a new Minecraft version, add a line to `Versions.Releases`. If the new version changes behavior, add a property to `TargetProfile`.
