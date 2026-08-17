#nullable enable
using System;
using System.Linq;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Generates and validates the A/B/C... ZZ dock/taskbar badge labels.
    /// Mirrors macOS DockBadgeLabel exactly.
    /// </summary>
    public static class DockBadgeLabel
    {
        public static string DefaultLabel(int ordinal)
        {
            var value = Math.Max(1, ordinal);
            var result = "";
            do
            {
                value -= 1;
                result = (char)('A' + value % 26) + result;
                value /= 26;
            } while (value > 0);
            return result;
        }

        public static string Normalize(string value)
        {
            if (value == null) throw new YTrayException(YTrayError.LaunchFailed, "Dock 角标不能为空");
            var normalized = value.Trim().ToUpperInvariant();
            if (normalized.Length < 1 || normalized.Length > 2
                || !normalized.All(c => c >= 'A' && c <= 'Z'))
                throw new YTrayException(YTrayError.LaunchFailed, "Dock 角标只能是 1–2 个英文字母");
            return normalized;
        }
    }
}
