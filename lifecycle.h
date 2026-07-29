#pragma once

#include <Windows.h>

enum MacTypeLifecycleState : DWORD
{
	MacTypeLifecycleDormant = 0,
	MacTypeLifecycleScheduled,
	MacTypeLifecycleInitializing,
	MacTypeLifecycleReady,
	MacTypeLifecycleFailed,
	MacTypeLifecycleShuttingDown,
};

struct MacTypeStatus
{
	DWORD size;
	DWORD version;
	MacTypeLifecycleState lifecycle;
	DWORD loadedRenderBackends;
	BOOL hooksEnabled;
	BOOL hooksInstalled;
	DWORD lastDiagnosticCode;
	DWORD lastError;
	DWORD hookedBackendCount;
	DWORD observedComMethodCount;
};

// Stable lifecycle and diagnostic boundary for loaders. Compatibility mode
// schedules initialization after the Windows loader lock has been released;
// a suspended-process loader can call these exports explicitly.
EXTERN_C BOOL WINAPI MacTypeInitialize();
EXTERN_C BOOL WINAPI MacTypeShutdown();
EXTERN_C BOOL WINAPI MacTypeGetStatus(MacTypeStatus* status);
EXTERN_C DWORD WINAPI MacTypeInitializeThread(LPVOID parameter);
