import Image from "next/image";
import { cn } from "@/lib/utils";
import { assetPath } from "@/lib/site";

type BrandMarkVariant = "vector" | "flat" | "material";

const artwork: Record<BrandMarkVariant, string> = {
  vector: "/brand/ytray-vector.svg",
  flat: "/brand/ytray-flat.png",
  material: "/icon.png",
};

export function BrandMark({
  className,
  variant = "vector",
  preload = false,
}: {
  className?: string;
  variant?: BrandMarkVariant;
  preload?: boolean;
}) {
  return (
    <span className={cn("brand-mark", `brand-mark-${variant}`, className)} aria-hidden="true">
      <Image
        src={assetPath(artwork[variant])}
        width={1024}
        height={1024}
        alt=""
        className="size-full"
        preload={preload}
      />
    </span>
  );
}
