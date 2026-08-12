#nullable enable
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using YTray.Models;
using YTray.Native;

namespace YTray.Core
{
    /// <summary>
    /// Composites the Chrome process icon with an A/B/C badge (mirrors macOS BrowserProcessIcon),
    /// writes a per-instance .ico, and creates a .lnk carrying the instance AUMID + icon so the
    /// taskbar shows a distinct, badged button per instance.
    /// </summary>
    public static class BrowserProcessIcon
    {
        public static readonly Color BrandOrange = Color.FromArgb(0xF2, 0x8B, 0x44);
        private static readonly object IconHandleLock = new object();
        private static readonly Dictionary<Guid, List<SafeIconHandle>> LiveIconHandles =
            new Dictionary<Guid, List<SafeIconHandle>>();

        public static string IconPath(Guid instanceId, string applicationDirectory) =>
            Path.Combine(applicationDirectory, "ProcessIcons", instanceId + ".ico");

        public static string PngPath(Guid instanceId, string applicationDirectory) =>
            Path.Combine(applicationDirectory, "ProcessIcons", instanceId + ".png");

        public static string LnkPath(Guid instanceId, string applicationDirectory) =>
            Path.Combine(applicationDirectory, "ProcessIcons", instanceId + ".lnk");

        /// <summary>
        /// Extract the icon of the browser executable and composite the badge onto it,
        /// writing both .ico and .png for flexibility. Returns the .ico path.
        /// </summary>
        public static string Write(string sourceExecutable, string badge, Guid instanceId, string applicationDirectory)
        {
            var label = DockBadgeLabel.Normalize(badge);
            var dir = Path.Combine(applicationDirectory, "ProcessIcons");
            Directory.CreateDirectory(dir);

            using (var baseIcon = ExtractLargeIcon(sourceExecutable))
            using (var rendered = RenderIcon(baseIcon, label, 256))
            {
                var icoPath = IconPath(instanceId, applicationDirectory);
                var pngPath = PngPath(instanceId, applicationDirectory);
                // Write PNG
                rendered.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                // Write native-size frames instead of asking Explorer to shrink one 256px image.
                SaveAsIcon(baseIcon, label, icoPath);
                return icoPath;
            }
        }

        public static void Remove(Guid instanceId, string applicationDirectory)
        {
            ReleaseWindowIcons(instanceId);
            TryDelete(IconPath(instanceId, applicationDirectory));
            TryDelete(PngPath(instanceId, applicationDirectory));
            TryDelete(LnkPath(instanceId, applicationDirectory));
        }

        /// <summary>
        /// Apply the generated icon and matching AUMID metadata to Chrome's live top-level window.
        /// A shortcut alone does not change an already-created taskbar button; WM_SETICON does.
        /// </summary>
        public static bool ApplyToProcessWindow(Guid instanceId, int processId, string applicationDirectory,
            string aumid, string displayName)
        {
            var hwnd = Native.WindowEnum.FindFirstVisibleWindow(processId);
            if (hwnd == IntPtr.Zero) hwnd = Native.WindowEnum.FindAnyVisibleWindow(processId);
            if (hwnd == IntPtr.Zero) return false;
            var icoPath = IconPath(instanceId, applicationDirectory);
            if (!File.Exists(icoPath)) return false;

            SafeIconHandle? bigOwner = null;
            SafeIconHandle? smallOwner = null;
            try
            {
                bigOwner = SafeIconHandle.Own(Native.Win32.LoadImage(IntPtr.Zero, icoPath,
                    Native.Win32.IMAGE_ICON, 32, 32, Native.Win32.LR_LOADFROMFILE));
                smallOwner = SafeIconHandle.Own(Native.Win32.LoadImage(IntPtr.Zero, icoPath,
                    Native.Win32.IMAGE_ICON, 16, 16, Native.Win32.LR_LOADFROMFILE));
                if (bigOwner.IsInvalid && smallOwner.IsInvalid) return false;

                var handles = new List<SafeIconHandle>();
                List<SafeIconHandle>? previousHandles;
                lock (IconHandleLock)
                {
                    // Install the replacement before releasing the previous HICONs. Destroying the
                    // old owners first leaves a short dangling native pointer in the HWND and can
                    // race an Explorer repaint.
                    if (!bigOwner.IsInvalid)
                    {
                        Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON,
                            (IntPtr)Native.Win32.ICON_BIG, bigOwner.DangerousGetHandle());
                        handles.Add(bigOwner);
                        bigOwner = null;
                    }
                    if (!smallOwner.IsInvalid)
                    {
                        Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON,
                            (IntPtr)Native.Win32.ICON_SMALL, smallOwner.DangerousGetHandle());
                        Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON,
                            (IntPtr)Native.Win32.ICON_SMALL2, smallOwner.DangerousGetHandle());
                        handles.Add(smallOwner);
                        smallOwner = null;
                    }
                    LiveIconHandles.TryGetValue(instanceId, out previousHandles);
                    LiveIconHandles[instanceId] = handles;
                }
                if (previousHandles != null)
                    foreach (var previousHandle in previousHandles)
                        previousHandle.Dispose();

                bool changed;
                return Native.Win32.EnsureWindowAppProperties(
                    hwnd, aumid, icoPath + ",0", false, out changed);
            }
            finally
            {
                bigOwner?.Dispose();
                smallOwner?.Dispose();
            }
        }

        private static void ReleaseWindowIcons(Guid instanceId)
        {
            List<SafeIconHandle>? handles = null;
            lock (IconHandleLock)
            {
                if (LiveIconHandles.TryGetValue(instanceId, out handles))
                    LiveIconHandles.Remove(instanceId);
            }
            if (handles == null) return;
            foreach (var handle in handles)
                handle.Dispose();
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Composite the base icon with a high-contrast orange instance marker.</summary>
        public static Bitmap RenderIcon(Icon? baseIcon, string badge, int size = 256)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Transparent);

                // Draw the base browser icon scaled to fill.
                if (baseIcon != null)
                {
                    try { g.DrawIcon(baseIcon, new Rectangle(0, 0, size, size)); }
                    catch
                    {
                        using (var b = baseIcon.ToBitmap())
                            g.DrawImage(b, new Rectangle(0, 0, size, size));
                    }
                }

                // Chrome for Testing already owns the bottom-right corner on Windows. Put YTray's
                // instance identity in the opposite (top-left) corner and make it deliberately
                // larger than the previous badge so A/B survives 16–32px taskbar rendering.
                float badgeHeight = Math.Max(9, size * 0.54f);
                float badgeWidth = badge.Length == 1 ? badgeHeight : Math.Min(size * 0.70f, badgeHeight * 1.38f);
                float inset = Math.Max(0.7f, size * 0.012f);
                float badgeRectX = inset;
                float badgeRectY = inset;
                var badgeRect = new RectangleF(badgeRectX, badgeRectY, badgeWidth, badgeHeight);

                // Use a compact rounded square rather than another circular browser mark. Swift
                // uses orange + white, but white on orange loses contrast at Windows' 16–32px
                // taskbar sizes; the dark letter keeps the instance identity unmistakable.
                using (var shadowBrush = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
                using (var shadowPath = RoundedRectangle(new RectangleF(
                        badgeRect.X + Math.Max(0.6f, size * 0.012f),
                        badgeRect.Y + Math.Max(0.8f, size * 0.018f),
                        badgeRect.Width, badgeRect.Height), badgeHeight * 0.30f))
                    g.FillPath(shadowBrush, shadowPath);
                using (var borderBrush = new SolidBrush(Color.White))
                using (var borderPath = RoundedRectangle(badgeRect, badgeHeight * 0.30f))
                    g.FillPath(borderBrush, borderPath);
                var ring = Math.Max(1.2f, size * 0.035f);
                var fillRect = RectangleF.Inflate(badgeRect, -ring, -ring);
                using (var fillBrush = new SolidBrush(BrandOrange))
                using (var fillPath = RoundedRectangle(fillRect, fillRect.Height * 0.25f))
                    g.FillPath(fillBrush, fillPath);

                // Near-black on orange has more than twice the contrast of white on orange.
                float fontSize = badge.Length == 1 ? badgeHeight * 0.68f : badgeHeight * 0.50f;
                using (var font = new Font("Arial", fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(0x20, 0x21, 0x24)))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var textRect = new RectangleF(badgeRectX, badgeRectY - size * 0.018f, badgeWidth, badgeHeight);
                    g.DrawString(badge, font, brush, textRect, sf);
                }
            }
            return bmp;
        }

        private static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
        {
            var path = new GraphicsPath();
            var diameter = Math.Max(1, Math.Min(Math.Min(rectangle.Width, rectangle.Height), radius * 2));
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        /// <summary>Extract the largest available icon from an executable.</summary>
        public static Icon? ExtractLargeIcon(string path)
        {
            try
            {
                // Use Icon.ExtractAssociatedIcon as the reliable baseline.
                return Icon.ExtractAssociatedIcon(path);
            }
            catch
            {
                return null;
            }
        }

        private sealed class IconFrame : IDisposable
        {
            public int Size { get; set; }
            public byte[] Bytes { get; set; } = Array.Empty<byte>();
            public void Dispose() { Bytes = Array.Empty<byte>(); }
        }

        /// <summary>Save native 16–256px PNG-compressed frames in one ICO.</summary>
        private static void SaveAsIcon(Icon? baseIcon, string badge, string path)
        {
            var frames = new List<IconFrame>();
            foreach (var size in new[] { 16, 20, 24, 32, 40, 48, 64, 256 })
            {
                using (var rendered = RenderIcon(baseIcon, badge, size))
                using (var stream = new MemoryStream())
                {
                    rendered.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    frames.Add(new IconFrame { Size = size, Bytes = stream.ToArray() });
                }
            }

            try
            {
                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var writer = new BinaryWriter(fs))
                {
                    writer.Write((ushort)0);
                    writer.Write((ushort)1);
                    writer.Write((ushort)frames.Count);
                    uint offset = (uint)(6 + frames.Count * 16);
                    foreach (var frame in frames)
                    {
                        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                        writer.Write((byte)(frame.Size >= 256 ? 0 : frame.Size));
                        writer.Write((byte)0);
                        writer.Write((byte)0);
                        writer.Write((ushort)1);
                        writer.Write((ushort)32);
                        writer.Write((uint)frame.Bytes.Length);
                        writer.Write(offset);
                        offset += (uint)frame.Bytes.Length;
                    }
                    foreach (var frame in frames) writer.Write(frame.Bytes);
                }
            }
            finally { foreach (var frame in frames) frame.Dispose(); }
        }

        /// <summary>
        /// Create a .lnk for the instance: Chrome target + args + AUMID + composited icon,
        /// so the taskbar groups this instance under its own button with the badged icon.
        /// </summary>
        public static string WriteInstanceShortcut(string browserExecutable, string arguments,
            string workingDir, Guid instanceId, string aumid, string displayName, string applicationDirectory)
        {
            var icoPath = IconPath(instanceId, applicationDirectory);
            var lnkPath = LnkPath(instanceId, applicationDirectory);
            try
            {
                Native.ShellLink.WriteLnk(lnkPath, browserExecutable, arguments, workingDir, icoPath, 0, aumid, displayName);
            }
            catch
            {
                // If COM shortcut creation fails, the AUMID still separates the instance on the taskbar;
                // the .lnk is only needed for pinning + custom icon. Best-effort.
            }
            return lnkPath;
        }
    }
}
