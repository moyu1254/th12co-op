"""Run destructive native fixtures sequentially on prepared isolated copies.

Build with build.ps1 -NativeOnly -SmokeTest first. The smoke DLL and source
hashes are recorded; a successful run is not evidence for other binaries.
"""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent.parent


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def run_owned(command, log, timeout=300):
    # Leave the parent alive until its owned descendants have been terminated.
    # subprocess.run(timeout=...) kills just Python, bypassing Game.close().
    process = subprocess.Popen(command, cwd=ROOT, stdout=log, stderr=subprocess.STDOUT,
                               creationflags=subprocess.CREATE_NO_WINDOW)
    try:
        return process.wait(timeout=timeout)
    except BaseException:
        if process.poll() is None:
            subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                           timeout=15, check=True, creationflags=subprocess.CREATE_NO_WINDOW)
            process.wait(timeout=15)
        raise


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--versions', default='retail-1.00b')
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--case', action='append', help='family:case:shot; repeat to select cases')
    args = parser.parse_args()
    versions = args.versions.split(',')
    if not set(versions) <= {'retail-1.00a', 'retail-1.00b', 'steam-1.00b'}:
        parser.error('Unknown game profile')
    cases = ([tuple(map(int, item.split(':'))) for item in args.case] if args.case else
             [(1, case, 0) for case in range(6)] +
             [(1, case, shot) for case in (2, 4) for shot in range(1, 6)] +
             [(1, 6, 1), (1, 7, 0)] + [(3, case, 0) for case in range(1, 20)] +
             [(2, case, 0) for case in range(101, 106)] +
             [(1, case, shot) for case in range(8, 12) for shot in range(6)] +
             [(1, 12, 0)] + [(5, case, 0) for case in range(1, 8)])
    binary = ROOT / 'dist/th12coop-smoke.dll'
    binary_hash = sha(binary)
    sources = {str(p.relative_to(ROOT)).replace('\\', '/'): sha(p)
               for p in (ROOT / 'src/native').iterdir() if p.suffix in ('.h', '.cpp')}
    rows = []
    args.out.mkdir(parents=True, exist_ok=True)
    for version in versions:
        output = args.out / version
        output.mkdir(parents=True, exist_ok=True)
        for family, case, shot in cases:
            if sha(binary) != binary_hash:
                raise RuntimeError('Smoke DLL changed during verification')
            name = f'family-{family}-case-{case}-shot-{shot}'
            record = output / (name + '.json')
            if record.exists():
                raise FileExistsError(f'Keep previous evidence; choose a new output folder: {record}')
            command = [sys.executable, str(ROOT / 'scripts/test_cooperative_rules.py'),
                       '--game', str(ROOT / f'game-test-{version}-smoke'),
                       '--out', str(output), '--family', str(family),
                       '--case', str(case), '--shot', str(shot)]
            with (output / (name + '.log')).open('w', encoding='utf8') as log:
                try:
                    exit_code = run_owned(command, log)
                except subprocess.TimeoutExpired:
                    exit_code = -1
            report = json.loads(record.read_text()) if record.exists() else {}
            row = {'version': version, 'family': family, 'case': case, 'shot': shot,
                   'passed': exit_code == 0 and report.get('passed', False),
                   'failure_mask': report.get('failure_mask')}
            rows.append(row)
            print(json.dumps(row), flush=True)
    report = {'passed': all(row['passed'] for row in rows), 'total': len(rows),
              'smoke_sha256': binary_hash, 'native_source_sha256': sources, 'cases': rows}
    (args.out / 'summary.json').write_text(json.dumps(report, indent=2), encoding='utf8')
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
