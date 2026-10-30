"""Select distinct equipment in the native menu using committed player input.

Production DLL, isolated quiet copies, read-only actor observations. Every
frame's digest must agree, including the partner's selection and cancellation.
"""
import argparse
import json
from pathlib import Path

from probe import Mapping, k
from test_native_characters import actors
from test_native_lockstep import NetGame, ROOT
from test_native_modes import Pair, prepared_folder, sha256
from quiet_test import quiet_evidence


def pulse(pair, mask, player=0):
    pair.advance(3, mask if player == 0 else 0, mask if player else 0)
    pair.advance(21)


def select(pair, state, cursor, player=0):
    pair.wait_menu(state)
    for _ in range(12):
        current = pair.menu()[2]
        if current == cursor:
            return
        pulse(pair, 0x20 if current < cursor else 0x10, player)
    raise AssertionError(f'Could not select {cursor} on {state}')


def enter(pair, mode, shots, cancel=False):
    pair.select(1, 1 if mode == 'extra' else 2 if mode == 'practice' else 0)
    pair.pulse(1)
    if mode != 'extra':
        pair.select(5, 1)
    else:
        pair.wait_menu(5)
    pair.pulse(1)
    for player, shot in enumerate(shots):
        select(pair, 6, shot // 2, player)
        if player:
            before = pair.menu()
            pulse(pair, 0x20)  # P1 cannot move P2's cursor.
            assert pair.menu() == before, 'P1 controlled the partner selection'
        pulse(pair, 1, player)
        select(pair, 7, shot % 2, player)
        if cancel:
            pulse(pair, 2, player)
            select(pair, 6, shot // 2, player)
            pulse(pair, 1, player)
            select(pair, 7, shot % 2, player)
        pulse(pair, 1, player)
    if mode == 'practice':
        pair.select(8, 0)
        pair.pulse(1)
    for _ in range(1200):
        state = pair.sample()
        if state['scene'] == 7 and state['packedPlayers'] == 0x101:
            return state
        pair.advance(1)
    raise AssertionError(f'Native selection did not start gameplay: {state}')


def run(args):
    games, observations = [], []
    pair = None
    report = dict(passed=False, rulesVersion=6, mode=args.mode, players=[args.p1, args.p2],
                  nativeSha256=sha256(ROOT / 'dist/th12coop-native.dll'), matchedDigestFrames=0)
    try:
        for folder, role in ((args.host_game, 'host'), (args.guest_game, 'guest')):
            folder = prepared_folder(folder, role)
            report.setdefault('exeSha256', []).append(sha256(folder / 'th12.exe'))
            games.append(NetGame(folder, 0x7143))
        pair = Pair(games, report)
        for game in games:
            assert k.ResumeThread(game.info.thread) != 0xffffffff
        report['entry'] = enter(pair, args.mode, (args.p1, args.p2), args.cancel)
        observations = [Mapping(f'Local\\TH12CoopActors_{g.info.pid}') for g in games]
        values = [actors(m) for m in observations]
        assert values[0] == values[1], values
        assert [a['shot'] for a in values[0]['actors']] == [args.p1, args.p2], values
        report['actors'] = values[0]
        pair.advance(240, 1, 9)
        report['final'] = pair.sample()
        report['quiet'] = [quiet_evidence(g.info.pid) for g in games]
        assert all(q['devices'] and q['graphicsDevices'] and not q['failures'] and not q['visibleOwnedWindows'] and not q['ownsForeground'] for q in report['quiet'])
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        for m in observations:
            m.close()
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
    parser.add_argument('--mode', choices=('practice', 'extra', 'normal'), default='practice')
    parser.add_argument('--p1', type=int, choices=range(6), default=0)
    parser.add_argument('--p2', type=int, choices=range(6), default=5)
    parser.add_argument('--cancel', action='store_true')
    parser.add_argument('--out', type=Path, required=True)
    run(parser.parse_args())
