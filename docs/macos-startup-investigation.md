# macOS startup investigation

Baseline: YTray **0.2.2**, commit `00da11da171216e1434440b074978d79d949549d`.
Reported symptom: launching YTray on macOS Tahoe makes the app disappear.

## Evidence and limits

- The released arm64 DMG contains a valid Developer ID signature and a stapled
  notarization ticket. Its bundled Sparkle framework passes deep signature validation.
- The unmodified released app reached `app.ready` and remained alive for 20 seconds
  on both first and subsequent launches on macOS 26.6.2 (25G83), arm64.
- The expanded published-binary check passed all four launches (two direct, two
  through Launch Services) on macOS 14 arm64, macOS 26.6.2 arm64 and macOS 26.6.1
  (25G76) Intel. See the `published-startup` jobs and `startup-*` evidence artifacts
  in [CI run 35232709329](https://github.com/yaklang/ytray/actions/runs/35232709329).
- Local macOS 14.1.2: release build succeeded; 93 Swift tests, 8 opt-in browser
  integration tests skipped, zero failures. Signed-release edge and management UI
  rendering also exited successfully.
- This does **not** establish the cause of the reported failure. The affected
  Mac's OS patch version, architecture, installed YTray version and crash report
  are still needed. A clean CI account does not cover existing user data, menu-bar
  utilities, display configurations or every Tahoe patch release.

YTray 0.2.2 uses `LSUIElement` and `.accessory` activation policy. It does not
normally keep a Dock icon or open a management window on every launch. Its
delegate also has no reopen handler, so a second Finder launch cannot recover a
hidden management window. An absent Dock icon alone is not evidence of process
termination.

## 0.2.3 changes

Manual startup now shows the management window. A Finder reopen restores the
same window, including when closed or minimized. Login/service launches remain
quiet, as does an explicit `--background` launch. The application delegate is
retained explicitly throughout the event loop. Startup logs identify menu-bar
and edge-widget setup, and `app.ready` is emitted after initialization finishes.

These changes fix the confirmed missing-UI/reopen behavior and improve lifecycle
diagnostics. They are not proof that the unobserved process crash on the user's
Mac is resolved. The crash-report investigation remains open if the process
actually exits.

## Collect evidence on the affected Mac

Immediately after reproducing the symptom, run from this checkout:

```sh
bash script/collect-macos-startup-diagnostics.sh /Applications/YTray.app
```

The script prints a local output directory. It records the OS/app version,
architecture, running YTray PIDs, signature validation, recent application logs
and YTray crash reports from the last seven days. It does not start or terminate
YTray, change settings, or collect browser profiles or `state.json`.

Interpret the evidence in this order:

1. A running YTray PID after the icon disappears means the UI is hidden rather
   than the process having crashed. Check the menu bar and edge widget.
2. If the process is gone, inspect the newest `YTray*.ips`: `exception`,
   `termination`, `faultingThread` and the faulting thread's frames identify the
   actual failure category. Do not infer it from an unrelated app's Tahoe crash.
3. Application logs locate startup progress. No `app.start` points toward a
   failure before the application logger; `store.ready` and `app.ready` narrow
   the later startup path. These markers alone do not establish a root cause.

## Regression coverage

The macOS workflow builds, tests, renders and packages the current source on
macOS 14 and macOS 26. The packaged app is started four times through both its
executable and Launch Services (`open`, as used for Finder launches). Each launch
must reach `app.ready` for its own PID and remain alive for 20 seconds. A clean
early exit also fails. Results, application output and crash reports are retained
as CI artifacts.

Current-build tests also require a visible manager after manual startup, send a
real Launch Services reopen to the same PID, and exercise closing/minimizing and
restoring the window. Login/service event handling is covered by unit tests.

For an investigation of a published binary, dispatch the macOS workflow with a
`release` input, for example `v0.2.2`. This additionally downloads and validates
the signed release on macOS 14 arm64, macOS 26 arm64 and macOS 26 Intel. That job
tests the original release, not a locally rebuilt substitute.

The startup script requires `--disposable-account` because it exercises real
startup, including login-item registration and application-data initialization.
It is intended for disposable CI accounts. The read-only collection script above
is the appropriate command on a user's Mac.
