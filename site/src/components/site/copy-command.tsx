"use client";

import * as React from "react";
import { Check, Copy } from "lucide-react";
import { Button } from "@/components/ui/button";

const command = "git clone https://github.com/yaklang/ytray.git\ncd ytray\n./script/startup.sh";

export function CopyCommand() {
  const [copied, setCopied] = React.useState(false);

  async function copy() {
    await navigator.clipboard.writeText(command);
    setCopied(true);
    window.setTimeout(() => setCopied(false), 1600);
  }

  return (
    <div className="relative min-w-0 overflow-hidden rounded-[18px] bg-[#17181c] text-[#f5f5f7]">
      <div className="flex items-center justify-between border-b border-white/10 px-5 py-3">
        <span className="font-mono text-xs text-white/55">macOS · 从源码运行</span>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={copy}
          className="h-9 gap-2 rounded-lg px-3 text-xs text-white/70 hover:bg-white/10 hover:text-white"
          aria-label="复制本地运行命令"
        >
          {copied ? <Check className="size-3.5" /> : <Copy className="size-3.5" />}
          {copied ? "已复制" : "复制"}
        </Button>
      </div>
      <pre className="overflow-x-auto p-5 text-[14px] leading-7 text-[#d7d7db]">
        <code>
          <span className="text-[#9fa5ff]">git clone</span> https://github.com/yaklang/ytray.git{"\n"}
          <span className="text-[#9fa5ff]">cd</span> ytray{"\n"}
          <span className="text-[#9fa5ff]">./script/startup.sh</span>
        </code>
      </pre>
    </div>
  );
}
