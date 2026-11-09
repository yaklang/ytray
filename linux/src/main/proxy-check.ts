import net from 'node:net'
import type { ProxyCheckDetail, ProxyCheckReport } from '../shared/types'
import type { ProxyEndpoint } from './proxy-address'
import { YTrayError } from './errors'

const DEFAULT_TARGETS = ['https://example.com/', 'https://baidu.com/', 'https://google.com/']
const TIMEOUT_MS = 10000

function normalizeTarget(raw: string): URL {
  const trimmed = raw.trim()
  if (!trimmed) throw new YTrayError('invalidURL', raw)
  const candidate = trimmed.includes('://') ? trimmed : `https://${trimmed}`
  let url: URL
  try {
    url = new URL(candidate)
  } catch {
    throw new YTrayError('invalidURL', raw)
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') throw new YTrayError('invalidURL', raw)
  return url
}

function probe(endpoint: ProxyEndpoint, url: URL, username: string, password: string): Promise<{ success: boolean; message: string }> {
  return new Promise((resolve) => {
    const host = url.hostname
    const port = url.port ? Number(url.port) : url.protocol === 'https:' ? 443 : 80
    const socket = net.connect({ host: endpoint.host, port: endpoint.port })
    let settled = false
    const finish = (success: boolean, message: string): void => {
      if (settled) return
      settled = true
      socket.destroy()
      resolve({ success, message })
    }

    const timer = setTimeout(() => finish(false, '检测超时 · 请检查 Host、端口或代理服务'), TIMEOUT_MS)
    socket.once('error', (error) => {
      clearTimeout(timer)
      finish(false, `代理连接失败 · ${error.message}`)
    })
    socket.once('connect', () => {
      const auth =
        username || password
          ? `Proxy-Authorization: Basic ${Buffer.from(`${username}:${password}`).toString('base64')}\r\n`
          : ''
      socket.write(`CONNECT ${host}:${port} HTTP/1.1\r\nHost: ${host}:${port}\r\n${auth}\r\n`)
    })
    socket.once('data', (chunk) => {
      clearTimeout(timer)
      const statusLine = chunk.toString('utf8').split('\r\n')[0] ?? ''
      const match = /^HTTP\/1\.[01] (\d{3})/.exec(statusLine)
      if (!match) {
        finish(false, '代理返回了无法识别的响应')
        return
      }
      const code = Number(match[1])
      if (code === 407) finish(false, '代理要求认证，请检查用户名和密码')
      else if (code >= 200 && code < 300) finish(true, `连接成功 · HTTP ${code}`)
      else finish(false, `代理拒绝连接 · HTTP ${code}`)
    })
  })
}

export async function checkProxy(
  endpoint: ProxyEndpoint,
  username: string,
  password: string,
  customTarget: string
): Promise<ProxyCheckReport> {
  const targets: URL[] = []
  const details: ProxyCheckDetail[] = []

  for (const target of DEFAULT_TARGETS) targets.push(normalizeTarget(target))
  if (customTarget.trim()) {
    try {
      const custom = normalizeTarget(customTarget)
      if (!targets.some((item) => item.href === custom.href)) targets.push(custom)
    } catch (error) {
      details.push({
        target: customTarget.trim(),
        success: false,
        message: error instanceof Error ? error.message : String(error),
        elapsedMs: 0
      })
    }
  }

  const results = await Promise.all(
    targets.map(async (target) => {
      const started = Date.now()
      const result = await probe(endpoint, target, username, password)
      return {
        target: target.href,
        success: result.success,
        message: result.message,
        elapsedMs: Date.now() - started
      }
    })
  )

  const all = [...results, ...details]
  const successCount = all.filter((detail) => detail.success).length
  return {
    details: all,
    successCount,
    success: successCount > 0,
    message: `${successCount > 0 ? '检测成功' : '检测失败'} · ${successCount}/${all.length} 个目标可访问`
  }
}
