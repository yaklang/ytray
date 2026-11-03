import fs from 'node:fs'
import net from 'node:net'
import path from 'node:path'
import { appVersion, controlSocketCompatPath, controlSocketPath } from './paths'
import { log } from './diagnostics'

export interface ControlBackend {
  listHistory(): Array<Record<string, unknown>>
  restore(instanceId: string): { ok: boolean; error?: string }
  claim(instanceId: string, requestId: string): { approved: boolean; reason: string }
}

const MAX_BUFFER_BYTES = 64 * 1024
const CONNECTION_TIMEOUT_MS = 15000

/**
 * Current-user IPC for first-party desktop clients.
 *
 * The macOS build terminates one newline-delimited JSON request per connection;
 * this implementation is deliberately more tolerant so a client that writes a
 * complete JSON object without a trailing newline, half-closes its write side,
 * or reuses a connection still gets an answer instead of a silent timeout.
 */
export class NativeControlServer {
  private server: net.Server | null = null
  private connections = 0

  constructor(private readonly backend: ControlBackend) {}

  get listening(): boolean {
    return this.server !== null
  }

  start(): void {
    if (this.server) return
    const socketPath = controlSocketPath()
    try {
      fs.mkdirSync(path.dirname(socketPath), { recursive: true, mode: 0o700 })
      fs.rmSync(socketPath, { force: true })
    } catch (error) {
      log.error('control.prepare', error)
    }

    const server = net.createServer({ allowHalfOpen: true }, (connection) => {
      this.connections += 1
      const peer = this.connections
      log.info('control.connect', `#${peer}`)
      connection.setTimeout(CONNECTION_TIMEOUT_MS)
      connection.setNoDelay(true)

      let buffer = ''

      const write = (response: Record<string, unknown>): void => {
        if (!connection.destroyed) connection.write(`${JSON.stringify(response)}\n`)
      }

      const respond = (request: Record<string, unknown>): void => {
        const response = this.handle(request)
        log.info(
          'control.request',
          `#${peer} action=${String(request.action ?? '-')} ok=${response.ok === true}`
        )
        write(response)
      }

      const dispatch = (line: string): void => {
        let parsed: unknown
        try {
          parsed = JSON.parse(line)
        } catch {
          log.error('control.parse', new Error('invalid json'))
          write({ ok: false, error: '无效的控制请求' })
          return
        }
        if (!parsed || typeof parsed !== 'object' || Array.isArray(parsed)) {
          write({ ok: false, error: '无效的控制请求' })
          return
        }
        respond(parsed as Record<string, unknown>)
      }

      const consume = (final: boolean): void => {
        let newline = buffer.indexOf('\n')
        while (newline >= 0) {
          const line = buffer.slice(0, newline).trim()
          buffer = buffer.slice(newline + 1)
          if (line) dispatch(line)
          newline = buffer.indexOf('\n')
        }
        if (final && buffer.trim()) dispatch(buffer.trim())
        if (final) buffer = ''
      }

      connection.on('data', (chunk: Buffer) => {
        buffer += chunk.toString('utf8')
        if (buffer.length > MAX_BUFFER_BYTES) {
          log.error('control.request', new Error('request too large'))
          connection.destroy()
          return
        }
        consume(false)
        // Tolerate a complete JSON object that was not newline terminated.
        const pending = buffer.trim()
        if (pending) {
          try {
            JSON.parse(pending)
            buffer = ''
            dispatch(pending)
          } catch {
            /* incomplete JSON; wait for more bytes */
          }
        }
      })

      connection.on('end', () => {
        consume(true)
        connection.end()
        log.info('control.disconnect', `#${peer}`)
      })
      connection.on('error', (error) => {
        log.error('control.connection', error, `#${peer}`)
        connection.destroy()
      })
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
      this.linkCompatPath(socketPath)
    })
    this.server = server
  }

  /**
   * Some clients derive the socket from the config directory rather than the
   * data directory. A symlink covers that without running a second server.
   */
  private linkCompatPath(socketPath: string): void {
    const compat = controlSocketCompatPath()
    if (path.resolve(compat) === path.resolve(socketPath)) return
    try {
      fs.mkdirSync(path.dirname(compat), { recursive: true, mode: 0o700 })
      fs.rmSync(compat, { force: true })
      fs.symlinkSync(socketPath, compat)
      log.info('control.compat', compat)
    } catch (error) {
      log.error('control.compat', error, compat)
    }
  }

  stop(): void {
    this.server?.close()
    this.server = null
    for (const file of [controlSocketPath(), controlSocketCompatPath()]) {
      try {
        fs.rmSync(file, { force: true })
      } catch {
        /* ignore */
      }
    }
  }

  private handle(request: Record<string, unknown>): Record<string, unknown> {
    switch (request.action) {
      case 'ping':
        return { ok: true, app: 'YTray', version: appVersion, pid: process.pid }
      case 'list_history':
        return { ok: true, instances: this.backend.listHistory() }
      case 'restore_history': {
        if (typeof request.id !== 'string') return { ok: false, error: '浏览器实例 ID 无效' }
        const result = this.backend.restore(request.id)
        return result.ok ? { ok: true } : { ok: false, error: result.error ?? '没有找到可恢复的浏览器实例' }
      }
      case 'claim_launch_approval': {
        const id = typeof request.id === 'string' ? request.id : ''
        const requestId = typeof request.requestId === 'string' ? request.requestId.trim() : ''
        if (!/^[0-9a-f-]{36}$/i.test(id)) return { ok: false, error: '浏览器实例 ID 无效' }
        if (!requestId || requestId.length > 160 || /[\u0000-\u001f]/.test(requestId)) {
          return { ok: false, error: '配对请求 ID 无效' }
        }
        const result = this.backend.claim(id, requestId)
        return { ok: true, approved: result.approved, reason: result.reason }
      }
      default:
        return { ok: false, error: '不支持的原生控制命令' }
    }
  }
}
