import { execFileSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import type { BrowserKind, BrowserRuntime } from '../shared/types'
import { browserKindTitle, inferBrowserKind } from './identity'
import { ytrayId } from './ids'

interface Candidate {
  executable: string
  kind: BrowserKind
}

/**
 * Linux distributions install Chromium browsers as plain executables instead of
 * .app bundles. Paths below cover Debian/Ubuntu, Fedora, Arch, snap and the
 * official Google/Microsoft packages.
 */
const candidates: Candidate[] = [
  { executable: '/usr/bin/google-chrome-stable', kind: 'chrome' },
  { executable: '/usr/bin/google-chrome', kind: 'chrome' },
  { executable: '/opt/google/chrome/chrome', kind: 'chrome' },
  { executable: '/usr/bin/google-chrome-beta', kind: 'chromeBeta' },
  { executable: '/usr/bin/google-chrome-unstable', kind: 'chromeCanary' },
  { executable: '/usr/bin/chromium', kind: 'chromium' },
  { executable: '/usr/bin/chromium-browser', kind: 'chromium' },
  { executable: '/usr/lib/chromium/chromium', kind: 'chromium' },
  { executable: '/snap/bin/chromium', kind: 'chromium' },
  { executable: '/usr/bin/microsoft-edge-stable', kind: 'edge' },
  { executable: '/usr/bin/microsoft-edge', kind: 'edge' },
  { executable: '/opt/microsoft/msedge/msedge', kind: 'edge' },
  { executable: '/usr/bin/microsoft-edge-beta', kind: 'edge' },
  { executable: '/usr/bin/microsoft-edge-dev', kind: 'edge' }
]

export function platformArchitecture(): string {
  return process.arch === 'arm64' ? 'arm64' : process.arch === 'ia32' ? 'x86' : 'x64'
}

function isExecutable(file: string): boolean {
  try {
    const stat = fs.statSync(file)
    if (!stat.isFile()) return false
    fs.accessSync(file, fs.constants.X_OK)
    return true
  } catch {
    return false
  }
}

export function readBrowserVersion(executable: string): string | null {
  try {
    const output = execFileSync(executable, ['--version'], {
      encoding: 'utf8',
      timeout: 5000,
      stdio: ['ignore', 'pipe', 'pipe']
    }).trim()
    // "Google Chrome 141.0.7390.54", "Chromium 140.0.7339.80",
    // "Chromium 156.0.8078.79 Arch Linux" — take the first version-looking token.
    const match = /(\d+\.\d+(?:\.\d+)*(?:\.\d+)?)/.exec(output)
    return match?.[1] ?? null
  } catch {
    return null
  }
}

function buildRuntime(executable: string, kind: BrowserKind, source: 'system' | 'local'): BrowserRuntime {
  const version = readBrowserVersion(executable) ?? ''
  return {
    id: ytrayId(),
    name: browserKindTitle(kind),
    version: version || '版本未知',
    architecture: platformArchitecture(),
    executablePath: executable,
    source,
    browserKind: kind,
    createdAt: new Date().toISOString()
  }
}

export function discoverSystemBrowsers(): BrowserRuntime[] {
  const found: BrowserRuntime[] = []
  // Tracks both the candidate path and its resolved target so a symlinked
  // alias and the real binary are only registered once.
  const seen = new Set<string>()
  const seenIdentities = new Set<string>()

  for (const candidate of candidates) {
    if (seen.has(candidate.executable) || !isExecutable(candidate.executable)) continue

    let resolved = candidate.executable
    try {
      resolved = fs.realpathSync(candidate.executable)
    } catch {
      /* an unresolvable symlink still points at a working launcher */
    }
    if (seen.has(resolved)) continue
    seen.add(candidate.executable)
    seen.add(resolved)

    const runtime = buildRuntime(candidate.executable, candidate.kind, 'system')
    // Distributions such as Arch ship a small launcher in /usr/bin next to the
    // real binary; both resolve to different paths but the same browser build.
    const identity = `${candidate.kind}:${runtime.version}`
    if (runtime.version !== '版本未知' && seenIdentities.has(identity)) continue
    seenIdentities.add(identity)
    found.push(runtime)
  }
  return found
}

export function inspectBrowser(selectedPath: string): BrowserRuntime | null {
  const trimmed = selectedPath.trim()
  if (!trimmed || !isExecutable(trimmed)) return null
  const kind = inferBrowserKind(path.basename(trimmed), trimmed)
  const systemRoot = ['/usr/bin/', '/usr/local/bin/', '/opt/', '/usr/lib/', '/snap/bin/'].some((prefix) =>
    trimmed.startsWith(prefix)
  )
  return buildRuntime(trimmed, kind, systemRoot ? 'system' : 'local')
}
