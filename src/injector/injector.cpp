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
    Handle thread(OpenThread(THREAD_SUSPEND_RESUME | THREAD_GET_CONTEXT | THREAD_QUERY_INFORMATION,
                             FALSE, threadId));
    if (!thread.value || GetProcessIdOfThread(thread.value) != pid) return false;
    const uintptr_t entry = profile.bootstrapEntry;
    unsigned char original[sizeof(profile.entryBytes)]; SIZE_T count = 0;
    if (!ReadProcessMemory(process, reinterpret_cast<void*>(entry), original, sizeof(original), &count) ||
        count != sizeof(original) || memcmp(original, profile.entryBytes, sizeof(profile.entryBytes))) return false;
    DWORD oldProtection = 0;
    if (!VirtualProtectEx(process, reinterpret_cast<void*>(entry), sizeof(original),
                          PAGE_EXECUTE_READWRITE, &oldProtection)) return false;
    const unsigned char park[] = {0xeb,0xfe};
    bool wrote = WriteProcessMemory(process, reinterpret_cast<void*>(entry), park, sizeof(park), &count) && count == sizeof(park);
    FlushInstructionCache(process, reinterpret_cast<void*>(entry), sizeof(original));
    bool suspended = true, ready = false;
    DWORD priorSuspendCount = wrote ? ResumeThread(thread.value) : DWORD(-1);
    if (priorSuspendCount != DWORD(-1)) suspended = priorSuspendCount > 1;
    if (wrote && priorSuspendCount == 1) {
        suspended = false;
        ULONGLONG deadline = GetTickCount64() + 15000;
        while (GetTickCount64() < deadline && WaitForSingleObject(process, 0) == WAIT_TIMEOUT) {
            Sleep(1);
            if (SuspendThread(thread.value) == DWORD(-1)) break;
            suspended = true;
            CONTEXT context = {}; context.ContextFlags = CONTEXT_CONTROL;
            if (!GetThreadContext(thread.value, &context)) break;
            if (context.Eip == entry) { ready = true; break; }
            if (ResumeThread(thread.value) == DWORD(-1)) break;
            suspended = false;
        }
    }
    if (!suspended && SuspendThread(thread.value) != DWORD(-1)) suspended = true;
    bool restored = WriteProcessMemory(process, reinterpret_cast<void*>(entry), original, sizeof(original), &count) && count == sizeof(original);
    DWORD ignored = 0;
    bool protectedAgain = VirtualProtectEx(process, reinterpret_cast<void*>(entry), sizeof(original), oldProtection, &ignored) != FALSE;
    FlushInstructionCache(process, reinterpret_cast<void*>(entry), sizeof(original));
    return ready && suspended && restored && protectedAgain;
}

int wmain(int argc, wchar_t** argv) {
    static_assert(sizeof(void*) == 4, "Build the injector for x86");
    bool early = argc == 5 && !wcscmp(argv[1], L"--early");
    if (argc != 3 && !early) { fwprintf(stderr, L"Usage: injector.exe [--early] <th12 PID> [primary thread ID] <absolute DLL path>\n"); return 2; }
    wchar_t* end = nullptr;
    unsigned long rawPid = wcstoul(argv[early ? 2 : 1], &end, 10);
    if (!rawPid || *end) { fwprintf(stderr, L"Invalid PID\n"); return 2; }
    DWORD pid = static_cast<DWORD>(rawPid);
    Handle process(OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                               PROCESS_VM_WRITE | PROCESS_VM_READ | SYNCHRONIZE, FALSE, pid));
    if (!process.value) { fwprintf(stderr, L"Cannot open game (Windows error %lu)\n", GetLastError()); return 3; }
    const auto profile = knownGame(processPath(process.value));
    if (!profile) { fwprintf(stderr, L"Unsupported game executable; injection refused. Requires a verified TH12 retail 1.00a/1.00b or Steam 1.00b build.\n"); return 4; }
    wchar_t fullPath[32768];
    auto pathLength = GetFullPathNameW(argv[early ? 4 : 2], _countof(fullPath), fullPath, nullptr);
    if (!pathLength || pathLength >= _countof(fullPath) || GetFileAttributesW(fullPath) == INVALID_FILE_ATTRIBUTES) {
        fwprintf(stderr, L"DLL not found\n"); return 5;
    }
    if (early) {
        unsigned long threadId = wcstoul(argv[3], &end, 10);
        if (!threadId || *end || !initializeLoader(process.value, pid, threadId, *profile)) {
            fwprintf(stderr, L"Cannot initialize the suspended game's loader safely; terminate this owned process.\n"); return 11;
        }
    }
    auto loadAddress = remoteLoadLibrary(pid);
    if (!loadAddress) { fwprintf(stderr, L"Cannot resolve x86 loader address\n"); return 6; }
    const SIZE_T length = (wcslen(fullPath)+1) * sizeof(wchar_t);
    auto remote = VirtualAllocEx(process.value, nullptr, length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
    SIZE_T written = 0;
    if (!remote || !WriteProcessMemory(process.value, remote, fullPath, length, &written) || written != length) {
        if (remote) VirtualFreeEx(process.value, remote, 0, MEM_RELEASE);
        fwprintf(stderr, L"Cannot copy DLL path\n"); return 7;
    }
    Handle thread(CreateRemoteThread(process.value, nullptr, 0,
        reinterpret_cast<LPTHREAD_START_ROUTINE>(loadAddress), remote, 0, nullptr));
    if (!thread.value) { VirtualFreeEx(process.value, remote, 0, MEM_RELEASE); fwprintf(stderr, L"Cannot start game loader\n"); return 8; }
    if (WaitForSingleObject(thread.value, 15000) != WAIT_OBJECT_0) {
        // Loader may still read its argument; leave it allocated until process exit.
        fwprintf(stderr, L"Game loader did not finish within 15 seconds\n"); return 9;
    }
    DWORD module = 0;
    GetExitCodeThread(thread.value, &module);
    VirtualFreeEx(process.value, remote, 0, MEM_RELEASE);
    if (!module) { fwprintf(stderr, L"Game rejected the DLL; check architecture and dependencies\n"); return 10; }
    printf("%lu\n", pid);
    return 0;
}
