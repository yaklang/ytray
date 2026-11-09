# Windows third-party dependencies

YTray targets the .NET Framework 4.7.2 component included with Windows 10 1809 and later. It does not redistribute a .NET runtime. Managed HandyControl (MIT) and Newtonsoft.Json (MIT) dependencies are embedded using Costura.Fody (MIT).

YTray uses the unmodified WinSparkle 0.9.4 library under LGPL-2.1-or-later.
Source, release and build instructions: https://github.com/vslavik/winsparkle/tree/v0.9.4
Distribution: https://github.com/vslavik/winsparkle/releases/tag/v0.9.4

WinSparkle's COPYING and Expat's COPYING.expat accompany the installer in Licenses and are embedded in the portable executable. The native engine and these licenses are extracted to %LOCALAPPDATA%\YTray\UpdateEngine when the updater is loaded. The application embeds the upstream DLL without modification and checks its published distribution hash and pinned architecture-specific hash.

Desktop control is implemented by YTray against inbox Windows UI Automation, user32 and GDI APIs. Its worker runs the same YTray executable; no third-party computer-control engine is redistributed or downloaded.
