#!/usr/bin/env python3
"""MC# test runner.

  tests/run.py              run everything
  tests/run.py arrays wait  run the cases whose name contains "arrays" or "wait"
  tests/run.py -u           rewrite the expected .out files from the current results (review the diff!)

Kinds of tests
  tests/cases/<name>.mcs    compiled for the whole default version range, then simulated (tools/mcsim.py) on
                            every version layer the build produced. The output must match <name>.out.
                            If layers differ, .out holds one "=== format N ===" section per layer.
  tests/errors/<name>.mcs   must fail to compile; every `// @error <text>` line must appear in the errors.
  examples/*.mcs            must compile.

Directives (comment lines anywhere in a case):
  // @mc 1.20.2-26.3        version range to build (default: the compiler's default range)
  // @ticks 100             ticks to simulate (default: until nothing is scheduled; 20 with #tick functions)
  // @do 5 die Steve        scripted player action at a tick (see tools/mcsim.py); repeatable
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

DLL = os.path.join(ROOT, 'src', 'Mcs.Compiler', 'bin', 'Release', 'net8.0', 'mcs.dll')
ENV = dict(os.environ, PATH=os.path.expanduser('~/.dotnet') + os.pathsep + os.environ.get('PATH', ''), DOTNET_CLI_TELEMETRY_OPTOUT='1')
GREEN, RED, DIM, RESET = ('\033[32m', '\033[31m', '\033[2m', '\033[0m') if sys.stdout.isatty() else ('', '', '', '')


def build_compiler():
    r = subprocess.run(['dotnet', 'build', os.path.join(ROOT, 'src', 'Mcs.Compiler'), '-c', 'Release', '-v', 'q', '-nologo'],
                       env=ENV, capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout + r.stderr)
        sys.exit('compiler build failed')


def compile_mcs(src, out_dir, mc=None):
    ns = re.sub(r'[^a-z0-9_]', '_', os.path.splitext(os.path.basename(src))[0].lower())
    args = ['dotnet', DLL, src, '-o', out_dir, '-n', ns] + (['--mc', mc] if mc else [])
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
    ok, log = compile_mcs(path, out_dir, d.get('mc'))
    if not ok:
        return 'compile error:\n' + log
    ticks = int(d['ticks']) if 'ticks' in d else None
    per_format = {}
    for fmt in mcsim.Pack(out_dir).formats:
        try:
            per_format[fmt] = mcsim.simulate(out_dir, fmt, ticks, actions(text))
        except Exception as e:  # a simulator crash is a test failure, not a runner crash
            per_format[fmt] = [f'!! simulator crashed: {type(e).__name__}: {e}']
    outputs = list(per_format.values())
    if all(o == outputs[0] for o in outputs):
        return '\n'.join(outputs[0]) + '\n'
    return ''.join(f'=== format {f} ===\n' + '\n'.join(o) + '\n' for f, o in per_format.items())


def run_error(path, tmp):
    d_text = directives(path)[1]
    wanted = re.findall(r'^\s*//\s*@error\s+(.*?)\s*$', d_text, re.M)
    ok, log = compile_mcs(path, os.path.join(tmp, os.path.basename(path)))
    if ok:
        return 'expected a compile error, but the build succeeded'
    missing = [w for w in wanted if w not in log]
    if not wanted:
        return 'no // @error lines in the test'
    return None if not missing else 'missing errors:\n  ' + '\n  '.join(missing) + '\ncompiler output:\n' + log


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('-')]
    update = '-u' in sys.argv or '--update' in sys.argv
    pick = (lambda name: any(a in name for a in args)) if args else (lambda name: True)

    def listdir(d, ext='.mcs'):
        full = os.path.join(ROOT, d)
        return sorted(os.path.join(full, f) for f in os.listdir(full) if f.endswith(ext) and pick(f)) if os.path.isdir(full) else []

    cases, errors, examples = listdir('tests/cases'), listdir('tests/errors'), listdir('examples')
    print(f'{DIM}building compiler…{RESET}')
    build_compiler()

    failed = []
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

    total = len(cases) + len(errors) + len(examples)
    print(f'\n{total - len(failed)}/{total} passed' + (f' — {RED}{len(failed)} failed{RESET}' if failed else ''))
    sys.exit(1 if failed else 0)


if __name__ == '__main__':
    main()
