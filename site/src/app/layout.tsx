import type { Metadata, Viewport } from "next";
import { siteConfig } from "@/lib/site";
import "./globals.css";

export const metadata: Metadata = {
  metadataBase: new URL("https://yaklang.io"),
  title: {
    default: siteConfig.title,
    template: "%s · YTray",
  },
  description: siteConfig.description,
  applicationName: "YTray",
  keywords: [
    "YTray",
    "测试浏览器隔离",
    "浏览器身份隔离",
    "多身份浏览器",
    "浏览器实例管理",
    "浏览器隔离",
    "HTTP 代理",
    "Chrome 多开",
    "Chrome for Testing",
    "本地浏览器代理",
    "浏览器调试",
    "本地插件",
    "Yakit",
    "macOS 托盘应用",
    "Windows 托盘应用",
  ],
  authors: [{ name: "Yaklang", url: "https://yaklang.io" }],
  creator: "Yaklang",
  publisher: "Yaklang",
  category: "Developer Tools",
  referrer: "origin-when-cross-origin",
  alternates: {
    canonical: "/ytray/",
    languages: { "zh-CN": "/ytray/" },
  },
  openGraph: {
    type: "website",
    locale: "zh_CN",
    url: "/ytray/",
    siteName: "YTray",
    title: siteConfig.title,
    description: siteConfig.description,
    images: [
      {
        url: "/ytray/assets/ytray-tab-quick.png",
        width: 1080,
        height: 748,
        alt: "YTray 为测试与研发提供浏览器身份隔离、代理与自定义启动",
      },
    ],
  },
  twitter: {
    card: "summary_large_image",
    title: siteConfig.title,
    description: siteConfig.description,
    images: ["/ytray/assets/ytray-tab-quick.png"],
  },
  robots: {
    index: true,
    follow: true,
    googleBot: {
      index: true,
      follow: true,
      "max-image-preview": "large",
      "max-snippet": -1,
      "max-video-preview": -1,
    },
  },
  manifest: "/ytray/manifest.webmanifest",
  icons: {
    icon: "/ytray/icon.svg",
    shortcut: "/ytray/icon.svg",
    apple: "/ytray/icon.svg",
  },
};

export const viewport: Viewport = {
  width: "device-width",
  initialScale: 1,
  colorScheme: "light",
  themeColor: "#ffffff",
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="zh-CN" suppressHydrationWarning>
      <body suppressHydrationWarning>
        <noscript>
          <style>{`[style*="opacity: 0"] { opacity: 1 !important; transform: none !important; filter: none !important; }`}</style>
        </noscript>
        {children}
      </body>
    </html>
  );
}
