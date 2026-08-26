import type { MetadataRoute } from "next";

export const dynamic = "force-static";

export default function manifest(): MetadataRoute.Manifest {
  return {
    name: "YTray — 多身份浏览器实例工作台",
    short_name: "YTray",
    description: "隔离 Cookie、代理与插件环境，在原生托盘里管理浏览器实例。",
    start_url: "/ytray/",
    display: "standalone",
    background_color: "#ffffff",
    theme_color: "#ffffff",
    lang: "zh-CN",
    icons: [
      { src: "/ytray/icons/icon-192.png", sizes: "192x192", type: "image/png" },
      { src: "/ytray/icons/icon-512.png", sizes: "512x512", type: "image/png" },
    ],
  };
}
