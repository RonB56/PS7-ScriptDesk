using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed record PSScriptAnalyzerNormalizationResult(
    IReadOnlyList<ScriptDiagnostic> Diagnostics,
    int RejectedFindingCount);

/// <summary>Pure conversion from raw worker findings to shared ScriptDesk diagnostics.</summary>
public static class PSScriptAnalyzerResultNormalizer
{
    public static PSScriptAnalyzerNormalizationResult Normalize(
        PSScriptAnalyzerRequest request,
        PSScriptAnalyzerResult result)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(result);

        if (!Guid.TryParse(request.DocumentId, out var documentId) ||
            !string.Equals(request.RequestId, result.RequestId, StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(result.Error))
        {
            return new PSScriptAnalyzerNormalizationResult(Array.Empty<ScriptDiagnostic>(), result.Findings?.Count ?? 0);
        }

        var lines = request.ScriptText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var normalized = new List<ScriptDiagnostic>();
        var rejected = 0;
        foreach (var finding in result.Findings ?? Array.Empty<PSScriptAnalyzerFinding>())
        {
            if (TryNormalize(request, documentId, lines, finding, out var diagnostic))
            {
                normalized.Add(diagnostic!);
            }
            else
            {
                rejected++;
            }
        }

        return new PSScriptAnalyzerNormalizationResult(
            normalized
                .DistinctBy(static diagnostic => (diagnostic.RuleId, diagnostic.Message, diagnostic.StartLine, diagnostic.StartColumn, diagnostic.EndLine, diagnostic.EndColumn))
                .ToArray(),
            rejected);
    }

    private static bool TryNormalize(
        PSScriptAnalyzerRequest request,
        Guid documentId,
        string[] lines,
        PSScriptAnalyzerFinding finding,
        out ScriptDiagnostic? diagnostic)
    {
        diagnostic = null;
        if (string.IsNullOrWhiteSpace(finding.Message) || finding.Line < 1 || finding.Column < 1)
        {
            return false;
        }

        var line = Math.Min(finding.Line, Math.Max(1, lines.Length));
        var column = Math.Min(finding.Column, Math.Max(1, lines[line - 1].Length + 1));
        var endLine = finding.EndLine.GetValueOrDefault(line);
        var endColumn = finding.EndColumn.GetValueOrDefault(column);
        if (endLine < 1 || endColumn < 1)
        {
            return false;
        }

        endLine = Math.Clamp(endLine, line, Math.Max(line, lines.Length));
        endColumn = Math.Min(endColumn, Math.Max(1, lines[endLine - 1].Length + 1));
        if (endLine == line && endColumn < column)
        {
            endColumn = column;
        }

        var correction = string.IsNullOrWhiteSpace(finding.Correction)
            ? null
            : new Dictionary<string, string> { ["correction"] = finding.Correction };

        diagnostic = new ScriptDiagnostic(
            documentId,
            request.Revision,
            ScriptDiagnosticSource.PSScriptAnalyzer,
            finding.RuleId,
            finding.Message,
            MapSeverity(finding.Severity),
            request.Path ?? finding.ScriptName,
            line,
            column,
            endLine,
            endColumn,
            RequestId: request.RequestId,
            CorrectionMetadata: correction);
        return true;
    }

    private static ScriptDiagnosticSeverity MapSeverity(string? severity)
    {
        return severity?.Trim().ToLowerInvariant() switch
        {
            "error" or "parseerror" => ScriptDiagnosticSeverity.Error,
            "warning" => ScriptDiagnosticSeverity.Warning,
            "information" or "info" => ScriptDiagnosticSeverity.Information,
            "hint" => ScriptDiagnosticSeverity.Hint,
            _ => ScriptDiagnosticSeverity.Information
        };
    }
}
