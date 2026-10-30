"""Main-thread native fixtures in an isolated real game, using the smoke DLL.

Unlike input-only smoke tests, these fixtures deliberately modify gameplay state
through a fixed test-only command API. Every case gets a fresh process and exits
immediately afterward. The production DLL has no such API.
"""
import argparse, hashlib, json, struct, time
from pathlib import Path
from smoke_gameplay import Game, ROOT
from probe import Mapping
from quiet_test import quiet_evidence

def run(folder, output, family, case, shot):
    folder = folder.resolve()
    assert folder.parent == ROOT and folder.name.startswith('game-test-'), 'Isolated fixture folder required'
    game = audit = None
    report = {'family': family, 'case': case, 'shot': shot, 'game': str(folder),
              'exe_sha256': hashlib.sha256((folder/'th12.exe').read_bytes()).hexdigest(),
              'smoke_sha256': hashlib.sha256((ROOT/'dist/th12coop-smoke.dll').read_bytes()).hexdigest(),
              'injector_sha256': hashlib.sha256((ROOT/'dist/injector.exe').read_bytes()).hexdigest(),
              'guard_sha256': hashlib.sha256((ROOT/'dist/th12coop-quiet-test.dll').read_bytes()).hexdigest(),
              'fixture_mutates_gameplay': True, 'passed': False}
    try:
        game = Game(folder)
        report['pid'] = game.proc.pid
        game.confirm_until(5)
        game.confirm_until(6)
        for _ in range(shot//2): game.pulse(0x20, .25)
        game.confirm_until(7)
        if shot % 2: game.pulse(0x20, .25)
        game.pulse(1, 2)
        for _ in range(100):
            st = game.run(0, 0, .05)
            if st['status'] == 2 and st['p1state'] == st['p2state'] == 1: break
        else: raise AssertionError(f'Actors not ready: {st}')
        assert (game.u32(0x4b0c90),game.u32(0x4b0c94)) == (shot//2,shot%2)
        audit = Mapping(f'Local\\TH12CoopAudit_{game.proc.pid}', True)
        assert struct.unpack('<2I',audit.read(0,8)) == (0x41423154,1)
        report['quietBeforeFixture'] = quiet_evidence(game.proc.pid)
        audit.write32(16,family)
        audit.write32(20,case)
        audit.write32(8,1)
        deadline = time.monotonic()+5
        while struct.unpack('<I',audit.read(12,4))[0] != 1:
            game.run(0,0,.01)
            if time.monotonic()>deadline: raise TimeoutError('Native fixture did not finish')
        result = struct.unpack('<I',audit.read(24,4))[0]
        report['failure_mask'] = f'{result:08x}'
        report['details'] = list(struct.unpack('<32I',audit.read(32,128)))
        assert result == 0, report
        if family == 1 and case == 3:
            # Damage's temporary spell context must be gone before the real
            # Spellcard tick runs; otherwise a boss-less stage can dereference
            # a null boss after the fixture has already reported success.
            game.run(0, 0, .1)
            report['postFixtureAlive'] = True
        report['passed'] = True
    except Exception as error:
        report['error'] = str(error)
        raise
    finally:
        if audit:
            # A fixture may finish before a later engine callback fails. The
            # external mapping stays valid even if the owned game has exited.
            report['fixtureCompleted'] = struct.unpack('<I', audit.read(12, 4))[0] == 1
            if report['fixtureCompleted']:
                report['failure_mask'] = f"{struct.unpack('<I', audit.read(24, 4))[0]:08x}"
                report['details'] = list(struct.unpack('<32I', audit.read(32, 128)))
            audit.close()
        if game:
            try:
                report['quiet'] = quiet_evidence(game.proc.pid)
                quiet = report['quiet']
                if not quiet['devices'] or not quiet['graphicsDevices'] or quiet['failures'] or quiet['visibleOwnedWindows'] or quiet['ownsForeground']:
                    report['passed'] = False
                    report['quietError'] = 'Audio/window guard evidence did not pass'
            except Exception as error:
                report['passed'] = False
                report['quietError'] = str(error)
            finally:
                try:game.close()
                except Exception as error:
                    report['passed'] = False
                    report['cleanupError'] = str(error)
                report['processExitCode'] = game.proc.returncode
                if game.proc.returncode != 0:
                    report['passed'] = False
                    report['unexpectedExit'] = True
                report['ownedHandlesClosed'] = not game.proc.info.process and not game.proc.info.thread
        output.mkdir(parents=True,exist_ok=True)
        native_log = folder / 'th12coop-native.log'
        if not report['passed'] and native_log.exists():
            (output/f'family-{family}-case-{case}-shot-{shot}.native.log').write_bytes(native_log.read_bytes())
        (output/f'family-{family}-case-{case}-shot-{shot}.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        print(json.dumps(report),flush=True)
    return report['passed']

if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--game',type=Path,required=True)
    parser.add_argument('--out',type=Path,required=True)
    parser.add_argument('--family',type=int,required=True)
    parser.add_argument('--case',type=int,required=True)
    parser.add_argument('--shot',type=int,default=0,choices=range(6))
    args=parser.parse_args()
    raise SystemExit(0 if run(args.game,args.out,args.family,args.case,args.shot) else 1)
