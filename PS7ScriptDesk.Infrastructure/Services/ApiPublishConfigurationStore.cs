using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Infrastructure.Services;

public sealed class ApiPublishConfigurationStore : IApiPublishConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

    public static JsonSerializerOptions CreateSerializerOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public string? GetCompanionPath(string? sourceScriptPath)
    {
        if (string.IsNullOrWhiteSpace(sourceScriptPath))
        {
            return null;
        }

        var extension = Path.GetExtension(sourceScriptPath);
        if (!string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.ChangeExtension(sourceScriptPath, ".ps7api.json");
    }

    public bool ConfigurationExists(string sourceScriptPath)
    {
        var companionPath = RequireCompanionPath(sourceScriptPath);
        return File.Exists(companionPath);
    }

    public ApiPublishConfiguration Load(string sourceScriptPath)
    {
        var companionPath = RequireCompanionPath(sourceScriptPath);
        try
        {
            using var stream = File.OpenRead(companionPath);
            EnsureSupportedSchemaVersion(stream, companionPath);
            stream.Position = 0;
            return JsonSerializer.Deserialize<ApiPublishConfiguration>(stream, SerializerOptions)
                   ?? throw new InvalidDataException($"The API companion file '{companionPath}' did not contain a configuration object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The API companion file '{companionPath}' is not valid JSON.", ex);
        }
    }

    public void Save(string sourceScriptPath, ApiPublishConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var companionPath = RequireCompanionPath(sourceScriptPath);
        RefusePlaintextSecretLiterals(configuration);

        if (File.Exists(companionPath))
        {
            using var existing = File.OpenRead(companionPath);
            EnsureSupportedSchemaVersion(existing, companionPath);
        }

        var directoryPath = Path.GetDirectoryName(Path.GetFullPath(companionPath));
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("Unable to resolve the API companion-file directory.");
        }

        Directory.CreateDirectory(directoryPath);
        configuration.SchemaVersion = ApiPublishConfiguration.CurrentSchemaVersion;
        configuration.SourceScript = Path.GetFileName(sourceScriptPath);

        var temporaryPath = Path.Combine(
            directoryPath,
            $".{Path.GetFileName(companionPath)}.ps7scriptdesk-{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       options: FileOptions.SequentialScan))
            {
                JsonSerializer.Serialize(stream, configuration, SerializerOptions);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(companionPath))
            {
                File.Replace(temporaryPath, companionPath, destinationBackupFileName: null, ignoreMetadataErrors: false);
            }
            else
            {
                File.Move(temporaryPath, companionPath);
            }
        }
        finally
        {
            TryDeleteTemporaryFile(temporaryPath);
        }
    }

    private static void EnsureSupportedSchemaVersion(Stream stream, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaVersionElement) ||
                schemaVersionElement.ValueKind != JsonValueKind.Number ||
                !schemaVersionElement.TryGetInt32(out var schemaVersion) ||
                schemaVersion <= 0)
            {
                throw new InvalidDataException($"The API companion file '{path}' is missing a valid schemaVersion value.");
            }

            if (schemaVersion != ApiPublishConfiguration.CurrentSchemaVersion)
            {
                throw new InvalidDataException($"The API companion file '{path}' uses unsupported schemaVersion {schemaVersion}.");
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The API companion file '{path}' is not valid JSON.", ex);
        }
    }

    private static void RefusePlaintextSecretLiterals(ApiPublishConfiguration configuration)
    {
        foreach (var endpoint in configuration.Endpoints)
        {
            foreach (var binding in endpoint.ParameterBindings)
            {
                if (binding.Source == ApiParameterSource.ServerDefined &&
                    binding.IsSecretSensitive &&
                    binding.ServerValue?.Kind == ApiServerDefinedValueKind.Literal)
                {
                    throw new InvalidOperationException("Secret-sensitive server-defined values cannot be persisted as plaintext literals. Use an environment-variable reference.");
                }
            }
        }
    }

    private string RequireCompanionPath(string? sourceScriptPath)
    {
        var companionPath = GetCompanionPath(sourceScriptPath);
        if (string.IsNullOrWhiteSpace(companionPath))
        {
            throw new InvalidOperationException("A saved .ps1 source script path is required before an API companion file can be used.");
        }

        return companionPath;
    }

    private static void TryDeleteTemporaryFile(string temporaryPath)
    {
        try
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
        catch
        {
            // Best-effort cleanup only. The original companion file has already been preserved or replaced.
        }
    }
}
