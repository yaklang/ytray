import { EventEmitter } from 'node:events'
import fs from 'node:fs'
import path from 'node:path'
import { app, dialog, nativeTheme } from 'electron'
import type {
  BrowserEngineTarget,
  BrowserInstance,
  BrowserPlugin,
  BrowserRuntime,
  ExtensionRelease,
  LaunchMode,
  LaunchSettings,
  MirrorVersion,
  PlatformInfo,
  ProxyCheckReport,
  StateSnapshot,
  UpdateState
} from '../shared/types'
import { ensureDirectories, loadState, saveState, type PersistedState } from './persistence'
import { cloneSettings, DEFAULT_PRESET_PROXY } from './settings'
import { inspectBrowser, discoverSystemBrowsers } from './discovery'
import { badgeFor, launchBrowser, type LaunchResult } from './launcher'
import {
  capture,
  closeBrowser,
  currentPageState,
  focusInstance,
  navigate,
  waitUntilReady
} from './cdp'
import { isInstanceProcess, isProcessAlive, openInFileManager, terminateProcess } from './process'
import {
  cleanupOldPluginVersions,
  clearManagedExtensionRemoved,
  enterpriseArtifact,
  fetchExtensionManifest,
  installExtension,
  MANAGED_EXTENSION_NAME,
  managedPluginOptOutPath,
  markManagedExtensionRemoved,
  pluginDirectory,
  readPluginManifest,
  resolveExtensionRoot,
  shouldInstallBundled
} from './extension-installer'
import { ensureExtensionsLoaded, ExtensionLoadingError } from './extension-service'
import { extractZip } from './archive'
import { sha256File } from './download'
import { removeProxyAuthExtension } from './proxy-auth'
import { isCompatible, fetchRuntimeVersions, installRuntime, removeRuntimeFiles } from './runtime-installer'
import { findPluginDirectories } from './plugin-discovery'
import { checkProxy } from './proxy-check'
import { splitProxy } from './proxy-address'
import { McpServer, MCP_ENDPOINT, type McpBackend } from './mcp'
import { NativeControlServer } from './control-socket'
import { isAutostartEnabled, removeInstanceDesktopEntry, setAutostartEnabled } from './desktop'
import {
  appVersion,
  controlSocketCompatPath,
  controlSocketPath,
  dataDirectory,
  sessionType,
  thumbnailsDirectory
} from './paths'
import { compareVersions } from './versions'
import { YTrayError, errorMessage } from './errors'
import { log } from './diagnostics'
import { applyUpdate, checkForUpdate, downloadUpdate, type AppRelease } from './updater'
import { ytrayId } from './ids'
import { findEngines, logEngineSelection, resolveForLaunch } from './engine'

const PAGE_REFRESH_INTERVAL = 3000
const THUMBNAIL_REFRESH_INTERVAL = 12000
const YAKIT_APPROVAL_LIFETIME = 2 * 60 * 1000

interface YakitApproval {
  expiresAt: number
  requestId?: string
}

function emptyEngineTarget(mode: BrowserEngineTarget['mode']): BrowserEngineTarget {
  return { mode, frontendId: '', frontendName: '', engineIdentityId: '', engineInstanceId: '', endpoint: '' }
}

export class InstanceStore extends EventEmitter {
  readonly state: PersistedState
  private readonly launches = new Map<string, LaunchResult>()
  private readonly thumbnailInFlight = new Set<string>()
  private readonly lastThumbnailAttempt = new Map<string, number>()
  private readonly yakitApprovals = new Map<string, YakitApproval>()
  private widgetPinned = false
  private maintenanceTimer: NodeJS.Timeout | null = null
  private launchToken: string | null = null
  private disposed = false

  readonly mcp: McpServer
  private readonly control: NativeControlServer

  private launchState: StateSnapshot['launch'] = {
    phase: 'idle',
    message: '',
    instanceId: null,
    restoringInstanceId: null
  }
  private mcpState: StateSnapshot['mcp'] = {
    available: false,
    autoStart: true,
    message: '等待 MCP 服务启动',
    endpoint: MCP_ENDPOINT
  }
  private extensionState: StateSnapshot['extension'] = {
    manifest: null,
    installedVersion: null,
    updateAvailable: false,
    message: '',
    installing: false,
    percent: 0
  }
  private updateState: UpdateState = {
    phase: 'idle',
    message: '',
    availableVersion: null,
    currentVersion: appVersion,
    releaseNotes: null,
    lastCheck: null,
    progressPercent: 0
  }
  private proxyCheckState: StateSnapshot['proxyCheck'] = {
    phase: 'idle',
    message: '',
    report: null
  }
  private errorMessage: string | null = null
  private availableVersions: MirrorVersion[] = []
  private pendingRelease: AppRelease | null = null
  private installProgress: { installing: boolean; percent: number; message: string } = {
    installing: false,
    percent: 0,
    message: ''
  }

  constructor() {
    super()
    ensureDirectories()
    this.state = loadState()
    this.mcpState.autoStart = this.state.settings.mcpAutoStart

    const backend: McpBackend = {
      runtimes: () => this.state.runtimes,
      instances: () => this.state.instances,
      defaultRuntimeId: () => this.state.settings.defaultRuntimeId,
      openNew: (options) => this.mcpOpen({ ...options }),
      restore: (instanceId) => this.mcpOpen({ restoreId: instanceId }),
      focus: (instanceId) => this.mcpFocus(instanceId),
      stop: (instanceId) => this.mcpStop(instanceId),
      isLaunching: () => this.isLaunching,
      onStatus: (available, message) => {
        this.mcpState = { ...this.mcpState, available, message }
        this.emitChange()
      }
    }

    this.mcp = new McpServer(backend)
    this.control = new NativeControlServer({
      listHistory: () =>
        this.state.instances
          .filter((instance) => instance.status !== 'running')
          .sort((a, b) => (a.startedAt < b.startedAt ? 1 : -1))
          .map((instance) => ({
            id: instance.id,
            name: instance.name,
            runtime: [instance.runtimeName, instance.runtimeVersion].filter(Boolean).join(' '),
            status: instance.status,
            startUrl: instance.startURL,
            pageTitle: instance.lastPageTitle ?? '',
            pageUrl: instance.lastPageURL ?? '',
            badge: instance.dockBadge ?? '',
            startedAt: Date.parse(instance.startedAt) || 0
          })),
      restore: (instanceId) => {
        if (this.isLaunching) return { ok: false, error: 'YTray 正在启动另一个浏览器实例' }
        const instance = this.state.instances.find((item) => item.id === instanceId && item.status !== 'running')
        if (!instance) return { ok: false, error: '没有找到可恢复的浏览器实例' }
        void this.restoreHistory(instance)
        return { ok: true }
      },
      claim: (instanceId, requestId) => this.claimYakitLaunch(instanceId, requestId)
    })

    this.refreshSystemBrowsers()
    this.refreshProcessStates()
    this.normalizeLoadedState()
    this.installBundledExtensionIfNeeded().catch(() => undefined)
    this.startMaintenance()
    log.info(
      'store.ready',
      `runtimes=${this.state.runtimes.length}; plugins=${this.state.plugins.length}; instances=${this.state.instances.length}`
    )
  }

  get isLaunching(): boolean {
    return this.launchState.phase === 'preparing' || this.launchState.phase === 'waiting'
  }

  get settings(): LaunchSettings {
    return this.state.settings
  }

  getInstalledManagedVersion(): string | null {
    return this.state.plugins.find((plugin) => plugin.name === MANAGED_EXTENSION_NAME)?.version ?? null
  }

  snapshot(): StateSnapshot {
    return {
      runtimes: [...this.state.runtimes],
      plugins: [...this.state.plugins],
      instances: [...this.state.instances],
      settings: cloneSettings(this.state.settings),
      defaultRuntimeId: this.state.settings.defaultRuntimeId,
      widget: { pinned: this.widgetPinned },
      mcp: { ...this.mcpState, autoStart: this.state.settings.mcpAutoStart },
      extension: {
        ...this.extensionState,
        installedVersion: this.getInstalledManagedVersion(),
        updateAvailable: this.isExtensionUpdateAvailable()
      },
      update: { ...this.updateState },
      launch: { ...this.launchState },
      installation: this.installationState,
      proxyCheck: { ...this.proxyCheckState },
      availableVersions: [...this.availableVersions],
      errorMessage: this.errorMessage,
      platform: this.platformInfo()
    }
  }

  platformInfo(): PlatformInfo {
    return {
      platform: 'linux',
      sessionType: sessionType(),
      dataDirectory: dataDirectory(),
      version: appVersion,
      isPackaged: app.isPackaged,
      appImagePath: process.env.APPIMAGE ?? null,
      chromiumClassIdentity: true,
      control: {
        listening: this.control.listening,
        path: controlSocketPath(),
        compatPath: controlSocketCompatPath()
      },
      notes: [
        sessionType() === 'wayland'
          ? 'Wayland 不允许应用自行定位窗口，贴边面板已改为托盘面板。'
          : 'X11 下支持通过 WM_CLASS 区分实例窗口。',
        '每实例图标标签仅在与 .desktop 条目匹配的桌面环境中生效。'
      ]
    }
  }

  private emitChange(): void {
    if (!this.disposed) this.emit('change', this.snapshot())
  }

  private save(): void {
    if (this.disposed) return
    saveState(this.state)
    this.emitChange()
  }

  // ---------------------------------------------------------------- runtimes

  refreshSystemBrowsers(): void {
    const discovered = discoverSystemBrowsers()
    const discoveredPaths = new Set(discovered.map((runtime) => runtime.executablePath))
    this.state.runtimes = this.state.runtimes.filter(
      (runtime) => runtime.source !== 'system' || discoveredPaths.has(runtime.executablePath)
    )
    for (const runtime of discovered) this.upsertRuntime(runtime, false)
    if (!this.state.settings.defaultRuntimeId || !this.state.runtimes.some((runtime) => runtime.id === this.state.settings.defaultRuntimeId)) {
      this.state.settings.defaultRuntimeId =
        this.state.runtimes.find((runtime) => runtime.source === 'system')?.id ?? this.state.runtimes[0]?.id ?? null
    }
    this.save()
  }

  private upsertRuntime(runtime: BrowserRuntime, persist = true): BrowserRuntime {
    const existing = this.state.runtimes.find((item) => item.executablePath === runtime.executablePath)
    if (existing) {
      runtime.id = existing.id
      runtime.createdAt = existing.createdAt
      this.state.runtimes[this.state.runtimes.indexOf(existing)] = runtime
    } else {
      this.state.runtimes.push(runtime)
    }
    if (!this.state.settings.defaultRuntimeId) this.state.settings.defaultRuntimeId = runtime.id
    if (persist) this.save()
    return runtime
  }

  addLocalRuntime(selectedPath: string): BrowserRuntime {
    const runtime = inspectBrowser(selectedPath)
    if (!runtime) throw new YTrayError('invalidExecutable', selectedPath)
    const saved = this.upsertRuntime(runtime)
    log.info('runtime.add', `${saved.name} ${saved.version} ${saved.executablePath}`)
    return saved
  }

  selectDefaultRuntime(runtimeId: string): void {
    if (!this.state.runtimes.some((runtime) => runtime.id === runtimeId)) return
    this.state.settings.defaultRuntimeId = runtimeId
    this.save()
  }

  removeRuntime(runtimeId: string): void {
    if (this.state.instances.some((instance) => instance.runtimeId === runtimeId && instance.status === 'running')) {
      throw new Error('该运行时仍有实例正在运行')
    }
    const runtime = this.state.runtimes.find((item) => item.id === runtimeId)
    if (!runtime) return
    this.state.runtimes = this.state.runtimes.filter((item) => item.id !== runtimeId)
    if (this.state.settings.defaultRuntimeId === runtimeId) {
      this.state.settings.defaultRuntimeId = this.state.runtimes[0]?.id ?? null
    }
    this.save()
  }

  uninstallRuntime(runtimeId: string): void {
    const runtime = this.state.runtimes.find((item) => item.id === runtimeId)
    if (!runtime) return
    if (runtime.source !== 'managed') throw new Error('只有 YTray 安装的 Chrome for Testing 可以卸载')
    if (this.state.instances.some((instance) => instance.runtimeId === runtimeId && instance.status === 'running')) {
      throw new Error('该浏览器仍有实例正在运行，请先停止实例')
    }
    removeRuntimeFiles(runtime)
    this.removeRuntime(runtimeId)
  }

  async refreshRuntimeVersions(): Promise<void> {
    this.errorMessage = null
    this.availableVersions = await fetchRuntimeVersions()
    this.save()
  }

  async installRuntimeVersion(version: string): Promise<void> {
    const target = this.availableVersions.find((item) => item.version === version)
    if (!target) throw new Error('版本清单中没有该版本')
    if (!isCompatible(target)) throw new Error(`该版本没有 ${process.arch} 的 Linux 安装包`)
    this.installProgress = { installing: true, percent: 0, message: `正在安装 Chrome for Testing ${version}…` }
    this.emitChange()
    try {
      const runtime = await installRuntime(target, (percent, message) => {
        this.installProgress = { installing: true, percent, message }
        this.emitChange()
      })
      this.upsertRuntime(runtime)
      this.installProgress = { installing: false, percent: 100, message: `Chrome for Testing ${version} 安装完成` }
    } catch (error) {
      this.installProgress = { installing: false, percent: 0, message: errorMessage(error) }
      throw error
    } finally {
      this.emitChange()
    }
  }

  get installationState(): { installing: boolean; percent: number; message: string } {
    return { ...this.installProgress }
  }

  // ----------------------------------------------------------------- plugins

  addPlugins(roots: string[]): number {
    const directories = findPluginDirectories(roots)
    let count = 0
    for (const directory of directories) {
      const manifest = readPluginManifest(directory)
      if (!manifest?.name) continue
      const existing = this.state.plugins.find(
        (plugin) => path.resolve(plugin.path) === path.resolve(directory)
      )
      const plugin: BrowserPlugin = {
        id: existing?.id ?? ytrayId(),
        name: manifest.name,
        version: manifest.version ?? '',
        path: directory,
        iconPath: existing?.iconPath ?? '',
        manifestVersion: manifest.manifest_version ?? 0,
        enabled: existing?.enabled ?? true,
        pinToToolbar: existing?.pinToToolbar ?? manifest.name === MANAGED_EXTENSION_NAME,
        createdAt: existing?.createdAt ?? new Date().toISOString()
      }
      if (existing) this.state.plugins[this.state.plugins.indexOf(existing)] = plugin
      else this.state.plugins.push(plugin)
      count += 1
    }
    if (count === 0) throw new YTrayError('invalidPlugin', roots[0] ?? '未选择目录')
    this.synchronizeDefaultPluginIds()
    this.save()
    return count
  }

  updatePlugin(pluginId: string, patch: Partial<BrowserPlugin>): void {
    const plugin = this.state.plugins.find((item) => item.id === pluginId)
    if (!plugin) return
    Object.assign(plugin, patch, { id: plugin.id })
    this.synchronizeDefaultPluginIds()
    this.save()
  }

  removePlugin(pluginId: string): void {
    const plugin = this.state.plugins.find((item) => item.id === pluginId)
    if (!plugin) return
    if (plugin.name === MANAGED_EXTENSION_NAME) {
      markManagedExtensionRemoved()
    }
    this.state.plugins = this.state.plugins.filter((item) => item.id !== pluginId)
    this.synchronizeDefaultPluginIds()
    this.save()
  }

  private synchronizeDefaultPluginIds(): void {
    this.state.settings.defaultPluginIds = this.state.plugins.filter((plugin) => plugin.enabled).map((plugin) => plugin.id)
  }

  private async installBundledExtensionIfNeeded(force = false): Promise<void> {
    if (!app.isPackaged) return
    if (!force && fs.existsSync(managedPluginOptOutPath())) return
    const root = path.join(process.resourcesPath, 'bundled-extension')
    const descriptorPath = path.join(root, 'bundled-extension.json')
    const archivePath = path.join(root, 'yakit-browser-agent.zip')
    if (!fs.existsSync(descriptorPath) || !fs.existsSync(archivePath)) return
    try {
      const descriptor = JSON.parse(fs.readFileSync(descriptorPath, 'utf8')) as {
        version?: string
        sha256?: string
        size?: number
        variant?: string
      }
      if (!descriptor.version) return
      if (descriptor.variant && descriptor.variant !== 'chrome-enterprise') return
      if (!shouldInstallBundled(descriptor.version, this.getInstalledManagedVersion(), force)) return

      const stat = fs.statSync(archivePath)
      if (descriptor.size && stat.size !== descriptor.size) {
        log.error('extension.bundled', new Error('size mismatch'), 'bundled archive rejected')
        return
      }
      if (descriptor.sha256) {
        const digest = await sha256File(archivePath)
        if (digest.toLowerCase() !== descriptor.sha256.toLowerCase()) {
          log.error('extension.bundled', new Error('sha256 mismatch'), 'bundled archive rejected')
          return
        }
      }

      const destination = pluginDirectory(descriptor.version)
      const staging = `${destination}.partial-${Date.now()}`
      fs.rmSync(staging, { recursive: true, force: true })
      await extractZip(archivePath, staging, { errorCode: 'extensionInstallFailed' })
      const extracted = resolveExtensionRoot(staging)
      fs.rmSync(destination, { recursive: true, force: true })
      fs.mkdirSync(path.dirname(destination), { recursive: true })
      fs.renameSync(extracted, destination)
      fs.rmSync(staging, { recursive: true, force: true })

      const manifest = readPluginManifest(destination)
      if (!manifest?.name) return
      this.registerManagedPlugin(destination, manifest.name, manifest.version ?? descriptor.version, manifest.manifest_version ?? 3)
      clearManagedExtensionRemoved()
      cleanupOldPluginVersions(descriptor.version)
      this.save()
      log.info('extension.bundled', `installed ${descriptor.version}`)
    } catch (error) {
      log.error('extension.bundled', error)
    }
  }

  private registerManagedPlugin(directory: string, name: string, version: string, manifestVersion: number): void {
    const existing = this.state.plugins.find((plugin) => plugin.name === MANAGED_EXTENSION_NAME)
    const plugin: BrowserPlugin = {
      id: existing?.id ?? ytrayId(),
      name,
      version,
      path: directory,
      iconPath: existing?.iconPath ?? '',
      manifestVersion,
      enabled: existing?.enabled ?? true,
      pinToToolbar: true,
      createdAt: existing?.createdAt ?? new Date().toISOString()
    }
    if (existing) this.state.plugins[this.state.plugins.indexOf(existing)] = plugin
    else this.state.plugins.push(plugin)
    this.synchronizeDefaultPluginIds()
  }

  async refreshExtensionManifest(): Promise<void> {
    this.extensionState = { ...this.extensionState, message: '正在获取插件版本…' }
    this.emitChange()
    try {
      this.extensionState = {
        ...this.extensionState,
        manifest: await fetchExtensionManifest(),
        message: ''
      }
    } catch (error) {
      this.extensionState = { ...this.extensionState, message: errorMessage(error) }
      throw error
    } finally {
      this.emitChange()
    }
  }

  isExtensionUpdateAvailable(): boolean {
    const manifest = this.extensionState.manifest
    if (!manifest) return false
    const latest = manifest.versions[0] ?? { version: manifest.latest, notes: [], publishedAt: '', artifacts: [] }
    if (!enterpriseArtifact(latest)) return false
    const installed = this.getInstalledManagedVersion()
    if (!installed) return true
    return compareVersions(latest.version, installed) > 0
  }

  async installExtensionVersion(version?: string): Promise<void> {
    if (this.extensionState.installing) return
    const manifest = this.extensionState.manifest
    const target: ExtensionRelease | undefined =
      manifest?.versions.find((item) => item.version === version) ?? manifest?.versions[0]
    if (!target) throw new YTrayError('extensionInstallFailed', '没有可安装的插件版本，请先刷新插件清单')

    this.extensionState = { ...this.extensionState, installing: true, percent: 0, message: `正在准备 Yakit 插件 ${target.version}…` }
    this.emitChange()
    try {
      const directory = await installExtension(target, (percent, message) => {
        this.extensionState = { ...this.extensionState, percent, message }
        this.emitChange()
      })
      const file = readPluginManifest(directory)
      if (!file?.name) throw new YTrayError('invalidPlugin', directory)
      this.registerManagedPlugin(directory, file.name, file.version ?? target.version, file.manifest_version ?? 3)
      clearManagedExtensionRemoved()
      cleanupOldPluginVersions(target.version)
      this.extensionState = {
        ...this.extensionState,
        installing: false,
        percent: 100,
        message: `Yakit 插件 ${target.version} 安装完成`
      }
      this.save()
    } catch (error) {
      this.extensionState = { ...this.extensionState, installing: false, message: errorMessage(error) }
      throw error
    } finally {
      this.emitChange()
    }
  }

  // ---------------------------------------------------------------- settings

  updateSettings(patch: Partial<LaunchSettings>): void {
    const next = { ...patch }
    if (next.debugPort !== undefined) {
      const value = Number(next.debugPort)
      next.debugPort = Number.isFinite(value) && value >= 1024 && value <= 65535 ? Math.trunc(value) : 9222
    }
    Object.assign(this.state.settings, next)
    if (patch.themePreference) nativeTheme.themeSource = patch.themePreference
    if (patch.launchAtLogin !== undefined) {
      setAutostartEnabled(patch.launchAtLogin)
      this.state.settings.launchAtLogin = isAutostartEnabled()
    }
    if (patch.mcpAutoStart !== undefined) {
      if (patch.mcpAutoStart && !this.mcp.running) this.startMcp()
      if (!patch.mcpAutoStart && this.mcp.running) this.stopMcp()
    }
    this.save()
  }

  saveSettings(): void {
    this.save()
  }

  setWidgetPinned(pinned: boolean): void {
    this.widgetPinned = pinned
    this.emitChange()
  }

  get isWidgetPinned(): boolean {
    return this.widgetPinned
  }

  private async resolveEngineTarget(
    configuration: LaunchSettings,
    restoring: boolean
  ): Promise<{ cancelled: boolean; target: BrowserEngineTarget | null }> {
    if (!this.state.settings.autoConnectYakit) {
      return { cancelled: false, target: emptyEngineTarget('none') }
    }
    let online: BrowserEngineTarget[] = []
    try {
      online = await findEngines(configuration.engineTarget ?? null)
    } catch (error) {
      log.error('engine.discovery', error)
    }
    const resolved = resolveForLaunch(configuration.engineTarget ?? null, online, restoring)
    if (resolved) return { cancelled: false, target: resolved }

    const labels = online.map((engine) => {
      let host = engine.endpoint
      try {
        host = new URL(engine.endpoint).host
      } catch {
        /* keep the raw endpoint */
      }
      return `${engine.frontendName} · ${host}`
    })
    const buttons = [...labels, '仅启动浏览器', '取消']
    const { response } = await dialog.showMessageBox({
      type: 'question',
      title: '选择 Yakit 引擎',
      message: `发现 ${online.length} 个可用引擎`,
      detail: '选择结果只对本次启动生效。',
      buttons,
      defaultId: 0,
      cancelId: buttons.length - 1,
      noLink: true
    })
    if (response === buttons.length - 1) return { cancelled: true, target: null }
    if (response === online.length) return { cancelled: false, target: emptyEngineTarget('none') }
    return { cancelled: false, target: online[response] ?? emptyEngineTarget('legacy') }
  }

  private rememberYakitLaunch(instanceId: string): void {
    if (!this.state.settings.autoConnectYakit) return
    this.yakitApprovals.set(instanceId, { expiresAt: Date.now() + YAKIT_APPROVAL_LIFETIME })
  }

  /** First-party client pairing approval; mirrors claim_launch_approval. */
  claimYakitLaunch(instanceId: string, requestId: string): { approved: boolean; reason: string } {
    if (!this.state.settings.autoConnectYakit) return { approved: false, reason: 'disabled' }
    const approval = this.yakitApprovals.get(instanceId)
    if (!approval) return { approved: false, reason: 'not_found' }
    if (Date.now() > approval.expiresAt) {
      this.yakitApprovals.delete(instanceId)
      return { approved: false, reason: 'expired' }
    }
    if (!this.state.instances.some((item) => item.id === instanceId && item.status === 'running')) {
      this.yakitApprovals.delete(instanceId)
      return { approved: false, reason: 'not_running' }
    }
    if (!approval.requestId) {
      approval.requestId = requestId
      return { approved: true, reason: '' }
    }
    return approval.requestId === requestId
      ? { approved: true, reason: '' }
      : { approved: false, reason: 'already_claimed' }
  }

  // ------------------------------------------------------------------ launch

  private selectedPlugins(ids: string[]): BrowserPlugin[] {
    return this.state.plugins.filter((plugin) => ids.includes(plugin.id) && plugin.enabled)
  }

  quickLaunchConfiguration(useProxy: boolean): LaunchSettings | null {
    const settings = cloneSettings(this.state.settings)
    settings.homeURL = this.state.settings.homeURL
    if (useProxy) {
      try {
        const server = this.rememberPresetProxy()
        settings.proxyServer = server
        settings.proxyUsername = this.state.settings.presetProxyUsername
        settings.proxyPassword = this.state.settings.presetProxyPassword
      } catch (error) {
        this.errorMessage = errorMessage(error)
        return null
      }
    } else {
      settings.proxyServer = ''
      settings.proxyUsername = ''
      settings.proxyPassword = ''
    }
    return settings
  }

  async launch(
    mode: LaunchMode,
    options: { settings?: LaunchSettings; pluginIds?: string[]; restoring?: BrowserInstance; badge?: string } = {}
  ): Promise<void> {
    if (this.isLaunching) throw new Error('YTray 正在启动另一个浏览器实例')
    this.errorMessage = null
    const token = ytrayId()
    this.launchToken = token
    this.launchState = {
      phase: 'preparing',
      message: '正在准备浏览器…',
      instanceId: null,
      restoringInstanceId: options.restoring?.id ?? null
    }
    this.emitChange()

    let configuration = cloneSettings(options.settings ?? this.state.settings)
    if (mode === 'isolated') {
      configuration = {
        ...cloneSettings(this.state.settings),
        defaultRuntimeId: configuration.defaultRuntimeId ?? this.state.settings.defaultRuntimeId,
        proxyServer: '',
        proxyUsername: '',
        proxyPassword: '',
        additionalFlags: '',
        defaultPluginIds: []
      }
    }
    if (options.restoring) {
      configuration.defaultRuntimeId = options.restoring.runtimeId
      configuration.homeURL = options.restoring.startURL
      configuration.dockBadge = options.restoring.dockBadge ?? ''
    }

    const runtimeId = configuration.defaultRuntimeId ?? this.state.runtimes[0]?.id ?? null
    const runtime = this.state.runtimes.find((item) => item.id === runtimeId)
    if (!runtime) {
      this.finishLaunchFailure(token, new YTrayError('noRuntime', ''))
      throw new YTrayError('noRuntime', '')
    }

    const pluginIds = options.pluginIds ?? configuration.defaultPluginIds
    const plugins = this.selectedPlugins(pluginIds)

    // The chooser dialog runs outside the try below; a failure here used to
    // strand the launch state in 'preparing' and block every future launch.
    let engine: { cancelled: boolean; target: BrowserEngineTarget | null }
    try {
      engine = await this.resolveEngineTarget(configuration, options.restoring !== undefined)
    } catch (error) {
      log.error('engine.resolve', error)
      this.finishLaunchFailure(token, error)
      throw error
    }
    if (engine.cancelled) {
      this.launchState = { phase: 'idle', message: '', instanceId: null, restoringInstanceId: null }
      this.launchToken = null
      this.emitChange()
      return
    }
    configuration.engineTarget = engine.target
    if (engine.target) logEngineSelection(engine.target)

    try {
      const usedBadges = new Set(
        this.state.instances
          .filter((instance) => instance.status === 'running' && instance.dockBadge)
          .map((instance) => instance.dockBadge as string)
      )
      const badge = badgeFor(
        {
          runtime,
          mode,
          settings: configuration,
          plugins,
          ordinal: this.state.instances.length + 1,
          restoring: options.restoring,
          badge: options.badge,
          configuredPlugins: this.state.plugins
        },
        usedBadges
      )
      if (usedBadges.has(badge)) throw new YTrayError('launchFailed', `Dock 角标 ${badge} 已被运行中的实例使用`)

      // launchBrowser awaits a port probe, so the actual instance is inserted by
      // performLaunch once the child process exists.
      await this.performLaunch(
        {
          runtime,
          mode,
          settings: configuration,
          plugins,
          ordinal: this.state.instances.length + 1,
          restoring: options.restoring,
          engineTarget: engine.target
        },
        badge,
        token
      )
    } catch (error) {
      this.finishLaunchFailure(token, error)
      throw error
    }
  }

  /** Removes the launch artifacts an instance leaves on disk. */
  private cleanupInstanceArtifacts(instance: BrowserInstance): void {
    if (instance.windowClass) removeInstanceDesktopEntry(instance.windowClass)
    try {
      fs.rmSync(path.join(thumbnailsDirectory(), `${instance.id}.jpg`), { force: true })
    } catch {
      /* best-effort */
    }
  }

  private async performLaunch(
    request: Parameters<typeof launchBrowser>[0],
    badge: string,
    token: string
  ): Promise<void> {
    let result: LaunchResult
    try {
      result = await launchBrowser(request, badge)
    } catch (error) {
      this.finishLaunchFailure(token, error)
      return
    }
    if (this.launchToken !== token) {
      void terminateProcess(result.instance.processId, 1000)
      this.cleanupInstanceArtifacts(result.instance)
      return
    }

    this.launches.set(result.instance.id, result)
    if (request.restoring) {
      this.state.instances = this.state.instances.filter((instance) => instance.id !== request.restoring?.id)
    }
    this.state.instances.unshift(result.instance)
    this.rememberYakitLaunch(result.instance.id)
    this.launchState = {
      phase: 'waiting',
      message: `正在启动 ${result.instance.runtimeName}…`,
      instanceId: result.instance.id,
      restoringInstanceId: this.launchState.restoringInstanceId
    }
    this.save()
    void this.completeLaunch(result, token, request)
  }

  private async completeLaunch(
    result: LaunchResult,
    token: string,
    request: Parameters<typeof launchBrowser>[0]
  ): Promise<void> {
    const instance = result.instance
    const ready = await waitUntilReady(instance.debugPort)
    if (this.launchToken !== token) return
    if (!ready) {
      const alive = isProcessAlive(instance.processId)
      this.finishLaunchFailure(
        token,
        new YTrayError(
          'launchFailed',
          alive ? '浏览器进程已经创建，但调试端口未在 15 秒内就绪' : '浏览器进程在完成启动前退出'
        )
      )
      return
    }

    if (result.extensionPaths.length > 0) {
      this.launchState = { ...this.launchState, message: '正在验证并加载插件…' }
      this.emitChange()
      try {
        await ensureExtensionsLoaded(instance.debugPort, result.extensionPaths, result.legacyLoadingExpected)
      } catch (error) {
        const message =
          error instanceof ExtensionLoadingError
            ? error.message
            : `插件加载失败：${errorMessage(error)}`
        // Authenticated-proxy launches cannot drop the internal extension, so
        // the fallback prompt is only offered for user plugins.
        const canFallback =
          error instanceof ExtensionLoadingError &&
          !(request.settings.proxyUsername || request.settings.proxyPassword) &&
          request.plugins.length > 0
        if (canFallback && (await this.confirmLaunchWithoutPlugins(message, request))) return
        this.finishLaunchFailure(token, new YTrayError('launchFailed', message))
        return
      }
      if (this.launchToken !== token) return
    }

    if (result.deferredStartupURL) {
      try {
        await navigate(instance.debugPort, result.deferredStartupURL)
      } catch (error) {
        this.finishLaunchFailure(token, error)
        return
      }
    }

    await this.refreshPageTitle(instance.id)
    this.launchState = {
      phase: 'succeeded',
      message: `${instance.runtimeName} 已启动`,
      instanceId: instance.id,
      restoringInstanceId: null
    }
    this.emitChange()
    this.scheduleThumbnail(instance.id, true)

    await new Promise((resolve) => setTimeout(resolve, 1200))
    if (this.launchToken !== token) return
    this.launchState = { phase: 'idle', message: '', instanceId: null, restoringInstanceId: null }
    this.launchToken = null
    this.emitChange()
  }

  private async confirmLaunchWithoutPlugins(
    reason: string,
    request: Parameters<typeof launchBrowser>[0]
  ): Promise<boolean> {
    const names = request.plugins.map((plugin) => plugin.name).join('、')
    // Automated diagnostics (same spirit as the --smoke-* flags on the other
    // platforms) need to exercise the relaunch without a human clicking.
    const autoApprove = process.env.YTRAY_SMOKE_PLUGIN_FALLBACK === '1'
    const response = autoApprove
      ? 0
      : (
          await dialog.showMessageBox({
            type: 'warning',
            title: '插件加载失败',
            message: names ? `插件未能加载：${names}` : '插件未能加载',
            detail: `${reason}\n\n可以不加载插件继续启动。该选择只影响本次启动，不会修改插件默认设置。`,
            buttons: ['不加载插件并启动', '取消'],
            defaultId: 1,
            cancelId: 1,
            noLink: true
          })
        ).response
    if (response !== 0) return false

    const instance = this.state.instances.find((item) => item.id === this.launchState.instanceId)
    if (!instance) return false

    this.launches.delete(instance.id)
    removeProxyAuthExtension(instance.id)
    this.state.instances = this.state.instances.filter((item) => item.id !== instance.id)
    this.launchState = { phase: 'idle', message: '', instanceId: null, restoringInstanceId: null }
    this.launchToken = null
    this.save()
    await terminateProcess(instance.processId, 1500)

    await this.launch(instance.mode, {
      settings: { ...cloneSettings(request.settings), defaultRuntimeId: instance.runtimeId },
      pluginIds: [],
      restoring: instance,
      badge: instance.dockBadge
    })
    return true
  }

  private finishLaunchFailure(token: string, error: unknown): void {
    if (this.launchToken !== token) return
    const message = errorMessage(error)
    log.error('instance.launch', error)
    this.errorMessage = message
    const instanceId = this.launchState.instanceId
    if (instanceId) {
      const instance = this.state.instances.find((item) => item.id === instanceId)
      if (instance) {
        instance.status = 'failed'
        this.cleanupInstanceArtifacts(instance)
      }
      const launch = this.launches.get(instanceId)
      if (launch) {
        this.launches.delete(instanceId)
        void terminateProcess(launch.instance.processId, 1000)
        removeProxyAuthExtension(instanceId)
      }
      this.save()
    }
    this.launchState = {
      phase: 'idle',
      message: '',
      instanceId: null,
      restoringInstanceId: null
    }
    this.launchToken = null
    this.emitChange()
  }

  async restoreHistory(instance: BrowserInstance): Promise<void> {
    if (instance.status === 'running') return
    const configuration = instance.settingsSnapshot
      ? cloneSettings(instance.settingsSnapshot)
      : cloneSettings(this.state.settings)
    configuration.defaultRuntimeId = instance.runtimeId
    configuration.homeURL = instance.startURL
    configuration.dockBadge = instance.dockBadge ?? ''
    await this.launch(instance.mode, {
      settings: configuration,
      pluginIds: instance.pluginIds.length > 0 ? instance.pluginIds : configuration.defaultPluginIds,
      restoring: instance,
      badge: instance.dockBadge
    })
  }

  // -------------------------------------------------------------------- stop

  async stop(instanceId: string): Promise<void> {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status !== 'running') return
    try {
      await Promise.race([
        Promise.all([this.captureThumbnail(instance), this.refreshPageTitle(instance.id)]),
        new Promise((resolve) => setTimeout(resolve, 900))
      ])
      try {
        await closeBrowser(instance.debugPort)
      } catch {
        /* the socket usually closes before the reply arrives */
      }
      this.launches.delete(instanceId)
      await terminateProcess(instance.processId)
      this.markStopped(instanceId)
    } catch (error) {
      this.errorMessage = errorMessage(error)
      log.error('instance.stop', error)
      this.emitChange()
    }
  }

  private markStopped(instanceId: string): void {
    removeProxyAuthExtension(instanceId)
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (instance) {
      if (instance.windowClass) removeInstanceDesktopEntry(instance.windowClass)
      instance.status = 'stopped'
      this.consolidateHistoryBadges()
      log.info('instance.stop', `instance=${instanceId}; pid=${instance.processId}`)
    }
    this.save()
  }

  private consolidateHistoryBadges(): void {
    const history = this.state.instances.filter((instance) => instance.status !== 'running')
    const newest = new Map<string, BrowserInstance>()
    for (const instance of history) {
      const badge = instance.dockBadge
      if (!badge) continue
      const current = newest.get(badge)
      if (!current || Date.parse(instance.startedAt) >= Date.parse(current.startedAt)) newest.set(badge, instance)
    }
    const removed = this.state.instances.filter((instance) => {
      if (instance.status === 'running' || !instance.dockBadge) return false
      return newest.get(instance.dockBadge)?.id !== instance.id
    })
    for (const instance of removed) this.cleanupInstanceArtifacts(instance)
    if (removed.length > 0) {
      this.state.instances = this.state.instances.filter(
        (instance) => instance.status === 'running' || !instance.dockBadge || newest.get(instance.dockBadge)?.id === instance.id
      )
    }
  }

  // --------------------------------------------------------------- lifecycle

  private startMaintenance(): void {
    this.maintenanceTimer = setInterval(() => {
      void this.maintenanceTick()
    }, PAGE_REFRESH_INTERVAL)
  }

  private async maintenanceTick(): Promise<void> {
    if (this.disposed) return
    if (this.refreshProcessStates()) return
    const running = this.state.instances.filter((instance) => instance.status === 'running')
    await Promise.all(running.map((instance) => this.refreshPageTitle(instance.id)))
    for (const instance of running) this.scheduleThumbnail(instance.id)
  }

  private refreshProcessStates(): boolean {
    let changed = false
    for (const instance of this.state.instances) {
      if (instance.status !== 'running') continue
      const runtime = this.state.runtimes.find((item) => item.id === instance.runtimeId)
      const alive =
        isProcessAlive(instance.processId) &&
        (!runtime || isInstanceProcess(instance.processId, instance.profilePath, runtime.executablePath))
      if (alive) continue
      removeProxyAuthExtension(instance.id)
      if (instance.windowClass) removeInstanceDesktopEntry(instance.windowClass)
      instance.status = 'stopped'
      changed = true
      log.info('instance.exit', `browser exited; instance=${instance.id}; pid=${instance.processId}`)
    }
    if (changed) {
      this.consolidateHistoryBadges()
      this.save()
    }
    return changed
  }

  private async refreshPageTitle(instanceId: string): Promise<void> {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status !== 'running') return
    const state = await currentPageState(instance.debugPort, 3)
    if (!state || this.disposed) return
    const current = this.state.instances.find((item) => item.id === instanceId)
    if (!current) return
    let changed = false
    if (state.title && current.lastPageTitle !== state.title) {
      current.lastPageTitle = state.title
      changed = true
    }
    if (state.url && current.lastPageURL !== state.url) {
      current.lastPageURL = state.url
      changed = true
    }
    if (changed) this.save()
  }

  private scheduleThumbnail(instanceId: string, force = false): void {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status !== 'running') return
    const last = this.lastThumbnailAttempt.get(instanceId) ?? (instance.thumbnailUpdatedAt ? Date.parse(instance.thumbnailUpdatedAt) : 0)
    if (!force && Date.now() - last < THUMBNAIL_REFRESH_INTERVAL) return
    if (this.thumbnailInFlight.has(instanceId)) return
    this.thumbnailInFlight.add(instanceId)
    this.lastThumbnailAttempt.set(instanceId, Date.now())
    void this.captureThumbnail(instance).finally(() => this.thumbnailInFlight.delete(instanceId))
  }

  private async captureThumbnail(instance: BrowserInstance): Promise<void> {
    const output = path.join(thumbnailsDirectory(), `${instance.id}.jpg`)
    try {
      await capture(instance.debugPort, output, 'jpeg', 68, 3)
      const current = this.state.instances.find((item) => item.id === instance.id)
      if (current) {
        current.thumbnailPath = output
        current.thumbnailUpdatedAt = new Date().toISOString()
        this.save()
      }
    } catch {
      /* previews are best-effort */
    }
  }

  async captureScreenshot(instanceId: string): Promise<string> {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status !== 'running') throw new Error('实例未在运行')
    const stamp = new Date().toISOString().replace(/[-:]/g, '').replace(/\..+/, '').replace('T', '-')
    const output = path.join(dataDirectory(), 'Screenshots', `${stamp}-${instance.id.slice(0, 8)}.png`)
    await capture(instance.debugPort, output, 'png', null, 4)
    instance.lastScreenshotPath = output
    instance.thumbnailPath = output
    instance.thumbnailUpdatedAt = new Date().toISOString()
    this.save()
    return output
  }

  async focus(instanceId: string): Promise<void> {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status !== 'running') return
    await focusInstance(instance.debugPort)
  }

  revealProfile(instanceId: string): void {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance) return
    openInFileManager(instance.profilePath)
  }

  revealScreenshot(filePath: string): void {
    openInFileManager(path.dirname(filePath))
  }

  renameHistory(instanceId: string, name: string): void {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    const value = name.trim()
    if (!instance || instance.status === 'running' || !value) return
    instance.name = value
    this.save()
  }

  removeHistory(instanceId: string): void {
    const instance = this.state.instances.find((item) => item.id === instanceId)
    if (!instance || instance.status === 'running') return
    this.cleanupInstanceArtifacts(instance)
    this.state.instances = this.state.instances.filter((item) => item.id !== instanceId)
    this.save()
  }

  removeAllHistory(): void {
    const removed = this.state.instances.filter((instance) => instance.status !== 'running')
    for (const instance of removed) this.cleanupInstanceArtifacts(instance)
    this.state.instances = this.state.instances.filter((instance) => instance.status === 'running')
    this.save()
  }

  // --------------------------------------------------------------------- MCP

  startMcp(): boolean {
    const started = this.mcp.start()
    if (started) this.mcpState = { ...this.mcpState, available: true, autoStart: this.state.settings.mcpAutoStart }
    this.emitChange()
    return started
  }

  stopMcp(): void {
    this.mcp.stop()
    this.emitChange()
  }

  startControlServer(): void {
    this.control.start()
  }

  stopControlServer(): void {
    this.control.stop()
  }

  private async mcpOpen(options: {
    restoreId?: string
    settings?: LaunchSettings
    url?: string
    runtimeId?: string
    useProxy?: boolean
  }): Promise<{ instance?: BrowserInstance; error?: string }> {
    if (this.isLaunching) return { error: 'YTray is already starting a browser instance' }
    if (options.restoreId) {
      const instance = this.state.instances.find((item) => item.id === options.restoreId && item.status !== 'running')
      if (!instance) return { error: 'stopped browser instance was not found' }
      try {
        await this.restoreHistory(instance)
      } catch (error) {
        return { error: errorMessage(error) }
      }
      const restored = this.state.instances.find((item) => item.id === options.restoreId) ?? instance
      return { instance: restored }
    }

    const configuration = options.settings ?? this.quickLaunchConfiguration(Boolean(options.useProxy))
    if (!configuration) return { error: this.errorMessage ?? 'The configured proxy is invalid' }
    if (options.runtimeId) configuration.defaultRuntimeId = options.runtimeId
    if (options.url) configuration.homeURL = options.url

    const before = new Set(this.state.instances.map((instance) => instance.id))
    try {
      await this.launch('quick', { settings: configuration })
    } catch (error) {
      return { error: errorMessage(error) }
    }
    const opened = this.state.instances.find((instance) => !before.has(instance.id))
    return opened ? { instance: opened } : { error: 'YTray did not create a browser instance' }
  }

  private mcpFocus(instanceId: string): string | null {
    const instance = this.state.instances.find((item) => item.id === instanceId && item.status === 'running')
    if (!instance) return 'running browser instance was not found'
    void focusInstance(instance.debugPort)
    return null
  }

  private mcpStop(instanceId: string): string | null {
    const instance = this.state.instances.find((item) => item.id === instanceId && item.status === 'running')
    if (!instance) return 'running browser instance was not found'
    void this.stop(instanceId)
    return null
  }

  // ------------------------------------------------------------------ proxy

  rememberPresetProxy(): string {
    const settings = this.state.settings
    settings.proxyServer = settings.presetProxyServer
    const existing = settings.recentProxyPresets.find(
      (preset) => preset.server === settings.presetProxyServer && preset.username === settings.presetProxyUsername
    )
    settings.recentProxyPresets = settings.recentProxyPresets.filter((preset) => preset !== existing)
    settings.recentProxyPresets.unshift({
      id: existing?.id ?? ytrayId(),
      server: settings.presetProxyServer,
      remark: settings.presetProxyRemark,
      username: settings.presetProxyUsername,
      password: settings.presetProxyPassword,
      lastUsedAt: new Date().toISOString()
    })
    settings.recentProxyPresets = settings.recentProxyPresets.slice(0, 8)
    this.save()
    return settings.presetProxyServer
  }

  selectProxyPreset(presetId: string): void {
    const preset = this.state.settings.recentProxyPresets.find((item) => item.id === presetId)
    if (!preset) return
    const settings = this.state.settings
    settings.presetProxyServer = preset.server
    settings.presetProxyUsername = preset.username
    settings.presetProxyPassword = preset.password
    settings.presetProxyRemark = preset.remark
    preset.lastUsedAt = new Date().toISOString()
    this.save()
  }

  removeProxyPreset(presetId: string): void {
    this.state.settings.recentProxyPresets = this.state.settings.recentProxyPresets.filter((item) => item.id !== presetId)
    this.save()
  }

  async runProxyCheck(): Promise<void> {
    this.proxyCheckState = { phase: 'checking', message: '正在检测代理连通性…', report: null }
    this.emitChange()
    try {
      const settings = this.state.settings
      const endpoint = splitProxy(settings.presetProxyServer || DEFAULT_PRESET_PROXY)
      const report: ProxyCheckReport = await checkProxy(
        endpoint,
        settings.presetProxyUsername,
        settings.presetProxyPassword,
        settings.presetProxyCheckTarget
      )
      this.proxyCheckState = {
        phase: report.success ? 'success' : 'failure',
        message: report.message,
        report
      }
    } catch (error) {
      this.proxyCheckState = { phase: 'failure', message: errorMessage(error), report: null }
    } finally {
      this.emitChange()
    }
  }

  // ----------------------------------------------------------------- updates

  async checkForUpdates(): Promise<void> {
    this.updateState = { ...this.updateState, phase: 'checking', message: '正在检查更新…', progressPercent: 0 }
    this.emitChange()
    try {
      const result = await checkForUpdate()
      this.pendingRelease = result.release
      if (result.state === 'available' && result.release) {
        this.updateState = {
          phase: 'available',
          message: `发现新版本 ${result.release.version}`,
          availableVersion: result.release.version,
          currentVersion: appVersion,
          releaseNotes: result.release.notes,
          lastCheck: new Date().toISOString(),
          progressPercent: 0
        }
      } else if (result.state === 'unsupported') {
        this.updateState = {
          phase: 'unsupported',
          message: `官方源尚未发布 ${appVersion} 之后的 Linux 安装包`,
          availableVersion: null,
          currentVersion: appVersion,
          releaseNotes: null,
          lastCheck: new Date().toISOString(),
          progressPercent: 0
        }
      } else {
        this.updateState = {
          phase: 'upToDate',
          message: '已是最新版本',
          availableVersion: null,
          currentVersion: appVersion,
          releaseNotes: null,
          lastCheck: new Date().toISOString(),
          progressPercent: 0
        }
      }
    } catch (error) {
      this.updateState = {
        ...this.updateState,
        phase: 'failed',
        message: errorMessage(error),
        lastCheck: new Date().toISOString()
      }
    } finally {
      this.emitChange()
    }
  }

  async installUpdate(): Promise<void> {
    if (!this.pendingRelease) throw new Error('没有可安装的更新')
    if (!process.env.APPIMAGE) throw new Error('当前不是 AppImage 运行，请从 Releases 手动下载替换')
    this.updateState = {
      ...this.updateState,
      phase: 'downloading',
      message: '正在下载更新…',
      progressPercent: 0
    }
    this.emitChange()
    try {
      const downloaded = await downloadUpdate(this.pendingRelease.asset, (percent, message) => {
        this.updateState = {
          ...this.updateState,
          phase: 'downloading',
          message,
          progressPercent: percent
        }
        this.emitChange()
      })
      this.updateState = {
        ...this.updateState,
        phase: 'installing',
        message: '正在替换当前版本…',
        progressPercent: 100
      }
      this.emitChange()
      applyUpdate(downloaded)
      setTimeout(() => app.quit(), 400)
    } catch (error) {
      this.updateState = { ...this.updateState, phase: 'failed', message: errorMessage(error) }
      this.emitChange()
      throw error
    }
  }

  // ------------------------------------------------------------- normalizing

  private normalizeLoadedState(): void {
    for (const plugin of this.state.plugins) {
      if (!plugin.iconPath) plugin.iconPath = ''
    }
    const managed = this.state.plugins.find((plugin) => plugin.name === MANAGED_EXTENSION_NAME)
    if (managed && managed.pinToToolbar === undefined) managed.pinToToolbar = true
    for (const instance of this.state.instances) {
      instance.pluginIds ??= []
      instance.startURL ||= 'chrome://newtab'
      if (instance.status === 'running') {
        const runtime = this.state.runtimes.find((item) => item.id === instance.runtimeId)
        if (!runtime || !isInstanceProcess(instance.processId, instance.profilePath, runtime.executablePath)) {
          instance.status = 'stopped'
        }
      }
    }
    this.synchronizeDefaultPluginIds()
    this.state.settings.launchAtLogin = isAutostartEnabled()
    this.save()
  }

  dispose(): void {
    if (this.disposed) return
    this.disposed = true
    if (this.maintenanceTimer) clearInterval(this.maintenanceTimer)
    this.mcp.dispose()
    this.control.stop()
    this.launches.clear()
    saveState(this.state)
  }
}
