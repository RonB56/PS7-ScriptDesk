using System.Text.Json;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Infrastructure.Services;

namespace PS7ScriptDesk.Tests;

public sealed class ApiPublishConfigurationStoreTests : IDisposable
{
    private readonly string _testDirectory = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk.ApiStore-{Guid.NewGuid():N}");
    private readonly ApiPublishConfigurationStore _store = new();

    public ApiPublishConfigurationStoreTests()
    {
        Directory.CreateDirectory(_testDirectory);
    }

    [Theory]
    [InlineData("Test.ps1", "Test.ps7api.json")]
    [InlineData("My.Script.ps1", "My.Script.ps7api.json")]
    [InlineData("Upper.PS1", "Upper.ps7api.json")]
    [InlineData("Unicode-測試.ps1", "Unicode-測試.ps7api.json")]
    public void CompanionPath_ReplacesPowerShellExtension(string scriptName, string expectedName)
    {
        var scriptPath = Path.Combine(_testDirectory, scriptName);

        Assert.Equal(Path.Combine(_testDirectory, expectedName), _store.GetCompanionPath(scriptPath));
    }

    [Fact]
    public void CompanionPath_PreservesRelativePathAndRejectsUnsavedPath()
    {
        Assert.Equal(Path.Combine("scripts", "Test.ps7api.json"), _store.GetCompanionPath(Path.Combine("scripts", "Test.ps1")));
        Assert.Null(_store.GetCompanionPath(null));
        Assert.Null(_store.GetCompanionPath(""));
        Assert.Null(_store.GetCompanionPath("notes.txt"));
    }

    [Fact]
    public void SaveLoadExists_UsesReadableJsonAndRelativeSourceIdentity()
    {
        var scriptPath = Path.Combine(_testDirectory, "Inventory.ps1");
        File.WriteAllText(scriptPath, "function Get-SystemInfo { }");
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(scriptPath);
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Get-SystemInfo", ApiHttpMethod.Get, "/api/systeminfo"));

        _store.Save(scriptPath, configuration);

        var companionPath = _store.GetCompanionPath(scriptPath)!;
        Assert.True(_store.ConfigurationExists(scriptPath));
        var json = File.ReadAllText(companionPath);
        Assert.Contains("\"schemaVersion\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"sourceScript\": \"Inventory.ps1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(_testDirectory, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("$type", json, StringComparison.OrdinalIgnoreCase);

        var loaded = _store.Load(scriptPath);
        Assert.Equal("Inventory.ps1", loaded.SourceScript);
        Assert.Equal(ApiTransport.Rest, loaded.Transport);
        Assert.Single(loaded.Endpoints);
        Assert.Equal("Get-SystemInfo", loaded.Endpoints[0].PowerShellFunctionName);
    }

    [Fact]
    public void JsonRoundTrip_PreservesEnvironmentSecretReferenceWithoutSecretValue()
    {
        var scriptPath = Path.Combine(_testDirectory, "Secrets.ps1");
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(scriptPath);
        configuration.Security.ApiKeyEnvironmentVariableName = "PS7API_API_KEY";
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Invoke-Secret", ApiHttpMethod.Post, "/api/secret"));
        configuration.Endpoints[0].ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "Token",
            Source = ApiParameterSource.ServerDefined,
            IsSecretSensitive = true,
            ServerValue = new ApiServerDefinedValue
            {
                Kind = ApiServerDefinedValueKind.EnvironmentVariable,
                Value = "PS7API_TOKEN"
            }
        });

        _store.Save(scriptPath, configuration);

        var json = File.ReadAllText(_store.GetCompanionPath(scriptPath)!);
        Assert.Contains("PS7API_TOKEN", json, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET_SENTINEL_123", json, StringComparison.Ordinal);
        Assert.Equal("PS7API_TOKEN", _store.Load(scriptPath).Endpoints[0].ParameterBindings[0].ServerValue!.Value);
    }

    [Fact]
    public void Save_RefusesSecretMarkedLiteralAndDoesNotWriteSentinel()
    {
        var scriptPath = Path.Combine(_testDirectory, "Unsafe.ps1");
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(scriptPath);
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Invoke-Unsafe", ApiHttpMethod.Post, "/api/unsafe"));
        configuration.Endpoints[0].ParameterBindings.Add(new ApiParameterBindingConfiguration
        {
            PowerShellParameterName = "Token",
            Source = ApiParameterSource.ServerDefined,
            IsSecretSensitive = true,
            ServerValue = new ApiServerDefinedValue
            {
                Kind = ApiServerDefinedValueKind.Literal,
                Value = "SECRET_SENTINEL_123"
            }
        });

        Assert.Throws<InvalidOperationException>(() => _store.Save(scriptPath, configuration));
        Assert.False(File.Exists(_store.GetCompanionPath(scriptPath)!));
    }

    [Fact]
    public void Load_RejectsMalformedJsonMissingSchemaAndUnsupportedSchemaVersion()
    {
        var malformedScript = Path.Combine(_testDirectory, "Malformed.ps1");
        File.WriteAllText(_store.GetCompanionPath(malformedScript)!, "{ not json");
        Assert.Throws<InvalidDataException>(() => _store.Load(malformedScript));

        var missingSchemaScript = Path.Combine(_testDirectory, "MissingSchema.ps1");
        File.WriteAllText(_store.GetCompanionPath(missingSchemaScript)!, "{}");
        Assert.Throws<InvalidDataException>(() => _store.Load(missingSchemaScript));

        var futureSchemaScript = Path.Combine(_testDirectory, "Future.ps1");
        File.WriteAllText(_store.GetCompanionPath(futureSchemaScript)!, "{\"schemaVersion\":999}");
        Assert.Throws<InvalidDataException>(() => _store.Load(futureSchemaScript));
    }

    [Fact]
    public void Save_DoesNotSilentlyOverwriteMalformedExistingJson()
    {
        var scriptPath = Path.Combine(_testDirectory, "Existing.ps1");
        var companionPath = _store.GetCompanionPath(scriptPath)!;
        File.WriteAllText(companionPath, "{ broken");

        Assert.Throws<InvalidDataException>(() => _store.Save(scriptPath, ApiPublishConfiguration.CreateDefaultForScriptPath(scriptPath)));
        Assert.Equal("{ broken", File.ReadAllText(companionPath));
    }

    [Fact]
    public void Save_UsesTemporaryFilePatternAndCleansUpAfterSuccess()
    {
        var scriptPath = Path.Combine(_testDirectory, "Cleanup.ps1");

        _store.Save(scriptPath, ApiPublishConfiguration.CreateDefaultForScriptPath(scriptPath));

        Assert.Empty(Directory.GetFiles(_testDirectory, "*.tmp"));
    }

    [Fact]
    public void Save_RequiresSavedPowerShellScriptPath()
    {
        Assert.Throws<InvalidOperationException>(() => _store.Save(string.Empty, new ApiPublishConfiguration()));
        Assert.Throws<InvalidOperationException>(() => _store.Save(Path.Combine(_testDirectory, "NotPowerShell.txt"), new ApiPublishConfiguration()));
    }

    [Fact]
    public void UnknownJsonFields_AreIgnoredForForwardCompatibility()
    {
        var scriptPath = Path.Combine(_testDirectory, "Unknown.ps1");
        var json = """
            {
              "schemaVersion": 1,
              "sourceScript": "Unknown.ps1",
              "transport": "Rest",
              "api": { "title": "Unknown", "future": true },
              "endpoints": [],
              "runtime": {},
              "security": {},
              "openApi": {},
              "futureRoot": { "ignored": true }
            }
            """;
        File.WriteAllText(_store.GetCompanionPath(scriptPath)!, json);

        var loaded = _store.Load(scriptPath);

        Assert.Equal("Unknown.ps1", loaded.SourceScript);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDirectory))
            {
                Directory.Delete(_testDirectory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
