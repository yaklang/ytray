#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace YTray.Core.SystemControl
{
    public sealed partial class SystemControlController
    {
        private long _feedbackSequence;
        private static bool HasOperationFeedback(string name) => name == "launch_app" || name == "activate_window" || name == "click" || name == "type_text" || name == "press_key" || name == "scroll";

        private async Task<bool> SendOperationFeedbackAsync(long id, string phase, long window = 0, string description = "", string context = "")
        {
            try {
                var connection = _connection;
                if (connection == null || !IsRunning) return false;
                var result = await connection.RequestAsync("feedback", new JObject { ["id"] = id, ["phase"] = phase, ["window_id"] = window,
                    ["description"] = description, ["context"] = context }, _lifetime?.Token ?? CancellationToken.None, 1800).ConfigureAwait(false);
                return result["structuredContent"]?["shown"]?.Value<bool>() == true;
            } catch { return false; }
        }
        private (long Window, string Description, string Context) DescribeOperation(string name, JObject arguments)
        {
            var window = (long?)arguments["window_id"] ?? 0;
            var context = "电脑桌面"; var control = ""; var role = "";
            lock (_sync) {
                var target = _targets.Values.FirstOrDefault(item => item.Handle.ToInt64() == window);
                if (target != null) {
                    context = target.Title;
                    if (arguments["element_index"] != null && _observations.TryGetValue(target.Reference, out var observation)) {
                        var selected = (observation.View["elements"] as JArray)?.OfType<JObject>()
                            .FirstOrDefault(element => (int?)element["element_index"] == (int?)arguments["element_index"]);
                        control = (string?)selected?["label"] ?? ""; role = (string?)selected?["role"] ?? "";
                    }
                }
            }
            string description;
            switch (name) {
                case "launch_app":
                    var app = Path.GetFileNameWithoutExtension((string?)arguments["app"] ?? "应用");
                    description = "打开 " + (app.Equals("chrome", StringComparison.OrdinalIgnoreCase) ? "Google Chrome" : app.Equals("msedge", StringComparison.OrdinalIgnoreCase) ? "Microsoft Edge" : app);
                    if (Uri.TryCreate((string?)arguments["url"], UriKind.Absolute, out var uri)) description += "，进入 " + uri.Host;
                    context = "启动应用并切换到前台"; break;
                case "activate_window": description = "切换到目标窗口"; break;
                case "click":
                    description = (string?)arguments["button"] == "right" ? "右键点击" : (int?)arguments["count"] == 2 ? "双击" : "点击";
                    description += role == "Edit" ? "输入框" : control.Length > 0 ? "「" + control + "」" : "界面上的指定位置"; break;
                case "type_text":
                    description = "输入 " + (((string?)arguments["text"] ?? "").Length) + " 个字符";
                    if (arguments["replace"]?.Value<bool>() == true) description += "，替换当前内容";
                    if (arguments["submit"]?.Value<bool>() == true) description += "并按回车提交";
                    break;
                case "press_key": description = "按下 " + ((string?)arguments["key"] ?? "").ToUpperInvariant().Replace("+", " + "); break;
                default:
                    var direction = (string?)arguments["direction"];
                    description = "滚动页面（" + (direction == "up" ? "向上" : direction == "left" ? "向左" : direction == "right" ? "向右" : "向下") + "）"; break;
            }
            return (window, description, context);
        }
    }
}
