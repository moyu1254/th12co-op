"""Run quiet launcher game tests sequentially and verify owned-process teardown."""
import argparse
import ctypes
from ctypes import wintypes
import hashlib
import json
from pathlib import Path
import subprocess
import time

ROOT = Path(__file__).resolve().parent.parent
KERNEL = ctypes.WinDLL('kernel32', use_last_error=True)
KERNEL.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
KERNEL.OpenProcess.restype = wintypes.HANDLE
KERNEL.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
KERNEL.CloseHandle.argtypes = [wintypes.HANDLE]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def preserved_files(folder):
    paths = [folder / 'th12.cfg', *folder.glob('score*.dat')]
    return {path.name: digest(path) if path.exists() else None for path in paths}


def exited(pid):
    process = KERNEL.OpenProcess(0x100000, False, pid)  # Synchronize only; never kill by PID.
    if not process:
        if ctypes.get_last_error() == 87:  # ERROR_INVALID_PARAMETER: PID no longer exists.
            return True
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        return KERNEL.WaitForSingleObject(process, 5000) == 0
    finally:
        KERNEL.CloseHandle(process)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--cases', default='lockstep,disconnect,offline-start,owner-exit')
    args = parser.parse_args()
    cases = args.cases.split(',')
    names = {'lockstep': 'lockstep-test-results.json', 'disconnect': 'disconnect-test-results.json',
             'offline-start': 'offline-start-results.json', 'owner-exit': 'owner-exit-test-startup.json'}
    if not cases or not set(cases) <= names.keys():
        parser.error('Unknown test case')
    folders = [args.game.resolve(), args.guest_game.resolve()]
    if any(path.parent != ROOT or not path.name.startswith('game-test-') for path in folders):
        parser.error('Explicit workspace game-test-* copies required')
    args.out.mkdir(parents=True, exist_ok=True)
    binary_hashes = {name: digest(ROOT / 'dist' / name) for name in
                     ('TH12Coop.exe', 'TH12Coop.dll', 'injector.exe', 'th12coop-native.dll', 'th12coop-quiet-test.dll')}
    rows = []
    for case in cases:
        if any(digest(ROOT / 'dist' / name) != expected for name, expected in binary_hashes.items()):
            raise RuntimeError('Launcher or native binary changed during verification')
        destination = args.out / f'{case}.json'
        if destination.exists():
            raise FileExistsError(f'Preserve earlier evidence: {destination}')
        before = [preserved_files(path) for path in folders]
        command = [str(ROOT / 'dist/TH12Coop.exe'), f'--{case}-test', '--game', str(folders[0])]
        report_path = ROOT / 'dist' / names[case]
        if case in ('lockstep', 'disconnect'):
            command += ['--guest-game', str(folders[1]), '--seconds', '60', '--p1', '1', '--p2', '4']
        elif case == 'owner-exit':
            report_path = args.out.resolve() / names[case]
            command += ['--report', str(report_path)]
        row = {'case': case, 'passed': False, 'binarySha256': binary_hashes}
        began = time.time()
        process = None
        try:
            process = subprocess.Popen(command, cwd=ROOT, creationflags=subprocess.CREATE_NO_WINDOW)
            row['exitCode'] = process.wait(timeout=210)
            if not report_path.exists() or report_path.stat().st_mtime < began:
                raise AssertionError('Launcher did not write a fresh report')
            report = json.loads(report_path.read_text(encoding='utf-8-sig'))
            row['report'] = report
            if case == 'owner-exit':
                row['ownedGameExitedAfterLauncher'] = bool(report.get('started')) and exited(report['gamePid'])
                row['passed'] = row['exitCode'] == 0 and row['ownedGameExitedAfterLauncher']
            else:
                row['passed'] = row['exitCode'] == 0 and report.get('passed') is True
        except Exception as error:
            row['error'] = str(error)
        finally:
            if process and process.poll() is None:
                # GameSession's kill-on-close job owns its game. Kill only the
                # launcher process created here; its teardown closes that job.
                process.kill()
                process.wait(timeout=15)
            row['sourceConfigurationsAndScoresPreserved'] = before == [preserved_files(path) for path in folders]
            row['passed'] &= row['sourceConfigurationsAndScoresPreserved']
            row['seconds'] = round(time.time() - began, 3)
            destination.write_text(json.dumps(row, indent=2), encoding='utf8')
            rows.append(row)
            print(json.dumps({'case': case, 'passed': row['passed'], 'seconds': row['seconds'],
                              'error': row.get('error')}), flush=True)
        if not row['passed']:
            break
    summary = {'passed': len(rows) == len(cases) and all(row['passed'] for row in rows), 'cases': rows}
    (args.out / 'summary.json').write_text(json.dumps(summary, indent=2), encoding='utf8')
    return 0 if summary['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
