using System.Text.Json;
using System.Text.Json.Serialization;

namespace PS7ScriptDesk.RestApiProofHost.Api;

public static class ApiJsonOptions
{
    public static JsonSerializerOptions Shared { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
