import fs from 'node:fs'
import path from 'node:path'
import type {
  BrowserInstance,
  BrowserPlugin,
  BrowserRuntime,
  LaunchSettings
} from '../shared/types'
import { dataDirectory, statePath } from './paths'
import { defaultSettings, normalizeSettings } from './settings'
import { log } from './diagnostics'

export interface PersistedState {
  runtimes: BrowserRuntime[]
  plugins: BrowserPlugin[]
  instances: BrowserInstance[]
  settings: LaunchSettings
}

export function emptyState(): PersistedState {
  return { runtimes: [], plugins: [], instances: [], settings: defaultSettings() }
}

export function ensureDirectories(): void {
  for (const directory of ['Profiles', 'Plugins', 'Runtimes', 'Logs', 'Thumbnails', 'ProxyAuth', 'Screenshots']) {
    try {
      fs.mkdirSync(path.join(dataDirectory(), directory), { recursive: true, mode: 0o700 })
    } catch (error) {
      log.error('state.directory', error, directory)
    }
  }
  try {
    fs.chmodSync(dataDirectory(), 0o700)
  } catch {
    /* best-effort */
  }
}

export function loadState(): PersistedState {
  const file = statePath()
  if (!fs.existsSync(file)) return emptyState()
  try {
    const parsed = JSON.parse(fs.readFileSync(file, 'utf8')) as Partial<PersistedState>
    return {
      runtimes: Array.isArray(parsed.runtimes) ? parsed.runtimes : [],
      plugins: Array.isArray(parsed.plugins) ? parsed.plugins : [],
      instances: Array.isArray(parsed.instances) ? parsed.instances : [],
      settings: normalizeSettings(parsed.settings)
    }
  } catch (error) {
    log.error('state.load', error, 'state.json unreadable, starting from an empty state')
    return emptyState()
  }
}

/** Atomic replace with 0600 permissions, matching the macOS/Windows state file policy. */
export function saveState(state: PersistedState): void {
  const file = statePath()
  const temporary = `${file}.tmp-${process.pid}`
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true, mode: 0o700 })
    fs.writeFileSync(temporary, JSON.stringify(state, null, 2), { mode: 0o600 })
    fs.renameSync(temporary, file)
    fs.chmodSync(file, 0o600)
  } catch (error) {
    try {
      fs.rmSync(temporary, { force: true })
    } catch {
      /* ignore */
    }
    log.error('state.save', error)
  }
}
