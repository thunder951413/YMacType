#include "../lifecycle.h"

#include <cstdio>
#include <string>

typedef BOOL (WINAPI *GetStatusProc)(MacTypeStatus*);

int wmain(int argc, wchar_t** argv)
{
	if ((argc == 3 && wcscmp(argv[1], L"--spawn") == 0) ||
		(argc == 4 && wcscmp(argv[1], L"--spawn-exe") == 0)) {
		wchar_t executable[MAX_PATH] = {};
		const wchar_t* outputPath = nullptr;
		if (argc == 3) {
			if (!GetModuleFileNameW(nullptr, executable, _countof(executable)))
				return 5;
			outputPath = argv[2];
		}
		else {
			wcscpy_s(executable, argv[2]);
			outputPath = argv[3];
		}
		std::wstring command =
			L"\"" + std::wstring(executable) + L"\" \"" + outputPath + L"\"";
		STARTUPINFOW startup = {};
		startup.cb = sizeof(startup);
		PROCESS_INFORMATION process = {};
		if (!CreateProcessW(
			nullptr, &command[0], nullptr, nullptr, FALSE, 0, nullptr, nullptr,
			&startup, &process))
			return 6;
		WaitForSingleObject(process.hProcess, 30000);
		DWORD exitCode = 7;
		GetExitCodeProcess(process.hProcess, &exitCode);
		CloseHandle(process.hThread);
		CloseHandle(process.hProcess);
		return static_cast<int>(exitCode);
	}

	if (argc != 2)
		return 2;

#ifdef _WIN64
	const wchar_t moduleName[] = L"MacType64.Core.dll";
#else
	const wchar_t moduleName[] = L"MacType.Core.dll";
#endif

	HMODULE module = nullptr;
	GetStatusProc getStatus = nullptr;
	MacTypeStatus status = {};
	bool ready = false;
	for (int attempt = 0; attempt != 300 && !ready; ++attempt) {
		module = GetModuleHandleW(moduleName);
		getStatus = module
			? reinterpret_cast<GetStatusProc>(
				GetProcAddress(module, "MacTypeGetStatus"))
			: nullptr;
		status = {};
		status.size = sizeof(status);
		ready =
			getStatus &&
			getStatus(&status) &&
			status.lifecycle == MacTypeLifecycleReady &&
			status.hooksEnabled &&
			status.hooksInstalled;
		if (!ready)
			Sleep(10);
	}

	FILE* output = nullptr;
	_wfopen_s(&output, argv[1], L"wb");
	if (!output)
		return 3;
	fprintf(
		output,
		"%s module=%d status=%d lifecycle=%lu enabled=%d installed=%d\n",
		ready ? "ready" : "not-ready",
		module != nullptr,
		getStatus != nullptr,
		static_cast<DWORD>(status.lifecycle),
		status.hooksEnabled,
		status.hooksInstalled);
	fclose(output);
	return ready ? 0 : 4;
}
