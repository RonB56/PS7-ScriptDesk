using System;
using System.Globalization;
using WpfColor = System.Windows.Media.Color;
using WpfColors = System.Windows.Media.Colors;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace PS7ScriptDesk.Shell.Editor
{
    internal static class EditorColorUtilities
    {
        internal static bool TryNormalizeHexColor(string? value, out string normalizedHex)
        {
            normalizedHex = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                text = "#" + text;
            }

            if (text.Length == 4)
            {
                text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
            }

            if (text.Length != 7)
            {
                return false;
            }

            if (!byte.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                !byte.TryParse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                !byte.TryParse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            normalizedHex = text.ToUpperInvariant();
            return true;
        }

        internal static string? NormalizeComparableHex(string? value)
        {
            return TryNormalizeHexColor(value, out var normalizedHex) ? normalizedHex : null;
        }

        internal static bool TryParseHexColor(string? value, out WpfColor color)
        {
            if (!TryNormalizeHexColor(value, out var normalizedHex))
            {
                color = WpfColors.Transparent;
                return false;
            }

            color = WpfColor.FromRgb(
                byte.Parse(normalizedHex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedHex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedHex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return true;
        }

        internal static string FormatColorHex(WpfColor color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        internal static WpfSolidColorBrush CreateFrozenBrush(WpfColor color)
        {
            var brush = new WpfSolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        internal static WpfColor GetBestTextColorForBackground(WpfColor backgroundColor)
        {
            var whiteContrast = GetContrastRatio(WpfColors.White, backgroundColor);
            var blackContrast = GetContrastRatio(WpfColors.Black, backgroundColor);
            return whiteContrast >= blackContrast ? WpfColors.White : WpfColors.Black;
        }

        internal static double GetContrastRatio(WpfColor foreground, WpfColor background)
        {
            var foregroundLuminance = GetRelativeLuminance(foreground);
            var backgroundLuminance = GetRelativeLuminance(background);
            var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
            var darker = Math.Min(foregroundLuminance, backgroundLuminance);
            return (lighter + 0.05d) / (darker + 0.05d);
        }

        internal static double GetRelativeLuminance(WpfColor color)
        {
            return (0.2126d * LinearizeSrgbChannel(color.R)) +
                   (0.7152d * LinearizeSrgbChannel(color.G)) +
                   (0.0722d * LinearizeSrgbChannel(color.B));
        }

        internal static double LinearizeSrgbChannel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }
    }
}
