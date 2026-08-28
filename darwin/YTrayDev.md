# YTrayDev local preview

Build the isolated macOS preview with:

```bash
./script/package-macos.sh --dev
```

The resulting `dist/darwin-<arch>-dev/YTrayDev.app` uses bundle identifier
`io.yaklang.ytray.dev`, runs as the distinct `YTrayDev` process, and stores all
settings, profiles, logs, runtimes, and
plugins below `~/Library/Application Support/YTrayDev`. It does not migrate or
write the production YTray directory.

For safety, YTrayDev disables application updates and launch at login. The
instance-color experiment is gated by an Info.plist flag that is present only
in the development bundle. New profiles receive deterministic Chrome theme
colors matching A/B/C; restored profiles keep their existing theme. YTrayDev
also skips automatic installation of the bundled browser extension so the
system Chrome can be used immediately for this focused color preview.

Rollback is recoverable and keeps preview data by default:

```bash
./script/rollback-macos-dev.sh
```

Validate the exact rollback targets without changing anything:

```bash
./script/rollback-macos-dev.sh --dry-run
```

To move both the app and its isolated data to the Trash:

```bash
./script/rollback-macos-dev.sh --trash-data
```
