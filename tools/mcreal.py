#!/usr/bin/env python3
"""Runs compiled MC# datapacks on real (vanilla, headless) Minecraft servers.

  tools/mcreal.py PACK_DIR… [--versions 1.20.1,1.21.11] [--ticks N]

For every Minecraft version, one server is started (jars are downloaded from Mojang once and cached in
~/.cache/mcs/servers). Each pack is then enabled on its own, run, and disabled again:

  1. every ERROR / WARN line the server logs while the pack is loaded or running is collected
     (functions that fail to parse, bad tags, broken pack.mcmeta overlays, …)
  2. the scores the pack leaves behind are read back with `scoreboard players list`

On 1.20.3+ the server is frozen (`tick freeze`) and advanced with `tick step N`, so a pack runs for exactly
N ticks, like the simulator. 1.20.1/1.20.2 have no /tick: there the pack runs in real time.
The test runner (tests/run.py --real) compares the scores against tools/mcsim.py.

Needs Java (21+; 26.x needs 25) and internet access the first time a version is used.
"""
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CACHE = os.path.join(os.path.expanduser('~'), '.cache', 'mcs', 'servers')
MANIFEST = 'https://piston-meta.mojang.com/mc/game/version_manifest_v2.json'

# Noise that vanilla servers log on their own
IGNORED = [
    r"Can't keep up!", r'Ambiguity between arguments', r'Failed to load properties', r'Failed to load eula',
    r'eula\.txt', r'No existing world data', r'UUID of added entity already exists', r'Missing data pack',
    r'Using default channel type', r'Ignoring unknown attribute', r'level\.dat',
    r'Detected setBlock in a far chunk', r'Unable to find spawn', r'Skipping', r'Server is running in offline mode',
    r'online-mode', r'offline mode', r'Mojang', r'servers will not be able to', r'While this makes',
    r'To change this', r'Experimental', r'Unknown data pack type', r'Failed to fetch',
    r'^Display entityNot a string$',   # ≤1.21.4: logged by every text_display summon (empty text)
]


def versions_from_compiler():
    """[(id, pack format)] from src/Mcs.Compiler/Versions.cs — the single source of truth."""
    text = open(os.path.join(ROOT, 'src', 'Mcs.Compiler', 'Versions.cs'), encoding='utf-8').read()
    return [(v, int(f)) for v, f in re.findall(r'new\("([\d.]+)", (\d+), \d+\)', text)]


def default_versions():
    """The newest release of every pack format group."""
    last = {}
    for v, f in versions_from_compiler():
        last[f] = v
    return list(last.values())


def has_tick_command(version):
    return version not in ('1.20.1', '1.20.2')


def _get(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'mcs-test-runner'})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def server_jar(version, log=print):
    """Downloads (once) and returns the path of the vanilla server jar."""
    path = os.path.join(CACHE, version, 'server.jar')
    if os.path.exists(path):
        return path
    manifest = json.loads(_get(MANIFEST))
    entry = next((v for v in manifest['versions'] if v['id'] == version), None)
    if entry is None:
        raise RuntimeError(f'Minecraft {version} is not in the Mojang version manifest')
    dl = json.loads(_get(entry['url']))['downloads']['server']
    log(f'downloading the {version} server ({dl["size"] // 1048576} MB)…')
    data = _get(dl['url'])
    if hashlib.sha1(data).hexdigest() != dl['sha1']:
        raise RuntimeError(f'checksum mismatch for the {version} server jar')
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path + '.part', 'wb') as f:
        f.write(data)
    os.replace(path + '.part', path)
    return path


LOG_LINE = re.compile(r'^\[[^\]]*\] \[([^\]/]*)/(\w+)\]: (.*)$')


class Server:
    def __init__(self, version, workdir, java='java', memory='1G'):
        self.version, self.dir = version, workdir
        self.lines, self.cond = [], threading.Condition()
        self.mark = 0
        os.makedirs(os.path.join(workdir, 'world', 'datapacks'), exist_ok=True)
        open(os.path.join(workdir, 'eula.txt'), 'w').write('eula=true\n')
        open(os.path.join(workdir, 'server.properties'), 'w').write('\n'.join([
            'level-type=minecraft\\:flat', 'generate-structures=false', 'level-seed=1', 'online-mode=false',
            'spawn-protection=0', 'view-distance=3', 'simulation-distance=3', 'server-port=0', 'query.port=0',
            'enable-rcon=false', 'sync-chunk-writes=false', 'max-tick-time=-1', 'spawn-monsters=false',
            'spawn-animals=false', 'spawn-npcs=false', 'difficulty=peaceful', 'log-ips=false',
            'pause-when-empty-seconds=-1',                 # 1.21.2+ stops ticking after 60 s without players
        ]) + '\n')
        jar = server_jar(version)
        self.proc = subprocess.Popen([java, f'-Xmx{memory}', '-jar', jar, 'nogui'], cwd=workdir,
                                     stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                     text=True, encoding='utf-8', errors='replace', bufsize=1)
        threading.Thread(target=self._read, daemon=True).start()
        self.wait_for(r'Done \(', 0, timeout=300)

    def _read(self):
        for raw in self.proc.stdout:
            with self.cond:
                self.lines.append(raw.rstrip('\n'))
                self.cond.notify_all()
        with self.cond:
            self.lines.append('<<server exited>>')
            self.cond.notify_all()

    def wait_for(self, pattern, start, timeout=60):
        """Index of the first log line at or after `start` matching pattern."""
        rx = re.compile(pattern)
        end = time.time() + timeout
        with self.cond:
            i = start
            while True:
                while i < len(self.lines):
                    if rx.search(self.lines[i]):
                        return i
                    if self.lines[i] == '<<server exited>>':
                        raise RuntimeError(f'{self.version}: the server stopped:\n' + '\n'.join(self.lines[-30:]))
                    i += 1
                left = end - time.time()
                if left <= 0:
                    raise TimeoutError(f'{self.version}: timed out waiting for {pattern!r}:\n' + '\n'.join(self.lines[-20:]))
                self.cond.wait(left)

    def send(self, command):
        self.proc.stdin.write(command + '\n')
        self.proc.stdin.flush()

    def run(self, command, timeout=60):
        """Runs a console command and returns the messages it logged (commands run in order, so a
        `say` marker after it shows where its output ends)."""
        self.mark += 1
        start = len(self.lines)
        self.send(command)
        self.send(f'say MCSMARK{self.mark}')
        end = self.wait_for(rf'\bMCSMARK{self.mark}\b', start, timeout)
        return [m.group(3) if (m := LOG_LINE.match(l)) else l for l in self.lines[start:end]]

    def enabled_packs(self):
        out = ' '.join(self.run('datapack list enabled'))
        return set(re.findall(r'\[(file/[^\]\s]+)', out))

    def wait_packs(self, cond, timeout=60):
        end = time.time() + timeout
        while not cond(self.enabled_packs()):
            if time.time() > end:
                raise TimeoutError(f'{self.version}: data pack reload did not finish')
            time.sleep(0.2)
        self.run('say reload done')      # one more round trip: the reload's main-thread work is finished

    def scores(self):
        """{(holder, objective): value} for every non-entity score holder."""
        out = ' '.join(self.run('scoreboard players list'))
        m = re.search(r'There (?:are|is) \d+ tracked [^:]*:\s*(.*)$', out)
        holders = [h.strip() for h in m.group(1).split(',')] if m else []
        result = {}
        for h in holders:
            if not h or re.fullmatch(r'[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}', h):
                continue
            for line in self.run(f'scoreboard players list {h}'):
                for obj, val in re.findall(r'\[([^\]]+)\]: (-?\d+)', line):
                    result[(h, obj)] = int(val)
        return result

    def stop(self):
        try:
            self.send('stop')
            self.proc.wait(60)
        except Exception:
            self.proc.kill()


def problems(lines):
    """ERROR/WARN messages (with their continuation lines) that are not vanilla noise."""
    out, keep = [], False
    for line in lines:
        m = LOG_LINE.match(line)
        if m:
            keep = m.group(2) in ('ERROR', 'WARN') and not any(re.search(p, m.group(3)) for p in IGNORED)
            if keep:
                out.append(f'{m.group(2)}: {m.group(3)}')
        elif keep and line.strip() and not line.startswith('\tat ') and len(out[-1]) < 2000:
            out[-1] += '\n    ' + line.strip()
    return out


def run_pack(server, pack_dir, ticks):
    """Enables one pack, runs it for `ticks` ticks, returns (problems, scores) and disables it again.
    Like in the game, the first tick runs the load functions and then the tick functions."""
    name = 'mcs_' + re.sub(r'[^a-zA-Z0-9_]', '_', os.path.basename(os.path.normpath(pack_dir)))
    dest = os.path.join(server.dir, 'world', 'datapacks', name)
    shutil.copytree(pack_dir, dest)
    start = len(server.lines)
    frozen = has_tick_command(server.version)
    if frozen:
        server.run('tick freeze')
    server.send('reload')                       # /reload enables newly found packs
    server.wait_packs(lambda p: f'file/{name}' in p)
    if frozen:
        ticks = max(ticks, 1)                  # a frozen server runs the load functions on the first stepped tick
        server.run(f'tick step {ticks}')
        time.sleep(ticks / 20 + 0.5)
        # stepping runs at 20 TPS; wait until the step counter has run out
        for _ in range(200):
            if not re.search(r'[Ss]tep', ' '.join(server.run('tick query'))):
                break
            time.sleep(0.2)
    else:
        time.sleep(ticks / 20 + 1.0)
    found = problems(server.lines[start:])
    scores = server.scores()
    server.send(f'datapack disable "file/{name}"')
    server.wait_packs(lambda p: f'file/{name}' not in p)
    if frozen:
        server.run('tick unfreeze')
    shutil.rmtree(dest, ignore_errors=True)
    return found, scores


def main():
    ap = argparse.ArgumentParser(description='Run compiled MC# datapacks on real Minecraft servers.')
    ap.add_argument('packs', nargs='+', help='datapack folders')
    ap.add_argument('--versions', help='comma-separated Minecraft versions (default: the newest of every pack format)')
    ap.add_argument('--ticks', type=int, default=40)
    ap.add_argument('--keep', action='store_true', help='keep the server folders')
    a = ap.parse_args()
    bad = False
    for v in (a.versions.split(',') if a.versions else default_versions()):
        work = tempfile.mkdtemp(prefix=f'mcs-real-{v}-')
        print(f'== {v}')
        server = Server(v, work)
        try:
            for p in a.packs:
                found, scores = run_pack(server, p, a.ticks)
                bad |= bool(found)
                print(f'  {os.path.basename(os.path.normpath(p))}: ' + ('ok' if not found else f'{len(found)} problem(s)'))
                for f in found:
                    print('    ' + f.replace('\n', '\n    '))
        finally:
            server.stop()
            if not a.keep:
                shutil.rmtree(work, ignore_errors=True)
    sys.exit(1 if bad else 0)


if __name__ == '__main__':
    main()
