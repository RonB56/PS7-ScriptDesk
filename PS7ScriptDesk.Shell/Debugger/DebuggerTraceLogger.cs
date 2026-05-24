using System;
using System.Collections.Generic;
using PS7ScriptDesk.Application.Diagnostics;

namespace PS7ScriptDesk.Shell.Debug
{
    internal static class DebuggerTraceLogger
    {
        public static string CurrentPath => DeveloperDiagnostics.CurrentSessionDirectory ?? string.Empty;

        public static void Write(string source, string message)
        {
            try
            {
                var safeSource = string.IsNullOrWhiteSpace(source) ? "Unknown" : source.Trim();
                var safeMessage = string.IsNullOrWhiteSpace(message) ? string.Empty : message.Replace(Environment.NewLine, " | ", StringComparison.Ordinal).Trim();
                if (!DeveloperDiagnostics.IsEnabled || !DeveloperDiagnostics.IsVerboseDebuggerEnabled())
                {
                    return;
                }

                DeveloperDiagnostics.LogDebug(
                    "Debugger",
                    safeMessage,
                    new Dictionary<string, object?>
                    {
                        ["traceSource"] = safeSource,
                        ["legacyTraceLength"] = safeMessage.Length
                    });
            }
            catch
            {
                // Best effort diagnostics only.
            }
        }
    }
}
