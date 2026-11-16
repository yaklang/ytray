using System.Drawing;
using YTray.Models;

namespace YTray.Native
{
    internal static class WindowsInputGeometry
    {
        // UIA bounds can extend beyond the window for clipped or scrolled controls.
        // The whole element's center can hit a different application; target only
        // the visible intersection, preserving negative multi-monitor coordinates.
        internal static Point VisibleCenter(Rectangle element, Rectangle window)
        {
            var visible = Rectangle.Intersect(element, window);
            if (visible.Width <= 0 || visible.Height <= 0)
                throw new ControlException("ElementNotVisible", "控件当前不可见，请滚动或展开后继续");
            return new Point(visible.Left + visible.Width / 2, visible.Top + visible.Height / 2);
        }
    }
}
