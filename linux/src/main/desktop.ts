import fs from 'node:fs'
import path from 'node:path'
import { app } from 'electron'
import { applicationsDirectory, autostartPath, dataDirectory, launchExecutable } from './paths'
import { identityColorHex, identityForegroundHex } from './identity'
import { log } from './diagnostics'

function quoteExec(value: string): string {
  return `"${value.replace(/(["\\`$])/g, '\\$1')}"`
}

export function appIconPath(): string {
  return app.isPackaged ? path.join(process.resourcesPath, 'icon.png') : path.join(app.getAppPath(), 'build', 'icon.png')
}

function badgeIconPath(windowClass: string): string {
  return path.join(dataDirectory(), 'Icons', `${windowClass.toLowerCase()}.svg`)
}

/**
 * Linux has no reliable way to repaint a Chromium window icon (Wayland ignores
 * client icons entirely), so the per-instance identity is carried by a generated
 * SVG that the desktop entry and most docks display instead.
 *
 * The badge is positioned with plain baseline math: `dominant-baseline` and `dy`
 * are ignored by several SVG renderers (Qt's, used by common Wayland shells),
 * which left the letter visibly off-centre. The values below are measured
 * against DejaVu Sans Bold so the glyph box itself is centred in the tile.
 */
export function writeInstanceIcon(badge: string, windowClass: string): string {
  const file = badgeIconPath(windowClass)
  const background = identityColorHex(badge)
  const foreground = identityForegroundHex(badge)
  const wide = badge.length > 1
  const fontSize = wide ? 104 : 132
  const baseline = wide ? 168 : 178.5
  // Two letters carry slightly asymmetric side bearings, so the group is nudged
  // back to the optical centre of the tile.
  const x = wide ? 130 : 128
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256">
  <rect x="8" y="8" width="240" height="240" rx="56" fill="${background}"/>
  <rect x="20" y="20" width="216" height="216" rx="46" fill="none" stroke="${foreground}" stroke-opacity="0.28" stroke-width="4"/>
  <text x="${x}" y="${baseline}" text-anchor="middle" font-family="DejaVu Sans"
        font-weight="bold" font-size="${fontSize}" fill="${foreground}">${badge}</text>
</svg>
`
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 })
    fs.writeFileSync(file, svg, { mode: 0o644 })
  } catch (error) {
    log.error('desktop.icon', error, file)
  }
  return file
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
  const icon = writeInstanceIcon(badge, windowClass)
  const content = [
    '[Desktop Entry]',
    'Type=Application',
    'Version=1.0',
    `Name=YTray · ${badge}`,
    'Comment=YTray 隔离浏览器实例',
    `Exec=${quoteExec(browserExecutable)} --class=${windowClass} %U`,
    `Icon=${icon}`,
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
    fs.rmSync(badgeIconPath(windowClass), { force: true })
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
