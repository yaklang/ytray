#nullable enable
using YTray.Models;

namespace YTray.Core.SystemControl
{
    // One public call owns this mutable record across awaits and composed inputs.
    // Worker reconnection preserves the grant; a user toggle replaces it.
    internal sealed class ControlRequestScope
    {
        private readonly ControlSessionOptions _grant;
        internal bool InputMayHaveExecuted { get; set; }
        internal ControlRequestScope(ControlSessionOptions grant) { _grant = grant; }
        internal void RequireActive(ControlSessionOptions? current, bool enabled)
        {
            if (!enabled || !ReferenceEquals(_grant, current))
                throw Failure(new ControlException("GrantRevoked", "电脑控制授权已更新；旧请求已取消，请重新观察。"));
        }
        internal ControlException Failure(ControlException error) => InputMayHaveExecuted && error.ExecutionState == "NotStarted"
            ? new ControlException(error.Code, error.Message, "Unknown") : error;
    }
}
