using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
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
                // Write ICO (multi-size)
                SaveAsIcon(rendered, icoPath);
                return icoPath;
            }
        }

        public static void Remove(Guid instanceId, string applicationDirectory)
        {
            TryDelete(IconPath(instanceId, applicationDirectory));
            TryDelete(PngPath(instanceId, applicationDirectory));
            TryDelete(LnkPath(instanceId, applicationDirectory));
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

                // Badge circle at bottom-right.
                float diameter = size * 0.31f;
                float badgeRectX = size - diameter - size * 0.025f;
                float badgeRectY = size * 0.025f;
                var badgeRect = new RectangleF(badgeRectX, badgeRectY, diameter, diameter);

                // White border ring.
                float border = size * 0.012f;
                using (var borderPen = new Pen(Color.White, size * 0.024f))
                    g.DrawEllipse(borderPen, RectangleF.Inflate(badgeRect, -border, -border));

                // Orange fill.
                using (var fillBrush = new SolidBrush(BrandOrange))
                    g.FillEllipse(fillBrush, badgeRect);

                // White bold letter.
                float fontSize = badge.Length == 1 ? diameter * 0.59f : diameter * 0.43f;
                using (var font = new Font(SystemFonts.DefaultFont.FontFamily, fontSize, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.White))
                {
                    var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                    var textRect = new RectangleF(badgeRectX, badgeRectY + size * 0.008f, diameter, diameter);
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

        /// <summary>Save a Bitmap as a multi-size .ico file.</summary>
        private static void SaveAsIcon(Bitmap source, string path)
        {
            // Write a simple single-image ICO (256x256) using the raw ICO header + PNG-encoded image data.
            // This is the modern "PNG-compressed ICO" format supported on Windows Vista+.
            using (var ms = new MemoryStream())
            {
                source.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                var pngBytes = ms.ToArray();

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    // ICONDIR
                    bw.Write((ushort)0);      // reserved
                    bw.Write((ushort)1);      // type = ICO
                    bw.Write((ushort)1);      // count
                    // ICONDIRENTRY
                    bw.Write((byte)0);         // width 256 -> 0
                    bw.Write((byte)0);        // height 256 -> 0
                    bw.Write((byte)0);        // palette
                    bw.Write((byte)0);        // reserved
                    bw.Write((ushort)1);      // color planes
                    bw.Write((ushort)32);     // bpp
                    bw.Write((uint)pngBytes.Length);
                    bw.Write((uint)(6 + 16)); // offset = header + 1 entry
                    bw.Write(pngBytes);
                }
            }
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