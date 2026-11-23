# MC# for VS Code

Errors while you type, syntax highlighting, snippets, `mcs.json` validation and a build-error matcher for [MC#](../../README.md) (`.mcs`).

- **Errors while you type:** after a short pause (and on save) the extension runs the compiler with `--check --stdin`, so the unsaved text is checked and nothing is written. If there is an `mcs.json` above the file, the whole project is checked and errors in other files show up too. The status bar shows `MC# ✓` or the error count.
- **Highlighting:** C# plus MC#-only syntax — `use X;`, `new Entity : Zombie x;`, `Wait()`, `class X : dummy`, and `asm` lines/blocks, whose contents are highlighted as mcfunction (commands, selectors, resource IDs, JSON, `$(macros)`).
- **Snippets:** `main`, `tick`, `load`, `asm`, `cw` (WriteLine), `wait`, `entityclass`, `foreache`, `dummy`, `mount`, `arr`, `board`, `bossbar`, `onevent`, `onrightclick`, `ontrigger`, `item`, `onrightclickobject`, `textdisplay`, `blockdisplay`.
- **`mcs.json`:** completion and validation of the project file.
- **Problem matcher `$mcs`:** turns `file(line,col): error MCS: …` into entries in the Problems panel.

### Settings

| Setting | Default | |
|---|---|---|
| `mcs.diagnostics` | `onType` | `onType`, `onSave` or `off` |
| `mcs.delay` | `600` | ms after the last keystroke before checking |
| `mcs.compiler` | (auto) | Command that starts the compiler, e.g. `dotnet /path/to/mcs.dll`. Auto: `src/Mcs.Compiler/bin/Release/net8.0/mcs.dll` in the workspace (run with `dotnet`, also `~/.dotnet/dotnet`), else `mcs` on the PATH |

Commands: **MC#: Check for errors**, **MC#: Show compiler output**.

## Install

```sh
python3 editors/vscode/install.py              # packages a .vsix and runs `code --install-extension`
python3 editors/vscode/install.py --code codium
```

Then run **Developer: Reload Window**. After changing the grammar, run the script again.

## Build task with errors in the Problems panel

`.vscode/tasks.json`:

```json
{
  "version": "2.0.0",
  "tasks": [
    { "label": "MC#: build file", "type": "shell", "command": "./mcs ${relativeFile}", "problemMatcher": "$mcs", "group": { "kind": "build", "isDefault": true } },
    { "label": "MC#: tests", "type": "shell", "command": "python3 tests/run.py", "problemMatcher": "$mcs", "group": "test" }
  ]
}
```
