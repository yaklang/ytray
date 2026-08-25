"use client";

import * as React from "react";
import { Button } from "@/components/ui/button";
import { cn } from "@/lib/utils";
import { siteConfig } from "@/lib/site";

type AssetKey = "darwin:arm64" | "darwin:amd64" | "windows:amd64" | "windows:386";
export type ProductPlatform = "macos" | "windows";

interface ReleaseAsset {
  platform?: string;
  architecture?: string;
  url?: string;
}

interface ReleaseManifest {
  version?: string;
  assets?: ReleaseAsset[];
}

interface DownloadState {
  version: string;
  assets: Partial<Record<AssetKey, string>>;
  recommended: AssetKey | null;
  productPlatform: ProductPlatform | null;
}

const DownloadContext = React.createContext<DownloadState>({
  version: "最新版",
  assets: {},
  recommended: null,
  productPlatform: null,
});

type DetectedPlatform = AssetKey | "other";

function detectPlatform(): DetectedPlatform {
  const platform = `${navigator.userAgent} ${navigator.platform}`.toLowerCase();
  if (platform.includes("win")) return "windows:amd64";
  if (platform.includes("mac")) return "darwin:arm64";
  return "other";
}

function productPlatformFor(asset: AssetKey | null): ProductPlatform {
  return asset?.startsWith("windows:") ? "windows" : "macos";
}

function subscribePlatform(onStoreChange: () => void) {
  // Hydration starts from the platform-neutral server snapshot. Notify once after mounting so
  // useSyncExternalStore reads the real browser platform without rendering both products first.
  const timer = window.setTimeout(onStoreChange, 0);
  return () => window.clearTimeout(timer);
}
const serverPlatformSnapshot = () => null;

export function DownloadProvider({ children }: { children: React.ReactNode }) {
  const detectedPlatform = React.useSyncExternalStore<DetectedPlatform | null>(
    subscribePlatform,
    detectPlatform,
    serverPlatformSnapshot,
  );
  const recommended = detectedPlatform === "other" ? null : detectedPlatform;
  const productPlatform = detectedPlatform === null ? null : productPlatformFor(recommended);
  const [releaseState, setReleaseState] = React.useState<Pick<DownloadState, "version" | "assets">>({
    version: "最新版",
    assets: {},
  });

  React.useEffect(() => {
    const controller = new AbortController();
    fetch(siteConfig.releaseManifest, {
      headers: { Accept: "application/json" },
      signal: controller.signal,
    })
      .then((response) => {
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        return response.json() as Promise<ReleaseManifest>;
      })
      .then((release) => {
        const assets: Partial<Record<AssetKey, string>> = {};
        release.assets?.forEach((asset) => {
          const key = `${asset.platform}:${asset.architecture}` as AssetKey;
          if (
            asset.url?.startsWith("https://aliyun-oss.yaklang.com/ytray/") &&
            ["darwin:arm64", "darwin:amd64", "windows:amd64", "windows:386"].includes(key)
          ) {
            assets[key] = asset.url;
          }
        });
        setReleaseState((current) => ({
          ...current,
          version: release.version ? `v${release.version}` : "最新版",
          assets,
        }));
      })
      .catch((error: unknown) => {
        if (error instanceof DOMException && error.name === "AbortError") return;
      });

    return () => controller.abort();
  }, []);

  return (
    <DownloadContext.Provider value={{ ...releaseState, recommended, productPlatform }}>
      {children}
    </DownloadContext.Provider>
  );
}

export function useProductPlatform() {
  return React.useContext(DownloadContext).productPlatform;
}

const labels: Record<AssetKey, string> = {
  "darwin:arm64": "下载 macOS",
  "darwin:amd64": "下载 Intel Mac 版",
  "windows:amd64": "下载 Windows x64",
  "windows:386": "下载 Windows x86",
};

export function SmartDownloadButton({
  compact = false,
  className,
}: {
  compact?: boolean;
  className?: string;
}) {
  const { assets, recommended } = React.useContext(DownloadContext);
  const key = recommended ?? "darwin:arm64";
  const href = assets[key] ?? siteConfig.latestRelease;

  return (
    <Button
      asChild
      size={compact ? "sm" : "lg"}
      className={cn(
        "rounded-full bg-action px-6 font-normal text-white shadow-none hover:bg-action-hover focus-visible:ring-action/30",
        compact ? "h-9 px-5 text-[13px]" : "h-12 text-[17px]",
        className,
      )}
    >
      <a href={href}>{recommended ? labels[key] : "下载最新版"}</a>
    </Button>
  );
}

export function ReleaseVersion({ className }: { className?: string }) {
  const { version } = React.useContext(DownloadContext);
  return <span className={className}>{version}</span>;
}

export function AssetDownloadLink({
  asset,
  children,
  className,
}: {
  asset: AssetKey;
  children: React.ReactNode;
  className?: string;
}) {
  const { assets } = React.useContext(DownloadContext);
  return (
    <a className={className} href={assets[asset] ?? siteConfig.latestRelease}>
      {children}
    </a>
  );
}
