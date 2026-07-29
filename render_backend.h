#pragma once

#include <Windows.h>
#include <stdint.h>

enum class RenderBackend : uint32_t
{
	None = 0,
	Gdi = 1u << 0,
	DirectWrite = 1u << 1,
	DWriteCore = 1u << 2,
	FreeType = 1u << 3,
	ChromiumHost = 1u << 4,
};

inline RenderBackend operator|(RenderBackend left, RenderBackend right)
{
	return static_cast<RenderBackend>(
		static_cast<uint32_t>(left) | static_cast<uint32_t>(right));
}

inline bool HasRenderBackend(RenderBackend value, RenderBackend backend)
{
	return (static_cast<uint32_t>(value) & static_cast<uint32_t>(backend)) != 0;
}

struct RenderBackendSnapshot
{
	uint32_t size;
	RenderBackend loaded;
};

// This is deliberately observational: capability detection must never load a
// rendering DLL into the target process.
RenderBackendSnapshot DetectLoadedRenderBackends();
