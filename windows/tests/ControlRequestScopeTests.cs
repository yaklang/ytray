#nullable enable
using Microsoft.VisualStudio.TestTools.UnitTesting;
using YTray.Core.SystemControl;
using YTray.Models;

namespace YTray.Tests
{
    [TestClass]
    public class ControlRequestScopeTests
    {
        [TestMethod, TestCategory("ControlAuthorization")]
        public void ReconnectionKeepsTheSameGrantButReenablingRevokesQueuedWork()
        {
            var grant = new ControlSessionOptions();
            var scope = new ControlRequestScope(grant);
            scope.RequireActive(grant, true);
            var revoked = Assert.ThrowsExactly<ControlException>(() => scope.RequireActive(new ControlSessionOptions(), true));
            Assert.AreEqual("GrantRevoked", revoked.Code);
            Assert.AreEqual("NotStarted", revoked.ExecutionState);
            Assert.ThrowsExactly<ControlException>(() => scope.RequireActive(grant, false));
        }

        [TestMethod, TestCategory("ControlAuthorization")]
        public void RevocationDuringComposedInputRetainsTheAlreadyAttemptedInput()
        {
            var grant = new ControlSessionOptions();
            var typing = new ControlRequestScope(grant) { InputMayHaveExecuted = true };
            var queued = new ControlRequestScope(grant);
            var interrupted = Assert.ThrowsExactly<ControlException>(() => typing.RequireActive(null, false));
            Assert.AreEqual("Unknown", interrupted.ExecutionState);
            Assert.AreEqual("reobserve_do_not_replay", (string?)interrupted.Result()["structuredContent"]?["error"]?["recovery"]);
            var neverStarted = Assert.ThrowsExactly<ControlException>(() => queued.RequireActive(null, false));
            Assert.AreEqual("NotStarted", neverStarted.ExecutionState, "Input state must belong to one call, not leak to concurrent queued calls");
            Assert.AreEqual("Unknown", typing.Failure(new ControlException("ElementChanged", "Submit target disappeared after typing")).ExecutionState);
        }
    }
}
