using System;

namespace PowerShellStudio.Domain.Models
{
    public class ExeExportRequest
    {
        public ExeExportRequest(
            string sourceScriptPath,
            string scriptContent,
            string outputExecutablePath,
            PowerShellRuntimeInfo runtimeInfo)
        {
            SourceScriptPath = string.IsNullOrWhiteSpace(sourceScriptPath)
                ? throw new ArgumentException("A saved source script path is required.", nameof(sourceScriptPath))
                : sourceScriptPath;

            ScriptContent = scriptContent ?? throw new ArgumentNullException(nameof(scriptContent));
            OutputExecutablePath = string.IsNullOrWhiteSpace(outputExecutablePath)
                ? throw new ArgumentException("An output executable path is required.", nameof(outputExecutablePath))
                : outputExecutablePath;
            RuntimeInfo = runtimeInfo ?? throw new ArgumentNullException(nameof(runtimeInfo));
        }

        public string SourceScriptPath { get; }

        public string ScriptContent { get; }

        public string OutputExecutablePath { get; }

        public PowerShellRuntimeInfo RuntimeInfo { get; }
    }
}
