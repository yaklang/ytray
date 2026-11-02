import { createHash } from 'node:crypto'
import fs from 'node:fs'
import { YTrayError, type YTrayErrorCode } from './errors'
import { downloadToFile, getJson } from './net'

export async function fetchJson<T>(url: string, options: { maxBytes?: number; timeoutMs?: number } = {}): Promise<T> {
  return getJson<T>(url, { maxBytes: options.maxBytes ?? 4 * 1024 * 1024, timeoutMs: options.timeoutMs ?? 20000 })
}

export async function downloadFile(
  url: string,
  destination: string,
  options: {
    expectedSize?: number | null
    errorCode?: YTrayErrorCode
    onProgress?: (received: number, total: number | null) => void
  } = {}
): Promise<void> {
  const code = options.errorCode ?? 'downloadFailed'
  try {
    await downloadToFile(url, destination, { onProgress: options.onProgress })
  } catch (error) {
    fs.rmSync(destination, { force: true })
    throw new YTrayError(code, error instanceof Error ? error.message : String(error))
  }
}

export function sha256File(file: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    const stream = fs.createReadStream(file)
    stream.on('error', reject)
    stream.on('data', (chunk) => hash.update(chunk))
    stream.on('end', () => resolve(hash.digest('hex')))
  })
}
