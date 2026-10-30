"""Production offline menu selection and shooting in a hidden, muted copy.

Uses the normal offline input IPC, not a network bridge or diagnostic writes.
"""
import argparse
import json
from pathlib import Path
import struct
import time

from probe import Mapping
from smoke_gameplay import Game, ROOT
from test_cooperative_modes import menu, select, wait_menu
from quiet_test import quiet_evidence
import hashlib


def run(args):
    game = damage = actors = None
    report = dict(passed=False, networkBridge=False, diagnosticWrites=False, players=[4, 1],
                  nativeSha256=hashlib.sha256((ROOT / 'dist/th12coop-native.dll').read_bytes()).hexdigest(),
                  exeSha256=hashlib.sha256((args.game / 'th12.exe').read_bytes()).hexdigest(),
                  damageFrames=0, hitFrames=0, reducedFrames=0, incapacitatedFrames=0)
    try:
        game = Game(args.game, production=True)
        select(game, 1, 0); game.pulse(1)
        select(game, 5, 0); game.pulse(1)
        select(game, 6, 2); game.pulse(1)
        select(game, 7, 0); game.pulse(1, 1)
        wait_menu(game, 6)

        def pulse_partner(mask):
            game.run(0, mask, .12); game.run(0, 0, .4)

        def partner_select(state, target):
            wait_menu(game, state)
            for _ in range(12):
                cursor = menu(game)[2]
                if cursor == target:
                    return
                pulse_partner(0x20 if cursor < target else 0x10)
            raise AssertionError('Offline partner cursor did not reach its selection')

        partner_select(6, 0); pulse_partner(1)
        partner_select(7, 1); pulse_partner(1)
        for _ in range(150):
            state = game.run(0, 0, .05)
            if state['status'] == 2 and state['p1state'] == state['p2state'] == 1:
                break
        else:
            raise AssertionError(f'Offline actors did not start: {state}')
        actors = Mapping(f'Local\\TH12CoopActors_{game.proc.pid}')
        data = actors.read(0, 144)
        selected = [struct.unpack_from('<I', data, 20 + p * 64)[0] for p in range(2)]
        assert selected == [4, 1], selected
        report['selected'] = selected
        damage = Mapping(f'Local\\TH12CoopDamage_{game.proc.pid}')
        sequence = None
        deadline = time.monotonic() + args.seconds
        while time.monotonic() < deadline:
            game.run(9, 9, .006)
            data = damage.read(0, 288)
            header = struct.unpack_from('<12I', data)
            if header[2] == sequence:
                continue
            sequence = header[2]
            report['damageFrames'] += 1
            for c in range(3):
                a, b, native, applied, bomb, factor, targets = struct.unpack_from('<5Q2I', data, 48 + c * 48)
                assert factor <= 65536 and applied >= bomb and applied <= native
                if header[4] != 3:
                    assert factor == 65536
                    report['incapacitatedFrames'] += 1
                report['hitFrames'] += bool(native)
                report['reducedFrames'] += bool(a + b and factor < 65536)
        assert report['hitFrames'] and report['reducedFrames'], report
        report['quiet'] = quiet_evidence(game.proc.pid)
        q = report['quiet']
        assert q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground']
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        for mapping in (damage, actors):
            if mapping:
                mapping.close()
        if game:
            game.close()
            report['ownedHandlesClosed'] = not game.proc.info.process and not game.proc.info.thread
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps(report), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', type=Path, required=True)
    parser.add_argument('--seconds', type=int, default=25)
    parser.add_argument('--out', type=Path, required=True)
    run(parser.parse_args())
