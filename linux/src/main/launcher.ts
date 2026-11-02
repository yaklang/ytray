import { spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import type {
  BrowserInstance,
  BrowserKind,
  BrowserPlugin,
  BrowserRuntime,
  LaunchMode,
  LaunchSettings
} from '../shared/types'
import { blockedCustomPrefixes, cloneSettings } from './settings'
import { defaultBadge, identityThemeArgument, normalizeBadge, windowClass } from './identity'
import { YTrayError } from './errors'
import { pluginExtensionId, MANAGED_EXTENSION_NAME } from './extension-installer'
import { normalizeProxy } from './proxy-address'
import { legacyLoadingExpected } from './extension-service'
import { writeProxyAuthExtension, removeProxyAuthExtension } from './proxy-auth'
import { writeInstanceDesktopEntry } from './desktop'
import { dataDirectory, logsDirectory, profilesDirectory } from './paths'
import { log } from './diagnostics'
import { ytrayId } from './ids'

export interface LaunchResult {
  instance: BrowserInstance
  child: ChildProcess
  extensionPaths: string[]
  legacyLoadingExpected: boolean
  deferredStartupURL: string | null
  logPath: string
}

export interface LaunchRequest {
  runtime: BrowserRuntime
  mode: LaunchMode
  settings: LaunchSettings
  plugins: BrowserPlugin[]
  ordinal: number
  restoring?: BrowserInstance
  badge?: string
  configuredPlugins?: BrowserPlugin[]
}

function canBind(port: number): Promise<boolean> {
  return new Promise((resolve) => {
    const server = net.createServer()
    server.once('error', () => resolve(false))
    server.once('listening', () => server.close(() => resolve(true)))
    server.listen(port, '127.0.0.1')
  })
}

export async function nextAvailablePort(startingAt: number): Promise<number> {
  const start = Math.max(1024, startingAt)
  for (let port = start; port <= Math.min(start + 200, 65535); port += 1) {
    if (await canBind(port)) return port
  }
  return start
}

function validateTarget(value: string): string {
  const target = value.trim()
  if (!target) throw new YTrayError('invalidURL', target)
  if (target.startsWith('chrome://') || target.startsWith('about:') || target.startsWith('data:')) return target
  try {
    const url = new URL(target)
    if (url.protocol === 'http:' || url.protocol === 'https:' || url.protocol === 'chrome-extension:') return target
  } catch {
    /* fall through */
  }
  throw new YTrayError('invalidURL', target)
}

export function managedBootstrapURL(
  extensionId: string,
  instanceId: string,
  badge: string,
  target: string,
  restore: boolean,
  proxyServer: string,
  kind: BrowserKind | undefined,
  version: string | undefined
): string {
  const params = new URLSearchParams({
    manager: 'ytray',
    instanceId,
    badge,
    target,
    restore: restore ? '1' : '0',
    startupProxy: proxyServer.trim() ? normalizeProxy(proxyServer) : 'direct'
  })
  if (kind) params.set('browserName', kind)
  if (version?.trim()) params.set('browserVersion', version.trim())
  return `chrome-extension://${extensionId}/ytray-bootstrap.html?${params.toString()}`
}

export function buildArguments(request: {
  mode: LaunchMode
  settings: LaunchSettings
  profilePath: string
  debugPort: number
  plugins: BrowserPlugin[]
  runtimeKind?: BrowserKind
  runtimeVersion?: string
  internalExtensionPaths: string[]
  badge: string
  instanceId: string
  isNewProfile: boolean
  restoreLastSession: boolean
  deferExtensionStartup: boolean
}): string[] {
  const { mode, settings } = request
  const args = [
    `--user-data-dir=${request.profilePath}`,
    '--remote-debugging-address=127.0.0.1',
    `--remote-debugging-port=${request.debugPort}`,
    `--remote-allow-origins=http://127.0.0.1:${request.debugPort}`,
    `--no-first-run`,
    '--no-default-browser-check',
    '--hide-crash-restore-bubble',
    // Linux equivalents of the macOS keychain flag: never touch libsecret/kwallet
    // for these disposable automation profiles.
    '--password-store=basic',
    '--use-mock-keychain',
    `--class=${windowClass(request.badge)}`
  ]

  if (request.isNewProfile && !request.restoreLastSession && settings.colorizeBrowserInstances) {
    args.push(identityThemeArgument(request.badge))
  }
  if (request.runtimeKind === 'chromeForTesting') args.push('--disable-infobars')
  if (settings.ignoreCertificateErrors) args.push('--ignore-certificate-errors')
  if (settings.useTestType) args.push('--test-type')

  if (mode !== 'isolated') {
    args.push(
      '--disable-background-networking',
      '--disable-component-update',
      '--disable-default-apps',
      '--disable-sync',
      '--disable-translate'
    )
    if (settings.disableNotifications) args.push('--disable-notifications')
    if (settings.restrictWebRTC) {
      args.push(
        '--disable-webrtc',
        '--force-webrtc-ip-handling-policy=disable_non_proxied_udp',
        '--webrtc-ip-handling-policy=disable_non_proxied_udp'
      )
    }

    const proxy = settings.proxyServer.trim()
    args.push(proxy ? `--proxy-server=${proxy}` : '--no-proxy-server')

    const extensionPaths = [
      ...request.internalExtensionPaths,
      ...request.plugins.filter((plugin) => plugin.enabled).map((plugin) => plugin.path)
    ]
    if (extensionPaths.length > 0) {
      const joined = extensionPaths.join(',')
      args.push(`--disable-extensions-except=${joined}`, `--load-extension=${joined}`)
    }

    for (const line of settings.additionalFlags.split('\n')) {
      const flag = line.trim()
      if (!flag) continue
      if (!flag.startsWith('--')) throw new YTrayError('invalidFlag', flag)
      if (blockedCustomPrefixes.some((prefix) => flag.startsWith(prefix))) throw new YTrayError('invalidFlag', flag)
      args.push(flag)
    }
  }

  const target = validateTarget(settings.homeURL)
  if (request.deferExtensionStartup) {
    if (request.restoreLastSession) args.push('--restore-last-session')
    args.push('about:blank')
    return args
  }

  const managed = request.plugins.find(
    (plugin) => plugin.enabled && plugin.name === MANAGED_EXTENSION_NAME
  )
  const managedId = managed ? pluginExtensionId(managed) : null
  const usesManagedBootstrap = mode !== 'isolated' && managedId !== null

  if (request.restoreLastSession) {
    args.push('--restore-last-session')
    if (usesManagedBootstrap) {
      args.push(
        managedBootstrapURL(
          managedId,
          request.instanceId,
          request.badge,
          target,
          true,
          settings.proxyServer,
          request.runtimeKind,
          request.runtimeVersion
        )
      )
    }
    return args
  }

  args.push(
    usesManagedBootstrap
      ? managedBootstrapURL(
          managedId,
          request.instanceId,
          request.badge,
          target,
          false,
          settings.proxyServer,
          request.runtimeKind,
          request.runtimeVersion
        )
      : target
  )
  return args
}

/** Pins managed extensions in Default/Preferences; other pinned ids are preserved. */
export function preparePinnedExtensions(profilePath: string, plugins: BrowserPlugin[]): void {
  const controlled = new Set(
    plugins.map((plugin) => pluginExtensionId(plugin)).filter((id): id is string => Boolean(id))
  )
  if (controlled.size === 0) return

  const defaultProfile = path.join(profilePath, 'Default')
  const preferencesPath = path.join(defaultProfile, 'Preferences')
  fs.mkdirSync(defaultProfile, { recursive: true })

  let preferences: Record<string, unknown> = {}
  if (fs.existsSync(preferencesPath)) {
    try {
      const parsed = JSON.parse(fs.readFileSync(preferencesPath, 'utf8')) as Record<string, unknown>
      if (parsed && typeof parsed === 'object') preferences = parsed
    } catch {
      log.info('launch.preferences', 'ignoring unreadable browser Preferences')
    }
  }

  const extensions = (preferences.extensions as Record<string, unknown> | undefined) ?? {}
  const pinned = Array.isArray(extensions.pinned_extensions)
    ? (extensions.pinned_extensions as string[]).filter((id) => id && !controlled.has(id))
    : []
  for (const plugin of plugins) {
    const id = pluginExtensionId(plugin)
    if (plugin.pinToToolbar === true && id && !pinned.includes(id)) pinned.push(id)
  }
  extensions.pinned_extensions = pinned
  preferences.extensions = extensions

  const temporary = `${preferencesPath}.ytray.tmp`
  fs.writeFileSync(temporary, JSON.stringify(preferences), { mode: 0o600 })
  fs.renameSync(temporary, preferencesPath)
}

function profilePathFor(settings: LaunchSettings, badge: string, instanceId: string): string {
  const root = settings.profileRootPath.trim() ? path.resolve(settings.profileRootPath.trim()) : profilesDirectory()
  return path.join(root, `Inst${badge}`, instanceId)
}

export function badgeFor(request: LaunchRequest, usedBadges: Set<string>): string {
  const requested = (request.restoring?.dockBadge ?? request.badge ?? request.settings.dockBadge ?? '').trim()
  if (requested) return normalizeBadge(requested)
  for (let ordinal = 1; ordinal <= 702; ordinal += 1) {
    const candidate = defaultBadge(ordinal)
    if (!usedBadges.has(candidate)) return candidate
  }
  return 'ZZ'
}

export async function launchBrowser(request: LaunchRequest, badge: string): Promise<LaunchResult> {
  const { runtime, mode, restoring } = request
  try {
    fs.accessSync(runtime.executablePath, fs.constants.X_OK)
  } catch {
    throw new YTrayError('invalidExecutable', runtime.executablePath)
  }

  const settings = cloneSettings(request.settings)
  const instanceId = restoring?.id ?? ytrayId()
  const normalizedBadge = normalizeBadge(badge)
  const className = windowClass(normalizedBadge)
  const profilePath = restoring?.profilePath ?? profilePathFor(settings, normalizedBadge, instanceId)
  fs.mkdirSync(profilePath, { recursive: true, mode: 0o700 })

  const debugPort = await nextAvailablePort(settings.debugPort)
  const usesProxyAuth =
    mode !== 'isolated' && Boolean(settings.proxyUsername || settings.proxyPassword)
  const proxyAuthPath = usesProxyAuth
    ? writeProxyAuthExtension(instanceId, settings.proxyUsername, settings.proxyPassword, settings.proxyServer)
    : null

  const internalExtensionPaths = proxyAuthPath ? [proxyAuthPath] : []
  const extensionPaths =
    mode === 'isolated'
      ? []
      : [...internalExtensionPaths, ...request.plugins.filter((plugin) => plugin.enabled).map((plugin) => plugin.path)]

  let args: string[]
  try {
    args = buildArguments({
      mode,
      settings,
      profilePath,
      debugPort,
      plugins: request.plugins,
      runtimeKind: runtime.browserKind,
      runtimeVersion: runtime.version,
      internalExtensionPaths,
      badge: normalizedBadge,
      instanceId,
      isNewProfile: restoring === undefined,
      restoreLastSession: restoring !== undefined && !usesProxyAuth,
      deferExtensionStartup: extensionPaths.length > 0
    })

    if (!legacyLoadingExpected(runtime.browserKind ?? 'chrome', runtime.version)) {
      // Branded Chrome 137+ drops this obsolete allowlist, which also cancels
      // extensions installed later through CDP.
      args = args.filter((argument) => !argument.startsWith('--disable-extensions-except='))
    }
    if (mode !== 'isolated') preparePinnedExtensions(profilePath, request.plugins)
  } catch (error) {
    if (proxyAuthPath) removeProxyAuthExtension(instanceId)
    throw error
  }

  writeInstanceDesktopEntry(normalizedBadge, className, runtime.executablePath)

  fs.mkdirSync(logsDirectory(), { recursive: true, mode: 0o700 })
  const logPath = path.join(logsDirectory(), `${instanceId}.log`)
  // spawn() needs a real descriptor, not a stream that has not opened yet.
  const logFd = fs.openSync(logPath, 'w', 0o600)

  let child: ChildProcess
  try {
    child = spawn(runtime.executablePath, args, {
      detached: true,
      stdio: ['ignore', logFd, logFd],
      cwd: dataDirectory()
    })
  } catch (error) {
    removeProxyAuthExtension(instanceId)
    throw new YTrayError('launchFailed', error instanceof Error ? error.message : String(error))
  } finally {
    // The child keeps its own duplicate of the descriptor.
    try {
      fs.closeSync(logFd)
    } catch {
      /* already closed */
    }
  }
  child.unref()

  if (child.pid === undefined) {
    removeProxyAuthExtension(instanceId)
    throw new YTrayError('launchFailed', '浏览器进程没有启动')
  }

  const instance: BrowserInstance = {
    id: instanceId,
    name: restoring?.name ?? `浏览器实例 ${request.ordinal}`,
    runtimeId: runtime.id,
    runtimeName: runtime.name,
    runtimeVersion: runtime.version,
    runtimeKind: runtime.browserKind,
    runtimeSource: runtime.source,
    mode,
    processId: child.pid,
    debugPort,
    profilePath,
    startURL: restoring?.startURL ?? settings.homeURL,
    startedAt: new Date().toISOString(),
    status: 'running',
    lastScreenshotPath: restoring?.lastScreenshotPath,
    thumbnailPath: restoring?.thumbnailPath,
    thumbnailUpdatedAt: restoring?.thumbnailUpdatedAt,
    lastPageTitle: restoring?.lastPageTitle,
    lastPageURL: restoring?.lastPageURL,
    dockBadge: normalizedBadge,
    windowClass: className,
    settingsSnapshot: cloneSettings(settings),
    pluginIds: request.plugins.map((plugin) => plugin.id)
  }

  const startupURL = (restoring?.lastPageURL?.trim() || settings.homeURL).trim()
  const managed = request.plugins.find((plugin) => plugin.enabled && plugin.name === MANAGED_EXTENSION_NAME)
  const managedId = managed ? pluginExtensionId(managed) : null
  const deferredStartupURL =
    extensionPaths.length === 0
      ? null
      : managedId === null
        ? startupURL
        : managedBootstrapURL(
            managedId,
            instanceId,
            normalizedBadge,
            startupURL,
            restoring !== undefined,
            settings.proxyServer,
            runtime.browserKind,
            runtime.version
          )

  log.info(
    'instance.launch',
    `pid=${child.pid}; instance=${instanceId}; port=${debugPort}; badge=${normalizedBadge}; mode=${mode}; plugins=${request.plugins.length}`
  )

  return {
    instance,
    child,
    extensionPaths,
    legacyLoadingExpected: legacyLoadingExpected(runtime.browserKind ?? 'chrome', runtime.version),
    deferredStartupURL,
    logPath
  }
}
