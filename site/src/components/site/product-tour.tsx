"use client";

import Image from "next/image";
import * as React from "react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { assetPath } from "@/lib/site";

const screens = [
  {
    id: "quick",
    label: "快速配置",
    title: "直连、代理与自定义启动",
    description: "高频入口只保留三种明确选择；每次启动仍创建独立用户目录。",
    image: "/assets/ytray-tab-quick.png",
    alt: "YTray 快速配置页面，包含无代理、HTTP 代理和自定义启动入口",
  },
  {
    id: "runtimes",
    label: "浏览器运行时",
    title: "本机浏览器与 Chrome for Testing",
    description: "自动发现系统浏览器，也可以选择并安装可固定版本的测试浏览器。",
    image: "/assets/ytray-tab-runtimes.png",
    alt: "YTray 浏览器来源页面，显示系统 Chrome 与 Chrome for Testing 安装入口",
  },
  {
    id: "settings",
    label: "启动设置",
    title: "调试端口与浏览器边界",
    description: "启动地址、回环调试端口、WebRTC、通知和附加参数都有明确落点。",
    image: "/assets/ytray-tab-settings.png",
    alt: "YTray 启动设置页面，显示浏览器、调试端口和网络选项",
  },
  {
    id: "instances",
    label: "运行与历史",
    title: "运行状态与身份恢复",
    description: "运行实例和停止后的历史分区展示，不用靠窗口标题猜测当前身份。",
    image: "/assets/ytray-tab-instances.png",
    alt: "YTray 运行与历史页面，分区显示运行浏览器和历史身份",
  },
  {
    id: "plugins",
    label: "插件管理",
    title: "本地插件与 Yakit Browser Agent",
    description: "插件经过 manifest 校验；启用后自动进入新实例，自定义启动仍可临时调整。",
    image: "/assets/ytray-tab-plugins.png",
    alt: "YTray 插件管理页面，显示 Yakit 浏览器插件和本地插件入口",
  },
  {
    id: "launch-at-login",
    label: "开机启动",
    title: "登录系统后只驻留托盘",
    description: "自动启动的是 YTray，不是浏览器；状态、系统入口和关闭边界都写清楚。",
    image: "/assets/ytray-tab-launch-at-login.png",
    alt: "YTray 开机启动页面，显示启用状态和系统登录项入口",
  },
] as const;

export function ProductTour() {
  const [activeIndex, setActiveIndex] = React.useState(0);
  const tabRefs = React.useRef<Array<HTMLButtonElement | null>>([]);
  const reduceMotion = useReducedMotion();
  const active = screens[activeIndex];

  function select(index: number) {
    const next = (index + screens.length) % screens.length;
    setActiveIndex(next);
    tabRefs.current[next]?.focus();
  }

  return (
    <div className="product-tour">
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
        <p className="text-[13px] text-ink-muted">{active.label}</p>
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
        <AnimatePresence mode="wait" initial={false}>
          <motion.figure
            key={active.id}
            initial={reduceMotion ? false : { opacity: 0, y: 12 }}
            animate={{ opacity: 1, y: 0 }}
            exit={reduceMotion ? { opacity: 1 } : { opacity: 0, y: -8 }}
            transition={{ duration: reduceMotion ? 0 : 0.32, ease: [0.16, 1, 0.3, 1] }}
          >
            <Image
              src={assetPath(active.image)}
              width={1080}
              height={748}
              sizes="(max-width: 768px) 100vw, 1200px"
              alt={active.alt}
              className="h-auto w-full object-contain"
            />
          </motion.figure>
        </AnimatePresence>
      </div>
    </div>
  );
}
