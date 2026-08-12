using System;
using System.Collections.Generic;
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
        public static event EventHandler ThemeChanged;

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
                if (!(resources[pair.Value] is SolidColorBrush brush) || brush.IsFrozen) continue;
                brush.Color = (Color)ColorConverter.ConvertFromString(palette[pair.Key]);
            }

            if (!(resources["WidgetSurfaceBrush"] is LinearGradientBrush surface) || surface.IsFrozen
                || surface.GradientStops.Count < 2) return;
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
                ["BrandPaleColor"] = "#FFF0E5",
                ["AppBackgroundColor"] = "#F5F5F3",
                ["TitleBarColor"] = "#FAFAF8",
                ["SidebarColor"] = "#FAFAF8",
                ["SurfaceColor"] = "#FFFFFFFF",
                ["SurfaceRaisedColor"] = "#FFFFFFFF",
                ["SurfaceMutedColor"] = "#F8F8F6",
                ["InputColor"] = "#FFFFFFFF",
                ["TextPrimaryColor"] = "#202124",
                ["TextSecondaryColor"] = "#676B72",
                ["TextTertiaryColor"] = "#989BA1",
                ["HairlineColor"] = "#E3E3DF",
                ["WindowBorderColor"] = "#D8D8D4",
                ["HoverColor"] = "#EEEDEA",
                ["PressedColor"] = "#E7E5E1",
                ["SuccessPaleColor"] = "#E8F6EE",
                ["DangerPaleColor"] = "#FCEBEA",
                ["FloatingTopColor"] = "#FFFCFCFB",
                ["FloatingBottomColor"] = "#FFF7F7F5",
                ["FloatingRaisedColor"] = "#FFFFFFFF",
                ["FloatingDeepColor"] = "#FFF4F4F1",
                ["FloatingInputColor"] = "#FFFFFFFF",
                ["FloatingHoverColor"] = "#FFECEBE8",
                ["FloatingBorderColor"] = "#FFD5D5D1",
                ["FloatingHairlineColor"] = "#FFE6E6E2",
                ["FloatingTextPrimaryColor"] = "#FF202124",
                ["FloatingTextSecondaryColor"] = "#FF696D74",
                ["FloatingTextTertiaryColor"] = "#FF979AA0",
                ["FloatingOrangePaleColor"] = "#FFFFE9D9",
            };

        private static readonly IReadOnlyDictionary<string, string> DarkPalette =
            new Dictionary<string, string>
            {
                ["BrandPaleColor"] = "#342A25",
                ["AppBackgroundColor"] = "#17181A",
                ["TitleBarColor"] = "#1C1D20",
                ["SidebarColor"] = "#1B1C1F",
                ["SurfaceColor"] = "#202125",
                ["SurfaceRaisedColor"] = "#242529",
                ["SurfaceMutedColor"] = "#1C1D21",
                ["InputColor"] = "#25262A",
                ["TextPrimaryColor"] = "#E4E5E7",
                ["TextSecondaryColor"] = "#A3A6AC",
                ["TextTertiaryColor"] = "#74777D",
                ["HairlineColor"] = "#2B2D32",
                ["WindowBorderColor"] = "#303238",
                ["HoverColor"] = "#292B30",
                ["PressedColor"] = "#303238",
                ["SuccessPaleColor"] = "#1B2D23",
                ["DangerPaleColor"] = "#332123",
                ["FloatingTopColor"] = "#FF212226",
                ["FloatingBottomColor"] = "#FF1A1B1E",
                ["FloatingRaisedColor"] = "#FF232428",
                ["FloatingDeepColor"] = "#FF18191C",
                ["FloatingInputColor"] = "#FF26272B",
                ["FloatingHoverColor"] = "#FF2A2C30",
                ["FloatingBorderColor"] = "#FF3A3C42",
                ["FloatingHairlineColor"] = "#FF2C2E33",
                ["FloatingTextPrimaryColor"] = "#FFE5E6E8",
                ["FloatingTextSecondaryColor"] = "#FFA2A5AA",
                ["FloatingTextTertiaryColor"] = "#FF73767C",
                ["FloatingOrangePaleColor"] = "#FF3C2C24",
            };
    }
}
