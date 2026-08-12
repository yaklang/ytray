using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using YTray.Models;

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
        private static readonly Dictionary<Guid, List<IntPtr>> LiveIconHandles = new Dictionary<Guid, List<IntPtr>>();

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

            var big = Native.Win32.LoadImage(IntPtr.Zero, icoPath, Native.Win32.IMAGE_ICON, 32, 32,
                Native.Win32.LR_LOADFROMFILE);
            var small = Native.Win32.LoadImage(IntPtr.Zero, icoPath, Native.Win32.IMAGE_ICON, 16, 16,
                Native.Win32.LR_LOADFROMFILE);
            if (big == IntPtr.Zero && small == IntPtr.Zero) return false;

            ReleaseWindowIcons(instanceId);
            var handles = new List<IntPtr>();
            if (big != IntPtr.Zero)
            {
                Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON, (IntPtr)Native.Win32.ICON_BIG, big);
                handles.Add(big);
            }
            if (small != IntPtr.Zero)
            {
                Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON, (IntPtr)Native.Win32.ICON_SMALL, small);
                Native.Win32.SendMessage(hwnd, Native.Win32.WM_SETICON, (IntPtr)Native.Win32.ICON_SMALL2, small);
                handles.Add(small);
            }
            lock (IconHandleLock) LiveIconHandles[instanceId] = handles;

            bool changed;
            var metadataApplied = Native.Win32.EnsureWindowAppProperties(
                hwnd, aumid, icoPath + ",0", false, out changed);
            return metadataApplied;
        }

        private static void ReleaseWindowIcons(Guid instanceId)
        {
            List<IntPtr> handles = null;
            lock (IconHandleLock)
            {
                if (LiveIconHandles.TryGetValue(instanceId, out handles))
                    LiveIconHandles.Remove(instanceId);
            }
            if (handles == null) return;
            foreach (var handle in handles)
                if (handle != IntPtr.Zero) Native.Win32.DestroyIcon(handle);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>Composite the base icon with an orange circular badge + white letter.</summary>
        public static Bitmap RenderIcon(Icon baseIcon, string badge, int size = 256)
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

                // Taskbar icons are commonly rendered at 24–32px. Give the identity badge nearly
                // half the icon and render every ICO frame independently so A/B remains legible.
                float badgeHeight = Math.Max(8, size * 0.46f);
                float badgeWidth = badge.Length == 1 ? badgeHeight : Math.Min(size * 0.62f, badgeHeight * 1.38f);
                float inset = Math.Max(1, size * 0.018f);
                float badgeRectX = size - badgeWidth - inset;
                float badgeRectY = size - badgeHeight - inset;
                var badgeRect = new RectangleF(badgeRectX, badgeRectY, badgeWidth, badgeHeight);

                // Dark keyline + white separator keep the orange badge readable on every browser.
                using (var shadowBrush = new SolidBrush(Color.FromArgb(105, 0, 0, 0)))
                    g.FillEllipse(shadowBrush, new RectangleF(badgeRect.X,
                        badgeRect.Y + Math.Max(1, size * 0.018f), badgeRect.Width, badgeRect.Height));
                using (var borderBrush = new SolidBrush(Color.White))
                    g.FillEllipse(borderBrush, badgeRect);
                var ring = Math.Max(1.2f, size * 0.035f);
                var fillRect = RectangleF.Inflate(badgeRect, -ring, -ring);
                using (var fillBrush = new SolidBrush(BrandOrange))
                    g.FillEllipse(fillBrush, fillRect);

                // White bold letter, biased slightly upward to account for Segoe's baseline.
                float fontSize = badge.Length == 1 ? badgeHeight * 0.62f : badgeHeight * 0.46f;
                using (var font = new Font(SystemFonts.DefaultFont.FontFamily, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var textRect = new RectangleF(badgeRectX, badgeRectY - size * 0.008f, badgeWidth, badgeHeight);
                    g.DrawString(badge, font, brush, textRect, sf);
                }
            }
            return bmp;
        }

        /// <summary>Extract the largest available icon from an executable.</summary>
        public static Icon ExtractLargeIcon(string path)
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
            public byte[] Bytes { get; set; }
            public void Dispose() { Bytes = null; }
        }

        /// <summary>Save native 16–256px PNG-compressed frames in one ICO.</summary>
        private static void SaveAsIcon(Icon baseIcon, string badge, string path)
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
