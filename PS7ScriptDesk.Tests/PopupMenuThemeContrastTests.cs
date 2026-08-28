using System.Globalization;
using System.Xml.Linq;

namespace PS7ScriptDesk.Tests;

public sealed class PopupMenuThemeContrastTests
{
    private static readonly string[] ThemeFiles =
    {
        "LightTheme.xaml",
        "DarkTheme.xaml",
        "IseBlueTheme.xaml"
    };

    [Theory]
    [MemberData(nameof(ThemeFileNames))]
    public void ThemeDictionaries_DefineReadableMenuForegrounds(string themeFile)
    {
        var resources = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

        foreach (var key in RequiredMenuBrushKeys)
        {
            Assert.True(resources.ContainsKey(key), $"{themeFile} is missing {key}.");
        }

        Assert.True(GetContrastRatio(resources["Theme.Menu.Background"], resources["Theme.Menu.Foreground"]) >= 4.5, $"{themeFile} enabled menu foreground contrast is too low.");
        Assert.True(GetContrastRatio(resources["Theme.Menu.HoverBackground"], resources["Theme.Menu.HoverForeground"]) >= 4.5, $"{themeFile} hover menu foreground contrast is too low.");
        Assert.True(GetContrastRatio(resources["Theme.Menu.SelectedBackground"], resources["Theme.Menu.SelectedForeground"]) >= 4.5, $"{themeFile} selected menu foreground contrast is too low.");
    }

    [Theory]
    [MemberData(nameof(ThemeFileNames))]
    public void MenuDisabledForeground_RemainsDistinctFromEnabledForeground(string themeFile)
    {
        var resources = LoadBrushColors(Path.Combine("PS7ScriptDesk.Shell", "Themes", themeFile));

        Assert.NotEqual(resources["Theme.Menu.Foreground"], resources["Theme.Menu.DisabledForeground"]);
        Assert.True(
            GetContrastRatio(resources["Theme.Menu.Background"], resources["Theme.Menu.Foreground"]) >
            GetContrastRatio(resources["Theme.Menu.Background"], resources["Theme.Menu.DisabledForeground"]),
            $"{themeFile} disabled menu foreground should be less prominent than enabled foreground.");
    }

    [Fact]
    public void ContextMenuAndMenuItemStyles_UseDynamicMenuResourcesForRuntimeThemeSwitching()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "App.xaml"));

        Assert.Contains("<Style TargetType=\"ContextMenu\" BasedOn=\"{StaticResource MenuPopupContextMenuStyle}\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"MenuPopupContextMenuStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Background\" Value=\"{DynamicResource Theme.Menu.Background}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"Foreground\" Value=\"{DynamicResource Theme.Menu.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Theme.Menu.Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"BorderBrush\" Value=\"{DynamicResource Theme.Menu.Border}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Theme.Menu.HoverForeground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Property=\"TextElement.Foreground\" Value=\"{DynamicResource Theme.Menu.DisabledForeground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Background=\"{DynamicResource Theme.Menu.Separator}\"", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MenuItemTemplate_ThemesHeaderGestureCheckmarkAndSubmenuArrow()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "App.xaml"));

        Assert.Contains("x:Name=\"CheckGlyph\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GestureText\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SubmenuArrow\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{TemplateBinding Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Fill=\"{TemplateBinding Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("TextElement.Foreground=\"{TemplateBinding Foreground}\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Theme.Text.Secondary\" />\r\n                                <Setter TargetName=\"RootBorder\" Property=\"Opacity\" Value=\"0.72\"", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EditorRightClickContextMenu_UsesSharedImplicitContextMenuAndMenuItemStyles()
    {
        var mainXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "MainWindow.xaml"));
        var menuStart = mainXaml.IndexOf("<avalonedit:TextEditor.ContextMenu>", StringComparison.Ordinal);
        Assert.True(menuStart >= 0, "Editor context menu was not found.");

        var menuEnd = mainXaml.IndexOf("</avalonedit:TextEditor.ContextMenu>", menuStart, StringComparison.Ordinal);
        Assert.True(menuEnd > menuStart, "Editor context menu end tag was not found.");

        var editorContextMenuXaml = mainXaml[menuStart..menuEnd];

        Assert.Contains("<ContextMenu>", editorContextMenuXaml, StringComparison.Ordinal);
        Assert.Contains("<MenuItem Header=\"Cut\"", editorContextMenuXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<ContextMenu Style=", editorContextMenuXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"Gray\"", editorContextMenuXaml, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Foreground=\"{DynamicResource Theme.Text.Secondary}\"", editorContextMenuXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingTooltipAndCompletionThemeResources_RemainIntact()
    {
        var appXaml = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "App.xaml"));
        var completionCode = File.ReadAllText(GetRepositoryPath("PS7ScriptDesk.Shell", "Editor", "PowerShellIntelliSenseService.cs"));

        Assert.Contains("<Style TargetType=\"ToolTip\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.ToolTip.Foreground", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"PowerShellCompletionListBoxItemStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Completion.Foreground", appXaml, StringComparison.Ordinal);
        Assert.Contains("ApplyCompletionListTheme(window)", completionCode, StringComparison.Ordinal);
    }

    public static IEnumerable<object[]> ThemeFileNames()
    {
        foreach (var themeFile in ThemeFiles)
        {
            yield return new object[] { themeFile };
        }
    }

    private static readonly string[] RequiredMenuBrushKeys =
    {
        "Theme.Menu.Background",
        "Theme.Menu.Foreground",
        "Theme.Menu.HoverBackground",
        "Theme.Menu.HoverForeground",
        "Theme.Menu.SelectedBackground",
        "Theme.Menu.SelectedForeground",
        "Theme.Menu.DisabledForeground",
        "Theme.Menu.Border",
        "Theme.Menu.Separator"
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
