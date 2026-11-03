import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { spawn } from 'node:child_process'
import { appVersion, launchExecutable } from './paths'
import { downloadFile, fetchJson, sha256File } from './download'
import { compareVersions } from './versions'
import { YTrayError } from './errors'
import { log } from './diagnostics'

const MANIFEST_URL = 'https://aliyun-oss.yaklang.com/ytray/latest.json'
const MAX_MANIFEST_BYTES = 512 * 1024

export interface AppReleaseAsset {
  platform: string
  architecture: string
  kind: string
  filename: string
  url: string
  sha256: string
  size: number
}

export type UpdateCheckState = 'available' | 'up-to-date' | 'unsupported'

export interface UpdateCheckResult {
  state: UpdateCheckState
  release: AppRelease | null
}

export interface AppRelease {
  version: string
  notes: string | null
  asset: AppReleaseAsset
}

interface Manifest {
  schema_version?: number
  product?: string
  version?: string
  release_notes_text?: string
  release_notes?: string
  assets?: Array<Partial<AppReleaseAsset> & { release_notes_text?: string }>
}

export function currentArchitecture(): string {
  return process.arch === 'arm64' ? 'arm64' : 'x64'
}

export async function checkForUpdate(): Promise<UpdateCheckResult> {
  let manifest: Manifest
  try {
    manifest = await fetchJson<Manifest>(MANIFEST_URL, { maxBytes: MAX_MANIFEST_BYTES, timeoutMs: 20000 })
  } catch (error) {
    throw new YTrayError('downloadFailed', `检查更新失败：${error instanceof Error ? error.message : String(error)}`)
  }
  if (!manifest.version || !Array.isArray(manifest.assets)) {
    throw new YTrayError('downloadFailed', '更新清单格式无效')
  }
  if (compareVersions(manifest.version, appVersion) <= 0) {
    return { state: 'up-to-date', release: null }
  }

  const asset = manifest.assets.find(
    (item) =>
      (item.platform ?? '').toLowerCase() === 'linux' &&
      (item.architecture ?? '').toLowerCase() === currentArchitecture() &&
      (item.kind ?? '').toLowerCase() === 'appimage' &&
      Boolean(item.url)
  )
  if (!asset?.url || !asset.sha256) {
    // The remote is newer but ships nothing for this platform yet; treat it as
    // "unsupported" so the UI does not claim the app is up to date.
    return { state: 'unsupported', release: null }
  }

  return {
    state: 'available',
    release: {
      version: manifest.version,
      notes: manifest.release_notes_text ?? manifest.release_notes ?? asset.release_notes_text ?? null,
      asset: {
        platform: 'linux',
        architecture: currentArchitecture(),
        kind: 'AppImage',
        filename: asset.filename ?? `YTray-${manifest.version}-linux-${currentArchitecture()}.AppImage`,
        url: asset.url,
        sha256: asset.sha256,
        size: asset.size ?? 0
      }
    }
  }
}

export async function downloadUpdate(
  asset: AppReleaseAsset,
  onProgress: (percent: number, message: string) => void
): Promise<string> {
  const directory = path.join(os.tmpdir(), `ytray-update-${process.pid}`)
  fs.mkdirSync(directory, { recursive: true, mode: 0o700 })
  const target = path.join(directory, path.basename(asset.filename))

  onProgress(2, '正在下载更新…')
  await downloadFile(asset.url, target, {
    expectedSize: asset.size || null,
    onProgress: (received, total) => {
      const percent = total ? Math.min(94, 4 + Math.floor((received * 90) / total)) : 4
      onProgress(percent, `正在下载 · ${(received / 1024 / 1024).toFixed(1)} MB`)
    }
  })
  onProgress(96, '正在校验 SHA-256…')
  const digest = await sha256File(target)
  if (digest.toLowerCase() !== asset.sha256.toLowerCase()) {
    fs.rmSync(target, { force: true })
    throw new YTrayError('downloadFailed', '更新包 SHA-256 校验失败')
  }
  onProgress(100, '更新已就绪，重启后生效')
  log.info('update.download', `${asset.filename} verified`)
  return target
}

/**
 * AppImage cannot overwrite itself while running. A detached shell helper waits
 * for this process to exit, replaces the file, and relaunches the new version.
 */
export function applyUpdate(downloaded: string): void {
  const target = launchExecutable()
  if (!process.env.APPIMAGE) {
    throw new YTrayError('downloadFailed', '当前不是 AppImage 运行，请手动替换安装文件')
  }
  const script = path.join(path.dirname(downloaded), 'apply-update.sh')
  const content = `#!/bin/sh
set -e
while kill -0 ${process.pid} 2>/dev/null; do sleep 0.2; done
cp "${downloaded}" "${target}.new"
chmod 755 "${target}.new"
mv "${target}.new" "${target}"
rm -f "${downloaded}"
exec "${target}" --background
`
  fs.writeFileSync(script, content, { mode: 0o700 })
  const helper = spawn('/bin/sh', [script], { detached: true, stdio: 'ignore' })
  helper.unref()
  log.info('update.apply', `replacing ${target}`)
}
