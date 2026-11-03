/**
 * Shared contract between the Electron main process and the renderer.
 * Mirrors the model names used by the macOS/Windows builds so behaviour and
 * persisted semantics stay comparable across the three implementations.
 */

export type BrowserKind =
  | 'chrome'
  | 'chromeBeta'
  | 'chromeCanary'
  | 'chromeForTesting'
  | 'chromium'
  | 'edge'

export type RuntimeSource = 'managed' | 'local' | 'system'

export type LaunchMode = 'quick' | 'isolated' | 'custom'

export type InstanceStatus = 'running' | 'stopped' | 'failed'

export type ThemePreference = 'system' | 'light' | 'dark'

export type LaunchPhase = 'idle' | 'preparing' | 'waiting' | 'succeeded'

export type ProxyCheckPhase = 'idle' | 'checking' | 'success' | 'failure'

export type UpdatePhase =
  | 'idle'
  | 'checking'
  | 'upToDate'
  | 'available'
  | 'downloading'
  | 'installing'
  | 'failed'

export interface BrowserRuntime {
  id: string
  name: string
  version: string
  architecture: string
  executablePath: string
  source: RuntimeSource
  browserKind?: BrowserKind
  createdAt: string
}

export interface BrowserPlugin {
  id: string
  name: string
  version: string
  path: string
  iconPath: string
  manifestVersion: number
  enabled: boolean
  pinToToolbar?: boolean
  createdAt: string
}

export interface ProxyPreset {
  id: string
  server: string
  remark: string
  username: string
  password: string
  lastUsedAt: string
}

export type EngineMode = 'engine' | 'legacy' | 'none'

/** Yakit/Yak engine that the launched browser instance connects back to. */
export interface BrowserEngineTarget {
  mode: EngineMode
  frontendId: string
  frontendName: string
  engineIdentityId: string
  engineInstanceId: string
  endpoint: string
}

export interface LaunchSettings {
  configurationVersion: number
  defaultRuntimeId: string | null
  homeURL: string
  proxyServer: string
  proxyUsername: string
  proxyPassword: string
  presetProxyServer: string
  presetProxyUsername: string
  presetProxyPassword: string
  presetProxyRemark: string
  presetProxyCheckTarget: string
  recentProxyPresets: ProxyPreset[]
  debugPort: number
  restrictWebRTC: boolean
  disableNotifications: boolean
  ignoreCertificateErrors: boolean
  useTestType: boolean
  colorizeBrowserInstances: boolean
  additionalFlags: string
  profileRootPath: string
  defaultPluginIds: string[]
  dockBadge: string
  themePreference: ThemePreference
  launchAtLogin: boolean
  mcpAutoStart: boolean
  checkForAppUpdates: boolean
  autoConnectYakit: boolean
  engineTarget: BrowserEngineTarget | null
}

export interface BrowserInstance {
  id: string
  name: string
  runtimeId: string
  runtimeName: string
  runtimeVersion?: string
  runtimeKind?: BrowserKind
  runtimeSource?: RuntimeSource
  mode: LaunchMode
  processId: number
  debugPort: number
  profilePath: string
  startURL: string
  startedAt: string
  status: InstanceStatus
  lastScreenshotPath?: string
  thumbnailPath?: string
  thumbnailUpdatedAt?: string
  lastPageTitle?: string
  lastPageURL?: string
  dockBadge?: string
  windowClass?: string
  engineTarget?: BrowserEngineTarget | null
  settingsSnapshot?: LaunchSettings
  pluginIds: string[]
}

export interface ExtensionRelease {
  version: string
  notes: string[]
  publishedAt: string
  artifacts: Array<{
    variant: string
    browser: string
    mode: string
    filename: string
    url: string
    sha256: string
    size: number | null
  }>
}

export interface ExtensionManifest {
  latest: string
  updatedAt: string
  versions: ExtensionRelease[]
}

export interface MirrorArtifact {
  os: string
  arch: string
  format: string
  url: string
  size: number | null
  sha256: string
}

export interface MirrorVersion {
  version: string
  artifacts: MirrorArtifact[]
}

export interface ProxyCheckDetail {
  target: string
  success: boolean
  message: string
  elapsedMs: number
}

export interface ProxyCheckReport {
  details: ProxyCheckDetail[]
  successCount: number
  success: boolean
  message: string
}

export interface DownloadProgress {
  percent: number
  message: string
  bytesReceived: number
  totalBytes: number | null
}

export interface PlatformInfo {
  platform: 'linux'
  sessionType: 'wayland' | 'x11' | 'unknown'
  dataDirectory: string
  version: string
  isPackaged: boolean
  appImagePath: string | null
  chromiumClassIdentity: boolean
  notes: string[]
}

export interface UpdateState {
  phase: UpdatePhase
  message: string
  availableVersion: string | null
  currentVersion: string
  releaseNotes: string | null
  lastCheck: string | null
  progressPercent: number
}

export interface McpState {
  available: boolean
  autoStart: boolean
  message: string
  endpoint: string
}

export interface ExtensionState {
  manifest: ExtensionManifest | null
  installedVersion: string | null
  updateAvailable: boolean
  message: string
  installing: boolean
  percent: number
}

export interface LaunchState {
  phase: LaunchPhase
  message: string
  instanceId: string | null
  restoringInstanceId: string | null
}

export interface InstallationState {
  installing: boolean
  percent: number
  message: string
}

export interface StateSnapshot {
  runtimes: BrowserRuntime[]
  plugins: BrowserPlugin[]
  instances: BrowserInstance[]
  settings: LaunchSettings
  defaultRuntimeId: string | null
  widget: { pinned: boolean }
  mcp: McpState
  extension: ExtensionState
  update: UpdateState
  launch: LaunchState
  installation: InstallationState
  proxyCheck: {
    phase: ProxyCheckPhase
    message: string
    report: ProxyCheckReport | null
  }
  availableVersions: MirrorVersion[]
  errorMessage: string | null
  platform: PlatformInfo
}

export type RendererCommand =
  | { type: 'setSettings'; patch: Partial<LaunchSettings> }
  | { type: 'saveSettings' }
  | { type: 'refreshBrowsers' }
  | { type: 'addLocalRuntime'; path: string }
  | { type: 'selectDefaultRuntime'; runtimeId: string }
  | { type: 'removeRuntime'; runtimeId: string }
  | { type: 'uninstallRuntime'; runtimeId: string }
  | { type: 'refreshRuntimeVersions' }
  | { type: 'installRuntime'; version: string }
  | { type: 'addPlugins'; paths: string[] }
  | { type: 'updatePlugin'; pluginId: string; patch: Partial<BrowserPlugin> }
  | { type: 'removePlugin'; pluginId: string }
  | { type: 'refreshExtensionManifest' }
  | { type: 'installExtension'; version?: string }
  | { type: 'launchQuick'; useProxy: boolean }
  | { type: 'launchCustom'; settings: Partial<LaunchSettings>; pluginIds: string[]; isolated: boolean }
  | { type: 'stopInstance'; instanceId: string }
  | { type: 'focusInstance'; instanceId: string }
  | { type: 'captureInstance'; instanceId: string }
  | { type: 'restoreInstance'; instanceId: string }
  | { type: 'removeHistory'; instanceId: string }
  | { type: 'removeAllHistory' }
  | { type: 'renameHistory'; instanceId: string; name: string }
  | { type: 'revealProfile'; instanceId: string }
  | { type: 'checkProxy' }
  | { type: 'rememberPresetProxy' }
  | { type: 'selectProxyPreset'; presetId: string }
  | { type: 'removeProxyPreset'; presetId: string }
  | { type: 'openScreenshot'; instanceId: string }
  | { type: 'setWidgetPinned'; pinned: boolean }
  | { type: 'startMcp' }
  | { type: 'stopMcp' }
  | { type: 'checkForUpdates' }
  | { type: 'installUpdate' }
  | { type: 'openExternal'; url: string }
  | { type: 'openManager' }
  | { type: 'quit' }

export interface CommandResult {
  ok: boolean
  error?: string
  message?: string
}
