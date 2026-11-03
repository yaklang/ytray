export type YTrayErrorCode =
  | 'noRuntime'
  | 'invalidExecutable'
  | 'invalidPlugin'
  | 'invalidURL'
  | 'invalidProxy'
  | 'invalidFlag'
  | 'launchFailed'
  | 'runtimeManifestFailed'
  | 'downloadFailed'
  | 'extensionInstallFailed'
  | 'screenshotFailed'

const templates: Record<YTrayErrorCode, (detail: string) => string> = {
  noRuntime: () => '没有可用浏览器；请选择本机浏览器，或安装一个 Chrome for Testing 版本',
  invalidExecutable: (d) => `找不到可执行的 Chrome：${d}`,
  invalidPlugin: (d) => `插件目录无效：${d}（所选目录或其子目录中必须包含 manifest.json）`,
  invalidURL: (d) => `启动地址无效：${d}`,
  invalidProxy: (d) => `HTTP 代理地址无效：${d}（例如 http://127.0.0.1:8083）`,
  invalidFlag: (d) => `不允许覆盖实例隔离或调试参数：${d}`,
  launchFailed: (d) => `浏览器启动失败：${d}`,
  runtimeManifestFailed: (d) => `获取浏览器版本失败：${d}`,
  downloadFailed: (d) => `运行时安装失败：${d}`,
  extensionInstallFailed: (d) => `插件下载失败：${d}`,
  screenshotFailed: (d) => `快速截图失败：${d}`
}

export class YTrayError extends Error {
  readonly code: YTrayErrorCode

  constructor(code: YTrayErrorCode, detail = '') {
    super(templates[code](detail))
    this.name = 'YTrayError'
    this.code = code
  }
}

export function errorMessage(error: unknown): string {
  if (error instanceof Error) return error.message
  return String(error)
}
