"""Exercise the loader fence without executing the game's entry instructions.

Uses isolated test copies and the same quiet guard/job ownership as gameplay
tests. This records intermittent startup errors; it never retries a failed game
in place or changes the loader timeout to turn a failure into a pass.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time

from test_native_lockstep import NetGame, ROOT
from quiet_test import quiet_evidence


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--count', type=int, default=10)
    parser.add_argument('--out', type=Path, required=True)
    args = parser.parse_args()
    if not 1 <= args.count <= 100:
        parser.error('count must be 1..100')
    report = {'passed': False, 'scope': 'OS loader and DLL initialization only; game entry instructions never executed',
              'game': str(args.game), 'cases': [], 'sha256': {name: hashlib.sha256(path.read_bytes()).hexdigest()
              for name, path in [('exe', args.game / 'th12.exe'), ('injector', ROOT / 'dist/injector.exe'),
                                 ('guard', ROOT / 'dist/th12coop-quiet-test.dll'), ('native', ROOT / 'dist/th12coop-native.dll')]}}
    try:
        for index in range(args.count):
            row = {'index': index, 'passed': False}
            began = time.monotonic()
            # Retain the partially constructed object's ownership information so
            # the result can assert cleanup even if initialization itself fails.
            game = NetGame.__new__(NetGame)
            try:
                game.__init__(args.game, 4660)
                row['pid'] = game.info.pid
                header = game.header()
                row['quiet'] = quiet_evidence(game.info.pid)
                if header[9] != 0 or header[22] != 0 or any(row['quiet'][k] for k in ('devices', 'createdWindows', 'graphicsDevices')):
                    raise AssertionError('Game work ran before the explicit primary-thread resume')
                row['passed'] = True
            except Exception as error:
                row['error'] = str(error)
            finally:
                try:
                    game.close()
                    row['ownedHandlesClosed'] = not game.info.process and not game.info.thread
                except Exception as error:
                    row['cleanupError'] = str(error)
                    row['passed'] = False
                row['seconds'] = round(time.monotonic() - began, 3)
                report['cases'].append(row)
                print(json.dumps(row), flush=True)
            if not row['passed']:
                break
        report['passed'] = len(report['cases']) == args.count and all(row['passed'] and row.get('ownedHandlesClosed') for row in report['cases'])
    finally:
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
    return 0 if report['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
