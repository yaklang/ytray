import { createHash } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import type { BrowserPlugin, ExtensionManifest, ExtensionRelease } from '../shared/types'
import { dataDirectory, pluginsDirectory } from './paths'
import { fetchJson, downloadFile, sha256File } from './download'
import { extractZip } from './archive'
import { compareVersions } from './versions'
import { YTrayError } from './errors'
import { log } from './diagnostics'

export const ENTERPRISE_VARIANT = 'chrome-enterprise'
export const MANAGED_EXTENSION_NAME = 'Yakit Browser Agent'
const MANIFEST_URL = 'https://aliyun-oss.yaklang.com/chrome-extension/manifest.json'

interface PluginManifestFile {
  name?: string
  version?: string
  key?: string
  manifest_version?: number
}

export function pluginsRoot(): string {
  return pluginsDirectory()
}

export function pluginDirectory(version: string): string {
  return path.join(pluginsRoot(), 'yakit-browser-agent', version)
}

export function readPluginManifest(directory: string): PluginManifestFile | null {
  try {
    return JSON.parse(fs.readFileSync(path.join(directory, 'manifest.json'), 'utf8')) as PluginManifestFile
  } catch {
    return null
  }
}

/** Chromium derives the extension id from the public key, or from the absolute path when unsigned. */
export function chromiumExtensionId(pluginPath: string, manifestKey?: string | null): string | null {
  if (!pluginPath.trim()) return null
  try {
    const identity = manifestKey?.trim()
      ? Buffer.from(manifestKey, 'base64')
      : Buffer.from(path.resolve(pluginPath), 'utf8')
    const digest = createHash('sha256').update(identity).digest()
    let id = ''
    for (let index = 0; index < 16; index += 1) {
      const byte = digest[index] as number
      id += String.fromCharCode(97 + (byte >> 4))
      id += String.fromCharCode(97 + (byte & 15))
    }
    return id
  } catch {
    return null
  }
}

export function pluginExtensionId(plugin: BrowserPlugin): string | null {
  const manifest = readPluginManifest(plugin.path)
  return chromiumExtensionId(plugin.path, manifest?.key)
}

export async function fetchExtensionManifest(): Promise<ExtensionManifest> {
  const raw = await fetchJson<{
    latest?: string
    updated_at?: string
    versions?: Array<{
      version?: string
      notes?: string[]
      published_at?: string
      artifacts?: Array<{
        variant?: string
        browser?: string
        mode?: string
        filename?: string
        url?: string
        sha256?: string
        size?: number | null
      }>
    }>
  }>(MANIFEST_URL)
  if (!raw.latest) throw new YTrayError('extensionInstallFailed', '插件清单格式无效')
  return {
    latest: raw.latest,
    updatedAt: raw.updated_at ?? '',
    versions: (raw.versions ?? []).map((version) => ({
      version: version.version ?? '',
      notes: version.notes ?? [],
      publishedAt: version.published_at ?? '',
      artifacts: (version.artifacts ?? []).map((artifact) => ({
        variant: artifact.variant ?? '',
        browser: artifact.browser ?? '',
        mode: artifact.mode ?? '',
        filename: artifact.filename ?? '',
        url: artifact.url ?? '',
        sha256: artifact.sha256 ?? '',
        size: artifact.size ?? null
      }))
    }))
  }
}

export function enterpriseArtifact(version: ExtensionRelease): ExtensionRelease['artifacts'][number] | null {
  return (
    version.artifacts.find(
      (artifact) => artifact.variant.toLowerCase() === ENTERPRISE_VARIANT && artifact.url.trim() !== ''
    ) ?? null
  )
}

export function resolveExtensionRoot(directory: string): string {
  if (fs.existsSync(path.join(directory, 'manifest.json'))) return directory
  const nested = fs
    .readdirSync(directory, { withFileTypes: true })
    .filter((entry) => entry.isDirectory())
    .map((entry) => path.join(directory, entry.name))
  if (nested.length === 1 && fs.existsSync(path.join(nested[0] as string, 'manifest.json'))) return nested[0] as string
  throw new YTrayError('extensionInstallFailed', '解压后未找到 manifest.json')
}

export async function installExtension(
  version: ExtensionRelease,
  onProgress?: (percent: number, message: string) => void
): Promise<string> {
  const artifact = enterpriseArtifact(version)
  if (!artifact) throw new YTrayError('extensionInstallFailed', `版本 ${version.version} 没有 ${ENTERPRISE_VARIANT} 产物`)

  const temporary = path.join(dataDirectory(), 'Logs', `.ytray-ext-${process.pid}-${Date.now()}.zip`)
  try {
    onProgress?.(2, '正在连接下载镜像…')
    await downloadFile(artifact.url, temporary, {
      expectedSize: artifact.size,
      errorCode: 'extensionInstallFailed',
      onProgress: (received, total) => {
        const percent = total ? Math.min(78, 5 + Math.floor((received * 73) / total)) : 5
        onProgress?.(percent, `正在下载 · ${received} / ${total ?? '?'}`)
      }
    })
    onProgress?.(81, '正在校验 SHA-256…')
    const digest = await sha256File(temporary)
    if (artifact.sha256 && digest.toLowerCase() !== artifact.sha256.toLowerCase()) {
      throw new YTrayError('extensionInstallFailed', 'SHA-256 校验失败')
    }

    const destination = pluginDirectory(version.version)
    fs.rmSync(destination, { recursive: true, force: true })
    fs.mkdirSync(destination, { recursive: true, mode: 0o700 })
    onProgress?.(84, '正在解压插件…')
    await extractZip(temporary, destination, {
      errorCode: 'extensionInstallFailed',
      onProgress: (completed, total) => onProgress?.(84 + Math.floor((completed * 15) / total), `正在解压 · ${completed} / ${total}`)
    })

    const root = resolveExtensionRoot(destination)
    onProgress?.(100, `插件 ${version.version} 安装完成`)
    log.info('extension.install', `installed ${version.version} at ${root}`)
    return root
  } finally {
    fs.rmSync(temporary, { force: true })
  }
}

export function cleanupOldPluginVersions(installedVersion: string): void {
  const root = path.join(pluginsRoot(), 'yakit-browser-agent')
  try {
    for (const entry of fs.readdirSync(root, { withFileTypes: true })) {
      if (!entry.isDirectory() || entry.name === installedVersion) continue
      fs.rmSync(path.join(root, entry.name), { recursive: true, force: true })
    }
  } catch {
    /* best-effort */
  }
}

export function shouldInstallBundled(bundled: string, installed: string | null, allowSame = false): boolean {
  if (!bundled) return false
  if (!installed) return true
  const comparison = compareVersions(bundled, installed)
  return comparison > 0 || (allowSame && comparison === 0)
}

export function managedPluginOptOutPath(): string {
  return path.join(pluginsRoot(), '.yakit-browser-agent-removed')
}

export function markManagedExtensionRemoved(): void {
  try {
    fs.mkdirSync(pluginsRoot(), { recursive: true })
    fs.writeFileSync(managedPluginOptOutPath(), new Date().toISOString(), { mode: 0o600 })
  } catch {
    /* ignore */
  }
}

export function clearManagedExtensionRemoved(): void {
  fs.rmSync(managedPluginOptOutPath(), { force: true })
}
