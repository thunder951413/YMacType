#include "../lifecycle.h"

#include <windows.h>
#include <cstdio>
#include <vector>

typedef BOOL (WINAPI* LifecycleProc)();

int wmain(int argc, wchar_t** argv)
{
	if (argc != 2) {
		fwprintf(stderr, L"usage: font_profile_probe <MacType.Core.dll>\n");
		return 2;
	}

	SetEnvironmentVariableW(L"MACTYPE_EXPLICIT_BOOTSTRAP", L"1");
	HMODULE core = LoadLibraryW(argv[1]);
	if (!core)
		return 3;
	LifecycleProc initialize = reinterpret_cast<LifecycleProc>(
		GetProcAddress(core, "MacTypeInitialize"));
	LifecycleProc shutdown = reinterpret_cast<LifecycleProc>(
		GetProcAddress(core, "MacTypeShutdown"));
	if (!initialize || !shutdown || !initialize())
		return 4;

	HDC dc = CreateCompatibleDC(nullptr);
	LOGFONTW requested = {};
	requested.lfHeight = -24;
	requested.lfWeight = FW_NORMAL;
	requested.lfCharSet = DEFAULT_CHARSET;
	requested.lfOutPrecision = OUT_DEFAULT_PRECIS;
	requested.lfClipPrecision = CLIP_DEFAULT_PRECIS;
	requested.lfQuality = DEFAULT_QUALITY;
	requested.lfPitchAndFamily = DEFAULT_PITCH;
	wcscpy_s(requested.lfFaceName, L"Segoe UI");
	HFONT font = CreateFontIndirectW(&requested);
	HGDIOBJ previous = SelectObject(dc, font);
	wchar_t resolved[LF_FACESIZE] = {};
	GetTextFaceW(dc, static_cast<int>(_countof(resolved)), resolved);
	const UINT metricsSize = GetOutlineTextMetricsW(dc, 0, nullptr);
	std::vector<BYTE> metricsBuffer(metricsSize);
	OUTLINETEXTMETRICW* metrics = metricsSize
		? reinterpret_cast<OUTLINETEXTMETRICW*>(metricsBuffer.data())
		: nullptr;
	LPCWSTR actualFamily = L"";
	if (metrics &&
		GetOutlineTextMetricsW(dc, metricsSize, metrics) == metricsSize) {
		actualFamily = reinterpret_cast<LPCWSTR>(
			metricsBuffer.data() +
			reinterpret_cast<ULONG_PTR>(metrics->otmpFamilyName));
	}
	wprintf(
		L"requested=Segoe UI reported=%ls actual=%ls\n",
		resolved, actualFamily);

	SelectObject(dc, previous);
	DeleteObject(font);
	DeleteDC(dc);
	shutdown();

	return wcsstr(actualFamily, L"PingFang") ||
		wcsstr(actualFamily, L"\x82F9\x65B9")
		? 0
		: 5;
}
