#nullable enable
using System;
using System.Windows;
using Newtonsoft.Json.Linq;
using YTray.Models;

namespace YTray.Core.SystemControl
{
    // The same window-image pixel frame used by clicks, calculated from this
    // read's dimensions. A text-only region never needs a prior capture/session.
    internal sealed class ControlRegion
    {
        internal Rect ScreenBounds { get; }
        internal JObject ImageBounds { get; }
        internal int ImageWidth { get; }
        internal int ImageHeight { get; }
        private ControlRegion(Rect screen, JObject image, int width, int height)
        { ScreenBounds = screen; ImageBounds = image; ImageWidth = width; ImageHeight = height; }

        internal static ControlRegion? Create(JObject source, JObject? options)
        {
            if (!(options?["region"] is JObject requested)) return null;
            if (!(source["window_bounds"] is JObject bounds)) throw new ControlException("RegionUnavailable", "当前观察没有窗口边界，无法读取指定区域");
            var width = (double?)bounds["width"] ?? 0; var height = (double?)bounds["height"] ?? 0;
            if (width <= 0 || height <= 0) throw new ControlException("RegionUnavailable", "窗口当前没有可读取区域");
            var cap = (int?)options?["max_image_dimension"] ?? 1400;
            var scale = Math.Min(1, cap / Math.Max(width, height));
            var imageWidth = (int?)source["screenshot_width"] ?? 0; var imageHeight = (int?)source["screenshot_height"] ?? 0;
            if (imageWidth <= 0) imageWidth = Math.Max(1, (int)Math.Round(width * scale));
            if (imageHeight <= 0) imageHeight = Math.Max(1, (int)Math.Round(height * scale));
            var x = requested.Value<double>("x"); var y = requested.Value<double>("y");
            var w = requested.Value<double>("width"); var h = requested.Value<double>("height");
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h)
                || x < 0 || y < 0 || w <= 0 || h <= 0 || x >= imageWidth || y >= imageHeight)
                throw new ControlException("InvalidRegion", "region 必须与当前窗口图片相交，width / height 必须大于 0");
            w = Math.Min(w, imageWidth - x); h = Math.Min(h, imageHeight - y);
            var screen = new Rect(((double?)bounds["x"] ?? 0) + x * width / imageWidth,
                ((double?)bounds["y"] ?? 0) + y * height / imageHeight, w * width / imageWidth, h * height / imageHeight);
            return new ControlRegion(screen, new JObject { ["x"] = x, ["y"] = y, ["width"] = w, ["height"] = h }, imageWidth, imageHeight);
        }
        internal bool Includes(JObject element) => TryFrame(element["frame"] as JObject, out var frame) && ScreenBounds.IntersectsWith(frame);
        internal static bool TryFrame(JObject? value, out Rect frame)
        {
            frame = Rect.Empty;
            if (value == null) return false;
            var x = (double?)value["x"] ?? 0; var y = (double?)value["y"] ?? 0;
            var w = (double?)value["w"] ?? 0; var h = (double?)value["h"] ?? 0;
            if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(w) || !double.IsFinite(h) || w <= 0 || h <= 0) return false;
            frame = new Rect(x, y, w, h); return true;
        }
    }
}
