using System.Reflection;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class ApplicationSettingsServiceReliabilityTests
{
    [Fact]
    public void DeterministicReadFailure_ReturnsSafeDefaults()
    {
        var service = new ApplicationSettingsService();
        var serviceType = typeof(ApplicationSettingsService);
        var pathField = serviceType.GetField("_settingsFilePath", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var readField = serviceType.GetField("_readAllText", BindingFlags.NonPublic | BindingFlags.Static)!;
        var originalPath = pathField.GetValue(service);
        var originalRead = readField.GetValue(null);
        var temporaryPath = Path.GetTempFileName();

        try
        {
            pathField.SetValue(service, temporaryPath);
            readField.SetValue(null, (Func<string, string>)(_ => throw new IOException("Injected settings read failure.")));

            var settings = service.LoadSettings();

            Assert.NotNull(settings);
            Assert.False(settings.IsDeveloperDiagnosticsEnabled);
        }
        finally
        {
            pathField.SetValue(service, originalPath);
            readField.SetValue(null, originalRead);
            File.Delete(temporaryPath);
        }
    }
}
