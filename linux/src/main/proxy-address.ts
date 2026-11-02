import { YTrayError } from './errors'

export type ProxyScheme = 'http' | 'https'

export interface ProxyEndpoint {
  scheme: ProxyScheme
  host: string
  port: number
  server: string
}

export function normalizeProxy(value: string): string {
  let candidate = value.trim()
  if (!candidate) throw new YTrayError('invalidProxy', value)
  if (!candidate.includes('://')) candidate = `http://${candidate}`
  let url: URL
  try {
    url = new URL(candidate)
  } catch {
    throw new YTrayError('invalidProxy', value)
  }
  const scheme = url.protocol.replace(':', '').toLowerCase()
  if (scheme !== 'http' && scheme !== 'https') throw new YTrayError('invalidProxy', value)
  if (!url.hostname) throw new YTrayError('invalidProxy', value)
  if (url.username || url.password) throw new YTrayError('invalidProxy', value)
  if (url.port && (Number(url.port) < 1 || Number(url.port) > 65535)) throw new YTrayError('invalidProxy', value)
  const rebuilt = `${scheme}://${url.hostname}${url.port ? `:${url.port}` : ''}`
  return rebuilt
}

export function splitProxy(value: string): ProxyEndpoint {
  const server = normalizeProxy(value)
  const url = new URL(server)
  const scheme: ProxyScheme = url.protocol === 'https:' ? 'https' : 'http'
  const host = url.hostname.replace(/^\[|\]$/g, '')
  return { scheme, host, port: url.port ? Number(url.port) : scheme === 'https' ? 443 : 80, server }
}

export function buildProxy(scheme: ProxyScheme, host: string, port: number): string {
  const normalizedHost = host.trim().replace(/^\[|\]$/g, '')
  if (
    !normalizedHost ||
    normalizedHost.includes('://') ||
    normalizedHost.includes('/') ||
    normalizedHost.includes('@') ||
    !Number.isInteger(port) ||
    port < 1 ||
    port > 65535
  ) {
    throw new YTrayError('invalidProxy', `${scheme}://${host}:${port}`)
  }
  return `${scheme}://${normalizedHost}:${port}`
}

export function isUsableProxy(value: string): boolean {
  try {
    normalizeProxy(value)
    return true
  } catch {
    return false
  }
}
