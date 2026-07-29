// dll injection
#define _CRT_SECURE_NO_DEPRECATE 1
#define WINVER 0x0A00
#define _WIN32_WINNT 0x0A00
#define _WIN32_IE 0x601
#define WIN32_LEAN_AND_MEAN 1
#define UNICODE  1
#define _UNICODE 1
#include <Windows.h>
#include <ShellApi.h>
#include <ComDef.h>
#include <ShlObj.h>
#include <ShLwApi.h>
#include <TlHelp32.h>
#include <tchar.h>
#include "array.h"
#include <strsafe.h>

// _vsnwprintf用
#include <wchar.h>		
#include <stdarg.h>

#define _CRTDBG_MAP_ALLOC
#include <cstdlib>
#include <malloc.h>
#include <new>
#include <crtdbg.h>
#include "diagnostics.h"

#define for if(0);else for
#ifndef _countof
#define _countof(array)		(sizeof(array) / sizeof((array)[0]))
#endif

#pragma comment(linker, "/subsystem:windows")
#pragma comment(lib, "Kernel32.lib")
#pragma comment(lib, "User32.lib")
#pragma comment(lib, "Shell32.lib")
#pragma comment(lib, "ShLwApi.lib")
#pragma comment(lib, "Ole32.lib")

#define IDS_USAGE		101
#define IDS_DLL			102
#define IDC_EXEC		103

static void showmsg(LPCSTR msg) {
	MessageBoxA(NULL, msg, "MacType ERROR", MB_OK | MB_ICONSTOP);
}

static void errmsg(UINT id, DWORD code)
{
	char  buffer[512];
	char  format[128];
	LoadStringA(GetModuleHandleA(NULL), id, format, 128);
	wnsprintfA(buffer, 512, format, code);
	showmsg(buffer);
}

inline HRESULT HresultFromLastError()
{
	DWORD dwErr = GetLastError();
	return HRESULT_FROM_WIN32(dwErr);
}


#ifdef _M_IX86
const auto MacTypeDll = L"MacType.Core.dll";
#else
const auto MacTypeDll = L"MacType64.Core.dll";
#endif


HINSTANCE hinstDLL;
static DWORD g_loaderFailureStage;

#include <stddef.h>
#define GetDLLInstance()	(hinstDLL)

#define _GDIPP_RUN_CPP
//#include "supinfo.h"

static bool CheckTargetMitigations(HANDLE process)
{
	PROCESS_MITIGATION_DYNAMIC_CODE_POLICY dynamicCode = {};
	if (GetProcessMitigationPolicy(
		process,
		ProcessDynamicCodePolicy,
		&dynamicCode,
		sizeof(dynamicCode)) &&
		dynamicCode.ProhibitDynamicCode &&
		!dynamicCode.AllowThreadOptOut) {
		g_loaderFailureStage = 14;
		SetLastError(ERROR_DYNAMIC_CODE_BLOCKED);
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::MitigationBlocked,
			ERROR_DYNAMIC_CODE_BLOCKED);
		MacTypeLog(
			MacTypeLogLevel::Warning,
			MacTypeDiagnosticCode::MitigationBlocked,
			L"target has strict dynamic-code policy");
		return false;
	}

	PROCESS_MITIGATION_BINARY_SIGNATURE_POLICY signature = {};
	if (GetProcessMitigationPolicy(
		process,
		ProcessSignaturePolicy,
		&signature,
		sizeof(signature)) &&
		(signature.MicrosoftSignedOnly ||
			signature.StoreSignedOnly ||
			signature.MitigationOptIn)) {
		g_loaderFailureStage = 15;
		SetLastError(ERROR_INVALID_IMAGE_HASH);
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::MitigationBlocked,
			ERROR_INVALID_IMAGE_HASH);
		MacTypeLog(
			MacTypeLogLevel::Warning,
			MacTypeDiagnosticCode::MitigationBlocked,
			L"target restricts loaded image signatures");
		return false;
	}
	return true;
}

static BYTE* FindRemoteModuleBase(DWORD processId, const wchar_t* moduleName)
{
	HANDLE snapshot = CreateToolhelp32Snapshot(
		TH32CS_SNAPMODULE | TH32CS_SNAPMODULE32, processId);
	if (snapshot == INVALID_HANDLE_VALUE)
		return NULL;

	MODULEENTRY32W entry = {};
	entry.dwSize = sizeof(entry);
	BYTE* moduleBase = NULL;
	if (Module32FirstW(snapshot, &entry)) {
		do {
			if (_wcsicmp(entry.szModule, moduleName) == 0) {
				moduleBase = entry.modBaseAddr;
				break;
			}
		} while (Module32NextW(snapshot, &entry));
	}
	CloseHandle(snapshot);
	return moduleBase;
}

static LPTHREAD_START_ROUTINE FindRemoteProcedure(
	DWORD processId,
	FARPROC localProcedure)
{
	if (!localProcedure)
		return NULL;

	HMODULE localOwner = NULL;
	if (!GetModuleHandleExW(
		GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
		GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
		reinterpret_cast<LPCWSTR>(localProcedure),
		&localOwner))
		return NULL;

	WCHAR ownerPath[MAX_PATH] = {};
	if (!GetModuleFileNameW(localOwner, ownerPath, _countof(ownerPath)))
		return NULL;
	const wchar_t* ownerName = PathFindFileNameW(ownerPath);
	BYTE* remoteOwner = FindRemoteModuleBase(processId, ownerName);
	if (!remoteOwner)
		return NULL;

	const DWORD_PTR procedureRva =
		reinterpret_cast<DWORD_PTR>(localProcedure) -
		reinterpret_cast<DWORD_PTR>(localOwner);
	return reinterpret_cast<LPTHREAD_START_ROUTINE>(
		remoteOwner + procedureRva);
}

static LPTHREAD_START_ROUTINE FindRemoteInitializer(DWORD processId)
{
	return FindRemoteProcedure(
		processId,
		GetProcAddress(hinstDLL, "MacTypeInitializeThread"));
}

static bool InjectDormantDll(
	const PROCESS_INFORMATION& processInfo,
	const wchar_t* dllPath)
{
	if (!CheckTargetMitigations(processInfo.hProcess))
		return false;

	const SIZE_T pathBytes = (wcslen(dllPath) + 1) * sizeof(wchar_t);
	void* remotePath = VirtualAllocEx(
		processInfo.hProcess,
		NULL,
		pathBytes,
		MEM_COMMIT | MEM_RESERVE,
		PAGE_READWRITE);
	if (!remotePath)
	{
		g_loaderFailureStage = 1;
		return false;
	}

	SIZE_T bytesWritten = 0;
	bool injected = false;
	bool remoteCallFinished = false;
	if (WriteProcessMemory(
		processInfo.hProcess,
		remotePath,
		dllPath,
		pathBytes,
		&bytesWritten) &&
		bytesWritten == pathBytes) {
		HMODULE localKernel32 = GetModuleHandleW(L"kernel32.dll");
		FARPROC localLoadLibrary = localKernel32
			? GetProcAddress(localKernel32, "LoadLibraryW")
			: NULL;
		LPTHREAD_START_ROUTINE remoteLoadLibrary =
			FindRemoteProcedure(
				processInfo.dwProcessId, localLoadLibrary);
		if (remoteLoadLibrary) {
			HANDLE thread = CreateRemoteThread(
				processInfo.hProcess,
				NULL,
				0,
				remoteLoadLibrary,
				remotePath,
				0,
				NULL);
			if (thread) {
				remoteCallFinished =
					WaitForSingleObject(thread, 30000) == WAIT_OBJECT_0;
				injected = remoteCallFinished;
				if (!injected)
					g_loaderFailureStage = 5;
				CloseHandle(thread);
			}
			else {
				g_loaderFailureStage = 4;
			}
		}
		else {
			g_loaderFailureStage = 3;
		}
	}
	else {
		g_loaderFailureStage = 2;
	}

	// A timed-out remote thread may still be reading this path. In that rare
	// failure case the allocation is deliberately left to process teardown.
	if (remoteCallFinished)
		VirtualFreeEx(processInfo.hProcess, remotePath, 0, MEM_RELEASE);
	const bool moduleLoaded =
		FindRemoteModuleBase(processInfo.dwProcessId, MacTypeDll) != NULL;
	if (injected && !moduleLoaded)
		g_loaderFailureStage = 6;
	return injected && moduleLoaded;
}

static bool InitializeInjectedProcess(const PROCESS_INFORMATION& processInfo)
{
	LPTHREAD_START_ROUTINE initializer =
		FindRemoteInitializer(processInfo.dwProcessId);
	if (!initializer)
	{
		g_loaderFailureStage = 7;
		return false;
	}

	HANDLE thread = CreateRemoteThread(
		processInfo.hProcess, NULL, 0, initializer, NULL, 0, NULL);
	if (!thread)
	{
		g_loaderFailureStage = 8;
		return false;
	}

	const DWORD waitResult = WaitForSingleObject(thread, 30000);
	DWORD exitCode = ERROR_DLL_INIT_FAILED;
	const bool initialized =
		waitResult == WAIT_OBJECT_0 &&
		GetExitCodeThread(thread, &exitCode) &&
		exitCode == ERROR_SUCCESS;
	if (!initialized)
		g_loaderFailureStage = 9;
	CloseHandle(thread);
	return initialized;
}

static bool RestoreEntryBreakpoint(
	const PROCESS_INFORMATION& processInfo,
	BYTE* entryPoint,
	BYTE originalByte)
{
	if (!entryPoint)
		return true;

	DWORD oldProtection = 0;
	if (!VirtualProtectEx(
		processInfo.hProcess,
		entryPoint,
		1,
		PAGE_EXECUTE_READWRITE,
		&oldProtection))
		return false;

	SIZE_T bytesWritten = 0;
	const bool restored =
		WriteProcessMemory(
			processInfo.hProcess,
			entryPoint,
			&originalByte,
			1,
			&bytesWritten) &&
		bytesWritten == 1;
	DWORD ignored = 0;
	VirtualProtectEx(
		processInfo.hProcess,
		entryPoint,
		1,
		oldProtection,
		&ignored);
	if (restored)
		FlushInstructionCache(processInfo.hProcess, entryPoint, 1);
	return restored;
}

static bool SuspendAtProcessEntry(
	const PROCESS_INFORMATION& processInfo)
{
	BYTE* entryPoint = NULL;
	BYTE originalByte = 0;
	bool breakpointArmed = false;
	const ULONGLONG deadline = GetTickCount64() + 30000;
	while (GetTickCount64() < deadline) {
		DEBUG_EVENT event = {};
		const DWORD remaining = static_cast<DWORD>(
			deadline - GetTickCount64());
		if (!WaitForDebugEvent(&event, remaining))
			break;

		DWORD continueStatus = DBG_CONTINUE;
		bool initialBreakpoint = false;
		switch (event.dwDebugEventCode) {
		case CREATE_PROCESS_DEBUG_EVENT:
			{
				BYTE* imageBase = static_cast<BYTE*>(
					event.u.CreateProcessInfo.lpBaseOfImage);
				IMAGE_DOS_HEADER dosHeader = {};
				IMAGE_NT_HEADERS ntHeaders = {};
				SIZE_T bytesRead = 0;
				if (ReadProcessMemory(
					processInfo.hProcess,
					imageBase,
					&dosHeader,
					sizeof(dosHeader),
					&bytesRead) &&
					dosHeader.e_magic == IMAGE_DOS_SIGNATURE &&
					ReadProcessMemory(
						processInfo.hProcess,
						imageBase + dosHeader.e_lfanew,
						&ntHeaders,
						sizeof(ntHeaders),
						&bytesRead) &&
					ntHeaders.Signature == IMAGE_NT_SIGNATURE) {
					entryPoint =
						imageBase +
						ntHeaders.OptionalHeader.AddressOfEntryPoint;
					DWORD oldProtection = 0;
					if (VirtualProtectEx(
						processInfo.hProcess,
						entryPoint,
						1,
						PAGE_EXECUTE_READWRITE,
						&oldProtection) &&
						ReadProcessMemory(
							processInfo.hProcess,
							entryPoint,
							&originalByte,
							1,
							&bytesRead)) {
						const BYTE breakpoint = 0xCC;
						SIZE_T bytesWritten = 0;
						breakpointArmed = WriteProcessMemory(
							processInfo.hProcess,
							entryPoint,
							&breakpoint,
							1,
							&bytesWritten) &&
							bytesWritten == 1;
						DWORD ignored = 0;
						VirtualProtectEx(
							processInfo.hProcess,
							entryPoint,
							1,
							oldProtection,
							&ignored);
						FlushInstructionCache(
							processInfo.hProcess, entryPoint, 1);
					}
				}
			}
			if (event.u.CreateProcessInfo.hFile)
				CloseHandle(event.u.CreateProcessInfo.hFile);
			break;
		case LOAD_DLL_DEBUG_EVENT:
			if (event.u.LoadDll.hFile)
				CloseHandle(event.u.LoadDll.hFile);
			break;
		case EXCEPTION_DEBUG_EVENT:
			continueStatus =
				event.u.Exception.ExceptionRecord.ExceptionCode ==
					EXCEPTION_BREAKPOINT
				? DBG_CONTINUE
				: DBG_EXCEPTION_NOT_HANDLED;
			initialBreakpoint =
				event.u.Exception.dwFirstChance &&
				event.u.Exception.ExceptionRecord.ExceptionCode ==
					EXCEPTION_BREAKPOINT &&
				breakpointArmed &&
				event.u.Exception.ExceptionRecord.ExceptionAddress ==
					entryPoint;
			break;
		case EXIT_PROCESS_DEBUG_EVENT:
			ContinueDebugEvent(
				event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
			g_loaderFailureStage = 10;
			return false;
		}

		if (initialBreakpoint) {
			const bool breakpointRestored =
				RestoreEntryBreakpoint(
					processInfo, entryPoint, originalByte);
			breakpointArmed = !breakpointRestored;

			CONTEXT context = {};
			context.ContextFlags = CONTEXT_CONTROL;
			bool contextRestored =
				breakpointRestored &&
				GetThreadContext(processInfo.hThread, &context) != FALSE;
			if (contextRestored) {
#ifdef _WIN64
				context.Rip =
					reinterpret_cast<DWORD64>(entryPoint);
#else
				context.Eip =
					reinterpret_cast<DWORD>(entryPoint);
#endif
				contextRestored =
					SetThreadContext(processInfo.hThread, &context) != FALSE;
			}
			const bool suspended =
				contextRestored &&
				SuspendThread(processInfo.hThread) != static_cast<DWORD>(-1);
			ContinueDebugEvent(
				event.dwProcessId, event.dwThreadId, DBG_CONTINUE);
			DebugActiveProcessStop(processInfo.dwProcessId);
			if (!suspended)
				g_loaderFailureStage = 10;
			return suspended;
		}

		ContinueDebugEvent(
			event.dwProcessId, event.dwThreadId, continueStatus);
	}

	const bool restored =
		!breakpointArmed ||
		RestoreEntryBreakpoint(
			processInfo, entryPoint, originalByte);
	DebugActiveProcessStop(processInfo.dwProcessId);
	if (!restored)
		TerminateProcess(
			processInfo.hProcess, ERROR_DLL_INIT_FAILED);
	g_loaderFailureStage = 10;
	return false;
}

static HRESULT AttachAndInitialize(
	DWORD processId,
	DWORD threadId,
	const wchar_t* dllPath)
{
	if (!processId || !threadId || !dllPath || !*dllPath)
		return E_INVALIDARG;

	PROCESS_INFORMATION processInfo = {};
	processInfo.dwProcessId = processId;
	processInfo.dwThreadId = threadId;
	processInfo.hProcess = OpenProcess(
		PROCESS_QUERY_INFORMATION |
		PROCESS_VM_OPERATION |
		PROCESS_VM_READ |
		PROCESS_VM_WRITE |
		PROCESS_CREATE_THREAD |
		SYNCHRONIZE,
		FALSE,
		processId);
	processInfo.hThread = OpenThread(
		THREAD_GET_CONTEXT |
		THREAD_SET_CONTEXT |
		THREAD_SUSPEND_RESUME |
		THREAD_QUERY_INFORMATION |
		SYNCHRONIZE,
		FALSE,
		threadId);
	if (!processInfo.hProcess || !processInfo.hThread) {
		if (processInfo.hThread)
			CloseHandle(processInfo.hThread);
		if (processInfo.hProcess)
			CloseHandle(processInfo.hProcess);
		g_loaderFailureStage = 11;
		return HRESULT_FROM_WIN32(GetLastError());
	}

	DebugSetProcessKillOnExit(FALSE);
	if (!DebugActiveProcess(processId)) {
		const DWORD error = GetLastError();
		CloseHandle(processInfo.hThread);
		CloseHandle(processInfo.hProcess);
		g_loaderFailureStage = 12;
		return HRESULT_FROM_WIN32(error);
	}

	DWORD previousSuspendCount =
		ResumeThread(processInfo.hThread);
	if (previousSuspendCount == static_cast<DWORD>(-1)) {
		const DWORD error = GetLastError();
		DebugActiveProcessStop(processId);
		CloseHandle(processInfo.hThread);
		CloseHandle(processInfo.hProcess);
		g_loaderFailureStage = 13;
		return HRESULT_FROM_WIN32(error);
	}
	// DebugActiveProcess can add a debugger suspension on top of the
	// CREATE_SUSPENDED count. Drain only the counts that existed at attach;
	// SuspendAtProcessEntry establishes a fresh, single suspension before
	// detaching the debugger.
	for (DWORD attempt = 0;
		previousSuspendCount > 1 && attempt != 8;
		++attempt) {
		previousSuspendCount =
			ResumeThread(processInfo.hThread);
		if (previousSuspendCount == static_cast<DWORD>(-1))
			break;
	}

	const bool initialized =
		SuspendAtProcessEntry(processInfo) &&
		InjectDormantDll(processInfo, dllPath) &&
		InitializeInjectedProcess(processInfo);
	CloseHandle(processInfo.hThread);
	CloseHandle(processInfo.hProcess);
	MacTypeLog(
		initialized ? MacTypeLogLevel::Info : MacTypeLogLevel::Error,
		initialized
			? MacTypeDiagnosticCode::ChildInjectionReady
			: MacTypeDiagnosticCode::ChildInjectionFailed,
		L"attach broker targetPid=%lu stage=%lu initialized=%d",
		processId, g_loaderFailureStage, initialized);
	return initialized
		? S_OK
		: HRESULT_FROM_WIN32(ERROR_DLL_INIT_FAILED);
}

//#define OLD_PSDK

#ifdef OLD_PSDK
extern "C" {
	HRESULT WINAPI _SHILCreateFromPath(LPCWSTR pszPath, LPITEMIDLIST* ppidl, DWORD* rgflnOut)
	{
		if (!pszPath || !ppidl) {
			return E_INVALIDARG;
		}

		LPSHELLFOLDER psf;
		HRESULT hr = ::SHGetDesktopFolder(&psf);
		if (hr != NOERROR) {
			return hr;
		}

		ULONG chEaten;
		LPOLESTR lpszDisplayName = ::StrDupW(pszPath);
		hr = psf->ParseDisplayName(NULL, NULL, lpszDisplayName, &chEaten, ppidl, rgflnOut);
		::LocalFree(lpszDisplayName);
		psf->Release();
		return hr;
	}

	void WINAPI _SHFree(void* pv)
	{
		if (!pv) {
			return;
		}

		LPMALLOC pMalloc = NULL;
		if (::SHGetMalloc(&pMalloc) == NOERROR) {
			pMalloc->Free(pv);
			pMalloc->Release();
		}
	}
}
#else
#define _SHILCreateFromPath	SHILCreateFromPath
#define _SHFree				SHFree
#endif


bool isX64PE(const TCHAR* file_path) {
	HANDLE hFile = CreateFile(file_path, GENERIC_READ, 0, NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
	if (hFile == INVALID_HANDLE_VALUE) {
		showmsg("Error opening file");
		return false;
	}

	IMAGE_DOS_HEADER dosHeader;
	DWORD bytesRead;
	if (!ReadFile(hFile, &dosHeader, sizeof(IMAGE_DOS_HEADER), &bytesRead, NULL)) {
		showmsg("Error reading file");
		CloseHandle(hFile);
		return false;
	}

	// Check if it's a PE file
	if (dosHeader.e_magic != IMAGE_DOS_SIGNATURE) {
		showmsg("Not a PE file");
		CloseHandle(hFile);
		return false;
	}

	IMAGE_NT_HEADERS ntHeaders;
	// Seek to the PE header offset
	SetFilePointer(hFile, dosHeader.e_lfanew, NULL, FILE_BEGIN);
	if (!ReadFile(hFile, &ntHeaders, sizeof(IMAGE_NT_HEADERS), &bytesRead, NULL)) {
		showmsg("Error reading PE header");
		CloseHandle(hFile);
		return false;
	}

	if (ntHeaders.FileHeader.Machine == IMAGE_FILE_MACHINE_I386) {
		CloseHandle(hFile);
		return false;
	}
	else if (ntHeaders.FileHeader.Machine == IMAGE_FILE_MACHINE_AMD64) {
		CloseHandle(hFile);
		return true;
	}
	else {
		CloseHandle(hFile);
		return false;
	}
}


// １つ目の引数だけファイルとして扱い、実行する。
//
// コマンドは こんな感じで連結されます。
//  exe linkpath linkarg cmdarg2 cmdarg3 cmdarg4 ...
//
static HRESULT RunArchitecturePeer(
	const wchar_t* peerName,
	const wchar_t* commandLine)
{
	wchar_t peerPath[MAX_PATH] = {};
	if (!GetModuleFileNameW(nullptr, peerPath, _countof(peerPath)) ||
		!PathRemoveFileSpecW(peerPath) ||
		!PathAppendW(peerPath, peerName)) {
		g_loaderFailureStage = 16;
		return HresultFromLastError();
	}

	SHELLEXECUTEINFOW execute = {};
	execute.cbSize = sizeof(execute);
	execute.fMask = SEE_MASK_NOCLOSEPROCESS | SEE_MASK_NOASYNC;
	execute.lpFile = peerPath;
	execute.lpParameters = commandLine;
	execute.nShow = SW_HIDE;
	if (!ShellExecuteExW(&execute) || !execute.hProcess) {
		g_loaderFailureStage = 16;
		return HresultFromLastError();
	}

	const DWORD waitResult =
		WaitForSingleObject(execute.hProcess, 60000);
	DWORD exitCode = ERROR_GEN_FAILURE;
	if (waitResult == WAIT_OBJECT_0)
		GetExitCodeProcess(execute.hProcess, &exitCode);
	CloseHandle(execute.hProcess);
	if (waitResult != WAIT_OBJECT_0) {
		g_loaderFailureStage = 17;
		return HRESULT_FROM_WIN32(
			waitResult == WAIT_TIMEOUT ? ERROR_TIMEOUT : GetLastError());
	}
	if (exitCode != ERROR_SUCCESS) {
		g_loaderFailureStage = exitCode;
		return HRESULT_FROM_WIN32(exitCode);
	}
	return S_OK;
}

static HRESULT HookAndExecute(int show)
{
	int     argc = 0;
	LPWSTR* argv = CommandLineToArgvW(GetCommandLineW(), &argc);
	if (!argv) {
		return HresultFromLastError();
	}
	if (argc <= 1) {
		char buffer[256];
		LoadStringA(GetModuleHandleA(NULL), IDS_USAGE, buffer, 256);
		MessageBoxA(NULL,
			buffer
			, "MacType", MB_OK | MB_ICONINFORMATION);
		LocalFree(argv);
		return S_OK;
	}


	int i;
	size_t length = 1;
	for (i = 1; i < argc; i++) {
		length += wcslen(argv[i]) + 3;
	}

	LPWSTR cmdline = (WCHAR*)calloc(sizeof(WCHAR), length);
	if (!cmdline) {
		LocalFree(argv);
		return E_OUTOFMEMORY;
	}

	LPWSTR p = cmdline;
	*p = L'\0';
	for (i = 1; i < argc; i++) {
		const bool dq = !!wcschr(argv[i], L' ');
		if (dq) {
			*p++ = '"';
			length--;
		}
		StringCchCopyExW(p, length, argv[i], &p, &length, STRSAFE_NO_TRUNCATION);
		if (dq) {
			*p++ = '"';
			length--;
		}
		*p++ = L' ';
		length--;
	}

	*CharPrevW(cmdline, p) = L'\0';

// now we got the full cmdline for external exetuble. let's check if we can hook into it
#ifdef _M_IX86
	if (isX64PE(argv[1])) {
		const HRESULT result =
			RunArchitecturePeer(L"MacLoader64.exe", cmdline);
		free(cmdline);
		LocalFree(argv);
		return result;
	}
#else
	if (!isX64PE(argv[1])) {
		const HRESULT result =
			RunArchitecturePeer(L"MacLoader.exe", cmdline);
		free(cmdline);
		LocalFree(argv);
		return result;
	}
#endif

	WCHAR file[MAX_PATH], dir[MAX_PATH];
	GetCurrentDirectoryW(_countof(dir), dir);
	StringCchCopyW(file, _countof(file), argv[1]);
	if (PathIsRelativeW(file)) {
		PathCombineW(file, dir, file);
	}
	else {
		WCHAR gdippDir[MAX_PATH];
		GetModuleFileNameW(NULL, gdippDir, _countof(gdippDir));
		PathRemoveFileSpec(gdippDir);

		// カレントディレクトリがgdi++.exeの置かれているディレクトリと同じだったら、
		// 起動しようとしているEXEのフルパスから抜き出したディレクトリ名をカレント
		// ディレクトリとして起動する。(カレントディレクトリがEXEと同じ場所である
		// 前提で作られているアプリ対策)
		if (wcscmp(dir, gdippDir) == 0) {
			StringCchCopyW(dir, _countof(dir), argv[1]);
			PathRemoveFileSpec(dir);
		}
	}

#ifdef _DEBUG
	if ((GetAsyncKeyState(VK_CONTROL) & 0x8000)
		&& MessageBoxW(NULL, cmdline, NULL, MB_YESNO) != IDYES) {
		free(cmdline);
		return NOERROR;
	}
#endif


	PROCESS_INFORMATION processInfo = {};
	STARTUPINFO startupInfo = { 0 };
	startupInfo.cb = sizeof(startupInfo);

	// get current directory and append mactype dll 
	WCHAR path[MAX_PATH] = { 0 };
	if (GetModuleFileNameW(NULL, path, _countof(path))) {
		PathRemoveFileSpecW(path);
		wcscat_s(path, L"\\");
	}
	wcscat_s(path, MacTypeDll);

	const WCHAR bootstrapVariable[] = L"MACTYPE_EXPLICIT_BOOTSTRAP";
	const DWORD previousSize =
		GetEnvironmentVariableW(bootstrapVariable, NULL, 0);
	WCHAR* previousValue =
		previousSize ? new (std::nothrow) WCHAR[previousSize] : NULL;
	if (previousValue)
		GetEnvironmentVariableW(
			bootstrapVariable, previousValue, previousSize);
	SetEnvironmentVariableW(bootstrapVariable, L"1");

	DebugSetProcessKillOnExit(FALSE);
	auto ret = CreateProcessW(
		NULL,
		cmdline,
		NULL,
		NULL,
		false,
		DEBUG_ONLY_THIS_PROCESS,
		NULL,
		dir,
		&startupInfo,
		&processInfo);

	if (previousValue)
		SetEnvironmentVariableW(bootstrapVariable, previousValue);
	else
		SetEnvironmentVariableW(bootstrapVariable, NULL);
	delete[] previousValue;

	free(cmdline);
	LocalFree(argv);
	argv = NULL;
	if (!ret)
		return E_ACCESSDENIED;

	const bool initialized =
		SuspendAtProcessEntry(processInfo) &&
		InjectDormantDll(processInfo, path) &&
		InitializeInjectedProcess(processInfo);
	ResumeThread(processInfo.hThread);
	CloseHandle(processInfo.hThread);
	CloseHandle(processInfo.hProcess);
	return initialized ? S_OK : HRESULT_FROM_WIN32(ERROR_DLL_INIT_FAILED);
}

int WINAPI wWinMain(HINSTANCE ins, HINSTANCE prev, LPWSTR cmd, int show)
{
	_CrtSetDbgFlag(_CrtSetDbgFlag(_CRTDBG_REPORT_FLAG) | _CRTDBG_LEAK_CHECK_DF);
	OleInitialize(NULL);

	WCHAR path[MAX_PATH];
	if (GetModuleFileNameW(NULL, path, _countof(path))) {
		PathRemoveFileSpec(path);
		wcscat(path, L"\\");
		wcscat(path, MacTypeDll);
		//DONT_RESOLVE_DLL_REFERENCESを指定すると依存関係の解決や
		//DllMainの呼び出しが行われない
		hinstDLL = LoadLibraryExW(path, NULL, DONT_RESOLVE_DLL_REFERENCES);
	}
	if (!hinstDLL) {
		errmsg(IDS_DLL, HresultFromLastError());
	}
	else {
		PathRemoveFileSpecW(path);
		SetCurrentDirectoryW(path);

		HRESULT hr = E_INVALIDARG;
		int argumentCount = 0;
		LPWSTR* arguments = CommandLineToArgvW(
			GetCommandLineW(), &argumentCount);
		if (arguments &&
			argumentCount == 5 &&
			_wcsicmp(arguments[1], L"--attach") == 0) {
			const DWORD processId =
				wcstoul(arguments[2], nullptr, 10);
			const DWORD threadId =
				wcstoul(arguments[3], nullptr, 10);
			hr = AttachAndInitialize(
				processId, threadId, arguments[4]);
		}
		else {
			hr = HookAndExecute(show);
		}
		if (arguments)
			LocalFree(arguments);
		if (hr != S_OK) {
			errmsg(IDC_EXEC, hr);
		}
	}

	OleUninitialize();
	if (g_loaderFailureStage) {
		MacTypeSetLastDiagnostic(
			MacTypeDiagnosticCode::LoaderFailure,
			g_loaderFailureStage);
		MacTypeLog(
			MacTypeLogLevel::Error,
			MacTypeDiagnosticCode::LoaderFailure,
			L"loader exiting with failure stage=%lu",
			g_loaderFailureStage);
	}
	return static_cast<int>(g_loaderFailureStage);
}

//EOF
