"""Observe the original built-in demo/replay path with one native player."""
import argparse
import json
from pathlib import Path

from probe import Mapping, k
from quiet_test import quiet_evidence
from test_native_lockstep import NetGame, ROOT
from test_native_modes import Pair, prepared_folder, sha256


def run(args):
    games = []
    pair = None
    report = dict(passed=False, scope='Built-in native demo/replay; no custom replay file or original solo clear',
                  nativeSha256=sha256(ROOT / 'dist/th12coop-native.dll'), matchedDigestFrames=0)
    try:
        for folder, role in ((args.host_game, 'host'), (args.guest_game, 'guest')):
            folder = prepared_folder(folder, role)
            report.setdefault('exeSha256', []).append(sha256(folder / 'th12.exe'))
            games.append(NetGame(folder, 0x7143))
        pair = Pair(games, report)
        for game in games:
            assert k.ResumeThread(game.info.thread) != 0xffffffff
        for _ in range(3600):
            state = pair.sample()
            if state['scene'] == 7 and state['modeFlags'] & 0x20 and state['packedPlayers'] == 0xff01:
                break
            pair.advance(1)
        else:
            raise AssertionError(f'Original single-player demo did not start: {state}')
        report['entry'] = state
        shot_seen = False
        for _ in range(240):
            pair.advance(1)
            state = pair.sample()
            assert state['modeFlags'] & 0x20 and state['packedPlayers'] >> 8 == 0xff, state
            shot_seen |= state['shots'][0][0] > 0
            assert all(shots[1] == 0 for shots in state['shots'])
        assert shot_seen, 'Native replay inputs did not produce original shooting'
        for game in games:
            try:
                unexpected = Mapping(f'Local\\TH12CoopDamage_{game.info.pid}')
            except OSError:
                pass
            else:
                unexpected.close()
                raise AssertionError('Cooperative damage manager ran during the native demo')
        report['singleNativePlayer'] = True
        report['nativeReplayShooting'] = True
        report['cooperativeDamageBypassed'] = True
        report['quiet'] = [quiet_evidence(g.info.pid) for g in games]
        assert all(q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground'] for q in report['quiet'])
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
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
