#pragma once
#include <windows.h>
struct GuardStatus {
    DWORD size, targetPid, timeoutMs;
    volatile LONG ready, finished, patched, created, cloaked, failures;
    volatile LONG reopen;
    volatile LONG hiddenSignal, hideCalls;
    DWORD lastFailure;
    DWORD stage, resumed, alive;
};
enum GuardStage : DWORD { Prepare = 1, CreateTarget, InitializeLoader, LoadComponent, InitializeComponent, ProtectWindows, RestoreWindows, Complete };
extern "C" __declspec(dllexport) DWORD WINAPI InitializeGuard(void*);
// Private handles are duplicated directly into the verified child, avoiding named
// object access across the administrator/desktop-user integrity boundary.
struct GuardBootstrap {
    DWORD size, reserved;
    HANDLE shared, release, finished;
};
