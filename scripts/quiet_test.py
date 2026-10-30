"""Hidden windows and crash-safe process ownership for real-game tests.

No desktop switch, keyboard/mouse synthesis, clipboard access, or endpoint
volume changes. The injector's separate quiet-test DLL guards every audio
buffer before Play; the game keeps its native audio playback clocks.
"""
import ctypes as C
from ctypes import wintypes as W
from pathlib import Path
import struct

from probe import Mapping

_kernel = C.WinDLL('kernel32', use_last_error=True)
_user = C.WinDLL('user32', use_last_error=True)
_user.GetThreadDesktop.argtypes = [W.DWORD]
_user.GetThreadDesktop.restype = W.HANDLE
_user.GetUserObjectInformationW.argtypes = [W.HANDLE,C.c_int,C.c_void_p,W.DWORD,C.POINTER(W.DWORD)]
_user.GetUserObjectInformationW.restype = W.BOOL
_window_callback = C.WINFUNCTYPE(W.BOOL,W.HWND,W.LPARAM)
_user.EnumWindows.argtypes = [_window_callback,W.LPARAM]
_user.EnumWindows.restype = W.BOOL
_user.GetWindowThreadProcessId.argtypes = [W.HWND,C.POINTER(W.DWORD)]
_user.GetWindowThreadProcessId.restype = W.DWORD
_user.GetForegroundWindow.restype = W.HWND
_user.IsWindowVisible.argtypes = [W.HWND]
_user.IsWindowVisible.restype = W.BOOL
_kernel.GetCurrentThreadId.restype = W.DWORD
_kernel.SetErrorMode.argtypes = [W.UINT]
_kernel.SetErrorMode.restype = W.UINT
_kernel.CreateJobObjectW.argtypes = [C.c_void_p, W.LPCWSTR]
_kernel.CreateJobObjectW.restype = W.HANDLE
_kernel.SetInformationJobObject.argtypes = [W.HANDLE, C.c_int, C.c_void_p, W.DWORD]
_kernel.SetInformationJobObject.restype = W.BOOL
_kernel.AssignProcessToJobObject.argtypes = [W.HANDLE, W.HANDLE]
_kernel.AssignProcessToJobObject.restype = W.BOOL
_kernel.CloseHandle.argtypes = [W.HANDLE]
_kernel.CloseHandle.restype = W.BOOL


class BasicLimits(C.Structure):
    _fields_ = [('process_time', C.c_int64), ('job_time', C.c_int64), ('flags', W.DWORD),
                ('minimum_working_set', C.c_size_t), ('maximum_working_set', C.c_size_t),
                ('active_process_limit', W.DWORD), ('affinity', C.c_size_t),
                ('priority_class', W.DWORD), ('scheduling_class', W.DWORD)]


class ExtendedLimits(C.Structure):
    _fields_ = [('basic', BasicLimits), ('io_counters', C.c_uint64 * 6),
                ('process_memory', C.c_size_t), ('job_memory', C.c_size_t),
                ('peak_process_memory', C.c_size_t), ('peak_job_memory', C.c_size_t)]


class QuietEnvironment:
    def __init__(self, root):
        # Child loader failures must not show a dialog before guard DLL entry.
        # This policy belongs to this test-runner process and its children only.
        _kernel.SetErrorMode(0x8003)
        self.job = None
        if not (Path(root) / 'dist/th12coop-quiet-test.dll').is_file():
            raise FileNotFoundError('Build the quiet-test guard using build.ps1 -SmokeTest before starting any game')
        try:
            # Hardware D3D9 can block on inactive desktops. The guard enforces
            # hidden/noactivate windows and D3DCREATE_NOWINDOWCHANGES instead.
            name = C.create_unicode_buffer(256)
            needed = W.DWORD()
            if not _user.GetUserObjectInformationW(_user.GetThreadDesktop(_kernel.GetCurrentThreadId()),2,name,C.sizeof(name),C.byref(needed)):
                raise C.WinError(C.get_last_error())
            self.name = name.value
            self.job = _kernel.CreateJobObjectW(None, None)
            if not self.job:
                raise C.WinError(C.get_last_error())
            limits = ExtendedLimits()
            # Explicit game ownership, same policy as the production launcher.
            # Existing Steam services must not become test-owned descendants.
            limits.basic.flags = 0x2000 | 0x1000
            if not _kernel.SetInformationJobObject(self.job, 9, C.byref(limits), C.sizeof(limits)):
                raise C.WinError(C.get_last_error())
        except Exception:
            self.close()
            raise

    def assign(self, process):
        if not _kernel.AssignProcessToJobObject(self.job, process):
            raise C.WinError(C.get_last_error())

    def close(self):
        errors = []
        if self.job:
            if _kernel.CloseHandle(self.job):
                self.job = None
            else:
                errors.append('Cannot close owned game job')
        if errors:
            raise OSError('; '.join(errors))


def quiet_configuration(source):
    data = bytearray(source)
    if len(data) != 60 or data[:4] != b'\x01\x00\x12\x00':
        raise ValueError('Valid TH12 test configuration required')
    # Window mode remains necessary for Direct3D on a non-input desktop. Audio
    # safety is provided by the pre-entry guard, independent of config parsing.
    if data[0x1d] not in (1, 2, 3):
        data[0x1d] = 1
    flags = struct.unpack_from('<I', data, 0x38)[0] & ~0x100
    struct.pack_into('<I', data, 0x38, flags)
    return data


def quiet_evidence(pid):
    mapping = Mapping(f'Local\\TH12CoopQuiet_{pid}')
    try:
        magic, version, devices, buffers, plays, failures, windows, dialogs, graphics = struct.unpack('<9I', mapping.read(0, 36))
        if magic != 0x51323154 or version != 1:
            raise AssertionError('Invalid quiet-test guard evidence')
        visible = []
        @_window_callback
        def inspect(window, _):
            owner = W.DWORD()
            _user.GetWindowThreadProcessId(window,C.byref(owner))
            if owner.value == pid and _user.IsWindowVisible(window):
                visible.append(window)
            return True
        if not _user.EnumWindows(inspect,0):
            raise C.WinError(C.get_last_error())
        foreground = W.DWORD()
        _user.GetWindowThreadProcessId(_user.GetForegroundWindow(),C.byref(foreground))
        return {'devices': devices, 'buffers': buffers, 'plays': plays, 'failures': failures,
                'privateDesktop': False, 'windows': 'hidden-noactivate', 'mute': 'per-buffer DSBVOLUME_MIN before Play',
                'nativePlaybackClocks': True, 'createdWindows': windows, 'blockedDialogs': dialogs,
                'graphicsDevices': graphics, 'visibleOwnedWindows': len(visible), 'ownsForeground': foreground.value == pid}
    finally:
        mapping.close()
