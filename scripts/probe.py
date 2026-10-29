"""Read game telemetry and exercise the MOD input API."""
import argparse
import ctypes as C
from ctypes import wintypes as W
import json
import struct
import time
from pathlib import Path

k = C.WinDLL('kernel32', use_last_error=True)
k.OpenProcess.argtypes = [W.DWORD, W.BOOL, W.DWORD]
k.OpenProcess.restype = W.HANDLE
k.ReadProcessMemory.argtypes = [W.HANDLE, C.c_void_p, C.c_void_p, C.c_size_t, C.POINTER(C.c_size_t)]
k.ReadProcessMemory.restype = W.BOOL
k.OpenFileMappingW.argtypes = [W.DWORD, W.BOOL, W.LPCWSTR]
k.OpenFileMappingW.restype = W.HANDLE
k.MapViewOfFile.argtypes = [W.HANDLE, W.DWORD, W.DWORD, W.DWORD, C.c_size_t]
k.MapViewOfFile.restype = C.c_void_p
k.UnmapViewOfFile.argtypes = [C.c_void_p]
k.CloseHandle.argtypes = [W.HANDLE]
k.GetTickCount.restype = W.DWORD

def read_process(pid, address, count):
    process = k.OpenProcess(0x10, False, pid)
    if not process: raise C.WinError(C.get_last_error())
    try:
        result = C.create_string_buffer(count)
        done = C.c_size_t()
        if not k.ReadProcessMemory(process, address, result, count, C.byref(done)) or done.value != count:
            raise C.WinError(C.get_last_error())
        return result.raw
    finally: k.CloseHandle(process)

class Mapping:
    def __init__(self, name, write=False):
        access = 6 if write else 4
        self.handle = k.OpenFileMappingW(access, False, name)
        if not self.handle: raise C.WinError(C.get_last_error())
        self.address = k.MapViewOfFile(self.handle, access, 0, 0, 0)
        if not self.address:
            k.CloseHandle(self.handle)
            raise C.WinError(C.get_last_error())
    def read(self, offset, length): return C.string_at(self.address + offset, length)
    def write32(self, offset, value): C.c_uint32.from_address(self.address + offset).value = value
    def close(self):
        k.UnmapViewOfFile(self.address); k.CloseHandle(self.handle)

def telemetry(mapping):
    words = struct.unpack('<7I4f5I', mapping.read(0,64))
    return dict(zip(['magic','version','p1input','p2input','flags','status','frame',
                     'p1x','p1y','p2x','p2y','heartbeat','p1state','p2state','p1bullets','p2bullets'],words))

def main():
    p=argparse.ArgumentParser()
    p.add_argument('pid',type=int)
    p.add_argument('--memory',type=lambda s:int(s,0))
    p.add_argument('--bytes',type=int,default=32)
    p.add_argument('--input',nargs=2,type=lambda s:int(s,0))
    p.add_argument('--seconds',type=float,default=1)
    p.add_argument('--smoke', action='store_true', help='Use menu-input hook in separately built smoke DLL')
    args=p.parse_args()
    if args.memory:
        print(read_process(args.pid,args.memory,args.bytes).hex(' '));return
    m=Mapping(f'Local\\TH12Coop_{args.pid}', bool(args.input))
    try:
        before=telemetry(m)
        print('before '+json.dumps(before))
        if args.input:
            end=time.monotonic()+args.seconds
            try:
                while time.monotonic()<end:
                    m.write32(44,k.GetTickCount())
                    m.write32(8,args.input[0] if args.input else 0)
                    m.write32(12,args.input[1] if args.input else 0)
                    m.write32(16,1|(2 if args.smoke else 0))
                    time.sleep(.008)
                print('after '+json.dumps(telemetry(m)))
            finally:
                m.write32(8,0);m.write32(12,0);m.write32(16,0)
    finally:m.close()

if __name__=='__main__':main()
