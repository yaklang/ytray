"use client";

import Image from "next/image";
import * as React from "react";
import { AnimatePresence, motion, useReducedMotion } from "motion/react";
import { assetPath } from "@/lib/site";

const capabilities = [
  {
    id: "identity",
    eyebrow: "身份隔离",
    title: "一个角标，就是一个测试现场",
    description: "独立目录、Cookie、缓存与历史；A / B / C 在 Dock 里一眼可辨。",
  },
  {
    id: "runtime",
    eyebrow: "本机浏览器 + CFT",
    title: "日常环境直接用，复现版本按需固定",
    description: "自动识别系统浏览器，也可以安装指定 Chrome for Testing。",
  },
  {
    id: "proxy",
    eyebrow: "免浏览器配置代理",
    title: "代理留在托盘，不打断调试",
    description: "直连与 HTTP 代理分开启动，认证和检测目标一次保存。",
  },
  {
    id: "plugin",
    eyebrow: "插件与 Yak 生态",
    title: "工具跟着身份进入现场",
    description: "本地插件与 Yakit Browser Agent 随实例加载，继续衔接 Yaklang 生态。",
  },
] as const;

type CapabilityId = (typeof capabilities)[number]["id"];

export function CapabilityStage() {
  const [activeIndex, setActiveIndex] = React.useState(0);
  const tabRefs = React.useRef<Array<HTMLButtonElement | null>>([]);
  const reduceMotion = useReducedMotion();
  const active = capabilities[activeIndex];

  function select(index: number) {
    const next = (index + capabilities.length) % capabilities.length;
    setActiveIndex(next);
    tabRefs.current[next]?.focus();
  }

  return (
    <div className="capability-stage">
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
            <CapabilityVisual type={active.id} />
          </motion.div>
        </AnimatePresence>
      </div>
    </div>
  );
}

function CapabilityVisual({ type }: { type: CapabilityId }) {
  if (type === "identity") {
    return (
      <>
        <Image className="capability-shot-main" src={assetPath("/assets/ytray-tab-instances.png")} width={1080} height={748} alt="YTray 运行与历史身份页面" />
        <Image className="capability-shot-dock" src={assetPath("/assets/ytray-dock-identities.png")} width={372} height={124} alt="Dock 中带 A、B、C 角标的三个独立浏览器身份" />
      </>
    );
  }

  if (type === "runtime") {
    return (
      <>
        <Image className="capability-shot-main" src={assetPath("/assets/ytray-tab-runtimes.png")} width={1080} height={748} alt="YTray 系统浏览器与 Chrome for Testing 页面" />
        <Image className="capability-shot-wizard" src={assetPath("/assets/ytray-custom-launch.png")} width={1440} height={1140} alt="背景不透明的 YTray 自定义启动向导" />
      </>
    );
  }

  if (type === "proxy") {
    return <Image className="capability-shot-widget" src={assetPath("/assets/ytray-widget.png")} width={780} height={1492} alt="YTray 代理、实例和历史托盘面板" />;
  }

  return <Image className="capability-shot-main" src={assetPath("/assets/ytray-tab-plugins.png")} width={1080} height={748} alt="YTray 本地插件与 Yakit Browser Agent 页面" />;
}
