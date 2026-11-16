#nullable enable
using System;

namespace YTray.Models
{
    public enum BrowserEnvironmentNetwork { LaunchChoice, Direct, SavedProxy }

    public sealed class BrowserEnvironment
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public Guid? RuntimeId { get; set; }
        public string Language { get; set; } = "";
        public string TimeZone { get; set; } = "";
        public int? WindowWidth { get; set; }
        public int? WindowHeight { get; set; }
        public int? ViewportWidth { get; set; }
        public int? ViewportHeight { get; set; }
        public BrowserEnvironmentNetwork Network { get; set; }
        public Guid? ProxyPresetId { get; set; }

        internal bool RequiresPageEmulation => !string.IsNullOrWhiteSpace(TimeZone) || ViewportWidth != null;

        public BrowserEnvironment Clone()
        {
            var copy = (BrowserEnvironment)MemberwiseClone();
            copy.Name = Name ?? "";
            copy.Language = Language ?? "";
            copy.TimeZone = TimeZone ?? "";
            return copy;
        }
    }
}
