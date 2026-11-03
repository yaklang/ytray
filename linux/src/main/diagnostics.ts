import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { logsDirectory } from './paths'

const MAIN_LOG_LIMIT = 5 * 1024 * 1024
const ERROR_LOG_LIMIT = 2 * 1024 * 1024
const BACKUPS = 3

const urlCredentials = /([a-z][a-z0-9+.-]*:\/\/)[^\s/@:]+:[^\s/@]+@/gi
const secretValues = /\b(password|passwd|pwd|token|secret)(\s*[:=]\s*)("[^"]*"|'[^']*'|[^\s,;\]}]+)/gi

function redact(text: string): string {
  const home = os.homedir()
  return text
    .replace(urlCredentials, '$1***:***@')
    .replace(secretValues, '$1$2***')
    .split(home)
    .join('~')
}

function rotate(file: string, limit: number): void {
  try {
    const stat = fs.statSync(file)
    if (stat.size < limit) return
    for (let index = BACKUPS - 1; index >= 1; index -= 1) {
      const from = `${file}.${index}`
      const to = `${file}.${index + 1}`
      if (fs.existsSync(from)) fs.renameSync(from, to)
    }
    fs.renameSync(file, `${file}.1`)
  } catch {
    /* rotation is best-effort */
  }
}

function append(file: string, limit: number, line: string): void {
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true })
    rotate(file, limit)
    fs.appendFileSync(file, redact(line) + '\n', { mode: 0o600 })
  } catch {
    /* logging must never break the app */
  }
}

function stamp(): string {
  return new Date().toISOString()
}

export const log = {
  info(scope: string, message: string): void {
    append(path.join(logsDirectory(), 'ytray.log'), MAIN_LOG_LIMIT, `${stamp()} INFO  [${scope}] ${message}`)
  },
  error(scope: string, error: unknown, message?: string): void {
    const detail = error instanceof Error ? (error.stack || `${error.name}: ${error.message}`) : String(error)
    append(
      path.join(logsDirectory(), 'ytray-errors.log'),
      ERROR_LOG_LIMIT,
      `${stamp()} ERROR [${scope}] ${message ? message + ' :: ' : ''}${detail}`
    )
    append(path.join(logsDirectory(), 'ytray.log'), MAIN_LOG_LIMIT, `${stamp()} ERROR [${scope}] ${message || detail}`)
  }
}
