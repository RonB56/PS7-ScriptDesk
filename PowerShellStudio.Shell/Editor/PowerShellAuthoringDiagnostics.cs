using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Authoring diagnostics that complement PowerShell parser syntax errors.
    ///
    /// The parser answers "is this valid grammar?"  This service answers the editor-style
    /// question "is this probably not what the author meant?"  Prefer AST/metadata facts
    /// captured from the real PowerShell 7 session, and keep regex fallback only for cases
    /// where those facts are temporarily unavailable while the editor is still warming up.
    /// </summary>
    public static class PowerShellAuthoringDiagnostics
    {
        private static readonly Regex FunctionDefinitionRegex = new(
            @"(?im)^\s*function\s+(?<name>[A-Za-z_][\w-]*)\b",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex CommandTokenRegex = new(
            @"(?<token>[A-Za-z_][\w-]*)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> KnownKeywords = new(StringComparer.OrdinalIgnoreCase)
        {
            "begin", "break", "catch", "class", "clean", "continue", "data", "do", "dynamicparam",
            "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from",
            "function", "hidden", "if", "in", "param", "process", "return", "switch", "throw", "trap",
            "try", "until", "using", "var", "while", "workflow"
        };

        private static readonly string[] KnownCommands =
        {
            "Add-Content", "Add-Member", "Clear-Content", "Clear-Host", "Clear-Item", "Clear-Variable",
            "Compare-Object", "ConvertFrom-Csv", "ConvertFrom-Json", "ConvertTo-Csv", "ConvertTo-Json",
            "Copy-Item", "Export-Csv", "ForEach-Object", "Format-List", "Format-Table", "Get-Alias",
            "Get-ChildItem", "Get-Command", "Get-Content", "Get-Credential", "Get-Date", "Get-Help",
            "Get-History", "Get-Item", "Get-ItemProperty", "Get-Location", "Get-Member", "Get-Module",
            "Get-Process", "Get-Service", "Get-Variable", "Group-Object", "Import-Csv", "Import-Module",
            "Invoke-Command", "Invoke-Expression", "Invoke-RestMethod", "Invoke-WebRequest", "Measure-Object",
            "Move-Item", "New-Item", "New-Object", "Out-File", "Out-GridView", "Pop-Location", "Push-Location",
            "Read-Host", "Remove-Item", "Rename-Item", "Select-Object", "Set-Content", "Set-ExecutionPolicy",
            "Set-Item", "Set-ItemProperty", "Set-Location", "Set-Variable", "Sort-Object", "Start-Job",
            "Start-Process", "Stop-Process", "Tee-Object", "Test-Connection", "Test-Path", "Where-Object",
            "Write-Debug", "Write-Error", "Write-Host", "Write-Information", "Write-Output", "Write-Progress",
            "Write-Verbose", "Write-Warning"
        };

        private static readonly HashSet<string> KnownCommandSet = new(
            KnownCommands.Concat(new[]
            {
                "cat", "cd", "chdir", "cls", "copy", "cp", "curl", "del", "dir", "echo", "erase", "fc",
                "fl", "foreach", "ft", "gal", "gci", "gcm", "gdr", "ghy", "gi", "gin", "gjb", "gl",
                "gm", "gp", "gps", "group", "gsv", "gv", "h", "history", "ii", "ipal", "ipcsv", "irm",
                "iwr", "kill", "ls", "md", "measure", "mi", "move", "mv", "nal", "ni", "popd", "ps",
                "pushd", "pwd", "r", "rbp", "rd", "ren", "ri", "rm", "rmdir", "rv", "sajb", "sal",
                "select", "set", "si", "sl", "sleep", "sort", "sp", "spps", "sv", "tee", "type",
                "where", "wget"
            }),
            StringComparer.OrdinalIgnoreCase);


        private static readonly HashSet<string> AutomaticVariables = new(StringComparer.OrdinalIgnoreCase)
        {
            "?", "^", "_", "$", "args", "ConsoleFileName", "Error", "ErrorActionPreference",
            "ErrorView", "ExecutionContext", "false", "FormatEnumerationLimit", "HOME", "Host",
            "InformationPreference", "input", "IsCoreCLR", "IsLinux", "IsMacOS", "IsWindows",
            "LASTEXITCODE", "MaximumHistoryCount", "MyInvocation", "NestedPromptLevel", "null",
            "OutputEncoding", "PID", "PROFILE", "ProgressPreference", "PSBoundParameters", "PSCommandPath",
            "PSCulture", "PSDebugContext", "PSEdition", "PSHOME", "PSItem", "PSScriptRoot",
            "PSSenderInfo", "PSStyle", "PSUICulture", "PSVersionTable", "PWD", "ShellId", "StackTrace",
            "switch", "this", "true", "VerbosePreference", "WarningPreference", "WhatIfPreference"
        };

        public static IReadOnlyList<ParseErrorInfo> Analyze(string scriptText)
        {
            return Analyze(scriptText, parseResult: null);
        }

        public static IReadOnlyList<ParseErrorInfo> Analyze(string scriptText, DiagnosticsParseResult? parseResult)
        {
            if (string.IsNullOrWhiteSpace(scriptText))
            {
                return Array.Empty<ParseErrorInfo>();
            }

            var results = new List<ParseErrorInfo>();
            var facts = parseResult?.AuthoringFacts;

            if (facts is not null)
            {
                if (facts.HasUsefulData)
                {
                    AddDuplicateFunctionDiagnosticsFromFacts(facts, scriptText, results);
                    AddFunctionAuthoringDiagnosticsFromFacts(facts, results);
                    AddCommandDiagnosticsFromFacts(facts, results);
                    AddVariableDiagnosticsFromFacts(facts, results);
                }

                // A successful parser result with no authoring facts means semantic analysis
                // was intentionally skipped or the script simply had no semantic facts worth
                // reporting.  Do not fall back to regex command scanning in that case because
                // it is less accurate for modern PowerShell and can create false positives on
                // large valid scripts.
                return results;
            }

            AddDuplicateFunctionDiagnosticsFallback(scriptText, results);
            AddSuspiciousCommandDiagnosticsFallback(scriptText, results);

            return results;
        }

        private static void AddDuplicateFunctionDiagnosticsFromFacts(
            ScriptAuthoringFacts facts,
            string scriptText,
            List<ParseErrorInfo> results)
        {
            var seen = new Dictionary<string, FunctionDefinitionInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var function in facts.Functions)
            {
                if (string.IsNullOrWhiteSpace(function.Name))
                {
                    continue;
                }

                if (!seen.TryGetValue(function.Name, out var firstDefinition))
                {
                    seen[function.Name] = function;
                    continue;
                }

                var firstLine = GetLineNumber(scriptText, firstDefinition.NameStartOffset);
                results.Add(new ParseErrorInfo(
                    $"Duplicate function definition '{function.Name}'. The first definition is on line {firstLine}; this later definition will replace it at runtime.",
                    ClampOffset(function.NameStartOffset, scriptText),
                    ClampEndOffset(function.NameStartOffset, function.NameEndOffset, scriptText)));
            }
        }

        private static void AddFunctionAuthoringDiagnosticsFromFacts(ScriptAuthoringFacts facts, List<ParseErrorInfo> results)
        {
            if (facts.Functions.Count == 0)
            {
                return;
            }

            var approvedVerbs = facts.ApprovedVerbs
                .Where(verb => !string.IsNullOrWhiteSpace(verb))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var invokedFunctionNames = facts.Commands
                .Select(command => command.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var hasExplicitModuleExport = facts.Commands.Any(command =>
                string.Equals(command.Name, "Export-ModuleMember", StringComparison.OrdinalIgnoreCase));

            foreach (var function in facts.Functions)
            {
                if (string.IsNullOrWhiteSpace(function.Name))
                {
                    continue;
                }

                AddUnapprovedVerbDiagnostic(function, approvedVerbs, results);

                if (!hasExplicitModuleExport &&
                    !IsSpecialFunctionName(function.Name) &&
                    !invokedFunctionNames.Contains(function.Name))
                {
                    results.Add(new ParseErrorInfo(
                        $"Possible unused function '{function.Name}'. It is defined in this script but no direct call was found.",
                        function.NameStartOffset,
                        Math.Max(function.NameStartOffset + 1, function.NameEndOffset)));
                }
            }
        }

        private static void AddUnapprovedVerbDiagnostic(
            FunctionDefinitionInfo function,
            HashSet<string> approvedVerbs,
            List<ParseErrorInfo> results)
        {
            if (approvedVerbs.Count == 0)
            {
                return;
            }

            var dashIndex = function.Name.IndexOf("-", StringComparison.Ordinal);
            if (dashIndex <= 0)
            {
                return;
            }

            var verb = function.Name.Substring(0, dashIndex);
            if (approvedVerbs.Contains(verb))
            {
                return;
            }

            var suggestion = FindClosestValue(verb, approvedVerbs, maxDistanceOverride: verb.Length <= 4 ? 1 : 2);
            var message = suggestion is null
                ? $"Function '{function.Name}' uses the unapproved PowerShell verb '{verb}'. Prefer an approved verb from Get-Verb."
                : $"Function '{function.Name}' uses the unapproved PowerShell verb '{verb}'. Did you mean '{suggestion}'?";

            results.Add(new ParseErrorInfo(
                message,
                function.NameStartOffset,
                Math.Max(function.NameStartOffset + 1, function.NameStartOffset + verb.Length)));
        }

        private static void AddCommandDiagnosticsFromFacts(ScriptAuthoringFacts facts, List<ParseErrorInfo> results)
        {
            if (facts.CommandMetadata.Count == 0)
            {
                // Command existence and parameter checks are only reliable when the
                // PowerShell-backed diagnostics pass returned metadata.  If metadata was
                // skipped for a large script, avoid falling back to guesses that can mark
                // valid modern PowerShell as suspicious.
                return;
            }

            var metadataByName = facts.CommandMetadata
                .GroupBy(metadata => metadata.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

            var scriptFunctions = facts.Functions
                .Select(function => function.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var command in facts.Commands)
            {
                if (string.IsNullOrWhiteSpace(command.Name) || scriptFunctions.Contains(command.Name))
                {
                    continue;
                }

                if (!metadataByName.TryGetValue(command.Name, out var metadata) || !metadata.Exists)
                {
                    var suggestion = FindClosestCommand(command.Name, facts.AvailableCommandNames);
                    if (suggestion is not null)
                    {
                        results.Add(new ParseErrorInfo(
                            $"Possible unknown command '{command.Name}'. Did you mean '{suggestion}'?",
                            command.StartOffset,
                            Math.Max(command.StartOffset + 1, command.EndOffset)));
                    }

                    continue;
                }

                // Alias resolution is editor guidance, not a syntax failure. Without a
                // separate warning severity channel, surfacing alias use here would turn
                // valid commands like 'cd C:\' into red error squiggles.
                AddUnknownParameterDiagnostics(command, metadata, results);
            }
        }

        private static void AddUnknownParameterDiagnostics(
            CommandInvocationInfo command,
            CommandMetadataInfo metadata,
            List<ParseErrorInfo> results)
        {
            if (command.Parameters.Count == 0 || metadata.ParameterNames.Count == 0)
            {
                return;
            }

            var validParameters = metadata.ParameterNames
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var parameter in command.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name) ||
                    parameter.Name.Equals("%", StringComparison.Ordinal) ||
                    validParameters.Contains(parameter.Name))
                {
                    continue;
                }

                if (LooksLikeInProgressParameterPrefix(parameter.Name, validParameters))
                {
                    continue;
                }

                var suggestion = FindClosestValue(parameter.Name, validParameters, maxDistanceOverride: parameter.Name.Length <= 4 ? 1 : 2);
                if (suggestion is null)
                {
                    // Dynamic parameters can be provider/session dependent. Avoid noisy false
                    // positives unless PowerShell metadata gives us a very close correction.
                    continue;
                }

                var resolvedName = string.IsNullOrWhiteSpace(metadata.ResolvedName) ? command.Name : metadata.ResolvedName;
                results.Add(new ParseErrorInfo(
                    $"Possible unknown parameter '-{parameter.Name}' for {resolvedName}. Did you mean '-{suggestion}'?",
                    parameter.StartOffset,
                    Math.Max(parameter.StartOffset + 1, parameter.EndOffset)));
            }
        }

        private static bool LooksLikeInProgressParameterPrefix(string parameterName, HashSet<string> validParameters)
        {
            if (string.IsNullOrWhiteSpace(parameterName) || validParameters.Count == 0)
            {
                return false;
            }

            foreach (var validParameter in validParameters)
            {
                if (string.IsNullOrWhiteSpace(validParameter) ||
                    string.Equals(validParameter, parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (validParameter.StartsWith(parameterName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void AddVariableDiagnosticsFromFacts(ScriptAuthoringFacts facts, List<ParseErrorInfo> results)
        {
            if (facts.Variables.Count == 0)
            {
                return;
            }

            var definitionsByName = facts.Variables
                .Where(variable => variable.IsDefinition && IsUserAuthoredVariable(variable.Name))
                .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(), StringComparer.OrdinalIgnoreCase);

            var readCountsByName = facts.Variables
                .Where(variable => variable.IsRead && IsUserAuthoredVariable(variable.Name))
                .GroupBy(variable => variable.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

            foreach (var read in facts.Variables.Where(variable => variable.IsRead && IsUserAuthoredVariable(variable.Name)))
            {
                if (definitionsByName.ContainsKey(read.Name))
                {
                    continue;
                }

                // Be deliberately conservative.  PowerShell permits many late-bound variables
                // from dot-sourced files, profiles, and imported modules.  Only flag normal
                // user-looking variable names and present it as a warning, not a parser error.
                if (!LooksLikeLocalUserVariable(read.Name))
                {
                    continue;
                }

                results.Add(new ParseErrorInfo(
                    $"Possible undefined variable '${read.Name}'. This variable is read here but is not assigned or declared in this script.",
                    read.StartOffset,
                    Math.Max(read.StartOffset + 1, read.EndOffset)));
            }

            foreach (var pair in definitionsByName)
            {
                var variableName = pair.Key;
                if (readCountsByName.ContainsKey(variableName))
                {
                    continue;
                }

                if (!LooksLikeLocalUserVariable(variableName))
                {
                    continue;
                }

                foreach (var definition in pair.Value)
                {
                    var definitionKind = string.IsNullOrWhiteSpace(definition.DefinitionKind)
                        ? "variable"
                        : definition.DefinitionKind;

                    var noun = string.Equals(definitionKind, "Parameter", StringComparison.OrdinalIgnoreCase)
                        ? "parameter"
                        : "variable";

                    results.Add(new ParseErrorInfo(
                        $"Possible unused {noun} '${variableName}'. It is declared or assigned but is not read later in this script.",
                        definition.StartOffset,
                        Math.Max(definition.StartOffset + 1, definition.EndOffset)));
                }
            }
        }

        private static void AddDuplicateFunctionDiagnosticsFallback(string scriptText, List<ParseErrorInfo> results)
        {
            var matches = FunctionDefinitionRegex.Matches(scriptText);
            var seen = new Dictionary<string, Match>(StringComparer.OrdinalIgnoreCase);

            foreach (Match match in matches)
            {
                var nameGroup = match.Groups["name"];
                var name = nameGroup.Value;

                if (!seen.TryGetValue(name, out var firstMatch))
                {
                    seen[name] = match;
                    continue;
                }

                var firstLine = GetLineNumber(scriptText, firstMatch.Groups["name"].Index);
                results.Add(new ParseErrorInfo(
                    $"Duplicate function definition '{name}'. The first definition is on line {firstLine}; this later definition will replace it at runtime.",
                    nameGroup.Index,
                    nameGroup.Index + Math.Max(1, nameGroup.Length)));
            }
        }

        private static void AddSuspiciousCommandDiagnosticsFallback(string scriptText, List<ParseErrorInfo> results)
        {
            var documentFunctions = FunctionDefinitionRegex.Matches(scriptText)
                .Cast<Match>()
                .Select(match => match.Groups["name"].Value)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var command in EnumerateCommandPositions(scriptText))
            {
                if (KnownKeywords.Contains(command.Token) ||
                    KnownCommandSet.Contains(command.Token) ||
                    documentFunctions.Contains(command.Token))
                {
                    continue;
                }

                if (!LooksLikeCommandName(command.Token))
                {
                    continue;
                }

                var suggestion = FindClosestCommand(command.Token, KnownCommands);
                if (suggestion is null)
                {
                    continue;
                }

                results.Add(new ParseErrorInfo(
                    $"Possible unknown command '{command.Token}'. Did you mean '{suggestion}'?",
                    command.Offset,
                    command.Offset + Math.Max(1, command.Token.Length)));
            }
        }

        private static IEnumerable<CommandToken> EnumerateCommandPositions(string text)
        {
            var lineStart = 0;
            while (lineStart <= text.Length)
            {
                var lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = text.Length;
                }

                var line = text.Substring(lineStart, lineEnd - lineStart);
                foreach (var token in EnumerateCommandPositionsForLine(line, lineStart))
                {
                    yield return token;
                }

                if (lineEnd >= text.Length)
                {
                    break;
                }

                lineStart = lineEnd + 1;
            }
        }

        private static IEnumerable<CommandToken> EnumerateCommandPositionsForLine(string line, int absoluteLineStart)
        {
            var segmentStart = 0;
            var inSingleQuote = false;
            var inDoubleQuote = false;

            for (var index = 0; index <= line.Length; index++)
            {
                var atEnd = index == line.Length;
                var ch = atEnd ? '\0' : line[index];

                if (!atEnd)
                {
                    if (ch == '`')
                    {
                        index++;
                        continue;
                    }

                    if (!inDoubleQuote && ch == '\'')
                    {
                        inSingleQuote = !inSingleQuote;
                        continue;
                    }

                    if (!inSingleQuote && ch == '"')
                    {
                        inDoubleQuote = !inDoubleQuote;
                        continue;
                    }

                    if (!inSingleQuote && !inDoubleQuote && ch == '#')
                    {
                        var segmentLengthBeforeComment = index - segmentStart;
                        if (segmentLengthBeforeComment > 0)
                        {
                            var tokenBeforeComment = TryGetCommandTokenFromSegment(line, absoluteLineStart, segmentStart, segmentLengthBeforeComment);
                            if (tokenBeforeComment is not null)
                            {
                                yield return tokenBeforeComment;
                            }
                        }

                        yield break;
                    }
                }

                if (atEnd || (!inSingleQuote && !inDoubleQuote && (ch == '|' || ch == ';' || ch == '{' || ch == '}')))
                {
                    var segmentLength = index - segmentStart;
                    if (segmentLength > 0)
                    {
                        var token = TryGetCommandTokenFromSegment(line, absoluteLineStart, segmentStart, segmentLength);
                        if (token is not null)
                        {
                            yield return token;
                        }
                    }

                    segmentStart = index + 1;
                }
            }
        }

        private static CommandToken? TryGetCommandTokenFromSegment(string line, int absoluteLineStart, int segmentStart, int segmentLength)
        {
            var segment = line.Substring(segmentStart, segmentLength);
            var leadingWhitespace = segment.Length - segment.TrimStart().Length;
            var firstNonWhitespaceOffset = segmentStart + leadingWhitespace;
            var trimmed = segment.Substring(leadingWhitespace);

            if (trimmed.Length == 0 ||
                trimmed.StartsWith("#", StringComparison.Ordinal) ||
                trimmed.StartsWith("$", StringComparison.Ordinal) ||
                trimmed.StartsWith("-", StringComparison.Ordinal) ||
                trimmed.StartsWith(")", StringComparison.Ordinal) ||
                trimmed.StartsWith("]", StringComparison.Ordinal))
            {
                return null;
            }

            if (trimmed.StartsWith("&", StringComparison.Ordinal) ||
                trimmed.StartsWith(".", StringComparison.Ordinal))
            {
                return null;
            }

            var match = CommandTokenRegex.Match(trimmed);
            if (!match.Success || match.Index != 0)
            {
                return null;
            }

            var token = match.Groups["token"].Value;
            if (string.Equals(token, "function", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(token, "param", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var afterToken = trimmed.Substring(match.Length).TrimStart();
            if (afterToken.StartsWith("=", StringComparison.Ordinal))
            {
                return null;
            }

            return new CommandToken(token, absoluteLineStart + firstNonWhitespaceOffset + match.Index);
        }

        private static bool IsSpecialFunctionName(string name)
        {
            return string.Equals(name, "prompt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "TabExpansion", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(name, "TabExpansion2", StringComparison.OrdinalIgnoreCase) ||
                   name.StartsWith("On", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserAuthoredVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || AutomaticVariables.Contains(name))
            {
                return false;
            }

            // Ignore PowerShell preference variables; users often set them globally or rely on
            // profile/session state, and flagging them as unused/undefined is noisy.
            if (name.EndsWith("Preference", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static bool LooksLikeLocalUserVariable(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2 || name.StartsWith("_", StringComparison.Ordinal))
            {
                return false;
            }

            return name.All(ch => char.IsLetterOrDigit(ch) || ch == '_');
        }

        private static bool LooksLikeCommandName(string token)
        {
            return token.Length >= 4 &&
                   token.Any(char.IsLetter) &&
                   (token.Contains('-', StringComparison.Ordinal) ||
                    token.StartsWith("Get", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("Set", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("New", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("Remove", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("Invoke", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("Start", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("Stop", StringComparison.OrdinalIgnoreCase) ||
                    token.StartsWith("et", StringComparison.OrdinalIgnoreCase));
        }

        private static string? FindClosestCommand(string token, IEnumerable<string> candidates)
        {
            var candidateList = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (candidateList.Count == 0)
            {
                candidateList = KnownCommands.ToList();
            }

            return FindClosestValue(token, candidateList, maxDistanceOverride: null);
        }

        private static string? FindClosestValue(string token, IEnumerable<string> candidates, int? maxDistanceOverride)
        {
            string? best = null;
            var bestDistance = int.MaxValue;

            foreach (var candidate in candidates)
            {
                var lengthDelta = Math.Abs(candidate.Length - token.Length);
                if (lengthDelta > 5)
                {
                    continue;
                }

                var distance = LevenshteinDistance(token, candidate);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            if (best is null)
            {
                return null;
            }

            var maxDistance = maxDistanceOverride ?? (token.Length <= 8 ? 2 : 3);
            return bestDistance <= maxDistance ? best : null;
        }

        private static int LevenshteinDistance(string left, string right)
        {
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }

            left = left.ToUpperInvariant();
            right = right.ToUpperInvariant();

            var previous = new int[right.Length + 1];
            var current = new int[right.Length + 1];

            for (var column = 0; column <= right.Length; column++)
            {
                previous[column] = column;
            }

            for (var row = 1; row <= left.Length; row++)
            {
                current[0] = row;
                for (var column = 1; column <= right.Length; column++)
                {
                    var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                    current[column] = Math.Min(
                        Math.Min(current[column - 1] + 1, previous[column] + 1),
                        previous[column - 1] + cost);
                }

                (previous, current) = (current, previous);
            }

            return previous[right.Length];
        }

        private static int GetLineNumber(string text, int offset)
        {
            var line = 1;
            var safeOffset = Math.Clamp(offset, 0, text.Length);
            for (var index = 0; index < safeOffset; index++)
            {
                if (text[index] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        private static int ClampOffset(int offset, string text)
        {
            return Math.Clamp(offset, 0, Math.Max(0, text.Length));
        }

        private static int ClampEndOffset(int startOffset, int endOffset, string text)
        {
            var start = ClampOffset(startOffset, text);
            var end = Math.Clamp(endOffset, start + 1, Math.Max(start + 1, text.Length));
            return Math.Min(end, text.Length);
        }

        private sealed record CommandToken(string Token, int Offset);
    }
}
