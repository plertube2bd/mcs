#!/usr/bin/env python3
"""MC# test runner.

  tests/run.py              run everything
  tests/run.py arrays wait  run the cases whose name contains "arrays" or "wait"
  tests/run.py -u           rewrite the expected .out files from the current results (review the diff!)
  tests/run.py --real       also run the cases and examples on real Minecraft servers (tools/mcreal.py):
                            one server per pack format (or --versions=1.20.1,1.21.11), --servers=N at a time.
                            Fails on any ERROR/WARN the server logs for a pack, and on scores that differ
                            from the simulator (cases without scripted players only).

Kinds of tests
  tests/cases/<name>.mcs    compiled for the whole default version range, then simulated (tools/mcsim.py) on
                            every version layer the build produced. The output must match <name>.out.
                            If layers differ, .out holds one "=== format N ===" section per layer.
  tests/errors/<name>.mcs   must fail to compile; every `// @error <text>` line must appear in the errors.
  examples/*.mcs            must compile.
  docs/ko/tutorial/examples/<name>.mcs   tutorial programs: like tests/cases (the tutorial shows <name>.out).
  docs/api.md               must equal `mcs --api-docs` (-u regenerates it).

Directives (comment lines anywhere in a case):
  // @mc 1.20.2-26.3        version range to build (default: the compiler's default range)
  // @ticks 100             ticks to simulate (default: until nothing is scheduled; 20 with #tick functions)
  // @do 5 die Steve        scripted player action at a tick (see tools/mcsim.py); repeatable
  // @warning <text>        the build must print a warning containing <text>; repeatable
  // @runtime               simulate a server with the MC# runtime mod (mcs.jar) installed
  // @args --runtime required   extra compiler options
  // @real load             on real servers, only check that the pack loads (for world state the simulator lacks)
"""
import concurrent.futures
import difflib
import os
import re
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, 'tools'))
import mcsim  # noqa: E402
import mcreal  # noqa: E402

DLL = os.path.join(ROOT, 'src', 'Mcs.Compiler', 'bin', 'Release', 'net8.0', 'mcs.dll')
ENV = dict(os.environ, PATH=os.path.expanduser('~/.dotnet') + os.pathsep + os.environ.get('PATH', ''), DOTNET_CLI_TELEMETRY_OPTOUT='1')
GREEN, RED, DIM, RESET = ('\033[32m', '\033[31m', '\033[2m', '\033[0m') if sys.stdout.isatty() else ('', '', '', '')


def build_compiler():
    r = subprocess.run(['dotnet', 'build', os.path.join(ROOT, 'src', 'Mcs.Compiler'), '-c', 'Release', '-v', 'q', '-nologo'],
                       env=ENV, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        sys.exit('compiler build failed')


def compile_mcs(src, out_dir, mc=None, extra=''):
    ns = re.sub(r'[^a-z0-9_]', '_', os.path.splitext(os.path.basename(src))[0].lower())
    args = ['dotnet', DLL, src, '-o', out_dir, '-n', ns] + (['--mc', mc] if mc else []) + extra.split()
    r = subprocess.run(args, env=ENV, capture_output=True, text=True, cwd=ROOT)
    return r.returncode == 0, (r.stdout + r.stderr).replace(ROOT + os.sep, '')


def directives(path):
    text = open(path, encoding='utf-8').read()
    return dict(re.findall(r'^\s*//\s*@(\w+)\s+(.*?)\s*$', text, re.M)), text


def actions(text):
    return mcsim.parse_actions(re.findall(r'^\s*//\s*@do\s+(.*?)\s*$', text, re.M))


def run_case(path, tmp):
    d, text = directives(path)
    out_dir = os.path.join(tmp, os.path.basename(path))
    ok, log = compile_mcs(path, out_dir, d.get('mc'), d.get('args', ''))
    if not ok:
        return 'compile error:\n' + log
    missing = [w for w in re.findall(r'^\s*//\s*@warning\s+(.*?)\s*$', text, re.M) if w not in log]
    if missing:
        return 'missing warnings:\n  ' + '\n  '.join(missing) + '\ncompiler output:\n' + log
    ticks = int(d['ticks']) if 'ticks' in d else None
    per_format = {}
    for fmt in mcsim.Pack(out_dir).formats:
        try:
            per_format[fmt] = mcsim.simulate(out_dir, fmt, ticks, actions(text), 'runtime' in d or re.search(r'^\s*//\s*@runtime\s*$', text, re.M) is not None)
        except Exception as e:  # a simulator crash is a test failure, not a runner crash
            per_format[fmt] = [f'!! simulator crashed: {type(e).__name__}: {e}']
    outputs = list(per_format.values())
    if all(o == outputs[0] for o in outputs):
        return '\n'.join(outputs[0]) + '\n'
    return ''.join(f'=== format {f} ===\n' + '\n'.join(o) + '\n' for f, o in per_format.items())


def run_error(path, tmp):
    d_text = directives(path)[1]
    wanted = re.findall(r'^\s*//\s*@error\s+(.*?)\s*$', d_text, re.M)
    ok, log = compile_mcs(path, os.path.join(tmp, os.path.basename(path)), directives(path)[0].get('mc'), directives(path)[0].get('args', ''))
    if ok:
        return 'expected a compile error, but the build succeeded'
    missing = [w for w in wanted if w not in log]
    if not wanted:
        return 'no // @error lines in the test'
    return None if not missing else 'missing errors:\n  ' + '\n  '.join(missing) + '\ncompiler output:\n' + log


def fake_player(holder):
    """Scores compared with the real server: fake players ($x, #x) only — not entities or player names."""
    return holder[:1] in '$#' and not re.fullmatch(r'#e\d+', holder)


def real_scores(sim):
    return {k: v for k, v in sim.scores.items() if fake_player(k[0])}


def run_real_version(version, fmt, packs, log):
    """packs: [(name, out_dir, case text or None)] → list of failure messages."""
    import shutil
    failures = []
    work = tempfile.mkdtemp(prefix=f'mcs-real-{version}-')
    try:
        server = mcreal.Server(version, work)
    except Exception as e:
        shutil.rmtree(work, ignore_errors=True)
        return [f'{version}: server did not start: {e}']
    try:
        order = [v for v, _ in mcreal.versions_from_compiler()]
        for name, out_dir, text, mc in packs:
            if mc:                                   # the case's own version range (// @mc)
                lo, _, hi = mc.partition('-')
                if not order.index(lo.strip()) <= order.index(version) <= order.index((hi or lo).strip()):
                    continue
            sim = None
            ticks = 20
            if text is not None:
                d = dict(re.findall(r'^\s*//\s*@(\w+)\s+(.*?)\s*$', text, re.M))
                try:
                    _, sim = mcsim.simulate_state(out_dir, fmt, int(d['ticks']) if 'ticks' in d else None, actions(text))
                    ticks = sim.tick if sim.pack.tag('minecraft:tick') else sim.tick + 20   # idle packs: slack is harmless (chunk loading)
                except Exception as e:
                    failures.append(f'{version} {name}: simulator crashed: {e}')
                    continue
            try:
                found, scores = mcreal.run_pack(server, out_dir, ticks)
            except Exception as e:
                failures.append(f'{version} {name}: {e}')
                break
            msgs = list(found)
            comparable = (sim is not None and not actions(text) and (text is None or not re.search(r'^\s*//\s*@real\s+load\b', text, re.M))
                          and (mcreal.has_tick_command(version) or not sim.pack.tag('minecraft:tick')))
            if comparable:
                want = real_scores(sim)
                objectives = {o for _, o in want} | set(sim.objectives)
                got = {k: v for k, v in scores.items() if k[1] in objectives and fake_player(k[0])}
                for k in sorted(set(want) | set(got)):
                    if want.get(k) != got.get(k):
                        msgs.append(f'score {k[0]} {k[1]}: simulator {want.get(k, "unset")}, Minecraft {got.get(k, "unset")}')
            status = 'ok' if not msgs else f'{RED}FAIL{RESET}'
            log(f'{status} {version:8} {name}' + ('' if comparable or sim is None else f' {DIM}(load check only){RESET}'))
            if msgs:
                shown = msgs if len(msgs) <= 15 else msgs[:15] + [f'… and {len(msgs) - 15} more']
                failures.append(f'{version} {name}:\n      ' + '\n      '.join(m.replace('\n', '\n      ') for m in shown))
    finally:
        server.stop()
        shutil.rmtree(work, ignore_errors=True)
    return failures


def run_real(cases, examples, tmp, versions, servers):
    fmt_of = dict(mcreal.versions_from_compiler())
    packs = []
    for p in cases + examples:
        is_case = p in cases
        name = os.path.relpath(p, ROOT)
        out_dir = os.path.join(tmp, 'real', os.path.basename(p))
        d = directives(p)
        ok, log = compile_mcs(p, out_dir, d[0].get('mc') if is_case else None, d[0].get('args', '') if is_case else '')
        if ok:
            packs.append((name, out_dir, d[1] if is_case else None, d[0].get('mc') if is_case else None))
    print(f'\n{DIM}real servers: {", ".join(versions)} ({len(packs)} packs each){RESET}')
    for v in versions:                       # download first, one at a time
        mcreal.server_jar(v, log=lambda m: print(f'{DIM}{m}{RESET}'))
    lock = __import__('threading').Lock()

    def log(m):
        with lock:
            print(m, flush=True)
    failures = []
    with concurrent.futures.ThreadPoolExecutor(servers) as pool:
        for f in [pool.submit(run_real_version, v, fmt_of[v], packs, log) for v in versions]:
            failures += f.result()
    if failures:
        print(f'\n{RED}real-server problems:{RESET}\n  ' + '\n  '.join(failures))
    print(f'\nreal servers: {len(versions)} version(s), ' + (f'{RED}{len(failures)} problem(s){RESET}' if failures else f'{GREEN}all ok{RESET}'))
    return not failures


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    update = '-u' in sys.argv or '--update' in sys.argv
    pick = (lambda name: any(a in name for a in args)) if args else (lambda name: True)

    def listdir(d, ext='.mcs'):
        full = os.path.join(ROOT, d)
        return sorted(os.path.join(full, f) for f in os.listdir(full) if f.endswith(ext) and pick(f)) if os.path.isdir(full) else []

    cases, errors, examples = listdir('tests/cases') + listdir('docs/ko/tutorial/examples'), listdir('tests/errors'), listdir('examples')
    print(f'{DIM}building compiler…{RESET}')
    build_compiler()

    failed = []
    if not args or pick('api.md'):   # docs/api.md is generated from the compiler's declarations
        api = subprocess.run(['dotnet', DLL, '--api-docs'], env=ENV, capture_output=True, text=True).stdout
        api_path = os.path.join(ROOT, 'docs', 'api.md')
        current = open(api_path, encoding='utf-8').read() if os.path.exists(api_path) else None
        if api != current and update:
            open(api_path, 'w', encoding='utf-8').write(api)
            print(f'{GREEN}updated{RESET} docs/api.md')
        elif api != current:
            failed.append('docs/api.md')
            print(f'{RED}FAIL{RESET} docs/api.md is out of date: run tests/run.py -u (or mcs --api-docs > docs/api.md)')
        else:
            print(f'{GREEN}ok{RESET}   docs/api.md')
    with tempfile.TemporaryDirectory(prefix='mcs-test-') as tmp, concurrent.futures.ThreadPoolExecutor(os.cpu_count() or 4) as pool:
        jobs = [(p, 'case', pool.submit(run_case, p, tmp)) for p in cases]
        jobs += [(p, 'error', pool.submit(run_error, p, tmp)) for p in errors]
        jobs += [(p, 'example', pool.submit(lambda p=p: None if compile_mcs(p, os.path.join(tmp, 'ex_' + os.path.basename(p)))[0] else 'does not compile', p)) for p in examples]
        for path, kind, fut in jobs:
            rel = os.path.relpath(path, ROOT)
            result = fut.result()
            problem = None
            if kind == 'case':
                exp_path = path[:-4] + '.out'
                expected = open(exp_path, encoding='utf-8').read() if os.path.exists(exp_path) else None
                if update and result != expected:
                    open(exp_path, 'w', encoding='utf-8').write(result)
                    print(f'{GREEN}updated{RESET} {rel}')
                    continue
                if expected is None:
                    problem = f'no expected output ({os.path.relpath(exp_path, ROOT)}); run with -u to create it\n' + result
                elif result != expected:
                    problem = ''.join(difflib.unified_diff(expected.splitlines(True), result.splitlines(True), 'expected', 'actual'))
            else:
                problem = result
            if problem:
                failed.append(rel)
                print(f'{RED}FAIL{RESET} {rel}\n' + ''.join('     ' + l + '\n' for l in problem.rstrip('\n').split('\n')))
            else:
                print(f'{GREEN}ok{RESET}   {rel}')

        total = len(cases) + len(errors) + len(examples) + (1 if not args or pick('api.md') else 0)
        print(f'\n{total - len(failed)}/{total} passed' + (f' — {RED}{len(failed)} failed{RESET}' if failed else ''))

        if '--real' in sys.argv:
            opt = dict(a[2:].split('=', 1) for a in sys.argv[1:] if a.startswith('--') and '=' in a)
            versions = opt['versions'].split(',') if 'versions' in opt else mcreal.default_versions()
            if not run_real(cases, examples, tmp, versions, int(opt.get('servers', 3))):
                failed.append('real')
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
