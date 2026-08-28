import { BrandMark } from "@/components/site/brand-mark";

export default function NotFound() {
  return (
    <main className="flex min-h-dvh items-center justify-center px-6">
      <div className="max-w-[720px] text-center">
        <BrandMark variant="flat" className="mx-auto mb-10 size-20" />
        <p className="eyebrow">404</p>
        <h1 className="display-l mt-5">这个实例不存在。</h1>
        <p className="mt-7 text-[19px] leading-8 text-ink-muted">页面可能已经移动，或者链接少了一段路径。</p>
        <a className="action-link mt-8" href="/ytray/">返回 YTray 首页 <span aria-hidden="true">›</span></a>
      </div>
    </main>
  );
}
