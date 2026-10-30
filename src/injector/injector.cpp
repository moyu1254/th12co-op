#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <tlhelp32.h>
#include <bcrypt.h>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>
#include <algorithm>

struct Handle {
    HANDLE value;
    explicit Handle(HANDLE h = nullptr): value(h) {}
    ~Handle() { if (value && value != INVALID_HANDLE_VALUE) CloseHandle(value); }
    Handle(const Handle&) = delete;
    Handle& operator=(const Handle&) = delete;
};

static std::wstring processPath(HANDLE process) {
    wchar_t path[32768]; DWORD size = _countof(path);
    if (!QueryFullProcessImageNameW(process, 0, path, &size)) return {};
    return {path, size};
}

static std::string sha256(const std::wstring& path) {
    Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr));
    if (file.value == INVALID_HANDLE_VALUE) return {};
    BCRYPT_ALG_HANDLE algorithm = nullptr;
    BCRYPT_HASH_HANDLE hash = nullptr;
    if (BCryptOpenAlgorithmProvider(&algorithm, BCRYPT_SHA256_ALGORITHM, nullptr, 0) < 0) return {};
    if (BCryptCreateHash(algorithm, &hash, nullptr, 0, nullptr, 0, 0) < 0) {
        BCryptCloseAlgorithmProvider(algorithm, 0); return {};
    }
    unsigned char buffer[65536], digest[32]; DWORD count = 0;
    bool ok = true;
    while (true) {
        if (!ReadFile(file.value, buffer, sizeof(buffer), &count, nullptr)) { ok = false; break; }
        if (!count) break;
        if (BCryptHashData(hash, buffer, count, 0) < 0) { ok = false; break; }
    }
    if (ok) ok = BCryptFinishHash(hash, digest, sizeof(digest), 0) >= 0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm, 0);
    if (!ok) return {};
    char output[65] = {};
    for (int i = 0; i < 32; ++i) sprintf_s(output + 2*i, 3, "%02x", digest[i]);
    return output;
}

struct GameProfile {
    const char* sha256;
    uintptr_t bootstrapEntry;
    unsigned char entryBytes[5];
};

static const GameProfile* knownGame(const std::wstring& path) {
    // Steam's executable enters its intact, plaintext wrapper first. Parking
    // there lets Windows finish import loading without executing the wrapper or
    // game. The native DLL then arms its post-bootstrap initialization boundary;
    // it never patches or replaces the encrypted game section on disk.
    static const GameProfile profiles[] = {
        {"e2939ba3f92e427216a619191a682a8197cc59a64eab99302a7b98811d6120a9", 0x46e7fd, {0xe8,0x47,0xcb,0x00,0x00}},
        {"d8d644d2e64957a3031b1a1399d0502e1ddaa5252d2c4e492770ad6717827628", 0x46e81d, {0xe8,0x47,0xcb,0x00,0x00}},
        {"eb275a8c285fd8450fa5ac79fa730a759aeca8b2904aeb16d30e65859372a459", 0x4d9310, {0xe8,0x00,0x00,0x00,0x00}},
    };
    const auto hash = sha256(path);
    for (const auto& profile : profiles) if (hash == profile.sha256) return &profile;
    return nullptr;
}

static uintptr_t remoteLoadLibrary(DWORD pid) {
    const auto load = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
    HMODULE localModule = nullptr;
    if (!load || !GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                                   reinterpret_cast<LPCWSTR>(load), &localModule)) return 0;
    wchar_t localPath[MAX_PATH];
    if (!GetModuleFileNameW(localModule, localPath, MAX_PATH)) return 0;
    auto localName = wcsrchr(localPath, L'\\');
    localName = localName ? localName + 1 : localPath;
    // Windows can report ERROR_BAD_LENGTH while loader notifications finish.
    // Each attempt takes a new snapshot; never infer the remote module address.
    for (unsigned attempt = 0; attempt < 100; ++attempt) {
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, pid));
        MODULEENTRY32W item = {}; item.dwSize = sizeof(item);
        if (snapshot.value != INVALID_HANDLE_VALUE && Module32FirstW(snapshot.value, &item)) {
            do {
                if (!_wcsicmp(item.szModule, localName)) {
                    return reinterpret_cast<uintptr_t>(item.modBaseAddr) +
                           reinterpret_cast<uintptr_t>(load) - reinterpret_cast<uintptr_t>(localModule);
                }
            } while (Module32NextW(snapshot.value, &item));
        }
        Sleep(10);
    }
    return 0;
}

// A newly suspended process has not finished loading its imports. Let the OS
// loader finish, parking the primary thread at the verified executable entry
// point before any game/CRT instruction runs. Return with that thread suspended.
static bool initializeLoader(HANDLE process, DWORD pid, DWORD threadId, const GameProfile& profile) {
    Handle thread(OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_SET_CONTEXT | THREAD_QUERY_INFORMATION,
                             FALSE, threadId));
    if (!thread.value || GetProcessIdOfThread(thread.value) != pid) {
        fwprintf(stderr,L"Entry fence: thread ownership/open failed (%lu).\n",GetLastError());return false;
    }
    const uintptr_t entry = profile.bootstrapEntry;
    unsigned char original[sizeof(profile.entryBytes)]; SIZE_T count = 0;
    if (!ReadProcessMemory(process, reinterpret_cast<void*>(entry), original, sizeof(original), &count) ||
        count != sizeof(original) || memcmp(original, profile.entryBytes, sizeof(profile.entryBytes))) {
        fwprintf(stderr,L"Entry fence: original signature read/validation failed (%lu, %zu bytes).\n",GetLastError(),count);return false;
    }
    // The initial thread may still be in WOW64's native loader and have no x86
    // context yet. Synchronize on actual x86 entry execution, not context polls.
    // Keep executable code and the arrival flag on separate RX/RW pages.
    SYSTEM_INFO systemInfo = {}; GetSystemInfo(&systemInfo);
    const SIZE_T pageSize = systemInfo.dwPageSize;
    auto parking = static_cast<unsigned char*>(VirtualAllocEx(process, nullptr, pageSize * 2,
        MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE));
    if (!parking) return false;
    const DWORD flagAddress = reinterpret_cast<DWORD>(parking + pageSize);
    unsigned char stub[] = {0xc7,0x05,0,0,0,0,1,0,0,0,0xeb,0xfe};
    memcpy(stub + 2, &flagAddress, sizeof(flagAddress));
    DWORD ignored = 0;
    if (!WriteProcessMemory(process, parking, stub, sizeof(stub), &count) || count != sizeof(stub) ||
        !VirtualProtectEx(process, parking, pageSize, PAGE_EXECUTE_READ, &ignored) ||
        !FlushInstructionCache(process, parking, sizeof(stub))) {
        VirtualFreeEx(process, parking, 0, MEM_RELEASE); return false;
    }
    DWORD oldProtection = 0;
    if (!VirtualProtectEx(process, reinterpret_cast<void*>(entry), sizeof(original),
                          PAGE_EXECUTE_READWRITE, &oldProtection)) {
        fwprintf(stderr,L"Entry fence: entry protection failed (%lu).\n",GetLastError());
        VirtualFreeEx(process, parking, 0, MEM_RELEASE); return false;
    }
    unsigned char park[] = {0xe9,0,0,0,0};
    const DWORD displacement = reinterpret_cast<DWORD>(parking) - static_cast<DWORD>(entry + sizeof(park));
    memcpy(park + 1, &displacement, sizeof(displacement));
    bool wrote = WriteProcessMemory(process, reinterpret_cast<void*>(entry), park, sizeof(park), &count) && count == sizeof(park);
    wrote = FlushInstructionCache(process, reinterpret_cast<void*>(entry), sizeof(original)) && wrote;
    bool suspended = true, ready = false;
    DWORD lastEip=0,loopError=0;
    const wchar_t* stage = L"initial resume";
    CONTEXT context = {}; context.ContextFlags = CONTEXT_CONTROL;
    DWORD priorSuspendCount = wrote ? ResumeThread(thread.value) : DWORD(-1);
    if (priorSuspendCount != DWORD(-1)) suspended = priorSuspendCount > 1;
    if (wrote && priorSuspendCount == 1) {
        suspended = false;
        ULONGLONG deadline = GetTickCount64() + 15000;
        stage = L"entry arrival";
        DWORD arrived = 0;
        while (GetTickCount64() < deadline && WaitForSingleObject(process, 0) == WAIT_TIMEOUT) {
            if (!ReadProcessMemory(process, parking + pageSize, &arrived, sizeof(arrived), &count) || count != sizeof(arrived)) {
                loopError = GetLastError(); break;
            }
            if (arrived == 1) break;
            Sleep(1);
        }
        if (arrived == 1) {
            stage = L"parked suspend";
            if (SuspendThread(thread.value) != DWORD(-1)) {
                suspended = true;
                stage = L"parked context";
                if (GetThreadContext(thread.value, &context)) {
                    lastEip = context.Eip;
                    ready = context.Eip == reinterpret_cast<DWORD>(parking + 10);
                } else loopError = GetLastError();
            } else loopError = GetLastError();
        }
    }
    if (!suspended && SuspendThread(thread.value) != DWORD(-1)) suspended = true;
    // SuspendThread is only a request. GetThreadContext confirms it has taken
    // effect. If arrival/context failed after resume, retain the entry fence and
    // parking pages until caller termination even when suspension was requested.
    bool restored = ready && suspended && WriteProcessMemory(process, reinterpret_cast<void*>(entry), original, sizeof(original), &count) && count == sizeof(original);
    bool protectedAgain = ready && suspended && VirtualProtectEx(process, reinterpret_cast<void*>(entry), sizeof(original), oldProtection, &ignored) != FALSE;
    bool flushed = ready && suspended && FlushInstructionCache(process, reinterpret_cast<void*>(entry), sizeof(original));
    bool succeeded = ready && suspended && restored && protectedAgain && flushed;
    if (succeeded) {
        stage = L"restore entry context";
        context.Eip = static_cast<DWORD>(entry);
        succeeded = SetThreadContext(thread.value, &context) != FALSE;
        if (!succeeded) loopError = GetLastError();
    }
    if (succeeded) {
        stage = L"release parking pages";
        succeeded = VirtualFreeEx(process, parking, 0, MEM_RELEASE) != FALSE;
        if (!succeeded) loopError = GetLastError();
    }
    if(!succeeded){
        DWORD exitCode=0;GetExitCodeProcess(process,&exitCode);
        fwprintf(stderr,L"Entry fence: stage=%ls ready=%d suspended=%d restored=%d protected=%d initialSuspend=%lu lastEip=%08lx exit=%08lx loopError=%lu.\n",
            stage,ready,suspended,restored,protectedAgain,priorSuspendCount,lastEip,exitCode,loopError);
        Handle snapshot(CreateToolhelp32Snapshot(TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32,pid));
        MODULEENTRY32W module={};module.dwSize=sizeof(module);
        if(snapshot.value!=INVALID_HANDLE_VALUE&&Module32FirstW(snapshot.value,&module))do{
            const auto start=reinterpret_cast<uintptr_t>(module.modBaseAddr);
            if(lastEip>=start&&lastEip<start+module.modBaseSize){
                fwprintf(stderr,L"Entry fence last instruction: %ls + 0x%lx.\n",module.szModule,static_cast<unsigned long>(lastEip-start));break;
            }
        }while(Module32NextW(snapshot.value,&module));
    }
    return succeeded;
}

static bool loadModule(HANDLE process, DWORD pid, const wchar_t* path) {
    const auto loadAddress = remoteLoadLibrary(pid);
    if (!loadAddress) return false;
    const SIZE_T length = (wcslen(path)+1) * sizeof(wchar_t);
    auto remote = VirtualAllocEx(process, nullptr, length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    SIZE_T written = 0;
    if (!remote || !WriteProcessMemory(process, remote, path, length, &written) || written != length) {
        if (remote) VirtualFreeEx(process, remote, 0, MEM_RELEASE);
        return false;
    }
    Handle thread(CreateRemoteThread(process, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(loadAddress), remote, 0, nullptr));
    if (!thread.value) { VirtualFreeEx(process, remote, 0, MEM_RELEASE); return false; }
    if (WaitForSingleObject(thread.value, 15000) != WAIT_OBJECT_0) return false;
    DWORD module = 0;
    GetExitCodeThread(thread.value, &module);
    VirtualFreeEx(process, remote, 0, MEM_RELEASE);
    return module != 0;
}

int wmain(int argc, wchar_t** argv) {
    static_assert(sizeof(void*) == 4, "Build the injector for x86");
    const bool quiet = argc == 6 && !wcscmp(argv[1], L"--early") && !wcscmp(argv[2], L"--quiet-test");
    bool early = (argc == 5 && !wcscmp(argv[1], L"--early")) || quiet;
    const int extra = quiet ? 1 : 0;
    if (argc != 3 && !early) { fwprintf(stderr, L"Usage: injector.exe [--early [--quiet-test]] <th12 PID> [primary thread ID] <absolute DLL path>\n"); return 2; }
    wchar_t* end = nullptr;
    unsigned long rawPid = wcstoul(argv[early ? 2 + extra : 1], &end, 10);
    if (!rawPid || *end) { fwprintf(stderr, L"Invalid PID\n"); return 2; }
    DWORD pid = static_cast<DWORD>(rawPid);
    Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                               PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid));
    if (!process.value) { fwprintf(stderr, L"Cannot open game (Windows error %lu)\n", GetLastError()); return 3; }
    const auto profile = knownGame(processPath(process.value));
    if (!profile) { fwprintf(stderr, L"Unsupported game executable; injection refused. Requires a verified TH12 retail 1.00a/1.00b or Steam 1.00b build.\n"); return 4; }
    wchar_t fullPath[32768];
    auto pathLength = GetFullPathNameW(argv[early ? 4 + extra : 2], _countof(fullPath), fullPath, nullptr);
    if (!pathLength || pathLength >= _countof(fullPath) || GetFileAttributesW(fullPath) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"DLL not found\n"); return 5;
    }
    if (early) {
        unsigned long threadId = wcstoul(argv[3 + extra], &end, 10);
        if (!threadId || *end || !initializeLoader(process.value, pid, threadId, *profile)) {
            fwprintf(stderr, L"Cannot initialize the suspended game's loader safely; terminate this owned process.\n"); return 11;
        }
    }
    if (quiet) {
        wchar_t ownPath[32768];
        const auto size = GetModuleFileNameW(nullptr, ownPath, _countof(ownPath));
        if (!size || size >= _countof(ownPath)) return 12;
        auto slash = wcsrchr(ownPath, L'\\');
        if (!slash) return 12;
        slash[1] = 0;
        const auto guard = std::wstring(ownPath) + L"th12coop-quiet-test.dll";
        if (!loadModule(process.value,pid,guard.c_str())) {
            fwprintf(stderr,L"Quiet-test output guard failed; terminate the still-suspended owned process.\n"); return 12;
        }
    }
    if (!loadModule(process.value,pid,fullPath)) {
        fwprintf(stderr,L"Game rejected the DLL or loader did not finish; terminate the owned process.\n"); return 10;
    }
    printf("%lu\n", pid);
    return 0;
}
