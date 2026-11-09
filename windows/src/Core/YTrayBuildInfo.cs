#nullable enable
using System.Linq;
using System.Reflection;

namespace YTray.Core
{
    internal static class YTrayBuildInfo
    {
        internal static string BuildId => typeof(YTrayBuildInfo).Assembly.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12);
        internal static string Version
        {
            get
            {
                var informational = Assembly.GetExecutingAssembly()
                    .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
                    .OfType<AssemblyInformationalVersionAttribute>()
                    .FirstOrDefault()?.InformationalVersion;
                if (!string.IsNullOrWhiteSpace(informational)) return informational!;
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
            }
        }
    }
}
