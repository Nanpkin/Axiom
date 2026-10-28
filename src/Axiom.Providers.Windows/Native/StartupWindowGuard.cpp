#include "StartupWindowGuard.h"
#include <dwmapi.h>
#include <commctrl.h>
#include <tlhelp32.h>
#include <cstring>
#include <string>
#include <vector>
static HINSTANCE self;
static GuardStatus* status;
static HANDLE mapping, releaseEvent, finishedEvent;
static volatile LONG active;
static UINT finishMessage;
struct WindowState { bool cloaked = false, shown = false, initiallyVisible = false; };
static SRWLOCK windowsLock = SRWLOCK_INIT;
static std::vector<HWND> windows;
struct ImportPatch { ULONG_PTR* address; ULONG_PTR original, replacement; HMODULE module; };
static std::vector<ImportPatch> imports;
static SRWLOCK importsLock = SRWLOCK_INIT;
static std::wstring applicationDirectory;
static void Failed(DWORD error) { status->lastFailure = error; InterlockedIncrement(&status->failures); }
static void Patch(HMODULE module);
static void PatchLoaded(HMODULE module) {
    if (!module || (reinterpret_cast<ULONG_PTR>(module) & 3) || module == self) return;
    wchar_t path[32768];
    if (GetModuleFileNameW(module, path, 32768) && !_wcsnicmp(path, applicationDirectory.c_str(), applicationDirectory.size())) Patch(module);
}
static HMODULE WINAPI LoadW(LPCWSTR path) { auto module = LoadLibraryW(path); PatchLoaded(module); return module; }
static HMODULE WINAPI LoadA(LPCSTR path) { auto module = LoadLibraryA(path); PatchLoaded(module); return module; }
static HMODULE WINAPI LoadExW(LPCWSTR path, HANDLE file, DWORD flags) { auto module = LoadLibraryExW(path, file, flags); if (!(flags & (LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE))) PatchLoaded(module); return module; }
static HMODULE WINAPI LoadExA(LPCSTR path, HANDLE file, DWORD flags) { auto module = LoadLibraryExA(path, file, flags); if (!(flags & (LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE | LOAD_LIBRARY_AS_DATAFILE_EXCLUSIVE))) PatchLoaded(module); return module; }
static void Hidden(HWND hwnd, bool wasVisible) {
    if (!wasVisible || IsWindowVisible(hwnd) || !InterlockedCompareExchange(&active, 0, 0)) return;
    DWORD cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && (cloaked & DWM_CLOAKED_APP)) { InterlockedIncrement(&status->hiddenSignal); SetEvent(releaseEvent); }
}
static BOOL WINAPI Show(HWND hwnd, int command) { if (command == SW_HIDE) InterlockedIncrement(&status->hideCalls); bool before = IsWindowVisible(hwnd) != FALSE; auto result = ShowWindow(hwnd, command); Hidden(hwnd, before); return result; }
static BOOL WINAPI Position(HWND hwnd, HWND after, int x, int y, int width, int height, UINT flags) { if (flags & SWP_HIDEWINDOW) InterlockedIncrement(&status->hideCalls); bool before = IsWindowVisible(hwnd) != FALSE; auto result = SetWindowPos(hwnd, after, x, y, width, height, flags); Hidden(hwnd, before); return result; }
static void Cloak(HWND hwnd, WindowState* state) {
    if (state->cloaked) return;
    AcquireSRWLockShared(&windowsLock);
    if (!InterlockedCompareExchange(&active, 0, 0)) { ReleaseSRWLockShared(&windowsLock); return; }
    BOOL on = TRUE; auto hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &on, sizeof(on));
    state->cloaked = SUCCEEDED(hr);
    if (state->cloaked) InterlockedIncrement(&status->cloaked); else InterlockedIncrement(&status->failures);
    ReleaseSRWLockShared(&windowsLock);
}
static LRESULT CALLBACK Subclass(HWND hwnd, UINT msg, WPARAM w, LPARAM l, UINT_PTR id, DWORD_PTR data) {
    auto state = reinterpret_cast<WindowState*>(data);
    if (msg == WM_CREATE && state->initiallyVisible && InterlockedCompareExchange(&active, 0, 0)) {
        auto result = DefSubclassProc(hwnd, msg, w, l);
        Cloak(hwnd, state);
        return result;
    }
    if (InterlockedCompareExchange(&active, 0, 0) && ((msg == WM_SHOWWINDOW && w) ||
        (msg == WM_WINDOWPOSCHANGING && (reinterpret_cast<WINDOWPOS*>(l)->flags & SWP_SHOWWINDOW)))) Cloak(hwnd, state);
    if (msg == WM_SHOWWINDOW && state->cloaked) {
        if (w) state->shown = true;
        else if (state->shown && InterlockedCompareExchange(&active, 0, 0)) SetEvent(releaseEvent);
    }
    if (msg == finishMessage) {
        // Visibility cleanup already completed on the guard worker. This queued message
        // only removes bookkeeping and must never hide a later manual reopen.
        AcquireSRWLockExclusive(&windowsLock);
        for (auto it = windows.begin(); it != windows.end(); ++it) if (*it == hwnd) { windows.erase(it); break; }
        ReleaseSRWLockExclusive(&windowsLock);
        RemoveWindowSubclass(hwnd, Subclass, id); delete state;
        return 0;
    }
    if (msg == WM_NCDESTROY) {
        if (state->shown && InterlockedCompareExchange(&active, 0, 0)) SetEvent(releaseEvent);
        AcquireSRWLockExclusive(&windowsLock);
        for (auto it = windows.begin(); it != windows.end(); ++it) if (*it == hwnd) { windows.erase(it); break; }
        ReleaseSRWLockExclusive(&windowsLock);
        RemoveWindowSubclass(hwnd, Subclass, id); delete state;
    }
    return DefSubclassProc(hwnd, msg, w, l);
}
static LRESULT CALLBACK CreateHook(int code, WPARAM w, LPARAM l) {
    if (code == HCBT_CREATEWND && InterlockedCompareExchange(&active, 0, 0)) {
        auto cs = reinterpret_cast<CBT_CREATEWNDW*>(l)->lpcs;
        if (!(cs->style & WS_CHILD) && cs->hwndParent != HWND_MESSAGE) {
            auto hwnd = reinterpret_cast<HWND>(w); auto state = new WindowState(); state->initiallyVisible = (cs->style & WS_VISIBLE) != 0;
            AcquireSRWLockExclusive(&windowsLock);
            if (!InterlockedCompareExchange(&active, 0, 0)) { ReleaseSRWLockExclusive(&windowsLock); delete state; return CallNextHookEx(nullptr, code, w, l); }
            if (SetWindowSubclass(hwnd, Subclass, 0x4158494f, reinterpret_cast<DWORD_PTR>(state))) {
                windows.push_back(hwnd);
                InterlockedIncrement(&status->created);
            } else { delete state; InterlockedIncrement(&status->failures); }
            ReleaseSRWLockExclusive(&windowsLock);
        }
    }
    return CallNextHookEx(nullptr, code, w, l);
}
static HWND WINAPI CreateW(DWORD ex, LPCWSTR cls, LPCWSTR title, DWORD style, int x, int y, int width, int height, HWND parent, HMENU menu, HINSTANCE instance, LPVOID data) {
    auto hook = InterlockedCompareExchange(&active, 0, 0) ? SetWindowsHookExW(WH_CBT, CreateHook, self, GetCurrentThreadId()) : nullptr;
    if (!hook && InterlockedCompareExchange(&active, 0, 0)) InterlockedIncrement(&status->failures);
    auto hwnd = CreateWindowExW(ex, cls, title, style, x, y, width, height, parent, menu, instance, data);
    if (hwnd && !(style & WS_CHILD) && parent != HWND_MESSAGE) {
        AcquireSRWLockShared(&windowsLock);
        if (InterlockedCompareExchange(&active, 0, 0)) {
            BOOL on = TRUE;
            if (SUCCEEDED(DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &on, sizeof(on)))) InterlockedIncrement(&status->cloaked);
        }
        ReleaseSRWLockShared(&windowsLock);
    }
    if (hook) UnhookWindowsHookEx(hook); return hwnd;
}
static HWND WINAPI CreateA(DWORD ex, LPCSTR cls, LPCSTR title, DWORD style, int x, int y, int width, int height, HWND parent, HMENU menu, HINSTANCE instance, LPVOID data) {
    auto hook = InterlockedCompareExchange(&active, 0, 0) ? SetWindowsHookExW(WH_CBT, CreateHook, self, GetCurrentThreadId()) : nullptr;
    auto hwnd = CreateWindowExA(ex, cls, title, style, x, y, width, height, parent, menu, instance, data);
    if (hwnd && !(style & WS_CHILD) && parent != HWND_MESSAGE) {
        AcquireSRWLockShared(&windowsLock);
        if (InterlockedCompareExchange(&active, 0, 0)) {
            BOOL on = TRUE;
            if (SUCCEEDED(DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &on, sizeof(on)))) InterlockedIncrement(&status->cloaked);
        }
        ReleaseSRWLockShared(&windowsLock);
    }
    if (hook) UnhookWindowsHookEx(hook); return hwnd;
}
static void Patch(HMODULE module) {
    auto image = reinterpret_cast<BYTE*>(module); auto dos = reinterpret_cast<IMAGE_DOS_HEADER*>(image);
    auto nt = reinterpret_cast<IMAGE_NT_HEADERS*>(image + dos->e_lfanew);
    auto offset = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_IMPORT].VirtualAddress; if (!offset) return;
    AcquireSRWLockExclusive(&importsLock);
    if (!InterlockedCompareExchange(&active, 0, 0)) { ReleaseSRWLockExclusive(&importsLock); return; }
    auto entry = reinterpret_cast<IMAGE_IMPORT_DESCRIPTOR*>(image + offset);
    for (; entry->Name; ++entry) {
        if (!entry->OriginalFirstThunk) continue;
        auto names = reinterpret_cast<IMAGE_THUNK_DATA*>(image + entry->OriginalFirstThunk);
        auto addresses = reinterpret_cast<IMAGE_THUNK_DATA*>(image + entry->FirstThunk);
        for (; names->u1.AddressOfData; ++names, ++addresses) {
            if (IMAGE_SNAP_BY_ORDINAL(names->u1.Ordinal)) continue;
            auto name = reinterpret_cast<IMAGE_IMPORT_BY_NAME*>(image + names->u1.AddressOfData)->Name;
            ULONG_PTR replacement = !strcmp(name, "CreateWindowExW") ? reinterpret_cast<ULONG_PTR>(CreateW) : !strcmp(name, "CreateWindowExA") ? reinterpret_cast<ULONG_PTR>(CreateA) : 0;
            if (!strcmp(name, "LoadLibraryW")) replacement = reinterpret_cast<ULONG_PTR>(LoadW);
            if (!strcmp(name, "LoadLibraryA")) replacement = reinterpret_cast<ULONG_PTR>(LoadA);
            if (!strcmp(name, "LoadLibraryExW")) replacement = reinterpret_cast<ULONG_PTR>(LoadExW);
            if (!strcmp(name, "LoadLibraryExA")) replacement = reinterpret_cast<ULONG_PTR>(LoadExA);
            if (!strcmp(name, "ShowWindow")) replacement = reinterpret_cast<ULONG_PTR>(Show);
            if (!strcmp(name, "SetWindowPos")) replacement = reinterpret_cast<ULONG_PTR>(Position);
            if (!replacement || addresses->u1.Function == replacement) continue;
            DWORD old;
            if (VirtualProtect(&addresses->u1.Function, sizeof(ULONG_PTR), PAGE_READWRITE, &old)) {
                imports.push_back({ &addresses->u1.Function, addresses->u1.Function, replacement, module });
                addresses->u1.Function = replacement; DWORD ignored; VirtualProtect(&addresses->u1.Function, sizeof(ULONG_PTR), old, &ignored); InterlockedIncrement(&status->patched);
            } else InterlockedIncrement(&status->failures);
        }
    }
    ReleaseSRWLockExclusive(&importsLock);
}
static DWORD WINAPI Finish(void*) {
    WaitForSingleObject(releaseEvent, status->timeoutMs);
    AcquireSRWLockExclusive(&windowsLock);
    InterlockedExchange(&active, 0);
    auto copy = windows; ReleaseSRWLockExclusive(&windowsLock);
    AcquireSRWLockExclusive(&importsLock);
    for (auto patch : imports) {
        MEMORY_BASIC_INFORMATION memory{};
        if (!VirtualQuery(patch.address, &memory, sizeof(memory)) || memory.State != MEM_COMMIT ||
            memory.Type != MEM_IMAGE || memory.AllocationBase != patch.module) continue; // Plugin already unloaded.
        DWORD old;
        if (VirtualProtect(patch.address, sizeof(ULONG_PTR), PAGE_READWRITE, &old)) {
            InterlockedCompareExchangePointer(reinterpret_cast<void* volatile*>(patch.address), reinterpret_cast<void*>(patch.original), reinterpret_cast<void*>(patch.replacement));
            DWORD ignored; VirtualProtect(patch.address, sizeof(ULONG_PTR), old, &ignored);
        } else Failed(GetLastError());
    }
    ReleaseSRWLockExclusive(&importsLock);
    std::vector<HWND> reopenWindows;
    for (auto hwnd : copy) {
        DWORD owner; GetWindowThreadProcessId(hwnd, &owner);
        if (owner != GetCurrentProcessId()) continue;
        DWORD cloaked = 0;
        if (SUCCEEDED(DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && (cloaked & DWM_CLOAKED_APP)) {
            if (status->reopen && IsWindowVisible(hwnd)) reopenWindows.push_back(hwnd);
            if (IsWindowVisible(hwnd)) ShowWindow(hwnd, SW_HIDE);
            if (!IsWindowVisible(hwnd)) {
                BOOL off = FALSE;
                auto hr = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, &off, sizeof(off)); if (FAILED(hr)) Failed(HRESULT_CODE(hr));
            } else Failed(ERROR_BUSY);
        }
        PostMessageW(hwnd, finishMessage, 0, 0);
    }
    if (status->reopen) for (auto hwnd : reopenWindows) { ShowWindow(hwnd, SW_SHOWNORMAL); SetForegroundWindow(hwnd); }
    // Publish only after all visibility changes. Deferred subclass removal never changes
    // visibility, and cannot overtake and hide a later manual reopen.
    InterlockedExchange(&status->finished, 1); SetEvent(finishedEvent); return 0;
}
extern "C" __declspec(dllexport) DWORD WINAPI InitializeGuard(void* parameter) {
    auto name = static_cast<const wchar_t*>(parameter); if (!name) return 1;
    mapping = OpenFileMappingW(FILE_MAP_ALL_ACCESS, FALSE, name); if (!mapping) return 2;
    status = static_cast<GuardStatus*>(MapViewOfFile(mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(GuardStatus)));
    if (!status || status->size != sizeof(GuardStatus) || status->targetPid != GetCurrentProcessId()) return 3;
    finishMessage = RegisterWindowMessageW(L"Axiom.VisibilityGuard.Finish.v1");
    releaseEvent = OpenEventW(SYNCHRONIZE | EVENT_MODIFY_STATE, FALSE, (std::wstring(name) + L".release").c_str());
    finishedEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, (std::wstring(name) + L".finished").c_str());
    if (!releaseEvent || !finishedEvent || !finishMessage) return 4;
    InterlockedExchange(&active, 1);
    wchar_t image[32768]; GetModuleFileNameW(nullptr, image, 32768);
    std::wstring directory(image); directory.resize(directory.find_last_of(L"\\") + 1);
    applicationDirectory = directory;
    auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, GetCurrentProcessId());
    MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (Module32FirstW(snapshot, &entry)) do {
        if (entry.hModule != self && !_wcsnicmp(entry.szExePath, directory.c_str(), directory.size())) Patch(entry.hModule);
    } while (Module32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    auto worker = CreateThread(nullptr, 0, Finish, nullptr, 0, nullptr); if (!worker) return 5; CloseHandle(worker);
    InterlockedExchange(&status->ready, 1); return 0;
}
BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) { if (reason == DLL_PROCESS_ATTACH) self = instance; return TRUE; }

// Host-side entry. Failure before ResumeThread only terminates the process just created
// by this call; after resume, cleanup releases protection and leaves the application running.
extern "C" __declspec(dllexport) DWORD WINAPI StartGuarded(const wchar_t* path, const wchar_t* arguments,
    const wchar_t* directory, const wchar_t* component, DWORD timeoutMs, HANDLE cancellation, HANDLE reopening, DWORD* pid, GuardStatus* report) {
    if (!path || !arguments || !directory || !component || !pid || timeoutMs < 1000 || timeoutMs > 20000) return ERROR_INVALID_PARAMETER;
    DWORD type;
    if (!GetBinaryTypeW(path, &type)) return GetLastError();
    if (type != SCS_64BIT_BINARY) return ERROR_BAD_EXE_FORMAT;
    const wchar_t* dllPath = component;
    wchar_t name[128]; swprintf_s(name, L"Local\\AxiomStartupGuard-%lu-%llu", GetCurrentProcessId(), GetTickCount64());
    // The managed start path serializes launches, so this per-call name cannot collide.
    HANDLE shared = nullptr, release = nullptr, finished = nullptr, loader = nullptr, init = nullptr;
    GuardStatus* view = nullptr; void* remote = nullptr; PROCESS_INFORMATION process{}; bool resumed = false;
    DWORD result = ERROR_GEN_FAILURE;
    auto cleanup = [&]() {
        if (process.hProcess && !resumed) { TerminateProcess(process.hProcess, ERROR_PROCESS_ABORTED); WaitForSingleObject(process.hProcess, 3000); }
        if (remote && process.hProcess) VirtualFreeEx(process.hProcess, remote, 0, MEM_RELEASE);
        for (auto handle : { loader, init, process.hThread, process.hProcess, release, finished, shared }) if (handle) CloseHandle(handle);
        if (view) UnmapViewOfFile(view);
    };
    do {
        shared = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(GuardStatus), name);
        if (!shared || GetLastError() == ERROR_ALREADY_EXISTS) { result = ERROR_ALREADY_EXISTS; break; }
        view = static_cast<GuardStatus*>(MapViewOfFile(shared, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(GuardStatus)));
        release = CreateEventW(nullptr, TRUE, FALSE, (std::wstring(name) + L".release").c_str());
        finished = CreateEventW(nullptr, TRUE, FALSE, (std::wstring(name) + L".finished").c_str());
        if (!view || !release || !finished) { result = GetLastError(); break; }
        ZeroMemory(view, sizeof(GuardStatus)); view->size = sizeof(GuardStatus); view->timeoutMs = timeoutMs;
        STARTUPINFOW startup{}; startup.cb = sizeof(startup);
        std::wstring command = L"\"" + std::wstring(path) + L"\" " + arguments;
        if (!CreateProcessW(path, command.data(), nullptr, nullptr, FALSE, CREATE_SUSPENDED | CREATE_NO_WINDOW,
            nullptr, directory, &startup, &process)) { result = GetLastError(); break; }
        *pid = process.dwProcessId; view->targetPid = process.dwProcessId;
        auto bytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);
        remote = VirtualAllocEx(process.hProcess, nullptr, max(bytes, sizeof(name)), MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
        if (!remote || !WriteProcessMemory(process.hProcess, remote, dllPath, bytes, nullptr)) { result = GetLastError(); break; }
        auto loadAddress = GetProcAddress(GetModuleHandleW(L"kernel32.dll"), "LoadLibraryW");
        // ntdll is mapped by process creation; kernel32 may not be present until the first
        // thread runs loader initialization. Bootstrap only the loader, leaving the main
        // thread suspended, then verify LoadLibraryW before using its address remotely.
        BYTE remoteCode[32]; SIZE_T read = 0;
        auto bootstrapAddress = GetProcAddress(GetModuleHandleW(L"ntdll.dll"), "RtlExitUserThread");
        if (!ReadProcessMemory(process.hProcess, reinterpret_cast<void*>(bootstrapAddress), remoteCode, sizeof(remoteCode), &read) ||
            read != sizeof(remoteCode) || memcmp(remoteCode, reinterpret_cast<void*>(bootstrapAddress), sizeof(remoteCode))) { result = ERROR_INVALID_ADDRESS; break; }
        auto bootstrap = CreateRemoteThread(process.hProcess, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(bootstrapAddress), nullptr, 0, nullptr);
        if (!bootstrap) { result = GetLastError(); break; }
        auto bootstrapped = WaitForSingleObject(bootstrap, 5000) == WAIT_OBJECT_0; CloseHandle(bootstrap);
        if (!bootstrapped) { result = ERROR_TIMEOUT; break; }
        if (!ReadProcessMemory(process.hProcess, reinterpret_cast<void*>(loadAddress), remoteCode, sizeof(remoteCode), &read) ||
            read != sizeof(remoteCode) || memcmp(remoteCode, reinterpret_cast<void*>(loadAddress), sizeof(remoteCode))) { result = ERROR_INVALID_ADDRESS; break; }
        loader = CreateRemoteThread(process.hProcess, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(loadAddress), remote, 0, nullptr);
        if (!loader || WaitForSingleObject(loader, 3000) != WAIT_OBJECT_0) { result = ERROR_TIMEOUT; break; }
        auto snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPMODULE, process.dwProcessId);
        MODULEENTRY32W entry{}; entry.dwSize = sizeof(entry); HMODULE loaded = nullptr;
        if (snapshot != INVALID_HANDLE_VALUE) {
            if (Module32FirstW(snapshot, &entry)) do { if (!_wcsicmp(entry.szExePath, dllPath)) loaded = entry.hModule; } while (Module32NextW(snapshot, &entry));
            CloseHandle(snapshot);
        }
        if (!loaded) { result = ERROR_DLL_INIT_FAILED; break; }
        if (!WriteProcessMemory(process.hProcess, remote, name, (wcslen(name) + 1) * sizeof(wchar_t), nullptr)) { result = GetLastError(); break; }
        auto offset = reinterpret_cast<BYTE*>(GetProcAddress(self, "InitializeGuard")) - reinterpret_cast<BYTE*>(self);
        init = CreateRemoteThread(process.hProcess, nullptr, 0, reinterpret_cast<LPTHREAD_START_ROUTINE>(reinterpret_cast<BYTE*>(loaded) + offset), remote, 0, nullptr);
        if (!init || WaitForSingleObject(init, 3000) != WAIT_OBJECT_0 || !view->ready || !view->patched) { result = ERROR_DLL_INIT_FAILED; break; }
        if (WaitForSingleObject(cancellation, 0) == WAIT_OBJECT_0) { result = ERROR_CANCELLED; break; }
        if (ResumeThread(process.hThread) == static_cast<DWORD>(-1)) { result = GetLastError(); break; }
        resumed = true;
        HANDLE waits[] = { finished, process.hProcess, cancellation, reopening };
        auto waited = WaitForMultipleObjects(4, waits, FALSE, timeoutMs + 2000);
        if (waited == WAIT_OBJECT_0) result = view->failures ? ERROR_NOT_SUPPORTED : ERROR_SUCCESS;
        else if (waited == WAIT_OBJECT_0 + 1) result = ERROR_PROCESS_ABORTED;
        else {
            if (waited == WAIT_OBJECT_0 + 3) InterlockedExchange(&view->reopen, 1);
            SetEvent(release);
            auto restored = WaitForSingleObject(finished, 3000) == WAIT_OBJECT_0;
            result = restored && waited == WAIT_OBJECT_0 + 3 ? (view->failures ? ERROR_NOT_SUPPORTED : ERROR_SUCCESS) :
                waited == WAIT_OBJECT_0 + 2 && restored ? ERROR_CANCELLED : ERROR_TIMEOUT;
        }
    } while (false);
    if (report && view) *report = *view;
    cleanup(); return result;
}
