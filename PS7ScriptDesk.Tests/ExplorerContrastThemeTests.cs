using System.Globalization;
using System.Xml.Linq;

namespace PS7ScriptDesk.Tests;

public sealed class ExplorerContrastThemeTests
{
    private static readonly string[] ThemeFiles =
    {
        "LightTheme.xaml",
        "DarkTheme.xaml",
        "IseBlueTheme.xaml"
    };

    [Fact]
    public void ExplorerTree_UsesExplicitThemeContainerAndTextStates()
    {
        var mainXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "MainWindow.xaml"));

        Assert.Contains("x:Key=\"ExplorerTreeItemStyle\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ExplorerTreeItemTextStyle\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource ExplorerTreeItemStyle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Primary", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Secondary", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Selection.Background", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Theme.Surface.Primary}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("BorderBrush=\"{DynamicResource Theme.Border.Subtle}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("BorderThickness=\"0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource Theme.Text.Primary}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsSelected\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsKeyboardFocusWithin\" Value=\"True\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"IsEnabled\" Value=\"False\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource ExplorerTreeItemTextStyle}\"", mainXaml, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ThemeFileNames))]
    public void ExplorerSelectedForeground_RemainsReadableAcrossThemes(string themeFile)
    {
        var colors = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

        Assert.True(GetContrastRatio(colors["Theme.Selection.Background"], colors["Theme.Text.Primary"]) >= 4.5,
            $"{themeFile} Explorer selected text contrast is too low.");
        Assert.True(GetContrastRatio(colors["Theme.Surface.Primary"], colors["Theme.Text.Primary"]) >= 4.5,
            $"{themeFile} Explorer normal text contrast is too low.");
        Assert.NotEqual(colors["Theme.Text.Primary"], colors["Theme.Text.Secondary"]);
    }

    public static IEnumerable<object[]> ThemeFileNames() => ThemeFiles.Select(themeFile => new object[] { themeFile });

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
        Assert.Equal(6, color.Length);
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

    private static string GetRepositoryPath(params string[] pathParts) => Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts));

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
