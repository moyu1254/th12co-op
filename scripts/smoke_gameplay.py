"""End-to-end TH12 retail checks driven through the smoke DLL input API.
Runs only isolated workspace game-smoke/game-test-* copies; never writes game
process memory.
"""
import argparse, ctypes, hashlib, json, struct, subprocess, sys, time
from ctypes import wintypes
from pathlib import Path
from probe import Mapping, telemetry, k, read_process
from quiet_test import QuietEnvironment, quiet_configuration, quiet_evidence
ROOT=Path(__file__).resolve().parent.parent

# Same Windows loader-entry fence as the launcher and test_native_lockstep.py.
# Keep this small adapter local: importing the network harness would change
# ctypes signatures and tie unrelated fixture entry points together.
class Startup(ctypes.Structure):
    _fields_=[('cb',wintypes.DWORD),('reserved',wintypes.LPWSTR),('desktop',wintypes.LPWSTR),('title',wintypes.LPWSTR),('x',wintypes.DWORD),('y',wintypes.DWORD),('xs',wintypes.DWORD),('ys',wintypes.DWORD),('xc',wintypes.DWORD),('yc',wintypes.DWORD),('fill',wintypes.DWORD),('flags',wintypes.DWORD),('show',wintypes.WORD),('reserved2bytes',wintypes.WORD),('reserved2',ctypes.c_void_p),('stdin',wintypes.HANDLE),('stdout',wintypes.HANDLE),('stderr',wintypes.HANDLE)]
class ProcessInfo(ctypes.Structure):
    _fields_=[('process',wintypes.HANDLE),('thread',wintypes.HANDLE),('pid',wintypes.DWORD),('tid',wintypes.DWORD)]
k.CreateProcessW.argtypes=[wintypes.LPCWSTR,wintypes.LPWSTR,ctypes.c_void_p,ctypes.c_void_p,wintypes.BOOL,wintypes.DWORD,ctypes.c_void_p,wintypes.LPCWSTR,ctypes.POINTER(Startup),ctypes.POINTER(ProcessInfo)]
k.CreateProcessW.restype=wintypes.BOOL
k.CreateFileMappingW.argtypes=[wintypes.HANDLE,ctypes.c_void_p,wintypes.DWORD,wintypes.DWORD,wintypes.DWORD,wintypes.LPCWSTR]
k.CreateFileMappingW.restype=wintypes.HANDLE
k.ResumeThread.argtypes=[wintypes.HANDLE];k.ResumeThread.restype=wintypes.DWORD
k.TerminateProcess.argtypes=[wintypes.HANDLE,wintypes.UINT];k.TerminateProcess.restype=wintypes.BOOL
k.WaitForSingleObject.argtypes=[wintypes.HANDLE,wintypes.DWORD];k.WaitForSingleObject.restype=wintypes.DWORD
k.GetExitCodeProcess.argtypes=[wintypes.HANDLE,ctypes.POINTER(wintypes.DWORD)];k.GetExitCodeProcess.restype=wintypes.BOOL

class OwnedProcess:
    def __init__(self,folder):
        self.info=ProcessInfo();self.returncode=None;self.args=[str(folder/'th12.exe')]
        self.quiet=QuietEnvironment(ROOT)
        startup=Startup();startup.cb=ctypes.sizeof(startup)
        startup.desktop=self.quiet.name
        command=ctypes.create_unicode_buffer('"'+self.args[0]+'"')
        if not k.CreateProcessW(self.args[0],command,None,None,False,4,None,str(folder),ctypes.byref(startup),ctypes.byref(self.info)):
            self.quiet.close()
            raise ctypes.WinError(ctypes.get_last_error())
        self.pid=self.info.pid
        try:self.quiet.assign(self.info.process)
        except Exception:
            k.TerminateProcess(self.info.process,1);k.WaitForSingleObject(self.info.process,5000)
            self.close_handles();raise
    def poll(self):
        if self.returncode is not None or not self.info.process:return self.returncode
        state=k.WaitForSingleObject(self.info.process,0)
        if state==258:return None
        if state!=0:raise ctypes.WinError(ctypes.get_last_error())
        value=wintypes.DWORD()
        if not k.GetExitCodeProcess(self.info.process,ctypes.byref(value)):raise ctypes.WinError(ctypes.get_last_error())
        self.returncode=value.value
        return self.returncode
    def terminate(self):
        if self.poll() is None and not k.TerminateProcess(self.info.process,0):
            error=ctypes.get_last_error()
            # A process can finish between poll and termination. Windows also
            # returns access denied while an earlier exit is still completing.
            # Accept that race only after this owned handle is signaled.
            try:self.wait(10)
            except Exception:raise ctypes.WinError(error)
    def wait(self,timeout=None):
        if self.returncode is not None:return self.returncode
        result=k.WaitForSingleObject(self.info.process,0xffffffff if timeout is None else max(0,int(timeout*1000)))
        if result==258:raise subprocess.TimeoutExpired(self.args,timeout)
        if result!=0:raise ctypes.WinError(ctypes.get_last_error())
        return self.poll()
    def resume(self):
        if k.ResumeThread(self.info.thread)==0xffffffff:raise ctypes.WinError(ctypes.get_last_error())
        k.CloseHandle(self.info.thread);self.info.thread=None
    def close_handles(self):
        if self.info.thread:k.CloseHandle(self.info.thread);self.info.thread=None
        if self.info.process:k.CloseHandle(self.info.process);self.info.process=None
        self.quiet.close()

class Game:
    def __init__(self,folder):
        self.proc=None;self.map=None;self.boot=None;self.ready=False
        try:self._start(folder)
        except Exception:
            self.close();raise
    def _start(self,folder):
        folder=Path(folder).resolve()
        if folder.parent!=ROOT.resolve() or not (folder.name=='game-smoke' or folder.name.startswith('game-test-')):
            raise ValueError('Smoke fixtures require a direct workspace game-smoke or game-test-* folder')
        self.folder=folder
        cfg=quiet_configuration((folder/'th12.cfg').read_bytes())
        (folder/'th12.cfg').write_bytes(cfg)
        self.proc=OwnedProcess(folder)
        boot_name=f'Local\\TH12CoopBoot_{self.proc.pid}'
        handle=k.CreateFileMappingW(wintypes.HANDLE(-1),None,4,0,16,boot_name)
        if not handle:raise ctypes.WinError(ctypes.get_last_error())
        try:
            if ctypes.get_last_error()==183:raise RuntimeError('Startup mapping already exists')
            self.boot=Mapping(boot_name,True)
        finally:k.CloseHandle(handle)
        for offset,value in [(0,0x42323154),(4,1),(8,self.proc.info.tid),(12,1)]:self.boot.write32(offset,value)
        subprocess.run([str(ROOT/'dist/injector.exe'),'--early','--quiet-test',str(self.proc.pid),str(self.proc.info.tid),str(ROOT/'dist/th12coop-smoke.dll')],check=True,capture_output=True,text=True,timeout=40,creationflags=subprocess.CREATE_NO_WINDOW)
        self.quiet_evidence=quiet_evidence(self.proc.pid)
        for _ in range(100):
            try:self.map=Mapping(f'Local\\TH12Coop_{self.proc.pid}',True);break
            except OSError:time.sleep(.05)
        if not self.map:raise RuntimeError('Native control mapping absent')
        for _ in range(200):
            state=self.state()
            if state['status']!=0 and (state['magic']!=0x50323154 or state['version']!=1):raise RuntimeError(f'Invalid native control mapping {state}')
            if state['status'] in (1,5):break
            if state['status']&0x80000000:raise RuntimeError(f'Native initialization failed {state}')
            if self.proc.poll() is not None:raise RuntimeError(f'Game exited during injection: {self.proc.returncode}')
            time.sleep(.05)
        else:raise RuntimeError(f'Native startup fence not ready {self.state()}')
        self.proc.resume()
        self.run(0,0,4)
        if self.state()['status']!=1:raise RuntimeError(f'Native initialization failed {self.state()}')
    def state(self):return telemetry(self.map)
    def u32(self,a):return struct.unpack('<I',read_process(self.proc.pid,a,4))[0]
    def menu_state(self):
        p=self.u32(0x4b4530)
        return self.u32(p+0x1c) if p else -1
    def confirm_until(self,state):
        for _ in range(20):
            if self.menu_state()==state:return
            self.pulse(1)
        raise AssertionError(f'Menu state {state} not reached: {self.menu_state()}')
    def _wait_ready(self):
        deadline=time.monotonic()+120;ready=None
        try:
            while time.monotonic()<deadline:
                if self.proc.poll() is not None:raise RuntimeError(f'Game exited before device readiness: {self.proc.returncode}')
                state=self.state()
                if state['status']&0x80000000:raise RuntimeError(f'Native initialization failed {state}')
                if ready is None:
                    try:ready=Mapping(f'Local\\TH12CoopReady_{self.proc.pid}')
                    except OSError:pass
                if ready is not None:
                    magic,version,device,_=struct.unpack('<4I',ready.read(0,16))
                    if magic==0x52323154 and version==1 and device==1 and state['status']==1:
                        self.quiet_evidence=quiet_evidence(self.proc.pid)
                        if self.quiet_evidence['failures']:raise RuntimeError(f'Audio guard failure: {self.quiet_evidence}')
                        self.ready=True;return
                self.map.write32(44,k.GetTickCount());self.map.write32(8,0);self.map.write32(12,0);self.map.write32(16,3)
                time.sleep(.05)
            raise RuntimeError(f'Game device readiness timeout {self.state()}')
        finally:
            if ready:ready.close()
            if self.boot:self.boot.close();self.boot=None
    def run(self,a,b,seconds):
        if not self.ready:self._wait_ready()
        end=time.monotonic()+seconds
        while time.monotonic()<end:
            if self.proc.poll() is not None:raise RuntimeError(f'Game exited: {self.proc.returncode}')
            self.map.write32(44,k.GetTickCount());self.map.write32(8,a);self.map.write32(12,b);self.map.write32(16,3)
            time.sleep(.008)
        return self.state()
    def pulse(self,key,release=.65):
        self.run(key,0,.15);return self.run(0,0,release)
    def close(self):
        try:
            if self.map:
                self.map.write32(8,0);self.map.write32(12,0);self.map.write32(16,0);self.map.close();self.map=None
        finally:
            try:
                if self.boot:self.boot.close();self.boot=None
            finally:
                if self.proc:
                    try:
                        if self.proc.poll() is None:self.proc.terminate();self.proc.wait(10)
                    finally:self.proc.close_handles()

def check(shot,folder,out,death=False):
    game=None;report={'shot':shot,'game':str(folder),'exe_sha256':hashlib.sha256((folder/'th12.exe').read_bytes()).hexdigest(),'checks':{}}
    try:
        game=Game(folder);report['pid']=game.proc.pid
        game.confirm_until(5) # rank (title intro can still be loading)
        game.confirm_until(6) # character
        for _ in range(shot//2):game.pulse(0x20,.25)
        game.confirm_until(7) # weapon
        if shot%2:game.pulse(0x20,.25)
        game.pulse(1,2)
        for _ in range(100):
            st=game.run(0,0,.05)
            if st['status']==2 and st['p1state']==1 and st['p2state']==1:break
        else:raise AssertionError(f'Two active players never appeared: {st}')
        selected=(game.u32(0x4b0c90),game.u32(0x4b0c94))
        assert selected==(shot//2,shot%2),f'Wrong menu selection {selected}'
        report['checks']['two_native_players']=st
        before=game.state();after=game.run(0x41,0x81,.3)
        assert after['p1x']<before['p1x']-20 and after['p2x']>before['p2x']+20,(before,after)
        assert after['p1bullets']>0 and after['p2bullets']>0,after
        report['checks']['independent_movement_shooting']=after
        before=game.run(0,0,.12);after=game.run(0,0x40,.2)
        assert abs(after['p1x']-before['p1x'])<.01 and after['p2x']<before['p2x']-10,(before,after)
        report['checks']['p2_does_not_move_p1']=after
        if death:
            # P1 stays idle after its old projectiles expire. Only P2 shoots.
            game.run(0,0x40,.3)
            game.run(0,0,1.5)
            initial_score=game.u32(0x4b0c44)
            after=game.run(0,1,15)
            final_score=game.u32(0x4b0c44)
            assert after['p1bullets']==0 and final_score>initial_score,(initial_score,final_score,after)
            report['checks']['p2_only_enemy_damage']={'score_before':initial_score,'score_after':final_score,'telemetry':after}
        bombs=game.u32(0x4b0ca0)
        game.run(0,2,.15);game.run(0,0,.2)
        assert game.u32(0x4b0ca0)<bombs,'P2 bomb did not consume a bomb'
        report['checks']['p2_bomb']={'before':bombs,'after':game.u32(0x4b0ca0)}
        game.pulse(0x100,.25)
        paused=game.state();after=game.run(0,0,.35)
        assert after['frame']==paused['frame'],(paused,after)
        report['checks']['pause_stops_both']=after
        game.pulse(0x100,.3)
        after=game.run(0,0,.2)
        assert after['frame']>paused['frame'],'Resume did not restart simulation'
        report['checks']['resume']=after
        if death:
            states=set();start_lives=game.u32(0x4b0c98)
            for _ in range(900):
                st=game.run(0,0,.05);states.add((st['p1state'],st['p2state']))
                if game.u32(0x4b0c98)<start_lives and st['p1state']==1 and st['p2state']==1:break
            report['checks']['natural_death_observation']={'states':list(states),'lives_before':start_lives,'lives_after':game.u32(0x4b0c98)}
            assert game.u32(0x4b0c98)<start_lives,'No natural collision observed in45seconds'
        # Pause -> return to title -> confirm. Original menu is always exercised.
        game.pulse(0x100,.3)
        game.pulse(0x20,.25);game.pulse(1,.5)
        game.pulse(0x10,.25) # confirmation defaults to No
        game.pulse(1,1)
        for _ in range(60):
            st=game.run(0,0,.05)
            if st['status']==1 and game.u32(0x4b4514)==0:break
        else:raise AssertionError(f'Return to title did not release players: {st}')
        report['checks']['teardown']=st
        report['passed']=True
    except Exception as e:
        report['passed']=False;report['error']=str(e)
        raise
    finally:
        if game:game.close()
        (out/f'shot-{shot}.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf8')
        print(json.dumps(report,ensure_ascii=False),flush=True)

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--shot',type=int,choices=range(6));ap.add_argument('--death',action='store_true');ap.add_argument('--game',type=Path,default=ROOT/'game-smoke');ap.add_argument('--out',type=Path,default=ROOT/'artifacts/offline-smoke');args=ap.parse_args()
    out=args.out;out.mkdir(parents=True,exist_ok=True)
    for shot in ([args.shot] if args.shot is not None else range(6)):check(shot,args.game,out,args.death)
if __name__=='__main__':main()
