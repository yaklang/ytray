import { app } from 'electron'
import os from 'node:os'
import path from 'node:path'
import packageJson from '../../package.json'

/**
 * Linux follows the XDG base directory spec instead of Electron's default
 * ~/.config location, so Profiles/Runtimes/Plugins stay next to the rest of the
 * user data the other platforms keep in an application-support directory.
 */
const directoryName = app.isPackaged ? 'YTray' : 'YTrayDev'

export const appVersion: string = packageJson.version

export function dataRoot(): string {
  const xdg = process.env.XDG_DATA_HOME?.trim()
  return xdg && path.isAbsolute(xdg) ? xdg : path.join(os.homedir(), '.local', 'share')
}

export function dataDirectory(): string {
  return path.join(dataRoot(), directoryName)
}

export function profilesDirectory(): string {
  return path.join(dataDirectory(), 'Profiles')
}

export function pluginsDirectory(): string {
  return path.join(dataDirectory(), 'Plugins')
}

export function runtimesDirectory(): string {
  return path.join(dataDirectory(), 'Runtimes')
}

export function logsDirectory(): string {
  return path.join(dataDirectory(), 'Logs')
}

export function thumbnailsDirectory(): string {
  return path.join(dataDirectory(), 'Thumbnails')
}

export function proxyAuthDirectory(): string {
  return path.join(dataDirectory(), 'ProxyAuth')
}

export function statePath(): string {
  return path.join(dataDirectory(), 'state.json')
}

export function controlSocketPath(): string {
  return path.join(dataDirectory(), 'control.sock')
}

export function picturesDirectory(): string {
  const pictures = process.env.XDG_PICTURES_DIR?.trim()
  return path.join(pictures && path.isAbsolute(pictures) ? pictures : path.join(os.homedir(), 'Pictures'), 'YTray')
}

export function autostartPath(): string {
  const config = process.env.XDG_CONFIG_HOME?.trim()
  const root = config && path.isAbsolute(config) ? config : path.join(os.homedir(), '.config')
  return path.join(root, 'autostart', 'ytray.desktop')
}

export function applicationsDirectory(): string {
  return path.join(dataRoot(), 'applications')
}

/** The AppImage file on disk, which is what autostart and desktop entries must exec. */
export function launchExecutable(): string {
  return process.env.APPIMAGE?.trim() || process.execPath
}

export function sessionType(): 'wayland' | 'x11' | 'unknown' {
  const type = (process.env.XDG_SESSION_TYPE || '').toLowerCase()
  if (type === 'wayland' || type === 'x11') return type
  if (process.env.WAYLAND_DISPLAY) return 'wayland'
  if (process.env.DISPLAY) return 'x11'
  return 'unknown'
}
