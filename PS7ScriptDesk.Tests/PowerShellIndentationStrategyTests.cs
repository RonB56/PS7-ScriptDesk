using System.Text.Json;
using System.Reflection;
using ICSharpCode.AvalonEdit.Document;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using PS7ScriptDesk.Shell.Editor;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellIndentationStrategyTests
{
    [Fact]
    public void MissingAutomaticIndentationSettingDefaultsToOffAndExplicitValuesRoundTrip()
    {
        var legacy = JsonSerializer.Deserialize<ApplicationSettings>("{}");
        Assert.Null(legacy?.AutomaticIndentationEnabled);

        foreach (var enabled in new bool?[] { true, false })
        {
            var settings = new ApplicationSettings { AutomaticIndentationEnabled = enabled };
            var loaded = JsonSerializer.Deserialize<ApplicationSettings>(JsonSerializer.Serialize(settings));
            Assert.Equal(enabled, loaded?.AutomaticIndentationEnabled);
        }
    }

    [Fact]
    public void SettingsService_PersistsIndentationChoiceWithoutTouchingUserConfiguration()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-G41-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var service = new ApplicationSettingsService();
        var path = typeof(ApplicationSettingsService).GetField("_settingsFilePath", BindingFlags.Instance | BindingFlags.NonPublic)!;
        path.SetValue(service, Path.Combine(directory, "settings.json"));

        try
        {
            service.SaveSettings(new ApplicationSettings { AutomaticIndentationEnabled = true });
            Assert.True(service.LoadSettings().AutomaticIndentationEnabled);
            service.SaveSettings(new ApplicationSettings { AutomaticIndentationEnabled = false });
            Assert.False(service.LoadSettings().AutomaticIndentationEnabled);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("if ($true) {", "    ")]
    [InlineData("# comment {", "")]
    [InlineData("$text = '{'", "")]
    public void IndentLine_UsesConservativeLocalBraceRules(string previousLine, string expected)
    {
        var document = new TextDocument(previousLine + "\nnext");
        var target = document.GetLineByNumber(2);

        new PowerShellIndentationStrategy().IndentLine(document, target);

        Assert.Equal(previousLine + "\n" + expected + "next", document.Text);
    }

    [Fact]
    public void IndentLine_DoesNotTreatHereStringContentAsCode()
    {
        var document = new TextDocument("$text = @'\ncontent {\nnext\n'@\n");
        var target = document.GetLineByNumber(3);

        new PowerShellIndentationStrategy().IndentLine(document, target);

        Assert.Equal("$text = @'\ncontent {\nnext\n'@\n", document.Text);
    }

    [Fact]
    public void GetIndentationForLine_ComputesLazyPrefixAndKeepsClosingBraceConservative()
    {
        var document = new TextDocument("if ($true) {\n\n");
        var strategy = new PowerShellIndentationStrategy();
        var blankLine = document.GetLineByNumber(2);

        Assert.Equal("    ", strategy.GetIndentationForLine(document, blankLine, "x"));
        Assert.Equal(string.Empty, strategy.GetIndentationForLine(document, blankLine, "}"));
        Assert.Equal("if ($true) {\n\n", document.Text);
    }

    [Fact]
    public void IndentLine_SkipsWhitespaceOnlyLinesAsIndentationSources()
    {
        var document = new TextDocument("if ($true) {\n    \n\n");
        var target = document.GetLineByNumber(3);

        new PowerShellIndentationStrategy().IndentLine(document, target);

        Assert.Equal("if ($true) {\n    \n\n", document.Text);
    }
}
