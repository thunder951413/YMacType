#include "diagnostics.h"

#include <Shlwapi.h>
#include <strsafe.h>
#include <stdarg.h>

#pragma comment(lib, "Shlwapi.lib")

namespace
{
SRWLOCK g_logLock = SRWLOCK_INIT;
volatile LONG g_lastCode;
volatile LONG g_lastError;

const wchar_t* LevelName(MacTypeLogLevel level)
{
	switch (level) {
	case MacTypeLogLevel::Debug:
		return L"DEBUG";
	case MacTypeLogLevel::Info:
		return L"INFO";
	case MacTypeLogLevel::Warning:
		return L"WARN";
	case MacTypeLogLevel::Error:
		return L"ERROR";
	default:
		return L"UNKNOWN";
	}
}

bool EnsureDirectory(const wchar_t* path)
{
	if (CreateDirectoryW(path, nullptr))
		return true;
	return GetLastError() == ERROR_ALREADY_EXISTS &&
		PathIsDirectoryW(path);
}

bool BuildLogPath(wchar_t* path, size_t pathCount)
{
	wchar_t root[MAX_PATH] = {};
	if (!GetEnvironmentVariableW(
		L"MACTYPE_LOG_DIR", root, _countof(root))) {
		if (!GetEnvironmentVariableW(
			L"LOCALAPPDATA", root, _countof(root))) {
			if (!GetTempPathW(_countof(root), root))
				return false;
		}
		if (!PathAppendW(root, L"MacType"))
			return false;
		if (!EnsureDirectory(root))
			return false;
		if (!PathAppendW(root, L"Logs"))
			return false;
	}
	if (!EnsureDirectory(root))
		return false;

	wchar_t executable[MAX_PATH] = {};
	if (!GetModuleFileNameW(nullptr, executable, _countof(executable)))
		StringCchCopyW(executable, _countof(executable), L"unknown.exe");
	wchar_t* executableName = PathFindFileNameW(executable);
	for (wchar_t* character = executableName; *character; ++character) {
		if (*character == L'\\' || *character == L'/' ||
			*character == L':' || *character == L'*' ||
			*character == L'?' || *character == L'"' ||
			*character == L'<' || *character == L'>' ||
			*character == L'|')
			*character = L'_';
	}

	return SUCCEEDED(StringCchPrintfW(
		path,
		pathCount,
		L"%s\\mactype-%s-%lu.log",
		root,
		executableName,
		GetCurrentProcessId()));
}

HANDLE OpenLogFile(const wchar_t* path)
{
	HANDLE file = CreateFileW(
		path,
		FILE_APPEND_DATA | GENERIC_READ,
		FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
		nullptr,
		OPEN_ALWAYS,
		FILE_ATTRIBUTE_NORMAL,
		nullptr);
	if (file == INVALID_HANDLE_VALUE)
		return file;

	LARGE_INTEGER size = {};
	if (GetFileSizeEx(file, &size) && size.QuadPart > 5 * 1024 * 1024) {
		CloseHandle(file);
		wchar_t previous[MAX_PATH] = {};
		if (SUCCEEDED(StringCchPrintfW(
			previous, _countof(previous), L"%s.1", path))) {
			MoveFileExW(
				path, previous,
				MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
		}
		file = CreateFileW(
			path,
			FILE_APPEND_DATA,
			FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
			nullptr,
			CREATE_ALWAYS,
			FILE_ATTRIBUTE_NORMAL,
			nullptr);
	}
	return file;
}
}

void MacTypeSetLastDiagnostic(
	MacTypeDiagnosticCode code,
	DWORD error)
{
	InterlockedExchange(
		&g_lastCode, static_cast<LONG>(code));
	InterlockedExchange(
		&g_lastError, static_cast<LONG>(error));
}

MacTypeDiagnosticCode MacTypeGetLastDiagnosticCode()
{
	return static_cast<MacTypeDiagnosticCode>(
		InterlockedCompareExchange(&g_lastCode, 0, 0));
}

DWORD MacTypeGetLastDiagnosticError()
{
	return static_cast<DWORD>(
		InterlockedCompareExchange(&g_lastError, 0, 0));
}

void MacTypeLog(
	MacTypeLogLevel level,
	MacTypeDiagnosticCode code,
	const wchar_t* format,
	...)
{
	if (!format)
		return;
	if (level == MacTypeLogLevel::Debug) {
		wchar_t verbose[2] = {};
		if (!GetEnvironmentVariableW(
			L"MACTYPE_LOG_VERBOSE", verbose, _countof(verbose)))
			return;
	}

	wchar_t message[2048] = {};
	va_list arguments;
	va_start(arguments, format);
	StringCchVPrintfW(
		message, _countof(message), format, arguments);
	va_end(arguments);

	SYSTEMTIME time = {};
	GetLocalTime(&time);
	wchar_t line[2560] = {};
	StringCchPrintfW(
		line,
		_countof(line),
		L"%04u-%02u-%02u %02u:%02u:%02u.%03u "
		L"pid=%lu tid=%lu level=%s code=%lu error=%lu %s\r\n",
		time.wYear,
		time.wMonth,
		time.wDay,
		time.wHour,
		time.wMinute,
		time.wSecond,
		time.wMilliseconds,
		GetCurrentProcessId(),
		GetCurrentThreadId(),
		LevelName(level),
		static_cast<DWORD>(code),
		MacTypeGetLastDiagnosticError(),
		message);
	OutputDebugStringW(line);

	wchar_t path[MAX_PATH] = {};
	if (!BuildLogPath(path, _countof(path)))
		return;

	AcquireSRWLockExclusive(&g_logLock);
	HANDLE file = OpenLogFile(path);
	if (file != INVALID_HANDLE_VALUE) {
		const int bytesRequired = WideCharToMultiByte(
			CP_UTF8, 0, line, -1, nullptr, 0, nullptr, nullptr);
		if (bytesRequired > 1) {
			char* bytes = static_cast<char*>(
				HeapAlloc(
					GetProcessHeap(), 0,
					static_cast<SIZE_T>(bytesRequired)));
			if (bytes) {
				const int converted = WideCharToMultiByte(
					CP_UTF8, 0, line, -1, bytes, bytesRequired,
					nullptr, nullptr);
				if (converted > 1) {
					DWORD written = 0;
					WriteFile(
						file, bytes,
						static_cast<DWORD>(converted - 1),
						&written, nullptr);
				}
				HeapFree(GetProcessHeap(), 0, bytes);
			}
		}
		CloseHandle(file);
	}
	ReleaseSRWLockExclusive(&g_logLock);
}
