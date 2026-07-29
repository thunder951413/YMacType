#pragma once

#include <Windows.h>

enum class MacTypeLogLevel : DWORD
{
	Debug = 0,
	Info,
	Warning,
	Error,
};

enum class MacTypeDiagnosticCode : DWORD
{
	None = 0,
	LifecycleScheduled = 100,
	LifecycleInitializeBegin,
	LifecycleReady,
	LifecycleInitializeFailed,
	LifecycleShutdown,
	HookTransactionFailed = 200,
	BackendObserved = 300,
	BackendHookFailed,
	ChildInjectionBegin = 400,
	ChildInjectionReady,
	ChildInjectionFailed,
	MitigationBlocked = 500,
	LoaderFailure = 600,
};

void MacTypeLog(
	MacTypeLogLevel level,
	MacTypeDiagnosticCode code,
	const wchar_t* format,
	...);

void MacTypeSetLastDiagnostic(
	MacTypeDiagnosticCode code,
	DWORD error);

MacTypeDiagnosticCode MacTypeGetLastDiagnosticCode();
DWORD MacTypeGetLastDiagnosticError();
