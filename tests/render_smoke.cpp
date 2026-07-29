#include "../lifecycle.h"

#include <dwrite.h>
#include <cstdio>
#include <vector>

#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

typedef BOOL (WINAPI *LifecycleProc)();

int wmain(int argc, wchar_t** argv)
{
	if (argc != 2) {
		fwprintf(stderr, L"usage: render_smoke <MacType.Core.dll>\n");
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

	IDWriteFactory* factory = nullptr;
	HRESULT result = DWriteCreateFactory(
		DWRITE_FACTORY_TYPE_SHARED,
		__uuidof(IDWriteFactory),
		reinterpret_cast<IUnknown**>(&factory));
	if (FAILED(result))
		return 5;

	IDWriteFontCollection* collection = nullptr;
	IDWriteFontFamily* family = nullptr;
	IDWriteFont* font = nullptr;
	IDWriteFontFace* face = nullptr;
	IDWriteGdiInterop* interop = nullptr;
	IDWriteBitmapRenderTarget* target = nullptr;
	IDWriteRenderingParams* renderingParams = nullptr;
	UINT32 familyIndex = 0;
	BOOL familyExists = FALSE;
	result = factory->GetSystemFontCollection(&collection);
	if (SUCCEEDED(result))
		result = collection->FindFamilyName(
			L"Segoe UI", &familyIndex, &familyExists);
	if (SUCCEEDED(result) && familyExists)
		result = collection->GetFontFamily(familyIndex, &family);
	if (SUCCEEDED(result))
		result = family->GetFirstMatchingFont(
			DWRITE_FONT_WEIGHT_NORMAL,
			DWRITE_FONT_STRETCH_NORMAL,
			DWRITE_FONT_STYLE_NORMAL,
			&font);
	if (SUCCEEDED(result))
		result = font->CreateFontFace(&face);
	if (SUCCEEDED(result))
		result = factory->GetGdiInterop(&interop);
	if (SUCCEEDED(result))
		result = interop->CreateBitmapRenderTarget(nullptr, 640, 120, &target);
	if (SUCCEEDED(result))
		result = factory->CreateRenderingParams(&renderingParams);
	if (FAILED(result))
		return 6;

	const wchar_t text[] =
		L"MacType Windows 11 0123456789";
	const UINT32 textLength =
		static_cast<UINT32>(_countof(text) - 1);
	std::vector<UINT16> glyphs(textLength);
	std::vector<FLOAT> advances(textLength, 24.0f);
	std::vector<UINT32> codePoints(textLength);
	for (UINT32 index = 0; index != textLength; ++index)
		codePoints[index] = text[index];
	result = face->GetGlyphIndices(
		codePoints.data(), textLength, glyphs.data());
	if (FAILED(result))
		return 7;

	HDC dc = target->GetMemoryDC();
	RECT bounds = { 0, 0, 640, 120 };
	FillRect(dc, &bounds, static_cast<HBRUSH>(
		GetStockObject(WHITE_BRUSH)));
	DWRITE_GLYPH_RUN run = {};
	run.fontFace = face;
	run.fontEmSize = 32.0f;
	run.glyphCount = textLength;
	run.glyphIndices = glyphs.data();
	run.glyphAdvances = advances.data();
	result = target->DrawGlyphRun(
		10.0f,
		70.0f,
		DWRITE_MEASURING_MODE_NATURAL,
		&run,
		renderingParams,
		RGB(0, 0, 0),
		nullptr);

	BITMAP bitmap = {};
	HBITMAP selected = static_cast<HBITMAP>(
		GetCurrentObject(dc, OBJ_BITMAP));
	GetObjectW(selected, sizeof(bitmap), &bitmap);
	BITMAPINFO info = {};
	info.bmiHeader.biSize = sizeof(info.bmiHeader);
	info.bmiHeader.biWidth = bitmap.bmWidth;
	info.bmiHeader.biHeight = -bitmap.bmHeight;
	info.bmiHeader.biPlanes = 1;
	info.bmiHeader.biBitCount = 32;
	info.bmiHeader.biCompression = BI_RGB;
	std::vector<DWORD> pixels(
		static_cast<size_t>(bitmap.bmWidth) * bitmap.bmHeight);
	const int lines = GetDIBits(
		dc, selected, 0, bitmap.bmHeight,
		pixels.data(), &info, DIB_RGB_COLORS);
	size_t inkPixels = 0;
	for (DWORD pixel : pixels) {
		if ((pixel & 0x00ffffffu) != 0x00ffffffu)
			++inkPixels;
	}

	if (target)
		target->Release();
	if (renderingParams)
		renderingParams->Release();
	if (interop)
		interop->Release();
	if (face)
		face->Release();
	if (font)
		font->Release();
	if (family)
		family->Release();
	if (collection)
		collection->Release();
	factory->Release();
	shutdown();
	FreeLibrary(core);
	wprintf(
		L"draw=0x%08lx lines=%d ink=%zu\n",
		result, lines, inkPixels);
	return SUCCEEDED(result) && lines > 0 && inkPixels > 100
		? 0
		: 8;
}
