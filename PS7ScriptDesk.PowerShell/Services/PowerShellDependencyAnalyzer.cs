using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>Evidence-based portability scan. It intentionally reports uncertainty instead of claiming a complete static analysis.</summary>
public sealed class PowerShellDependencyAnalyzer : IExeExportDependencyAnalyzer
{
    private static readonly Regex ModulePattern = new("""(?im)^\s*(?:Import-Module|using\s+module)\s+["']?(?<value>[^\s'";]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex AssemblyPattern = new("""(?im)^\s*(?:using\s+assembly|Add-Type(?:\s+-Path)?)\s+["']?(?<value>[^\s'";]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex ScriptRelativePattern = new("""(?i)\$PSScriptRoot|(?:["'])(?<value>\.\\[^'"]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex AbsolutePathPattern = new("""(?i)(?:["'])(?<value>[A-Z]:\\[^'"]+|\\\\[^'"]+)""", RegexOptions.CultureInvariant);
    private static readonly Regex ExecutablePattern = new("""(?i)(?:Start-Process\s+|&\s*["']?)(?<value>[^\s'"]+\.(?:exe|cmd|bat))""", RegexOptions.CultureInvariant);

    public IReadOnlyList<ExeExportDependency> Analyze(string scriptContent)
    {
        var dependencies = new List<ExeExportDependency>();
        if (string.IsNullOrWhiteSpace(scriptContent))
            return dependencies;

        AddMatches(dependencies, ModulePattern, scriptContent, ExeExportDependencyKind.Module, ExeExportDependencyClassification.ExternalDependency,
            "Imported modules are not automatically bundled. Confirm the module is available on the destination computer.");
        AddMatches(dependencies, AssemblyPattern, scriptContent, ExeExportDependencyKind.Assembly, ExeExportDependencyClassification.ExternalDependency,
            "Referenced assemblies are not automatically bundled. Review this dependency before portable deployment.");
        AddMatches(dependencies, ScriptRelativePattern, scriptContent, ExeExportDependencyKind.ScriptRelativePath, ExeExportDependencyClassification.PotentialPortabilityProblem,
            "Script-relative paths run from the embedded script workspace. Include or provide the referenced resource separately.");
        AddMatches(dependencies, AbsolutePathPattern, scriptContent, ExeExportDependencyKind.File, ExeExportDependencyClassification.PotentialPortabilityProblem,
            "An absolute or network path may not exist on the destination computer.");
        AddMatches(dependencies, ExecutablePattern, scriptContent, ExeExportDependencyKind.Executable, ExeExportDependencyClassification.ExternalDependency,
            "An external executable is invoked and is not automatically bundled.");

        if (Regex.IsMatch(scriptContent, @"(?i)(?:Get-Content|Set-Content|Import-Csv|Export-Csv|Import-Clixml|Export-Clixml)\s+\$", RegexOptions.CultureInvariant))
            dependencies.Add(new ExeExportDependency(ExeExportDependencyKind.Unknown, ExeExportDependencyClassification.CannotDetermine,
                "Dynamic file path", "A file path is computed dynamically and requires manual portability review."));
        if (Regex.IsMatch(scriptContent, @"(?i)\b(?:pwsh|powershell\.exe)\b", RegexOptions.CultureInvariant))
            dependencies.Add(new ExeExportDependency(ExeExportDependencyKind.Executable, ExeExportDependencyClassification.SystemDependency,
                "PowerShell executable", "The script explicitly invokes PowerShell; embedded runtime mode may still have an external PowerShell dependency."));

        return dependencies;
    }

    private static void AddMatches(
        ICollection<ExeExportDependency> dependencies,
        Regex pattern,
        string content,
        ExeExportDependencyKind kind,
        ExeExportDependencyClassification classification,
        string message)
    {
        foreach (Match match in pattern.Matches(content))
        {
            var value = match.Groups["value"].Success ? match.Groups["value"].Value : "$PSScriptRoot";
            var lineNumber = 1 + content[..match.Index].Count(static character => character == '\n');
            dependencies.Add(new ExeExportDependency(kind, classification, value, message, lineNumber));
        }
    }
}
