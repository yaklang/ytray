#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using YTray.Models;

namespace YTray.Core
{
    /// <summary>
    /// Applies the shared YTray palette to every existing WPF surface. The brushes in App.xaml
    /// reference these colours dynamically, so switching theme does not require recreating windows.
    /// </summary>
    public static class ThemeManager
    {
        private static bool _initialized;
        private static AppThemePreference _preference = AppThemePreference.System;

        public static AppThemePreference Preference => _preference;
        public static bool IsDark { get; private set; }
        public static event EventHandler? ThemeChanged;

        internal static void TrackNativeTitleBar(Window window)
        {
            void Apply(object? sender, EventArgs e)
            {
                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;
                var dark = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                    DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
            }
            window.SourceInitialized += Apply;
            ThemeChanged += Apply;
            window.Closed += (_, __) => ThemeChanged -= Apply;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

        public static void Initialize(AppThemePreference preference)
        {
            _preference = preference;
            if (!_initialized)
            {
                SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
                _initialized = true;
            }
            ApplyResolvedTheme();
        }

        public static void SetPreference(AppThemePreference preference)
        {
            if (!Enum.IsDefined(typeof(AppThemePreference), preference))
                preference = AppThemePreference.System;
            _preference = preference;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.Invoke(new Action(ApplyResolvedTheme));
            else
                ApplyResolvedTheme();
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
            _initialized = false;
        }

        private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (_preference != AppThemePreference.System) return;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.BeginInvoke(new Action(ApplyResolvedTheme));
        }

        private static void ApplyResolvedTheme()
        {
            var dark = _preference == AppThemePreference.Dark
                || (_preference == AppThemePreference.System && SystemUsesDarkTheme());
            IsDark = dark;
            ApplyPalette(dark ? DarkPalette : LightPalette);
            var application = Application.Current;
            if (application != null)
                foreach (Window window in application.Windows)
                {
                    ApplyLocalPalette(window.Resources, dark ? DarkPalette : LightPalette);
                    window.InvalidateVisual();
                }
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }

        private static bool SystemUsesDarkTheme()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    var value = key?.GetValue("AppsUseLightTheme");
                    if (value is int number) return number == 0;
                }
            }
            catch { }
            return false;
        }

        private static void ApplyPalette(IReadOnlyDictionary<string, string> palette)
        {
            var resources = Application.Current?.Resources;
            if (resources == null) return;
            ApplyPalette(resources, palette);
        }

        internal static void ApplyPalette(ResourceDictionary resources, bool dark) =>
            ApplyPalette(resources, dark ? DarkPalette : LightPalette);

        internal static void ApplyLocalPalette(ResourceDictionary resources, bool dark) =>
            ApplyLocalPalette(resources, dark ? DarkPalette : LightPalette);

        private static void ApplyPalette(ResourceDictionary resources, IReadOnlyDictionary<string, string> palette)
        {
            foreach (var pair in palette)
            {
                var color = (Color)ColorConverter.ConvertFromString(pair.Value);
                resources[pair.Key] = color;

                // Updating only the Color resource leaves room for already-materialized WPF
                // brushes (especially brushes held by styles and popups) to retain the old
                // colour. Mutate the shared brush instance as well so every open window redraws
                // immediately. If a third-party dictionary froze it, replace the resource; all
                // DynamicResource consumers will then resolve the replacement.
                if (!PaletteBrushKeys.TryGetValue(pair.Key, out var brushKey)) continue;
                if (resources[brushKey] is SolidColorBrush brush && !brush.IsFrozen)
                    brush.Color = color;
                else
                    resources[brushKey] = new SolidColorBrush(color);
            }
        }

        private static void ApplyLocalPalette(ResourceDictionary resources, IReadOnlyDictionary<string, string> palette)
        {
            foreach (var pair in FloatingBrushKeys)
            {
                if (!(resources[pair.Value] is SolidColorBrush brush)) continue;
                var color = (Color)ColorConverter.ConvertFromString(palette[pair.Key]);
                if (brush.IsFrozen) resources[pair.Value] = new SolidColorBrush(color);
                else brush.Color = color;
            }

            if (!(resources["WidgetSurfaceBrush"] is LinearGradientBrush surface)
                || surface.GradientStops.Count < 2) return;
            if (surface.IsFrozen)
            {
                surface = surface.Clone();
                resources["WidgetSurfaceBrush"] = surface;
            }
            surface.GradientStops[0].Color = (Color)ColorConverter.ConvertFromString(palette["FloatingTopColor"]);
            surface.GradientStops[1].Color = (Color)ColorConverter.ConvertFromString(palette["FloatingBottomColor"]);
        }

        private static readonly IReadOnlyDictionary<string, string> PaletteBrushKeys =
            new Dictionary<string, string>
            {
                ["BrandPaleColor"] = "BrandPaleBrush",
                ["AppBackgroundColor"] = "AppBackgroundBrush",
                ["TitleBarColor"] = "TitleBarBrush",
                ["SidebarColor"] = "SidebarBrush",
                ["SurfaceColor"] = "SurfaceBrush",
                ["SurfaceRaisedColor"] = "SurfaceRaisedBrush",
                ["SurfaceMutedColor"] = "SurfaceMutedBrush",
                ["InputColor"] = "InputBrush",
                ["TextPrimaryColor"] = "TextPrimaryBrush",
                ["TextSecondaryColor"] = "TextSecondaryBrush",
                ["TextTertiaryColor"] = "TextTertiaryBrush",
                ["HairlineColor"] = "HairlineBrush",
                ["WindowBorderColor"] = "WindowBorderBrush",
                ["HoverColor"] = "HoverBrush",
                ["PressedColor"] = "PressedBrush",
                ["SuccessPaleColor"] = "SuccessPaleBrush",
                ["DangerPaleColor"] = "DangerPaleBrush",
                ["UpdatePaleColor"] = "UpdatePaleBrush",
                ["UpdateTextColor"] = "UpdateTextBrush",
            };

        private static readonly IReadOnlyDictionary<string, string> FloatingBrushKeys =
            new Dictionary<string, string>
            {
                ["FloatingRaisedColor"] = "WidgetRaisedBrush",
                ["FloatingDeepColor"] = "WidgetDeepBrush",
                ["FloatingInputColor"] = "WidgetInputBrush",
                ["FloatingHoverColor"] = "WidgetHoverBrush",
                ["FloatingBorderColor"] = "WidgetBorderBrush",
                ["FloatingHairlineColor"] = "WidgetHairlineBrush",
                ["FloatingTextPrimaryColor"] = "WidgetTextPrimaryBrush",
                ["FloatingTextSecondaryColor"] = "WidgetTextSecondaryBrush",
                ["FloatingTextTertiaryColor"] = "WidgetTextTertiaryBrush",
                ["FloatingOrangePaleColor"] = "WidgetOrangePaleBrush",
            };

        private static readonly IReadOnlyDictionary<string, string> LightPalette =
            new Dictionary<string, string>
            {
                ["BrandPaleColor"] = "#FFF1E6",
                ["AppBackgroundColor"] = "#F5F6F8",
                ["TitleBarColor"] = "#FAFBFC",
                ["SidebarColor"] = "#F8F9FA",
                ["SurfaceColor"] = "#FFFFFFFF",
                ["SurfaceRaisedColor"] = "#FFFFFFFF",
                ["SurfaceMutedColor"] = "#F0F2F5",
                ["InputColor"] = "#FFFFFFFF",
                ["TextPrimaryColor"] = "#1A1D21",
                ["TextSecondaryColor"] = "#5B616B",
                ["TextTertiaryColor"] = "#8C939E",
                ["HairlineColor"] = "#E3E6EB",
                ["WindowBorderColor"] = "#D0D5DD",
                ["HoverColor"] = "#ECEEF2",
                ["PressedColor"] = "#E2E5EB",
                ["SuccessPaleColor"] = "#E7F6EE",
                ["DangerPaleColor"] = "#FDECEB",
                ["UpdatePaleColor"] = "#FFF3C4",
                ["UpdateTextColor"] = "#9A5A00",
                ["FloatingTopColor"] = "#FFFBFCFD",
                ["FloatingBottomColor"] = "#FFF5F6F8",
                ["FloatingRaisedColor"] = "#FFFFFFFF",
                ["FloatingDeepColor"] = "#FFF0F2F5",
                ["FloatingInputColor"] = "#FFFFFFFF",
                ["FloatingHoverColor"] = "#FFECEEF2",
                ["FloatingBorderColor"] = "#FFD4D8E0",
                ["FloatingHairlineColor"] = "#FFE4E7EC",
                ["FloatingTextPrimaryColor"] = "#FF1A1D21",
                ["FloatingTextSecondaryColor"] = "#FF5B616B",
                ["FloatingTextTertiaryColor"] = "#FF8C939E",
                ["FloatingOrangePaleColor"] = "#FFFFECDE",
            };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette =
            new Dictionary<string, string>
            {
                ["BrandPaleColor"] = "#3B2618",
                ["AppBackgroundColor"] = "#121417",
                ["TitleBarColor"] = "#15181C",
                ["SidebarColor"] = "#16191E",
                ["SurfaceColor"] = "#1A1E24",
                ["SurfaceRaisedColor"] = "#20252C",
                ["SurfaceMutedColor"] = "#15181D",
                ["InputColor"] = "#161A20",
                ["TextPrimaryColor"] = "#F1F3F5",
                ["TextSecondaryColor"] = "#9EA5B0",
                ["TextTertiaryColor"] = "#6C737F",
                ["HairlineColor"] = "#2A3038",
                ["WindowBorderColor"] = "#363D47",
                ["HoverColor"] = "#232931",
                ["PressedColor"] = "#2B323C",
                ["SuccessPaleColor"] = "#182D22",
                ["DangerPaleColor"] = "#341F22",
                ["UpdatePaleColor"] = "#463712",
                ["UpdateTextColor"] = "#FFD166",
                ["FloatingTopColor"] = "#FF1D2127",
                ["FloatingBottomColor"] = "#FF171A1F",
                ["FloatingRaisedColor"] = "#FF242931",
                ["FloatingDeepColor"] = "#FF1E2229",
                ["FloatingInputColor"] = "#FF15181D",
                ["FloatingHoverColor"] = "#FF2A303A",
                ["FloatingBorderColor"] = "#FF343B46",
                ["FloatingHairlineColor"] = "#FF282E37",
                ["FloatingTextPrimaryColor"] = "#FFE5E6E8",
                ["FloatingTextSecondaryColor"] = "#FF9EA5B0",
                ["FloatingTextTertiaryColor"] = "#FF6C737F",
                ["FloatingOrangePaleColor"] = "#FF3B271A",
            };
    }
}
