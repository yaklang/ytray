#nullable enable
using System;

namespace YTray.Models
{
    public enum BrowserEnvironmentNetwork { LaunchChoice, Direct, SavedProxy }
    public enum BrowserEnvironmentColorScheme { Default, Light, Dark }
    public enum BrowserEnvironmentMotion { Default, Normal, Reduced }
    public enum BrowserEnvironmentWebRtc { Default, PublicInterfaceOnly, DisableNonProxiedUdp }
    public enum BrowserEnvironmentCookies { Default, BlockThirdParty }
    public enum BrowserEnvironmentPermission { Default, Ask, Block }

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
        public bool RandomizeOnLaunch { get; set; }
        public double? PixelRatio { get; set; }
        public BrowserEnvironmentColorScheme ColorScheme { get; set; }
        public BrowserEnvironmentMotion Motion { get; set; }
        public BrowserEnvironmentWebRtc WebRtc { get; set; }
        public BrowserEnvironmentCookies Cookies { get; set; }
        public BrowserEnvironmentPermission Location { get; set; }
        public BrowserEnvironmentPermission Camera { get; set; }
        public BrowserEnvironmentPermission Microphone { get; set; }

        internal bool RequiresPageEmulation => !string.IsNullOrWhiteSpace(TimeZone) || ViewportWidth != null || PixelRatio != null
            || ColorScheme != BrowserEnvironmentColorScheme.Default || Motion != BrowserEnvironmentMotion.Default;

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
