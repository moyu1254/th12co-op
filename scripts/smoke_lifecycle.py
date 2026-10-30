"""Retry and title/re-entry checks through the native test input interface."""
import argparse, hashlib, json
from smoke_gameplay import Game, ROOT
from pathlib import Path

parser=argparse.ArgumentParser();parser.add_argument('--game',type=Path,default=ROOT/'game-smoke');parser.add_argument('--out',type=Path,default=ROOT/'artifacts/offline-smoke');args=parser.parse_args()
out=args.out;out.mkdir(parents=True,exist_ok=True)
game=None
report={'game':str(args.game),'exe_sha256':hashlib.sha256((args.game/'th12.exe').read_bytes()).hexdigest()}
try:
    game=Game(args.game,players=(0,0))
    def enter():
        game.confirm_until(5);game.confirm_until(6);game.confirm_until(7)
        game.pulse(1,2)
        for _ in range(100):
            state=game.run(0,0,.05)
            if state['status']==2 and state['p1state']==1 and state['p2state']==1:return state
        raise AssertionError('Stage did not activate both players')
    report['initial']=enter()
    game.run(0x41,0x81,.2);game.run(0,2,.15);game.run(0,0,.2)
    report['before_retry']={'telemetry':game.state(),'bombs':game.u32(0x4b0ca0)}
    game.pulse(0x100,.3)
    for _ in range(3):game.pulse(0x20,.25)
    game.pulse(1,.5);game.pulse(0x10,.25);game.pulse(1,2)
    state=game.run(0,0,.3)
    assert state['status']==2 and state['p1state']==1 and state['p2state']==1,state
    assert game.u32(0x4b0ca0)==2,'Retry did not reset bombs'
    report['after_retry']={'telemetry':state,'bombs':game.u32(0x4b0ca0)}
    state=game.run(1,1,.4)
    assert state['p1bullets']>0 and state['p2bullets']>0,state
    report['retry_shooting']=state
    game.pulse(0x100,.3);game.pulse(0x20,.25);game.pulse(1,.5)
    game.pulse(0x10,.25);game.pulse(1,1)
    for _ in range(100):
        state=game.run(0,0,.05)
        if state['status']==1 and game.u32(0x4b4514)==0:break
    else:raise AssertionError('Title did not release players')
    report['released']=state
    report['reentered']=enter()
    state=game.run(0x41,0x81,.3)
    assert state['p1x']<0 and state['p2x']>0 and state['p1bullets']>0 and state['p2bullets']>0,state
    report['reentered_shooting']=state
    report['passed']=True
except Exception as error:
    report['passed']=False;report['error']=str(error)
    raise
finally:
    if game:game.close()
    (out/'lifecycle.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps(report),flush=True)
