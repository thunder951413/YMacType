# Windows 11 validation

The test programs are intentionally small native executables so both x86 and
x64 builds can be injected exactly like real applications.

- `lifecycle_smoke.cpp` verifies dormant loading, explicit initialization,
  late DirectWrite loading, status reporting and shutdown.
- `injected_child.cpp` verifies entry-point injection, same-bitness child
  propagation and x86/x64 broker propagation.
- `render_smoke.cpp` creates a real DirectWrite font face and bitmap render
  target, draws a glyph run and verifies that pixels were produced.

Release validation builds and runs every test in both architectures. Browser
validation launches the locally installed Chrome through the matching
MacLoader and checks the per-process diagnostic logs for successful renderer
initialization.
