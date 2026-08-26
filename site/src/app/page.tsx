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
import { PlatformProductStage } from "@/components/site/platform-product-stage";
import { SiteHeader } from "@/components/site/header";
import { Reveal } from "@/components/site/reveal";
import { siteConfig } from "@/lib/site";

const faqs = [
  {
    question: "YTray 到底解决什么问题？",
    answer:
      "它为每项调试任务创建独立的本地浏览器实例。每个实例都有自己的用户目录，Cookie、Local Storage、缓存、插件、代理、调试端口和访问记录不会混到其他任务中。",
  },
  {
    question: "它和 Chrome 自带的多用户配置有什么区别？",
    answer:
      "Chrome Profiles 很适合分开工作与个人账号；YTray 还可以为每个实例保存代理、回环调试端口、本地插件、Chrome for Testing 版本和桌面身份角标，并在停止后恢复原来的浏览器环境。两者可以按场景并存。",
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
    question: "应该从哪里下载和更新？",
    answer:
      "首次安装可以使用本站按系统推荐的正式安装包，也可以从 GitHub Releases 获取。之后 YTray 会在应用内检查、下载、校验并安装自身更新；Chrome for Testing 和 Yakit Browser Agent 也分别提供应用内更新。",
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
        `${siteConfig.url}/assets/ytray-windows-overview.png`,
        `${siteConfig.url}/assets/ytray-tab-quick.png`,
        `${siteConfig.url}/assets/ytray-windows-widget.png`,
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
        "任务栏与 Dock 身份角标",
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
            <BrandMark className="mb-6 size-20 rounded-[20px] sm:size-24" priority />
            <p className="mb-6 text-[13px] text-ink-muted"><ReleaseVersion /> 现已发布 · macOS 14+ · Windows 10/11</p>
            <h1 id="hero-title" className="hero-title">
              <span className="hero-title-line">为测试与研发而生的</span>
              <span className="hero-title-line">浏览器隔离方案</span>
            </h1>
            <p className="mt-7 max-w-[38ch] text-balance text-[clamp(18px,1.9vw,27px)] font-normal leading-[1.4] tracking-[-0.02em] text-ink-muted">
              把不同账号、代理与插件固定在各自独立的浏览器实例中，需要时随时启动、停止和恢复。
            </p>
            <div className="mt-8 flex flex-wrap items-center justify-center gap-x-7 gap-y-4">
              <SmartDownloadButton />
              <a className="action-link" href={siteConfig.github}>查看完整源码 <span aria-hidden="true">›</span></a>
            </div>
            <p className="mt-5 text-[13px] text-ink-muted">免费使用 · 源码可查 · 浏览器身份数据留在本机</p>

            <PlatformProductStage />
          </div>
        </section>

        <section id="features" className="feature-screen" aria-labelledby="capabilities-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal className="max-w-[880px]">
              <p className="eyebrow">核心能力</p>
              <h2 id="capabilities-title" className="display-l mt-5">一个实例，保留一套完整浏览器环境。</h2>
              <p className="mt-6 max-w-[52ch] text-[18px] leading-[1.55] text-ink-muted">浏览器版本、登录状态、代理和插件分别保存，多项任务可以同时进行。</p>
            </Reveal>
            <Reveal className="mt-14" delay={0.08}><CapabilityStage /></Reveal>
          </div>
        </section>

        <section id="tour" className="tour-section" aria-labelledby="tour-title">
          <div className="mx-auto max-w-[1400px] px-6 py-24 lg:px-8 lg:py-32">
            <Reveal>
              <div className="max-w-[980px]">
                <p className="eyebrow">真实界面导览</p>
                <h2 id="tour-title" className="display-l mt-5">常用配置与实例状态，一眼就能找到。</h2>
                <p className="mt-6 max-w-[52ch] text-[18px] leading-[1.55] text-ink-muted">
                  网站会根据当前系统展示对应的真实界面；所有截图均由 Windows 或 macOS 客户端实际渲染。
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
                <h2 id="comparison-title" className="display-l mt-5">适合本地人工调试，也能与自动化和云测试配合。</h2>
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
                    <tr className="comparison-ytray"><th scope="row">YTray</th><td>本地测试与研发联调</td><td>持久、可恢复身份</td><td>macOS / Windows 原生</td><td>免费使用、源码可查；系统浏览器 + CFT + 代理 + 插件 + 桌面角标</td></tr>
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
                <h2 id="technical-title" className="display-l mt-5">原生桌面实现，不额外捆绑 Web 运行时。</h2>
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
                <p className="mb-5 text-[15px] leading-6 text-ink-muted">根据当前系统展示对应的本地构建命令：</p>
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
                <h2 id="download-title" className="display-l mt-5">安装 YTray，开始管理独立浏览器实例。</h2>
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
