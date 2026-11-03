import { app, BrowserWindow, Menu, Tray, dialog, ipcMain, nativeImage, nativeTheme, screen, shell } from 'electron'
import fs from 'node:fs'
import path from 'node:path'
import { InstanceStore } from './store'
import { registerIpc, STATE_CHANNEL } from './ipc'
import { appIconPath } from './desktop'
import { ensureDesktopIntegration } from './desktop-integration'
import { log } from './diagnostics'
import { handleMediaProtocol, registerMediaScheme } from './media'
import {
  appVersion,
  controlSocketCompatPath,
  controlSocketPath,
  instanceStatePath,
  logsDirectory
} from './paths'
import { openInFileManager } from './process'

registerMediaScheme()

const singleInstance = app.requestSingleInstanceLock()
if (!singleInstance) app.quit()

app.setName('YTray')
if (app.isPackaged) {
  // Match the product name so clients that look YTray up by process name work
  // the same way they do on macOS (YTray.app) and Windows (YTray.exe).
  process.title = 'YTray'
}

// The tray widget is a transparent, always-on-top window. On Linux several
// drivers/compositors cannot composite that combination and kill the GPU
// process, which terminates the whole app. Software rendering is the safe
// default; opt back in with YTRAY_ENABLE_GPU=1 after verifying the setup.
if (process.env.YTRAY_ENABLE_GPU !== '1') {
  app.disableHardwareAcceleration()
}

let store: InstanceStore | null = null
let tray: Tray | null = null
let managerWindow: BrowserWindow | null = null
let widgetWindow: BrowserWindow | null = null
let widgetShownAt = 0

const startInBackground = process.argv.includes('--background')
// Electron consumes unknown switches placed before the app path, so the smoke
// output is passed through the environment instead of argv.
const smokeUiOutput = process.env.YTRAY_SMOKE_UI?.trim() || null

function rendererUrl(): string | null {
  return process.env.ELECTRON_RENDERER_URL ?? null
}

function loadRenderer(window: BrowserWindow, view: 'manager' | 'widget'): void {
  const url = rendererUrl()
  if (url) {
    void window.loadURL(`${url}#/${view}`)
  } else {
    void window.loadFile(path.join(__dirname, '../renderer/index.html'), { hash: `/${view}` })
  }
}

function createManagerWindow(showOnReady = true, offscreen = false): BrowserWindow {
  const window = new BrowserWindow({
    width: 1180,
    height: 800,
    minWidth: 920,
    minHeight: 640,
    show: false,
    title: 'YTray',
    backgroundColor: '#16120f',
    icon: appIconPath(),
    autoHideMenuBar: true,
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      offscreen
    }
  })
  loadRenderer(window, 'manager')
  if (showOnReady) {
    // ready-to-show is not guaranteed on Linux when hardware acceleration is
    // disabled, which previously left the process running with no visible
    // window. Reveal on whichever signal arrives first.
    let revealed = false
    const reveal = (source: string): void => {
      if (revealed || window.isDestroyed()) return
      revealed = true
      window.show()
      const bounds = window.getBounds()
      log.info(
        'window.manager',
        `shown via ${source}; visible=${window.isVisible()}; bounds=${bounds.width}x${bounds.height}+${bounds.x}+${bounds.y}`
      )
    }
    window.once('ready-to-show', () => reveal('ready-to-show'))
    window.webContents.once('did-finish-load', () => reveal('did-finish-load'))
    setTimeout(() => reveal('timeout'), 3000)
  }
  window.on('closed', () => {
    managerWindow = null
  })
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith('https://')) void shell.openExternal(url)
    return { action: 'deny' }
  })
  return window
}

function createWidgetWindow(offscreen = false): BrowserWindow {
  const window = new BrowserWindow({
    width: 392,
    height: 620,
    show: false,
    frame: false,
    resizable: false,
    movable: true,
    skipTaskbar: true,
    alwaysOnTop: true,
    fullscreenable: false,
    backgroundColor: '#00000000',
    transparent: true,
    hasShadow: true,
    icon: appIconPath(),
    webPreferences: {
      preload: path.join(__dirname, '../preload/index.js'),
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: false,
      offscreen
    }
  })
  window.setAlwaysOnTop(true, 'pop-up-menu')
  try {
    window.setVisibleOnAllWorkspaces(true, { visibleOnFullScreen: true })
  } catch {
    /* not supported by every compositor */
  }
  loadRenderer(window, 'widget')
  window.on('blur', () => {
    // Pinned widgets stay open, mirroring the macOS/Windows panels. The grace
    // period ignores the blur caused by the tray click that opened the panel.
    if (store?.isWidgetPinned) return
    if (Date.now() - widgetShownAt < 400) return
    if (window.isVisible()) window.hide()
  })
  window.on('closed', () => {
    widgetWindow = null
  })
  return window
}

function positionWidget(): void {
  const window = widgetWindow
  if (!window) return
  try {
    const cursor = screen.getCursorScreenPoint()
    const display = screen.getDisplayNearestPoint(cursor)
    const { x, y, width, height } = display.workArea
    const bounds = window.getBounds()
    const targetX = Math.min(Math.max(x + width - bounds.width - 16, x + 8), x + width - bounds.width - 8)
    const targetY = Math.min(Math.max(y + 8, y + 8), y + height - bounds.height - 8)
    // Wayland compositors may ignore client positioning; the window then opens
    // where the compositor decides, which is still usable.
    window.setPosition(Math.round(targetX), Math.round(targetY), false)
  } catch (error) {
    log.error('widget.position', error)
  }
}

function showWidget(): void {
  if (!widgetWindow) widgetWindow = createWidgetWindow()
  positionWidget()
  widgetWindow.show()
  widgetWindow.focus()
  widgetShownAt = Date.now()
}

function toggleWidget(): void {
  if (widgetWindow?.isVisible()) widgetWindow.hide()
  else showWidget()
}

function showManager(): void {
  if (!managerWindow) managerWindow = createManagerWindow()
  if (managerWindow.isMinimized()) managerWindow.restore()
  managerWindow.show()
  managerWindow.focus()
}

function buildTray(): void {
  const image = nativeImage.createFromPath(appIconPath())
  tray = new Tray(image.isEmpty() ? nativeImage.createEmpty() : image.resize({ width: 22, height: 22 }))
  tray.setToolTip('YTray')
  tray.on('click', () => {
    log.info('tray.activate', 'click')
    toggleWidget()
  })
  refreshTrayMenu()
}

/** Menu items mirror the macOS status-bar and Windows tray menus. */
let trayMenuSignature = ''

function refreshTrayMenu(): void {
  if (!tray || !store) return
  const snapshot = store.snapshot()
  const busy = snapshot.launch.phase === 'preparing' || snapshot.launch.phase === 'waiting'
  const launchAtLogin = snapshot.settings.launchAtLogin
  const update = snapshot.update
  const signature = [
    busy,
    launchAtLogin,
    update.phase,
    update.availableVersion ?? '',
    snapshot.runtimes.length,
    snapshot.mcp.available
  ].join('|')
  if (signature === trayMenuSignature) return
  trayMenuSignature = signature

  const launchDisabled = busy || snapshot.runtimes.length === 0
  const updateLabel =
    update.phase === 'available' && update.availableVersion
      ? `发现新版本 ${update.availableVersion}…`
      : update.phase === 'checking'
        ? '正在检查更新…'
        : '检查更新…'

  tray.setContextMenu(
    Menu.buildFromTemplate([
      {
        label: busy ? '正在启动浏览器…' : '无代理启动',
        enabled: !launchDisabled,
        click: () => launchFromTray(false)
      },
      { label: '使用 HTTP 代理启动', enabled: !launchDisabled, click: () => launchFromTray(true) },
      { label: '显示小组件', click: () => showWidget() },
      { label: '全部管理', click: () => showManager() },
      { label: '打开诊断日志', click: () => openInFileManager(logsDirectory()) },
      {
        label: '开机启动',
        type: 'checkbox',
        checked: launchAtLogin,
        click: (item) => store?.updateSettings({ launchAtLogin: item.checked })
      },
      { type: 'separator' },
      {
        label: updateLabel,
        enabled: update.phase !== 'checking' && update.phase !== 'downloading',
        click: () => {
          showManager()
          void store?.checkForUpdates()
        }
      },
      { type: 'separator' },
      { label: '退出 YTray', click: () => app.quit() }
    ])
  )
}

function launchFromTray(useProxy: boolean): void {
  if (!store) return
  const configuration = store.quickLaunchConfiguration(useProxy)
  if (!configuration) {
    dialog.showErrorBox('无法启动浏览器', store.snapshot().errorMessage ?? '代理地址无效')
    return
  }
  void store.launch('quick', { settings: configuration }).catch((error: unknown) => {
    dialog.showErrorBox('浏览器启动失败', error instanceof Error ? error.message : String(error))
  })
}

function broadcast(state: unknown): void {
  for (const window of [managerWindow, widgetWindow]) {
    if (window && !window.isDestroyed()) window.webContents.send(STATE_CHANNEL, state)
  }
}

app.on('second-instance', (_event, argv) => {
  const url = argv.find((argument) => argument.startsWith('ytray://'))
  log.info('app.second-instance', url ? `url=${url}` : `argv=${argv.slice(1, 4).join(' ')}`)
  showManager()
})

if (singleInstance) {
  app.whenReady().then(() => {
    app.setAppUserModelId('io.yaklang.ytray')
    handleMediaProtocol()

    store = new InstanceStore()
    nativeTheme.themeSource = store.settings.themePreference
    registerIpc(store, { openManager: () => showManager() })
    store.on('change', (state) => {
      broadcast(state)
      refreshTrayMenu()
    })

    ipcMain.on('ytray:widget-height', (_event, height: number) => {
      if (!widgetWindow || widgetWindow.isDestroyed()) return
      const value = Math.max(220, Math.min(900, Math.round(Number(height) || 620)))
      const bounds = widgetWindow.getBounds()
      widgetWindow.setBounds({ ...bounds, height: value })
    })

    buildTray()
    store.startControlServer()
    ensureDesktopIntegration()
    writeInstanceState()
    if (store.settings.mcpAutoStart) store.startMcp()
    else store.stopMcp()
    if (store.settings.checkForAppUpdates) void store.checkForUpdates()

    if (smokeUiOutput) {
      void captureSmokeUi(smokeUiOutput)
    } else if (!startInBackground) {
      managerWindow = createManagerWindow()
    } else {
      createWidgetWindow()
    }

    log.info('app.ready', `packaged=${app.isPackaged}; background=${startInBackground}`)
  })
}

/**
 * Headless UI self-check used by CI and by `npm run smoke:ui`: renders the
 * manager window without showing it and writes a PNG, so layout regressions are
 * visible without taking a screenshot of the user's desktop.
 */
async function captureSmokeUi(output: string): Promise<void> {
  log.info('app.smoke-ui', `start -> ${output}`)
  const window =
    process.env.YTRAY_SMOKE_VIEW === 'widget' ? createWidgetWindow(true) : createManagerWindow(false, true)
  await new Promise<void>((resolve) => {
    if (!window.webContents.isLoading()) {
      resolve()
      return
    }
    window.webContents.once('did-finish-load', () => resolve())
  })
  log.info('app.smoke-ui', 'loaded')
  await new Promise((resolve) => setTimeout(resolve, 900))
  const image = await Promise.race([
    window.webContents.capturePage(),
    new Promise<null>((resolve) => setTimeout(() => resolve(null), 8000))
  ])
  if (!image) {
    log.error('app.smoke-ui', new Error('capturePage timed out'))
    app.exit(1)
    return
  }
  log.info('app.smoke-ui', `captured ${image.isEmpty() ? 'empty' : `${image.getSize().width}x${image.getSize().height}`}`)
  fs.writeFileSync(output, image.toPNG(), { mode: 0o600 })
  app.exit(0)
}

app.on('window-all-closed', () => {
  log.info('app.windows', 'all windows closed; staying alive')
})

app.on('before-quit', () => {
  log.info('app.quit', 'quitting')
  try {
    fs.rmSync(instanceStatePath(), { force: true })
  } catch {
    /* ignore */
  }
  store?.dispose()
  tray?.destroy()
})

/** Small heartbeat file for clients that discover YTray through the data dir. */
function writeInstanceState(): void {
  const payload = {
    app: 'YTray',
    version: appVersion,
    pid: process.pid,
    startedAt: new Date().toISOString(),
    control: {
      socket: controlSocketPath(),
      compat: controlSocketCompatPath(),
      mcp: 'http://127.0.0.1:19222/mcp'
    }
  }
  try {
    fs.mkdirSync(path.dirname(instanceStatePath()), { recursive: true, mode: 0o700 })
    fs.writeFileSync(instanceStatePath(), `${JSON.stringify(payload, null, 2)}\n`, { mode: 0o600 })
    log.info('app.instance-state', instanceStatePath())
  } catch (error) {
    log.error('app.instance-state', error)
  }
}

process.on('uncaughtException', (error) => {
  log.error('process.uncaught', error)
})
process.on('unhandledRejection', (reason) => {
  log.error('process.unhandled', reason)
})
// Best-effort heartbeat cleanup for abnormal JS exits; SIGKILL residue is
// detected by clients through the recorded pid.
process.on('exit', () => {
  try {
    fs.rmSync(instanceStatePath(), { force: true })
  } catch {
    /* ignore */
  }
})
