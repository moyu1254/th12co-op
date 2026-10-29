"""Deterministic retry/title/reentry and missing-input checks on two real games."""
import argparse, hashlib, json, struct, time
from pathlib import Path
from test_native_lockstep import NetGame, ROOT, k, read_process
from thread_snapshot import enemies

parser=argparse.ArgumentParser();parser.add_argument('--host-game',type=Path,default=ROOT/'game-net-host');parser.add_argument('--guest-game',type=Path,default=ROOT/'game-net-guest');parser.add_argument('--out',type=Path,default=ROOT/'artifacts/lockstep');args=parser.parse_args()
games=[];report={'passed':False,'checkpoints':[],'game_folders':[str(args.host_game),str(args.guest_game)],'exe_sha256':hashlib.sha256((args.host_game/'th12.exe').read_bytes()).hexdigest()};frame=1

def boundary():
    limit=time.monotonic()+20
    while True:
        for game in games:game.heartbeat()
        heads=[game.header() for game in games]
        if any(h[8]==4 for h in heads):raise AssertionError(('native failure',frame,heads))
        if all(h[9]==frame for h in heads):break
        if time.monotonic()>limit:raise AssertionError(('timeout',frame,heads))
        time.sleep(.001)
    assert heads[0][12:14]==heads[1][12:14],('diverged',frame,heads)
    return struct.unpack('<8I',games[0].map.read(4352+((frame-1)%256)*32,32))

def advance(count,p1=0,p2=0):
    global frame
    for _ in range(count):
        boundary()
        for game in games:game.commit(frame,p1,p2)
        frame+=1
    return boundary()

def pulse(mask,count=24):
    advance(3,mask);return advance(count-3)

def word(address):
    values=[struct.unpack('<I',read_process(g.info.pid,address,4))[0] for g in games]
    assert values[0]==values[1],(hex(address),values)
    return values[0]

def checkpoint(name):
    digest=boundary();row={'name':name,'frame':frame-1,'digest':list(digest),'bombs':word(0x4b0ca0),'lives':word(0x4b0c98)}
    report['checkpoints'].append(row);print(json.dumps(row),flush=True);return digest

def enter():
    for _ in range(60):
        d=boundary()
        if d[3]==7 and d[5]==257:return d
        pulse(1,30)
    raise AssertionError('both actors did not enter stage')

try:
    for folder in (args.host_game,args.guest_game):games.append(NetGame(folder,0x9abc))
    for game in games:k.ResumeThread(game.info.thread)
    enter();advance(120);checkpoint('initial')
    advance(3,0,2);advance(90);assert word(0x4b0ca0)==1
    checkpoint('p2_bomb')
    pulse(0x100,30)
    for _ in range(3):pulse(0x20)
    pulse(1,30);pulse(0x10);pulse(1,90)
    d=checkpoint('retry');assert d[5]==257 and word(0x4b0ca0)==2
    advance(180,1,1);checkpoint('retry_shooting')
    pulse(0x100,30);pulse(0x20);pulse(1,30);pulse(0x10);pulse(1,90)
    for _ in range(600):
        d=advance(1)
        if d[3]==4 and d[5]==0xffff:break
    else:raise AssertionError('title did not release both actors')
    checkpoint('title_released')
    enter();advance(120,0x41,0x81);checkpoint('reentered')
    # Withhold one frame from both games while maintaining the launcher
    # heartbeat. Neither native simulation nor its input counters may advance.
    before=[game.header()[9:14] for game in games];game_frame=word(0x4b0cbc)
    until=time.monotonic()+2
    while time.monotonic()<until:
        for game in games:game.heartbeat()
        time.sleep(.01)
    assert before==[game.header()[9:14] for game in games]
    assert word(0x4b0cbc)==game_frame
    advance(10,1,1);checkpoint('missing_input_resumed')
    # Reject an illegal bit before a world update, with a visible native error.
    for game in games:game.commit(frame,4,0)
    limit=time.monotonic()+2
    while time.monotonic()<limit:
        for game in games:game.heartbeat()
        if all(game.header()[8]==4 for game in games):break
        time.sleep(.01)
    assert all(game.header()[8]==4 and game.header()[14]==4 for game in games)
    assert all(game.header()[10]==frame-1 for game in games)
    report['invalid_mask_rejected']=True;report['completed_frames']=frame-1;report['passed']=True
except Exception as error:
    report['enemies']=[enemies(game.info.pid) for game in games]
    report['error']=str(error);raise
finally:
    for game in games:game.close()
    path=args.out/'native-lifecycle.json';path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(report,indent=2),encoding='utf8')
    print(json.dumps({k:v for k,v in report.items() if k!='enemies'}),flush=True)
