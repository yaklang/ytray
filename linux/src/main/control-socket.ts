import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { controlSocketPath } from './paths'
import { log } from './diagnostics'

export interface ControlBackend {
  listHistory(): Array<Record<string, unknown>>
  restore(instanceId: string): { ok: boolean; error?: string }
}

/**
 * Current-user IPC for first-party desktop clients, mirroring the macOS
 * control.sock protocol: one JSON request per connection, newline terminated.
 */
export class NativeControlServer {
  private server: net.Server | null = null

  constructor(private readonly backend: ControlBackend) {}

  start(): void {
    if (this.server) return
    const socketPath = controlSocketPath()
    try {
      fs.mkdirSync(path.dirname(socketPath), { recursive: true, mode: 0o700 })
      fs.rmSync(socketPath, { force: true })
    } catch (error) {
      log.error('control.prepare', error)
    }

    const server = net.createServer({ allowHalfOpen: false }, (connection) => {
      connection.setTimeout(3000)
      let buffer = ''
      connection.on('data', (chunk) => {
        buffer += chunk.toString('utf8')
        if (buffer.length > 64 * 1024) {
          connection.destroy()
          return
        }
        const newline = buffer.indexOf('\n')
        if (newline < 0) return
        const line = buffer.slice(0, newline)
        buffer = buffer.slice(newline + 1)
        const response = this.handle(line)
        connection.end(JSON.stringify(response) + '\n')
      })
      connection.on('error', () => connection.destroy())
      connection.on('timeout', () => connection.destroy())
    })

    server.on('error', (error) => {
      log.error('control.start', error)
      this.server = null
    })
    server.listen(socketPath, () => {
      try {
        fs.chmodSync(socketPath, 0o600)
      } catch {
        /* best-effort */
      }
      log.info('control.ready', socketPath)
    })
    this.server = server
  }

  stop(): void {
    this.server?.close()
    this.server = null
    try {
      fs.rmSync(controlSocketPath(), { force: true })
    } catch {
      /* ignore */
    }
  }

  private handle(line: string): Record<string, unknown> {
    let request: Record<string, unknown>
    try {
      request = JSON.parse(line) as Record<string, unknown>
    } catch {
      return { ok: false, error: '无效的控制请求' }
    }
    switch (request.action) {
      case 'list_history':
        return { ok: true, instances: this.backend.listHistory() }
      case 'restore_history': {
        if (typeof request.id !== 'string') return { ok: false, error: '浏览器实例 ID 无效' }
        const result = this.backend.restore(request.id)
        return result.ok ? { ok: true } : { ok: false, error: result.error ?? '没有找到可恢复的浏览器实例' }
      }
      default:
        return { ok: false, error: '不支持的原生控制命令' }
    }
  }
}
