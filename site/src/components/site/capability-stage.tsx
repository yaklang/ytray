"use client";

import Image from "next/image";
import * as React from "react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { type ProductPlatform, useProductPlatform } from "@/components/site/downloads";
import { assetPath } from "@/lib/site";

const capabilities = [
  {
    id: "identity",
    eyebrow: "身份隔离",
    title: "每个实例都有独立身份",
    description: "用户目录、Cookie、缓存和访问记录互不影响；A / B / C 角标让任务栏或 Dock 中的窗口一眼可辨。",
  },
  {
    id: "runtime",
    eyebrow: "本机浏览器 + CFT",
    title: "使用本机浏览器，或固定测试版本",
    description: "自动识别系统浏览器，也可以安装指定版本的 Chrome for Testing。",
  },
  {
    id: "proxy",
    eyebrow: "免浏览器配置代理",
    title: "启动时直接选择网络路径",
    description: "直连与 HTTP 代理分开启动，地址、认证和检测目标只需保存一次。",
  },
  {
    id: "plugin",
    eyebrow: "插件与 Yak 生态",
    title: "插件随新实例自动加载",
    description: "启用本地插件或 Yakit Browser Agent 后，新建实例会自动带上它们。",
  },
] as const;

type CapabilityId = (typeof capabilities)[number]["id"];

export function CapabilityStage() {
  const [activeIndex, setActiveIndex] = React.useState(0);
  const tabRefs = React.useRef<Array<HTMLButtonElement | null>>([]);
  const reduceMotion = useReducedMotion();
  const platform = useProductPlatform();
  const active = capabilities[activeIndex];

  function select(index: number) {
    const next = (index + capabilities.length) % capabilities.length;
    setActiveIndex(next);
    tabRefs.current[next]?.focus();
  }

  return (
    <div className="capability-stage" data-platform={platform ?? undefined}>
      <div className="capability-tabs" role="tablist" aria-label="YTray 核心能力">
        {capabilities.map((item, index) => (
          <button
            key={item.id}
            ref={(node) => { tabRefs.current[index] = node; }}
            id={`capability-tab-${item.id}`}
            type="button"
            role="tab"
            aria-selected={index === activeIndex}
            aria-controls={`capability-panel-${item.id}`}
            tabIndex={index === activeIndex ? 0 : -1}
            className="capability-tab"
            onClick={() => setActiveIndex(index)}
            onKeyDown={(event) => {
              if (event.key === "ArrowDown" || event.key === "ArrowRight") { event.preventDefault(); select(activeIndex + 1); }
              if (event.key === "ArrowUp" || event.key === "ArrowLeft") { event.preventDefault(); select(activeIndex - 1); }
              if (event.key === "Home") { event.preventDefault(); select(0); }
              if (event.key === "End") { event.preventDefault(); select(capabilities.length - 1); }
            }}
          >
            <span className="capability-index">0{index + 1}</span>
            <span>
              <span className="capability-eyebrow">{item.eyebrow}</span>
              <strong>{item.title}</strong>
              <small>{item.description}</small>
            </span>
          </button>
        ))}
      </div>

      <div
        id={`capability-panel-${active.id}`}
        role="tabpanel"
        aria-labelledby={`capability-tab-${active.id}`}
        className="capability-panel"
      >
        <AnimatePresence mode="wait" initial={false}>
          <motion.div
            key={active.id}
            className={`capability-visual capability-visual-${active.id}`}
            initial={reduceMotion ? false : { opacity: 0, scale: 0.985, y: 16 }}
            animate={{ opacity: 1, scale: 1, y: 0 }}
            exit={reduceMotion ? { opacity: 1 } : { opacity: 0, scale: 0.99, y: -10 }}
            transition={{ duration: reduceMotion ? 0 : 0.42, ease: [0.16, 1, 0.3, 1] }}
          >
            {platform ? <CapabilityVisual type={active.id} platform={platform} /> : null}
          </motion.div>
        </AnimatePresence>
      </div>
    </div>
  );
}

function CapabilityVisual({ type, platform }: { type: CapabilityId; platform: ProductPlatform }) {
  const windows = platform === "windows";

  if (type === "identity") {
    return (
      <>
        <Image className="capability-shot-main" src={assetPath(windows ? "/assets/ytray-windows-instances.png" : "/assets/ytray-tab-instances.png")} width={windows ? 1268 : 1080} height={windows ? 794 : 748} alt={`YTray ${windows ? "Windows" : "macOS"} 实例页面`} />
        <Image className="capability-shot-dock" src={assetPath(windows ? "/assets/ytray-windows-widget.png" : "/assets/ytray-dock-identities.png")} width={windows ? 414 : 372} height={windows ? 538 : 124} alt={windows ? "YTray Windows 悬浮面板中的浏览器实例" : "macOS Dock 中带 A、B、C 角标的浏览器身份"} />
      </>
    );
  }

  if (type === "runtime") {
    return (
      <>
        <Image className="capability-shot-main" src={assetPath(windows ? "/assets/ytray-windows-runtimes.png" : "/assets/ytray-tab-runtimes.png")} width={windows ? 1268 : 1080} height={windows ? 794 : 748} alt={`YTray ${windows ? "Windows" : "macOS"} 浏览器来源页面`} />
        {!windows ? <Image className="capability-shot-wizard" src={assetPath("/assets/ytray-custom-launch.png")} width={1440} height={1140} alt="YTray macOS 自定义启动向导" /> : null}
      </>
    );
  }

  if (type === "proxy") {
    return <Image className="capability-shot-widget" src={assetPath(windows ? "/assets/ytray-windows-widget.png" : "/assets/ytray-widget.png")} width={windows ? 414 : 780} height={windows ? 538 : 1492} alt={`YTray ${windows ? "Windows 悬浮" : "macOS 菜单栏"}面板`} />;
  }

  return <Image className="capability-shot-main" src={assetPath(windows ? "/assets/ytray-windows-plugins.png" : "/assets/ytray-tab-plugins.png")} width={windows ? 1268 : 1080} height={windows ? 794 : 748} alt={`YTray ${windows ? "Windows" : "macOS"} 本地插件与 Yakit Browser Agent 页面`} />;
}
