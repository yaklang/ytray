import fs from 'node:fs'
import path from 'node:path'
import type { BrowserRuntime, MirrorVersion } from '../shared/types'
import { browserKindTitle } from './identity'
import { platformArchitecture } from './discovery'
import { dataDirectory, runtimesDirectory } from './paths'
import { fetchJson, downloadFile, sha256File } from './download'
import { extractZip } from './archive'
import { YTrayError } from './errors'
import { log } from './diagnostics'
import { ytrayId } from './ids'

const MANIFEST_URL = 'https://aliyun-oss.yaklang.com/browsers/chrome/manifest.json'

interface RawManifest {
  schema_version?: number
  product?: string
  versions?: Array<{
    version?: string
    artifacts?: Array<{
      os?: string
      arch?: string
      format?: string
      url?: string
      size?: number | null
      sha256?: string
    }>
  }>
}

export function runtimePlatform(): string {
  return `linux-${platformArchitecture()}`
}

export async function fetchRuntimeVersions(): Promise<MirrorVersion[]> {
  let raw: RawManifest
  try {
    raw = await fetchJson<RawManifest>(`${MANIFEST_URL}?ytray_runtime=${Math.floor(Date.now() / 1000)}`, {
      timeoutMs: 15000
    })
  } catch (error) {
    throw new YTrayError('runtimeManifestFailed', error instanceof Error ? error.message : String(error))
  }
  if (raw.product && raw.product.toLowerCase() !== 'chrome-for-testing') {
    throw new YTrayError('runtimeManifestFailed', '版本清单产品不匹配')
  }
  const versions = (raw.versions ?? [])
    .filter((version) => version.version)
    .map((version) => ({
      version: version.version as string,
      artifacts: (version.artifacts ?? []).map((artifact) => ({
        os: artifact.os ?? '',
        arch: artifact.arch ?? '',
        format: artifact.format ?? '',
        url: artifact.url ?? '',
        size: artifact.size ?? null,
        sha256: artifact.sha256 ?? ''
      }))
    }))
  if (versions.length === 0) throw new YTrayError('runtimeManifestFailed', '版本清单中没有可用版本')
  return versions
}

export function isCompatible(version: MirrorVersion): boolean {
  return version.artifacts.some(
    (artifact) =>
      artifact.os.toLowerCase() === 'linux' &&
      artifact.arch.toLowerCase() === platformArchitecture() &&
      artifact.format.toLowerCase() === 'zip'
  )
}

export function locateChrome(root: string): string | null {
  const direct = [
    path.join(root, 'chrome-linux64', 'chrome'),
    path.join(root, 'chrome-linux', 'chrome'),
    path.join(root, 'chrome')
  ]
  for (const candidate of direct) {
    try {
      fs.accessSync(candidate, fs.constants.X_OK)
      return candidate
    } catch {
      /* try the next layout */
    }
  }
  return null
}

function ensureInstallationIsIdle(destination: string, version: string): void {
  if (!fs.existsSync(destination)) return
  const root = path.resolve(destination) + path.sep
  for (const entry of fs.readdirSync('/proc')) {
    if (!/^\d+$/.test(entry)) continue
    try {
      const executable = fs.readlinkSync(`/proc/${entry}/exe`)
      if (executable.startsWith(root)) {
        throw new YTrayError(
          'downloadFailed',
          `Chrome for Testing ${version} 正在被使用，请关闭使用该版本的浏览器实例后重试`
        )
      }
    } catch (error) {
      if (error instanceof YTrayError) throw error
    }
  }
}

export async function installRuntime(
  version: MirrorVersion,
  onProgress?: (percent: number, message: string) => void
): Promise<BrowserRuntime> {
  const artifact = version.artifacts.find(
    (item) =>
      item.os.toLowerCase() === 'linux' &&
      item.arch.toLowerCase() === platformArchitecture() &&
      item.format.toLowerCase() === 'zip'
  )
  if (!artifact) throw new YTrayError('downloadFailed', `版本 ${version.version} 没有 ${runtimePlatform()} ZIP`)

  const destination = path.join(runtimesDirectory(), version.version, runtimePlatform())
  ensureInstallationIsIdle(destination, version.version)

  const temporaryZip = path.join(dataDirectory(), 'Logs', `.ytray-runtime-${process.pid}-${Date.now()}.zip`)
  const staging = `${destination}.installing-${Date.now()}`
  try {
    onProgress?.(2, '正在连接下载镜像…')
    await downloadFile(artifact.url, temporaryZip, {
      expectedSize: artifact.size,
      onProgress: (received, total) => {
        const percent = total ? Math.min(78, 5 + Math.floor((received * 73) / total)) : 5
        onProgress?.(percent, `正在下载 · ${received} / ${total ?? '?'}`)
      }
    })
    onProgress?.(81, '正在校验 SHA-256…')
    const digest = await sha256File(temporaryZip)
    if (artifact.sha256 && digest.toLowerCase() !== artifact.sha256.toLowerCase()) {
      throw new YTrayError('downloadFailed', 'SHA-256 校验失败')
    }

    onProgress?.(84, '正在解压浏览器…')
    await extractZip(temporaryZip, staging, {
      onProgress: (completed, total) =>
        onProgress?.(84 + Math.floor((completed * 15) / total), `正在解压 · ${completed} / ${total}`)
    })
    if (!locateChrome(staging)) throw new YTrayError('downloadFailed', 'ZIP 内未找到 Chrome 可执行文件')

    ensureInstallationIsIdle(destination, version.version)
    const backup = `${destination}.previous-${Date.now()}`
    fs.mkdirSync(path.dirname(destination), { recursive: true })
    if (fs.existsSync(destination)) fs.renameSync(destination, backup)
    try {
      fs.renameSync(staging, destination)
    } catch (error) {
      if (!fs.existsSync(destination) && fs.existsSync(backup)) fs.renameSync(backup, destination)
      throw error
    }
    fs.rmSync(backup, { recursive: true, force: true })

    const executable = locateChrome(destination)
    if (!executable) throw new YTrayError('downloadFailed', '安装目录内未找到 Chrome 可执行文件')
    fs.chmodSync(executable, 0o755)
    onProgress?.(100, `Chrome for Testing ${version.version} 安装完成`)
    log.info('runtime.install', `${version.version} -> ${executable}`)

    return {
      id: ytrayId(),
      name: `${browserKindTitle('chromeForTesting')} ${version.version}`,
      version: version.version,
      architecture: runtimePlatform(),
      executablePath: executable,
      source: 'managed',
      browserKind: 'chromeForTesting',
      createdAt: new Date().toISOString()
    }
  } finally {
    fs.rmSync(temporaryZip, { force: true })
    fs.rmSync(staging, { recursive: true, force: true })
  }
}

export function removeRuntimeFiles(runtime: BrowserRuntime): void {
  if (runtime.source !== 'managed') return
  const installation = path.resolve(runtimesDirectory(), runtime.version, runtimePlatform())
  const runtimesRoot = path.resolve(runtimesDirectory()) + path.sep
  const executable = path.resolve(runtime.executablePath)
  if (!installation.startsWith(runtimesRoot) || !executable.startsWith(installation + path.sep)) {
    throw new Error('浏览器不在 YTray 管理的安装目录中')
  }
  fs.rmSync(installation, { recursive: true, force: true })
  const versionDirectory = path.dirname(installation)
  if (fs.existsSync(versionDirectory) && fs.readdirSync(versionDirectory).length === 0) {
    fs.rmdirSync(versionDirectory)
  }
  log.info('runtime.uninstall', runtime.version)
}
