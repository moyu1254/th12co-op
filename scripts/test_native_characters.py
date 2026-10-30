"""Input-only mixed-character checks using production DLLs on isolated copies.

Runs two real engines with identical committed input. Scalar actor observations
are read-only; no fixture writes health, positions, power, scores or game memory.
QuietEnvironment prevents audio output and keeps owned windows hidden.
"""
import argparse
import json
import struct
from pathlib import Path

from probe import Mapping, k, read_process
from test_native_lockstep import NetGame, ROOT
from test_native_modes import Pair, prepared_folder, sha256
from quiet_test import quiet_evidence


def actors(mapping):
    data = mapping.read(0, 144)
    magic, version, sequence, bomb = struct.unpack_from('<4I', data)
    if magic != 0x41323154 or version != 1:
        raise AssertionError('Actor observation header is invalid')
    result = []
    for index in range(2):
        values = struct.unpack_from('<5I4i5f2I', data, 16 + index * 64)
        result.append(dict(present=values[0], shot=values[1], anmSlot=values[2],
                           powerLevels=values[3], powerUnit=values[4],
                           speeds=list(values[5:9]), radii=list(values[9:12]),
                           x=values[12], y=values[13], state=values[14], focus=values[15]))
    return {'bomb': bomb, 'actors': result}


def enter(pair, mode, stage):
    pair.select(1, 1 if mode == 'extra' else 2)
    pair.pulse(1)
    if mode == 'practice':
        pair.select(5, 1)
    else:
        pair.wait_menu(5)
    pair.pulse(1)
    pair.select(6, 0)  # Explicit launcher choices override this native menu.
    pair.pulse(1)
    pair.select(7, 0)
    pair.pulse(1)
    if mode == 'practice':
        pair.select(8, stage - 1)
        pair.pulse(1)
    for _ in range(1800):
        state = pair.sample()
        if (state['scene'] == 7 and state['stage'] == stage
                and state['packedPlayers'] == 0x101 and state['gameTime'] is not None
                and state['gameTime'] >= 30):
            return state
        pair.advance(1)
    raise AssertionError(f'Mixed actors did not activate: {state}')


def pointer_word(pair, address, offset):
    values = []
    for game in pair.games:
        pointer = struct.unpack('<I', read_process(game.info.pid, address, 4))[0]
        values.append(struct.unpack('<I', read_process(game.info.pid, pointer + offset, 4))[0] if pointer else None)
    if values[0] != values[1]:
        raise AssertionError(f'Paired pointer field differs at {address:#x}+{offset:#x}: {values}')
    return values[0]


def native_options(pair, shots):
    # Player::repopulate_options dispatch, verified in retail 1.00a/1.00b.
    # Read ANM ownership directly so P2 needs no published process pointer.
    expected_scripts = (18, 19, 11, 12, 15, 16)
    level = pair.words(0x4b0c48) // pair.words(0x4b0cd4)
    if not level:
        return {'powerLevels': 0, 'actors': [[], []], 'exercised': False}
    values = []
    for game in pair.games:
        def word(address):
            return struct.unpack('<I', read_process(game.info.pid, address, 4))[0]
        manager = word(0x4ce8cc)
        assets = [word(manager + 0x4b50c0 + slot * 4) for slot in (7, 26)]
        result = [[], []]
        for offset in (0x8856b8, 0x8856c0):
            node = word(manager + offset)
            visited = 0
            while node and visited < 65536:
                vm, node = struct.unpack('<II', read_process(game.info.pid, node, 8))
                visited += 1
                if not vm:
                    continue
                data = read_process(game.info.pid, vm, 0x43c)
                asset = struct.unpack_from('<I', data, 0x3f8)[0]
                script = struct.unpack_from('<h', data, 0x3ea)[0]
                for n in range(2):
                    if asset == assets[n] and script == expected_scripts[shots[n]]:
                        result[n].append(list(struct.unpack_from('<3f', data, 0x430)))
            if node:
                raise AssertionError('ANM option observation exceeded its list bound')
        for n in range(2):
            result[n].sort()
            if len(result[n]) != level:
                raise AssertionError(f'Actor {n + 1}: expected {level} equipment options, observed {result[n]}')
        values.append(result)
    if values[0] != values[1]:
        raise AssertionError(f'Paired native option positions differ: {values}')
    return {'powerLevels': level, 'actors': values[0], 'exercised': True,
            'scripts': [expected_scripts[shot] for shot in shots]}


def pause_action(pair, action):
    def pause():
        pair.boundary()
        return tuple(pointer_word(pair, 0x4b4510, offset) for offset in (4, 0x14, 0x38, 0x40))

    def wait(state, count, minimum):
        for _ in range(240):
            current = pause()
            if current[0] == state and current[3] == count and current[1] >= minimum:
                return current
            pair.advance(1)
        raise AssertionError(f'Pause did not reach state {state}: {current}')

    def select(target, state, count, minimum):
        for _ in range(12):
            current = wait(state, count, minimum)
            if current[2] == target:
                return
            pair.pulse(0x20 if current[2] < target else 0x10)
        raise AssertionError(f'Pause cursor did not reach {target}: {current}')

    pair.pulse(0x100)
    select(action, 3, 4, 11)
    pair.pulse(1)
    select(0, 5, 2, 30)
    pair.pulse(1)


def lifecycle(pair, observations, args, stage, sample, initial):
    def activation_counts():
        return [(game.folder / 'th12coop-native.log').read_text(encoding='utf8', errors='replace')
                .count('Both Player objects activated through native reset') for game in pair.games]

    def check_restored(label):
        value = sample(label)
        for previous, current in zip(initial['actors'], value['actors']):
            for field in ('shot', 'anmSlot', 'powerLevels', 'powerUnit', 'speeds', 'radii'):
                if previous[field] != current[field]:
                    raise AssertionError(f'{label} changed actor {field}: {previous} -> {current}')
        for _ in range(360):
            pair.advance(1, 8, 8)
            if all(actor['focus'] == 1 for actor in actors(observations[0])['actors']):
                break
        else:
            raise AssertionError(f'{label}: native focus did not become available')
        seen = [False, False]
        for _ in range(48):
            pair.advance(1, 1, 9)
            state = pair.sample()
            for n in range(2):
                seen[n] |= all(shots[n] > 0 for shots in state['shots'])
        if not all(seen):
            raise AssertionError(f'{label}: both actors did not resume native shooting')

    before_time = pointer_word(pair, 0x4b43dc, 0x54)
    before_activations = activation_counts()
    pause_action(pair, 3)
    timer_reset = None
    for _ in range(1200):
        state = pair.sample()
        current_time = pointer_word(pair, 0x4b43dc, 0x54)
        activated = all(after > before for after, before in zip(activation_counts(), before_activations))
        if activated and current_time is not None and current_time < before_time:
            timer_reset = {'before': before_time, 'after': current_time}
        if timer_reset and state['packedPlayers'] == 0x101 and pair.words(0x4b0ca0) == 2:
            break
        pair.advance(1)
    else:
        raise AssertionError('Retry did not reset the stage timer, team bombs and native activation')
    check_restored('retry-restored')
    pair.advance(1, 0, 2)
    active_bomb = sample('retry-p2-bomb-before-title')
    if active_bomb['bomb'] != initial['actors'][1]['shot'] or pair.words(0x4b0ca0) != 1:
        raise AssertionError('P2 did not initiate the bomb before title teardown')
    if pointer_word(pair, 0x4b43c4, 0x3c) != 1:
        raise AssertionError('Title teardown was not requested during an active native bomb')
    pause_action(pair, 1)
    for _ in range(1200):
        state = pair.sample()
        if state['scene'] == 4 and state['packedPlayers'] == 0xffff:
            break
        pair.advance(1)
    else:
        raise AssertionError('Title transition did not release both actors')
    released = [actors(mapping) for mapping in observations]
    if any(any(actor['present'] for actor in value['actors']) for value in released):
        raise AssertionError('Title transition retained an actor observation')
    if pointer_word(pair, 0x4ce8cc, 0x4b50c0 + 26 * 4) != 0:
        raise AssertionError('Title transition did not release P2 ANM slot 26')
    enter(pair, args.mode, stage)
    check_restored('title-reentered')
    return {'retryTimerReset': timer_reset, 'retryBombsReset': True,
            'p2BombActiveBeforeTitle': True,
            'titleReleasedActors': True, 'titleReleasedAnm26': True,
            'reentryPreservedEquipment': True, 'shootingAfterRetryAndReentry': True}


def run_case(args, p1, p2):
    host = prepared_folder(args.host_game, 'host')
    guest = prepared_folder(args.guest_game, 'guest')
    if host == guest:
        raise ValueError('Host and guest fixtures must be separate')
    stage = 7 if args.mode == 'extra' else args.stage
    report = dict(passed=False, p1=p1, p2=p2, mode=args.mode, stage=stage,
                  rulesVersion=4, exeSha256=[sha256(p / 'th12.exe') for p in (host, guest)],
                  nativeSha256=sha256(ROOT / 'dist/th12coop-native.dll'),
                  guardSha256=sha256(ROOT / 'dist/th12coop-quiet-test.dll'),
                  injectorSha256=sha256(ROOT / 'dist/injector.exe'),
                  scope='Initial movement, focus, shooting, both initiating bombs; not a full clear or visual inspection',
                  samples=[], pids=[], matchedDigestFrames=0)
    games, observations = [], []
    pair = None
    try:
        configurations = [(ROOT / 'profiles/th12-online.cfg').read_bytes() for _ in range(2)]
        if args.different_local_settings:
            guest_configuration = bytearray(configurations[1])
            guest_configuration[4] = 7
            guest_configuration[0x1d] = 2
            guest_configuration[0x20] = 90
            guest_configuration[0x21] = 35
            struct.pack_into('<ii', guest_configuration, 0x28, 20, 50)
            configurations[1] = guest_configuration
        report['differentLocalSettings'] = args.different_local_settings
        for folder, configuration in zip((host, guest), configurations):
            game = NetGame(folder, 0x6e31, configuration=configuration, p1=p1, p2=p2)
            games.append(game)
            report['pids'].append(game.info.pid)
        report['quiet'] = [game.quiet_evidence for game in games]
        pair = Pair(games, report)
        for game in games:
            if k.ResumeThread(game.info.thread) == 0xffffffff:
                raise OSError('Could not resume owned game')
        report['entry'] = enter(pair, args.mode, stage)
        observations = [Mapping(f'Local\\TH12CoopActors_{game.info.pid}') for game in games]

        def sample(label):
            pair.boundary()
            values = [actors(m) for m in observations]
            if values[0] != values[1]:
                raise AssertionError(f'Actor observations differ: {values}')
            value = values[0]
            if [actor['shot'] for actor in value['actors']] != [p1, p2]:
                raise AssertionError(f'Wrong per-player selections: {value}')
            if [actor['anmSlot'] for actor in value['actors']] != [7, 26]:
                raise AssertionError(f'ANM ownership is not independent: {value}')
            if any(actor['powerLevels'] != 4 or actor['powerUnit'] != 100 for actor in value['actors']):
                raise AssertionError(f'Unexpected shared power scale: {value}')
            value = dict(value, label=label, frame=pair.frame - 1)
            if label in ('initial', 'normal-movement', 'focused-movement', 'retry-restored', 'title-reentered'):
                value['options'] = native_options(pair, (p1, p2))
            report['samples'].append(value)
            return value

        initial = sample('initial')
        # Native per-character hit/collection/graze tables at 0x4b31a8,
        # 0x4b31cc and 0x4b31b4, checked in the supported retail binaries.
        expected_radii = ((2.0, 5.0, 60.0), (3.5, 7.0, 70.0), (3.0, 6.0, 60.0))
        for n, shot in enumerate((p1, p2)):
            if tuple(initial['actors'][n]['radii']) != expected_radii[shot // 2]:
                raise AssertionError(f'Actor {n + 1} has another character\'s native radii')
        if pair.words(0x4b0c90) * 2 + pair.words(0x4b0c94) != p1:
            raise AssertionError('Shared stage/story route does not match P1')
        pair.advance(8, 0x40, 0x80)
        moving = sample('normal-movement')
        for n, sign in ((0, -1), (1, 1)):
            distance = moving['actors'][n]['x'] - initial['actors'][n]['x']
            expected = initial['actors'][n]['speeds'][0] * 8 / 128 * sign
            if abs(distance - expected) > 0.02:
                raise AssertionError(f'Actor {n + 1} used the wrong normal speed: {distance} vs {expected}')
        # Native TH12 ignores focus before the stage has an active enemy
        # manager entry and its actor timer reaches four. Wait for the native
        # state through ordinary input rather than altering that state.
        for _ in range(360):
            pair.advance(1, 8, 8)
            if all(actor['focus'] == 1 for actor in actors(observations[0])['actors']):
                break
        else:
            raise AssertionError('Native focus did not become available')
        focus_ready = sample('focus-ready')
        pair.advance(8, 0x48, 0x88)
        focused = sample('focused-movement')
        if initial['options']['exercised']:
            if any(initial['options']['actors'][n] == focused['options']['actors'][n] for n in range(2)):
                raise AssertionError('An actor\'s native options did not move with its movement/focus')
            report['independentNativeOptions'] = True
        else:
            report['independentNativeOptions'] = 'Not exercised: stage starts at zero power'
        for n, sign in ((0, -1), (1, 1)):
            distance = focused['actors'][n]['x'] - focus_ready['actors'][n]['x']
            expected = focus_ready['actors'][n]['speeds'][1] * 8 / 128 * sign
            if abs(distance - expected) > 0.02:
                raise AssertionError(f'Actor {n + 1} used the wrong focused speed: {distance} vs {expected}')
        seen = [False, False]
        for _ in range(48):
            pair.advance(1, 1, 9)
            state = pair.sample()
            for n in range(2):
                seen[n] |= all(s[n] > 0 for s in state['shots'])
        if not all(seen):
            raise AssertionError('Both actors did not produce native projectiles')
        report['bothActorsShot'] = True
        sample('shooting')
        pair.advance(2)
        for initiator, shot in ((1, p2), (0, p1)):
            before = pair.words(0x4b0ca0)
            pair.advance(1, 2 if initiator == 0 else 0, 2 if initiator == 1 else 0)
            observed = sample(f'p{initiator + 1}-bomb')
            if observed['bomb'] != shot or pair.words(0x4b0ca0) != before - 1:
                raise AssertionError('Bomb did not select the initiating equipment and spend one shared bomb')
            for _ in range(720):
                pair.advance(1)
                states = []
                for game in games:
                    ptr = struct.unpack('<I', read_process(game.info.pid, 0x4b43c4, 4))[0]
                    states.append(struct.unpack('<I', read_process(game.info.pid, ptr + 0x3c, 4))[0])
                if states == [0, 0]:
                    break
            else:
                raise AssertionError('The initiating equipment bomb did not complete')
            sample(f'p{initiator + 1}-bomb-ended')
        report['bothBombsCompleted'] = True
        if args.lifecycle:
            report['lifecycle'] = lifecycle(pair, observations, args, stage, sample, initial)
        report['final'] = pair.sample()
        report['quiet'] = [quiet_evidence(game.info.pid) for game in games]
        if any(q['failures'] or q['devices'] < 1 or q['buffers'] < 1 or q['graphicsDevices'] < 1 or
               q['visibleOwnedWindows'] or q['ownsForeground'] for q in report['quiet']):
            raise AssertionError('Quiet/background execution was not confirmed')
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        errors = []
        for mapping in observations:
            try:
                mapping.close()
            except Exception as error:
                errors.append(str(error))
        if pair:
            try:
                pair.close()
            except Exception as error:
                errors.append(str(error))
        for game in games:
            try:
                game.close()
            except Exception as error:
                errors.append(str(error))
        report['ownedHandlesClosed'] = all(not g.info.process and not g.info.thread for g in games)
        if errors:
            report['passed'] = False
            report['cleanupErrors'] = errors
        args.out.mkdir(parents=True, exist_ok=True)
        (args.out / f'{args.mode}-{stage}-{p1}-{p2}.json').write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps({k: v for k, v in report.items() if k != 'samples'}), flush=True)
        if errors:
            raise RuntimeError('Owned-process cleanup failed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--host-game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--mode', choices=('practice', 'extra'), default='practice')
    parser.add_argument('--stage', type=int, choices=range(1, 7), default=1)
    parser.add_argument('--pairs', default='0:5,1:4,2:3,3:2,4:1,5:0', help='P1:P2 comma-separated, or all for 36 pairs')
    parser.add_argument('--different-local-settings', action='store_true', help='Use different local resolution, audio volume, key binding and window position on the guest')
    parser.add_argument('--lifecycle', action='store_true', help='Check native retry, title teardown and same-process reentry after the equipment checks')
    args = parser.parse_args()
    pairs = [(a, b) for a in range(6) for b in range(6)] if args.pairs == 'all' else [tuple(map(int, p.split(':'))) for p in args.pairs.split(',')]
    if any(len(p) != 2 or any(s not in range(6) for s in p) for p in pairs):
        parser.error('Use equipment ids 0 through 5')
    for p1, p2 in pairs:
        run_case(args, p1, p2)


if __name__ == '__main__':
    main()
