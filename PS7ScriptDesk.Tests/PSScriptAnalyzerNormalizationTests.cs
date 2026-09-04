using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.PowerShell.Services;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class PSScriptAnalyzerNormalizationTests
{
    [Fact]
    public void Normalize_PreservesMetadataMapsSeverityAndClampsRange()
    {
        var request = Request(7, "a.ps1");
        var result = new PSScriptAnalyzerResult("r1", new[]
        {
            new PSScriptAnalyzerFinding("PSRule", "message", "ParseError", 99, 99, 99, 99, "worker.ps1", "fix")
        });

        var normalized = PSScriptAnalyzerResultNormalizer.Normalize(request, result);
        var diagnostic = Assert.Single(normalized.Diagnostics);
        Assert.Equal(ScriptDiagnosticSource.PSScriptAnalyzer, diagnostic.SourceId);
        Assert.Equal(ScriptDiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("PSRule", diagnostic.RuleId);
        Assert.Equal("a.ps1", diagnostic.Path);
        Assert.Equal("r1", diagnostic.RequestId);
        Assert.Equal(2, diagnostic.StartLine);
        Assert.Equal(4, diagnostic.StartColumn);
        Assert.Equal("fix", diagnostic.CorrectionMetadata!["correction"]);
    }

    [Fact]
    public void Normalize_RejectsMalformedFindingButKeepsValidSibling()
    {
        var request = Request(1, null);
        var result = new PSScriptAnalyzerResult("r1", new[]
        {
            new PSScriptAnalyzerFinding("bad", "", "Warning", 1, 1),
            new PSScriptAnalyzerFinding("good", "kept", "Information", 1, 1, 1, 2)
        });

        var normalized = PSScriptAnalyzerResultNormalizer.Normalize(request, result);
        Assert.Equal(1, normalized.RejectedFindingCount);
        Assert.Equal("good", Assert.Single(normalized.Diagnostics).RuleId);
    }

    [Theory]
    [InlineData("Error", ScriptDiagnosticSeverity.Error)]
    [InlineData("Warning", ScriptDiagnosticSeverity.Warning)]
    [InlineData("Information", ScriptDiagnosticSeverity.Information)]
    [InlineData("Hint", ScriptDiagnosticSeverity.Hint)]
    [InlineData("Unknown", ScriptDiagnosticSeverity.Information)]
    public void Normalize_MapsSeverityWithDeliberateUnknownFallback(string severity, ScriptDiagnosticSeverity expected)
    {
        var diagnostic = Assert.Single(PSScriptAnalyzerResultNormalizer.Normalize(Request(0, null), new PSScriptAnalyzerResult("r1", new[]
        {
            new PSScriptAnalyzerFinding("rule", "message", severity, 1, 1)
        })).Diagnostics);
        Assert.Equal(expected, diagnostic.Severity);
    }

    [Fact]
    public void Normalize_RejectsWrongRequestAndPreservesDocumentRevision()
    {
        var request = Request(4, null);
        Assert.Empty(PSScriptAnalyzerResultNormalizer.Normalize(request, new PSScriptAnalyzerResult("other", Array.Empty<PSScriptAnalyzerFinding>())).Diagnostics);
        var diagnostic = Assert.Single(PSScriptAnalyzerResultNormalizer.Normalize(request, new PSScriptAnalyzerResult("r1", new[]
        {
            new PSScriptAnalyzerFinding("rule", "message", "Warning", 1, 1)
        })).Diagnostics);
        Assert.Equal(request.Revision, diagnostic.DocumentRevision);
        Assert.Equal(Guid.Parse(request.DocumentId), diagnostic.DocumentId);
    }

    private static PSScriptAnalyzerRequest Request(long revision, string? path)
        => new("r1", Guid.Parse("11111111-1111-1111-1111-111111111111").ToString(), revision, path, "x\nabc");
}
