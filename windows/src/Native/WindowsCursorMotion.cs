#nullable enable
using System;
using System.Drawing;

namespace YTray.Native
{
    // Screen-pixel geometry; timing is based on logical distance so mixed-DPI
    // displays keep the same readable pace. Convex control bounds prevent arcs
    // from wandering off the virtual desktop, including negative monitor origins.
    internal sealed class WindowsCursorMotion
    {
        private readonly PointF _from, _first, _second, _to;
        internal int DurationMilliseconds { get; }
        internal WindowsCursorMotion(PointF from, PointF to, Rectangle desktop, float scale)
        {
            _from = from; _to = to;
            var dx = to.X - from.X; var dy = to.Y - from.Y;
            var distance = Math.Sqrt(dx * dx + dy * dy);
            var logical = distance / Math.Max(1, scale);
            DurationMilliseconds = logical <= 8 ? 0 : logical < 24 ? 260 : (int)Math.Min(1050, 340 + 18 * Math.Sqrt(logical));
            if (distance < 1) { _first = from; _second = to; return; }
            var normal = new PointF((float)(-dy / distance), (float)(dx / distance));
            var midpoint = new PointF((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            var center = new PointF(desktop.Left + desktop.Width / 2f, desktop.Top + desktop.Height / 2f);
            var sign = (center.X - midpoint.X) * normal.X + (center.Y - midpoint.Y) * normal.Y >= 0 ? 1 : -1;
            var bend = (float)Math.Min(64 * Math.Max(1, scale), distance * .12) * sign;
            _first = Clamp(new PointF(from.X + dx * .28f + normal.X * bend, from.Y + dy * .28f + normal.Y * bend), desktop);
            _second = Clamp(new PointF(from.X + dx * .72f + normal.X * bend, from.Y + dy * .72f + normal.Y * bend), desktop);
        }
        internal PointF Sample(double progress)
        {
            var t = Math.Max(0, Math.Min(1, progress));
            // Quintic easing has zero velocity and acceleration at both ends.
            t = t * t * t * (10 + t * (-15 + 6 * t));
            var inverse = 1 - t;
            return new PointF((float)(inverse * inverse * inverse * _from.X + 3 * inverse * inverse * t * _first.X + 3 * inverse * t * t * _second.X + t * t * t * _to.X),
                (float)(inverse * inverse * inverse * _from.Y + 3 * inverse * inverse * t * _first.Y + 3 * inverse * t * t * _second.Y + t * t * t * _to.Y));
        }
        private static PointF Clamp(PointF point, Rectangle bounds) => new PointF(Math.Max(bounds.Left, Math.Min(bounds.Right - 1, point.X)), Math.Max(bounds.Top, Math.Min(bounds.Bottom - 1, point.Y)));
    }
}
