"""Input-only Extra/practice entry, retry and title teardown on a fresh test copy.

The smoke DLL supplies menu input. This script never writes process memory or
copies solo score/replay files. Every invocation creates its own disposable CWD.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import time
import uuid
from smoke_gameplay import Game, ROOT
from probe import read_process


def fresh_copy(source, mode, stage, shot):
    source=source.resolve()
    if not source.is_relative_to(ROOT.resolve()):
        raise ValueError('Use an already prepared workspace game copy, not an installed game folder')
    target=ROOT/f'game-test-mode-{mode}-{stage}-{shot}-{uuid.uuid4().hex[:10]}'
    target.mkdir(parents=True)
    names={'th12.exe','th12.dat','thbgm.dat'}
    names.update(p.name for p in source.glob('*.dll'))
    if (source/'steam_appid.txt').exists():names.add('steam_appid.txt')
    for name in names:os.link(source/name,target/name)
    (target/'th12.cfg').write_bytes((ROOT/'profiles/th12-online.cfg').read_bytes())
    return target


def menu(game):
    p=game.u32(0x4b4530)
    return (game.u32(p+0x1c),game.u32(p+0x24),game.u32(p+0x28)) if p else (-1,-1,-1)


def menu_diagnostic(game):
    p=game.u32(0x4b4530);result={'menu':list(menu(game))}
    if p:
        count=game.u32(p+0xfc)
        result.update(choiceCount=game.u32(p+0x30),disabledCount=count,
                      disabled=[game.u32(p+0xb8+n*4) for n in range(min(count,16))],
                      timer=game.u32(p+0x2b8))
    result['rawInput']=[game.u32(0x4d48b8+n*4) for n in range(5)]
    score=game.u32(0x4b451c)
    result['originalExtraFlags']=list(read_process(game.proc.pid,score+0x1e9d0,6)) if score else None
    # The two supported code profiles share the A1 pointer-load / B1 10
    # signature; inspect its patched operand to include the actual query view.
    for address in (0x43f160,0x43f180):
        code=read_process(game.proc.pid,address,7)
        if code[0]==0xa1 and code[5:7]==b'\xb1\x10':
            view=game.u32(int.from_bytes(code[1:5],'little'))
            result['queryAddress']=hex(address)
            result['queryExtraFlags']=list(read_process(game.proc.pid,view+0x1e9d0,6)) if view else None
            break
    return result


def wait_menu(game, state, interactive=True):
    until=time.monotonic()+15
    while time.monotonic()<until:
        current=menu(game)
        if current[0]==state and (not interactive or current[1]==2):return current
        game.run(0,0,.05)
    raise AssertionError(f'Menu {state} did not become ready; got {menu(game)}')


def select(game, state, index):
    wait_menu(game,state)
    if not hasattr(game,'menu_navigation'):game.menu_navigation=[]
    for _ in range(16):
        current=menu(game)
        if current[0]!=state:raise AssertionError(f'Menu changed while selecting: {current}')
        if current[2]==index:return
        trace={'target':index,'before':menu_diagnostic(game)}
        game.run(0x20 if current[2]<index else 0x10,0,.15)
        trace['held']=menu_diagnostic(game)
        game.run(0,0,.22);trace['released']=menu_diagnostic(game)
        game.menu_navigation.append(trace)
    raise AssertionError(f'Could not select {index} in menu {state}: {menu(game)}')


def actors(game):
    until=time.monotonic()+20
    while time.monotonic()<until:
        st=game.run(0,0,.05)
        if st['status']==2 and st['p1state']==1 and st['p2state']==1:return st
    raise AssertionError(f'Two active actors never appeared: {game.state()}')


def checkpoint(game):
    thread=game.u32(0x4b44e8);enemies=game.u32(0x4b43dc)
    return {'telemetry':game.state(),'stage':game.u32(0x4b0cb0),
            'rank':game.u32(0x4b0ca8),'character':game.u32(0x4b0c90),
            'weapon':game.u32(0x4b0c94),'modeFlags':game.u32(0x4b0ce0),
            'bombs':game.u32(0x4b0ca0),
            'gameTime':game.u32(thread+0x14) if thread else None,
            'stageTime':game.u32(enemies+0x54) if enemies else None,
            'activationCount':activation_count(game)}


def activation_count(game):
    path=game.folder/'th12coop-native.log'
    text=path.read_text(encoding='utf8',errors='replace') if path.exists() else ''
    return text.count('Both Player objects activated through native reset')


def pause_diagnostic(game):
    p=game.u32(0x4b4510);thread=game.u32(0x4b44e8)
    result={'state':game.u32(p+4) if p else None,
            'gameTime':game.u32(thread+0x14) if thread else None,
            'gameFlags':game.u32(thread+0x60) if thread else None,
            'bombs':game.u32(0x4b0ca0),'activationCount':activation_count(game)}
    if p:
        disabled=game.u32(p+0x10c)
        result.update(timer=game.u32(p+0x14),cursor=game.u32(p+0x38),
                      choiceCount=game.u32(p+0x40),disabledCount=disabled,
                      disabled=[game.u32(p+0xc8+4*n) for n in range(min(disabled,16))])
    return result


def pause_trace(game,action,**extra):
    if not hasattr(game,'pause_navigation'):game.pause_navigation=[]
    value=pause_diagnostic(game)
    game.pause_navigation.append({'action':action,**extra,**value})
    return value


def wait_pause(game,state,count=None,min_timer=0):
    until=time.monotonic()+8
    while time.monotonic()<until:
        current=pause_diagnostic(game)
        if (current['state']==state and current.get('timer',0)>=min_timer
                and (count is None or current.get('choiceCount')==count)):
            return current
        game.run(0,0,.03)
    raise AssertionError(f'Pause state {state} count {count} not ready: {pause_diagnostic(game)}')


def open_pause(game):
    # Native 1.00a 432064 / 1.00b 432084 refuses Escape before game tick30.
    # Actor activation alone does not imply that the pause menu can open.
    until=time.monotonic()+8
    while time.monotonic()<until:
        current=pause_diagnostic(game)
        if current['state']==0 and current['gameTime'] is not None and current['gameTime']>=30:break
        game.run(0,0,.03)
    else:raise AssertionError(f'Gameplay did not become pausable: {pause_diagnostic(game)}')
    pause_trace(game,'before-escape')
    game.pulse(0x100,.1)
    wait_pause(game,3,4,11)
    result=pause_trace(game,'pause-open')
    assert result['gameFlags']&0x10,result
    return result


def select_pause(game,index,state=3,count=4):
    wait_pause(game,state,count,30 if state==5 else 11)
    for _ in range(12):
        current=pause_diagnostic(game)
        assert current['state']==state and current['choiceCount']==count,current
        assert index not in current['disabled'],current
        if current['cursor']==index:return pause_trace(game,'selected',target=index)
        key=0x20 if current['cursor']<index else 0x10
        pause_trace(game,'before-key',target=index,key=key)
        game.run(key,0,.15);pause_trace(game,'key-held',target=index,key=key)
        game.run(0,0,.12);pause_trace(game,'key-released',target=index,key=key)
    raise AssertionError(f'Pause cursor did not reach {index}: {pause_diagnostic(game)}')


def confirm_pause_action(game,index):
    # Shared native action table: 0 Resume, 1 Title, 2 Save replay, 3 Retry.
    # Title/Retry use state5, then two choices (0 Yes / 1 No) after timer30.
    assert index in (1,3)
    open_pause(game);select_pause(game,index)
    game.pulse(1,.05)
    wait_pause(game,5,2,30)
    pause_trace(game,'confirmation-ready',actionIndex=index)
    select_pause(game,0,state=5,count=2)
    pause_trace(game,'confirm-yes',actionIndex=index)
    game.run(1,0,.08)
    game.run(0,0,.01)


def verify_retry(game,before):
    # All three witnesses are required. Merely finding two active actors after
    # menu inputs also succeeds when Escape was ignored and no retry happened.
    until=time.monotonic()+20;observations=[];timer_reset=None
    while time.monotonic()<until:
        game.run(0,0,.02)
        try:current=checkpoint(game)
        except OSError:continue  # Objects can be destroyed between two reads.
        observations.append({name:current[name] for name in
                             ('gameTime','stageTime','bombs','activationCount')})
        if (current['activationCount']>before['activationCount']
                and current['stageTime'] is not None and before['stageTime'] is not None
                and current['stageTime']<before['stageTime']):
            timer_reset={'before':before['stageTime'],'after':current['stageTime']}
        st=current['telemetry']
        if (timer_reset and current['activationCount']>before['activationCount']
                and current['bombs']==2 and st['status']==2
                and st['p1state']==st['p2state']==1):
            return current,{'stageTimerReset':timer_reset,'observations':observations}
    raise AssertionError(f'Retry lacked bomb reset, new activation or stage-timer reset: before={before}; observations={observations[-20:]}')


def run(args):
    folder=fresh_copy(args.game,args.mode,args.stage,args.shot)
    report={'passed':False,'mode':args.mode,'stage':args.stage,'shot':args.shot,
            'rank':args.rank,'game':str(folder),'freshScores':True,'checks':{},
            'exeSha256':hashlib.sha256((folder/'th12.exe').read_bytes()).hexdigest(),
            'nativeSha256':hashlib.sha256((ROOT/'dist/th12coop-smoke.dll').read_bytes()).hexdigest()}
    game=None
    try:
        game=Game(folder);report['pid']=game.proc.pid
        # Native MainMenu states: 1 title, 5 rank, 6 character, 7 shot, 8 stage.
        # The title action table at 1.00b 4411A4 maps 1 Extra / 2 Practice.
        select(game,1,1 if args.mode=='extra' else 2);game.pulse(1,.8)
        if args.mode=='practice':
            select(game,5,args.rank);game.pulse(1,.8)
        else:
            # Extra has one rank entry (rank4); native 44512A still requires
            # confirmation before advancing to the character screen.
            wait_menu(game,5);game.pulse(1,.8)
        select(game,6,args.shot//2);game.pulse(1,.8)
        select(game,7,args.shot%2);game.pulse(1,.8)
        if args.mode=='practice':
            select(game,8,args.stage-1);report['checks']['practiceSelection']=list(menu(game));game.pulse(1,.8)
        actors(game);expected_stage=7 if args.mode=='extra' else args.stage
        first=checkpoint(game)
        assert first['stage']==expected_stage,first
        assert first['rank']==(4 if args.mode=='extra' else args.rank),first
        assert (first['character'],first['weapon'])==(args.shot//2,args.shot%2),first
        assert bool(first['modeFlags']&0x10)==(args.mode=='practice'),first
        report['checks']['modeEntry']=first
        before=game.state();after=game.run(0x41,0x81,.2)
        assert after['p1x']<before['p1x'] and after['p2x']>before['p2x'],(before,after)
        assert after['p1bullets'] and after['p2bullets'],after
        report['checks']['twoPlayerControls']=after
        assert game.u32(0x4b0ca0)==2,'Fresh mode did not start with two team bombs'
        game.run(0,2,.15);game.run(0,0,.1)
        before_retry=checkpoint(game)
        assert before_retry['bombs']==1,before_retry
        assert before_retry['activationCount']>=1,before_retry
        report['checks']['p2BombBeforeRetry']=before_retry
        confirm_pause_action(game,3)
        retried,retry_evidence=verify_retry(game,before_retry)
        assert retried['stage']==expected_stage and retried['rank']==first['rank'],retried
        assert (retried['character'],retried['weapon'])==(first['character'],first['weapon']),retried
        assert bool(retried['modeFlags']&0x10)==(args.mode=='practice'),retried
        assert retried['telemetry']['p1state']==retried['telemetry']['p2state']==1,retried
        report['checks']['retry']=retried;report['checks']['retryEvidence']=retry_evidence
        shooting=game.run(1,1,.2)
        assert shooting['p1bullets'] and shooting['p2bullets'],shooting
        report['checks']['retryShooting']=shooting
        confirm_pause_action(game,1)
        until=time.monotonic()+15
        while time.monotonic()<until:
            st=game.run(0,0,.05)
            if st['status']==1 and game.u32(0x4b4514)==0:break
        else:raise AssertionError(f'Title teardown did not release both actors: {game.state()}')
        report['checks']['titleTeardown']=st;report['passed']=True
    except Exception as error:
        report['error']=str(error)
        if game and game.proc.poll() is None:
            try:
                report['lastMenu']=list(menu(game));report['lastTelemetry']=game.state()
                report['lastPause']=pause_diagnostic(game)
            except OSError:pass
        raise
    finally:
        if game:report['menuNavigation']=getattr(game,'menu_navigation',[])
        if game:report['pauseNavigation']=getattr(game,'pause_navigation',[])
        if game:game.close()
        report['ownedProcessExited']=game is None or game.proc.poll() is not None
        args.out.parent.mkdir(parents=True,exist_ok=True)
        args.out.write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
        print(json.dumps(report,ensure_ascii=False),flush=True)


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--game',type=Path,required=True)
    parser.add_argument('--mode',choices=['extra','practice'],required=True)
    parser.add_argument('--stage',type=int,choices=range(1,7),default=1)
    parser.add_argument('--shot',type=int,choices=range(6),default=0)
    parser.add_argument('--rank',type=int,choices=range(4),default=1)
    parser.add_argument('--out',type=Path,required=True)
    run(parser.parse_args())
