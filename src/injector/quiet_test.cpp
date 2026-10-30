#define WIN32_LEAN_AND_MEAN
#define INITGUID
#include <windows.h>
#include <mmsystem.h>
#include <dsound.h>
#include <d3d9.h>
#include <werapi.h>
#include <new>

// Test-only output guard. Loaded at the verified executable entry fence, before
// the game can create an audio buffer. Never linked into the production MOD.
// All DirectSound buffers keep their native playback clocks; only their volume
// is pinned to silence. A failed mute prevents Play instead of leaking sound.
namespace {
struct Evidence { DWORD magic, version; volatile LONG devices, buffers, plays, failures, windows, dialogs, graphics; };
static Evidence* evidence;
static HANDLE evidenceMapping;
using CreateSound = HRESULT (WINAPI*)(LPCGUID, LPDIRECTSOUND8*, LPUNKNOWN);
static CreateSound originalCreate;
class SilentBuffer;
static HRESULT WrapNotification(IDirectSoundBuffer* inner,SilentBuffer* owner,void** result);

class SilentBuffer final : public IDirectSoundBuffer8 {
    IDirectSoundBuffer* inner;
    bool primary;
    LONG references = 1;
public:
    SilentBuffer(IDirectSoundBuffer* value, bool isPrimary) : inner(value), primary(isPrimary) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id, void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if(id==IID_IDirectSoundNotify)return WrapNotification(inner,this,result);
        if (id != IID_IUnknown && id != IID_IDirectSoundBuffer && id != IID_IDirectSoundBuffer8) return E_NOINTERFACE;
        *result = static_cast<IDirectSoundBuffer8*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override {
        auto value = InterlockedDecrement(&references);
        if (!value) { inner->Release(); delete this; }
        return value;
    }
    IDirectSoundBuffer* Native() const { return inner; }
    HRESULT STDMETHODCALLTYPE GetCaps(LPDSBCAPS a) override { return inner->GetCaps(a); }
    HRESULT STDMETHODCALLTYPE GetCurrentPosition(LPDWORD a, LPDWORD b) override { return inner->GetCurrentPosition(a,b); }
    HRESULT STDMETHODCALLTYPE GetFormat(LPWAVEFORMATEX a,DWORD b,LPDWORD c) override { return inner->GetFormat(a,b,c); }
    HRESULT STDMETHODCALLTYPE GetVolume(LPLONG a) override { return inner->GetVolume(a); }
    HRESULT STDMETHODCALLTYPE GetPan(LPLONG a) override { return inner->GetPan(a); }
    HRESULT STDMETHODCALLTYPE GetFrequency(LPDWORD a) override { return inner->GetFrequency(a); }
    HRESULT STDMETHODCALLTYPE GetStatus(LPDWORD a) override { return inner->GetStatus(a); }
    HRESULT STDMETHODCALLTYPE Initialize(LPDIRECTSOUND, LPCDSBUFFERDESC) override { return DSERR_ALREADYINITIALIZED; }
    HRESULT STDMETHODCALLTYPE Lock(DWORD a,DWORD b,LPVOID* c,LPDWORD d,LPVOID* e,LPDWORD f,DWORD g) override {
        return primary ? DSERR_INVALIDCALL : inner->Lock(a,b,c,d,e,f,g);
    }
    HRESULT STDMETHODCALLTYPE Play(DWORD a,DWORD b,DWORD c) override {
        if (primary) return DS_OK; // Never operate on the device-wide primary mix.
        const auto muted = inner->SetVolume(DSBVOLUME_MIN);
        if (FAILED(muted)) { InterlockedIncrement(&evidence->failures); return muted; }
        InterlockedIncrement(&evidence->plays);
        return inner->Play(a,b,c);
    }
    HRESULT STDMETHODCALLTYPE SetCurrentPosition(DWORD a) override { return primary ? DS_OK : inner->SetCurrentPosition(a); }
    HRESULT STDMETHODCALLTYPE SetFormat(LPCWAVEFORMATEX a) override { return primary ? DS_OK : inner->SetFormat(a); }
    HRESULT STDMETHODCALLTYPE SetVolume(LONG) override {
        if (primary) return DS_OK; // Primary volume can affect other applications.
        const auto result = inner->SetVolume(DSBVOLUME_MIN);
        if (FAILED(result)) InterlockedIncrement(&evidence->failures);
        return result;
    }
    HRESULT STDMETHODCALLTYPE SetPan(LONG a) override { return primary ? DS_OK : inner->SetPan(a); }
    HRESULT STDMETHODCALLTYPE SetFrequency(DWORD a) override { return primary ? DS_OK : inner->SetFrequency(a); }
    HRESULT STDMETHODCALLTYPE Stop() override { return primary ? DS_OK : inner->Stop(); }
    HRESULT STDMETHODCALLTYPE Unlock(LPVOID a,DWORD b,LPVOID c,DWORD d) override { return primary ? DSERR_INVALIDCALL : inner->Unlock(a,b,c,d); }
    HRESULT STDMETHODCALLTYPE Restore() override { return primary ? DS_OK : inner->Restore(); }
    HRESULT STDMETHODCALLTYPE SetFX(DWORD a,LPDSEFFECTDESC b,LPDWORD c) override {
        if (primary) return DSERR_INVALIDCALL;
        IDirectSoundBuffer8* extended = nullptr;
        auto result = inner->QueryInterface(IID_IDirectSoundBuffer8,reinterpret_cast<void**>(&extended));
        if (SUCCEEDED(result)) { result = extended->SetFX(a,b,c); extended->Release(); }
        return result;
    }
    HRESULT STDMETHODCALLTYPE AcquireResources(DWORD a,DWORD b,LPDWORD c) override {
        if (primary) return DSERR_INVALIDCALL;
        IDirectSoundBuffer8* extended = nullptr;
        auto result = inner->QueryInterface(IID_IDirectSoundBuffer8,reinterpret_cast<void**>(&extended));
        if (SUCCEEDED(result)) { result = extended->AcquireResources(a,b,c); extended->Release(); }
        return result;
    }
    HRESULT STDMETHODCALLTYPE GetObjectInPath(REFGUID,DWORD,REFGUID,LPVOID* value) override {
        if (value) *value = nullptr;
        return E_NOINTERFACE; // No unguarded audio interface can escape.
    }
};

class SilentNotification final : public IDirectSoundNotify {
    IDirectSoundNotify* inner;
    SilentBuffer* owner;
    LONG references=1;
public:
    SilentNotification(IDirectSoundNotify* original,SilentBuffer* buffer):inner(original),owner(buffer) {owner->AddRef();}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if(!result)return E_POINTER;*result=nullptr;
        if(id!=IID_IUnknown&&id!=IID_IDirectSoundNotify)return owner->QueryInterface(id,result);
        *result=this;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override {return InterlockedIncrement(&references);}
    ULONG STDMETHODCALLTYPE Release() override {
        auto n=InterlockedDecrement(&references);
        if(!n){inner->Release();owner->Release();delete this;}return n;
    }
    HRESULT STDMETHODCALLTYPE SetNotificationPositions(DWORD count,LPCDSBPOSITIONNOTIFY positions) override {return inner->SetNotificationPositions(count,positions);}
};
static HRESULT WrapNotification(IDirectSoundBuffer* inner,SilentBuffer* owner,void** result) {
    IDirectSoundNotify* notify=nullptr;
    auto status=inner->QueryInterface(IID_IDirectSoundNotify,reinterpret_cast<void**>(&notify));
    if(FAILED(status))return status;
    auto wrapped=new(std::nothrow) SilentNotification(notify,owner);
    if(!wrapped){notify->Release();return E_OUTOFMEMORY;}
    *result=wrapped;return S_OK;
}

static HRESULT WrapBuffer(IDirectSoundBuffer* original, IDirectSoundBuffer** result, bool primary = false) {
    if (!primary) {
        auto muted = original->SetVolume(DSBVOLUME_MIN);
        if (FAILED(muted)) { InterlockedIncrement(&evidence->failures); original->Release(); return muted; }
    }
    auto wrapped = new(std::nothrow) SilentBuffer(original,primary);
    if (!wrapped) { original->Release(); return E_OUTOFMEMORY; }
    *result = wrapped; InterlockedIncrement(&evidence->buffers); return DS_OK;
}

class SilentSound final : public IDirectSound8 {
    IDirectSound8* inner;
    LONG references = 1;
public:
    explicit SilentSound(IDirectSound8* value) : inner(value) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if (!result) return E_POINTER;
        *result = nullptr;
        if (id != IID_IUnknown && id != IID_IDirectSound && id != IID_IDirectSound8) return E_NOINTERFACE;
        *result = static_cast<IDirectSound8*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override {
        auto value = InterlockedDecrement(&references);
        if (!value) { inner->Release(); delete this; }
        return value;
    }
    HRESULT STDMETHODCALLTYPE CreateSoundBuffer(LPCDSBUFFERDESC desc,LPDIRECTSOUNDBUFFER* result,LPUNKNOWN outer) override {
        if (!desc || !result) return DSERR_INVALIDPARAM;
        *result = nullptr;
        auto silent = *desc;
        const bool primary = (silent.dwFlags & DSBCAPS_PRIMARYBUFFER) != 0;
        if (!primary) silent.dwFlags |= DSBCAPS_CTRLVOLUME | DSBCAPS_GLOBALFOCUS;
        IDirectSoundBuffer* buffer = nullptr;
        auto status = inner->CreateSoundBuffer(&silent,&buffer,outer);
        return SUCCEEDED(status) ? WrapBuffer(buffer,result,primary) : status;
    }
    HRESULT STDMETHODCALLTYPE GetCaps(LPDSCAPS a) override { return inner->GetCaps(a); }
    HRESULT STDMETHODCALLTYPE DuplicateSoundBuffer(LPDIRECTSOUNDBUFFER source,LPDIRECTSOUNDBUFFER* result) override {
        if (!source || !result) return DSERR_INVALIDPARAM;
        *result = nullptr; IDirectSoundBuffer* duplicate = nullptr;
        auto status = inner->DuplicateSoundBuffer(static_cast<SilentBuffer*>(source)->Native(),&duplicate);
        return SUCCEEDED(status) ? WrapBuffer(duplicate,result) : status;
    }
    HRESULT STDMETHODCALLTYPE SetCooperativeLevel(HWND a,DWORD) override { return inner->SetCooperativeLevel(a,DSSCL_NORMAL); }
    HRESULT STDMETHODCALLTYPE Compact() override { return inner->Compact(); }
    HRESULT STDMETHODCALLTYPE GetSpeakerConfig(LPDWORD a) override { return inner->GetSpeakerConfig(a); }
    HRESULT STDMETHODCALLTYPE SetSpeakerConfig(DWORD) override { return DS_OK; } // Never change the user's speaker configuration.
    HRESULT STDMETHODCALLTYPE Initialize(LPCGUID a) override { return inner->Initialize(a); }
    HRESULT STDMETHODCALLTYPE VerifyCertification(LPDWORD a) override { return inner->VerifyCertification(a); }
};

static HRESULT WINAPI CreateSilentSound(LPCGUID device,LPDIRECTSOUND8* result,LPUNKNOWN outer) {
    if (!result) return E_POINTER;
    *result = nullptr; IDirectSound8* inner = nullptr;
    auto status = originalCreate(device,&inner,outer);
    if (FAILED(status)) return status;
    auto wrapped = new(std::nothrow) SilentSound(inner);
    if (!wrapped) { inner->Release(); return E_OUTOFMEMORY; }
    *result = wrapped; InterlockedIncrement(&evidence->devices); return DS_OK;
}

static HWND WINAPI HiddenWindow(DWORD extended,LPCSTR cls,LPCSTR title,DWORD style,int x,int y,int w,int h,HWND parent,HMENU menu,HINSTANCE instance,LPVOID parameter) {
    InterlockedIncrement(&evidence->windows);
    extended = (extended | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_TOPMOST;
    return CreateWindowExA(extended,cls,title,style & ~WS_VISIBLE,x,y,w,h,parent,menu,instance,parameter);
}
static BOOL WINAPI HideWindow(HWND window,int) { return ShowWindow(window,SW_HIDE); }
static BOOL WINAPI NoForeground(HWND) { return FALSE; }
static BOOL WINAPI HiddenPosition(HWND window,HWND after,int x,int y,int w,int h,UINT flags) {
    return SetWindowPos(window,after,x,y,w,h,(flags & ~SWP_SHOWWINDOW) | SWP_NOACTIVATE | SWP_NOZORDER | SWP_HIDEWINDOW);
}
static LONG WINAPI HiddenLong(HWND window,int index,LONG value) {
    if (index==GWL_STYLE) value &= ~WS_VISIBLE;
    if (index==GWL_EXSTYLE) value=(value | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) & ~WS_EX_TOPMOST;
    return SetWindowLongA(window,index,value);
}
static INT_PTR WINAPI NoDialog(HINSTANCE,LPCSTR,HWND,DLGPROC,LPARAM) { InterlockedIncrement(&evidence->dialogs);SetLastError(ERROR_ACCESS_DENIED);return -1; }
static int WINAPI NoMessage(HWND,LPCSTR,LPCSTR,UINT) { InterlockedIncrement(&evidence->dialogs);return IDCANCEL; }
static HCURSOR WINAPI NoCursor(HCURSOR) { return nullptr; }
static int WINAPI NoCursorCount(BOOL show) { return show ? 0 : -1; }
static BOOL WINAPI NoKeyboard(LPBYTE) { return TRUE; }
static BOOL WINAPI NoIme(HWND,BOOL) { return TRUE; }
static BOOL WINAPI ReadOnlySystem(UINT action,UINT value,PVOID result,UINT flags) {
    if (action==SPI_GETWORKAREA || action==SPI_GETSCREENSAVEACTIVE || action==SPI_GETSCREENSAVETIMEOUT)
        return SystemParametersInfoA(action,value,result,flags & ~(SPIF_UPDATEINIFILE | SPIF_SENDCHANGE));
    SetLastError(ERROR_ACCESS_DENIED);return FALSE;
}
static BOOL WINAPI NoChild(LPCSTR,LPSTR,LPSECURITY_ATTRIBUTES,LPSECURITY_ATTRIBUTES,BOOL,DWORD,LPVOID,LPCSTR,LPSTARTUPINFOA,LPPROCESS_INFORMATION) {
    SetLastError(ERROR_ACCESS_DENIED);return FALSE;
}
static LRESULT WINAPI SafeMessage(HWND window,UINT message,WPARAM wparam,LPARAM lparam) {
    if(message==WM_SYSCOMMAND)return 0;
    DWORD owner=0;GetWindowThreadProcessId(window,&owner);
    return owner==GetCurrentProcessId()?SendMessageA(window,message,wparam,lparam):0;
}

// D3D9 can change focus itself, bypassing the game's user32 import table.
// NOWINDOWCHANGES disables that behavior. Only windowed devices are permitted.
class HiddenGraphics final : public IDirect3D9 {
    IDirect3D9* inner;
    LONG references=1;
public:
    explicit HiddenGraphics(IDirect3D9* value):inner(value) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID id,void** result) override {
        if (!result)return E_POINTER;*result=nullptr;
        if(id!=IID_IUnknown&&id!=IID_IDirect3D9)return E_NOINTERFACE;
        *result=this;AddRef();return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return InterlockedIncrement(&references); }
    ULONG STDMETHODCALLTYPE Release() override { auto n=InterlockedDecrement(&references);if(!n){inner->Release();delete this;}return n; }
    HRESULT STDMETHODCALLTYPE RegisterSoftwareDevice(void* a) override { return inner->RegisterSoftwareDevice(a); }
    UINT STDMETHODCALLTYPE GetAdapterCount() override { return inner->GetAdapterCount(); }
    HRESULT STDMETHODCALLTYPE GetAdapterIdentifier(UINT a,DWORD b,D3DADAPTER_IDENTIFIER9* c) override { return inner->GetAdapterIdentifier(a,b,c); }
    UINT STDMETHODCALLTYPE GetAdapterModeCount(UINT a,D3DFORMAT b) override { return inner->GetAdapterModeCount(a,b); }
    HRESULT STDMETHODCALLTYPE EnumAdapterModes(UINT a,D3DFORMAT b,UINT c,D3DDISPLAYMODE* d) override { return inner->EnumAdapterModes(a,b,c,d); }
    HRESULT STDMETHODCALLTYPE GetAdapterDisplayMode(UINT a,D3DDISPLAYMODE* b) override { return inner->GetAdapterDisplayMode(a,b); }
    HRESULT STDMETHODCALLTYPE CheckDeviceType(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d,BOOL e) override { return inner->CheckDeviceType(a,b,c,d,e); }
    HRESULT STDMETHODCALLTYPE CheckDeviceFormat(UINT a,D3DDEVTYPE b,D3DFORMAT c,DWORD d,D3DRESOURCETYPE e,D3DFORMAT f) override { return inner->CheckDeviceFormat(a,b,c,d,e,f); }
    HRESULT STDMETHODCALLTYPE CheckDeviceMultiSampleType(UINT a,D3DDEVTYPE b,D3DFORMAT c,BOOL d,D3DMULTISAMPLE_TYPE e,DWORD* f) override { return inner->CheckDeviceMultiSampleType(a,b,c,d,e,f); }
    HRESULT STDMETHODCALLTYPE CheckDepthStencilMatch(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d,D3DFORMAT e) override { return inner->CheckDepthStencilMatch(a,b,c,d,e); }
    HRESULT STDMETHODCALLTYPE CheckDeviceFormatConversion(UINT a,D3DDEVTYPE b,D3DFORMAT c,D3DFORMAT d) override { return inner->CheckDeviceFormatConversion(a,b,c,d); }
    HRESULT STDMETHODCALLTYPE GetDeviceCaps(UINT a,D3DDEVTYPE b,D3DCAPS9* c) override { return inner->GetDeviceCaps(a,b,c); }
    HMONITOR STDMETHODCALLTYPE GetAdapterMonitor(UINT a) override { return inner->GetAdapterMonitor(a); }
    HRESULT STDMETHODCALLTYPE CreateDevice(UINT a,D3DDEVTYPE b,HWND window,DWORD flags,D3DPRESENT_PARAMETERS* parameters,IDirect3DDevice9** result) override {
        if(!parameters||!parameters->Windowed)return D3DERR_INVALIDCALL;
        InterlockedIncrement(&evidence->graphics);
        return inner->CreateDevice(a,b,window,flags | D3DCREATE_NOWINDOWCHANGES,parameters,result);
    }
};
using CreateGraphics=IDirect3D9*(WINAPI*)(UINT);
static CreateGraphics originalGraphics;
static IDirect3D9* WINAPI CreateHiddenGraphics(UINT sdk) {
    auto inner=originalGraphics(sdk);if(!inner)return nullptr;
    auto wrapped=new(std::nothrow) HiddenGraphics(inner);
    if(!wrapped)inner->Release();return wrapped;
}

static void* GuardAddress(const char* name);
static FARPROC WINAPI GuardProc(HMODULE module,LPCSTR name) {
    if(reinterpret_cast<uintptr_t>(name)>65535) {
        if(module==GetModuleHandleW(L"dsound.dll")&&!strcmp(name,"DirectSoundCreate8"))return reinterpret_cast<FARPROC>(CreateSilentSound);
        if(module==GetModuleHandleW(L"user32.dll") || module==GetModuleHandleW(L"kernel32.dll") ||
           module==GetModuleHandleW(L"d3d9.dll")) {
            if(auto guarded=GuardAddress(name))return reinterpret_cast<FARPROC>(guarded);
        }
    } else if(module==GetModuleHandleW(L"dsound.dll") && reinterpret_cast<uintptr_t>(name)==11) {
        return reinterpret_cast<FARPROC>(CreateSilentSound);
    }
    return GetProcAddress(module,name);
}
struct GuardImport {const char* name;void* hook;bool found;};
static GuardImport guards[]={
    {"CreateWindowExA",reinterpret_cast<void*>(HiddenWindow),false},
    {"ShowWindow",reinterpret_cast<void*>(HideWindow),false},
    {"SetForegroundWindow",reinterpret_cast<void*>(NoForeground),false},
    {"SetWindowPos",reinterpret_cast<void*>(HiddenPosition),false},
    {"SetWindowLongA",reinterpret_cast<void*>(HiddenLong),false},
    {"DialogBoxParamA",reinterpret_cast<void*>(NoDialog),false},
    {"MessageBoxA",reinterpret_cast<void*>(NoMessage),false},
    {"SetCursor",reinterpret_cast<void*>(NoCursor),false},
    {"ShowCursor",reinterpret_cast<void*>(NoCursorCount),false},
    {"SetKeyboardState",reinterpret_cast<void*>(NoKeyboard),false},
    {"WINNLSEnableIME",reinterpret_cast<void*>(NoIme),false},
    {"SystemParametersInfoA",reinterpret_cast<void*>(ReadOnlySystem),false},
    {"CreateProcessA",reinterpret_cast<void*>(NoChild),false},
    {"SendMessageA",reinterpret_cast<void*>(SafeMessage),false},
    {"GetProcAddress",reinterpret_cast<void*>(GuardProc),false}
};
static void* GuardAddress(const char* name) {
    for(const auto& guard:guards)if(!strcmp(name,guard.name))return guard.hook;
    if(!strcmp(name,"Direct3DCreate9"))return reinterpret_cast<void*>(CreateHiddenGraphics);
    return nullptr;
}

static bool ReplaceImport(void** slot,void* replacement) {
    DWORD old=0;if(!slot||!*slot||!VirtualProtect(slot,sizeof(void*),PAGE_READWRITE,&old))return false;
    InterlockedExchangePointer(slot,replacement);
    DWORD ignored=0;return VirtualProtect(slot,sizeof(void*),old,&ignored)!=FALSE;
}

static bool Install() {
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if(FAILED(WerSetFlags(WER_FAULT_REPORTING_NO_UI)))return false;
    auto image = reinterpret_cast<BYTE*>(GetModuleHandleW(nullptr));
    auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    auto pe = reinterpret_cast<IMAGE_NT_HEADERS32*>(image + dos->e_lfanew);
    auto imports = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + pe->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress);
    void** target = nullptr;void** graphicsTarget=nullptr;
    for (;imports->Name;++imports) {
        const auto library=reinterpret_cast<char*>(image+imports->Name);
        auto names = reinterpret_cast<IMAGE_THUNK_DATA32*>(image+imports->OriginalFirstThunk);
        auto addresses = reinterpret_cast<IMAGE_THUNK_DATA32*>(image+imports->FirstThunk);
        for (;names->u1.AddressOfData;++names,++addresses) {
            auto slot=reinterpret_cast<void**>(&addresses->u1.Function);
            if (IMAGE_SNAP_BY_ORDINAL32(names->u1.Ordinal)) {
                if (!_stricmp(library,"DSOUND.dll") && IMAGE_ORDINAL32(names->u1.Ordinal)==11) target=slot;
                continue;
            }
            auto name=reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(image+names->u1.AddressOfData)->Name;
            if (!_stricmp(library,"d3d9.dll") && !strcmp(name,"Direct3DCreate9"))graphicsTarget=slot;
            for(auto& guard:guards)if(!strcmp(name,guard.name)){
                if(!ReplaceImport(slot,guard.hook))return false;guard.found=true;
            }
        }
    }
    if (!target || !*target || !graphicsTarget || !*graphicsTarget) return false;
    for(const auto& guard:guards)if(!guard.found)return false;
    wchar_t name[80]; wsprintfW(name,L"Local\\TH12CoopQuiet_%lu",GetCurrentProcessId());
    evidenceMapping = CreateFileMappingW(INVALID_HANDLE_VALUE,nullptr,PAGE_READWRITE,0,sizeof(Evidence),name);
    if (!evidenceMapping || GetLastError()==ERROR_ALREADY_EXISTS) { if (evidenceMapping) CloseHandle(evidenceMapping); return false; }
    evidence = static_cast<Evidence*>(MapViewOfFile(evidenceMapping,FILE_MAP_ALL_ACCESS,0,0,sizeof(Evidence)));
    if (!evidence) return false;
    evidence->magic=0x51323154; evidence->version=1;
    originalCreate = reinterpret_cast<CreateSound>(*target);
    originalGraphics=reinterpret_cast<CreateGraphics>(*graphicsTarget);
    return ReplaceImport(target,reinterpret_cast<void*>(CreateSilentSound)) && ReplaceImport(graphicsTarget,reinterpret_cast<void*>(CreateHiddenGraphics));
}
}

BOOL WINAPI DllMain(HINSTANCE module,DWORD reason,LPVOID) {
    if (reason!=DLL_PROCESS_ATTACH) return TRUE;
    DisableThreadLibraryCalls(module);
    return Install();
}

#ifdef QUIET_GUARD_SELF_TEST
#include <cstdio>
class FakeBuffer final : public IDirectSoundBuffer {
public:
    LONG volume = 0; int volumes = 0, plays = 0, formats = 0, locks = 0, stops = 0;
    bool failMute = false;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetCaps(LPDSBCAPS) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetCurrentPosition(LPDWORD,LPDWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetFormat(LPWAVEFORMATEX,DWORD,LPDWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetVolume(LPLONG a) override { *a=volume; return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetPan(LPLONG) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetFrequency(LPDWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE GetStatus(LPDWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE Initialize(LPDIRECTSOUND,LPCDSBUFFERDESC) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE Lock(DWORD,DWORD,LPVOID*,LPDWORD,LPVOID*,LPDWORD,DWORD) override { ++locks; return DS_OK; }
    HRESULT STDMETHODCALLTYPE Play(DWORD,DWORD,DWORD) override { ++plays; return DS_OK; }
    HRESULT STDMETHODCALLTYPE SetCurrentPosition(DWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE SetFormat(LPCWAVEFORMATEX) override { ++formats; return DS_OK; }
    HRESULT STDMETHODCALLTYPE SetVolume(LONG a) override { ++volumes;volume=a; return failMute ? DSERR_CONTROLUNAVAIL : DS_OK; }
    HRESULT STDMETHODCALLTYPE SetPan(LONG) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE SetFrequency(DWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE Stop() override { ++stops; return DS_OK; }
    HRESULT STDMETHODCALLTYPE Unlock(LPVOID,DWORD,LPVOID,DWORD) override { return DS_OK; }
    HRESULT STDMETHODCALLTYPE Restore() override { return DS_OK; }
};
int main() {
    Evidence state={}; evidence=&state;
    FakeBuffer primary, secondary;
    auto p=new SilentBuffer(&primary,true); auto s=new SilentBuffer(&secondary,false);
    p->SetVolume(0);p->Play(0,0,0);p->SetFormat(nullptr);p->Stop();
    const auto lock=p->Lock(0,0,nullptr,nullptr,nullptr,nullptr,0);
    bool ok=primary.volumes==0&&primary.plays==0&&primary.formats==0&&primary.stops==0&&primary.locks==0&&FAILED(lock);
    s->SetVolume(0); s->Play(0,0,0);
    ok=ok&&secondary.volume==DSBVOLUME_MIN&&secondary.volumes==2&&secondary.plays==1;
    secondary.failMute=true;
    ok=ok&&FAILED(s->Play(0,0,0))&&secondary.plays==1&&state.failures==1;
    p->Release();s->Release();
    auto window=HiddenWindow(WS_EX_TOPMOST,"STATIC","quiet guard self-test",WS_OVERLAPPEDWINDOW|WS_VISIBLE,0,0,16,16,nullptr,nullptr,GetModuleHandleW(nullptr),nullptr);
    if (!window)ok=false;
    else {
        ok=ok&&!IsWindowVisible(window)&&(GetWindowLongA(window,GWL_EXSTYLE)&WS_EX_NOACTIVATE)!=0;
        HideWindow(window,SW_SHOW);HiddenPosition(window,HWND_TOPMOST,0,0,16,16,SWP_SHOWWINDOW);
        HiddenLong(window,GWL_STYLE,GetWindowLongA(window,GWL_STYLE)|WS_VISIBLE);
        HiddenLong(window,GWL_EXSTYLE,WS_EX_TOPMOST);
        SafeMessage(window,WM_SYSCOMMAND,SC_RESTORE,0);
        ok=ok&&!IsWindowVisible(window)&&(GetWindowLongA(window,GWL_EXSTYLE)&WS_EX_NOACTIVATE)!=0;
        ok=ok&&GetForegroundWindow()!=window&&NoCursorCount(FALSE)<0&&NoCursorCount(TRUE)>=0;
        ok=ok&&GuardProc(GetModuleHandleW(L"user32.dll"),"MessageBoxA")==reinterpret_cast<FARPROC>(NoMessage);
        DestroyWindow(window);
    }
    std::printf("quiet guard: primary untouched, secondary mute/fail-closed, hidden/noactivate window, bounded cursor calls: %s\n",ok?"PASS":"FAIL");
    return ok?0:1;
}
#endif
