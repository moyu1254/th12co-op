"""Observe real, native accepted damage while matching every paired frame.

No game memory writes or synthetic targets: production DLLs and normal input.
The Python oracle independently seals category windows and checks every factor.
"""
import argparse
from collections import deque
import json
from pathlib import Path
import struct

from probe import Mapping, k, read_process
from quiet_test import quiet_evidence
from test_native_characters import enter
from test_native_lockstep import NetGame, ROOT
from test_native_modes import Pair, prepared_folder, sha256


def observe(mapping):
    data = mapping.read(0, 288)
    header = struct.unpack_from('<12I', data)
    assert header[:2] == (0x44323154, 1), header
    groups = [struct.unpack_from('<5Q2I', data, 48 + c * 48) for c in range(3)]
    return header, groups, [struct.unpack_from('<4Q', data, 192 + c * 32) for c in range(3)]


def run(args):
    games, mappings = [], []
    pair = None
    if args.ufo_fixture and args.campaign_fixture:
        raise ValueError('Choose only one diagnostic fixture')
    fixture = args.ufo_fixture or args.campaign_fixture
    native = 'th12coop-smoke.dll' if fixture else 'th12coop-native.dll'
    report = dict(passed=False, rulesVersion=5, players=[args.p1, args.p2], stage=args.stage,
                  nativeSha256=sha256(ROOT / 'dist' / native), fixtureMutatesGameplay=fixture, matchedDigestFrames=0,
                  framesObserved=0, categories=[dict(hitFrames=0, reducedFrames=0, bombFrames=0, bothHitFrames=0, spellHitFrames=0, normalHitFrames=0, minFactor=65536) for _ in range(3)], samples=[])
    try:
        for folder, role in ((args.host_game, 'host'), (args.guest_game, 'guest')):
            folder = prepared_folder(folder, role)
            report.setdefault('exeSha256', []).append(sha256(folder / 'th12.exe'))
            games.append(NetGame(folder, 0x7143, p1=args.p1, p2=args.p2, native=native))
        pair = Pair(games, report)
        for game in games:
            assert k.ResumeThread(game.info.thread) != 0xffffffff
        enter(pair, 'practice', args.stage)
        if fixture:
            audits = [Mapping(f'Local\\TH12CoopAudit_{g.info.pid}', True) for g in games]
            try:
                for audit in audits:
                    audit.write32(16, 6 if args.ufo_fixture else 4)
                    audit.write32(20, 1 if args.ufo_fixture else 2); audit.write32(8, 1)
                pair.advance(2)
                for audit in audits:
                    assert struct.unpack('<I', audit.read(12, 4))[0] == 1
                    assert struct.unpack('<I', audit.read(24, 4))[0] == 0
                report['fixture'] = struct.unpack('<32I', audits[0].read(32, 128))
            finally:
                for audit in audits:
                    audit.close()
        mappings = [Mapping(f'Local\\TH12CoopDamage_{g.info.pid}') for g in games]
        windows = [deque(maxlen=n) for n in (60, 30, 30)]
        previous_frame = 0
        previous_phase = None
        previous_epoch = None
        previous_active = None
        previous_ufo_epoch = None
        for frame in range(args.frames):
            # Solo, simultaneous concentrated/distributed fire, loss of one
            # shooter's hits, then a native bomb overlapping continuous shots.
            phase = frame % 900
            p1 = 9
            p2 = 9 if args.ufo_fixture or 180 <= phase < 660 else 0
            if phase == (120 if args.ufo_fixture else 450):
                p2 |= 2
            # Native boss dialogue requires rising confirmation edges. Keep
            # long gameplay checks moving through dialogue using normal input.
            gui = struct.unpack('<I', read_process(games[0].info.pid, 0x4b43e4, 4))[0]
            if gui and struct.unpack('<I', read_process(games[0].info.pid, gui + 0x6d30, 4))[0]:
                p1 = 1 if frame % 20 < 3 else 0
                p2 = 0
            pair.advance(1, p1, p2)
            values = [observe(m) for m in mappings]
            assert values[0] == values[1], 'Paired damage observations differ'
            header, groups, totals = values[0]
            current_frame, active_mask, current_phase = header[3], header[4], header[6:10]
            if current_frame == previous_frame:
                continue
            if previous_phase is not None and current_phase != previous_phase:
                if current_phase[0] != previous_phase[0]:
                    for w in windows:
                        w.clear()
                else:
                    windows[0].clear()
            if active_mask != 3 or previous_active != 3:
                for w in windows:
                    w.clear()
            if previous_epoch is not None and header[5] != previous_epoch:
                windows[0].clear()
            if previous_ufo_epoch is not None and header[10] != previous_ufo_epoch:
                windows[2].clear()
            for c, group in enumerate(groups):
                a, b, native, applied, bomb, factor, targets = group
                windows[c].append((a, b))
                d1 = sum(v[0] for v in windows[c])
                d2 = sum(v[1] for v in windows[c])
                num, den = ((6, 5), (5, 4), (1, 1))[c]
                expected = 65536 if active_mask != 3 or d1 + d2 == 0 else min(65536, num * max(d1, d2) * 65536 // (den * (d1 + d2)))
                assert factor == expected, (frame, c, factor, expected, header, list(windows[c]))
                assert applied >= bomb, 'Cooperative budget reduced a native bomb baseline'
                assert applied <= native, 'Damage budget amplified native damage'
                stats = report['categories'][c]
                if native:
                    stats['hitFrames'] += 1
                    stats['spellHitFrames' if header[9] else 'normalHitFrames'] += 1
                if factor < 65536 and a + b:
                    stats['reducedFrames'] += 1
                stats['bombFrames'] += bool(bomb)
                stats['bothHitFrames'] += bool(a and b)
                stats['minFactor'] = min(stats['minFactor'], factor)
            if frame % 300 == 0 or (active_mask != 3 and groups[0][2]):
                report['samples'].append(dict(inputFrame=pair.frame - 1, header=header, groups=groups, totals=totals))
            previous_frame, previous_phase = current_frame, current_phase
            previous_epoch = header[5]
            previous_active = active_mask
            previous_ufo_epoch = header[10]
            report['framesObserved'] += 1
            if frame % 900 == 899:
                print(json.dumps(dict(inputFrame=pair.frame - 1, damageFrame=current_frame, categories=report['categories'])), flush=True)
        report['totals'] = totals
        assert sum(c['hitFrames'] for c in report['categories']) > 0, 'No actual accepted damage exercised'
        assert sum(c['reducedFrames'] for c in report['categories']) > 0, 'No cooperative shot reduction exercised'
        if args.ufo_fixture:
            assert report['categories'][2]['reducedFrames'] > 0, 'UFO shot reduction was not exercised'
        report['quiet'] = [quiet_evidence(g.info.pid) for g in games]
        assert all(q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground'] for q in report['quiet'])
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        for m in mappings:
            m.close()
        if pair:
            pair.close()
        for game in games:
            try:
                game.close()
            except Exception as error:
                report.setdefault('cleanupErrors', []).append(str(error))
                report['passed'] = False
        report['ownedHandlesClosed'] = all(not g.info.process and not g.info.thread for g in games)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps({k: v for k, v in report.items() if k != 'samples'}), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--p1', type=int, choices=range(6), default=0)
    parser.add_argument('--p2', type=int, choices=range(6), default=5)
    parser.add_argument('--stage', type=int, choices=range(1, 7), default=1)
    parser.add_argument('--frames', type=int, default=3600)
    parser.add_argument('--ufo-fixture', action='store_true', help='Diagnostic DLL: spawn a native UFO, prepare power and player immunity; retain original UFO HP and effects')
    parser.add_argument('--campaign-fixture', action='store_true', help='Diagnostic DLL: native Power 400 and sustained player immunity, damage multiplier 1; no enemy HP or pattern changes')
    parser.add_argument('--out', type=Path, required=True)
    run(parser.parse_args())
