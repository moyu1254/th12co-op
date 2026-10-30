"""Observe P2 collision and respawn using only ordinary movement input."""
import argparse, hashlib, json
from smoke_gameplay import Game, ROOT
from pathlib import Path

parser=argparse.ArgumentParser();parser.add_argument('--game',type=Path,default=ROOT/'game-smoke');parser.add_argument('--out',type=Path,default=ROOT/'artifacts/offline-smoke');args=parser.parse_args()
out=args.out;out.mkdir(parents=True,exist_ok=True)
game=None;report={'game':str(args.game),'exe_sha256':hashlib.sha256((args.game/'th12.exe').read_bytes()).hexdigest(),'states':[]}
try:
    game=Game(args.game,players=(0,0))
    game.confirm_until(5);game.confirm_until(6);game.confirm_until(7);game.pulse(1,2)
    game.run(0,0,2)
    assert game.state()['status']==2
    report['lives_before']=game.u32(0x4b0c98)
    observed=set();p2_hit=False;respawn=False
    for step in range(1200):
        st=game.state()
        pair=(st['p1state'],st['p2state'])
        observed.add(pair)
        if st['p1state']==1 and st['p2state'] in (2,4):
            if not p2_hit:
                report['p2_hit_with_p1_healthy']=st
            p2_hit=True
        if p2_hit and st['p2state']==1:
            respawn=True;report['respawn']=st;break
        # P2 visits the two columns where the opening enemy waves enter.
        target_x=144 if (step//220)%2==0 else -144
        target_y=100
        p2=0
        if not p2_hit:
            if st['p2x']<target_x-7:p2|=0x80
            elif st['p2x']>target_x+7:p2|=0x40
            if st['p2y']>target_y+7:p2|=0x10
            elif st['p2y']<target_y-7:p2|=0x20
        p1=0x40 if st['p1x']>-178 else 0
        game.run(p1,p2,.05)
    report['states']=sorted(observed)
    report['lives_after']=game.u32(0x4b0c98)
    assert p2_hit and respawn,report
    assert report['lives_after']<report['lives_before'],report
    before=game.state();after=game.run(0,0x81,.2)
    assert after['p2x']>before['p2x'] and after['p2bullets']>0,after
    report['post_respawn_controls']=after;report['passed']=True
except Exception as error:
    report['passed']=False;report['error']=str(error)
    raise
finally:
    if game:game.close()
    (out/'p2-collision.json').write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps(report),flush=True)
