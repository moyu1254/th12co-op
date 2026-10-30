"""Read-only focus rendering checks on paired, hidden isolated game copies."""
import argparse
import json
import struct
from pathlib import Path

from probe import Mapping, read_process
from quiet_test import quiet_evidence
from test_native_characters import actors, enter
from test_native_lockstep import NetGame, ROOT, k
from test_native_modes import Pair, prepared_folder, sha256


def markers(game, positions):
    def word(address):
        return struct.unpack('<I', read_process(game.info.pid, address, 4))[0]

    manager = word(0x4ce8cc)
    bullets = word(0x4b43c8)
    asset = word(bullets + 0x4debdc)
    result = []
    for offset in (0x8856b8, 0x8856c0):
        node = word(manager + offset)
        visited = set()
        while node:
            if node in visited or len(visited) >= 65536:
                raise AssertionError('Invalid native ANM list')
            visited.add(node)
            vm, node = struct.unpack('<II', read_process(game.info.pid, node, 8))
            data = read_process(game.info.pid, vm, 0x498)
            script = struct.unpack_from('<h', data, 0x3ea)[0]
            if word(vm + 0x3f8) != asset or script not in (76, 77):
                continue
            root = word(vm + 0x3c) or vm
            x = struct.unpack('<f', read_process(game.info.pid, root + 0x430, 4))[0] - 224
            owner = min(range(2), key=lambda n: abs(x - positions[n])) + 1
            if abs(x - positions[owner - 1]) > 0.1:
                raise AssertionError(f'Marker owner position does not match: {x}, {positions}')
            result.append({'owner': owner, 'script': script,
                           'visible': bool(word(vm + 0x47c) & 2),
                           'drawCallback': bool(word(vm + 0x48c))})
    return sorted(result, key=lambda item: (item['owner'], item['script']))


def run(args):
    folders = [prepared_folder(args.host_game, 'host'), prepared_folder(args.guest_game, 'guest')]
    hashes = [sha256(folder / 'th12.exe') for folder in folders]
    if hashes[0] != hashes[1]:
        raise ValueError('Use the same executable profile for both peers')
    report = {'passed': False, 'exeSha256': hashes[0],
              'nativeSha256': sha256(ROOT / 'dist' / args.native),
              'matchedDigestFrames': 0, 'samples': []}
    games, observations = [], []
    pair = None
    try:
        for index, folder in enumerate(folders):
            games.append(NetGame(folder, 0x6e31, p1=args.p1, p2=args.p2,
                                 native=args.native, local_player=index + 1))
        pair = Pair(games, report)
        for game in games:
            if k.ResumeThread(game.info.thread) == 0xffffffff:
                raise OSError('Could not resume owned game')
        enter(pair, 'practice', 1)
        observations = [Mapping(f'Local\\TH12CoopActors_{game.info.pid}') for game in games]
        for _ in range(360):
            pair.advance(1, 8, 8)
            if all(actor['focus'] == 1 for actor in actors(observations[0])['actors']):
                break
        else:
            raise AssertionError('Native focus did not activate')

        def sample(label, require_pair=False):
            pair.boundary()
            positions = [actor['x'] for actor in actors(observations[0])['actors']]
            views = [markers(game, positions) for game in games]
            report['samples'].append({'label': label, 'views': views})
            for index, view in enumerate(views):
                if require_pair and [(m['owner'], m['script']) for m in view] != [(1, 76), (1, 77), (2, 76), (2, 77)]:
                    raise AssertionError(f'Both complete marker families must be present: {view}')
                for marker in view:
                    remote = marker['owner'] != index + 1
                    if marker['drawCallback'] != remote or (remote and marker['visible']):
                        raise AssertionError(f'{label}: remote marker/decorations were not suppressed: {view}')
                    if require_pair and not remote and not marker['visible']:
                        raise AssertionError(f'{label}: local marker was hidden: {view}')
            return views

        pair.advance(8, 8, 8)
        sample('both-focused', True)
        for cycle in range(3):
            for frame in range(1, 5):
                pair.advance(1)
                sample(f'release-{cycle}-{frame}')
            pair.advance(8, 8, 8)
            sample(f'refocus-{cycle}', True)
        pair.advance(60)
        if any(sample('focus-ended')):
            raise AssertionError('Focus marker family did not finish its native lifetime')
        pair.advance(12, 8, 0)
        sample('only-p1-focused')
        pair.advance(60)
        pair.advance(12, 0, 8)
        sample('only-p2-focused')
        report['quiet'] = [quiet_evidence(game.info.pid) for game in games]
        if any(q['failures'] or not q['graphicsDevices'] or q['visibleOwnedWindows'] or q['ownsForeground'] for q in report['quiet']):
            raise AssertionError('Quiet/background execution failed')
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        errors = []
        for observation in observations:
            try:
                observation.close()
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
        report['ownedHandlesClosed'] = all(not game.info.process and not game.info.thread for game in games)
        if errors:
            report['passed'] = False
            report['cleanupErrors'] = errors
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps({key: value for key, value in report.items() if key != 'samples'}), flush=True)
        if errors:
            raise RuntimeError('Owned process cleanup failed: ' + '; '.join(errors))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--host-game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    parser.add_argument('--p1', type=int, choices=range(6), default=0)
    parser.add_argument('--p2', type=int, choices=range(6), default=4)
    parser.add_argument('--native', default='th12coop-native.dll')
    run(parser.parse_args())
