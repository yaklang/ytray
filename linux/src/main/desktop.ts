import fs from 'node:fs'
import path from 'node:path'
import { app } from 'electron'
import { applicationsDirectory, autostartPath, launchExecutable } from './paths'
import { log } from './diagnostics'

function quoteExec(value: string): string {
  return `"${value.replace(/(["\\`$])/g, '\\$1')}"`
}

export function appIconPath(): string {
  return app.isPackaged ? path.join(process.resourcesPath, 'icon.png') : path.join(app.getAppPath(), 'build', 'icon.png')
}

/**
 * Writes a per-instance desktop entry so GNOME/KDE can pair the Chromium window
 * (WM_CLASS = YTray-<badge>) with an application name in the dock and task switcher.
 * Wayland compositors generally ignore client-supplied icons; the entry still
 * gives the window a distinct, selectable application name.
 */
export function writeInstanceDesktopEntry(badge: string, windowClass: string, browserExecutable: string): string {
  const directory = applicationsDirectory()
  const file = path.join(directory, `ytray-${windowClass.toLowerCase()}.desktop`)
  const content = [
    '[Desktop Entry]',
    'Type=Application',
    'Version=1.0',
    `Name=YTray · ${badge}`,
    'Comment=YTray 隔离浏览器实例',
    `Exec=${quoteExec(browserExecutable)} --class=${windowClass} %U`,
    `Icon=${appIconPath()}`,
    `StartupWMClass=${windowClass}`,
    'Terminal=false',
    'NoDisplay=true',
    'Categories=Network;WebBrowser;',
    ''
  ].join('\n')
  try {
    fs.mkdirSync(directory, { recursive: true })
    fs.writeFileSync(file, content, { mode: 0o644 })
  } catch (error) {
    log.error('desktop.entry', error, file)
  }
  return file
}

export function removeInstanceDesktopEntry(windowClass: string): void {
  const file = path.join(applicationsDirectory(), `ytray-${windowClass.toLowerCase()}.desktop`)
  try {
    fs.rmSync(file, { force: true })
  } catch {
    /* best-effort cleanup */
  }
}

function autostartContent(): string {
  return [
    '[Desktop Entry]',
    'Type=Application',
    'Version=1.0',
    'Name=YTray',
    'Comment=多身份浏览器实例工作台',
    `Exec=${quoteExec(launchExecutable())} --background`,
    'Terminal=false',
    'X-GNOME-Autostart-enabled=true',
    'StartupNotify=false',
    ''
  ].join('\n')
}

export function isAutostartEnabled(): boolean {
  try {
    return fs.existsSync(autostartPath())
  } catch {
    return false
  }
}

export function setAutostartEnabled(enabled: boolean): boolean {
  // Source builds must never pollute the real desktop session (matches the
  // macOS "development builds do not touch Login Items" boundary).
  if (!app.isPackaged) return false
  const file = autostartPath()
  try {
    if (enabled) {
      fs.mkdirSync(path.dirname(file), { recursive: true })
      fs.writeFileSync(file, autostartContent(), { mode: 0o644 })
    } else {
      fs.rmSync(file, { force: true })
    }
    return isAutostartEnabled() === enabled
  } catch (error) {
    log.error('autostart', error)
    return false
  }
}
