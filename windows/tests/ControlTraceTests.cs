#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;

namespace YTray.Tests
{
    [TestClass]
    public class ControlTraceTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public async Task ReopenedHistoryGroupsTaskAcrossSessionsAndPreservesCallData()
        {
            var root = Path.Combine(Path.GetTempPath(), "ytray-trace-" + Guid.NewGuid().ToString("N"));
            var firstSession = new string('a', 32);
            var secondSession = new string('b', 32);
            var signature = new string('c', 64);
            try {
                string requestId;
                using (var trace = new ControlTrace(root)) {
                    trace.RegisterClient(firstSession, "Fixture Agent");
                    var args = new JObject { ["task_id"] = "same-task", ["text"] = "测试调用" };
                    var first = trace.Begin(firstSession + signature, new JValue(41), "type_text", args);
                    var second = trace.Begin(secondSession + signature, new JValue(41), "type_text", args);
                    var cancelled = trace.Begin(firstSession + signature, new JValue(42), "type_text", args);
                    requestId = first.Id;
                    Assert.AreEqual(first.RunId, second.RunId);
                    Assert.AreEqual(first.RunId, cancelled.RunId);
                    Assert.AreNotEqual(first.Session, second.Session);
                    var firstWithoutTask = trace.Begin(firstSession + signature, new JValue(50), "list_apps", new JObject());
                    var secondWithoutTask = trace.Begin(secondSession + signature, new JValue(50), "list_apps", new JObject());
                    Assert.AreNotEqual(firstWithoutTask.RunId, secondWithoutTask.RunId);
                    trace.Finish(first, 200, new JObject {
                        ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "readback" },
                            new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "screenshot-bytes" })
                    });
                    trace.Finish(second, 200, new JObject { ["isError"] = true });
                    trace.Finish(cancelled, 202, new JObject {
                        ["isError"] = true, ["structuredContent"] = new JObject {
                            ["error"] = new JObject { ["code"] = "Cancelled", ["selection_sent"] = true }
                        }
                    });
                    await trace.FlushAsync();
                }
                // Exercise records written by the previous release without changing their original call metadata.
                foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "ComputerControl", "Trace"), "trace-*.json")) {
                    var saved = JObject.Parse(File.ReadAllText(file));
                    saved["run_id"] = (string?)saved["session"] + "/" + (string?)saved["task_id"];
                    File.WriteAllText(file, saved.ToString());
                }
                using var reopened = new ControlTrace(root);
                var records = await reopened.ReadSavedAsync();
                Assert.AreEqual(3, records.Length);
                Assert.AreEqual(1, records.Select(call => (string?)call["run_id"]).Distinct().Count());
                Assert.AreEqual(2, records.Select(call => (string?)call["session"]).Distinct().Count());
                var record = records.Single(value => (string?)value["id"] == requestId);
                Assert.AreEqual("Fixture Agent", (string?)record["client"]);
                Assert.AreEqual("测试调用", (string?)record["request"]?["arguments"]?["text"]);
                Assert.AreEqual("readback", (string?)record["response"]?["content"]?[0]?["text"]);
                Assert.AreEqual("image/png", (string?)record["response"]?["content"]?[1]?["mimeType"]);
                Assert.IsNull(record["response"]?["content"]?[1]?["data"]);
                Assert.IsFalse(records.Any(value => value.ToString().Contains(signature)));
                var partial = records.Single(value => (string?)value["status"] == "cancelled");
                Assert.AreEqual(true, (bool?)partial["response"]?["structuredContent"]?["error"]?["selection_sent"]);
            } finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
