using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Dialogs;

internal static class LocalApiTestRequestBuilder
{
    public static IReadOnlyList<string> CreateQueryPairs(IEnumerable<RestApiParameterBindingRow> parameters)
        => parameters
            .Where(parameter => parameter.Source == ApiParameterSource.Query && ShouldIncludeParameterValue(parameter))
            .Select(parameter =>
            {
                var apiName = ResolveApiParameterName(parameter);
                var value = parameter.TestValue ?? string.Empty;
                return $"{Uri.EscapeDataString(apiName)}={Uri.EscapeDataString(value)}";
            })
            .ToList();

    public static bool ShouldIncludeParameterValue(RestApiParameterBindingRow parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return parameter.Source != ApiParameterSource.Query ||
               parameter.Required != ApiRequiredBehavior.Optional ||
               !string.IsNullOrWhiteSpace(parameter.TestValue);
    }

    public static string ResolveApiParameterName(RestApiParameterBindingRow parameter)
        => string.IsNullOrWhiteSpace(parameter.Name)
            ? parameter.PowerShellParameterName
            : parameter.Name;
}
