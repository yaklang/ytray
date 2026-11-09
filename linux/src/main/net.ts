import fs from 'node:fs'
import http from 'node:http'
import https from 'node:https'
import zlib from 'node:zlib'
import type { IncomingMessage } from 'node:http'

const MAX_REDIRECTS = 5

function open(url: string, headers: Record<string, string>, timeoutMs: number, remaining = MAX_REDIRECTS): Promise<IncomingMessage> {
  return new Promise((resolve, reject) => {
    const target = new URL(url)
    const client = target.protocol === 'https:' ? https : http
    const request = client.request(
      {
        protocol: target.protocol,
        hostname: target.hostname,
        port: target.port || (target.protocol === 'https:' ? 443 : 80),
        path: `${target.pathname}${target.search}`,
        method: 'GET',
        headers,
        timeout: timeoutMs
      },
      (response) => {
        const status = response.statusCode ?? 0
        const location = response.headers.location
        if (status >= 300 && status < 400 && location) {
          response.resume()
          if (remaining <= 0) {
            reject(new Error('重定向次数过多'))
            return
          }
          resolve(open(new URL(location, target).toString(), headers, timeoutMs, remaining - 1))
          return
        }
        resolve(response)
      }
    )
    request.on('timeout', () => request.destroy(new Error('请求超时')))
    request.on('error', reject)
    request.end()
  })
}

/** Deterministic HTTP client for loopback and mirror requests; Electron's global fetch is not used. */
export function get(url: string, options: { timeoutMs?: number; headers?: Record<string, string> } = {}): Promise<IncomingMessage> {
  return open(
    url,
    { Accept: 'application/json, */*', 'User-Agent': 'YTray-Linux', ...options.headers },
    options.timeoutMs ?? 20000
  )
}

export async function getBuffer(
  url: string,
  options: { timeoutMs?: number; maxBytes?: number } = {}
): Promise<{ status: number; body: Buffer }> {
  const response = await get(url, {
    timeoutMs: options.timeoutMs,
    headers: { 'Accept-Encoding': 'gzip, deflate' }
  })
  const maxBytes = options.maxBytes ?? 4 * 1024 * 1024
  const chunks: Buffer[] = []
  let size = 0
  for await (const chunk of response) {
    const buffer = chunk as Buffer
    size += buffer.length
    if (size > maxBytes) {
      response.destroy()
      throw new Error('响应内容异常（文件过大）')
    }
    chunks.push(buffer)
  }
  const raw = Buffer.concat(chunks)
  const body = decodeBody(raw, response.headers['content-encoding'])
  if (body.length > maxBytes) throw new Error('响应内容异常（解压后过大）')
  return { status: response.statusCode ?? 0, body }
}

/**
 * The OSS/CDN fronting the manifests compresses responses even when the client
 * asks for identity, so bodies are decoded here. The magic-byte check also
 * covers a missing or wrong Content-Encoding header.
 */
function decodeBody(body: Buffer, encoding: string | undefined): Buffer {
  const value = (encoding ?? '').toLowerCase()
  if (value.includes('br')) return zlib.brotliDecompressSync(body)
  if (value.includes('deflate')) return zlib.inflateSync(body)
  if (value.includes('gzip') || (body.length > 2 && body[0] === 0x1f && body[1] === 0x8b)) {
    return zlib.gunzipSync(body)
  }
  return body
}

export async function getJson<T>(url: string, options: { timeoutMs?: number; maxBytes?: number } = {}): Promise<T> {
  const { status, body } = await getBuffer(url, options)
  if (status < 200 || status >= 300) throw new Error(`HTTP ${status}`)
  return JSON.parse(body.toString('utf8').replace(/^\uFEFF/, '')) as T
}

export async function downloadToFile(
  url: string,
  destination: string,
  options: {
    timeoutMs?: number
    onProgress?: (received: number, total: number | null) => void
  } = {}
): Promise<void> {
  const response = await get(url, { timeoutMs: options.timeoutMs ?? 30 * 60 * 1000 })
  const status = response.statusCode ?? 0
  if (status < 200 || status >= 300) {
    response.resume()
    throw new Error(`HTTP ${status}`)
  }
  const encoding = ((response.headers['content-encoding'] ?? '') as string).toLowerCase()
  const compressed = encoding !== '' && encoding !== 'identity'
  const length = Number.parseInt(response.headers['content-length'] ?? '', 10)
  const total = Number.isFinite(length) && length > 0 && !compressed ? length : null
  let received = 0
  let lastReport = 0

  await new Promise<void>((resolve, reject) => {
    const output = fs.createWriteStream(destination, { mode: 0o600 })
    const source = compressed
      ? encoding.includes('br')
        ? response.pipe(zlib.createBrotliDecompress())
        : encoding.includes('deflate')
          ? response.pipe(zlib.createInflate())
          : response.pipe(zlib.createGunzip())
      : response
    source.on('data', (chunk: Buffer) => {
      received += chunk.length
      const now = Date.now()
      if (now - lastReport < 120) return
      lastReport = now
      options.onProgress?.(received, total)
    })
    source.on('error', reject)
    output.on('error', reject)
    output.on('finish', resolve)
    source.pipe(output)
  })
  options.onProgress?.(received, total)
  if (total !== null && received !== total) throw new Error('下载内容不完整')
}
