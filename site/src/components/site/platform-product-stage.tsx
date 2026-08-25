"use client";

import Image from "next/image";
import { motion, useReducedMotion } from "motion/react";
import { useProductPlatform } from "@/components/site/downloads";
import { assetPath } from "@/lib/site";

const stages = {
  macos: {
    label: "macOS 14+ · SwiftUI / AppKit",
    main: { src: "/assets/ytray-tab-quick.png", width: 1080, height: 748, alt: "YTray macOS 快速配置页面" },
    widget: { src: "/assets/ytray-widget.png", width: 780, height: 1492, alt: "YTray macOS 菜单栏小组件" },
    proof: { src: "/assets/ytray-dock-identities.png", width: 372, height: 124, alt: "macOS Dock 中带身份角标的浏览器实例" },
  },
  windows: {
    label: "Windows 10 / 11 · C# / WPF",
    main: { src: "/assets/ytray-windows-instances.png", width: 1268, height: 794, alt: "YTray Windows 浏览器实例与页面预览" },
    widget: { src: "/assets/ytray-windows-widget.png", width: 414, height: 538, alt: "YTray Windows 悬浮面板" },
    proof: null,
  },
} as const;

export function PlatformProductStage() {
  const platform = useProductPlatform();
  const reduceMotion = useReducedMotion();

  if (!platform) {
    return <div className="hero-stage hero-stage-loading mt-12 w-full sm:mt-16 lg:mt-20" aria-label="正在识别当前操作系统" aria-busy="true" />;
  }

  const stage = stages[platform];
  return (
    <motion.div
      key={platform}
      className={`hero-stage hero-stage-${platform} mt-12 w-full sm:mt-16 lg:mt-20`}
      initial={reduceMotion ? false : { opacity: 0, y: 18, scale: 0.99 }}
      animate={{ opacity: 1, y: 0, scale: 1 }}
      transition={{ duration: reduceMotion ? 0 : 0.55, ease: [0.16, 1, 0.3, 1] }}
    >
      <span className="hero-platform-label">{stage.label}</span>
      <Image
        src={assetPath(stage.main.src)}
        width={stage.main.width}
        height={stage.main.height}
        priority
        sizes="(max-width: 768px) 1050px, (max-width: 1440px) 88vw, 1220px"
        alt={stage.main.alt}
        className="hero-main-shot"
      />
      <div className="hero-widget-frame">
        <Image
          src={assetPath(stage.widget.src)}
          width={stage.widget.width}
          height={stage.widget.height}
          sizes="(max-width: 768px) 230px, 330px"
          alt={stage.widget.alt}
          className="hero-widget-shot"
        />
      </div>
      {stage.proof ? (
        <Image
          src={assetPath(stage.proof.src)}
          width={stage.proof.width}
          height={stage.proof.height}
          sizes="(max-width: 768px) 260px, 372px"
          alt={stage.proof.alt}
          className="hero-dock-shot"
        />
      ) : null}
    </motion.div>
  );
}
