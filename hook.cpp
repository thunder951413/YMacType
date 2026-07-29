// API hook
//
// GetProcAddressで得たcall先（関数本体）を直接書き換え、
// 自分のフック関数にjmpさせる。
//
// 内部で元のAPIを使う時は、コードを一度戻してからcall。
// すぐにjmpコードに戻す。
//
// マルチスレッドで 書き換え中にcallされると困るので、
// CriticalSectionで排他制御しておく。
//

#include "override.h"
#include "ft.h"
#include "fteng.h"
#include <locale.h>
#include "undocAPI.h"
#include "delayimp.h"
#include <dwrite_2.h>
#include <dwrite_3.h>
#include <VersionHelpers.h>
#include "EventLogging.h"
#include "hookCounter.h"
#include "dynCodeHelper.h"
#include "lifecycle.h"
#include "render_backend.h"
#include "diagnostics.h"

DWORD GetObservedComMethodImplementationCount();

#ifdef STATIC_LIB
	#include <aux_ulib.h>
	#include <psapi.h>

	#pragma comment(lib, "aux_ulib.lib")
	#pragma comment(lib, "psapi.lib")
#endif

#ifndef _WIN64
#include "wow64ext.h"
#endif
#ifdef INFINALITY
#include <freetype/ftenv.h>
#endif
#pragma comment(lib, "delayimp")

HINSTANCE g_dllInstance;
extern LONG g_bHookEnabled;

//PFNLdrGetProcedureAddress LdrGetProcedureAddress = (PFNLdrGetProcedureAddress)GetProcAddress(LoadLibrary(_T("ntdll.dll")),"LdrGetProcedureAddress");
//PFNCreateProcessW nCreateProcessW = (PFNCreateProcessW)MyGetProcAddress(LoadLibrary(_T("kernel32.dll")),"CreateProcessW");
//PFNCreateProcessA nCreateProcessA = (PFNCreateProcessA)MyGetProcAddress(LoadLibrary(_T("kernel32.dll")),"CreateProcessA");
// HMODULE hGDIPP = GetModuleHandleW(L"gdiplus.dll");
// typedef int (WINAPI *PFNGdipCreateFontFamilyFromName)(const WCHAR *name, void *fontCollection, void **FontFamily);
// PFNGdipCreateFontFamilyFromName GdipCreateFontFamilyFromName = hGDIPP? (PFNGdipCreateFontFamilyFromName)GetProcAddress(hGDIPP, "GdipCreateFontFamilyFromName"):0;

#ifdef USE_DETOURS

#include "detours.h"
#ifdef _M_IX86
#pragma comment (lib, "detours.lib")
#else
#pragma comment (lib, "detours64.lib")
#endif
// DATA_foo、ORIG_foo の２つをまとめて定義するマクロ
#define HOOK_MANUALLY HOOK_DEFINE
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	rettype (WINAPI * ORIG_##name) argtype; \
	BOOL IsHooked_##name = false; \
	rettype WINAPI REF_##name argtype { \
		HCounter _; \
		if (!InterlockedCompareExchange(&g_bHookEnabled, FALSE, FALSE)) \
			return ORIG_##name arglist; \
		return IMPL_##name arglist; \
	}

#include "hooklist.h"

#undef HOOK_DEFINE
#undef HOOK_MANUALLY

//
#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	ORIG_##name = name;
#pragma optimize("s", on)
static void hook_initinternal()
{
#include "hooklist.h"
}
#pragma optimize("", on)
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	if (&ORIG_##name && !IsHooked_##name) { \
		if (DetourAttach(&(PVOID&)ORIG_##name, REF_##name) == NOERROR) IsHooked_##name = true; \
	}

static void hook_reset_normal_state();

static LONG hook_init()
{
	DetourRestoreAfterWith();

	DetourTransactionBegin();
	DetourUpdateThread(GetCurrentThread());

#include "hooklist.h"

	LONG error = DetourTransactionCommit();

	if (error != NOERROR) {
		hook_reset_normal_state();
		TRACE(_T("hook_init error: %#x\n"), error);
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::HookTransactionFailed,
			static_cast<DWORD>(error));
		MacTypeLog(
			MacTypeLogLevel::Error,
			MacTypeDiagnosticCode::HookTransactionFailed,
			L"Detours hook transaction failed");
	}
	return error;
}
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

static void hook_reset_normal_state()
{
#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;
#define HOOK_DEFINE(rettype, name, argtype, arglist) IsHooked_##name = false;
#include "hooklist.h"
#undef HOOK_DEFINE
#undef HOOK_MANUALLY
}

#define HOOK_DEFINE(rettype, name, argtype, arglist);
#define HOOK_MANUALLY(rettype, name, argtype, arglist) \
	LONG hook_demand_##name(bool bForce = false){ \
	DetourRestoreAfterWith(); \
	DetourTransactionBegin(); \
	DetourUpdateThread(GetCurrentThread()); \
	LONG attachError = NOERROR; \
	if (&ORIG_##name && (bForce || !IsHooked_##name)) \
		attachError = DetourAttach(&(PVOID&)ORIG_##name, REF_##name); \
	LONG error = DetourTransactionCommit(); \
	if (attachError == NOERROR && error == NOERROR) IsHooked_##name = true; \
	else IsHooked_##name = false; \
	LONG result = attachError != NOERROR ? attachError : error; \
	if (result != NOERROR) TRACE(_T("hook_init error: %#x\n"), result); \
	return result; \
}

#include "hooklist.h"
#undef HOOK_MANUALLY
#undef HOOK_DEFINE

//
#define HOOK_MANUALLY HOOK_DEFINE
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	if (IsHooked_##name) DetourDetach(&(PVOID&)ORIG_##name, REF_##name);
static LONG hook_term()
{
	DetourTransactionBegin();
	DetourUpdateThread(GetCurrentThread());

#include "hooklist.h"

	LONG error = DetourTransactionCommit();

	if (error != NOERROR) {
		TRACE(_T("hook_term error: %#x\n"), error);
		return error;
	}

#undef HOOK_DEFINE
#undef HOOK_MANUALLY
#define HOOK_MANUALLY HOOK_DEFINE
#define HOOK_DEFINE(rettype, name, argtype, arglist) IsHooked_##name = false;
#include "hooklist.h"
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

	HCounter::wait(3000);
	return NOERROR;
}
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

#else
#include "easyhook.h"
#ifdef STATIC_LIB
#ifdef _M_IX86
#pragma comment (lib, "easyhk32_s.lib")
#else
#pragma comment (lib, "easyhk64_s.lib")
#endif
#else
#ifdef _M_IX86
#pragma comment (lib, "easyhk32.lib")
#else
#pragma comment (lib, "easyhk64.lib")
#endif
#endif

#define HOOK_MANUALLY HOOK_DEFINE
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	rettype (WINAPI * ORIG_##name) argtype; \
	HOOK_TRACE_INFO HOOK_##name = {0}; \
	rettype WINAPI REF_##name argtype { \
		HCounter _; \
		if (!InterlockedCompareExchange(&g_bHookEnabled, FALSE, FALSE)) \
			return ORIG_##name arglist; \
		return IMPL_##name arglist; \
	}

#include "hooklist.h"
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

//
#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	ORIG_##name = name;
#pragma optimize("s", on)
static void hook_initinternal()
{
#include "hooklist.h"
}
#pragma optimize("", on)
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

#define FORCE(expr) {if(!SUCCEEDED(NtStatus = (expr))) goto ERROR_ABORT;}

#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	if (&ORIG_##name) { \
	FORCE(LhInstallHook((PVOID&)ORIG_##name, REF_##name, (PVOID)0, &HOOK_##name)); \
	*(void**)&ORIG_##name =  (void*)HOOK_##name.Link->OldProc; \
	FORCE(LhSetExclusiveACL(ACLEntries, 0, &HOOK_##name)); }
#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;

static LONG hook_init()
{
	ULONG ACLEntries[1] = {0};
	NTSTATUS NtStatus;

#include "hooklist.h"
#undef HOOK_DEFINE

	FORCE(LhSetGlobalExclusiveACL(ACLEntries, 0));
	return NOERROR;

ERROR_ABORT:
	TRACE(_T("hook_init error: %#x\n"), NtStatus);
	return 1;
}
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

#define HOOK_DEFINE(rettype, name, argtype, arglist);
#define HOOK_MANUALLY(rettype, name, argtype, arglist) \
	LONG hook_demand_##name(bool bForce = false){ \
	NTSTATUS NtStatus; \
	ULONG ACLEntries[1] = { 0 }; \
	if (bForce) {  \
		memset((void*)&HOOK_##name, 0, sizeof(HOOK_TRACE_INFO));  \
	}  \
	if (&ORIG_##name) {	\
	FORCE(LhInstallHook((PVOID&)ORIG_##name, REF_##name, (PVOID)0, &HOOK_##name)); \
	*(void**)&ORIG_##name =  (void*)HOOK_##name.Link->OldProc; \
	FORCE(LhSetExclusiveACL(ACLEntries, 0, &HOOK_##name)); } \
	return NOERROR; \
	ERROR_ABORT: \
	TRACE(_T("hook_init error: %#x\n"), NtStatus); \
	return 1; \
	}

#include "hooklist.h"
#undef HOOK_MANUALLY


#undef HOOK_MANUALLY
#undef HOOK_DEFINE

#define HOOK_MANUALLY(rettype, name, argtype, arglist) ;
#define HOOK_DEFINE(rettype, name, argtype, arglist) \
	ORIG_##name = name;
#pragma optimize("s", on)
static LONG hook_term()
{
	#include "hooklist.h"
	LhUninstallAllHooks();
	return LhWaitForPendingRemovals();
}
#endif
#pragma optimize("", on)
#undef HOOK_DEFINE
#undef HOOK_MANUALLY

//---

CTlsData<CThreadLocalInfo>	g_TLInfo;
HINSTANCE					g_hinstDLL;
LONG						g_bHookEnabled;
volatile LONG				g_bootstrapHooksInstalled;
volatile LONG				g_lifecycleState;
#ifdef _DEBUG
HANDLE						g_hfDbgText;
#endif

//void InstallManagerHook();
//void RemoveManagerHook();

//#include "APITracer.hpp"

//ベースアドレスを変えた方がロードが早くなる
#if _DLL
#pragma comment(linker, "/base:0x06540000")
#endif

typedef BOOL(WINAPI *TIsImmersiveProcess)(_In_ HANDLE hProcess);

TIsImmersiveProcess IsUWP = (TIsImmersiveProcess)GetProcAddress(GetModuleHandle(L"user32.dll"), "IsImmersiveProcess");

BOOL WINAPI IsRunAsUser(VOID)
{
	if (IsUWP && IsUWP(GetCurrentProcess())) return true;	// treat all UWP apps as user exe
	HANDLE hProcessToken = NULL;
	DWORD groupLength = 50;

	PTOKEN_GROUPS groupInfo = (PTOKEN_GROUPS)LocalAlloc(0,
		groupLength);

	SID_IDENTIFIER_AUTHORITY siaNt = SECURITY_NT_AUTHORITY;
	PSID InteractiveSid = NULL;
	PSID ServiceSid = NULL;
	DWORD i;

	// Start with assumption that process is an SERVICE, not a EXE;
	BOOL fExe = FALSE;


	if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY,
		&hProcessToken))
		goto ret;

	if (groupInfo == NULL)
		goto ret;

	if (!GetTokenInformation(hProcessToken, TokenGroups, groupInfo,
		groupLength, &groupLength))
	{
		if (GetLastError() != ERROR_INSUFFICIENT_BUFFER)
			goto ret;

		LocalFree(groupInfo);
		groupInfo = NULL;

		groupInfo = (PTOKEN_GROUPS)LocalAlloc(0, groupLength);

		if (groupInfo == NULL)
			goto ret;

		if (!GetTokenInformation(hProcessToken, TokenGroups, groupInfo,
			groupLength, &groupLength))
		{
			goto ret;
		}
	}

	//
	//  We now know the groups associated with this token.  We want to look to	see if
		//  the interactive group is active in the token, and if so, we know that
		//  this is an interactive process.
		//
		//  We also look for the "service" SID, and if it's present, we know we're a service.
		//
		//  The service SID will be present iff the service is running in a
		//  user account (and was invoked by the service controller).
		//


	if (!AllocateAndInitializeSid(&siaNt, 1, SECURITY_INTERACTIVE_RID, 0,
		0,
		0, 0, 0, 0, 0, &InteractiveSid))
	{
		goto ret;
	}

	if (!AllocateAndInitializeSid(&siaNt, 1, SECURITY_SERVICE_RID, 0, 0, 0,
		0, 0, 0, 0, &ServiceSid))
	{
		goto ret;
	}

	for (i = 0; i < groupInfo->GroupCount; i += 1)
	{
		SID_AND_ATTRIBUTES sanda = groupInfo->Groups[i];
		PSID Sid = sanda.Sid;

		//
		//  Check to see if the group we're looking at is one of
		//  the 2 groups we're interested in.
		//

		if (EqualSid(Sid, InteractiveSid))
		{
			//
			//  This process has the Interactive SID in its
			//  token.  This means that the process is running as
			//  an EXE.
			//
			fExe = true;
			goto ret;
		}
		else if (EqualSid(Sid, ServiceSid))
		{
			//
			//  This process has the Service SID in its
			//  token.  This means that the process is running as
			//  a service running in a user account.
			//
			fExe = FALSE;
			goto ret;
		}
	}

	//
	//  Neither Interactive or Service was present in the current users token,
	//  This implies that the process is running as a service, most likely
	//  running as LocalSystem.
	//
	fExe = FALSE;

ret:

	if (InteractiveSid)
		FreeSid(InteractiveSid);

	if (ServiceSid)
		FreeSid(ServiceSid);

	if (groupInfo)
		LocalFree(groupInfo);

	if (hProcessToken)
		CloseHandle(hProcessToken);

// 	EventLogging logger;
// 	TCHAR s[100] = { 0 };
// 	wsprintf(s, L"Loading processid %d, isUserProcess=%d", GetCurrentProcessId(), (int)fExe);
// 	LPCTSTR lpStrings[] = {s}; 
// 	logger.LogIt(1, 1, lpStrings, 1);
	return(fExe);
}

BOOL AddEasyHookEnv()
{
	TCHAR directory[MAX_PATH] = {};
	const DWORD moduleLength = GetModuleFileName(
		GetDLLInstance(), directory, _countof(directory));
	if (!moduleLength || moduleLength >= _countof(directory))
		return FALSE;
	if (!PathRemoveFileSpec(directory))
		return FALSE;

	const DWORD pathLength =
		GetEnvironmentVariable(_T("PATH"), nullptr, 0);
	const size_t capacity =
		static_cast<size_t>(pathLength) +
		_tcslen(directory) + 2;
	LPTSTR path = static_cast<LPTSTR>(
		calloc(capacity, sizeof(TCHAR)));
	if (!path)
		return FALSE;
	if (pathLength)
		GetEnvironmentVariable(
			_T("PATH"), path, pathLength);

	BOOL result = TRUE;
	if (!_tcsstr(path, directory)) {
		if (*path && path[_tcslen(path) - 1] != _T(';'))
			StringCchCat(path, capacity, _T(";"));
		StringCchCat(path, capacity, directory);
		result = SetEnvironmentVariable(_T("PATH"), path);
	}
	free(path);
	return result;
}

void HookFontCreation() {
	HMODULE gdi32 = GetModuleHandle(L"gdi32full.dll");	// prefer to hook deeply
	if (!gdi32) {
		gdi32 = GetModuleHandle(L"gdi32.dll");
	}
	if (gdi32) {
		void* CreateFontIndirectW = GetProcAddress(gdi32, "CreateFontIndirectWImpl");
		void* CreateFontIndirectExW = GetProcAddress(gdi32, "CreateFontIndirectExW");
		if (!CreateFontIndirectW) {
			CreateFontIndirectW = GetProcAddress(gdi32, "CreateFontIndirectW");
		}
		*(DWORD_PTR*)&ORIG_CreateFontIndirectW = (DWORD_PTR)CreateFontIndirectW;
		*(DWORD_PTR*)&ORIG_CreateFontIndirectExW = (DWORD_PTR)CreateFontIndirectExW;

		hook_demand_CreateFontIndirectExW();
		hook_demand_CreateFontIndirectW();
	}
}

extern FT_Int * g_charmapCache;
extern BYTE* AACache, *AACacheFull;	
extern HFONT g_alterGUIFont;
extern void DebugOut(const WCHAR* szFormat, ...);


void EZHookMain(HINSTANCE instance, DWORD reason, LPVOID lpReserved) {
#ifdef STATIC_LIB
	switch (reason) {
	case DLL_PROCESS_ATTACH:
	case DLL_THREAD_ATTACH:
	case DLL_THREAD_DETACH:
		EasyHookDllMain(instance, reason, lpReserved);
	}
#else
	switch (reason) {
	case DLL_PROCESS_ATTACH:
	{
		LPWSTR dllPath = new WCHAR[MAX_PATH + 1];
		int nSize = GetModuleFileName(g_dllInstance, dllPath, MAX_PATH + 1);
		WCHAR* p = &dllPath[nSize];
		while (*--p != L'\\');
		*p = L'\0';
#ifdef _WIN64
		wcscat(dllPath, L"\\easyhk64.dll");
#else
		wcscat(dllPath, L"\\easyhk32.dll");
#endif
		HMODULE hEasyhk = LoadLibrary(dllPath);
		delete[]dllPath;
		if (!hEasyhk) {
			DebugOut(L"Failed to load Easyhook, exiting");
			return;
		}
	}
	}
#endif
}

extern COLORCACHE* g_AACache2[MAX_CACHE_SIZE]; 
HANDLE hDelayHook = 0;
static BOOL DispatchLifecycle(HINSTANCE instance, DWORD reason, LPVOID lpReserved)
{
	try {
		static bool bDllInited = false;
		BOOL IsUnload = false, bEnableDW = true, bUseFontSubstitute = false;


		switch (reason) {
		case DLL_PROCESS_ATTACH:
#ifdef DEBUG
			//MessageBox(0, L"Load", NULL, MB_OK);
#endif
			DebugOut(L"Begin core loading stage, pid %d", ::GetCurrentProcessId());
			if (bDllInited)
				return true;
			g_dllInstance = instance;
#ifdef EASYHOOK
			EZHookMain(instance, reason, lpReserved);
#endif
			//初期化順序
			//DLL_PROCESS_DETACHではこれの逆順にする
			//1. CRT関数の初期化
			//2. クリティカルセクションの初期化
			//3. TLSの準備
			//4. CGdippSettingsのインスタンス生成、INI読み込み
			//5. ExcludeModuleチェック
			// 6. FreeTypeライブラリの初期化
			// 7. FreeTypeFontEngineのインスタンス生成
			// 8. APIをフック
			// 9. ManagerのGetProcAddressをフック

			//1
			_CrtSetDbgFlag(_CrtSetDbgFlag(_CRTDBG_REPORT_FLAG) | _CRTDBG_LEAK_CHECK_DF);
			_CrtSetReportMode(_CRT_ASSERT, _CRTDBG_MODE_DEBUG | _CRTDBG_MODE_WNDW);
			//_CrtSetBreakAlloc(100);

			//Operaよ止まれ～
			//Assert(GetModuleHandleA("opera.exe") == NULL);

			//setlocale(LC_ALL, "");
			g_hinstDLL = instance;


			//APITracer::Start(instance, APITracer::OutputFile);

					//2, 3
			CCriticalSectionLock::Init();
			COwnedCriticalSectionLock::Init();
			CThreadCounter::Init();
			if (!g_TLInfo.ProcessInit()) {
				DebugOut(L"Can't initialize process, exiting");
				return FALSE;
			}

			// Above classes are heavily referenced and must be initialized as early as possible.
			// Unload dll is not safe until their initialization is complete.
			bDllInited = true;

			//4
			{
#ifdef INFINALITY 
				// enable infinality exclusive features
				FT_initEnv();
#endif
				CGdippSettings* pSettings = CGdippSettings::CreateInstance();
				if (!pSettings || !pSettings->LoadSettings(instance)) {
					CGdippSettings::DestroyInstance();
					return FALSE;
				}
				IsUnload = IsProcessUnload();
				bEnableDW = pSettings->DirectWrite();
				bUseFontSubstitute = !!pSettings->FontSubstitutes();
			}
			{
				const RenderBackendSnapshot backends = DetectLoadedRenderBackends();
				DebugOut(L"Detected render backends: 0x%08x",
					static_cast<unsigned int>(backends.loaded));
			}
			if (!IsUnload &&
				!InterlockedCompareExchange(
					&g_bootstrapHooksInstalled, FALSE, FALSE))
				hook_initinternal();	//不加载的模块就不做任何事莵E
			//5
			if (!IsProcessExcluded() && !IsUnload) {
#ifndef _WIN64
				InitWow64ext();
#endif
				if (!FontLInit()) {
					DebugOut(L"FreeType failed to initialize, exiting");
					return FALSE;
				}
				g_pFTEngine = new FreeTypeFontEngine;
				if (!g_pFTEngine) {
					return FALSE;
				}

				//if (!AddEasyHookEnv()) return FALSE;	//fail to load easyhook
				InterlockedExchange(&g_bHookEnabled, TRUE);
				if (!InterlockedCompareExchange(
					&g_bootstrapHooksInstalled, FALSE, FALSE)) {
					AutoEnableDynamicCodeGen dynamicCodeForHookThread;
					if (hook_init() != NOERROR) {
						DebugOut(L"Can't do hooking, exiting");
						return FALSE;
					}
					InterlockedExchange(&g_bootstrapHooksInstalled, TRUE);
				}
				//hook d2d if already loaded
	/*
				DWORD dwSessionID = 0;
				if (ProcessIdToSessionIdProc)
					ProcessIdToSessionIdProc(GetCurrentThreadId(), &dwSessionID);
				else
					dwSessionID = 1;*/
				if (IsRunAsUser() && bEnableDW && IsWindowsVistaOrGreater())	//vista or later
				{
					HookD2DDll();
					//hook_demand_LdrLoadDll();
				}
				// only hook font creation funcs if font substition is set.
				if (bUseFontSubstitute) {
					HookFontCreation();
				}
			}
			//获得当前加载模式

			if (IsUnload)
			{
				HANDLE mutex_offical = OpenMutex(MUTEX_ALL_ACCESS, false, _T("{46AD3688-30D0-411e-B2AA-CB177818F428}"));
				HANDLE mutex_gditray2 = OpenMutex(MUTEX_ALL_ACCESS, false, _T("Global\\MacType"));
				if (!mutex_gditray2)
					mutex_gditray2 = OpenMutex(MUTEX_ALL_ACCESS, false, _T("MacType"));
				HANDLE mutex_CompMode = OpenMutex(MUTEX_ALL_ACCESS, false, _T("Global\\MacTypeCompMode"));
				if (!mutex_CompMode)
					mutex_CompMode = OpenMutex(MUTEX_ALL_ACCESS, false, _T("MacTypeCompMode"));
				BOOL HookMode = (mutex_offical || (mutex_gditray2 && mutex_CompMode)) || (!mutex_offical && !mutex_gditray2);	//是否在兼容模式下
				CloseHandle(mutex_CompMode);
				CloseHandle(mutex_gditray2);
				CloseHandle(mutex_offical);
				if (!HookMode) {	//非兼容模式下，拒绝加载
					DebugOut(L"Process is in unloaddll list, exiting");
					return false;
				}
			}

			//APITracer::Finish();
			break;
		case DLL_THREAD_ATTACH:
#ifdef EASYHOOK
			EZHookMain(instance, reason, lpReserved);
#endif
			break;
		case DLL_THREAD_DETACH:
			g_TLInfo.ThreadTerm();
#ifdef EASYHOOK
			EZHookMain(instance, reason, lpReserved);
#endif
			break;
		case DLL_PROCESS_DETACH:
			//		RemoveManagerHook();
			if (!bDllInited)
				return true;
			bDllInited = false;
			InterlockedExchange(&g_bHookEnabled, FALSE);
			if (lpReserved == NULL &&
				InterlockedExchange(&g_bootstrapHooksInstalled, FALSE)) {
				hook_term();
				//delete AACacheFull;
				//delete AACache;
	// 			for (int i=0;i<CACHE_SIZE;i++)
	// 				delete g_AACache2[i];	//清除缓磥E
				//free(g_charmapCache);
			}
#ifndef DEBUG
			if (lpReserved != NULL) return true;
#endif

			if (g_pFTEngine) {
				delete g_pFTEngine;
			}

#ifdef INFINALITY 
			// enable infinality exclusive features
			FT_freeEnv();
#endif
			//if (g_alterGUIFont)
			//	DeleteObject(g_alterGUIFont);
			FontLFree();
			/*
			#ifndef _WIN64
					__FUnloadDelayLoadedDLL2("easyhook32.dll");
			#else
					__FUnloadDelayLoadedDLL2("easyhook64.dll");
			#endif*/

			CGdippSettings::DestroyInstance();
			g_TLInfo.ProcessTerm();
			CCriticalSectionLock::Term();
			COwnedCriticalSectionLock::Term();
#ifdef EASYHOOK
			EZHookMain(instance, reason, lpReserved);
#endif
			break;
		}
		return TRUE;
	}
	catch(...) {
		return FALSE;
	}
}

namespace
{
enum LifecycleState : LONG
{
	LifecycleDormant = MacTypeLifecycleDormant,
	LifecycleScheduled = MacTypeLifecycleScheduled,
	LifecycleInitializing = MacTypeLifecycleInitializing,
	LifecycleReady = MacTypeLifecycleReady,
	LifecycleFailed = MacTypeLifecycleFailed,
	LifecycleShuttingDown = MacTypeLifecycleShuttingDown,
};

DWORD WINAPI DeferredInitializeThread(LPVOID parameter)
{
	const BOOL initialized = MacTypeInitialize();
	FreeLibraryAndExitThread(
		static_cast<HMODULE>(parameter), initialized ? ERROR_SUCCESS : ERROR_DLL_INIT_FAILED);
}

#ifdef USE_DETOURS
BOOL StartDeferredBootstrap()
{
	// A loader that explicitly calls MacTypeInitialize can suppress the
	// compatibility worker. This is the contract used by the next-generation
	// suspended-process loader.
	WCHAR explicitBootstrap[2] = {};
	if (GetEnvironmentVariableW(
		L"MACTYPE_EXPLICIT_BOOTSTRAP", explicitBootstrap, _countof(explicitBootstrap)))
		return TRUE;

	HMODULE pinnedModule = nullptr;
	if (!GetModuleHandleExW(
		GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS,
		reinterpret_cast<LPCWSTR>(&DeferredInitializeThread),
		&pinnedModule))
		return FALSE;

	InterlockedExchange(&g_lifecycleState, LifecycleScheduled);
	MacTypeSetLastDiagnostic(
		MacTypeDiagnosticCode::LifecycleScheduled, ERROR_SUCCESS);
	MacTypeLog(
		MacTypeLogLevel::Info,
		MacTypeDiagnosticCode::LifecycleScheduled,
		L"deferred initialization scheduled module=%p", pinnedModule);
	HANDLE thread = CreateThread(
		nullptr, 0, DeferredInitializeThread, pinnedModule, 0, nullptr);
	if (!thread) {
		InterlockedExchange(&g_lifecycleState, LifecycleFailed);
		FreeLibrary(pinnedModule);
		return FALSE;
	}
	CloseHandle(thread);
	return TRUE;
}
#endif
}

EXTERN_C BOOL WINAPI MacTypeInitialize()
{
	if (!g_dllInstance)
		return FALSE;

	LONG previous = InterlockedCompareExchange(
		&g_lifecycleState, LifecycleInitializing, LifecycleScheduled);
	if (previous == LifecycleDormant) {
		previous = InterlockedCompareExchange(
			&g_lifecycleState, LifecycleInitializing, LifecycleDormant);
	}
	if (previous == LifecycleReady)
		return TRUE;
	if (previous == LifecycleInitializing) {
		const ULONGLONG deadline = GetTickCount64() + 30000;
		do {
			Sleep(1);
			const LONG state = InterlockedCompareExchange(
				&g_lifecycleState,
				LifecycleDormant,
				LifecycleDormant);
			if (state == LifecycleReady)
				return TRUE;
			if (state == LifecycleFailed ||
				state == LifecycleDormant)
				return FALSE;
		} while (GetTickCount64() < deadline);
		SetLastError(ERROR_TIMEOUT);
		return FALSE;
	}
	if (previous != LifecycleScheduled && previous != LifecycleDormant)
		return FALSE;

	MacTypeSetLastDiagnostic(
		MacTypeDiagnosticCode::LifecycleInitializeBegin, ERROR_SUCCESS);
	MacTypeLog(
		MacTypeLogLevel::Info,
		MacTypeDiagnosticCode::LifecycleInitializeBegin,
		L"initialization started");

	// The explicit-bootstrap marker is inherited by the launched process only
	// to keep this DLL dormant until the launcher calls us. Do not let it make
	// renderer and utility descendants dormant as well.
	SetEnvironmentVariableW(L"MACTYPE_EXPLICIT_BOOTSTRAP", nullptr);

	const BOOL initialized =
		DispatchLifecycle(g_dllInstance, DLL_PROCESS_ATTACH, nullptr);
	if (!initialized)
		DispatchLifecycle(g_dllInstance, DLL_PROCESS_DETACH, nullptr);
	InterlockedExchange(
		&g_lifecycleState, initialized ? LifecycleReady : LifecycleFailed);
	if (initialized) {
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::LifecycleReady, ERROR_SUCCESS);
		MacTypeLog(
			MacTypeLogLevel::Info,
			MacTypeDiagnosticCode::LifecycleReady,
			L"initialization completed hooks=%ld",
			InterlockedCompareExchange(
				&g_bootstrapHooksInstalled, FALSE, FALSE));
	}
	else {
		const DWORD error = GetLastError()
			? GetLastError()
			: ERROR_DLL_INIT_FAILED;
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::LifecycleInitializeFailed, error);
		MacTypeLog(
			MacTypeLogLevel::Error,
			MacTypeDiagnosticCode::LifecycleInitializeFailed,
			L"initialization failed");
	}
	return initialized;
}

EXTERN_C BOOL WINAPI MacTypeShutdown()
{
	if (!g_dllInstance)
		return FALSE;

	LONG previous = LifecycleDormant;
	for (;;) {
		previous = InterlockedCompareExchange(
			&g_lifecycleState,
			LifecycleDormant,
			LifecycleDormant);
		if (previous == LifecycleDormant ||
			previous == LifecycleShuttingDown)
			return TRUE;
		if (previous == LifecycleInitializing ||
			previous == LifecycleScheduled) {
			SetLastError(ERROR_BUSY);
			return FALSE;
		}
		if (previous == LifecycleFailed) {
			if (InterlockedCompareExchange(
				&g_lifecycleState,
				LifecycleDormant,
				LifecycleFailed) == LifecycleFailed)
				return TRUE;
			continue;
		}
		if (previous == LifecycleReady &&
			InterlockedCompareExchange(
				&g_lifecycleState,
				LifecycleShuttingDown,
				LifecycleReady) == LifecycleReady)
			break;
	}

	BOOL result = TRUE;
	result = DispatchLifecycle(
		g_dllInstance, DLL_PROCESS_DETACH, nullptr);

	InterlockedExchange(&g_bHookEnabled, FALSE);
	if (InterlockedExchange(&g_bootstrapHooksInstalled, FALSE))
		hook_term();
	InterlockedExchange(&g_lifecycleState, LifecycleDormant);
	MacTypeSetLastDiagnostic(
		MacTypeDiagnosticCode::LifecycleShutdown, ERROR_SUCCESS);
	MacTypeLog(
		MacTypeLogLevel::Info,
		MacTypeDiagnosticCode::LifecycleShutdown,
		L"shutdown completed result=%d", result);
	return result;
}

EXTERN_C BOOL WINAPI MacTypeGetStatus(MacTypeStatus* status)
{
	const DWORD baseSize =
		static_cast<DWORD>(offsetof(
			MacTypeStatus, lastDiagnosticCode));
	if (!status || status->size < baseSize)
		return FALSE;

	const DWORD callerSize = status->size;
	const RenderBackendSnapshot backends = DetectLoadedRenderBackends();
	status->version = 2;
	status->lifecycle = static_cast<MacTypeLifecycleState>(
		InterlockedCompareExchange(
			&g_lifecycleState, LifecycleDormant, LifecycleDormant));
	status->loadedRenderBackends = static_cast<DWORD>(backends.loaded);
	status->hooksEnabled = InterlockedCompareExchange(
		&g_bHookEnabled, FALSE, FALSE) != FALSE;
	status->hooksInstalled = InterlockedCompareExchange(
		&g_bootstrapHooksInstalled, FALSE, FALSE) != FALSE;
	if (callerSize >= sizeof(MacTypeStatus)) {
		status->lastDiagnosticCode =
			static_cast<DWORD>(MacTypeGetLastDiagnosticCode());
		status->lastError = MacTypeGetLastDiagnosticError();
		DWORD value = status->loadedRenderBackends;
		DWORD count = 0;
		while (value) {
			count += value & 1;
			value >>= 1;
		}
		status->hookedBackendCount =
			status->hooksInstalled ? count : 0;
		status->observedComMethodCount =
			GetObservedComMethodImplementationCount();
	}
	return TRUE;
}

EXTERN_C DWORD WINAPI MacTypeInitializeThread(LPVOID)
{
	return MacTypeInitialize() ? ERROR_SUCCESS : ERROR_DLL_INIT_FAILED;
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID lpReserved)
{
	if (reason == DLL_PROCESS_ATTACH) {
		g_dllInstance = instance;
		g_hinstDLL = instance;
		InterlockedExchange(&g_bHookEnabled, FALSE);
		InterlockedExchange(&g_bootstrapHooksInstalled, FALSE);
		InterlockedExchange(&g_lifecycleState, LifecycleDormant);
#ifdef USE_DETOURS
		// DllMain only schedules initialization. Configuration, FreeType,
		// Detours transactions and rendering setup all run after loader lock.
		if (!StartDeferredBootstrap()) {
			if (InterlockedExchange(&g_bootstrapHooksInstalled, FALSE))
				hook_term();
			return TRUE; // Degrade safely without breaking the host process.
		}
		return TRUE;
#else
		return MacTypeInitialize();
#endif
	}

	if (reason == DLL_THREAD_DETACH &&
		InterlockedCompareExchange(
			&g_lifecycleState, LifecycleDormant, LifecycleDormant) == LifecycleReady)
		return DispatchLifecycle(instance, reason, lpReserved);

	if (reason == DLL_PROCESS_DETACH) {
		InterlockedExchange(&g_bHookEnabled, FALSE);
		if (lpReserved != nullptr)
			return TRUE; // The process owns all remaining resources.
		return MacTypeShutdown();
	}

	return TRUE;
}
//EOF
