using System;
using System.Collections.Generic;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Application.Diagnostics;

/// <summary>
/// Opt-in sink for Phase 1 controller correlation. It records IDs and state only;
/// it never records script, command, or terminal content.
/// </summary>
public static class TerminalControllerDiagnostics
{
    public static Action<TerminalControllerDiagnostic> CreateSink()
        => diagnostic => DeveloperDiagnostics.LogTrace(
            "TerminalController",
            diagnostic.Event,
            new Dictionary<string, object?>
            {
                ["operationId"] = diagnostic.Identity.OperationId,
                ["sessionGeneration"] = diagnostic.Identity.SessionGeneration,
                ["rendererGeneration"] = diagnostic.Identity.RendererGeneration,
                ["resizeGeneration"] = diagnostic.Identity.ResizeGeneration,
                ["outputSequence"] = diagnostic.Identity.OutputSequence,
                ["state"] = diagnostic.State.ToString(),
                ["detail"] = diagnostic.Detail
            });
}

