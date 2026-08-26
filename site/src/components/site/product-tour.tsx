"use client";

import Image from "next/image";
import * as React from "react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { type ProductPlatform, useProductPlatform } from "@/components/site/downloads";
import { assetPath } from "@/lib/site";

type PlatformImage = Record<ProductPlatform, {
  src: string;
  width: number;
  height: number;
  alt: string;
}>;

const screens = [
  {
    id: "quick",
    label: "快速配置",
    title: "直连、代理与自定义启动",
    description: "高频入口只保留三种明确选择；每次启动仍创建独立用户目录。",
    images: {
      macos: { src: "/assets/ytray-tab-quick.png", width: 1080, height: 748, alt: "YTray macOS 快速配置页面" },
      windows: { src: "/assets/ytray-windows-overview.png", width: 1268, height: 794, alt: "YTray Windows 运行中心" },
    } satisfies PlatformImage,
  },
  {
    id: "runtimes",
    label: "浏览器运行时",
    title: "本机浏览器与 Chrome for Testing",
    description: "自动发现系统浏览器，也可以选择并安装可固定版本的测试浏览器。",
    images: {
      macos: { src: "/assets/ytray-tab-runtimes.png", width: 1080, height: 748, alt: "YTray macOS 浏览器运行时页面" },
      windows: { src: "/assets/ytray-windows-runtimes.png", width: 1268, height: 794, alt: "YTray Windows 浏览器来源页面" },
    } satisfies PlatformImage,
  },
  {
    id: "settings",
    label: "代理与启动",
    title: "代理、调试与启动参数",
    description: "代理预设、启动地址、回环调试端口和附加参数都有明确落点。",
    images: {
      macos: { src: "/assets/ytray-tab-settings.png", width: 1080, height: 748, alt: "YTray macOS 启动设置页面" },
      windows: { src: "/assets/ytray-windows-proxy.png", width: 1268, height: 794, alt: "YTray Windows 代理与启动页面" },
    } satisfies PlatformImage,
  },
  {
    id: "instances",
    label: "运行与历史",
    title: "运行状态与身份恢复",
    description: "运行实例和停止后的历史分区展示，不用靠窗口标题猜测当前身份。",
    images: {
      macos: { src: "/assets/ytray-tab-instances.png", width: 1080, height: 748, alt: "YTray macOS 运行与历史页面" },
      windows: { src: "/assets/ytray-windows-instances.png", width: 1268, height: 794, alt: "YTray Windows 浏览器实例页面" },
    } satisfies PlatformImage,
  },
  {
    id: "plugins",
    label: "插件管理",
    title: "本地插件与 Yakit Browser Agent",
    description: "插件经过 manifest 校验；启用后自动进入新实例，自定义启动仍可临时调整。",
    images: {
      macos: { src: "/assets/ytray-tab-plugins.png", width: 1080, height: 748, alt: "YTray macOS 插件管理页面" },
      windows: { src: "/assets/ytray-windows-plugins.png", width: 1268, height: 794, alt: "YTray Windows 本地插件页面" },
    } satisfies PlatformImage,
  },
  {
    id: "launch-at-login",
    label: "开机启动",
    title: "登录系统后只驻留托盘",
    description: "自动启动的是 YTray，不是浏览器；状态、系统入口和关闭边界都写清楚。",
    images: {
      macos: { src: "/assets/ytray-tab-launch-at-login.png", width: 1080, height: 748, alt: "YTray macOS 开机启动页面" },
      windows: { src: "/assets/ytray-windows-startup.png", width: 1268, height: 794, alt: "YTray Windows 开机启动页面" },
    } satisfies PlatformImage,
  },
] as const;

export function ProductTour() {
  const [activeIndex, setActiveIndex] = React.useState(0);
  const tabRefs = React.useRef<Array<HTMLButtonElement | null>>([]);
  const reduceMotion = useReducedMotion();
  const platform = useProductPlatform();
  const active = screens[activeIndex];
  const activeImage = platform ? active.images[platform] : null;

  function select(index: number) {
    const next = (index + screens.length) % screens.length;
    setActiveIndex(next);
    tabRefs.current[next]?.focus();
  }

  return (
    <div className="product-tour" aria-busy={!activeImage}>
      <div className="product-tabs" role="tablist" aria-label="YTray 原生管理器页面">
        {screens.map((screen, index) => (
          <button
            key={screen.id}
            ref={(node) => { tabRefs.current[index] = node; }}
            id={`tour-tab-${screen.id}`}
            type="button"
            role="tab"
            aria-selected={index === activeIndex}
            aria-controls={`tour-panel-${screen.id}`}
            tabIndex={index === activeIndex ? 0 : -1}
            className="product-tab"
            onClick={() => setActiveIndex(index)}
            onKeyDown={(event) => {
              if (event.key === "ArrowRight") { event.preventDefault(); select(activeIndex + 1); }
              if (event.key === "ArrowLeft") { event.preventDefault(); select(activeIndex - 1); }
              if (event.key === "Home") { event.preventDefault(); select(0); }
              if (event.key === "End") { event.preventDefault(); select(screens.length - 1); }
            }}
          >
            {screen.label}
          </button>
        ))}
      </div>

      <div className="tour-copy" aria-live="polite">
        <p className="text-[13px] text-ink-muted">{platform === "windows" ? "Windows" : platform === "macos" ? "macOS" : "正在识别系统"} · {active.label}</p>
        <h3 className="mt-2 text-[clamp(24px,3vw,40px)] font-medium tracking-[-0.03em]">{active.title}</h3>
        <p className="mt-3 max-w-[54ch] text-[17px] leading-[1.6] text-ink-muted">{active.description}</p>
      </div>

      <div
        id={`tour-panel-${active.id}`}
        role="tabpanel"
        aria-labelledby={`tour-tab-${active.id}`}
        tabIndex={0}
        className="tour-panel"
      >
        {activeImage ? <AnimatePresence mode="wait" initial={false}>
          <motion.figure
            key={`${platform}-${active.id}`}
            initial={reduceMotion ? false : { opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            exit={reduceMotion ? { opacity: 1 } : { opacity: 0, y: -8 }}
            transition={{ duration: reduceMotion ? 0 : 0.32, ease: [0.16, 1, 0.3, 1] }}
          >
            <Image
              src={assetPath(activeImage.src)}
              width={activeImage.width}
              height={activeImage.height}
              sizes="(max-width: 768px) 100vw, 1200px"
              alt={activeImage.alt}
              className="h-auto w-full object-contain"
            />
          </motion.figure>
        </AnimatePresence> : <div className="tour-panel-placeholder" />}
      </div>
    </div>
  );
}
