#include "render_backend.h"

namespace
{
bool IsLoaded(const wchar_t* moduleName)
{
	return GetModuleHandleW(moduleName) != nullptr;
}
}

RenderBackendSnapshot DetectLoadedRenderBackends()
{
	RenderBackend loaded = RenderBackend::None;

	if (IsLoaded(L"gdi32.dll") || IsLoaded(L"gdi32full.dll"))
		loaded = loaded | RenderBackend::Gdi;
	if (IsLoaded(L"dwrite.dll"))
		loaded = loaded | RenderBackend::DirectWrite;
	if (IsLoaded(L"DWriteCore.dll"))
		loaded = loaded | RenderBackend::DWriteCore;
	if (IsLoaded(L"freetype.dll") || IsLoaded(L"freetype6.dll"))
		loaded = loaded | RenderBackend::FreeType;
	if (IsLoaded(L"chrome.dll") || IsLoaded(L"msedge.dll") ||
		IsLoaded(L"electron.exe"))
		loaded = loaded | RenderBackend::ChromiumHost;

	return { sizeof(RenderBackendSnapshot), loaded };
}
