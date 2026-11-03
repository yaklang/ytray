import { app, dialog, ipcMain, shell } from 'electron'
import type { CommandResult, RendererCommand } from '../shared/types'
import type { InstanceStore } from './store'
import { errorMessage } from './errors'
import { log } from './diagnostics'

export const STATE_CHANNEL = 'ytray:state'
export const COMMAND_CHANNEL = 'ytray:command'

export function registerIpc(store: InstanceStore, hooks: { openManager: () => void }): void {
  ipcMain.handle('ytray:get-state', () => store.snapshot())

  ipcMain.handle('ytray:pick-file', async () => {
    const result = await dialog.showOpenDialog({
      title: '选择 Chromium 浏览器可执行文件',
      properties: ['openFile'],
      filters: [{ name: '可执行文件', extensions: ['*'] }]
    })
    return result.canceled ? null : (result.filePaths[0] ?? null)
  })

  ipcMain.handle('ytray:pick-directory', async () => {
    const result = await dialog.showOpenDialog({
      title: '选择已解压的浏览器插件目录',
      properties: ['openDirectory', 'multiSelections']
    })
    return result.canceled ? [] : result.filePaths
  })

  ipcMain.handle(COMMAND_CHANNEL, async (_event, command: RendererCommand): Promise<CommandResult> => {
    try {
      return await dispatch(store, hooks, command)
    } catch (error) {
      log.error('ipc', error, command.type)
      return { ok: false, error: errorMessage(error) }
    }
  })
}

async function dispatch(
  store: InstanceStore,
  hooks: { openManager: () => void },
  command: RendererCommand
): Promise<CommandResult> {
  switch (command.type) {
    case 'setSettings':
      store.updateSettings(command.patch)
      return { ok: true }

    case 'saveSettings':
      store.saveSettings()
      return { ok: true }

    case 'refreshBrowsers':
      store.refreshSystemBrowsers()
      return { ok: true }

    case 'addLocalRuntime':
      store.addLocalRuntime(command.path)
      return { ok: true, message: '已添加本机浏览器' }

    case 'selectDefaultRuntime':
      store.selectDefaultRuntime(command.runtimeId)
      return { ok: true }

    case 'removeRuntime':
      store.removeRuntime(command.runtimeId)
      return { ok: true }

    case 'uninstallRuntime':
      store.uninstallRuntime(command.runtimeId)
      return { ok: true, message: '已卸载 Chrome for Testing' }

    case 'refreshRuntimeVersions':
      await store.refreshRuntimeVersions()
      return { ok: true }

    case 'installRuntime':
      await store.installRuntimeVersion(command.version)
      return { ok: true, message: `Chrome for Testing ${command.version} 安装完成` }

    case 'addPlugins': {
      const count = store.addPlugins(command.paths)
      return { ok: true, message: `已添加 ${count} 个插件` }
    }

    case 'updatePlugin':
      store.updatePlugin(command.pluginId, command.patch)
      return { ok: true }

    case 'removePlugin':
      store.removePlugin(command.pluginId)
      return { ok: true }

    case 'refreshExtensionManifest':
      await store.refreshExtensionManifest()
      return { ok: true }

    case 'installExtension':
      await store.installExtensionVersion(command.version)
      return { ok: true }

    case 'launchQuick': {
      const configuration = store.quickLaunchConfiguration(command.useProxy)
      if (!configuration) return { ok: false, error: '代理地址无效，请检查预设代理设置' }
      await store.launch('quick', { settings: configuration })
      return { ok: true }
    }

    case 'launchCustom': {
      const settings = { ...store.settings, ...command.settings }
      await store.launch(command.isolated ? 'isolated' : 'custom', {
        settings,
        pluginIds: command.isolated ? [] : command.pluginIds
      })
      return { ok: true }
    }

    case 'stopInstance':
      void store.stop(command.instanceId)
      return { ok: true }

    case 'focusInstance':
      await store.focus(command.instanceId)
      return { ok: true }

    case 'captureInstance': {
      const file = await store.captureScreenshot(command.instanceId)
      store.revealScreenshot(file)
      return { ok: true, message: `截图已保存：${file}` }
    }

    case 'openScreenshot': {
      const instance = store.snapshot().instances.find((item) => item.id === command.instanceId)
      if (!instance?.lastScreenshotPath) return { ok: false, error: '该实例还没有截图' }
      store.revealScreenshot(instance.lastScreenshotPath)
      return { ok: true }
    }

    case 'restoreInstance': {
      const instance = store.snapshot().instances.find((item) => item.id === command.instanceId)
      if (!instance) return { ok: false, error: '没有找到可恢复的浏览器实例' }
      await store.restoreHistory(instance)
      return { ok: true }
    }

    case 'removeHistory':
      store.removeHistory(command.instanceId)
      return { ok: true }

    case 'removeAllHistory':
      store.removeAllHistory()
      return { ok: true }

    case 'renameHistory':
      store.renameHistory(command.instanceId, command.name)
      return { ok: true }

    case 'revealProfile':
      store.revealProfile(command.instanceId)
      return { ok: true }

    case 'checkProxy':
      await store.runProxyCheck()
      return { ok: true }

    case 'rememberPresetProxy':
      store.rememberPresetProxy()
      return { ok: true }

    case 'selectProxyPreset':
      store.selectProxyPreset(command.presetId)
      return { ok: true }

    case 'removeProxyPreset':
      store.removeProxyPreset(command.presetId)
      return { ok: true }

    case 'setWidgetPinned':
      store.setWidgetPinned(command.pinned)
      return { ok: true }

    case 'startMcp':
      return store.startMcp() ? { ok: true } : { ok: false, error: store.snapshot().mcp.message }

    case 'stopMcp':
      store.stopMcp()
      return { ok: true }

    case 'checkForUpdates':
      await store.checkForUpdates()
      return { ok: true }

    case 'installUpdate':
      await store.installUpdate()
      return { ok: true }

    case 'openExternal':
      await shell.openExternal(command.url)
      return { ok: true }

    case 'openManager':
      hooks.openManager()
      return { ok: true }

    case 'quit':
      app.quit()
      return { ok: true }

    default: {
      // Exhaustiveness guard: new commands must be handled explicitly.
      const never: never = command
      return { ok: false, error: `未知命令：${JSON.stringify(never)}` }
    }
  }
}
