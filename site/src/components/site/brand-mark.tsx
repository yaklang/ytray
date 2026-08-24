import { cn } from "@/lib/utils";

export function BrandMark({ className }: { className?: string }) {
  return (
    <span className={cn("brand-mark", className)} aria-hidden="true">
      <i className="brand-orbit brand-orbit-outer" />
      <i className="brand-orbit brand-orbit-middle" />
      <i className="brand-orbit brand-orbit-inner" />
      <b className="brand-core" />
      <b className="brand-dot" />
    </span>
  );
}
