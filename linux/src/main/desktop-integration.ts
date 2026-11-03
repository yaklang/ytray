import { execFile } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import { app } from 'electron'
import { applicationsDirectory, dataRoot, launchExecutable } from './paths'
import { appIconPath } from './desktop'
import { log } from './diagnostics'

function quoteExec(value: string): string {
  return `"${value.replace(/(["\\`$])/g, '\\$1')}"`
}

function run(command: string, args: string[]): void {
  execFile(command, args, { timeout: 5000 }, (error) => {
    if (error) log.info('desktop.command', `${command} failed: ${error.message}`)
  })
}

/**
 * AppImages are not "installed" by default, so no desktop entry or URI handler
 * exists outside the mount point. Clients that discover or launch YTray through
 * XDG (app id, ytray:// URL) therefore see nothing. Registering the same entry
 * the AppImage carries makes the Linux build behave like the macOS/Windows ones.
 */
export function ensureDesktopIntegration(): void {
  if (!app.isPackaged) return
  const directory = applicationsDirectory()
  const entry = path.join(directory, 'ytray.desktop')
  const iconDirectory = path.join(dataRoot(), 'icons', 'hicolor', '256x256', 'apps')
  const icon = path.join(iconDirectory, 'ytray.png')

  const content = [
    '[Desktop Entry]',
    'Type=Application',
    'Version=1.0',
    'Name=YTray',
    'Comment=多身份浏览器实例工作台',
    `Exec=${quoteExec(launchExecutable())} %U`,
    'Icon=ytray',
    'Terminal=false',
    'Categories=Network;WebBrowser;',
    'Keywords=browser;chromium;proxy;debug;',
    'StartupWMClass=YTray',
    'StartupNotify=true',
    'MimeType=x-scheme-handler/ytray;',
    ''
  ].join('\n')

  try {
    fs.mkdirSync(directory, { recursive: true })
    const previous = fs.existsSync(entry) ? fs.readFileSync(entry, 'utf8') : ''
    if (previous !== content) fs.writeFileSync(entry, content, { mode: 0o644 })

    fs.mkdirSync(iconDirectory, { recursive: true })
    const source = appIconPath()
    if (fs.existsSync(source)) fs.copyFileSync(source, icon)

    run('update-desktop-database', [directory])
    run('xdg-mime', ['default', 'ytray.desktop', 'x-scheme-handler/ytray'])
    log.info('desktop.integration', entry)
  } catch (error) {
    log.error('desktop.integration', error)
  }
}

/** Removes only the app-level entry; per-instance entries are managed separately. */
export function removeDesktopIntegration(): void {
  if (!app.isPackaged) return
  try {
    fs.rmSync(path.join(applicationsDirectory(), 'ytray.desktop'), { force: true })
  } catch {
    /* best-effort */
  }
}
