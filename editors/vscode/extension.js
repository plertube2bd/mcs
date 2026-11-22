// MC# live diagnostics: runs the compiler with --check on the current buffer and shows its errors.
// Plain JavaScript on purpose: no npm install, no build step.
'use strict';
const vscode = require('vscode');
const cp = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

const DIAG = /^(.*)\((\d+),(\d+)\): error MCS: (.*)$/;
let collection, output, statusItem;
const timers = new Map();      // document uri → pending timeout
const running = new Map();     // run key → child process
const owned = new Map();       // run key → uris that got diagnostics from that run
let warnedMissing = false;

function activate(context) {
    collection = vscode.languages.createDiagnosticCollection('mcs');
    output = vscode.window.createOutputChannel('MC#');
    statusItem = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 0);
    statusItem.command = 'mcs.showOutput';
    context.subscriptions.push(collection, output, statusItem,
        vscode.commands.registerCommand('mcs.showOutput', () => output.show()),
        vscode.commands.registerCommand('mcs.check', () => {
            const ed = vscode.window.activeTextEditor;
            if (ed && ed.document.languageId === 'mcs') check(ed.document);
        }),
        vscode.workspace.onDidOpenTextDocument(d => schedule(d, 0)),
        vscode.workspace.onDidSaveTextDocument(d => { if (mode() !== 'off') schedule(d, 0); }),
        vscode.workspace.onDidChangeTextDocument(e => { if (mode() === 'onType') schedule(e.document, setting('delay', 600)); }),
        vscode.workspace.onDidCloseTextDocument(d => { if (!projectFile(d)) collection.delete(d.uri); }),
        vscode.workspace.onDidChangeConfiguration(e => { if (e.affectsConfiguration('mcs')) { warnedMissing = false; checkAllOpen(); } }),
    );
    checkAllOpen();
}

function setting(name, fallback) {
    return vscode.workspace.getConfiguration('mcs').get(name, fallback);
}

function mode() {
    return setting('diagnostics', 'onType');
}

function checkAllOpen() {
    if (mode() === 'off') { collection.clear(); return; }
    for (const d of vscode.workspace.textDocuments) schedule(d, 0);
}

function schedule(doc, delay) {
    if (doc.languageId !== 'mcs' || doc.uri.scheme !== 'file' || mode() === 'off') return;
    const key = doc.uri.toString();
    clearTimeout(timers.get(key));
    timers.set(key, setTimeout(() => { timers.delete(key); check(doc); }, delay));
}

/** The nearest mcs.json above the file (inside the workspace), or null. */
function projectFile(doc) {
    const ws = vscode.workspace.getWorkspaceFolder(doc.uri);
    const stop = ws ? ws.uri.fsPath : path.parse(doc.uri.fsPath).root;
    for (let dir = path.dirname(doc.uri.fsPath); ; dir = path.dirname(dir)) {
        const f = path.join(dir, 'mcs.json');
        if (fs.existsSync(f)) return f;
        if (dir === stop || path.dirname(dir) === dir) return null;
    }
}

/** [command, ...args] that start the compiler. */
function compilerCommand(doc) {
    const configured = (setting('compiler', '') || '').trim();
    if (configured) return configured.match(/"[^"]*"|\S+/g).map(s => s.replace(/^"|"$/g, ''));
    const dotnetHome = path.join(os.homedir(), '.dotnet', process.platform === 'win32' ? 'dotnet.exe' : 'dotnet');
    const dotnet = fs.existsSync(dotnetHome) ? dotnetHome : 'dotnet';
    // A compiler built inside the workspace (this repository)
    const folders = (vscode.workspace.workspaceFolders || []).map(f => f.uri.fsPath);
    const start = path.dirname(doc.uri.fsPath);
    for (let dir = start; ; dir = path.dirname(dir)) { folders.push(dir); if (path.dirname(dir) === dir) break; }
    for (const root of folders)
        for (const cfg of ['Release', 'Debug']) {
            const dll = path.join(root, 'src', 'Mcs.Compiler', 'bin', cfg, 'net8.0', 'mcs.dll');
            if (fs.existsSync(dll)) return [dotnet, dll];
        }
    return ['mcs'];
}

function check(doc) {
    if (doc.isClosed) return;
    const proj = projectFile(doc);
    const cwd = proj ? path.dirname(proj) : path.dirname(doc.uri.fsPath);
    const key = proj || doc.uri.fsPath;
    const [cmd, ...pre] = compilerCommand(doc);
    const args = proj ? [...pre, 'build', proj, '--check', '--stdin', doc.uri.fsPath]
                      : [...pre, doc.uri.fsPath, '--check', '--stdin', doc.uri.fsPath];

    const previous = running.get(key);
    if (previous) previous.kill();
    let child;
    try {
        child = cp.spawn(cmd, args, { cwd, env: { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: '1', DOTNET_NOLOGO: '1' } });
    } catch (e) {
        return missing(cmd, e);
    }
    running.set(key, child);
    statusItem.text = '$(sync~spin) MC#';
    statusItem.tooltip = 'Checking with the MC# compiler';
    statusItem.show();
    let out = '';
    child.stdout.on('data', b => out += b);
    child.stderr.on('data', b => out += b);
    child.on('error', e => { running.delete(key); statusItem.hide(); missing(cmd, e); });
    const timer = setTimeout(() => child.kill(), 60000);
    child.on('close', code => {
        clearTimeout(timer);
        if (running.get(key) !== child) return;     // a newer run replaced this one
        running.delete(key);
        if (code === null) { statusItem.hide(); return; }
        publish(key, doc, cwd, out, code);
    });
    child.stdin.on('error', () => {});
    child.stdin.end(doc.getText());
}

function missing(cmd, err) {
    if (warnedMissing) return;
    warnedMissing = true;
    output.appendLine(`Could not start the MC# compiler (${cmd}): ${err.message}`);
    vscode.window.showWarningMessage(
        'MC#: the compiler was not found, so errors are not shown while you type. Build it (dotnet build src/Mcs.Compiler -c Release) or set "mcs.compiler".',
        'Open Settings').then(c => { if (c) vscode.commands.executeCommand('workbench.action.openSettings', 'mcs.compiler'); });
}

function publish(key, doc, cwd, text, code) {
    const byFile = new Map();
    const general = [];
    for (const line of text.split(/\r?\n/)) {
        const m = DIAG.exec(line);
        if (m) {
            const file = path.resolve(cwd, m[1]);
            const ln = Math.max(0, +m[2] - 1), col = Math.max(0, +m[3] - 1);
            let msg = m[4], codeId;
            const c = /^(CS\d+): (.*)$/s.exec(msg);
            if (c) { codeId = c[1]; msg = c[2]; }
            const d = new vscode.Diagnostic(rangeAt(file, ln, col), msg, vscode.DiagnosticSeverity.Error);
            d.source = 'MC#';
            if (codeId) d.code = codeId;
            if (!byFile.has(file)) byFile.set(file, []);
            byFile.get(file).push(d);
        } else if (/^error: /.test(line)) {
            general.push(line.slice(7));
        }
    }
    if (general.length) {   // errors without a location (bad mcs.json, unsupported version, …)
        const list = byFile.get(doc.uri.fsPath) || [];
        for (const g of general) {
            const d = new vscode.Diagnostic(new vscode.Range(0, 0, 0, 0), g, vscode.DiagnosticSeverity.Error);
            d.source = 'MC#';
            list.push(d);
        }
        byFile.set(doc.uri.fsPath, list);
    }
    if (code !== 0 && byFile.size === 0) output.appendLine(text.trim());

    for (const uri of owned.get(key) || []) if (!byFile.has(uri)) collection.delete(vscode.Uri.file(uri));
    for (const [file, list] of byFile) collection.set(vscode.Uri.file(file), list);
    owned.set(key, new Set(byFile.keys()));

    const n = [...byFile.values()].reduce((a, l) => a + l.length, 0);
    statusItem.text = n ? `$(error) MC# ${n}` : '$(check) MC#';
    statusItem.tooltip = n ? `${n} MC# error(s)` : 'MC#: no errors';
}

/** Underline the word at the error (or one character). */
function rangeAt(file, line, col) {
    const doc = vscode.workspace.textDocuments.find(d => d.uri.fsPath === file);
    if (doc && line < doc.lineCount) {
        const w = doc.getWordRangeAtPosition(new vscode.Position(line, col));
        if (w) return w;
        const len = doc.lineAt(line).text.length;
        return new vscode.Range(line, Math.min(col, len), line, Math.min(col + 1, len));
    }
    return new vscode.Range(line, col, line, col + 1);
}

function deactivate() {
    for (const c of running.values()) c.kill();
}

module.exports = { activate, deactivate };
