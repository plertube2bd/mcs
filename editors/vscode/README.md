# MC# for VS Code

Syntax highlighting, snippets, `mcs.json` validation and a build-error matcher for [MC#](../../README.md) (`.mcs`).

- **Highlighting:** C# plus MC#-only syntax — `use X;`, `new Entity : Zombie x;`, `Wait()`, `class X : dummy`, and `asm` lines/blocks, whose contents are highlighted as mcfunction (commands, selectors, resource IDs, JSON, `$(macros)`).
- **Snippets:** `main`, `tick`, `load`, `asm`, `cw` (WriteLine), `wait`, `entityclass`, `foreache`, `dummy`, `mount`, `arr`, `board`, `bossbar`, `onevent`, `onrightclick`, `ontrigger`, `item`, `onrightclickobject`.
- **`mcs.json`:** completion and validation of the project file.
- **Problem matcher `$mcs`:** turns `file(line,col): error MCS: …` into entries in the Problems panel.

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
