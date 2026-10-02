using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Native;

/// <summary>
/// Requests native DWM caption colors while leaving the standard WPF/Windows
/// frame, hit testing, system menu, and caption buttons intact.
/// </summary>
internal static class NativeTitleBarTheme
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private const string DarkTheme = "Dark";
    private const string LightTheme = "Light";
    private const string IseBlueTheme = "IseBlue";

    private readonly record struct Colors(bool UseDarkMode, uint Caption, uint Border, uint Text);

    public static bool TryApply(Window window, string? themeName)
    {
        if (window is null)
        {
            return false;
        }

        IntPtr hwnd;
        try
        {
            hwnd = new WindowInteropHelper(window).Handle;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            DeveloperDiagnostics.LogWarning("UI", "Native title-bar theming skipped because the window handle was unavailable.");
            return false;
        }

        if (hwnd == IntPtr.Zero)
        {
            DeveloperDiagnostics.LogWarning("UI", "Native title-bar theming skipped because the window handle was not created yet.");
            return false;
        }

        var colors = GetColors(themeName);
        var applied = false;

        try
        {
            var darkMode = colors.UseDarkMode ? 1 : 0;
            var darkModeResult = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
            if (darkModeResult != 0)
            {
                darkModeResult = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, ref darkMode, sizeof(int));
            }

            applied |= darkModeResult == 0;
            applied |= TrySetColor(hwnd, DwmwaCaptionColor, colors.Caption);
            applied |= TrySetColor(hwnd, DwmwaBorderColor, colors.Border);
            applied |= TrySetColor(hwnd, DwmwaTextColor, colors.Text);

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Native title-bar theme request completed without changing the native window frame.",
                new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["theme"] = NormalizeThemeName(themeName),
                    ["darkModeAttributeApplied"] = darkModeResult == 0,
                    ["anyAttributeApplied"] = applied
                });
            return applied;
        }
        catch (DllNotFoundException ex)
        {
            DeveloperDiagnostics.LogException("UI", ex, "Native title-bar theming is unavailable because DWM could not be loaded.");
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            DeveloperDiagnostics.LogException("UI", ex, "Native title-bar theming is unavailable on this Windows build.");
            return false;
        }
        catch (ExternalException ex)
        {
            DeveloperDiagnostics.LogException("UI", ex, "Native title-bar theming failed; the default native frame remains active.");
            return false;
        }
    }

    private static bool TrySetColor(IntPtr hwnd, int attribute, uint color)
    {
        var result = DwmSetWindowAttribute(hwnd, attribute, ref color, sizeof(uint));
        return result == 0;
    }

    private static Colors GetColors(string? themeName)
    {
        return NormalizeThemeName(themeName) switch
        {
            LightTheme => new Colors(false, ToColorRef(0xEEF4FB), ToColorRef(0xC7D2E3), ToColorRef(0x172033)),
            IseBlueTheme => new Colors(true, ToColorRef(0x00163F), ToColorRef(0x164A9A), ToColorRef(0xF1F6FF)),
            _ => new Colors(true, ToColorRef(0x111827), ToColorRef(0x263449), ToColorRef(0xE5EEF9))
        };
    }

    private static string NormalizeThemeName(string? themeName)
        => string.Equals(themeName, LightTheme, StringComparison.OrdinalIgnoreCase)
            ? LightTheme
            : string.Equals(themeName, IseBlueTheme, StringComparison.OrdinalIgnoreCase)
                ? IseBlueTheme
                : DarkTheme;

    private static uint ToColorRef(int rgb)
        => (uint)((rgb & 0xFF) << 16 | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize);
}
