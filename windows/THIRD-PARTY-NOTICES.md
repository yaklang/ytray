# Windows third-party dependencies

Release executables include the .NET 10 runtime and Windows Desktop runtime, distributed under the MIT license. Runtime source, license and third-party notices: https://github.com/dotnet/runtime and https://github.com/dotnet/windowsdesktop. This is an application-local, self-contained distribution; it does not install or modify a machine-wide .NET runtime.

YTray uses the unmodified WinSparkle 0.9.4 library under LGPL-2.1-or-later.
Source, release and build instructions: https://github.com/vslavik/winsparkle/tree/v0.9.4
Distribution: https://github.com/vslavik/winsparkle/releases/tag/v0.9.4

WinSparkle's COPYING and Expat's COPYING.expat accompany the installer in Licenses and are embedded in the portable executable. The native engine and these licenses are extracted to %LOCALAPPDATA%\YTray\UpdateEngine when the updater is loaded. The application embeds the upstream DLL without modification and checks its published distribution hash and pinned architecture-specific hash.

YTray computer control uses the unmodified Cua Driver 0.38.0 Windows x86_64 release from trycua/cua, source commit 85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19, under its MIT license. This upstream release is a prerelease integration candidate. Its LICENSE and THIRD_PARTY_NOTICES.md are included in the pinned embedded archive and installed beside the executable and helpers in the current user's runtime directory. The reference MCP schemas are generated from that same release's dump-docs output.

Source and license: https://github.com/trycua/cua/tree/85e2b1318bd77ccf3cfa88ccdf875a4bb7eaed19
Distribution: https://github.com/trycua/cua/releases/tag/cua-driver-rs-v0.38.0
