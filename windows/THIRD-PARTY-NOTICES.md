# Native updater dependencies

YTray uses the unmodified WinSparkle 0.9.4 library under LGPL-2.1-or-later.
Source, release and build instructions: https://github.com/vslavik/winsparkle/tree/v0.9.4
Distribution: https://github.com/vslavik/winsparkle/releases/tag/v0.9.4

WinSparkle's COPYING and Expat's COPYING.expat accompany the installer in Licenses and are embedded in the portable executable. The native engine and these licenses are extracted to %LOCALAPPDATA%\YTray\UpdateEngine when the updater is loaded. The application embeds the upstream DLL without modification and checks its published distribution hash and pinned architecture-specific hash.
