import Image from "next/image";
import { cn } from "@/lib/utils";
import { assetPath } from "@/lib/site";

export function BrandMark({ className, priority = false }: { className?: string; priority?: boolean }) {
  return (
    <span className={cn("brand-mark", className)} aria-hidden="true">
      <Image src={assetPath("/icon.png")} width={1024} height={1024} alt="" className="size-full" priority={priority} />
    </span>
  );
}
