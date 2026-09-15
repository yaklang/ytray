#nullable enable
using System;
using YTray.Models;

namespace YTray.Core
{
    public sealed class ExtensionLaunchPrompt
    {
        public string Message { get; }
        public bool CanSkipPlugins { get; }

        public ExtensionLaunchPrompt(BrowserRuntime runtime, LaunchSettings settings, int pluginCount, string? failure = null)
        {
            CanSkipPlugins = string.IsNullOrEmpty(settings.ProxyUsername)
                && string.IsNullOrEmpty(settings.ProxyPassword);
            Message = $"{runtime.DisplayTitle} 未能完成插件加载。YTray 已尝试现有加载方式及浏览器扩展接口。\n\n"
                + (string.IsNullOrWhiteSpace(failure) ? "" : "原因：" + failure + "\n\n")
                + (CanSkipPlugins
                    ? $"本次选择了 {pluginCount} 个插件。可以不加载这些插件继续启动；插件功能将不可用，其他启动配置保持不变。\n\n此选择仅对本次启动生效，不会关闭插件的默认加载设置。需要插件时，请选择 Chrome for Testing、Chromium 或 Edge。"
                    : "当前代理使用了账号或密码，代理认证同样依赖插件，因此无法通过“不加载插件”继续启动。\n\n请改用 Chrome for Testing、Chromium 或 Edge，或在代理设置中改用无需认证的代理。");
        }
    }
}
