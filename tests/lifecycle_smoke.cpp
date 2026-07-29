#include "../lifecycle.h"

#include <cstdio>
#include <dwrite.h>

typedef BOOL (WINAPI *GetStatusProc)(MacTypeStatus*);
typedef BOOL (WINAPI *LifecycleProc)();

struct InitializeRequest
{
	LifecycleProc initialize;
	BOOL result;
};

DWORD WINAPI InitializeWorker(void* parameter)
{
	InitializeRequest* request =
		static_cast<InitializeRequest*>(parameter);
	request->result = request->initialize();
	return request->result ? ERROR_SUCCESS : GetLastError();
}

int wmain(int argc, wchar_t** argv)
{
	if (argc < 2 || argc > 3) {
		fwprintf(
			stderr,
			L"usage: lifecycle_smoke <MacType.Core.dll> [--initialize]\n");
		return 2;
	}

	if (!SetEnvironmentVariableW(L"MACTYPE_EXPLICIT_BOOTSTRAP", L"1")) {
		fwprintf(stderr, L"failed to select explicit bootstrap mode\n");
		return 3;
	}

	HMODULE module = LoadLibraryW(argv[1]);
	if (!module) {
		fwprintf(stderr, L"LoadLibrary failed: %lu\n", GetLastError());
		return 4;
	}

	GetStatusProc getStatus = reinterpret_cast<GetStatusProc>(
		GetProcAddress(module, "MacTypeGetStatus"));
	if (!getStatus) {
		fwprintf(stderr, L"MacTypeGetStatus export is missing\n");
		FreeLibrary(module);
		return 5;
	}

	MacTypeStatus status = {};
	status.size = sizeof(status);
	if (!getStatus(&status)) {
		fwprintf(stderr, L"MacTypeGetStatus failed\n");
		FreeLibrary(module);
		return 6;
	}

	const bool dormant =
		status.lifecycle == MacTypeLifecycleDormant &&
		!status.hooksEnabled &&
		!status.hooksInstalled;
	wprintf(
		L"version=%lu lifecycle=%lu backends=0x%08lx enabled=%d installed=%d\n",
		status.version,
		static_cast<DWORD>(status.lifecycle),
		status.loadedRenderBackends,
		status.hooksEnabled,
		status.hooksInstalled);

	bool lifecycleOk = dormant;
	HMODULE dwriteModule = nullptr;
	if (argc == 3) {
		LifecycleProc initialize = reinterpret_cast<LifecycleProc>(
			GetProcAddress(module, "MacTypeInitialize"));
		LifecycleProc shutdown = reinterpret_cast<LifecycleProc>(
			GetProcAddress(module, "MacTypeShutdown"));
		if (!initialize || !shutdown) {
			fwprintf(stderr, L"lifecycle exports are missing\n");
			FreeLibrary(module);
			return 8;
		}
		InitializeRequest requests[8] = {};
		HANDLE workers[8] = {};
		for (size_t index = 0; index != _countof(workers); ++index) {
			requests[index].initialize = initialize;
			workers[index] = CreateThread(
				nullptr, 0, InitializeWorker,
				&requests[index], 0, nullptr);
			if (!workers[index]) {
				fwprintf(stderr, L"failed to create lifecycle worker\n");
				for (size_t closeIndex = 0;
					closeIndex != index; ++closeIndex)
					CloseHandle(workers[closeIndex]);
				FreeLibrary(module);
				return 8;
			}
		}
		WaitForMultipleObjects(
			_countof(workers), workers, TRUE, 30000);
		bool allInitialized = true;
		for (size_t index = 0; index != _countof(workers); ++index) {
			if (workers[index])
				CloseHandle(workers[index]);
			allInitialized =
				allInitialized && requests[index].result;
		}
		if (!allInitialized) {
			fwprintf(stderr, L"explicit initialization failed\n");
			FreeLibrary(module);
			return 8;
		}

		dwriteModule = LoadLibraryW(L"dwrite.dll");
		typedef HRESULT (WINAPI *DWriteCreateFactoryProc)(
			DWRITE_FACTORY_TYPE, REFIID, IUnknown**);
		DWriteCreateFactoryProc createFactory = dwriteModule
			? reinterpret_cast<DWriteCreateFactoryProc>(
				GetProcAddress(dwriteModule, "DWriteCreateFactory"))
			: nullptr;
		IUnknown* factory = nullptr;
		if (!createFactory ||
			FAILED(createFactory(
				DWRITE_FACTORY_TYPE_SHARED,
				__uuidof(IDWriteFactory),
				&factory))) {
			fwprintf(stderr, L"late DirectWrite factory creation failed\n");
			shutdown();
			FreeLibrary(module);
			return 10;
		}
		factory->Release();

		status = {};
		status.size = sizeof(status);
		lifecycleOk = getStatus(&status) &&
			status.lifecycle == MacTypeLifecycleReady &&
			status.hooksEnabled &&
			status.hooksInstalled &&
			(status.loadedRenderBackends & (1u << 1));
		wprintf(
			L"initialized lifecycle=%lu backends=0x%08lx enabled=%d installed=%d\n",
			static_cast<DWORD>(status.lifecycle),
			status.loadedRenderBackends,
			status.hooksEnabled,
			status.hooksInstalled);

		if (!shutdown()) {
			fwprintf(stderr, L"explicit shutdown failed\n");
			FreeLibrary(module);
			return 9;
		}

		status = {};
		status.size = sizeof(status);
		lifecycleOk = lifecycleOk &&
			getStatus(&status) &&
			status.lifecycle == MacTypeLifecycleDormant &&
			!status.hooksEnabled &&
			!status.hooksInstalled;
	}

	FreeLibrary(module);
	if (dwriteModule)
		FreeLibrary(dwriteModule);
	return lifecycleOk ? 0 : 7;
}
