import fs from 'node:fs'
import path from 'node:path'
import WebSocket from 'ws'
import { YTrayError } from './errors'
import { getJson } from './net'

export interface PageState {
  title: string
  url: string
}

interface CdpTarget {
  id?: string
  type?: string
  title?: string
  url?: string
  webSocketDebuggerUrl?: string
}

interface CdpReply {
  id?: number
  result?: Record<string, unknown>
  error?: { code?: number; message?: string }
}

const lastVisibleTargetByPort = new Map<number, string>()

function loopback(port: number): string {
  return `http://127.0.0.1:${port}`
}

export async function pageTargets(port: number): Promise<CdpTarget[] | null> {
  if (port < 1 || port > 65535) return null
  try {
    return await getJson<CdpTarget[]>(`${loopback(port)}/json/list`, { timeoutMs: 2000 })
  } catch {
    return null
  }
}

export async function waitUntilReady(port: number, attempts = 60): Promise<boolean> {
  for (let attempt = 0; attempt < Math.max(attempts, 1); attempt += 1) {
    if ((await pageTargets(port)) !== null) return true
    await delay(250)
  }
  return false
}

function delay(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms))
}

async function exchange(wsUrl: string, command: Record<string, unknown>, timeoutMs = 4000): Promise<CdpReply[]> {
  const socket = new WebSocket(wsUrl, { handshakeTimeout: timeoutMs })
  const replies: CdpReply[] = []
  const commandId = command.id

  return new Promise<CdpReply[]>((resolve, reject) => {
    const timer = setTimeout(() => {
      socket.terminate()
      reject(new YTrayError('screenshotFailed', '等待 CDP 响应超时'))
    }, timeoutMs)

    const finish = (error?: Error): void => {
      clearTimeout(timer)
      try {
        socket.close()
      } catch {
        /* ignore */
      }
      if (error) reject(error)
      else resolve(replies)
    }

    socket.on('open', () => socket.send(JSON.stringify(command)))
    socket.on('error', (error) => finish(error instanceof Error ? error : new Error(String(error))))
    socket.on('close', () => finish())
    socket.on('message', (data) => {
      try {
        const reply = JSON.parse(data.toString()) as CdpReply
        replies.push(reply)
        if (reply.id === commandId) finish()
      } catch {
        /* ignore non-JSON frames */
      }
    })
  })
}

async function connectBrowser(port: number, timeoutMs = 4000): Promise<{ socket: WebSocket; send: (method: string, params?: Record<string, unknown>) => Promise<Record<string, unknown>> }> {
  const info = await getJson<{ webSocketDebuggerUrl?: string }>(`${loopback(port)}/json/version`, {
    timeoutMs
  }).catch(() => null)
  if (!info) throw new YTrayError('launchFailed', '浏览器调试端口未就绪')
  const url = info.webSocketDebuggerUrl
  if (!url || !url.startsWith('ws://127.0.0.1')) throw new YTrayError('launchFailed', '浏览器返回了无效的本地调试地址')

  const socket = new WebSocket(url, { handshakeTimeout: timeoutMs })
  let nextId = 0
  await new Promise<void>((resolve, reject) => {
    socket.once('open', () => resolve())
    socket.once('error', (error) => reject(error instanceof Error ? error : new Error(String(error))))
  })

  const send = (method: string, params: Record<string, unknown> = {}): Promise<Record<string, unknown>> => {
    const id = (nextId += 1)
    return new Promise<Record<string, unknown>>((resolve, reject) => {
      const timer = setTimeout(() => {
        socket.off('message', onMessage)
        reject(new YTrayError('launchFailed', `浏览器没有响应 ${method}`))
      }, timeoutMs)

      const onMessage = (data: WebSocket.RawData): void => {
        try {
          const reply = JSON.parse(data.toString()) as CdpReply
          if (reply.id !== id) return
          clearTimeout(timer)
          socket.off('message', onMessage)
          if (reply.error) reject(new YTrayError('launchFailed', reply.error.message ?? '浏览器调试接口返回错误'))
          else resolve(reply.result ?? {})
        } catch {
          /* ignore fragments that do not parse yet */
        }
      }
      socket.on('message', onMessage)
      socket.send(JSON.stringify({ id, method, params }))
    })
  }

  return { socket, send }
}

async function probeVisible(target: CdpTarget, timeoutMs = 850): Promise<PageState | null> {
  if (!target.webSocketDebuggerUrl) return null
  try {
    const replies = await exchange(
      target.webSocketDebuggerUrl,
      {
        id: 1,
        method: 'Runtime.evaluate',
        params: {
          expression:
            "document.visibilityState === 'visible' ? JSON.stringify({title: document.title, url: location.href}) : ''",
          returnByValue: true
        }
      },
      timeoutMs
    )
    for (const reply of replies) {
      const result = reply.result as { result?: { value?: string } } | undefined
      const value = result?.result?.value
      if (!value) continue
      const parsed = JSON.parse(value) as { title?: string; url?: string }
      return { title: (parsed.title ?? '').trim(), url: (parsed.url ?? '').trim() }
    }
  } catch {
    /* hidden or stale targets simply lose the race */
  }
  return null
}

async function findVisiblePage(port: number): Promise<{ target: CdpTarget; state: PageState } | null> {
  const targets = (await pageTargets(port))?.filter(
    (target) => target.type === 'page' && target.webSocketDebuggerUrl
  )
  if (!targets || targets.length === 0) return null

  const preferredId = lastVisibleTargetByPort.get(port)
  if (preferredId) {
    const preferred = targets.find((target) => target.id === preferredId)
    if (preferred) {
      const state = await probeVisible(preferred)
      if (state) return { target: preferred, state }
    }
  }

  const settled = await Promise.any(
    targets.map(async (target) => {
      const state = await probeVisible(target)
      if (!state) throw new Error('hidden')
      return { target, state }
    })
  ).catch(() => null)

  if (settled?.target.id) lastVisibleTargetByPort.set(port, settled.target.id)
  return settled
}

export async function currentPageState(port: number, attempts = 1): Promise<PageState | null> {
  for (let attempt = 0; attempt < Math.max(attempts, 1); attempt += 1) {
    const visible = await findVisiblePage(port)
    if (visible) return visible.state
    const targets = await pageTargets(port)
    const first = targets?.find((target) => target.type === 'page')
    if (first) {
      const title = (first.title ?? '').trim()
      const url = (first.url ?? '').trim()
      if (title || url) return { title, url }
    }
    if (attempt + 1 < attempts) await delay(150)
  }
  return null
}

export async function currentPageTitle(port: number, attempts = 1): Promise<string | null> {
  return (await currentPageState(port, attempts))?.title ?? null
}

export async function navigate(port: number, url: string): Promise<void> {
  const targets = await pageTargets(port)
  if (!targets) throw new YTrayError('launchFailed', '找不到可恢复的浏览器标签页')
  const visible = await findVisiblePage(port)
  const target =
    visible?.target ??
    targets.find((item) => item.type === 'page' && item.webSocketDebuggerUrl)
  if (!target?.webSocketDebuggerUrl) throw new YTrayError('launchFailed', '找不到可恢复的浏览器标签页')

  const replies = await exchange(target.webSocketDebuggerUrl, {
    id: 1,
    method: 'Page.navigate',
    params: { url }
  })
  for (const reply of replies) {
    if (reply.id !== 1) continue
    if (reply.error) throw new YTrayError('launchFailed', reply.error.message ?? '恢复页面失败')
    return
  }
  throw new YTrayError('launchFailed', '浏览器没有确认恢复页面')
}

export async function capture(
  port: number,
  outputPath: string,
  format: 'png' | 'jpeg',
  quality: number | null,
  attempts = 3
): Promise<void> {
  let lastError = '调试端口尚未就绪'
  for (let attempt = 0; attempt < attempts; attempt += 1) {
    try {
      const targets = await pageTargets(port)
      if (!targets) throw new YTrayError('screenshotFailed', '没有可截图的页面')
      const visible = await findVisiblePage(port)
      const target =
        visible?.target ?? targets.find((item) => item.type === 'page' && item.webSocketDebuggerUrl)
      if (!target?.webSocketDebuggerUrl) throw new YTrayError('screenshotFailed', '没有可截图的页面')

      const params: Record<string, unknown> = { format, captureBeyondViewport: false, fromSurface: true }
      if (quality !== null) params.quality = quality
      const replies = await exchange(target.webSocketDebuggerUrl, {
        id: 1,
        method: 'Page.captureScreenshot',
        params
      })
      let encoded: string | undefined
      for (const reply of replies) {
        if (reply.id !== 1) continue
        if (reply.error) throw new YTrayError('screenshotFailed', reply.error.message ?? 'CDP 返回错误')
        encoded = (reply.result as { data?: string } | undefined)?.data
      }
      if (!encoded) throw new YTrayError('screenshotFailed', 'CDP 没有返回图片')
      writeAtomically(outputPath, Buffer.from(encoded, 'base64'))
      return
    } catch (error) {
      lastError = error instanceof Error ? error.message : String(error)
      await delay(250)
    }
  }
  throw new YTrayError('screenshotFailed', lastError)
}

export async function focusInstance(port: number): Promise<boolean> {
  let connection: Awaited<ReturnType<typeof connectBrowser>> | null = null
  try {
    connection = await connectBrowser(port, 2500)
    const targets = await connection.send('Target.getTargets')
    const info = targets as { targetInfos?: Array<{ targetId?: string; type?: string }> }
    const page = info.targetInfos?.find((target) => target.type === 'page')
    if (!page?.targetId) return false
    await connection.send('Target.activateTarget', { targetId: page.targetId })
    return true
  } catch {
    return false
  } finally {
    connection?.socket.close()
  }
}

export async function closeBrowser(port: number): Promise<void> {
  const connection = await connectBrowser(port, 2500)
  try {
    // Browser.close usually tears the socket down before replying, so the rejection is expected.
    await connection.send('Browser.close').catch(() => undefined)
  } finally {
    connection.socket.close()
  }
}

export function writeAtomically(outputPath: string, bytes: Buffer): void {
  if (bytes.length === 0) throw new YTrayError('screenshotFailed', 'CDP 返回了空图片')
  const directory = path.dirname(outputPath)
  fs.mkdirSync(directory, { recursive: true })
  const temporary = `${outputPath}.new-${process.pid}-${Date.now()}`
  try {
    fs.writeFileSync(temporary, bytes, { mode: 0o600 })
    fs.renameSync(temporary, outputPath)
  } finally {
    try {
      fs.rmSync(temporary, { force: true })
    } catch {
      /* ignore */
    }
  }
}
