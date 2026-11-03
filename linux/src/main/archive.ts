import fs from 'node:fs'
import path from 'node:path'
import yauzl from 'yauzl'
import { YTrayError, type YTrayErrorCode } from './errors'

/**
 * Streaming zip extraction with explicit zip-slip protection. Used by both the
 * Chrome for Testing and the browser-plugin installers, which download archives
 * from the public OSS mirror.
 */
export function extractZip(
  zipPath: string,
  destination: string,
  options: {
    errorCode?: YTrayErrorCode
    onProgress?: (completed: number, total: number) => void
  } = {}
): Promise<void> {
  const code = options.errorCode ?? 'downloadFailed'
  return new Promise((resolve, reject) => {
    const root = path.resolve(destination)
    fs.mkdirSync(root, { recursive: true, mode: 0o700 })

    yauzl.open(zipPath, { lazyEntries: true, autoClose: true }, (openError, zip) => {
      if (openError || !zip) {
        reject(new YTrayError(code, '无法读取 ZIP 文件'))
        return
      }
      const total = Math.max(zip.entryCount, 1)
      let completed = 0

      const fail = (error: unknown): void => {
        zip.close()
        reject(error instanceof YTrayError ? error : new YTrayError(code, String(error)))
      }

      zip.on('error', fail)
      zip.on('end', () => resolve())
      zip.on('entry', (entry: yauzl.Entry) => {
        const target = path.resolve(root, entry.fileName)
        if (target !== root && !target.startsWith(root + path.sep)) {
          fail(new YTrayError(code, 'ZIP 包含不安全的文件路径'))
          return
        }

        if (entry.fileName.endsWith('/')) {
          fs.mkdirSync(target, { recursive: true })
          completed += 1
          options.onProgress?.(completed, total)
          zip.readEntry()
          return
        }

        zip.openReadStream(entry, (streamError, stream) => {
          if (streamError || !stream) {
            fail(new YTrayError(code, 'ZIP 项目无法解压'))
            return
          }
          fs.mkdirSync(path.dirname(target), { recursive: true })
          const output = fs.createWriteStream(target, { mode: 0o600 })
          stream.on('error', fail)
          output.on('error', fail)
          output.on('close', () => {
            // Preserve the Unix mode bits the archive carries; Chrome's binary must stay executable.
            const mode = (entry.externalFileAttributes >>> 16) & 0o777
            if (mode) {
              try {
                fs.chmodSync(target, mode)
              } catch {
                /* best-effort */
              }
            }
            completed += 1
            options.onProgress?.(completed, total)
            zip.readEntry()
          })
          stream.pipe(output)
        })
      })

      zip.readEntry()
    })
  })
}
