import http from 'node:http'
import type { AddressInfo } from 'node:net'
import type { BrowserInstance, BrowserRuntime } from '../shared/types'
import { appVersion } from './paths'
import { log } from './diagnostics'

export const MCP_PORT = 19222
export const MCP_ENDPOINT = `http://127.0.0.1:${MCP_PORT}/mcp`
const MODERN_VERSION = '2026-07-28'
const LEGACY_VERSIONS = ['2025-11-25', '2025-06-18', '2025-03-26']
const MAX_BODY_BYTES = 1024 * 1024

export interface McpBackend {
  runtimes(): BrowserRuntime[]
  instances(): BrowserInstance[]
  defaultRuntimeId(): string | null
  openNew(options: { url?: string; runtimeId?: string; useProxy: boolean }): Promise<{ instance?: BrowserInstance; error?: string }>
  restore(instanceId: string): Promise<{ instance?: BrowserInstance; error?: string }>
  focus(instanceId: string): string | null
  stop(instanceId: string): string | null
  isLaunching(): boolean
  onStatus(available: boolean, message: string): void
}

const tools = [
  {
    name: 'list_browser_runtimes',
    description: 'List browser runtimes available to YTray. Use a runtime id with open_browser_instance.',
    inputSchema: { type: 'object', additionalProperties: false },
    annotations: { readOnlyHint: true, idempotentHint: true }
  },
  {
    name: 'list_browser_instances',
    description: 'List YTray browser instances. Running instances are returned by default.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        include_history: { type: 'boolean', default: false, description: 'Include stopped historical instances.' }
      }
    },
    annotations: { readOnlyHint: true, idempotentHint: true }
  },
  {
    name: 'open_browser_instance',
    description: 'Open a new browser instance, or restore a stopped history instance by id.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: {
        instance_id: { type: 'string', description: 'Optional stopped history instance id to restore.' },
        url: { type: 'string', description: 'Optional http, https, chrome, or about URL to open.' },
        runtime_id: { type: 'string', description: 'Optional runtime id from list_browser_runtimes.' },
        use_proxy: { type: 'boolean', default: false, description: 'Use the proxy preset currently configured in YTray.' }
      }
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: false }
  },
  {
    name: 'focus_browser_instance',
    description: 'Bring a running YTray browser instance to the foreground.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: { instance_id: { type: 'string' } },
      required: ['instance_id']
    },
    annotations: { readOnlyHint: false, destructiveHint: false, idempotentHint: true }
  },
  {
    name: 'stop_browser_instance',
    description: 'Stop a running YTray browser instance while preserving it in history.',
    inputSchema: {
      type: 'object',
      additionalProperties: false,
      properties: { instance_id: { type: 'string' } },
      required: ['instance_id']
    },
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: true }
  }
]

type Json = Record<string, unknown>

function jsonRpcResult(id: unknown, result: Json, modern: boolean): Json {
  if (modern) {
    result.resultType ??= 'complete'
    result._meta ??= { 'io.modelcontextprotocol/serverInfo': serverInfo() }
  }
  return { jsonrpc: '2.0', id: id ?? null, result }
}

function jsonRpcError(id: unknown, code: number, message: string, data?: unknown): Json {
  const error: Json = { code, message }
  if (data !== undefined) error.data = data
  return { jsonrpc: '2.0', id: id ?? null, error }
}

function serverInfo(): Json {
  return { name: 'ytray', version: appVersion }
}

function toolResult(value: Json): Json {
  return {
    resultType: 'complete',
    content: [{ type: 'text', text: JSON.stringify(value) }],
    structuredContent: value,
    isError: false
  }
}

function toolError(message: string): Json {
  return { resultType: 'complete', content: [{ type: 'text', text: message }], isError: true }
}

function runtimeJson(runtime: BrowserRuntime, defaultId: string | null): Json {
  return {
    id: runtime.id,
    name: runtime.name,
    version: runtime.version || '版本未知',
    kind: runtime.browserKind ?? 'chrome',
    source: runtime.source,
    default: runtime.id === defaultId
  }
}

function instanceJson(instance: BrowserInstance): Json {
  return {
    id: instance.id,
    name: instance.name,
    runtime: instance.runtimeName,
    runtime_id: instance.runtimeId,
    status: instance.status,
    pid: instance.processId,
    debug_port: instance.debugPort,
    start_url: instance.startURL,
    page_title: instance.lastPageTitle ?? null,
    page_url: instance.lastPageURL ?? null,
    dock_badge: instance.dockBadge ?? null,
    started_at: instance.startedAt
  }
}

function isAllowedStartURL(value: string): boolean {
  if (value.length > 2048) return false
  try {
    const url = new URL(value)
    if (url.protocol === 'http:' || url.protocol === 'https:') return Boolean(url.hostname)
    return url.protocol === 'chrome:' || url.protocol === 'about:'
  } catch {
    return false
  }
}

export class McpServer {
  private server: http.Server | null = null
  private disposed = false

  constructor(private readonly backend: McpBackend) {}

  get running(): boolean {
    return this.server !== null
  }

  start(): boolean {
    if (this.server) return true
    if (this.disposed) {
      this.backend.onStatus(false, 'MCP 服务已经关闭，请重启 YTray')
      return false
    }
    try {
      const server = http.createServer((request, response) => {
        void this.handle(request, response)
      })
      server.on('error', (error) => {
        log.error('mcp.server', error)
        this.backend.onStatus(false, `启动失败：${error.message}`)
        this.server = null
      })
      server.listen(MCP_PORT, '127.0.0.1', () => {
        const address = server.address() as AddressInfo | null
        this.backend.onStatus(true, `正在监听 ${MCP_ENDPOINT}`)
        log.info('mcp.start', `listening on ${MCP_ENDPOINT} (${address?.address ?? '127.0.0.1'})`)
      })
      this.server = server
      return true
    } catch (error) {
      this.backend.onStatus(false, `启动失败：${error instanceof Error ? error.message : String(error)}`)
      return false
    }
  }

  stop(): void {
    const server = this.server
    this.server = null
    server?.close()
    this.backend.onStatus(false, 'MCP 服务已停止，可随时重新启动')
    log.info('mcp.stop', 'listener stopped')
  }

  dispose(): void {
    this.disposed = true
    this.stop()
  }

  private async handle(request: http.IncomingMessage, response: http.ServerResponse): Promise<void> {
    let method = '-'
    const send = (status: number, body: Json | null): void => {
      log.info('mcp.request', `method=${method} status=${status} host=${request.headers.host ?? '-'}`)
      const payload = body ? Buffer.from(JSON.stringify(body)) : Buffer.alloc(0)
      response.writeHead(status, {
        'Content-Type': 'application/json; charset=utf-8',
        'Content-Length': payload.length,
        Connection: 'close',
        'Cache-Control': 'no-store'
      })
      response.end(payload)
    }

    try {
      if ((request.method ?? '') !== 'POST' || request.url !== '/mcp') {
        send(404, jsonRpcError(null, -32601, 'MCP endpoint not found'))
        return
      }
      if (!allowedHost(request.headers.host) || !allowedOrigin(request.headers.origin)) {
        send(403, jsonRpcError(null, -32600, 'Forbidden host or origin'))
        return
      }
      if (!(request.headers['content-type'] ?? '').startsWith('application/json')) {
        send(415, jsonRpcError(null, -32600, 'Content-Type must be application/json'))
        return
      }

      const body = await readBody(request)
      if (body === null) {
        send(400, jsonRpcError(null, -32700, 'Parse error'))
        return
      }

      let message: Json
      try {
        message = JSON.parse(body) as Json
      } catch {
        send(400, jsonRpcError(null, -32700, 'Parse error'))
        return
      }

      const id = message.id ?? null
      const methodName = typeof message.method === 'string' ? message.method : ''
      method = methodName || '-'
      if (message.jsonrpc !== '2.0' || !methodName) {
        send(400, jsonRpcError(id, -32600, 'Invalid Request'))
        return
      }

      const modern = isModern(message, request.headers['mcp-protocol-version'])
      if (modern) {
        const failure = validateModernHeaders(message, request.headers, methodName)
        if (failure) {
          send(400, jsonRpcError(id, failure.code, failure.message, failure.data))
          return
        }
      }
      if (id === null) {
        send(202, null)
        return
      }

      switch (methodName) {
        case 'initialize': {
          const requested = (message.params as Json | undefined)?.protocolVersion
          const selected =
            typeof requested === 'string' && LEGACY_VERSIONS.includes(requested) ? requested : (LEGACY_VERSIONS[0] as string)
          send(
            200,
            jsonRpcResult(
              id,
              {
                protocolVersion: selected,
                capabilities: { tools: { listChanged: false } },
                serverInfo: serverInfo(),
                instructions: 'Use YTray tools to open and manage local isolated browser instances.'
              },
              modern
            )
          )
          return
        }
        case 'server/discover':
          send(
            200,
            jsonRpcResult(
              id,
              {
                supportedVersions: [MODERN_VERSION, ...LEGACY_VERSIONS],
                capabilities: { tools: { listChanged: false } }
              },
              true
            )
          )
          return
        case 'ping':
          send(200, jsonRpcResult(id, {}, modern))
          return
        case 'tools/list':
          send(200, jsonRpcResult(id, { tools }, modern))
          return
        case 'tools/call':
          send(200, jsonRpcResult(id, await this.callTool(message), modern))
          return
        default:
          send(modern ? 404 : 200, jsonRpcError(id, -32601, 'Method not found'))
      }
    } catch (error) {
      log.error('mcp.request', error)
      send(500, jsonRpcError(null, -32603, 'Internal error'))
    }
  }

  private async callTool(message: Json): Promise<Json> {
    const params = (message.params as Json | undefined) ?? {}
    const name = params.name
    if (typeof name !== 'string' || !name) throw new Error('tool name is required')
    const argumentToken = params.arguments
    if (argumentToken !== undefined && (argumentToken === null || typeof argumentToken !== 'object' || Array.isArray(argumentToken))) {
      return toolError('arguments must be an object')
    }
    const args = (argumentToken as Json | undefined) ?? {}

    try {
      switch (name) {
        case 'list_browser_runtimes': {
          only(args)
          const defaultId = this.backend.defaultRuntimeId()
          return toolResult({ runtimes: this.backend.runtimes().map((runtime) => runtimeJson(runtime, defaultId)) })
        }
        case 'list_browser_instances': {
          only(args, 'include_history')
          const includeHistory = optionalBool(args, 'include_history') ?? false
          const instances = this.backend
            .instances()
            .filter((instance) => includeHistory || instance.status === 'running')
          return toolResult({ instances: instances.map(instanceJson) })
        }
        case 'open_browser_instance':
          only(args, 'instance_id', 'url', 'runtime_id', 'use_proxy')
          return await this.openBrowser(args)
        case 'focus_browser_instance': {
          only(args, 'instance_id')
          const id = requiredString(args, 'instance_id')
          const failure = this.backend.focus(id)
          return failure ? toolError(failure) : toolResult({ instance_id: id, status: 'focused' })
        }
        case 'stop_browser_instance': {
          only(args, 'instance_id')
          const id = requiredString(args, 'instance_id')
          const failure = this.backend.stop(id)
          return failure ? toolError(failure) : toolResult({ instance_id: id, status: 'stopping' })
        }
        default:
          return toolError(`Unknown tool: ${name}`)
      }
    } catch (error) {
      return toolError(error instanceof Error ? error.message : String(error))
    }
  }

  private async openBrowser(args: Json): Promise<Json> {
    const instanceId = optionalString(args, 'instance_id')
    if (instanceId) {
      if (Object.keys(args).some((key) => key !== 'instance_id')) {
        return toolError('instance_id cannot be combined with new-instance options')
      }
      if (!/^[0-9a-f-]{36}$/i.test(instanceId)) return toolError('instance_id is not a valid id')
      const result = await this.backend.restore(instanceId)
      if (!result.instance) return toolError(result.error ?? 'stopped browser instance was not found')
      return toolResult({ ...instanceJson(result.instance), launch_status: 'starting' })
    }

    const url = optionalString(args, 'url')?.trim()
    if (url && !isAllowedStartURL(url)) return toolError('url must use http, https, chrome, or about')
    const useProxy = optionalBool(args, 'use_proxy') ?? false
    const runtimeId = optionalString(args, 'runtime_id')
    if (runtimeId && !this.backend.runtimes().some((runtime) => runtime.id === runtimeId)) {
      return toolError('runtime_id was not found')
    }
    if (this.backend.isLaunching()) return toolError('YTray is already starting a browser instance')

    const result = await this.backend.openNew({ url: url ?? undefined, runtimeId: runtimeId ?? undefined, useProxy })
    if (!result.instance) return toolError(result.error ?? 'YTray could not start the browser instance')
    return toolResult({ ...instanceJson(result.instance), launch_status: 'starting' })
  }
}

function isModern(message: Json, headerVersion: string | string[] | undefined): boolean {
  const meta = (message.params as Json | undefined)?._meta as Json | undefined
  const bodyVersion = meta?.['io.modelcontextprotocol/protocolVersion']
  const header = Array.isArray(headerVersion) ? headerVersion[0] : headerVersion
  return bodyVersion !== undefined || (header !== undefined && !LEGACY_VERSIONS.includes(header))
}

function validateModernHeaders(
  message: Json,
  headers: http.IncomingHttpHeaders,
  method: string
): { code: number; message: string; data?: unknown } | null {
  const meta = (message.params as Json | undefined)?._meta as Json | undefined
  const bodyVersion = meta?.['io.modelcontextprotocol/protocolVersion']
  const capabilities = meta?.['io.modelcontextprotocol/clientCapabilities']
  const headerVersion = Array.isArray(headers['mcp-protocol-version'])
    ? headers['mcp-protocol-version'][0]
    : headers['mcp-protocol-version']
  if (headerVersion !== bodyVersion) {
    return { code: -32020, message: 'MCP request headers do not match the body' }
  }
  if (bodyVersion !== MODERN_VERSION) {
    return {
      code: -32022,
      message: 'Unsupported protocol version',
      data: { supported: [MODERN_VERSION, ...LEGACY_VERSIONS], requested: bodyVersion ?? headerVersion ?? 'missing' }
    }
  }
  if (capabilities === null || typeof capabilities !== 'object') {
    return { code: -32602, message: 'Modern MCP requests require clientCapabilities metadata' }
  }
  if (headers['mcp-method'] !== method) {
    return { code: -32020, message: 'Mcp-Method header does not match the body' }
  }
  if (method === 'tools/call') {
    const params = (message.params as Json | undefined) ?? {}
    const bodyName = params.name
    if (typeof bodyName !== 'string' || decodeHeader(headers['mcp-name']) !== bodyName) {
      return { code: -32020, message: 'Mcp-Name header does not match the tool name' }
    }
  }
  return null
}

function decodeHeader(value: string | string[] | undefined): string | undefined {
  const raw = Array.isArray(value) ? value[0] : value
  if (!raw || !raw.startsWith('=?base64?') || !raw.endsWith('?=')) return raw
  try {
    return Buffer.from(raw.slice(9, -2), 'base64').toString('utf8')
  } catch {
    return undefined
  }
}

function allowedHost(value: string | undefined): boolean {
  return value === `127.0.0.1:${MCP_PORT}` || value === `localhost:${MCP_PORT}`
}

function allowedOrigin(value: string | undefined): boolean {
  return (
    value === undefined ||
    value === `http://127.0.0.1:${MCP_PORT}` ||
    value === `http://localhost:${MCP_PORT}`
  )
}

function readBody(request: http.IncomingMessage): Promise<string | null> {
  return new Promise((resolve) => {
    let size = 0
    const chunks: Buffer[] = []
    request.on('data', (chunk: Buffer) => {
      size += chunk.length
      if (size > MAX_BODY_BYTES) {
        resolve(null)
        request.destroy()
        return
      }
      chunks.push(chunk)
    })
    request.on('end', () => resolve(Buffer.concat(chunks).toString('utf8')))
    request.on('error', () => resolve(null))
  })
}

function only(args: Json, ...allowed: string[]): void {
  const unexpected = Object.keys(args).find((key) => !allowed.includes(key))
  if (unexpected) throw new Error(`unexpected argument: ${unexpected}`)
}

function requiredString(args: Json, name: string): string {
  const value = optionalString(args, name)
  if (!value) throw new Error(`${name} is required`)
  return value
}

function optionalString(args: Json, name: string): string | undefined {
  const value = args[name]
  if (value === undefined) return undefined
  if (typeof value !== 'string' || !value.trim()) throw new Error(`${name} must be a non-empty string`)
  return value
}

function optionalBool(args: Json, name: string): boolean | undefined {
  const value = args[name]
  if (value === undefined) return undefined
  if (typeof value !== 'boolean') throw new Error(`${name} must be a boolean`)
  return value
}
