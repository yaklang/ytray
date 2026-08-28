"use client";

import * as React from "react";
import { Menu } from "lucide-react";
import { BrandMark } from "@/components/site/brand-mark";
import { SmartDownloadButton } from "@/components/site/downloads";
import { Button } from "@/components/ui/button";
import {
  Sheet,
  SheetClose,
  SheetContent,
  SheetHeader,
  SheetTitle,
  SheetTrigger,
} from "@/components/ui/sheet";
import { cn } from "@/lib/utils";
import { navigation } from "@/lib/site";

export function SiteHeader() {
  const [scrolled, setScrolled] = React.useState(false);

  React.useEffect(() => {
    const update = () => setScrolled(window.scrollY > 12);
    update();
    window.addEventListener("scroll", update, { passive: true });
    return () => window.removeEventListener("scroll", update);
  }, []);

  return (
    <header
      className={cn(
        "fixed inset-x-0 top-0 z-50 h-16 border-b transition-[background-color,border-color] duration-300",
        scrolled
          ? "border-hairline bg-background/80 backdrop-blur-xl"
          : "border-transparent bg-transparent",
      )}
    >
      <div className="mx-auto flex h-full max-w-[1400px] items-center px-6 lg:px-8">
        <a href="#product" className="flex items-center gap-2.5 text-[15px] font-medium" aria-label="YTray 首页">
          <BrandMark className="size-8" />
          YTray
        </a>

        <nav className="ml-auto hidden items-center gap-8 lg:flex" aria-label="主导航">
          {navigation.map((item) => (
            <a
              key={item.label}
              href={item.href}
              className="text-[13px] font-normal text-ink-muted transition-colors duration-200 hover:text-ink"
            >
              {item.label}
            </a>
          ))}
        </nav>

        <div className="ml-auto hidden lg:block lg:ml-8">
          <SmartDownloadButton compact />
        </div>

        <div className="ml-auto lg:hidden">
          <Sheet>
            <SheetTrigger asChild>
              <Button variant="ghost" size="icon" className="size-11 rounded-xl" aria-label="打开导航菜单">
                <Menu className="size-5" />
              </Button>
            </SheetTrigger>
            <SheetContent side="top" className="min-h-dvh border-0 bg-background px-6 pt-20">
              <SheetHeader>
                <SheetTitle className="sr-only">YTray 导航</SheetTitle>
              </SheetHeader>
              <nav className="mt-8 flex flex-col" aria-label="移动端导航">
                {navigation.map((item) => (
                  <SheetClose asChild key={item.label}>
                    <a
                      href={item.href}
                      className="border-b border-hairline py-5 text-[clamp(28px,9vw,44px)] font-medium tracking-[-0.03em]"
                    >
                      {item.label}
                    </a>
                  </SheetClose>
                ))}
              </nav>
              <div className="mt-10">
                <SmartDownloadButton className="w-full" />
              </div>
            </SheetContent>
          </Sheet>
        </div>
      </div>
    </header>
  );
}
