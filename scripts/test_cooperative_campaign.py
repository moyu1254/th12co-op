"""Observe native campaign progression using the destructive smoke-only fixture.

The fixture grants invulnerability, power400 and optionally damage4. Native
timers retain speed1. This is a stage/script/lifecycle check, NOT a normal-difficulty clear.
Only the audit/input mappings are written; process memory is read-only here.
"""
import argparse
import ctypes
import hashlib
import json
import math
from pathlib import Path
import struct
import time

from probe import Mapping, k
from smoke_gameplay import Game, ROOT
from test_cooperative_modes import fresh_copy, select, wait_menu, actors


class Reader:
    def __init__(self,game):self.game=game
    def read(self,address,count):
        data=ctypes.create_string_buffer(count);done=ctypes.c_size_t()
        if not k.ReadProcessMemory(self.game.proc.info.process,address,data,count,ctypes.byref(done)) or done.value!=count:
            raise ctypes.WinError(ctypes.get_last_error())
        return data.raw
    def u32(self,address):return struct.unpack('<I',self.read(address,4))[0]
    def i32(self,address):return struct.unpack('<i',self.read(address,4))[0]
    def f32(self,address):return struct.unpack('<f',self.read(address,4))[0]


def configure(game,case_id):
    audit=Mapping(f'Local\\TH12CoopAudit_{game.proc.pid}',True)
    try:
        assert struct.unpack('<2I',audit.read(0,8))==(0x41423154,1),'Audit ABI mismatch'
        audit.write32(16,4);audit.write32(20,case_id);audit.write32(8,1)
        deadline=time.monotonic()+5
        while time.monotonic()<deadline:
            game.run(0,0,.02)
            if struct.unpack('<I',audit.read(12,4))[0]==1:
                result=struct.unpack('<I',audit.read(24,4))[0]
                details=list(struct.unpack('<32I',audit.read(32,128)))
                assert result==0,{'fixtureResult':hex(result),'details':details}
                return audit,details
        raise AssertionError('Campaign fixture command timed out')
    except Exception:
        audit.close();raise


def observe(game,reader,started):
    globals_=reader.read(0x4b0c40,0xa4)
    def word(address):return struct.unpack_from('<I',globals_,address-0x4b0c40)[0]
    pointers=reader.read(0x4b43d8,0x160)
    def pointer(address):return struct.unpack_from('<I',pointers,address-0x4b43d8)[0]
    enemy,gui,ending,pause,thread=(pointer(a) for a in (0x4b43dc,0x4b43e4,0x4b43d8,0x4b4510,0x4b44e8))
    boss=reader.u32(enemy+0x1c) if enemy else 0
    boss_info=None
    if boss:
        # GUI 1.00a 41EBC2/41EBCD confirms manager+1C is the FULL Enemy.
        x,y=struct.unpack('<2f',reader.read(boss+0x1074,8))
        boss_info={'x':x if math.isfinite(x) else None,'y':y if math.isfinite(y) else None,
                   'hp':reader.i32(boss+0x2648),'flags':reader.u32(boss+0x26f8)}
    return {'seconds':round(time.monotonic()-started,3),'telemetry':game.state(),
            'scene':reader.u32(0x4cee3c),'pendingScene':reader.u32(0x4cee40),
            'stage':word(0x4b0cb0),'rank':word(0x4b0ca8),'character':word(0x4b0c90),
            'shot':word(0x4b0c94),'modeFlags':word(0x4b0ce0),'score':word(0x4b0c44),
            'power':word(0x4b0c48),'lives':word(0x4b0c98),'continues':word(0x4b0cc4),
            'stageTime':reader.u32(enemy+0x54) if enemy else None,'boss':boss_info,
            'gameThreadTime':reader.u32(thread+0x14) if thread else None,
            'gameThreadFlags':reader.u32(thread+0x60) if thread else None,
            'dialogue':bool(gui and reader.u32(gui+0x6d30)),
            'guiFlags':reader.u32(gui+0x6d18) if gui else None,
            'pauseState':reader.u32(pause+4) if pause else None,
            'clearResult':reader.u32(pause+0x1f4) if pause else 0,
            'ending':{'index':reader.u32(ending+0x1c),'flags':reader.u32(ending+0x20),
                      'scriptLoaded':bool(reader.u32(ending+0x14))} if ending else None}


def input_for(snapshot):
    if snapshot['pauseState'] not in (None,0):return 0,0
    # Host's native dialogue accepts rising shot edges. Pulse only during the
    # real dialogue object; never issue pause/continue/retry/stage-skip commands.
    if snapshot['dialogue']:
        return (1 if int(snapshot['seconds']/.16)%2==0 else 0),0
    if snapshot['telemetry']['status']!=2:return 0,0
    boss=snapshot['boss'];target=boss['x'] if boss and boss['x'] is not None else 0.
    target=max(-150.,min(150.,target))
    masks=[]
    for number,offset in ((1,-16.),(2,16.)):
        x=snapshot['telemetry'][f'p{number}x'];y=snapshot['telemetry'][f'p{number}y']
        mask=1|8
        if x<target+offset-18:mask|=0x80
        elif x>target+offset+18:mask|=0x40
        if y<370:mask|=0x20
        elif y>402:mask|=0x10
        masks.append(mask)
    return tuple(masks)


def save(report,path):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(report,ensure_ascii=False,indent=2,allow_nan=False),encoding='utf8')


class ProgressWatch:
    """Observe real callback/timer progress; never repair or skip game state."""
    def __init__(self):self.stage=None;self.last={}
    def check(self,snapshot,now):
        st=snapshot['telemetry'];flags=snapshot['gameThreadFlags'] or 0
        eligible=(st['status']==2 and snapshot['scene']==7 and 1<=snapshot['stage']<=7
                  and snapshot['stageTime'] is not None and not snapshot['dialogue']
                  and snapshot['pauseState'] in (None,0) and not snapshot['ending']
                  and not flags&0x14)  # Native loading / pause, not delayed-init bit0x800.
        if not eligible:
            self.stage=None;self.last={};return None
        if self.stage!=snapshot['stage']:
            self.stage=snapshot['stage'];self.last={}
        values={'nativeStageTimer':snapshot['stageTime'],
                'playerCallbackFrame':st['frame'],'gameThreadTime':snapshot['gameThreadTime']}
        for name,value in values.items():
            if value is None:continue
            previous=self.last.get(name)
            if previous is None or previous[0]!=value:self.last[name]=(value,now)
            elif now-previous[1]>20:
                return {'stalledField':name,'value':value,'secondsWithoutProgress':round(now-previous[1],3),
                        'stage':snapshot['stage'],'gameThreadTime':snapshot['gameThreadTime'],
                        'gameThreadFlags':snapshot['gameThreadFlags'],'stageTime':snapshot['stageTime'],
                        'playerCallbackFrame':st['frame']}
        return None


def run(args):
    folder=fresh_copy(args.game,'campaign-'+args.mode,1,args.shot)
    report={'passed':False,'mode':args.mode,'shot':args.shot,'rank':args.rank,
            'game':str(folder),'freshScores':True,'fixture':{'family':4,'caseId':args.case,
            'invulnerability':60,'power':400,'nativeTimerSpeed':1,'damageMultiplier':4 if args.case==1 else 1},
            'scope':'Invulnerable native progression with native timing; not a normal-difficulty clear',
            'exeSha256':hashlib.sha256((folder/'th12.exe').read_bytes()).hexdigest(),
            'nativeSha256':hashlib.sha256((ROOT/'dist/th12coop-smoke.dll').read_bytes()).hexdigest(),
            'events':[],'stagesObserved':[],'stagesWithTwoPlayers':[],'maxPlayerShots':[0,0]}
    game=audit=None;started=time.monotonic()
    try:
        game=Game(folder,players=(args.shot,args.shot));report['pid']=game.proc.pid;reader=Reader(game)
        select(game,1,0 if args.mode=='story' else 1);game.pulse(1,.8)
        if args.mode=='story':select(game,5,args.rank)
        else:wait_menu(game,5)
        game.pulse(1,.8);select(game,6,args.shot//2);game.pulse(1,.8)
        select(game,7,args.shot%2);game.pulse(1,.8);actors(game)
        assert reader.u32(0x4b0cb0)==(1 if args.mode=='story' else 7),'Wrong starting stage'
        assert reader.u32(0x4b0ca8)==(args.rank if args.mode=='story' else 4),'Wrong rank'
        assert (reader.u32(0x4b0c90),reader.u32(0x4b0c94))==(args.shot//2,args.shot%2),'Wrong shot selection'
        audit,details=configure(game,args.case);report['configureDetails']=details
        assert details[1]==0x3f800000,{'unexpectedInitialNativeTimerSpeedBits':details[1]}
        deadline=started+args.timeout;last_key=None;last_report=0;invalid_reads=0;ending_samples=0;paused_since=None
        progress=ProgressWatch()
        while time.monotonic()<deadline:
            try:snapshot=observe(game,reader,started);invalid_reads=0
            except OSError:
                # Native asynchronous teardown can retire an object between a
                # pointer read and its fields. Retry the next observation only.
                invalid_reads+=1
                if invalid_reads>=20:raise
                game.run(0,0,.02);continue
            report['last']=snapshot;stage=snapshot['stage'];st=snapshot['telemetry']
            if 1<=stage<=7 and stage not in report['stagesObserved']:report['stagesObserved'].append(stage)
            if st['status']==2 and st['p1state'] in (0,1) and st['p2state'] in (0,1):
                if stage not in report['stagesWithTwoPlayers']:report['stagesWithTwoPlayers'].append(stage)
            for n in (1,2):report['maxPlayerShots'][n-1]=max(report['maxPlayerShots'][n-1],st[f'p{n}bullets'])
            key=(stage,snapshot['scene'],snapshot['dialogue'],snapshot['pauseState'],
                 bool(snapshot['ending']),st['p1state'],st['p2state'])
            if key!=last_key or snapshot['seconds']-last_report>=10:
                report['events'].append(snapshot);last_key=key;last_report=snapshot['seconds']
                save(report,args.out)
                print(json.dumps({'seconds':snapshot['seconds'],'stage':stage,'scene':snapshot['scene'],
                                  'dialogue':snapshot['dialogue'],'boss':snapshot['boss'],
                                  'ending':snapshot['ending'],'pause':snapshot['pauseState']}),flush=True)
            if snapshot['ending'] and snapshot['ending']['scriptLoaded']:ending_samples+=1
            else:ending_samples=0
            if args.mode=='story' and ending_samples>=3:
                assert report['stagesObserved']==list(range(1,7)),report['stagesObserved']
                assert set(range(1,7))<=set(report['stagesWithTwoPlayers']),report['stagesWithTwoPlayers']
                assert snapshot['ending']['index']==args.shot+6,snapshot['ending']
                report['completion']='Native ending object and loaded ending script after stages1..6';break
            if args.mode=='extra' and stage==7 and snapshot['clearResult']==1 and snapshot['pauseState'] in range(20,27):
                # Dialogue 420C75 calls native clear-result entry 433870;
                # that entry sets state21 and +1F4=1. Miss/gameover sets +1F4=0.
                assert report['stagesObserved']==[7],report['stagesObserved']
                assert report['stagesWithTwoPlayers']==[7],report['stagesWithTwoPlayers']
                report['completion']='Native Extra clear-result state with clear flag';break
            if snapshot['pauseState'] in range(13,20) and not snapshot['clearResult']:
                raise AssertionError(f'Unexpected game-over/result state: {snapshot}')
            if snapshot['pauseState'] in range(1,11):
                if paused_since is None:paused_since=time.monotonic()
                elif time.monotonic()-paused_since>3:
                    raise AssertionError(f'Unexpected pause menu blocks campaign progression: {snapshot}')
            else:paused_since=None
            stalled=progress.check(snapshot,time.monotonic())
            if stalled:
                report['stall']=stalled
                raise AssertionError(f'Native campaign progression stalled: {stalled}')
            game.run(*input_for(snapshot),.035)
        else:raise TimeoutError(f'No native campaign completion within {args.timeout}s')
        assert all(report['maxPlayerShots']),report['maxPlayerShots']
        assert report['last']['continues']==0,'Fixture unexpectedly continued'
        report['fixtureDetails']=list(struct.unpack('<32I',audit.read(32,128)))
        report['passed']=True
    except Exception as error:
        report['error']=f'{type(error).__name__}: {error}'
        raise
    finally:
        if game:report['menuNavigation']=getattr(game,'menu_navigation',[])
        if audit:audit.close()
        if game:game.close()
        report['ownedProcessExited']=game is None or game.proc.poll() is not None
        report['elapsedSeconds']=round(time.monotonic()-started,3)
        save(report,args.out)
        print(json.dumps({key:report.get(key) for key in ('passed','completion','error','stagesObserved','elapsedSeconds')}),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--game',type=Path,required=True)
    parser.add_argument('--mode',choices=['story','extra'],required=True)
    parser.add_argument('--shot',type=int,choices=range(6),default=0)
    parser.add_argument('--rank',type=int,choices=range(4),default=1)
    parser.add_argument('--case',type=int,choices=[1,2],default=1)
    parser.add_argument('--timeout',type=int,default=2400)
    parser.add_argument('--out',type=Path,required=True)
    run(parser.parse_args())
