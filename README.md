MacType
========================
[日本語](./README_ja-JP.md)

Better font rendering for Windows.

Latest build
------------------

[Download YMacType for Windows 11](https://github.com/thunder951413/YMacType/releases/latest)

YMacType Windows 11 refactor
------------------

This branch keeps the existing MacTray bootstrap compatible while replacing
the rendering core and command-line loaders. It supports x86 and x64 (ARM is
intentionally out of scope) and adds:

- loader-lock-safe initialization with a documented lifecycle/status API;
- early entry-point injection and automatic child-process propagation;
- x86/x64 broker injection in both directions;
- capability-based GDI, DirectWrite and DWriteCore discovery;
- Chromium/Electron process-tree coverage before renderer sandbox lockdown;
- per-process UTF-8 diagnostics with rotation and stable diagnostic codes;
- CFG, CET, DEP/ASLR and W^X-safe remote bootstrap memory;
- repeatable release packaging, local deployment with backup/rollback, and
  x86/x64 lifecycle, pixel-rendering and injection tests.

Windows 11 settings and service
------------------

Run `YMacType.Settings.exe` to configure the active font substitutions and
rendering parameters. The panel enumerates installed fonts, provides a live
preview, applies the bundled macOS-style profile without restarting the
injection service, and shows current coverage and recent diagnostic errors.

The settings panel also runs as a single system-tray application after logon.
Left-clicking its tray icon opens a live list of processes where YMacType is
effective. The list refreshes automatically and can be searched by application
name, PID or executable path. Closing the window hides it back to the tray;
the tray menu provides an explicit exit action. The Status page reports the
Automatic LocalSystem service and provides a switch for enabling or disabling
the settings-panel logon task without changing the core rendering service.

The recommended installation is the automatic LocalSystem service mode. The
legacy MacTuner, MacWiz, updater, benchmark, tray shortcuts and obsolete
uninstaller are removed by the installer after being backed up. `MacTray.exe`
is intentionally retained only as the headless x86/x64 injection service host;
removing it would reduce coverage for applications that cannot be attached
after startup.

The default `YMacType-macOS.ini` profile substitutes common Windows UI and
Chinese fonts with PingFang SC and uses grayscale, low-hinting rendering.
Applications with protected processes, private renderers, GPU-only text, or
incompatible sandboxes remain outside the safe interception boundary. Console
hosts (Windows Terminal, OpenConsole, PowerShell, cmd and conhost) are excluded
because their terminal rendering path cannot benefit from the GDI/DirectWrite
hooks and would otherwise create misleading diagnostics. Windows 11 Task
Manager is also excluded because its WinUI text stack can retain incompatible
glyph indices when a DirectWrite font face is substituted. Windows text-input
hosts and the WeType renderer remain covered by MacType rasterization, but use
the profile's `ExcludeSub` compatibility mode. This preserves the exact font
face and glyph-index mapping supplied by the IME while retaining GDI and
DirectWrite rendering improvements.

See [the architecture and compatibility notes](architecture/REFACTORING.md)
for the exact interception and protected-process boundaries.

Official site
------------------

MacType official site: 

http://www.mactype.net (An archived version is restored)

What's new?
------------------

- Win11 compatible
- CET compatible
- Updated FreeType
- Support for color fonts :sunglasses:
- New installer
- Lots of bug fixes
- Updates for multi-monitor support
- Tray app can intercept explorer in Service Mode now
- Tweaks for diacritics
- Updates to EasyHook
- Lower CPU in Tray Mode
- Better DirectWrite support thanks to [しらいと](http://silight.hatenablog.jp)
- Separate DirectWrite parameter adjustment
- Traditional Chinese localization greatly improved thanks to GT Wang
- English localization improved
- Added Korea localization, thanks to 조현희
- MultiLang system improved

Donation
------------------

MacType now accepts donations. 

Please visit http://www.mactype.net and keep an eye on the bottom right corner :heart:

Thank you for your support! Your donations will keep the server running, keep me updating, and buy more coffees :coffee:

Known issues
---------------

- Please backup your profiles before upgrading!

- Only Chinese simplified/Traditional and English are fully localized, some options may missing in MacType Tuner due to the strings missing in the language file. You can help with translations!

- If you want to use MacType-patch together with MacType official release, remember to add DirectWrite=0 to your profile or you will have mysterious problems

- If you're running 64 bit Windows, antimalware/antivirus software may conflict with MacType, because it sees MacType trying to modify running software. One possible workaround is to try running in Service Mode (recommended), or add HookChildProcesses=0 to your profile. See https://github.com/snowie2000/mactype/wiki/HookChildProcesses for an explanation

- Office 2013 does not use DirectWrite or GDI (it uses its own custom rendering), so Office 2013 doesn't work with MacType. If this bothers you you can use Office 2010 which uses GDI or Office 2016+ which uses DirectWrite.

- WPS has a built in defense that **UNLOADS** MacType automatically. The latest version has a workaround [here](https://github.com/snowie2000/mactype/wiki/WPS) thanks to wmjordan.

How to get registry mode back
-------------

It is no longer possible to enable registry mode via the wizard in Windows 10. 

We have a detailed guide on how you can enable the registry mode manually in [wiki](https://github.com/snowie2000/mactype/wiki/Enable-registry-mode-manually), get your screwdrivers ready before you head over to it.

How to build
-------------

Check how to build [document](https://github.com/snowie2000/mactype/blob/directwrite/doc/HOWTOBUILD.md)

