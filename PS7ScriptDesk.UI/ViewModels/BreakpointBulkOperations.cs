using System;
using System.Collections.Generic;
using System.Linq;

namespace PS7ScriptDesk.UI.ViewModels;

/// <summary>Bulk breakpoint operations over the application's open document models.</summary>
public static class BreakpointBulkOperations
{
    public static int ClearAll(IEnumerable<EditorTabViewModel> openDocuments)
    {
        ArgumentNullException.ThrowIfNull(openDocuments);
        return openDocuments.Sum(static document => document.ClearBreakpoints());
    }

    public static int ClearDocument(IEnumerable<EditorTabViewModel> openDocuments, Guid documentId)
    {
        ArgumentNullException.ThrowIfNull(openDocuments);
        if (documentId == Guid.Empty)
        {
            return 0;
        }

        var document = openDocuments.FirstOrDefault(candidate => candidate.DiagnosticDocument.DocumentId == documentId);
        return document?.ClearBreakpoints() ?? 0;
    }
}
