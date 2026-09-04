using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using Xunit;

namespace PS7ScriptDesk.Tests;

public sealed class ScriptDiagnosticContractTests
{
    [Fact]
    public void DocumentIdentity_IsUniqueAndRevisionIsMonotonic()
    {
        var first = new ScriptDocumentIdentity();
        var second = new ScriptDocumentIdentity();

        Assert.NotEqual(first.DocumentId, second.DocumentId);
        Assert.Equal(0, first.Revision);
        Assert.Equal(1, first.AdvanceRevision());
        Assert.Equal(2, first.AdvanceRevision());
        Assert.Equal(new ScriptDocumentSnapshot(first.DocumentId, 2), first.Capture());
    }

    [Fact]
    public void DiagnosticContract_PreservesSourceSeverityRangeAndOptionalFields()
    {
        var document = new ScriptDocumentIdentity();
        var diagnostic = new ScriptDiagnostic(
            document.DocumentId,
            document.Revision,
            ScriptDiagnosticSource.PSScriptAnalyzer,
            "PSAvoidUsingWriteHost",
            "Avoid Write-Host.",
            ScriptDiagnosticSeverity.Warning,
            "script.ps1",
            2,
            3,
            2,
            12,
            10,
            19,
            "request-1");

        Assert.Equal(ScriptDiagnosticSource.PSScriptAnalyzer, diagnostic.SourceId);
        Assert.Equal(ScriptDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(10, diagnostic.StartOffset);
        Assert.Equal(19, diagnostic.EndOffset);
        Assert.Equal("request-1", diagnostic.RequestId);
    }

    [Fact]
    public void Store_ReplacesOneSourceAtomicallyWithoutAffectingAnother()
    {
        var store = new ScriptDiagnosticStore();
        var document = new ScriptDocumentIdentity();
        var changed = 0;
        store.Changed += (_, _) => changed++;

        var parser = CreateDiagnostic(document, ScriptDiagnosticSource.Parser, "parser");
        var authoring = CreateDiagnostic(document, ScriptDiagnosticSource.Authoring, "authoring");
        Assert.True(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 0, new[] { parser }));
        Assert.True(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Authoring, 0, new[] { authoring }));

        var replacement = CreateDiagnostic(document, ScriptDiagnosticSource.Parser, "replacement");
        Assert.True(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 1, new[] { replacement with { DocumentRevision = 1 } }));

        Assert.Equal(new[] { "replacement", "authoring" }, store.GetDiagnostics(document.DocumentId).Select(diagnostic => diagnostic.Message));
        Assert.Equal(3, changed);
    }

    [Fact]
    public void Store_RejectsStaleReplacementAndSupportsSourceAndDocumentClearing()
    {
        var store = new ScriptDiagnosticStore();
        var document = new ScriptDocumentIdentity();
        var current = CreateDiagnostic(document, ScriptDiagnosticSource.Parser, "current") with { DocumentRevision = 2 };
        var stale = CreateDiagnostic(document, ScriptDiagnosticSource.Parser, "stale") with { DocumentRevision = 1 };

        Assert.True(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 2, new[] { current }));
        Assert.False(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 1, new[] { stale }));
        Assert.Equal("current", Assert.Single(store.GetDiagnostics(document.DocumentId)).Message);
        Assert.True(store.ClearDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 3));
        Assert.Empty(store.GetDiagnostics(document.DocumentId));
        Assert.False(store.ReplaceDiagnostics(document.DocumentId, ScriptDiagnosticSource.Parser, 2, new[] { current }));
    }

    [Fact]
    public void DocumentReplacementUsesDifferentIdentitySoOldDiagnosticsCannotAttach()
    {
        var first = new ScriptDocumentIdentity();
        var reopened = new ScriptDocumentIdentity();
        var store = new ScriptDiagnosticStore();

        Assert.NotEqual(first.DocumentId, reopened.DocumentId);
        Assert.True(store.ReplaceDiagnostics(first.DocumentId, ScriptDiagnosticSource.Parser, 0, new[] { CreateDiagnostic(first, ScriptDiagnosticSource.Parser, "old") }));
        Assert.Empty(store.GetDiagnostics(reopened.DocumentId));
    }

    [Fact]
    public void CompatibilityParserDiagnosticCanMapToSharedContractWithoutChangingExistingModel()
    {
        var document = new ScriptDocumentIdentity();
        var shared = new ScriptDiagnostic(document.DocumentId, document.Revision, ScriptDiagnosticSource.Parser, null, "Syntax error", ScriptDiagnosticSeverity.Error, null, 1, 1, 1, 2, 0, 1);

        Assert.Equal(ScriptDiagnosticSource.Parser, shared.SourceId);
        Assert.Equal(ScriptDiagnosticSeverity.Error, shared.Severity);
        Assert.Equal(0, shared.StartOffset);
        Assert.Equal(1, shared.EndOffset);
    }

    private static ScriptDiagnostic CreateDiagnostic(ScriptDocumentIdentity document, ScriptDiagnosticSource source, string message)
        => new(document.DocumentId, 0, source, null, message, ScriptDiagnosticSeverity.Warning, null, 1, 1, 1, 2);
}
