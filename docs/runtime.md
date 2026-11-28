# MC# runtime (mcs.jar)

A server mod / plugin for MC# datapacks, for **Fabric, NeoForge and Paper**. Each jar works on **Minecraft 26.1 – 26.3**. Datapacks work without it; with it installed you get:

1. **Fast paths**: some operations are computed in one step instead of tens to hundreds of commands, with bit-identical results.
2. **Permission level 3–4 commands**: `Runtime.Sudo(...)` lets datapack functions run `ban`, `op`, `whitelist`, `stop` …
3. **Function shortcuts**: `/<namespace>:<function>` runs a function. Paths with a `__` segment are hidden.
4. **Runtime debugging**: view, change and watch MC# variables by name, trace function calls, profile.
5. **Interpreter**: `/mcsi` runs MC# code interactively (operators only), linked to the running datapack's variables and methods.

## Installing

Build with `runtime/build.sh` (Java 25, Gradle 9). The jars go to `runtime/dist/`.

| Server | File | Folder | Needs |
|---|---|---|---|
| Fabric | `mcs-fabric.jar` | `mods/` | Fabric API |
| NeoForge | `mcs-neoforge.jar` | `mods/` | — |
| Paper | `mcs-paper.jar` | `plugins/` | — |

The config file is created on first start: `config/mcs-runtime.properties` (Fabric, NeoForge) or `plugins/MCSRuntime/mcs-runtime.properties` (Paper).

| Key | Description | Default |
|---|---|---|
| `sudo.enabled` | Datapack functions may run permission level 3–4 commands | `true` |
| `sudo.log` | Log every sudo command | `true` |
| `shortcuts.enabled` | `/<ns>:<function>` shortcuts | `true` |
| `interpreter.permission` | Permission level for `/mcsi` | `2` |
| `debug.permission` | Permission level for the debugger (`/mcs vars`, `watch`, `trace`, `profile` …) | `2` |
| `interpreter.max-steps` | Interpreter: maximum loop iterations per input | `100000` |

## In the datapack: declaring the dependency

Set `"runtime"` in `mcs.json` (or `--runtime`):

| Value | With the jar | Without the jar |
|---|---|---|
| `optional` (default) | Fast paths | Runs as before; prints a one-time warning on load: `[mcsRuntimeServices] Warn: There is not MC# Runtime Server Mod. Performance may be reduced, but there are no issues with the datapack.` (only packs that use fast paths) |
| `required` | Fast paths + `Runtime.Sudo` | **The pack does not start**: an error is printed on load, `Main` / `[Load]` do not run, `[Tick]` methods return at once. |
| `off` | Not used | — |

- The jar exists for 26.1+, so fast paths and the probe are only added to 26.1+ layers. `required` needs a target range starting at 26.1 or later.
- Every mod command (`mcs …`) is on a macro line. Macro lines are parsed only when called, so the pack loads without errors on servers without the jar. On load, `<ns>:__rt/probe` asks the jar for its protocol version (`$__rt`).

### MC# API

```csharp
if (Runtime.IsAvailable) m.Console.WriteLine("mcs.jar is installed");   // always false below 26.1 or with "off"
Runtime.Sudo("whitelist on");                                            // needs "runtime": "required"
Runtime.Sudo($"ban {name} cheating");                                    // runtime values work too (macro)
```

`Runtime.Sudo` runs the command with permission level 4, right there in the function. It **only works in function files**: players, the console and command blocks cannot use `mcs sudo` (the jar only allows it in the context functions are parsed in). Anyone who can `/function` (level 2) can still run a function that uses it, so only use it in datapacks you trust.

### Operations with a fast path

| Operation | 26.1–26.2 | 26.3 |
|---|---|---|
| `Q16` `*` `/`, `Milli` `*` `/`, C# `int` `/` `%` | fast path | mostly `/compute` (already one command) |
| `Math.Sin` / `Cos` / `Tan` | fast path (same table + interpolation) | `/compute` |
| `NString.IndexOf` / `Contains` | fast path | fast path |

The first line of each helper function (`<ns>:__math/*`, `<ns>:__str/indexof`) hands the work to the jar, which follows the helper command by command (scoreboard `/` is floor division, dividing by zero leaves the score, ints wrap). `runtime/test/realtest.py` checks on real servers that the results with and without the jar are the same.

## Function shortcuts

```
/game:start                 # = /function game:start
/game:give {item:"apple"}   # macro arguments
```

- Same permission as `/function` (level 2), and runs in order (also inside functions).
- Hidden: paths with a segment starting with `__`, `minecraft:` functions, and in MC# packs the compiler's sub-functions (`main/_fn1`, `_wait0` …).
- Never replaces an existing command. Rebuilt on `/reload`.

## Debugger (`/mcs`)

The MC# compiler writes `data/<ns>/mcs/symbols.json`: where each variable and method lives (score holders, storage paths, function paths). The debugger and the interpreter use it to find MC# names.

| Command | Description |
|---|---|
| `/mcs version` | Jar version, loader, protocol |
| `/mcs packs` | Loaded MC# datapacks |
| `/mcs vars <pack> [method]` | Statics (or a method's parameters and locals) with their values |
| `/mcs get <pack> <name>` / `/mcs set <pack> <name> <value>` | Read / write one value. Names: `count`, `Cls.field`, `Main.n` (a local) |
| `/mcs watch <pack> <name>` | Report every change (`/mcs watch` alone lists them) |
| `/mcs unwatch [<pack> <name>]` | Stop watching |
| `/mcs trace start [pack]` / `stop` | Show function calls by depth (up to 300 lines) |
| `/mcs profile start [pack]` / `stop` / `show` | Calls, commands and self time (ms) per function |

Trace and profile wrap the functions only while active and put the originals back afterwards, so nothing is slower otherwise.

## Interpreter (`/mcsi`)

```
/mcsi                       # interactive mode: chat lines are MC# code; "exit" to leave
/mcsi run <code>            # run once (also from the console)
/mcsi use <pack>            # the datapack to link (automatic if only one MC# pack is loaded)
/mcsi vars | reset | exit
```

```csharp
int x = 6 * 7;
print($"x={x}");               // m.Console.WriteLine works too
count += 10;                   // a static of the linked pack (written to its score)
print(Add(2, 3));              // calls a method of the pack (parameter scores → function → return score)
for (int i = 0; i < 3; i++) Spawn(i);
/say commands work as is       // a line starting with "/" is a Minecraft command
int n = cmd("data get storage game:mcs v");   // a command's result
```

- **Supported:** `int` `bool` `Q16` `Milli` `string` `var` locals, operators, `if` `while` `for` `do` `break` `continue`, `?:`, casts, `$"…{expr}…"`, `Math.*`, `m.Random`.
- **Linked to the pack:** read and write statics (`int`/`bool`/`Milli`/`Q16`/`NString`), call static methods with `int`/`bool`/`Milli`/`Q16` parameters and results. A method runs to completion (as `@s` = you) before the next statement.
- **Not supported:** classes and objects, `new`, `foreach`, `switch`, `Wait()`, library objects (`Player`, `Entity` …). Use `/commands` or the pack's methods for those.
- Numbers behave like C# (`int` wraps, `/` and `%` round toward zero). Values written to pack variables are rounded to the variable's precision.
- Commands run with your own permissions; the interpreter never raises them.

## Tests

```sh
runtime/build.sh
python3 runtime/test/realtest.py                       # Fabric, NeoForge, Paper × 26.1.2, 26.2, 26.3
python3 runtime/test/realtest.py --loaders paper --versions 26.3
```

Servers are downloaded once into `~/.cache/mcs/runtime-servers`. Each gets two test packs (`runtime/test/rtcheck.mcs`, `rtsudo.mcs`) and the console checks every feature. In the simulator (`tests/run.py`), cases with `// @runtime` run as if the jar were installed.

## Layout

| Folder | Contents |
|---|---|
| `runtime/common/` | Shared code (vanilla Minecraft classes only); compiles to identical bytecode against 26.1.2, 26.2 and 26.3 |
| `runtime/fabric/`, `neoforge/`, `paper/` | Loader glue (command registration, ticks, chat, disconnects) |
| `runtime/test/` | Real-server tests |
