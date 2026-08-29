using System.Text.Json;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Tests;

public sealed class UiScaleServiceTests
{
    [Fact]
    public void NewService_DefaultsTo100Percent()
    {
        var service = new UiScaleService();

        Assert.Equal(100, service.CurrentPercentage);
        Assert.Equal(1.0, service.CurrentFactor);
    }

    [Fact]
    public void SupportedPercentages_ExposeTheCompleteBoundedPresetSet()
    {
        var service = new UiScaleService();

        Assert.Equal(new[] { 75, 80, 90, 100, 110, 125, 150, 175, 200 }, service.SupportedPercentages);
    }

    [Theory]
    [InlineData(75)]
    [InlineData(100)]
    [InlineData(125)]
    [InlineData(150)]
    [InlineData(200)]
    public void ValidPreset_AppliesImmediately(int percentage)
    {
        var service = new UiScaleService();
        var changed = 0;
        service.ScaleChanged += (_, _) => changed++;

        service.SetPercentage(percentage);

        Assert.Equal(percentage, service.CurrentPercentage);
        Assert.Equal(percentage / 100.0, service.CurrentFactor);
        Assert.Equal(percentage == 100 ? 0 : 1, changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(74)]
    [InlineData(101)]
    [InlineData(201)]
    public void InvalidPersistedPercentage_FallsBackTo100(int? percentage)
    {
        var service = new UiScaleService(percentage);

        Assert.Equal(100, service.CurrentPercentage);
    }

    [Fact]
    public void UnsupportedRuntimeChange_IsRejectedWithoutChangingTheCurrentValue()
    {
        var service = new UiScaleService(125);

        service.SetPercentage(126);

        Assert.Equal(125, service.CurrentPercentage);
    }

    [Fact]
    public void IncreaseAndDecrease_ClampAtPresetBoundaries()
    {
        var service = new UiScaleService(75);

        service.Decrease();
        Assert.Equal(75, service.CurrentPercentage);

        service.Increase();
        Assert.Equal(80, service.CurrentPercentage);

        service.SetPercentage(200);
        service.Increase();
        Assert.Equal(200, service.CurrentPercentage);
    }

    [Fact]
    public void Reset_ReturnsTo100WithoutChangingOtherSettings()
    {
        var settings = new ApplicationSettings { EditorZoomLevel = 17, UiScalePercent = 150 };
        var service = new UiScaleService(settings.UiScalePercent);

        service.Reset();

        Assert.Equal(100, service.CurrentPercentage);
        Assert.Equal(17, settings.EditorZoomLevel);
        Assert.Equal(150, settings.UiScalePercent);
    }

    [Fact]
    public void ApplicationSettings_SerializesUiScaleWithoutChangingEditorZoom()
    {
        var settings = new ApplicationSettings { UiScalePercent = 125, EditorZoomLevel = 14 };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<ApplicationSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(125, restored!.UiScalePercent);
        Assert.Equal(14, restored.EditorZoomLevel);
    }
}
