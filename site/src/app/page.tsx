import Image from "next/image";
import StaggeredText from "@/components/react-bits/staggered-text";
import { BrandMark } from "@/components/site/brand-mark";
import { CapabilityStage } from "@/components/site/capability-stage";
import { CopyCommand } from "@/components/site/copy-command";
import {
  AssetDownloadLink,
  DownloadProvider,
  ReleaseVersion,
  SmartDownloadButton,
} from "@/components/site/downloads";
import { ProductTour } from "@/components/site/product-tour";
import { SiteHeader } from "@/components/site/header";
import { Reveal } from "@/components/site/reveal";
import { assetPath, siteConfig } from "@/lib/site";

const faqs = [
  {
    question: "YTray 到底解决什么问题？",
    answer:
      "它把手工测试和研发联调里的浏览器身份变成可重复的本地环境。每个实例使用独立用户目录，Cookie、Local Storage、缓存、插件、代理、调试端口和历史不会混进另一个任务。",
  },
  {
    question: "它和 Chrome 自带的多用户配置有什么区别？",
    answer:
      "Chrome Profiles 很适合分开工作与个人账号；YTray 额外面向测试现场管理代理、回环调试端口、本地插件、CFT 版本、Dock 角标和停止后的身份恢复。两者可以按场景并存。",
  },
  {
    question: "YTray 会上传浏览器配置、Cookie 或代理凭据吗？",
    answer:
      "不会上传这些身份数据。它们保存在本机；调试服务只绑定 127.0.0.1。YTray 仅访问版本清单，以及你主动下载的浏览器、插件和安装包资源。",
  },
  {
    question: "代理为什么说免浏览器配置？",
    answer:
      "代理地址、认证和检测目标在 YTray 托盘中保存；启动实例时由 YTray 写入对应浏览器参数，用户名和密码认证通过临时本地扩展完成。你不需要进入每个浏览器的设置页重复修改。",
  },
  {
    question: "YTray 收费吗，代码开放吗？",
    answer:
      "YTray 当前免费，完整代码在 GitHub 公开。仓库目前尚未单列 LICENSE 文件；若要在法律意义上对外宣称“开源”，项目方仍需选择并补充明确许可证。",
  },
] as const;

const jsonLd = {
  "@context": "https://schema.org",
  "@graph": [
    {
      "@type": "SoftwareApplication",
      name: "YTray",
      applicationCategory: "DeveloperApplication",
      operatingSystem: "macOS 14 or later, Windows 10, Windows 11",
      description: siteConfig.description,
      url: `${siteConfig.url}/`,
      downloadUrl: siteConfig.latestRelease,
      softwareHelp: `${siteConfig.github}#readme`,
      codeRepository: siteConfig.github,
      isAccessibleForFree: true,
      offers: { "@type": "Offer", price: "0", priceCurrency: "USD" },
      screenshot: [
        `${siteConfig.url}/assets/ytray-tab-quick.png`,
        `${siteConfig.url}/assets/ytray-widget.png`,
        `${siteConfig.url}/assets/ytray-dock-identities.png`,
      ],
      audience: {
        "@type": "Audience",
        audienceType: "软件测试、Web 研发、安全测试与本地联调人员",
      },
      featureList: [
        "独立浏览器用户目录与身份恢复",
        "系统浏览器与 Chrome for Testing",
        "无需进入浏览器设置的 HTTP 代理",
        "本地插件与 Yakit Browser Agent",
        "回环地址调试端口",
        "Dock 身份角标",
        "SwiftUI/AppKit 与 WPF 原生实现",
      ],
    },
    {
      "@type": "FAQPage",
      mainEntity: faqs.map((item) => ({
        "@type": "Question",
        name: item.question,
        acceptedAnswer: { "@type": "Answer", text: item.answer },
      })),
    },
  ],
};

export default function Home() {
  return (
    <DownloadProvider>
      <script type="application/ld+json" dangerouslySetInnerHTML={{ __html: JSON.stringify(jsonLd) }} />
      <a className="skip-link" href="#main">跳到正文</a>
      <SiteHeader />

      <main id="main">
        <section id="product" className="hero-section" aria-labelledby="hero-title">
          <div className="hero-wash" aria-hidden="true" />
          <div className="mx-auto flex max-w-[1400px] flex-col items-center px-6 pb-16 pt-28 text-center lg:px-8 lg:pb-24 lg:pt-32">
            <BrandMark className="mb-6 size-20 rounded-[20px] sm:size-24" />
            <p className="mb-6 text-[13px] text-ink-muted"><ReleaseVersion /> 现已发布 · macOS 14+ · Windows 10/11</p>
            <StaggeredText
              id="hero-title"
              as="h1"
              text="为测试与研发而生的|浏览器隔离方案"
              separator="|"
              segmentBy="lines"
              direction="bottom"
              blur={false}
              delay={60}
              duration={0.8}
              from={{ opacity: 0, y: 24 }}
              to={{ opacity: 1, y: 0 }}
              className="hero-title"
            />
            <p className="mt-7 max-w-[38ch] text-balance text-[clamp(18px,1.9vw,27px)] font-normal leading-[1.4] tracking-[-0.02em] text-ink-muted">
              在本机浏览器与 Chrome for Testing 之间，为每个测试现场保留独立身份、代理、插件和调试入口。
            </p>
            <div className="mt-8 flex flex-wrap items-center justify-center gap-x-7 gap-y-4">
              <SmartDownloadButton />
              <a className="action-link" href={siteConfig.github}>查看完整源码 <span aria-hidden="true">›</span></a>
            </div>
            <p className="mt-5 text-[13px] text-ink-muted">免费 · 源码公开 · 浏览器身份数据保存在本机</p>

            <div className="hero-stage mt-12 w-full sm:mt-16 lg:mt-20">
              <Image
                src={assetPath("/assets/ytray-tab-quick.png")}
                width={1080}
                height={748}
                priority
                sizes="(max-width: 768px) 1050px, (max-width: 1440px) 88vw, 1220px"
                alt="YTray 原生管理器快速配置页面，显示直连、HTTP 代理和自定义启动入口"
                className="hero-main-shot"
              />
              <Image
                src={assetPath("/assets/ytray-widget.png")}
                width={780}
                height={1492}
                sizes="(max-width: 768px) 230px, 330px"
                alt="YTray 原生托盘小组件，显示代理、运行实例和历史"
                className="hero-widget-shot"
              />
              <Image
                src={assetPath("/assets/ytray-dock-identities.png")}
                width={372}
                height={124}
                sizes="(max-width: 768px) 260px, 372px"
                alt="macOS Dock 中三个带 A、B、C 角标的 Chrome for Testing 独立身份"
                className="hero-dock-shot"
              />
            </div>
          </div>
        </section>

        <section id="features" className="feature-screen" aria-labelledby="capabilities-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal className="max-w-[880px]">
              <p className="eyebrow">核心能力</p>
              <h2 id="capabilities-title" className="display-l mt-5">四个动作，接住完整测试现场。</h2>
              <p className="mt-6 max-w-[52ch] text-[18px] leading-[1.55] text-ink-muted">身份、浏览器版本、代理和插件各守边界，登录态不再串线。</p>
            </Reveal>
            <Reveal className="mt-14" delay={0.08}><CapabilityStage /></Reveal>
          </div>
        </section>

        <section id="tour" className="tour-section" aria-labelledby="tour-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal>
              <div className="max-w-[980px]">
                <p className="eyebrow">真实界面导览</p>
                <h2 id="tour-title" className="display-l mt-5">六个页面，配置各归其位。</h2>
                <p className="mt-6 max-w-[52ch] text-[18px] leading-[1.55] text-ink-muted">
                  全部由当前 macOS 原生代码本机渲染，不是概念稿。
                </p>
              </div>
            </Reveal>
            <Reveal className="mt-12" delay={0.08}><ProductTour /></Reveal>
          </div>
        </section>

        <section id="comparison" className="comparison-section" aria-labelledby="comparison-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal>
              <div className="max-w-[980px]">
                <p className="eyebrow">方案对比</p>
                <h2 id="comparison-title" className="display-l mt-5">专注本地测试现场，不替代所有工具。</h2>
              </div>
            </Reveal>
            <Reveal className="mt-12" delay={0.08}>
              <div className="comparison-scroll" tabIndex={0} aria-label="浏览器隔离方案对比表，可横向滚动">
                <table className="comparison-table">
                  <thead><tr><th scope="col">方案</th><th scope="col">最适合</th><th scope="col">身份模型</th><th scope="col">运行位置</th><th scope="col">与 YTray 的关系</th></tr></thead>
                  <tbody>
                    <tr><th scope="row"><a href="https://support.google.com/chrome/answer/2364824">Chrome Profiles</a></th><td>工作/个人账号分离</td><td>持久 Profile</td><td>本机 Chrome</td><td>缺少面向测试的代理、调试端口、CFT 与实例恢复编排</td></tr>
                    <tr><th scope="row"><a href="https://playwright.dev/docs/browser-contexts">Playwright Context</a></th><td>自动化测试隔离</td><td>快速、默认非持久</td><td>代码驱动</td><td>自动化更强；YTray 更适合人工联调与持续身份</td></tr>
                    <tr><th scope="row"><a href="https://developer.chrome.com/docs/automation-and-testing">Chrome for Testing</a></th><td>固定浏览器版本</td><td>浏览器发行物</td><td>本机或 CI</td><td>是 YTray 支持的浏览器来源，本身不管理身份生命周期</td></tr>
                    <tr><th scope="row"><a href="https://www.browserstack.com/docs/local-testing/overview">BrowserStack Local</a></th><td>远程浏览器/真机访问内网</td><td>远程测试会话</td><td>本地隧道 + 云端浏览器</td><td>跨设备覆盖更广；YTray 的身份与浏览器留在本机</td></tr>
                    <tr className="comparison-ytray"><th scope="row">YTray</th><td>本地测试与研发联调</td><td>持久、可恢复身份</td><td>macOS / Windows 原生</td><td>免费、源码公开；系统浏览器 + CFT + 代理 + 插件 + Dock 角标</td></tr>
                  </tbody>
                </table>
              </div>
              <p className="mt-6 max-w-[80ch] text-[13px] leading-6 text-ink-muted">比较依据为各项目官方文档。YTray 不提供指纹伪装、团队云同步或远程移动设备矩阵。</p>
            </Reveal>
          </div>
        </section>

        <section id="technical" className="technical-section" aria-labelledby="technical-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal>
              <div className="max-w-[980px]">
                <p className="eyebrow">原生、轻量、本地</p>
                <h2 id="technical-title" className="display-l mt-5">系统原生，所以轻而快。</h2>
                <p className="mt-6 max-w-[56ch] text-[18px] leading-[1.55] text-ink-muted">
                  macOS 使用 SwiftUI / AppKit，Windows 使用 C# / WPF；不额外携带一套 Web 运行时。
                </p>
              </div>
            </Reveal>
            <div className="mt-14 grid border-t border-hairline lg:grid-cols-2">
              <Reveal className="min-w-0 lg:pr-16">
                <dl className="technical-list">
                  <div><dt>身份存储</dt><dd>每实例独立用户目录，停止后可恢复</dd></div>
                  <div><dt>调试服务</dt><dd>仅绑定 127.0.0.1，端口占用自动避让</dd></div>
                  <div><dt>网络路径</dt><dd>无代理与 HTTP 代理显式分开</dd></div>
                  <div><dt>插件边界</dt><dd>manifest 校验，本地目录与内置 Yakit Browser Agent</dd></div>
                  <div><dt>开机启动</dt><dd>只驻留托盘，不自动打开浏览器</dd></div>
                  <div><dt>数据策略</dt><dd>浏览器配置、Cookie 与代理凭据不上传</dd></div>
                </dl>
              </Reveal>
              <Reveal className="min-w-0 border-t border-hairline pt-12 lg:border-l lg:border-t-0 lg:pl-16 lg:pt-12" delay={0.08}>
                <p className="mb-5 text-[15px] leading-6 text-ink-muted">macOS 14+ 安装 Xcode Command Line Tools 后，可直接从源码运行：</p>
                <CopyCommand />
                <a className="action-link mt-6" href={`${siteConfig.github}#本地开发`}>查看完整开发说明 <span aria-hidden="true">›</span></a>
              </Reveal>
            </div>
          </div>
        </section>

        <section id="download" className="download-section" aria-labelledby="download-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal>
              <div className="max-w-[900px]">
                <p className="eyebrow">下载 YTray</p>
                <h2 id="download-title" className="display-l mt-5">下载安装到托盘，马上分开测试身份。</h2>
                <p className="mt-6 max-w-[48ch] text-[18px] leading-[1.55] text-ink-muted">macOS 与 Windows 四种安装包独立构建，并随 Release 提供校验值。</p>
              </div>
            </Reveal>
            <div className="platform-lineup mt-14 grid border-t border-hairline sm:grid-cols-2 lg:grid-cols-4">
              <Platform name="Apple Silicon" system="macOS 14+" detail="M1 / M2 / M3 / M4" asset="darwin:arm64" label="下载 arm64" />
              <Platform name="Intel Mac" system="macOS 14+" detail="Intel 处理器" asset="darwin:amd64" label="下载 amd64" />
              <Platform name="Windows x64" system="Windows 10 / 11" detail="推荐给大多数设备" asset="windows:amd64" label="下载 x64" />
              <Platform name="Windows x86" system="Windows 10 / 11" detail="32 位兼容版本" asset="windows:386" label="下载 x86" />
            </div>
            <p className="mt-6 text-[13px] leading-6 text-ink-muted">Windows on ARM 当前可通过系统兼容层运行 x64 版本，暂未提供原生 ARM64 安装包。</p>
          </div>
        </section>

        <section className="faq-section" aria-labelledby="faq-title">
          <div className="mx-auto grid max-w-[1400px] gap-12 px-6 py-24 lg:grid-cols-[0.72fr_1.28fr] lg:gap-24 lg:px-8 lg:py-32">
            <Reveal><div className="lg:sticky lg:top-28"><p className="eyebrow">常见问题</p><h2 id="faq-title" className="display-l mt-5">先把能力与边界说清楚。</h2></div></Reveal>
            <div className="faq-list border-t border-hairline">
              {faqs.map((item, index) => (
                <Reveal key={item.question} delay={Math.min(index, 5) * 0.05}>
                  <details className="group border-b border-hairline">
                    <summary className="flex min-h-24 cursor-pointer list-none items-center justify-between gap-8 py-6 text-[19px] font-medium marker:hidden">{item.question}<span aria-hidden="true" className="faq-plus">+</span></summary>
                    <p className="max-w-[58ch] pb-8 pr-12 text-[17px] leading-[1.65] text-ink-muted">{item.answer}</p>
                  </details>
                </Reveal>
              ))}
            </div>
          </div>
        </section>

      </main>

      <footer className="border-t border-hairline bg-surface-2">
        <div className="mx-auto flex max-w-[1400px] flex-col gap-8 px-6 py-12 text-[13px] text-ink-muted sm:flex-row sm:items-center sm:justify-between lg:px-8">
          <div className="flex items-center gap-3 text-ink"><BrandMark className="size-8 rounded-[9px]" /><span className="font-medium">YTray</span></div>
          <p>Yaklang 公开源码项目 · 本地优先 · 为测试与研发而生</p>
          <div className="flex gap-6"><a className="hover:text-ink" href={siteConfig.github}>GitHub</a><a className="hover:text-ink" href={`${siteConfig.github}/issues`}>反馈问题</a></div>
        </div>
      </footer>
    </DownloadProvider>
  );
}

function Platform({ name, system, detail, asset, label }: {
  name: string;
  system: string;
  detail: string;
  asset: "darwin:arm64" | "darwin:amd64" | "windows:amd64" | "windows:386";
  label: string;
}) {
  return (
    <Reveal className="platform-item">
      <p className="text-[13px] text-ink-muted">{system}</p>
      <h3 className="mt-4 text-[24px] font-medium tracking-[-0.02em]">{name}</h3>
      <p className="mt-2 text-[15px] text-ink-muted">{detail}</p>
      <AssetDownloadLink asset={asset} className="action-link mt-8">{label} <span aria-hidden="true">↓</span></AssetDownloadLink>
    </Reveal>
  );
}
