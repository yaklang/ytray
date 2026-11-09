import type { LaunchSettings, ProxyPreset } from '../shared/types'

export const CURRENT_CONFIGURATION_VERSION = 5
export const DEFAULT_PRESET_PROXY = 'http://127.0.0.1:8083'

export const blockedCustomPrefixes = [
  '--user-data-dir',
  '--remote-debugging-address',
  '--remote-debugging-port',
  '--load-extension',
  '--disable-extensions-except',
  '--proxy-server',
  '--no-proxy-server',
  '--proxy-pac-url',
  '--proxy-auto-detect',
  '--proxy-bypass-list'
]

export function defaultSettings(): LaunchSettings {
  return {
    configurationVersion: CURRENT_CONFIGURATION_VERSION,
    defaultRuntimeId: null,
    homeURL: 'chrome://newtab',
    proxyServer: '',
    proxyUsername: '',
    proxyPassword: '',
    presetProxyServer: DEFAULT_PRESET_PROXY,
    presetProxyUsername: '',
    presetProxyPassword: '',
    presetProxyRemark: '',
    presetProxyCheckTarget: '',
    recentProxyPresets: [],
    debugPort: 9222,
    restrictWebRTC: true,
    disableNotifications: true,
    ignoreCertificateErrors: true,
    useTestType: true,
    colorizeBrowserInstances: true,
    additionalFlags: '',
    profileRootPath: '',
    defaultPluginIds: [],
    dockBadge: '',
    themePreference: 'system',
    launchAtLogin: false,
    mcpAutoStart: true,
    checkForAppUpdates: true,
    autoConnectYakit: true,
    engineTarget: null
  }
}

function clonePreset(preset: ProxyPreset): ProxyPreset {
  return { ...preset }
}

export function cloneSettings(settings: LaunchSettings): LaunchSettings {
  return {
    ...settings,
    recentProxyPresets: settings.recentProxyPresets.map(clonePreset),
    defaultPluginIds: [...settings.defaultPluginIds],
    engineTarget: settings.engineTarget ? { ...settings.engineTarget } : null
  }
}

/** Fills omitted fields so older or hand-edited state files stay loadable. */
export function normalizeSettings(raw: Partial<LaunchSettings> | null | undefined): LaunchSettings {
  const base = defaultSettings()
  if (!raw || typeof raw !== 'object') return base
  const merged: LaunchSettings = { ...base, ...raw }
  if (!Array.isArray(merged.recentProxyPresets)) merged.recentProxyPresets = []
  if (!Array.isArray(merged.defaultPluginIds)) merged.defaultPluginIds = []
  if (!['system', 'light', 'dark'].includes(merged.themePreference)) merged.themePreference = 'system'
  // A stale or hand-edited 0/NaN used to persist and silently move every
  // instance to port 1024.
  merged.debugPort = Number.isFinite(merged.debugPort) && merged.debugPort >= 1024 && merged.debugPort <= 65535
    ? Math.trunc(merged.debugPort)
    : 9222
  return merged
}
