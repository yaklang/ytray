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
    icons: [{ src: "/ytray/icon.svg", sizes: "any", type: "image/svg+xml" }],
  };
}
