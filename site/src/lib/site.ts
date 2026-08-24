export const siteConfig = {
  name: "YTray",
  title: "YTray — 为测试与研发而生的浏览器隔离方案",
  description:
    "YTray 是面向测试与研发的原生浏览器隔离方案，支持本机浏览器与 Chrome for Testing、免配置 HTTP 代理、本地插件、独立身份和历史恢复。",
  url: "https://yaklang.io/ytray",
  github: "https://github.com/yaklang/ytray",
  latestRelease: "https://github.com/yaklang/ytray/releases/latest",
  releaseManifest: "https://aliyun-oss.yaklang.com/ytray/latest.json",
} as const;

export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? "";

export function assetPath(path: string) {
  return `${basePath}${path.startsWith("/") ? path : `/${path}`}`;
}

export const navigation = [
  { label: "产品", href: "#product" },
  { label: "能力", href: "#features" },
  { label: "界面", href: "#tour" },
  { label: "对比", href: "#comparison" },
  { label: "技术", href: "#technical" },
  { label: "下载", href: "#download" },
] as const;
