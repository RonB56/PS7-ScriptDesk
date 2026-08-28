using System.Globalization;
using System.Xml.Linq;

namespace PS7ScriptDesk.Tests;

public sealed class CompletionContrastThemeResourceTests
{
    private static readonly string[] ThemeFiles =
    {
        "LightTheme.xaml",
        "DarkTheme.xaml",
        "IseBlueTheme.xaml"
    };

    [Theory]
    [MemberData(nameof(ThemeFileNames))]
    public void ThemeDictionaries_DefineReadableCompletionForegrounds(string themeFile)
    {
        var resources = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

        foreach (var key in RequiredCompletionBrushKeys)
        {
            Assert.True(resources.ContainsKey(key), $"{themeFile} is missing {key}.");
        }

        Assert.True(GetContrastRatio(resources["Theme.Completion.Background"], resources["Theme.Completion.Foreground"]) >= 4.5, $"{themeFile} completion foreground contrast is too low.");
        Assert.True(GetContrastRatio(resources["Theme.Completion.Background"], resources["Theme.Completion.SecondaryForeground"]) >= 3.0, $"{themeFile} completion secondary foreground contrast is too low.");
        Assert.True(GetContrastRatio(resources["Theme.Completion.HoverBackground"], resources["Theme.Completion.HoverForeground"]) >= 4.5, $"{themeFile} completion hover contrast is too low.");
        Assert.True(GetContrastRatio(resources["Theme.Completion.SelectedBackground"], resources["Theme.Completion.SelectedForeground"]) >= 4.5, $"{themeFile} completion selected contrast is too low.");
    }

    [Theory]
    [MemberData(nameof(ThemeFileNames))]
    public void CompletionDisabledForeground_RemainsDistinctFromNormalForeground(string themeFile)
    {
        var resources = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

        Assert.NotEqual(resources["Theme.Completion.Foreground"], resources["Theme.Completion.DisabledForeground"]);
        Assert.True(
            GetContrastRatio(resources["Theme.Completion.Background"], resources["Theme.Completion.Foreground"]) >
            GetContrastRatio(resources["Theme.Completion.Background"], resources["Theme.Completion.DisabledForeground"]),
            $"{themeFile} disabled completion foreground should be less prominent than normal foreground.");
    }

    [Fact]
    public void CompletionListItemStyle_UsesDynamicThemeResourcesForAllVisualStates()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "App.xaml"));

        Assert.Contains("x:Key=\"PowerShellCompletionListBoxItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource Theme.Completion.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Theme.Completion.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.HoverBackground", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.HoverForeground", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.SelectedBackground", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.SelectedForeground", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.DisabledForeground", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Opacity\" Value=\"1\" />", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionWindow_AppliesThemeResourcesToAvalonEditCompletionList()
    {
        var serviceCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellIntelliSenseService.cs"));

        Assert.Contains("ApplyCompletionListTheme(window)", serviceCode, StringComparison.Ordinal);
        Assert.Contains("CompletionList?.ListBox", serviceCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, \"Theme.Completion.Background\")", serviceCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, \"Theme.Completion.Foreground\")", serviceCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, \"Theme.Completion.Border\")", serviceCode, StringComparison.Ordinal);
        Assert.Contains("PowerShellCompletionListBoxItemStyle", serviceCode, StringComparison.Ordinal);
    }

    [Fact]
    public void CompletionDocumentationText_RemainsOnTooltipResources()
    {
        var completionDataCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellCompletionData.cs"));

        Assert.Contains("SetResourceReference(TextBlock.ForegroundProperty, \"Theme.ToolTip.SecondaryForeground\")", completionDataCode, StringComparison.Ordinal);
        Assert.Contains("SetResourceReference(TextBlock.ForegroundProperty, \"Theme.ToolTip.Foreground\")", completionDataCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground = Brushes.Gray", completionDataCode, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ThemeFileNames()
    {
        foreach (var themeFile in ThemeFiles)
        {
            yield return new object[] { themeFile };
        }
    }

    private static readonly string[] RequiredCompletionBrushKeys =
    {
        "Theme.Completion.Background",
        "Theme.Completion.Foreground",
        "Theme.Completion.SecondaryForeground",
        "Theme.Completion.Border",
        "Theme.Completion.HoverBackground",
        "Theme.Completion.HoverForeground",
        "Theme.Completion.SelectedBackground",
        "Theme.Completion.SelectedForeground",
        "Theme.Completion.DisabledForeground"
    };

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
