using System;
using System.Collections.Generic;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// PowerShell-parser facts collected from the real PowerShell 7 AST/command metadata.
    /// These are intentionally separate from parser syntax errors so the editor can add
    /// helpful authoring diagnostics without pretending PowerShell grammar rejected the code.
    /// </summary>
    public sealed class ScriptAuthoringFacts
    {
        public static ScriptAuthoringFacts Empty { get; } = new(
            Array.Empty<FunctionDefinitionInfo>(),
            Array.Empty<CommandInvocationInfo>(),
            Array.Empty<CommandMetadataInfo>(),
            Array.Empty<VariableUsageInfo>(),
            Array.Empty<string>(),
            Array.Empty<string>());

        public ScriptAuthoringFacts(
            IReadOnlyList<FunctionDefinitionInfo>? functions,
            IReadOnlyList<CommandInvocationInfo>? commands,
            IReadOnlyList<CommandMetadataInfo>? commandMetadata,
            IReadOnlyList<VariableUsageInfo>? variables,
            IReadOnlyList<string>? availableCommandNames,
            IReadOnlyList<string>? approvedVerbs)
        {
            Functions = functions ?? Array.Empty<FunctionDefinitionInfo>();
            Commands = commands ?? Array.Empty<CommandInvocationInfo>();
            CommandMetadata = commandMetadata ?? Array.Empty<CommandMetadataInfo>();
            Variables = variables ?? Array.Empty<VariableUsageInfo>();
            AvailableCommandNames = availableCommandNames ?? Array.Empty<string>();
            ApprovedVerbs = approvedVerbs ?? Array.Empty<string>();
        }

        public IReadOnlyList<FunctionDefinitionInfo> Functions { get; }

        public IReadOnlyList<CommandInvocationInfo> Commands { get; }

        public IReadOnlyList<CommandMetadataInfo> CommandMetadata { get; }

        public IReadOnlyList<VariableUsageInfo> Variables { get; }

        public IReadOnlyList<string> AvailableCommandNames { get; }

        public IReadOnlyList<string> ApprovedVerbs { get; }

        public bool HasUsefulData => Functions.Count > 0 || Commands.Count > 0 || CommandMetadata.Count > 0 || Variables.Count > 0;
    }

    public sealed record FunctionDefinitionInfo(
        string Name,
        int StartOffset,
        int EndOffset,
        int NameStartOffset,
        int NameEndOffset);

    public sealed record CommandInvocationInfo(
        string Name,
        int StartOffset,
        int EndOffset,
        IReadOnlyList<CommandParameterUsageInfo> Parameters);

    public sealed record CommandParameterUsageInfo(
        string Name,
        string Text,
        int StartOffset,
        int EndOffset);

    public sealed record CommandMetadataInfo(
        string Name,
        bool Exists,
        string? ResolvedName,
        string? CommandType,
        string? ModuleName,
        string? Definition,
        IReadOnlyList<string> ParameterNames);

    public sealed record VariableUsageInfo(
        string Name,
        int StartOffset,
        int EndOffset,
        bool IsDefinition,
        bool IsRead,
        string? DefinitionKind);
}
