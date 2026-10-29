"""Read-only diagnostics of threads belonging to a test-owned process."""
import ctypes as C
import struct
from ctypes import wintypes as W
from probe import k, read_process

class ThreadEntry(C.Structure):
    _fields_=[('size',W.DWORD),('usage',W.DWORD),('tid',W.DWORD),('pid',W.DWORD),('priority',W.LONG),('base',W.LONG),('flags',W.DWORD)]

k.CreateToolhelp32Snapshot.argtypes=[W.DWORD,W.DWORD];k.CreateToolhelp32Snapshot.restype=W.HANDLE
k.Thread32First.argtypes=[W.HANDLE,C.POINTER(ThreadEntry)];k.Thread32Next.argtypes=[W.HANDLE,C.POINTER(ThreadEntry)]
k.OpenThread.argtypes=[W.DWORD,W.BOOL,W.DWORD];k.OpenThread.restype=W.HANDLE
k.SuspendThread.argtypes=[W.HANDLE];k.SuspendThread.restype=W.DWORD
k.Wow64GetThreadContext.argtypes=[W.HANDLE,C.c_void_p]

def snapshot(pid):
    result=[];handle=k.CreateToolhelp32Snapshot(4,0);entry=ThreadEntry();entry.size=C.sizeof(entry)
    try:
        more=k.Thread32First(handle,C.byref(entry))
        while more:
            if entry.pid==pid:
                thread=k.OpenThread(0x4a,False,entry.tid)
                if thread:
                    suspended=k.SuspendThread(thread)!=0xffffffff
                    try:
                        context=(C.c_uint32*179)();context[0]=0x10007
                        if suspended and k.Wow64GetThreadContext(thread,context):
                            row={'tid':entry.tid,'eip':hex(context[46]),'esp':hex(context[49]),'eax':hex(context[44]),'ecx':hex(context[43]),'edi':hex(context[39]),'esi':hex(context[40])}
                            try:
                                raw=read_process(pid,context[49],min(1024,0x1000-(context[49]&0xfff)))
                                row['stack']=raw.hex()
                                row['game_addresses']=[hex(v) for v in C.cast(C.create_string_buffer(raw),C.POINTER(C.c_uint32*(len(raw)//4))).contents if 0x400000<=v<0x500000]
                            except OSError:pass
                            result.append(row)
                    finally:
                        if suspended:k.ResumeThread(thread)
                        k.CloseHandle(thread)
            more=k.Thread32Next(handle,C.byref(entry))
    finally:k.CloseHandle(handle)
    return result

def enemies(pid):
    def word(address):return struct.unpack('<I',read_process(pid,address,4))[0]
    manager=word(0x4b43dc);result=[]
    node=word(manager+0x68) if manager else 0
    while node and len(result)<32:
        enemy=word(node);context=enemy+8;data=enemy+0x1040
        result.append({'enemy':hex(enemy),'context':[word(context+x) for x in (0,0x1008,0x100c,0x1010,0x1018,0x101c,0x1020)],'position':read_process(pid,data+0x34,12).hex(),'life':read_process(pid,data+0x1608,24).hex(),'vars':read_process(pid,data+0x23c,48).hex()})
        node=word(node+4)
    return result
