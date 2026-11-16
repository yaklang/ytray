#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class ControlActionResultTests
    {
        [TestMethod, TestCategory("ControlProtocol")]
        public void DeliveredInputSurvivesAReadbackFailureWithoutInvitingReplay()
        {
            var action = ControlJson.Result(new JObject { ["execution_state"] = "Completed" });
            var observation = ControlActionResult.ReadbackFailure(42, "Editor", "Window closed after Enter");
            var result = ControlActionResult.Combine(action, observation, "type_text");
            Assert.IsFalse(result.Value<bool>("isError"));
            var state = (JObject)result["structuredContent"]!;
            Assert.AreEqual("Completed", (string?)state["execution_state"]);
            Assert.IsFalse(state.Value<bool>("observation_available"));
            Assert.IsFalse(state.Value<bool>("automatic_replay_allowed"));
            Assert.IsNull(state["error"]);
            Assert.AreEqual("Window closed after Enter", (string?)state["observation_error"]?["message"]);
            Assert.AreEqual(42L, state.Value<long>("window_id"));
            StringAssert.Contains((string)result["content"]![0]!["text"]!, "不要重复执行");
            Assert.IsTrue(observation.Value<bool>("isError"), "Combining results must not mutate the original readback");
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void InputFailureAndUnavailableReadbackKeepSeparateCauses()
        {
            var action = new ControlException("InputTimeout", "Input may have been delivered", "Unknown").Result();
            var observation = ControlActionResult.ReadbackFailure(42, "Editor", "Worker disconnected");
            var result = ControlActionResult.Combine(action, observation, "submit");
            Assert.IsTrue(result.Value<bool>("isError"));
            var state = (JObject)result["structuredContent"]!;
            Assert.AreEqual("Unknown", (string?)state["execution_state"]);
            Assert.AreEqual("InputTimeout", (string?)state["error"]?["code"]);
            Assert.AreEqual("Worker disconnected", (string?)state["observation_error"]?["message"]);
            Assert.AreEqual("reobserve_do_not_replay", (string?)state["error"]?["recovery"]);
            Assert.IsTrue(state["error"]!.Value<bool>("text_sent"));
            Assert.IsFalse(state.Value<bool>("observation_available"));
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void FreshReadbackCannotEraseTheOriginalInputFailure()
        {
            var action = new ControlException("ElementDisabled", "Button is disabled").Result();
            var observation = ControlJson.Result(new JObject { ["window_id"] = 42 }, "[3] Button disabled");
            ((JArray)observation["content"]!).Add(new JObject { ["type"] = "image", ["data"] = "image-data" });
            var result = ControlActionResult.Combine(action, observation, "click");
            Assert.IsTrue(result.Value<bool>("isError"));
            Assert.IsTrue(result["structuredContent"]!.Value<bool>("observation_available"));
            Assert.AreEqual("NotStarted", (string?)result["structuredContent"]?["execution_state"]);
            Assert.AreEqual("ElementDisabled", (string?)result["structuredContent"]?["error"]?["code"]);
            StringAssert.Contains((string)result["content"]![0]!["text"]!, "[3] Button disabled");
            Assert.AreEqual("image-data", (string?)result["content"]?[1]?["data"]);
            Assert.AreEqual(2, ((JArray)result["content"]!).Count);
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void FlatWorkerFailureDoesNotCopyPrivateBookkeepingIntoPublicError()
        {
            var action = new JObject { ["isError"] = true, ["structuredContent"] = new JObject {
                ["code"] = "WorkerError", ["execution_state"] = "Unknown", ["session_id"] = "private", ["request_id"] = "private" },
                ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "Delivery uncertain" }) };
            var observation = ControlJson.Result(new JObject { ["window_id"] = 42 }, "Fresh state");
            var result = ControlActionResult.Combine(action, observation, "click");
            Assert.AreEqual("WorkerError", (string?)result["structuredContent"]?["error"]?["code"]);
            Assert.AreEqual("Unknown", (string?)result["structuredContent"]?["execution_state"]);
            Assert.IsNull(result["structuredContent"]?["error"]?["session_id"]);
            Assert.IsNull(result["structuredContent"]?["error"]?["request_id"]);
        }

        [TestMethod, TestCategory("ControlProtocol")]
        public void SuccessfulReadbackRemainsCompactAndIsNotDuplicated()
        {
            var action = ControlJson.Result(new JObject { ["completed"] = true });
            var observation = ControlJson.Result(new JObject { ["window_id"] = 42 }, "Updated controls");
            var result = ControlActionResult.Combine(action, observation, "press_key");
            Assert.IsFalse(result.Value<bool>("isError"));
            Assert.IsTrue(result["structuredContent"]!.Value<bool>("observation_available"));
            Assert.AreEqual("Updated controls", (string?)result["content"]?[0]?["text"]);
            Assert.AreEqual(1, ((JArray)result["content"]!).Count);
            Assert.IsNull(result["structuredContent"]?["tree_markdown"]);
        }
    }
}
