#nullable enable
using System;

namespace YTray
{
    internal static class FrameworkCompatibility
    {
        internal static bool Contains(this string text, string value, StringComparison comparison) => text.IndexOf(value, comparison) >= 0;
        internal static string Replace(this string text, string oldValue, string newValue, StringComparison comparison)
        {
            if (comparison != StringComparison.Ordinal) throw new ArgumentOutOfRangeException(nameof(comparison));
            return text.Replace(oldValue, newValue);
        }
    }
}
