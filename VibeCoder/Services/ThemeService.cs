using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;
using Agent.Core.Services;
using VibeCoder.Core.Services;

namespace VibeCoder.Services
{
    /// <summary>
    /// Applies the Material Design theme at runtime: base theme (dark/light),
    /// primary/secondary colors (presets) and the chrome brushes.
    /// Operates on the UserControl's resource dictionary (not Application.Current), because
    /// the theme lives in VibeCoderToolWindowControl.Resources inside the VSIX.
    /// </summary>
    public class ThemeService
    {
        private readonly ResourceDictionary _resources;

        // Chrome brushes shared by the custom (non-Material) UI. Keys are referenced via
        // DynamicResource in XAML so replacing the dictionary entry updates the whole window.
        private static readonly Dictionary<string, string> DarkChrome = new Dictionary<string, string>
        {
            ["Dark.Window"] = "#2D2D30",
            ["Dark.Panel"] = "#252526",
            ["Dark.Control"] = "#333337",
            ["Dark.ControlHover"] = "#3E3E42",
            ["Dark.ControlPressed"] = "#264F78",
            ["Dark.Border"] = "#3F3F46",
            ["Dark.Text"] = "#F1F1F1",
            ["Dark.Accent"] = "#264F78",
            ["Dark.ErrorText"] = "#F48771",
            ["Code.Background"] = "#1E1E1E"
        };

        private static readonly Dictionary<string, string> LightChrome = new Dictionary<string, string>
        {
            ["Dark.Window"] = "#F5F5F5",
            ["Dark.Panel"] = "#FFFFFF",
            ["Dark.Control"] = "#FFFFFF",
            ["Dark.ControlHover"] = "#E6E6E6",
            ["Dark.ControlPressed"] = "#C9DEF5",
            ["Dark.Border"] = "#D4D4D4",
            ["Dark.Text"] = "#1E1E1E",
            ["Dark.Accent"] = "#0A6CFF",
            ["Dark.ErrorText"] = "#C42B1C",
            ["Code.Background"] = "#FFFFFF"
        };

        public ThemeService(ResourceDictionary resources)
        {
            _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        }

        private BundledTheme GetBundledTheme() =>
            _resources.MergedDictionaries.OfType<BundledTheme>().FirstOrDefault();

        public bool IsDark()
        {
            var bundled = GetBundledTheme();
            return bundled == null || bundled.GetTheme().GetBaseTheme() == BaseTheme.Dark;
        }

        public static bool IsDark(string baseTheme) =>
            !string.Equals(baseTheme, "Light", StringComparison.OrdinalIgnoreCase);

        public static Color ColorFromHex(string hex)
        {
            if (ThemeColorMath.TryParseHex(hex, out byte r, out byte g, out byte b))
                return Color.FromRgb(r, g, b);
            return Colors.White;
        }

        public static string ToHex(Color color) => ThemeColorMath.RgbToHex(color.R, color.G, color.B);

        /// <summary>Replaces the custom chrome brushes with the dark or light palette.</summary>
        public void ApplyChrome(bool isDark)
        {
            var source = isDark ? DarkChrome : LightChrome;
            foreach (var pair in source)
                _resources[pair.Key] = new SolidColorBrush(ColorFromHex(pair.Value));
        }

        public void ApplyBaseTheme(bool isDark)
        {
            var bundled = GetBundledTheme();
            if (bundled == null) return;

            var theme = bundled.GetTheme();
            theme.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);
            bundled.SetTheme(theme);
        }

        public void ApplyPrimary(Color color)
        {
            var bundled = GetBundledTheme();
            if (bundled == null) return;

            var theme = bundled.GetTheme();
            theme.SetPrimaryColor(color);
            bundled.SetTheme(theme);
        }

        public void ApplySecondary(Color color)
        {
            var bundled = GetBundledTheme();
            if (bundled == null) return;

            var theme = bundled.GetTheme();
            theme.SetSecondaryColor(color);
            bundled.SetTheme(theme);
        }

        public void ApplyResolvedTheme(bool isDark, Color primary, Color secondary)
        {
            var bundled = GetBundledTheme();
            if (bundled == null) return;

            var theme = bundled.GetTheme();
            theme.SetBaseTheme(isDark ? BaseTheme.Dark : BaseTheme.Light);
            theme.SetPrimaryColor(primary);
            theme.SetSecondaryColor(secondary);
            bundled.SetTheme(theme);
        }

    }
}
