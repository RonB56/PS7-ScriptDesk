using System.Globalization;
using System.Xml.Linq;

namespace PS7ScriptDesk.Tests;

public sealed class TooltipThemeResourceTests
{
    [Fact]
    public void ThemeDictionaries_DefineReadableTooltipResources()
    {
        foreach (var themeFile in new[] { "LightTheme.xaml", "DarkTheme.xaml", "IseBlueTheme.xaml" })
        {
            var resources = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

            Assert.True(resources.ContainsKey("Theme.ToolTip.Background"), $"{themeFile} is missing Theme.ToolTip.Background.");
            Assert.True(resources.ContainsKey("Theme.ToolTip.Foreground"), $"{themeFile} is missing Theme.ToolTip.Foreground.");
            Assert.True(resources.ContainsKey("Theme.ToolTip.SecondaryForeground"), $"{themeFile} is missing Theme.ToolTip.SecondaryForeground.");
            Assert.True(resources.ContainsKey("Theme.ToolTip.Border"), $"{themeFile} is missing Theme.ToolTip.Border.");

            var background = resources["Theme.ToolTip.Background"];
            var foreground = resources["Theme.ToolTip.Foreground"];
            var secondary = resources["Theme.ToolTip.SecondaryForeground"];

            Assert.True(GetContrastRatio(background, foreground) >= 4.5, $"{themeFile} tooltip foreground contrast is too low.");
            Assert.True(GetContrastRatio(background, secondary) >= 3.0, $"{themeFile} tooltip secondary foreground contrast is too low.");
        }
    }

    [Fact]
    public void AppTooltipStyles_UseThemeAwareTooltipResources()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "App.xaml"));

        Assert.Contains("<Style TargetType=\"ToolTip\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource Theme.ToolTip.Background}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource Theme.ToolTip.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Theme.ToolTip.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource Theme.ToolTip.Border}\"", appXaml, StringComparison.Ordinal);

        Assert.Contains("<Style x:Key=\"ContextHelpToolTipStyle\" TargetType=\"ToolTip\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.ToolTip.Background", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.ToolTip.Foreground", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.ToolTip.Border", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorHoverAndCompletionDescriptions_BindNestedTextToTooltipResources()
    {
        var mainWindowCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "MainWindow.xaml.cs"));
        var completionDataCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellCompletionData.cs"));

        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, \"Theme.ToolTip.Background\")", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, \"Theme.ToolTip.Foreground\")", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, \"Theme.ToolTip.Foreground\")", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, \"Theme.ToolTip.Border\")", mainWindowCode, StringComparison.Ordinal);

        Assert.DoesNotContain("Foreground = Brushes.Gray", completionDataCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(TextBlock.ForegroundProperty, \"Theme.ToolTip.SecondaryForeground\")", completionDataCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(TextBlock.ForegroundProperty, \"Theme.ToolTip.Foreground\")", completionDataCode, StringComparison.Ordinal);
    }

    private static Dictionary<string, Rgb> LoadBrushColors(string relativePath)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var document = XDocument.Load(GetRepositoryPath(relativePath));

        return document
            .Descendants(presentation + "SolidColorBrush")
            .Select(element => new
            {
                Key = (string?)element.Attribute(xaml + "Key"),
                Color = (string?)element.Attribute("Color")
            })
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && !string.IsNullOrWhiteSpace(entry.Color))
            .ToDictionary(entry => entry.Key!, entry => ParseColor(entry.Color!), StringComparer.Ordinal);
    }

    private static Rgb ParseColor(string value)
    {
        var color = value.TrimStart('#');
        Assert.True(color.Length == 6, $"Expected #RRGGBB color but found '{value}'.");

        return new Rgb(
            byte.Parse(color[..2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color[2..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            byte.Parse(color[4..6], NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    }

    private static double GetContrastRatio(Rgb left, Rgb right)
    {
        var leftLuminance = GetRelativeLuminance(left);
        var rightLuminance = GetRelativeLuminance(right);
        var lighter = Math.Max(leftLuminance, rightLuminance);
        var darker = Math.Min(leftLuminance, rightLuminance);
        return (lighter + 0.05) / (darker + 0.05);
    }

    private static double GetRelativeLuminance(Rgb color)
    {
        static double Convert(byte channel)
        {
            var normalized = channel / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Convert(color.Red) + 0.7152 * Convert(color.Green) + 0.0722 * Convert(color.Blue);
    }

    private static string GetRepositoryPath(params string[] pathParts)
    {
        return Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts));
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }

    private readonly record struct Rgb(byte Red, byte Green, byte Blue);
}
