#!/usr/bin/env python3
"""Runs the MC# runtime (mcs.jar) on real Fabric / NeoForge / Paper servers and checks every feature.

  runtime/test/realtest.py [--loaders fabric,neoforge,paper] [--versions 26.1.2,26.2,26.3] [--keep]

Servers are downloaded once into ~/.cache/mcs/runtime-servers. For each loader and version, a fresh world gets
two test datapacks (runtime/test/rtcheck.mcs: "runtime": "optional", rtsudo.mcs: "required") and the console
checks: fast paths give the same results as vanilla (tools/mcsim.py), sudo, shortcuts, the debugger, trace /
profile and the interpreter. Build the jars first (runtime/build.sh).
"""
import argparse
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import mcsim  # noqa: E402

CACHE = os.path.join(os.path.expanduser('~'), '.cache', 'mcs', 'runtime-servers')
DLL = os.path.join(ROOT, 'src', 'Mcs.Compiler', 'bin', 'Release', 'net8.0', 'mcs.dll')
FORMATS = {'26.1': 101, '26.1.1': 101, '26.1.2': 101, '26.2': 107, '26.3': 121}
JARS = {'fabric': 'runtime/fabric/build/libs/mcs-fabric-1.0.0.jar',
        'neoforge': 'runtime/neoforge/build/libs/mcs-neoforge-1.0.0.jar',
        'paper': 'runtime/paper/build/libs/mcs-paper-1.0.0.jar'}


def fetch(url, dest):
    if os.path.exists(dest):
        return dest
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    print(f'  downloading {url}')
    req = urllib.request.Request(url, headers={'User-Agent': 'mcs-runtime-test'})
    with urllib.request.urlopen(req, timeout=120) as r, open(dest + '.part', 'wb') as f:
        shutil.copyfileobj(r, f)
    os.replace(dest + '.part', dest)
    return dest


def get_json(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'mcs-runtime-test'})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def get_text(url):
    req = urllib.request.Request(url, headers={'User-Agent': 'mcs-runtime-test'})
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read().decode()


# ---------------------------------------------------------------- server installs

def install_fabric(v, d):
    """→ (command, mods dir)"""
    loader = next(x['version'] for x in get_json('https://meta.fabricmc.net/v2/versions/loader') if x['stable'])
    installer = next(x['version'] for x in get_json('https://meta.fabricmc.net/v2/versions/installer') if x['stable'])
    jar = fetch(f'https://meta.fabricmc.net/v2/versions/loader/{v}/{loader}/{installer}/server/jar', os.path.join(d, 'fabric-server-launch.jar'))
    meta = get_text('https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/maven-metadata.xml')
    api = [x for x in re.findall(r'<version>([^<]+)</version>', meta) if x.endswith('+' + v)]
    if not api:
        api = [x for x in re.findall(r'<version>([^<]+)</version>', meta) if x.split('+')[1] == '.'.join(v.split('.')[:2])]
    api = api[-1]
    os.makedirs(os.path.join(d, 'mods'), exist_ok=True)
    fetch(f'https://maven.fabricmc.net/net/fabricmc/fabric-api/fabric-api/{api}/fabric-api-{api}.jar', os.path.join(d, 'mods', f'fabric-api-{api}.jar'))
    return ['java', '-Xmx2G', '-jar', jar, 'nogui'], os.path.join(d, 'mods')


def install_neoforge(v, d):
    versions = get_json('https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge')['versions']
    prefix = v + '.' if v.count('.') == 2 else v + '.0.'
    nv = [x for x in versions if x.startswith(prefix) and not re.search(r'beta|alpha', x)][-1]
    args = os.path.join(d, 'libraries', 'net', 'neoforged', 'neoforge', nv, 'unix_args.txt')
    if not os.path.exists(args):
        inst = fetch(f'https://maven.neoforged.net/releases/net/neoforged/neoforge/{nv}/neoforge-{nv}-installer.jar', os.path.join(d, 'installer.jar'))
        print(f'  installing NeoForge {nv}')
        subprocess.run(['java', '-jar', inst, '--installServer', d], cwd=d, check=True, capture_output=True)
    os.makedirs(os.path.join(d, 'mods'), exist_ok=True)
    return ['java', '-Xmx2G', '@' + args, 'nogui'], os.path.join(d, 'mods')


def install_paper(v, d):
    builds = get_json(f'https://fill.papermc.io/v3/projects/paper/versions/{v}/builds')
    stable = [b for b in builds if b.get('channel') == 'STABLE'] or builds
    b = max(stable, key=lambda x: x['id'])
    jar = fetch(b['downloads']['server:default']['url'], os.path.join(d, f'paper-{v}-{b["id"]}.jar'))
    os.makedirs(os.path.join(d, 'plugins'), exist_ok=True)
    return ['java', '-Xmx2G', '-jar', jar, '--nogui'], os.path.join(d, 'plugins')


INSTALL = {'fabric': install_fabric, 'neoforge': install_neoforge, 'paper': install_paper}


# ---------------------------------------------------------------- a running server

class Server:
    def __init__(self, cmd, cwd):
        self.lines = []
        self.lock = threading.Lock()
        self.p = subprocess.Popen(cmd, cwd=cwd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, bufsize=1)
        threading.Thread(target=self._read, daemon=True).start()

    def _read(self):
        for line in self.p.stdout:
            with self.lock:
                self.lines.append(line.rstrip('\n'))

    def mark(self):
        with self.lock:
            return len(self.lines)

    def since(self, mark):
        with self.lock:
            return self.lines[mark:]

    def wait(self, pattern, mark=0, timeout=60):
        rx = re.compile(pattern)
        end = time.time() + timeout
        while time.time() < end:
            for line in self.since(mark):
                if rx.search(line):
                    return line
            if self.p.poll() is not None:
                raise RuntimeError('server exited:\n' + '\n'.join(self.lines[-40:]))
            time.sleep(0.1)
        raise TimeoutError(f'no line matching {pattern!r} within {timeout}s; last lines:\n' + '\n'.join(self.since(mark)[-25:]))

    def send(self, cmd):
        m = self.mark()
        self.p.stdin.write(cmd + '\n')
        self.p.stdin.flush()
        return m

    def stop(self):
        try:
            self.send('stop')
            self.p.wait(timeout=60)
        except Exception:
            self.p.kill()


# ---------------------------------------------------------------- checks

def fmt(raw, typ):
    """Symbols.format of a raw score"""
    if typ == 'bool':
        return 'True' if raw else 'False'
    if typ in ('q16', 'milli'):
        d = raw / (65536 if typ == 'q16' else 1000)
        if d == int(d):
            return str(int(d))
        return ('%.5f' % d).rstrip('0').rstrip('.')
    return str(raw)


def expected_statics(pack, fmt_):
    _, sim = mcsim.simulate_state(pack, fmt_, 2, (), False)
    sym = json.load(open(os.path.join(pack, 'data', 'rtcheck', 'mcs', 'symbols.json')))
    out = {}
    for s in sym['statics']:
        if 'holder' not in s or s['name'] in ('counter', 'avail'):
            continue
        out[s['name']] = fmt(sim.scores.get((s['holder'], s['objective']), 0), s['type'])
    out['avail'] = 'True'          # the mod is installed
    return out


def run_checks(srv, v, packs):
    results = []

    def check(name, ok, detail=''):
        results.append((name, ok, detail))
        print(f'    {"ok  " if ok else "FAIL"} {name}' + (f'  {detail}' if detail and not ok else ''))

    def cmd(c, pattern, timeout=15):
        m = srv.send(c)
        try:
            return srv.wait(pattern, m, timeout)
        except TimeoutError as e:
            return e

    r = cmd('mcs version', r'MC# runtime \S+ \((Fabric|NeoForge|Paper)\), protocol 1')
    check('mcs version', isinstance(r, str), str(r))

    # fast paths: the datapack's statics equal the vanilla results
    exp = expected_statics(packs['rtcheck'], FORMATS[v])
    m = srv.send('mcs vars rtcheck')
    time.sleep(1.5)
    got = {}
    for line in srv.since(m):
        mm = re.search(r'\s(\w+) : (\w+) = (.*)$', line)
        if mm:
            got[mm.group(1)] = mm.group(3).strip()
    bad = {k: (got.get(k), e) for k, e in exp.items() if got.get(k) != e}
    check('fast paths = vanilla results (mcs vars)', not bad, f'(got, expected): {bad}')

    # sudo: rtsudo's Main ran permission level 3 commands
    banned = os.path.join(srv.cwd, 'banned-ips.json')
    try:
        ok = '10.11.12.13' in open(banned).read()
    except OSError:
        ok = False
    check('Runtime.Sudo (ban-ip from a function)', ok)
    check('Runtime.Sudo with a runtime value', any('sudo-7' in l for l in srv.lines))
    r = cmd('mcs sudo say should-not-run', r'Unknown or incomplete command|Incorrect argument|Unknown command')
    check('mcs sudo is not available outside functions', isinstance(r, str) and not any(re.search(r'\[Server\] should-not-run', l) for l in srv.lines), str(r))

    # shortcuts
    r = cmd('rtcheck:hello', r'hello-shortcut')
    check('/rtcheck:hello shortcut', isinstance(r, str), str(r))
    r = cmd('rtcheck:__load', r'Unknown or incomplete command|Unknown command')
    check('/rtcheck:__load is hidden', isinstance(r, str), str(r))

    # debugger: watch
    r = cmd('mcs watch rtcheck counter', r'\[watch\] rtcheck:counter = \d+')
    check('mcs watch', isinstance(r, str), str(r))
    m = srv.mark()
    try:
        srv.wait(r'\[watch\] rtcheck:counter \d+ → \d+', m, 5)
        check('watch reports changes', True)
    except TimeoutError as e:
        check('watch reports changes', False, str(e))
    srv.send('mcs unwatch')
    r = cmd('mcs set rtcheck sumI 123', r'rtcheck:sumI : int = 123')
    check('mcs set', isinstance(r, str), str(r))

    # trace / profile
    m = srv.send('mcs trace start rtcheck')
    try:
        srv.wait(r'Tick\s+\(rtcheck:tick\)', m, 5)
        check('mcs trace', True)
    except TimeoutError as e:
        check('mcs trace', False, str(e))
    cmd('mcs trace stop', r'\[trace\] stopped')
    srv.send('mcs profile start rtcheck')
    time.sleep(1.5)
    m = srv.send('mcs profile stop')
    try:
        srv.wait(r'\[profile\] \d+ functions', m, 5)
        srv.wait(r'\d+\.\d+\s+\d+\s+\d+\s+Tick', m, 5)
        check('mcs profile', True)
    except TimeoutError as e:
        check('mcs profile', False, str(e))

    # interpreter, linked to the running pack
    cmd('mcsi use rtcheck', r'mcsi: linked to rtcheck')
    m = srv.send('mcsi run int x = 6 * 7; print($"x={x}"); print($"add={Add(2, 3)} half={Half(5)}"); '
                 'for (int i = 0; i < 3; i++) sumI += i; print($"sumI={sumI}"); var t = idx > 5 ? "yes" : "no"; t')
    try:
        srv.wait(r'x=42', m, 10)
        srv.wait(r'add=5 half=2\.5', m, 5)
        srv.wait(r'sumI=126', m, 5)
        srv.wait(r'= yes', m, 5)
        check('mcsi run (locals, pack statics, pack methods)', True)
    except TimeoutError as e:
        check('mcsi run (locals, pack statics, pack methods)', False, str(e))
    r = cmd('mcsi run /say from-mcsi', r'from-mcsi')
    check('mcsi command line', isinstance(r, str), str(r))
    r = cmd('mcsi run int y = 1 / 0;', r'mcsi: .*division by zero')
    check('mcsi errors', isinstance(r, str), str(r))

    errs = [l for l in srv.lines if re.search(r'(ERROR|Exception)', l) and re.search(r'mcs|MC#', l)]
    check('no runtime errors in the log', not errs, '\n'.join(errs[:10]))
    return results


def build_packs(tmp):
    packs = {}
    for name, extra in (('rtcheck', []), ('rtsudo', ['--runtime', 'required'])):
        out = os.path.join(tmp, name)
        r = subprocess.run(['dotnet', DLL, os.path.join(HERE, name + '.mcs'), '-o', out, '-n', name, '--mc', '26.1-26.3'] + extra,
                           capture_output=True, text=True, env=dict(os.environ, PATH=os.path.expanduser('~/.dotnet') + os.pathsep + os.environ['PATH']))
        if r.returncode != 0:
            sys.exit(r.stdout + r.stderr)
        packs[name] = out
    return packs


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--loaders', default='fabric,neoforge,paper')
    ap.add_argument('--versions', default='26.1.2,26.2,26.3')
    ap.add_argument('--keep', action='store_true', help='keep the server folders of the last run')
    a = ap.parse_args()
    tmp = os.path.join(CACHE, '_packs')
    shutil.rmtree(tmp, ignore_errors=True)
    packs = build_packs(tmp)
    summary = []
    for loader in a.loaders.split(','):
        jar = os.path.join(ROOT, JARS[loader])
        if not os.path.exists(jar):
            sys.exit(f'{jar} is missing: build it first (runtime/build.sh)')
        for v in a.versions.split(','):
            print(f'== {loader} {v}')
            d = os.path.join(CACHE, f'{loader}-{v}')
            os.makedirs(d, exist_ok=True)
            cmdline, mods = INSTALL[loader](v, d)
            for f in os.listdir(mods):
                if f.startswith('mcs-'):
                    os.remove(os.path.join(mods, f))
            shutil.copy(jar, mods)
            for f in ('world', 'banned-ips.json', 'config/mcs-runtime.properties', 'plugins/MCSRuntime'):
                p = os.path.join(d, f)
                shutil.rmtree(p, ignore_errors=True) if os.path.isdir(p) else (os.path.exists(p) and os.remove(p))
            with open(os.path.join(d, 'eula.txt'), 'w') as f:
                f.write('eula=true\n')
            with socket.socket() as sock:          # a free port, so several servers can run at once
                sock.bind(('127.0.0.1', 0))
                port = sock.getsockname()[1]
            with open(os.path.join(d, 'server.properties'), 'w') as f:
                f.write(f'server-port={port}\n')
                f.write('online-mode=false\nlevel-type=minecraft\\:flat\nspawn-protection=0\nmax-tick-time=-1\n'
                        'generate-structures=false\nsync-chunk-writes=false\nenable-command-block=true\n')
            dp = os.path.join(d, 'world', 'datapacks')
            os.makedirs(dp, exist_ok=True)
            for name, p in packs.items():
                shutil.copytree(p, os.path.join(dp, name))
            srv = Server(cmdline, d)
            srv.cwd = d
            try:
                srv.wait(r'Done \(', 0, 600)
                srv.wait(r'rtcheck-done', 0, 30)
                res = run_checks(srv, v, packs)
            except Exception as e:
                res = [('server', False, str(e))]
                print(f'    FAIL {e}')
            finally:
                srv.stop()
                with open(os.path.join(d, 'last.log'), 'w') as f:
                    f.write('\n'.join(srv.lines) + '\n')
            failed = [r for r in res if not r[1]]
            summary.append((loader, v, len(res) - len(failed), len(res)))
    print('\nsummary')
    for loader, v, ok, n in summary:
        print(f'  {loader:9} {v:8} {ok}/{n}')
    sys.exit(0 if all(ok == n for *_, ok, n in summary) else 1)


if __name__ == '__main__':
    main()
