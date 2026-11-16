#nullable enable
using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    // Input delivery and observing its effect are separate outcomes. A lost readback
    // must never turn a successful Enter/click into a retryable input failure.
    internal static class ControlActionResult
    {
        internal static JObject ReadbackFailure(long window, string title, string message)
        {
            var result = new ControlException("ObservationUnavailable", message).Result();
            result["structuredContent"]!["window_id"] = window;
            result["structuredContent"]!["title"] = title;
            return result;
        }

        internal static JObject Combine(JObject action, JObject observation, string step)
        {
            var result = (JObject)observation.DeepClone();
            var state = result["structuredContent"] as JObject;
            if (state == null) result["structuredContent"] = state = new JObject();
            var failed = action["isError"]?.Value<bool>() == true;
            var observed = observation["isError"]?.Value<bool>() != true;
            var error = failed ? ActionError(action, step) : null;
            var execution = (string?)error?["execution_state"] ?? "Completed";
            state["execution_state"] = execution;
            state["automatic_replay_allowed"] = false;
            state["observation_available"] = observed;
            if (!observed) {
                state["observation_error"] = state["error"]?.DeepClone() ?? new JObject { ["code"] = "ObservationUnavailable", ["message"] = Message(observation) };
                state.Remove("error");
            }
            result["isError"] = failed;
            if (error != null) {
                state["error"] = error;
                Append(result, (string)error["message"]!);
            } else if (!observed) {
                state["message"] = "动作已投递，但暂未取得最新界面；请重新观察，不要重复执行。";
                // The unavailable observation's text is not the outcome of the input.
                result["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = state["message"] });
            }
            if (!observed && failed) Append(result, "最新界面暂不可用；请重新观察后再继续，不要重复执行未知结果的动作。");
            return result;
        }

        internal static JObject ActionError(JObject action, string step)
        {
            var state = action["structuredContent"] as JObject ?? new JObject();
            var failure = state["error"] ?? state["refusal"] ?? state;
            var execution = (string?)failure["execution_state"] ?? (string?)state["execution_state"] ?? "Unknown";
            var error = new JObject {
                ["code"] = failure["code"] ?? "ActionIncomplete", ["message"] = Message(action),
                ["step"] = failure["step"] ?? new JValue(step), ["execution_state"] = execution,
                ["recovery"] = execution == "NotStarted" ? "review_request" : "reobserve_do_not_replay"
            };
            foreach (var field in new[] { "selection_sent", "text_sent", "verification" })
                if (failure[field] is JToken value) error[field] = value.DeepClone();
            if ((string?)error["step"] == "submit") error["text_sent"] ??= true;
            return error;
        }

        private static void Append(JObject result, string message)
        {
            if (!(result["content"] is JArray content)) result["content"] = content = new JArray();
            var text = content.OfType<JObject>().FirstOrDefault(c => (string?)c["type"] == "text");
            if (text == null) content.Insert(0, new JObject { ["type"] = "text", ["text"] = message });
            else text["text"] = ((string?)text["text"] ?? "") + "\n" + message;
        }

        internal static string Message(JObject result)
        {
            var state = result["structuredContent"];
            var message = (string?)state?["error"]?["message"] ?? (string?)state?["refusal"]?["message"] ?? (string?)state?["message"];
            if (string.IsNullOrWhiteSpace(message)) message = (result["content"] as JArray ?? new JArray())
                .Where(c => (string?)c["type"] == "text").Select(c => (string?)c["text"]).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
            if (message == null || string.IsNullOrWhiteSpace(message)) return "操作结果未确认，请查看已返回的最新界面";
            return message.Length > 800 ? message.Substring(0, 800) + "…" : message;
        }
    }
}
