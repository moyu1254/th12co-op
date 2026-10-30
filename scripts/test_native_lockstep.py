"""Run two isolated, real TH12 processes against identical committed input.
No game memory is written: launch, injection, and the documented IPC are used.
"""
import argparse, ctypes as C, hashlib, json, struct, subprocess, time
from ctypes import wintypes as W
from pathlib import Path
from probe import Mapping, k, read_process
from thread_snapshot import snapshot
from quiet_test import QuietEnvironment, quiet_configuration, quiet_evidence
ROOT=Path(__file__).resolve().parent.parent
class Startup(C.Structure):
    _fields_=[('cb',W.DWORD),('reserved',W.LPWSTR),('desktop',W.LPWSTR),('title',W.LPWSTR),('x',W.DWORD),('y',W.DWORD),('xs',W.DWORD),('ys',W.DWORD),('xc',W.DWORD),('yc',W.DWORD),('fill',W.DWORD),('flags',W.DWORD),('show',W.WORD),('reserved2bytes',W.WORD),('reserved2',C.c_void_p),('stdin',W.HANDLE),('stdout',W.HANDLE),('stderr',W.HANDLE)]
class ProcessInfo(C.Structure):
    _fields_=[('process',W.HANDLE),('thread',W.HANDLE),('pid',W.DWORD),('tid',W.DWORD)]
k.CreateProcessW.argtypes=[W.LPCWSTR,W.LPWSTR,C.c_void_p,C.c_void_p,W.BOOL,W.DWORD,C.c_void_p,W.LPCWSTR,C.POINTER(Startup),C.POINTER(ProcessInfo)]
k.CreateProcessW.restype=W.BOOL
k.CreateFileMappingW.argtypes=[W.HANDLE,C.c_void_p,W.DWORD,W.DWORD,W.DWORD,W.LPCWSTR];k.CreateFileMappingW.restype=W.HANDLE
k.ResumeThread.argtypes=[W.HANDLE];k.ResumeThread.restype=W.DWORD
k.TerminateProcess.argtypes=[W.HANDLE,W.UINT];k.TerminateProcess.restype=W.BOOL
k.WaitForSingleObject.argtypes=[W.HANDLE,W.DWORD];k.WaitForSingleObject.restype=W.DWORD

class NetGame:
    def __init__(self,folder,seed,configuration=None,p1=255,p2=255):
        self.map=None;self.players=None;self.quiet=None;self.info=ProcessInfo();self.folder=folder
        try:self._start(folder,seed,configuration,p1,p2)
        except Exception:
            self.close();raise
    def _start(self,folder,seed,configuration,p1,p2):
        folder=Path(folder).resolve()
        if folder.parent!=ROOT or not folder.name.startswith(('game-test-','game-net-')):
            raise ValueError('Native fixtures require an isolated game-test-* or game-net-* folder in the workspace')
        (folder/'th12.cfg').write_bytes(quiet_configuration(configuration if configuration is not None else (ROOT/'profiles/th12-online.cfg').read_bytes()))
        for name in ('scoreth12.dat','score.dat'):
            p=folder/name
            if p.exists():p.unlink()
        startup=Startup();startup.cb=C.sizeof(startup)
        self.quiet=QuietEnvironment(ROOT);startup.desktop=self.quiet.name
        command=C.create_unicode_buffer('"'+str(folder/'th12.exe')+'"')
        if not k.CreateProcessW(None,command,None,None,False,4,None,str(folder),C.byref(startup),C.byref(self.info)):raise C.WinError(C.get_last_error())
        self.quiet.assign(self.info.process)
        handle=k.CreateFileMappingW(W.HANDLE(-1),None,4,0,12544,f'Local\\TH12CoopNet_{self.info.pid}')
        if not handle:raise C.WinError(C.get_last_error())
        self.map=Mapping(f'Local\\TH12CoopNet_{self.info.pid}',True);k.CloseHandle(handle)
        for offset,value in [(0,0x4e323154),(4,1),(8,12544),(12,256),(16,3),(20,seed),(24,k.GetTickCount()),(76,4),(92,self.info.tid)]:self.map.write32(offset,value)
        if p1 != 255 or p2 != 255:
            if p1 not in range(6) or p2 not in range(6):raise ValueError('Both player selections must be 0..5')
            name=f'Local\\TH12CoopPlayers_{self.info.pid}'
            handle=k.CreateFileMappingW(W.HANDLE(-1),None,4,0,16,name)
            if not handle:raise C.WinError(C.get_last_error())
            try:self.players=Mapping(name,True)
            finally:k.CloseHandle(handle)
            for offset,value in [(0,0x43323154),(4,1),(8,p1),(12,p2)]:self.players.write32(offset,value)
        result=subprocess.run([str(ROOT/'dist/injector.exe'),'--early','--quiet-test',str(self.info.pid),str(self.info.tid),str(ROOT/'dist/th12coop-native.dll')],capture_output=True,text=True,timeout=40,creationflags=subprocess.CREATE_NO_WINDOW)
        if result.returncode:
            raise RuntimeError(f'Injection failed: {result.stdout} {result.stderr}')
        self.quiet_evidence=quiet_evidence(self.info.pid)
        for _ in range(300):
            self.heartbeat()
            if self.header()[8] in (1,5):break
            time.sleep(.01)
        else:
            header=self.header();raise RuntimeError(f'No native ready: {header}')
    def heartbeat(self):self.map.write32(24,k.GetTickCount())
    def header(self):return struct.unpack('<30I',self.map.read(0,120))
    def commit(self,frame,p1,p2):
        offset=256+(frame%256)*16
        self.map.write32(offset+4,p1);self.map.write32(offset+8,p2);self.map.write32(offset+12,0);self.map.write32(offset,frame)
    def close(self):
        errors=[]
        try:
            if self.map:
                try:
                    self.map.write32(28,99)
                except Exception as error:
                    errors.append(f'mapping write: {error}')
                finally:
                    try:
                        self.map.close()
                        self.map=None
                    except Exception as error:
                        errors.append(f'mapping close: {error}')
        finally:
            try:
                if self.info.process:
                    try:
                        observed=k.WaitForSingleObject(self.info.process,0)
                        if observed==258:
                            if not k.TerminateProcess(self.info.process,0):
                                error=C.get_last_error()
                                # The process may have exited between the poll
                                # and TerminateProcess. Confirm that race rather
                                # than treating an arbitrary API failure as exit.
                                observed=k.WaitForSingleObject(self.info.process,0)
                                if observed!=0:
                                    raise RuntimeError(f'TerminateProcess failed (Win32 {error}); owned process exit not confirmed')
                            else:
                                observed=k.WaitForSingleObject(self.info.process,5000)
                        if observed==258:
                            raise RuntimeError('Owned process exit timed out after termination')
                        if observed!=0:
                            raise RuntimeError(f'WaitForSingleObject failed ({observed:#x}, Win32 {C.get_last_error()})')
                        if not k.CloseHandle(self.info.process):
                            raise RuntimeError(f'CloseHandle(process) failed (Win32 {C.get_last_error()})')
                        # Retain the handle on failure so callers cannot report
                        # success from nulled fields and can retry cleanup.
                        self.info.process=None
                    except Exception as error:
                        errors.append(str(error))
            finally:
                if self.info.thread:
                    try:
                        if not k.CloseHandle(self.info.thread):
                            raise RuntimeError(f'CloseHandle(thread) failed (Win32 {C.get_last_error()})')
                        self.info.thread=None
                    except Exception as error:
                        errors.append(str(error))
        for name in ('players','quiet'):
            resource=getattr(self,name,None)
            if resource:
                try:resource.close();setattr(self,name,None)
                except Exception as error:errors.append(f'{name} close: {error}')
        if errors:
            raise RuntimeError('Owned process cleanup failed: '+'; '.join(errors))

def main():
    ap=argparse.ArgumentParser();ap.add_argument('--frames',type=int,default=1800);ap.add_argument('--seed',type=int,default=4660)
    ap.add_argument('--host-game',type=Path,default=ROOT/'game-net-host');ap.add_argument('--guest-game',type=Path,default=ROOT/'game-net-guest');ap.add_argument('--out',type=Path,default=ROOT/'artifacts/lockstep');args=ap.parse_args()
    games=[];report={'passed':False,'frames':0,'pids':[],'stage_frames':0,'game_folders':[str(args.host_game),str(args.guest_game)],'exe_sha256':hashlib.sha256((args.host_game/'th12.exe').read_bytes()).hexdigest()}
    out=args.out;out.mkdir(parents=True,exist_ok=True)
    try:
        for folder in (args.host_game,args.guest_game):
            game=NetGame(folder,args.seed);games.append(game);report['pids'].append(game.info.pid)
        for game in games:k.ResumeThread(game.info.thread)
        stage_begin=None;proof=[]
        for frame in range(1,args.frames+2):
            deadline=time.monotonic()+20
            while True:
                for g in games:g.heartbeat()
                heads=[g.header() for g in games]
                if any(h[8]==4 for h in heads):raise AssertionError(f'Native failure at {frame}: {heads}')
                if all(h[9]==frame for h in heads):break
                if time.monotonic()>deadline:raise AssertionError(f'Frame wait timeout {frame}: {heads}')
                time.sleep(.001)
            if heads[0][12:14]!=heads[1][12:14]:
                report['divergence']={'requested':frame,'headers':heads}
                report['divergence']['raw']=[]
                for g in games:
                    menu=struct.unpack('<I',read_process(g.info.pid,0x4b4530,4))[0]
                    report['divergence']['raw'].append({'menu':read_process(g.info.pid,menu,0x90).hex() if menu else None,'rng':read_process(g.info.pid,0x4ce560,16).hex(),'supervisor':read_process(g.info.pid,0x4cee3c,0x44).hex(),'drawReady':read_process(g.info.pid,0x4cf468,4).hex()})
                raise AssertionError(f'Digest mismatch before frame {frame}: {heads[0][24:30]} != {heads[1][24:30]}')
            digest=struct.unpack('<8I',games[0].map.read(4352+((frame-1)%256)*32,32))
            report['completed_frames']=frame-1
            if frame==args.frames+1:break
            if digest[3]==7 and digest[5]!=0xffff:
                if stage_begin is None:stage_begin=frame
                report['stage_frames']+=1
            if stage_begin is None:p1=1 if frame%45<8 else 0;p2=0
            else:
                t=frame-stage_begin
                p1=1;p2=1|(0x80 if (t//60)%2==0 else 0x40)
                if t==180 or t==240:p1=0x100
                if 181<=t<240:p1=p2=0
                if t==400:p2=2
            for game in games:game.commit(frame,p1,p2)
            report['frames']=frame
            if frame%120==0:
                proof.append({'frame':frame,'digest':digest,'components':list(heads[0][24:30])});print(json.dumps(proof[-1]),flush=True)
        report['samples']=proof
        assert report['stage_frames']>300,report
        report['passed']=True
    except Exception as error:
        report['error']=str(error)
        report['threads']=[]
        for g in games:
            try:report['threads'].append({'pid':g.info.pid,'threads':snapshot(g.info.pid),'stage_thread':read_process(g.info.pid,0x4cf0d8,32).hex(),'audio_pending':read_process(g.info.pid,0x4d14d4,4).hex()})
            except Exception as detail:report['threads'].append({'pid':g.info.pid,'diagnosticError':str(detail)})
        raise
    finally:
        report['quiet']=[]
        for game in games:
            try:report['quiet'].append(quiet_evidence(game.info.pid))
            except OSError:pass
        for game in games:game.close()
        report['ownedHandlesClosed']=all(not game.info.process and not game.info.thread for game in games)
        (out/'native-pair.json').write_text(json.dumps(report,indent=2),encoding='utf8')
        print(json.dumps({k:v for k,v in report.items() if k not in ('threads','samples')}),flush=True)
if __name__=='__main__':main()
