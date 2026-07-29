# MacType rendering architecture

## Goal

MacType should intercept supported Windows font backends rather than assume
that every application draws text through GDI.  The rendering algorithm and
the process-integration mechanism are separate components. This refactor
targets Windows 11 on x86 and x64 only; ARM64 is intentionally out of scope.

## Layers

1. **Render core**
   - FreeType face management, glyph rasterization, gamma/LCD processing and
     glyph caches.
   - No process injection or backend-specific COM objects.
2. **Backend adapters**
   - GDI/GDI+, system DirectWrite, DWriteCore and dynamically linked FreeType.
   - Adapters preserve the host's metrics and replace only glyph masks where
     possible.
3. **Process integration**
   - Target discovery, architecture matching, child-process bootstrap,
     mitigation-policy checks and diagnostics.
   - It must not weaken a target process mitigation policy.

## Lifecycle

`MacTypeInitialize`, `MacTypeShutdown` and `MacTypeGetStatus` are the stable
loader-facing lifecycle and diagnostic API. In compatibility mode `DllMain`
stores the module handle and schedules a pinned worker; configuration,
FreeType initialization and Detours transactions execute after loader lock.

The launcher sets `MACTYPE_EXPLICIT_BOOTSTRAP`, starts the target under the
Windows debugger, places a temporary breakpoint at the executable entry point,
and stops there after the system loader has released loader lock. It then
injects the dormant DLL, invokes `MacTypeInitializeThread`, checks the return
value and resumes the primary thread. This eliminates the race between
application startup and DirectWrite/Skia factory creation without running
FreeType or Detours transactions from `DllMain`.

## Backend rules

- Detect capabilities with `GetModuleHandle`/`GetProcAddress`/`QueryInterface`;
  do not branch on the reported Windows version.
- Capability detection must not load rendering DLLs as a side effect.
- DirectWrite adapters start at `DWriteCreateFactory` and wrap COM objects.
- DWriteCore is a separate backend starting at `DWriteCoreCreateFactory`.
- Chromium renderers must be initialized before sandbox lockdown and before
  Skia creates its DirectWrite font manager.
- Once text has become a GPU texture or geometry, it is outside MacType's
  reliable interception boundary.

The compatibility adapter detects an already-loaded DWriteCore module and
attaches to `DWriteCoreCreateFactory`. It intentionally does not load
DWriteCore as a side effect. System and packaged `LoadLibrary` entry points
rescan already-loaded rendering modules after a load completes, outside the
loader callback. Full simultaneous system-DirectWrite plus DWriteCore COM
proxying remains a separate adapter milestone because their vtables can have
different implementation addresses in one process.

`CreateProcessA/W` propagation is enabled by default so browser renderer,
GPU/utility and Electron descendants receive the architecture-matched core.
The bootstrap executes at the child's original entry point, loads the x86 or
x64 `MacType*.Core.dll` directly, and lets the loader-safe worker complete
initialization. Existing `MacType.dll`/`MacType64.dll` bootstrap files remain
compatible with MacTray and are not replaced by the core binaries.
Profiles can set `HookChildProcesses=0` when process propagation is unwanted.
Normal browser process trees are same-bitness. A process that deliberately
creates a child of the opposite architecture must be handed to the matching
x86/x64 injection broker; the legacy cross-WoW64 shellcode is not treated as a
reliable Windows 11 integration path.

## Security boundary

Protected processes, strict dynamic-code policies and code-integrity policy are
hard compatibility boundaries.  MacType reports and skips those processes; it
does not modify their mitigation policy.

## Diagnostics

Every initialized core and loader writes a per-process UTF-8 log under
`%LOCALAPPDATA%\MacType\Logs`. Services use the corresponding service-profile
directory. `MACTYPE_LOG_DIR` overrides the destination and
`MACTYPE_LOG_VERBOSE=1` enables high-volume rendering traces. Files rotate at
5 MiB and retain one previous generation.

`MacTypeGetStatus` version 2 reports the lifecycle, loaded backends, hook state,
last diagnostic code/error, backend count and number of observed COM method
implementations. Loader stages, mitigation-policy rejections, child broker
results and DirectWrite/DWriteCore implementation conflicts are persisted.

## Coverage boundary

- GDI/GDI+, DirectWrite/D2D and DWriteCore are interceptable when MacType is
  initialized before their factories and render targets are created.
- Chromium, Edge and Electron use Skia with DirectWrite on Windows. Early
  renderer-process injection therefore reaches their DirectWrite analysis
  path when the renderer permits third-party modules; late injection cannot
  recover factories that already exist.
- Official Chrome enables Microsoft-signed Code Integrity Guard for renderer
  processes. An unsigned MacType build must skip those protected renderers;
  the browser and eligible utility processes remain covered. Disabling CIG
  would weaken the browser sandbox and is deliberately not performed by the
  installer.
- Applications that rasterize text completely inside a private engine, emit
  GPU geometry, or run inside PPL/strict ACG remain outside a universal
  replacement boundary. They require an engine-specific adapter or must be
  skipped.
