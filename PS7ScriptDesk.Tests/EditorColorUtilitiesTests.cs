using PS7ScriptDesk.Shell.Editor;
using System.Windows.Media;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class EditorColorUtilitiesTests
{
    [Theory]
    [InlineData("#0f4c81", "#0F4C81")]
    [InlineData("0f4c81", "#0F4C81")]
    [InlineData("  #abc  ", "#AABBCC")]
    [InlineData("abc", "#AABBCC")]
    [InlineData("#000", "#000000")]
    [InlineData("fff", "#FFFFFF")]
    [InlineData("\tABCDEF\r\n", "#ABCDEF")]
    [InlineData("#A BBCC", "#A BBCC")]
    public void NormalizationPreservesAcceptedFormats(string input, string expected)
    {
        Assert.True(EditorColorUtilities.TryNormalizeHexColor(input, out var normalized));
        Assert.Equal(expected, normalized);
        Assert.Equal(expected, EditorColorUtilities.NormalizeComparableHex(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("#12")]
    [InlineData("#1234")]
    [InlineData("#12345678")]
    [InlineData("#GG0000")]
    [InlineData("##12345")]
    [InlineData("red")]
    public void InvalidInputReturnsEmptyNormalizationNullComparisonAndTransparentColor(string? input)
    {
        Assert.False(EditorColorUtilities.TryNormalizeHexColor(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
        Assert.Null(EditorColorUtilities.NormalizeComparableHex(input));
        Assert.False(EditorColorUtilities.TryParseHexColor(input, out var color));
        Assert.Equal(Colors.Transparent, color);
    }

    [Theory]
    [InlineData("#0f4c81", 15, 76, 129)]
    [InlineData("abc", 170, 187, 204)]
    [InlineData("#A BBCC", 10, 187, 204)]
    public void ParsingUsesExistingRgbChannelConversion(string input, byte red, byte green, byte blue)
    {
        Assert.True(EditorColorUtilities.TryParseHexColor(input, out var color));
        Assert.Equal(Color.FromRgb(red, green, blue), color);
        Assert.Equal($"#{red:X2}{green:X2}{blue:X2}", EditorColorUtilities.FormatColorHex(color));
    }

    [Fact]
    public void FormattingAndContrastUseRgbRegardlessOfAlpha()
    {
        var transparentRgb = Color.FromArgb(0, 15, 76, 129);
        Assert.Equal("#0F4C81", EditorColorUtilities.FormatColorHex(transparentRgb));
        Assert.Equal(1d, EditorColorUtilities.GetContrastRatio(transparentRgb, Color.FromRgb(15, 76, 129)), 12);
    }

    [Fact]
    public void KnownContrastAndLuminanceResultsRemainUnchanged()
    {
        Assert.Equal(21d, EditorColorUtilities.GetContrastRatio(Colors.Black, Colors.White), 12);
        Assert.Equal(21d, EditorColorUtilities.GetContrastRatio(Colors.White, Colors.Black), 12);
        Assert.Equal(1d, EditorColorUtilities.GetContrastRatio(Colors.Red, Colors.Red), 12);
        Assert.Equal(0.2126d, EditorColorUtilities.GetRelativeLuminance(Colors.Red), 12);
        Assert.Equal(4.478089453577214d, EditorColorUtilities.GetContrastRatio(Color.FromRgb(119, 119, 119), Colors.White), 12);
        Assert.Equal((10d / 255d) / 12.92d, EditorColorUtilities.LinearizeSrgbChannel(10), 12);
        Assert.Equal(Math.Pow(((11d / 255d) + 0.055d) / 1.055d, 2.4d), EditorColorUtilities.LinearizeSrgbChannel(11), 12);
    }

    [Fact]
    public void ForegroundSelectionUsesHigherBlackOrWhiteContrast()
    {
        Assert.Equal(Colors.White, EditorColorUtilities.GetBestTextColorForBackground(Colors.Black));
        Assert.Equal(Colors.Black, EditorColorUtilities.GetBestTextColorForBackground(Colors.White));
        Assert.Equal(Colors.White, EditorColorUtilities.GetBestTextColorForBackground(Color.FromRgb(15, 76, 129)));
        Assert.Equal(Colors.Black, EditorColorUtilities.GetBestTextColorForBackground(Color.FromRgb(119, 119, 119)));
    }

    [Fact]
    public void CreatedBrushIsFrozenAndPreservesColor()
    {
        WpfTestDispatcher.Run(() =>
        {
            var color = Color.FromArgb(128, 15, 76, 129);
            var brush = EditorColorUtilities.CreateFrozenBrush(color);
            Assert.Equal(color, brush.Color);
            Assert.True(brush.IsFrozen);
            Assert.Throws<InvalidOperationException>(() => brush.Color = Colors.White);
        });
    }
}
