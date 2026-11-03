import type { BrowserEngineTarget } from '../shared/types'
import { getJson } from './net'
import { log } from './diagnostics'

const PORT_BLOCKS = [64333, 14333, 24333, 34333]
const PROBE_SPAN = 16
const PROBE_TIMEOUT_MS = 900
const MAX_BODY_BYTES = 32768

interface DiscoveryPayload {
  type?: string
  protocolVersion?: number
  engineIdentityId?: string
  engineInstanceId?: string
  frontendId?: string
  frontendName?: string
}

function clip(value: string, max: number): string {
  return value.slice(0, Math.min(value.length, max))
}

function parsePayload(value: DiscoveryPayload, port: number): BrowserEngineTarget | null {
  if (value.type !== 'engine' || value.protocolVersion !== 3) return null
  const identity = value.engineIdentityId?.trim()
  const instance = value.engineInstanceId?.trim()
  if (!identity || identity.length > 160 || !instance || instance.length > 160) return null
  return {
    mode: 'engine',
    engineIdentityId: identity,
    engineInstanceId: instance,
    frontendId: clip(value.frontendId ?? 'yak', 80),
    frontendName: clip(value.frontendName ?? '独立 Yak 引擎', 120),
    endpoint: `ws://127.0.0.1:${port}/extension`
  }
}

async function probe(port: number): Promise<BrowserEngineTarget | null> {
  try {
    const payload = await getJson<DiscoveryPayload>(`http://127.0.0.1:${port}/discovery`, {
      timeoutMs: PROBE_TIMEOUT_MS,
      maxBytes: MAX_BODY_BYTES
    })
    return parsePayload(payload, port)
  } catch {
    return null
  }
}

function candidatePorts(remembered: BrowserEngineTarget | null): number[] {
  const ports = new Set<number>()
  for (const first of PORT_BLOCKS) {
    for (let port = first; port < first + PROBE_SPAN; port += 1) ports.add(port)
  }
  if (remembered?.endpoint) {
    try {
      const url = new URL(remembered.endpoint)
      const port = Number(url.port)
      if ((url.hostname === '127.0.0.1' || url.hostname === 'localhost') && port > 0) ports.add(port)
    } catch {
      /* remembered endpoints may be malformed after a manual edit */
    }
  }
  return [...ports]
}

/** Discovers locally running Yak engines, mirroring the Windows probe ranges. */
export async function findEngines(remembered: BrowserEngineTarget | null): Promise<BrowserEngineTarget[]> {
  const results = await Promise.all(candidatePorts(remembered).map((port) => probe(port)))
  const seen = new Set<string>()
  const engines: BrowserEngineTarget[] = []
  for (const engine of results) {
    if (!engine) continue
    const key = `${engine.engineIdentityId}:${engine.engineInstanceId}`
    if (seen.has(key)) continue
    seen.add(key)
    engines.push(engine)
  }
  engines.sort((a, b) => a.frontendName.localeCompare(b.frontendName) || a.endpoint.localeCompare(b.endpoint))
  return engines
}

export function sameFrontend(a: BrowserEngineTarget, b: BrowserEngineTarget): boolean {
  return a.engineIdentityId === b.engineIdentityId && a.frontendId === b.frontendId
}

function resolveRemembered(
  remembered: BrowserEngineTarget,
  online: BrowserEngineTarget[]
): BrowserEngineTarget | null {
  if (remembered.mode === 'none' || remembered.mode === 'legacy') return { ...remembered }
  const matches = online.filter((engine) => sameFrontend(remembered, engine))
  return (
    matches.find((engine) => engine.engineInstanceId === remembered.engineInstanceId) ??
    (matches.length === 1 ? (matches[0] as BrowserEngineTarget) : matches.length === 0 ? { ...remembered } : null)
  )
}

/**
 * Decides which engine a launch should connect to. Returns null when the user
 * must pick one (several engines online and nothing remembered).
 */
export function resolveForLaunch(
  remembered: BrowserEngineTarget | null,
  online: BrowserEngineTarget[],
  restoring: boolean
): BrowserEngineTarget | null {
  if (remembered) return resolveRemembered(remembered, online)
  // Existing profiles keep their plugin pairing until a target is chosen explicitly.
  if (restoring || online.length === 0) return { mode: 'legacy', frontendId: '', frontendName: '', engineIdentityId: '', engineInstanceId: '', endpoint: '' }
  return online.length === 1 ? (online[0] as BrowserEngineTarget) : null
}

export function engineSummary(target: BrowserEngineTarget | null | undefined): string {
  if (!target) return '接入：原有连接设置'
  if (target.mode === 'none') return '仅启动浏览器'
  if (target.mode === 'legacy') return '接入：原有连接设置'
  return `接入：${target.frontendName}`
}

export function logEngineSelection(target: BrowserEngineTarget): void {
  log.info(
    'engine.target',
    `mode=${target.mode}; frontend=${target.frontendName || '-'}; endpoint=${target.endpoint || '-'}`
  )
}
