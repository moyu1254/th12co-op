"""Input-only paired native checks for Extra or Practice stage entry.

Uses the production DLL and prepared, isolated net-host/net-guest folders.
Checks initial gameplay and ECL contexts, not a full clear or all spell cards.
NetGame resets config/scores only in the explicitly validated test folders.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import struct
import time

from probe import Mapping, telemetry
from test_native_lockstep import NetGame, ROOT, k, read_process


def prepared_folder(path, role):
    folder = path.resolve()
    pattern = rf'game-test-(retail-1\.00[ab]|steam-1\.00b)-net-{role}'
    if folder.parent != ROOT.resolve() or not re.fullmatch(pattern, folder.name):
        raise ValueError(f'Use a prepared workspace game-test-<version>-net-{role} folder')
    for name in ('th12.exe', 'th12.dat', 'thbgm.dat'):
        if not (folder / name).is_file():
            raise ValueError(f'Prepared test folder is missing {name}: {folder}')
    return folder


def sha256(path):
    with path.open('rb') as source:
        return hashlib.file_digest(source, 'sha256').hexdigest()


class Pair:
    def __init__(self, games, report):
        self.games = games
        self.report = report
        self.frame = 1
        self.matched = -1
        self.telemetry = []

    def close(self):
        for mapping in self.telemetry:
            mapping.close()
        self.telemetry = []

    def boundary(self):
        deadline = time.monotonic() + (125 if self.frame == 1 else 40)
        while True:
            for game in self.games:
                if k.WaitForSingleObject(game.info.process, 0) == 0:
                    raise AssertionError('An owned game exited before the input boundary')
                game.heartbeat()
            headers = [game.header() for game in self.games]
            if any(h[8] == 4 for h in headers):
                raise AssertionError(f'Native error at frame {self.frame}: {[h[14] for h in headers]}')
            if all(h[9] == self.frame and h[8] == 2 for h in headers):
                break
            if time.monotonic() >= deadline:
                raise AssertionError(f'Input boundary timeout at frame {self.frame}')
            time.sleep(.001)
        completed = self.frame - 1
        digests = [struct.unpack('<8I', game.map.read(4352 + (completed % 256) * 32, 32))
                   for game in self.games]
        if (any(h[11] != completed or h[19] != 6 for h in headers)
                or any(d[0] != completed for d in digests)
                or headers[0][12:14] != headers[1][12:14]
                or digests[0] != digests[1]):
            self.report['divergence'] = {'requestedFrame': self.frame,
                                         'digests': [list(d) for d in digests],
                                         'components': [list(h[24:30]) for h in headers]}
            raise AssertionError(f'Paired digest/rules mismatch at frame {completed}')
        if completed > self.matched:
            self.matched = completed
            self.report['matchedDigestFrames'] = completed
        return digests[0]

    def advance(self, count, p1=0, p2=0):
        for _ in range(count):
            self.boundary()
            for game in self.games:
                game.commit(self.frame, p1, p2)
            self.frame += 1
        return self.boundary()

    def pulse(self, mask):
        self.advance(3, mask)
        self.advance(21)

    def words(self, address):
        values = [struct.unpack('<I', read_process(game.info.pid, address, 4))[0]
                  for game in self.games]
        if values[0] != values[1]:
            raise AssertionError('Paired scalar mismatch')
        return values[0]

    def menu(self):
        result = []
        for game in self.games:
            pointer = struct.unpack('<I', read_process(game.info.pid, 0x4b4530, 4))[0]
            if not pointer:
                result.append((-1, -1, -1))
            else:
                fields = struct.unpack('<4I', read_process(game.info.pid, pointer + 0x1c, 16))
                result.append((fields[0], fields[2], fields[3]))
        if result[0] != result[1]:
            raise AssertionError(f'Paired menu mismatch: {result}')
        return result[0]

    def wait_menu(self, state):
        for _ in range(600):
            self.boundary()
            current = self.menu()
            if current[0] == state and current[1] == 2:
                return current
            self.advance(2)
        raise AssertionError(f'Menu {state} did not become interactive: {self.menu()}')

    def select(self, state, cursor):
        self.wait_menu(state)
        for _ in range(16):
            current = self.menu()
            if current[0] != state:
                raise AssertionError(f'Menu changed during selection: {current}')
            if current[2] == cursor:
                return
            self.pulse(0x20 if current[2] < cursor else 0x10)
        raise AssertionError(f'Could not select cursor {cursor}: {self.menu()}')

    def sample(self):
        digest = self.boundary()
        # Steam's early fence precedes final native initialization. Open the
        # read-only telemetry only after both games reach an input boundary.
        if not self.telemetry:
            for game in self.games:
                self.telemetry.append(Mapping(f'Local\\TH12Coop_{game.info.pid}'))
        values = [telemetry(mapping) for mapping in self.telemetry]
        thread_times = []
        for game in self.games:
            pointer = struct.unpack('<I', read_process(game.info.pid, 0x4b44e8, 4))[0]
            thread_times.append(struct.unpack('<I', read_process(game.info.pid, pointer + 0x14, 4))[0]
                                if pointer else None)
        if thread_times[0] != thread_times[1]:
            raise AssertionError('Paired GameThread timer mismatch')
        return {'frame': self.frame - 1, 'hash': f'{digest[2]:08x}{digest[1]:08x}',
                'scene': digest[3], 'stage': digest[4], 'packedPlayers': digest[5],
                'gameTime': thread_times[0], 'rank': self.words(0x4b0ca8),
                'modeFlags': self.words(0x4b0ce0),
                'actors': [[value['p1state'], value['p2state']] for value in values],
                'shots': [[value['p1bullets'], value['p2bullets']] for value in values]}

    def ecl_counts(self):
        samples = []
        for game in self.games:
            def word(address):
                return struct.unpack('<I', read_process(game.info.pid, address, 4))[0]
            manager = word(0x4b43dc)
            node = word(manager + 0x68) if manager else 0
            enemies = contexts = asynchronous = visited = 0
            while node and visited < 4096:
                visited += 1
                full = word(node)
                if full:
                    enemies += 1
                    context_node = full + 0x1030
                    count = 0
                    while context_node and count < 256:
                        contexts += 1
                        if count:
                            asynchronous += 1
                        count += 1
                        context_node = word(context_node + 4)
                    if context_node:
                        raise AssertionError('ECL context sampling exceeded its bound')
                node = word(node + 4)
            if node:
                raise AssertionError('Enemy sampling exceeded its bound')
            samples.append({'enemies': enemies, 'contexts': contexts, 'asyncContexts': asynchronous})
        if samples[0] != samples[1]:
            raise AssertionError(f'Paired ECL context counts differ: {samples}')
        return samples[0]


def run(args):
    expected_stage = 7 if args.mode == 'extra' else (args.stage or 1)
    if (args.mode == 'extra' and args.stage not in (None, 7)) or not 1 <= expected_stage <= 7:
        raise ValueError('Extra uses stage7; Practice uses stage1 through stage6')
    if args.mode == 'practice' and expected_stage == 7:
        raise ValueError('Practice uses stage1 through stage6')
    host = prepared_folder(args.host_game, 'host')
    guest = prepared_folder(args.guest_game, 'guest')
    if host == guest:
        raise ValueError('Use distinct prepared host and guest folders')
    hashes = [sha256(folder / 'th12.exe') for folder in (host, guest)]
    if hashes[0] != hashes[1]:
        raise ValueError('This paired native test requires the same executable version')
    report = {'passed': False, 'mode': args.mode, 'stage': expected_stage, 'rulesVersion': 6,
              'scope': 'Initial paired gameplay; not a full clear or all-spell test',
              'gameFolders': [str(host), str(guest)], 'exeSha256': hashes[0],
              'nativeSha256': sha256(ROOT / 'dist/th12coop-native.dll'),
              'matchedDigestFrames': 0, 'gameplayFrames': 0, 'samples': [], 'pids': []}
    games = []
    pair = None
    try:
        for folder in (host, guest):
            game = NetGame(folder, 0x6e31, p1=0, p2=0)
            games.append(game)
            report['pids'].append(game.info.pid)
        pair = Pair(games, report)
        for game in games:
            if k.ResumeThread(game.info.thread) == 0xffffffff:
                raise OSError('Could not resume owned game thread')
        # Native states and cursor routes are shared with test_cooperative_modes.
        pair.select(1, 1 if args.mode == 'extra' else 2)
        pair.pulse(1)
        if args.mode == 'practice':
            pair.select(5, 1)  # Normal difficulty; no scorefile unlock edits.
        else:
            pair.wait_menu(5)
        pair.pulse(1)
        pair.select(6, 0)
        pair.pulse(1)
        pair.select(7, 0)
        pair.pulse(1)
        if args.mode == 'practice':
            pair.select(8, expected_stage - 1)
            pair.pulse(1)
        for _ in range(1800):
            state = pair.sample()
            if (state['scene'] == 7 and state['stage'] == expected_stage
                    and state['packedPlayers'] == 0x101
                    and state['actors'] == [[1, 1], [1, 1]]
                    and state['gameTime'] is not None and state['gameTime'] >= 30):
                break
            pair.advance(1)
        else:
            raise AssertionError(f'Requested mode/stage never became active: {state}')
        if state['rank'] != (4 if args.mode == 'extra' else 1):
            raise AssertionError(f'Unexpected difficulty: {state}')
        if bool(state['modeFlags'] & 0x10) != (args.mode == 'practice'):
            raise AssertionError(f'Unexpected game mode flags: {state}')
        report['entry'] = state
        shot_seen = [False, False]
        maxima = {'enemies': 0, 'contexts': 0, 'asyncContexts': 0}
        for tick in range(180):
            # The same P1/P2 masks are committed to both processes.
            pair.advance(1, 0x41 if tick < 18 else 1, 0x81 if tick < 18 else 1)
            state = pair.sample()
            if state['stage'] != expected_stage:
                raise AssertionError('The requested stage changed during the initial gameplay check')
            for actor in range(2):
                shot_seen[actor] |= all(shots[actor] > 0 for shots in state['shots'])
            if tick % 30 == 0 or tick == 179:
                state['ecl'] = pair.ecl_counts()
                for field in maxima:
                    maxima[field] = max(maxima[field], state['ecl'][field])
                report['samples'].append(state)
                print(json.dumps(state), flush=True)
            report['gameplayFrames'] += 1
        report['final'] = state
        report['bothActorsShot'] = all(shot_seen)
        report['maxObservedEcl'] = maxima
        if state['actors'] != [[1, 1], [1, 1]] or state['packedPlayers'] != 0x101:
            raise AssertionError(f'Both actors are not active at the final boundary: {state}')
        if not all(shot_seen) or not maxima['contexts']:
            raise AssertionError('Initial gameplay did not demonstrate both shooting and real ECL contexts')
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        cleanup_errors = []
        if pair:
            try:
                pair.close()
            except Exception as error:
                cleanup_errors.append(str(error))
        for game in games:
            try:
                game.close()
            except Exception as error:
                cleanup_errors.append(str(error))
        if cleanup_errors:
            report['passed'] = False
            report['cleanupErrors'] = cleanup_errors
        report['ownedHandlesClosed'] = all(not game.info.process and not game.info.thread for game in games)
        args.out.parent.mkdir(parents=True, exist_ok=True)
        args.out.write_text(json.dumps(report, indent=2), encoding='utf8')
        print(json.dumps({key: value for key, value in report.items() if key != 'samples'}), flush=True)
        if cleanup_errors:
            raise RuntimeError('Owned-process cleanup failed; see report')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode', choices=('extra', 'practice'), required=True)
    parser.add_argument('--stage', type=int, choices=range(1, 8),
                        help='Practice1-6; Extra7 (defaults to Practice1 or Extra7)')
    parser.add_argument('--host-game', type=Path, required=True)
    parser.add_argument('--guest-game', type=Path, required=True)
    parser.add_argument('--out', type=Path, required=True)
    run(parser.parse_args())


if __name__ == '__main__':
    main()
