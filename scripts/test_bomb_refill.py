"""Observe natural collision/refill with the production DLL and input-only IPC.

Run on an isolated game-test-* copy. Each actor gets a fresh process; no game
memory is written. The two starting bombs are spent before seeking a collision.
"""
import argparse
import hashlib
import json
from pathlib import Path
import time
import struct

from quiet_test import quiet_evidence
from smoke_gameplay import Game, ROOT
from probe import Mapping


def run(folder, output, actor):
    game = None
    report = dict(passed=False, actor=actor, diagnosticWrites=False,
                  exeSha256=hashlib.sha256((folder / 'th12.exe').read_bytes()).hexdigest(),
                  nativeSha256=hashlib.sha256((ROOT / 'dist/th12coop-native.dll').read_bytes()).hexdigest())
    try:
        game = Game(folder, players=(0, 0), production=True)
        game.confirm_until(5); game.confirm_until(6); game.confirm_until(7)
        game.pulse(1, 2)
        for _ in range(100):
            state = game.run(0, 0, .05)
            if state['status'] == 2 and state['p1state'] == state['p2state'] == 1:
                break
        else:
            raise AssertionError('Both actors did not enter the stage')
        assert game.u32(0x4b0ca0) == 2
        game.run(8, 8, 1)
        with_visuals = Mapping(f'Local\\TH12CoopVisuals_{game.proc.pid}')
        try:
            visual = struct.unpack('<10I', with_visuals.read(0, 40))
            assert visual[:3] == (0x56323154, 1, 0) and all(visual[4:6]) and visual[6:8] == (0, 0), visual
            report['offlineVisuals'] = list(visual)
        finally:
            with_visuals.close()
        for remaining in (1, 0):
            game.run(0, 2, .15)
            assert game.u32(0x4b0ca0) == remaining
            deadline = time.monotonic() + 15
            while game.u32(game.u32(0x4b43c4) + 0x3c):
                assert time.monotonic() < deadline, 'Bomb did not finish'
                game.run(0, 0, .05)
            game.run(0, 0, .1)
        report['bombsBefore'] = game.u32(0x4b0ca0)
        report['livesBefore'] = game.u32(0x4b0c98)
        assert report['bombsBefore'] == 0
        hit = False
        deadline = time.monotonic() + 60
        while time.monotonic() < deadline:
            state = game.state()
            victim = 'p1' if actor == 0 else 'p2'
            if state[victim + 'state'] in (2, 4):
                hit = True
            if hit and state[victim + 'state'] == 1:
                break
            inputs = []
            for n, prefix in enumerate(('p1', 'p2')):
                x, y = (144, 100) if n == actor else (-178, 420)
                mask = 0
                if not (n == actor and hit):
                    if state[prefix + 'x'] < x - 7: mask |= 0x80
                    elif state[prefix + 'x'] > x + 7: mask |= 0x40
                    if state[prefix + 'y'] > y + 7: mask |= 0x10
                    elif state[prefix + 'y'] < y - 7: mask |= 0x20
                inputs.append(mask)
            game.run(*inputs, .025)
        else:
            raise AssertionError('Natural collision and respawn not observed')
        report['bombsAfter'] = game.u32(0x4b0ca0)
        report['livesAfter'] = game.u32(0x4b0c98)
        assert report['bombsAfter'] == 2 and report['livesAfter'] < report['livesBefore']
        report['quiet'] = quiet_evidence(game.proc.pid)
        q = report['quiet']
        assert q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground']
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        if game:
            game.close()
            report['ownedHandlesClosed'] = not game.proc.info.process and not game.proc.info.thread
        output.mkdir(parents=True, exist_ok=True)
        (output / f'actor-{actor}.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps(report), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--actor', type=int, choices=(0, 1))
    args = parser.parse_args()
    for actor in range(2) if args.actor is None else (args.actor,):
        run(args.game, args.out, actor)
