#pragma once
#include <windows.h>
struct GuardStatus {
    DWORD size, targetPid, timeoutMs;
    volatile LONG ready, finished, patched, created, cloaked, failures;
    volatile LONG reopen;
    volatile LONG hiddenSignal, hideCalls;
    DWORD lastFailure;
};
extern "C" __declspec(dllexport) DWORD WINAPI InitializeGuard(void*);
