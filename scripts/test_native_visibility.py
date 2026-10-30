"""Production rendering observations with opposite local roles and equal digests.

Uses two isolated real games, committed input and read-only render telemetry.
The observations cover the native draw path, not a screenshot quality review.
"""
import argparse
import hashlib
import json
from pathlib import Path
import struct

from probe import Mapping, k
from quiet_test import quiet_evidence
from test_native_lockstep import NetGame, ROOT
from test_native_modes import Pair
from test_native_characters import enter


def run(args):
    games, mappings = [], []
    pair = None
    report = dict(passed=False, rulesVersion=6, diagnosticWrites=False,
                  nativeSha256=hashlib.sha256((ROOT / 'dist/th12coop-native.dll').read_bytes()).hexdigest())
    try:
        for role, folder in enumerate((args.host_game, args.guest_game), 1):
            games.append(NetGame(folder, 0x7143, p1=0, p2=5, local_player=role))
        pair = Pair(games, report)
        for game in games:
            assert k.ResumeThread(game.info.thread) != 0xffffffff
        enter(pair, 'practice', 1)
        mappings = [Mapping(f'Local\\TH12CoopVisuals_{g.info.pid}') for g in games]
        for _ in range(3):
            pair.advance(90, 9, 9)  # Both focus, shoot and retain their own marker.
            pair.advance(30)        # Release and recreate marker VMs.
        pair.advance(90, 8, 8)
        report['visuals'] = [list(struct.unpack('<10I', m.read(0, 40))) for m in mappings]
        for role, data in enumerate(report['visuals'], 1):
            assert data[:3] == [0x56323154, 1, role], data
            local, remote = role - 1, 2 - role
            assert data[4 + local] > 0 and data[6 + local] == 0, data
            assert data[4 + remote] == 0 and data[6 + remote] > 0, data
            assert data[8 + local] >> 24 > 112 and data[8 + remote] >> 24 <= 112, data
        report['quiet'] = [quiet_evidence(g.info.pid) for g in games]
        assert all(q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground'] for q in report['quiet'])
        report['exeSha256'] = [hashlib.sha256((p / 'th12.exe').read_bytes()).hexdigest() for p in (args.host_game, args.guest_game)]
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        for mapping in mappings:
            mapping.close()
        if pair:
            pair.close()
        for game in games:
            game.close()
        report['ownedHandlesClosed'] = all(not g.info.process and not g.info.thread for g in games)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps(report), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    run(parser.parse_args())
