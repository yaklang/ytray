import fs from 'node:fs'
import path from 'node:path'
import WebSocket from 'ws'
import { log } from './diagnostics'
import { getJson } from './net'

export class ExtensionLoadingError extends Error {}

class DevToolsUnavailable extends Error {}

/**
 * Browser-level CDP session. Branded Chrome 137+ ignores --load-extension, so
 * extensions are installed through Extensions.loadUnpacked and read back to
 * prove they are enabled before the startup page is opened.
 */
class BrowserDevTools {
  private socket: WebSocket | null = null
  private nextId = 0
  private timeoutMs: number

  constructor(timeoutMs: number) {
    this.timeoutMs = timeoutMs
  }

  async connect(port: number): Promise<void> {
    const info = await getJson<{ webSocketDebuggerUrl?: string }>(`http://127.0.0.1:${port}/json/version`, {
      timeoutMs: this.timeoutMs
    }).catch(() => null)
    if (!info) throw new Error('浏览器调试端口未就绪')
    const address = info.webSocketDebuggerUrl ?? ''
    if (!address.startsWith('ws://127.0.0.1:') || !address.includes('/devtools/browser/')) {
      throw new Error('浏览器返回了无效的本地调试地址')
    }
    const socket = new WebSocket(address, { handshakeTimeout: this.timeoutMs })
    await new Promise<void>((resolve, reject) => {
      socket.once('open', () => resolve())
      socket.once('error', (error) => reject(error instanceof Error ? error : new Error(String(error))))
    })
    this.socket = socket
  }

  send(method: string, params: Record<string, unknown> = {}): Promise<Record<string, unknown>> {
    const socket = this.socket
    if (!socket) return Promise.reject(new Error('浏览器调试连接已关闭'))
    const id = (this.nextId += 1)
    return new Promise<Record<string, unknown>>((resolve, reject) => {
      const timer = setTimeout(() => {
        socket.off('message', onMessage)
        reject(new Error('浏览器扩展接口响应超时'))
      }, this.timeoutMs)

      const onMessage = (data: WebSocket.RawData): void => {
        try {
          const reply = JSON.parse(data.toString()) as {
            id?: number
            result?: Record<string, unknown>
            error?: { code?: number; message?: string }
          }
          if (reply.id !== id) return
          clearTimeout(timer)
          socket.off('message', onMessage)
          if (reply.error) {
            const message = reply.error.message ?? '扩展接口返回错误'
            if (reply.error.code === -32601 || message.startsWith('Method not available')) {
              reject(new DevToolsUnavailable(message))
            } else {
              reject(new Error(message))
            }
            return
          }
          resolve(reply.result ?? {})
        } catch {
          /* ignore non-JSON frames */
        }
      }

      socket.on('message', onMessage)
      socket.send(JSON.stringify({ id, method, params }))
    })
  }

  close(): void {
    this.socket?.close()
    this.socket = null
  }
}

export function legacyLoadingExpected(kind: string, version: string): boolean {
  const supportsCli = kind === 'chromeForTesting' || kind === 'chromium' || kind === 'edge'
  if (supportsCli) return true
  const major = Number.parseInt((version || '').split('.')[0] ?? '', 10)
  return Number.isFinite(major) && major > 0 && major < 137
}

interface ExtensionInfo {
  id?: string
  path?: string
  enabled?: boolean
}

export async function ensureExtensionsLoaded(
  port: number,
  extensionPaths: string[],
  legacyExpected: boolean
): Promise<void> {
  if (extensionPaths.length === 0) return
  const client = new BrowserDevTools(25000)
  try {
    await client.connect(port)
    await loadAll(client, extensionPaths, legacyExpected)
  } catch (error) {
    if (error instanceof ExtensionLoadingError) throw error
    throw new ExtensionLoadingError(`无法完成插件加载验证：${error instanceof Error ? error.message : String(error)}`)
  } finally {
    client.close()
  }
}

async function loadAll(
  client: BrowserDevTools,
  extensionPaths: string[],
  legacyExpected: boolean
): Promise<void> {
  const selected = [...new Set(extensionPaths.map((item) => path.resolve(item)))]
  let installed: ExtensionInfo[] | null = null
  try {
    const result = (await client.send('Extensions.getExtensions')) as { extensions?: ExtensionInfo[] }
    installed = result.extensions ?? []
  } catch (error) {
    if (!(error instanceof DevToolsUnavailable)) throw error
  }

  for (const target of selected) {
    if (installed?.some((item) => samePath(item.path, target) && item.enabled === true)) continue

    try {
      const result = (await client.send('Extensions.loadUnpacked', { path: target })) as { id?: string }
      const id = result.id
      if (!id || !/^[a-p]{32}$/.test(id)) throw new Error('加载接口没有返回有效插件 ID')

      if (installed !== null) {
        let enabled = false
        let observed: ExtensionInfo[] = []
        for (let attempt = 0; attempt < 5 && !enabled; attempt += 1) {
          const readBack = (await client.send('Extensions.getExtensions')) as { extensions?: ExtensionInfo[] }
          observed = readBack.extensions ?? []
          enabled = observed.some((item) => item.id === id && samePath(item.path, target) && item.enabled === true)
          if (!enabled) await new Promise((resolve) => setTimeout(resolve, 100))
        }
        if (!enabled) {
          log.info('extension.verify', `id=${id}; observed=${JSON.stringify(observed)}`)
          throw new Error('浏览器未确认插件已启用')
        }
      }
      log.info('extension.load', `CDP loaded extension=${id}`)
    } catch (error) {
      if (error instanceof DevToolsUnavailable && installed === null && legacyExpected) {
        log.info('extension.load', 'CDP unavailable; retaining supported legacy command-line loading')
        return
      }
      throw new ExtensionLoadingError(
        `插件加载失败（${path.basename(target)}）：${error instanceof Error ? error.message : String(error)}`
      )
    }
  }
}

function samePath(actual: string | undefined, expected: string): boolean {
  if (!actual) return false
  try {
    return fs.realpathSync(actual) === fs.realpathSync(expected)
  } catch {
    return path.resolve(actual) === expected
  }
}
