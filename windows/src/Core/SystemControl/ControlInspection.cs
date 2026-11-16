#nullable enable
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using YTray.Models;
using YTray.Native;

namespace YTray.Core.SystemControl
{
    internal sealed class ControlInspection
    {
        internal readonly WindowsControlTarget? Target;
        internal readonly JObject Layout;
        internal readonly JObject? Image;
        internal readonly DateTime ObservedAt = DateTime.Now;

        internal ControlInspection(WindowsControlTarget? target, JObject layout, JObject? image)
        { Target = target; Layout = layout; Image = image; }
    }

    public sealed partial class SystemControlController
    {
        internal bool CanInspectDesktop => _grant?.AllowDesktop == true;

        internal Task<WindowsControlTarget[]> ListInspectionWindowsAsync(CancellationToken cancellation) =>
            Task.Run(() => {
                if (!IsEnabled) throw new ControlException("ControlDisabled", "请在电脑控制页开启电脑控制。");
                WindowsControlTarget.RequireDesktop();
                return CurrentWindows();
            }, cancellation);

        internal async Task<ControlInspection> InspectAsync(WindowsControlTarget? selected, bool screenshot, CancellationToken cancellation)
        {
            await _interaction.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                await ReadyAsync(cancellation).ConfigureAwait(false);
                selected?.Revalidate();
                var target = selected == null ? null : RegisterTarget(selected.Handle.ToInt64());
                if (selected != null && (target!.Pid != selected.Pid || target.Created != selected.Created))
                    throw new ControlException("TargetChanged", "窗口已关闭或被替换，请刷新窗口列表。");
                var options = new JObject { ["detail"] = "full", ["include_screenshot"] = screenshot };
                var raw = await ObserveSimpleAsync(target, options, new ControlTiming(), cancellation, inspection: true).ConfigureAwait(false);
                if (raw["isError"]?.Value<bool>() == true)
                    throw new ControlException((string?)raw["structuredContent"]?["error"]?["code"] ?? "InspectionFailed", ActionErrorMessage(raw));
                var layout = (JObject)raw["structuredContent"]!;
                if (target != null)
                    layout["focus"] = await ReadNativeContextAsync(target, layout, null, cancellation).ConfigureAwait(false);
                JObject? image = null;
                foreach (var item in (JArray)raw["content"]!) {
                    if ((string?)item["type"] == "image") { image = (JObject)item; break; }
                }
                return new ControlInspection(target, layout, image);
            } finally { _interaction.Release(); }
        }

        internal async Task HighlightInspectionAsync(WindowsControlTarget target, string elementToken, CancellationToken cancellation)
        {
            var inspection = await InspectAsync(target, false, cancellation).ConfigureAwait(false);
            var row = (inspection.Layout["elements"] as JArray)?.OfType<JObject>().FirstOrDefault(element =>
                (string?)element["element_token"] == elementToken && element["offscreen"]?.Value<bool>() != true);
            if (!(row?["frame"] is JObject frame) || inspection.Target == null)
                throw new ControlException("ElementChanged", "控件已经移动或消失，请重新分析。");
            await _interaction.WaitAsync(cancellation).ConfigureAwait(false);
            try {
                await ReadyAsync(cancellation).ConfigureAwait(false);
                var current = inspection.Target;
                current.Revalidate(requireGeometry: true);
                if (!Allowed(current)) throw new ControlException("TargetUnavailable", "窗口当前无法分析。");
                if (_grant is { AllowForeground: true, ReadOnly: false })
                    await WindowsApplicationControl.ActivateAsync(current, cancellation).ConfigureAwait(false);
                var parameters = new JObject {
                    ["window_id"] = current.Handle.ToInt64(), ["pid"] = current.Pid, ["created"] = current.Created,
                    ["window_bounds"] = inspection.Layout["window_bounds"], ["frame"] = frame
                };
                var result = await RequestNativeAsync(_connection!, "inspection/highlight", parameters, _lifetime!.Token,
                    cancellation, 2000).ConfigureAwait(false);
                if (result["isError"]?.Value<bool>() == true)
                    throw new ControlException("HighlightUnavailable", ActionErrorMessage(result));
            } finally { _interaction.Release(); }
        }
    }
}
