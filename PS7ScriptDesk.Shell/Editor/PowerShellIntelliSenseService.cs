using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Orchestrates IntelliSense completions for the editor.
    ///
    /// This service blends PowerShell-engine results with local editor knowledge. The engine
    /// remains the authoritative source for cmdlets, parameters, members, provider paths, and
    /// dynamic values. Local candidates make the popup feel immediate and ISE-like even before
    /// the engine responds, and ensure document variables/functions/snippets are always visible
    /// instead of only appearing when the engine returns nothing.
    /// </summary>
    public sealed class PowerShellIntelliSenseService : IDisposable
    {
        private readonly PowerShellCompletionService _completionService = new();
        private readonly PowerShellSnippetProvider _snippetProvider = new();

        public event EventHandler<EditorMetadataWarmupStatusChangedEventArgs>? MetadataWarmupStatusChanged
        {
            add => _completionService.MetadataWarmupStatusChanged += value;
            remove => _completionService.MetadataWarmupStatusChanged -= value;
        }

        private static readonly string[] Keywords =
        {
            "begin", "break", "catch", "class", "clean", "continue", "data", "do", "dynamicparam",
            "else", "elseif", "end", "enum", "exit", "filter", "finally", "for", "foreach", "from",
            "function", "hidden", "if", "in", "param", "process", "return", "switch", "throw", "trap",
            "try", "until", "using", "var", "while", "workflow"
        };

        private static readonly string[] AutomaticVariables =
        {
            "$_", "$?", "$$", "$^", "$args", "$ConfirmPreference", "$DebugPreference", "$Error",
            "$ErrorActionPreference", "$ErrorView", "$ExecutionContext", "$false", "$FormatEnumerationLimit",
            "$HOME", "$Host", "$InformationPreference", "$input", "$IsCoreCLR", "$IsLinux", "$IsMacOS",
            "$IsWindows", "$LASTEXITCODE", "$Matches", "$MaximumHistoryCount", "$MyInvocation", "$NestedPromptLevel",
            "$null", "$OutputEncoding", "$PID", "$PROFILE", "$ProgressPreference", "$PSBoundParameters",
            "$PSCommandPath", "$PSCulture", "$PSDefaultParameterValues", "$PSEdition", "$PSEmailServer",
            "$PSHOME", "$PSScriptRoot", "$PSSenderInfo", "$PSStyle", "$PSUICulture", "$PSVersionTable",
            "$PWD", "$ShellId", "$StackTrace", "$true", "$VerbosePreference", "$WarningPreference",
            "$WhatIfPreference"
        };

        private static readonly string[] CommonCommands =
        {
            "Add-Content", "Clear-Content", "Clear-Host", "Compare-Object", "ConvertFrom-Csv", "ConvertFrom-Json",
            "ConvertTo-Csv", "ConvertTo-Json", "Copy-Item", "ForEach-Object", "Format-List", "Format-Table",
            "Get-Alias", "Get-ChildItem", "Get-Command", "Get-Content", "Get-Credential", "Get-Date",
            "Get-Help", "Get-History", "Get-Item", "Get-ItemProperty", "Get-Location", "Get-Member",
            "Get-Module", "Get-Process", "Get-Service", "Get-Variable", "Group-Object", "Import-Csv",
            "Import-Module", "Invoke-Command", "Invoke-RestMethod", "Invoke-WebRequest", "Measure-Object",
            "Move-Item", "New-Item", "New-Object", "Out-File", "Out-GridView", "Remove-Item",
            "Rename-Item", "Select-Object", "Set-Content", "Set-ExecutionPolicy", "Set-ItemProperty",
            "Set-Location", "Sort-Object", "Start-Job", "Start-Process", "Stop-Process", "Tee-Object",
            "Test-Connection", "Test-Path", "Where-Object", "Write-Debug", "Write-Error", "Write-Host",
            "Write-Information", "Write-Output", "Write-Progress", "Write-Verbose", "Write-Warning"
        };

        private static readonly string[] CommonAliases =
        {
            "cat", "cd", "chdir", "cls", "copy", "cp", "curl", "del", "dir", "echo", "erase", "fc",
            "fl", "foreach", "ft", "gal", "gci", "gcm", "gdr", "ghy", "gi", "gin", "gjb", "gl",
            "gm", "gp", "gps", "group", "gsv", "gv", "h", "history", "ii", "ipal", "ipcsv", "irm",
            "iwr", "kill", "ls", "md", "measure", "mi", "move", "mv", "nal", "ni", "popd", "ps",
            "pushd", "pwd", "r", "rbp", "rd", "ren", "ri", "rm", "rmdir", "rv", "sajb", "sal", "select",
            "set", "si", "sl", "sleep", "sort", "sp", "spps", "sv", "tee", "type", "where", "wget"
        };

        private static readonly Regex VariableRegex = new(@"\$[A-Za-z_][\w]*", RegexOptions.Compiled);
        private static readonly Regex FunctionRegex = new(@"\bfunction\s+([A-Za-z_][\w-]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex ParamNameRegex = new(@"\bparam\s*\((?<body>.*?)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex ParamVariableRegex = new(@"\$[A-Za-z_][\w]*", RegexOptions.Compiled);

        private static readonly Dictionary<string, string> AutomaticVariableDescriptions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["$_"] = "Current pipeline object.",
            ["$args"] = "Array of undeclared parameters and values passed to a function, script, or script block.",
            ["$Error"] = "Collection of recent error objects in the current session.",
            ["$ErrorActionPreference"] = "Controls how PowerShell responds to non-terminating errors.",
            ["$ExecutionContext"] = "PowerShell engine execution context.",
            ["$false"] = "Boolean false.",
            ["$HOME"] = "Full path of the user's home directory.",
            ["$Host"] = "Information and services exposed by the current host application.",
            ["$input"] = "Enumerator for input passed to a function or script block.",
            ["$LASTEXITCODE"] = "Exit code of the last native program or script.",
            ["$Matches"] = "Hash table populated by the -match operator.",
            ["$MyInvocation"] = "Information about the current command, script, or function invocation.",
            ["$null"] = "Null value.",
            ["$PID"] = "Process ID of the current PowerShell host.",
            ["$PROFILE"] = "Path to the current user's PowerShell profile script.",
            ["$PSBoundParameters"] = "Dictionary of parameters explicitly bound to a script or function.",
            ["$PSCommandPath"] = "Full path and file name of the running script.",
            ["$PSScriptRoot"] = "Folder that contains the running script.",
            ["$PSStyle"] = "ANSI formatting and output-rendering preferences in PowerShell 7.",
            ["$PSVersionTable"] = "Table of PowerShell version and platform information.",
            ["$PWD"] = "Current location object.",
            ["$true"] = "Boolean true."
        };

        public async Task<CompletionWindow?> ShowCompletionAsync(
            TextEditor editor,
            string? pwshExecutablePath,
            bool includeEngine = true,
            int engineWaitMilliseconds = 180,
            bool forceCompletion = false,
            CancellationToken cancellationToken = default)
        {
            if (editor is null) return null;

            var caretOffset = editor.CaretOffset;
            var documentText = editor.Text ?? string.Empty;
            var context = GetCompletionContext(documentText, caretOffset);

            var parameterContext = TryGetParameterCompletionContext(documentText, context);
            var parameterValueContext = TryGetParameterValueCompletionContext(documentText, caretOffset, context);
            var staticMemberContext = TryGetStaticMemberCompletionContext(documentText, context);
            var memberContext = staticMemberContext is null ? TryGetMemberCompletionContext(documentText, context) : null;
            var commandSpecificContextName = parameterContext?.CommandName ?? parameterValueContext?.CommandName;
            var isParameterContextRequest = parameterContext is not null || parameterValueContext is not null;
            var completionStopwatch = isParameterContextRequest ? Stopwatch.StartNew() : null;
            PowerShellQuickInfo? parameterCommandInfo = null;
            var usedCachedParameterMetadata = false;
            var fetchedParameterMetadata = false;
            var engineTimedOut = false;
            var completionSource = "none";
            var completionKind = parameterContext is not null
                ? "parameter"
                : parameterValueContext is not null
                    ? "parameter-value"
                    : memberContext is not null
                        ? "member"
                        : staticMemberContext is not null
                            ? "static-member"
                            : "command";

            var cachedCommandReferences = _completionService.GetCachedCommandReferences(pwshExecutablePath);
            PowerShellQuickInfo? cachedParameterQuickInfoForLogging = null;
            var cachedParameterMetadataAvailable = !string.IsNullOrWhiteSpace(commandSpecificContextName) &&
                                                   _completionService.TryGetCachedCommandQuickInfo(
                                                       pwshExecutablePath,
                                                       commandSpecificContextName,
                                                       out cachedParameterQuickInfoForLogging);
            var cachedParameterCount = cachedParameterQuickInfoForLogging?.Parameters.Count ?? 0;

            AppLogger.Debug(
                "EditorCompletion",
                $"Classified completion request. Kind={completionKind}, ForceCompletion={forceCompletion}, IncludeEngine={includeEngine}, Fragment='{context.Fragment}', Command='{commandSpecificContextName ?? string.Empty}', CaretOffset={caretOffset}, CachedCommandCount={cachedCommandReferences.Count}, CachedQuickInfoAvailable={cachedParameterMetadataAvailable}, CachedParameterCount={cachedParameterCount}, MetadataWarmupTriggered=False.");
            if (!string.IsNullOrWhiteSpace(commandSpecificContextName) &&
                _completionService.TryGetCachedCommandQuickInfo(
                    pwshExecutablePath,
                    commandSpecificContextName,
                    out var cachedParameterCommandInfo))
            {
                if (!RequiresParameterMetadata(parameterContext, parameterValueContext) || HasUsableParameterMetadata(cachedParameterCommandInfo))
                {
                    parameterCommandInfo = cachedParameterCommandInfo;
                    usedCachedParameterMetadata = true;
                    completionSource = "cache";
                }
            }

            if (!string.IsNullOrWhiteSpace(commandSpecificContextName) &&
                (parameterCommandInfo is null || (RequiresParameterMetadata(parameterContext, parameterValueContext) && !HasUsableParameterMetadata(parameterCommandInfo))) &&
                !string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                using var parameterCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                parameterCts.CancelAfter(TimeSpan.FromMilliseconds(
                    forceCompletion
                        ? (parameterContext is not null ? 2500 : 1200)
                        : (parameterContext is not null ? 1500 : (includeEngine ? 450 : 650))));

                try
                {
                    parameterCommandInfo = await _completionService.GetCommandQuickInfoAsync(
                            commandSpecificContextName,
                            pwshExecutablePath,
                            requireParameters: RequiresParameterMetadata(parameterContext, parameterValueContext),
                            cancellationToken: parameterCts.Token)
                        .ConfigureAwait(true);
                    fetchedParameterMetadata = parameterCommandInfo is not null;
                    if (parameterCommandInfo is not null && HasUsableParameterMetadata(parameterCommandInfo))
                    {
                        completionSource = "live";
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    parameterCommandInfo = null;
                }
                catch
                {
                    parameterCommandInfo = null;
                }
            }

            CompletionServiceResult? engineResult = null;
            var shouldQueryEngine = includeEngine &&
                                    engineWaitMilliseconds > 0 &&
                                    !string.IsNullOrWhiteSpace(pwshExecutablePath) &&
                                    (!isParameterContextRequest || !HasUsableParameterMetadata(parameterCommandInfo));
            if (shouldQueryEngine)
            {
                using var engineCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var effectiveEngineWaitMilliseconds = memberContext is not null || staticMemberContext is not null
                    ? Math.Max(engineWaitMilliseconds, 450)
                    : engineWaitMilliseconds;
                engineCts.CancelAfter(TimeSpan.FromMilliseconds(effectiveEngineWaitMilliseconds));

                try
                {
                    engineResult = await _completionService.GetCompletionsAsync(
                            documentText,
                            caretOffset,
                            pwshExecutablePath!,
                            engineCts.Token)
                        .ConfigureAwait(true);

                    if (engineResult is { HasItems: true } && completionSource == "none")
                    {
                        completionSource = "engine";
                    }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // The engine did not answer quickly enough. Return local completions now
                    // instead of making Ctrl+Space / typing feel blocked.
                    engineTimedOut = true;
                    engineResult = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Local candidates still keep IntelliSense useful if the engine is busy.
                    engineResult = null;
                }
            }

            if (cancellationToken.IsCancellationRequested) throw new OperationCanceledException(cancellationToken);
            var window = BuildWindow(editor, context, engineResult, documentText, parameterContext, parameterValueContext, memberContext, staticMemberContext, parameterCommandInfo, cachedCommandReferences, pwshExecutablePath, completionKind, forceCompletion);

            if (completionStopwatch is not null)
            {
                completionStopwatch.Stop();
                var fragment = parameterContext is not null
                    ? "-" + parameterContext.FragmentWithoutDash
                    : parameterValueContext is not null
                        ? parameterValueContext.Fragment
                        : context.Fragment;
                AppLogger.Debug(
                    "EditorCompletion",
                    $"Parameter IntelliSense for '{commandSpecificContextName}' fragment='{fragment}' completed in {completionStopwatch.ElapsedMilliseconds:N0} ms. Source={completionSource}, CachedMetadata={usedCachedParameterMetadata}, CachedParameterCount={parameterCommandInfo?.Parameters.Count ?? 0}, FetchedMetadata={fetchedParameterMetadata}, EngineTimedOut={engineTimedOut}, EngineItems={engineResult?.Items.Count ?? 0}, WindowCreated={window is not null}.");
            }

            return window;
        }

        public void StartMetadataWarmup(PowerShellRuntimeInfo? runtimeInfo)
        {
            _completionService.StartMetadataWarmup(runtimeInfo);
        }

        public void RefreshMetadata(PowerShellRuntimeInfo? runtimeInfo)
        {
            _completionService.RefreshMetadata(runtimeInfo);
        }

        public void Dispose()
        {
            _completionService.Dispose();
        }

        public async Task<EditorQuickInfo?> GetQuickInfoAsync(
            TextEditor editor,
            int offset,
            string? pwshExecutablePath,
            CancellationToken cancellationToken = default)
        {
            if (editor is null || editor.Document is null)
            {
                return null;
            }

            var documentText = editor.Text ?? string.Empty;
            if (documentText.Length == 0)
            {
                return null;
            }

            var token = GetTokenAtOffset(documentText, offset);
            if (token is null || string.IsNullOrWhiteSpace(token.Text))
            {
                return await BuildCommandContextQuickInfoAsync(
                    documentText,
                    offset,
                    pwshExecutablePath,
                    cancellationToken).ConfigureAwait(true);
            }

            var tokenText = token.Text;

            if (tokenText.StartsWith("$", StringComparison.Ordinal))
            {
                return BuildVariableQuickInfo(tokenText, documentText);
            }

            if (tokenText.StartsWith("-", StringComparison.Ordinal))
            {
                return await BuildParameterQuickInfoAsync(
                    tokenText,
                    documentText,
                    token.StartOffset,
                    pwshExecutablePath,
                    cancellationToken).ConfigureAwait(true);
            }

            if (IsDocumentFunction(tokenText, documentText))
            {
                return new EditorQuickInfo(
                    tokenText,
                    $"Function in this script: {tokenText}\n\nPress Ctrl+Space near the name to insert it from IntelliSense.");
            }

            var localQuickInfo = BuildLocalCommandQuickInfo(tokenText);
            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return localQuickInfo;
            }

            if (_completionService.TryGetCachedCommandQuickInfo(pwshExecutablePath, tokenText, out var cachedCommandInfo) &&
                cachedCommandInfo is not null)
            {
                return BuildBestCommandQuickInfo(tokenText, cachedCommandInfo, preferredParameterName: null, pwshExecutablePath, localQuickInfo);
            }

            using var quickInfoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quickInfoCts.CancelAfter(TimeSpan.FromMilliseconds(350));

            try
            {
                var commandInfo = await _completionService.GetCommandQuickInfoAsync(
                        tokenText,
                        pwshExecutablePath,
                        cancellationToken: quickInfoCts.Token)
                    .ConfigureAwait(true);

                return BuildBestCommandQuickInfo(tokenText, commandInfo, preferredParameterName: null, pwshExecutablePath, localQuickInfo);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return localQuickInfo;
            }
        }

        private CompletionWindow? BuildWindow(
            TextEditor editor,
            CompletionContext context,
            CompletionServiceResult? engineResult,
            string documentText,
            ParameterCompletionContext? parameterContext,
            ParameterValueCompletionContext? parameterValueContext,
            MemberCompletionContext? memberContext,
            StaticMemberCompletionContext? staticMemberContext,
            PowerShellQuickInfo? parameterCommandInfo,
            IReadOnlyList<PowerShellCommandReference> cachedCommandReferences,
            string? pwshExecutablePath,
            string completionKind,
            bool forceCompletion)
        {
            var candidates = new List<CompletionCandidate>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var isParameterContext = parameterContext is not null || context.Fragment.StartsWith("-", StringComparison.Ordinal);
            var isParameterValueContext = parameterValueContext is not null;
            var isMemberContext = memberContext is not null;
            var isStaticMemberContext = staticMemberContext is not null;
            var isCommandSpecificContext = isParameterContext || isParameterValueContext || isMemberContext || isStaticMemberContext;
            var activeMatchFragment = staticMemberContext?.MemberFragment ?? memberContext?.MemberFragment ?? context.Fragment;
            var engineCandidateCount = 0;
            var parameterMetadataCandidateCount = 0;
            var parameterFallbackCandidateCount = 0;
            var parameterValueCandidateCount = 0;
            var pathValueCandidateCount = 0;
            var localMemberCandidateCount = 0;
            var cachedCommandCandidateCount = 0;
            var localFallbackCandidateCount = 0;
            var snippetCandidateCount = 0;

            void AddCandidate(
                string completionText,
                string listText,
                string description,
                CompletionItemKind kind,
                int replacementOffset,
                int replacementLength,
                double priority,
                bool requireFragmentMatch)
            {
                if (string.IsNullOrWhiteSpace(completionText))
                {
                    return;
                }

                var visibleText = string.IsNullOrWhiteSpace(listText) ? completionText : listText;
                var visibleMatchScore = GetMatchScore(visibleText, activeMatchFragment);
                var completionMatchScore = GetMatchScore(completionText, activeMatchFragment);
                var matchScore = Math.Max(visibleMatchScore, completionMatchScore);
                if (requireFragmentMatch && matchScore < 0)
                {
                    return;
                }

                var key = $"{kind}:{completionText}:{visibleText}";
                if (!seen.Add(key))
                {
                    return;
                }

                candidates.Add(new CompletionCandidate(
                    completionText,
                    visibleText,
                    description,
                    kind,
                    replacementOffset,
                    replacementLength,
                    priority,
                    matchScore));
            }

            if (engineResult is { HasItems: true })
            {
                foreach (var item in engineResult.Items)
                {
                    AddCandidate(
                        item.CompletionText,
                        item.ListItemText,
                        item.Tooltip,
                        item.Kind,
                        engineResult.ReplacementIndex,
                        engineResult.ReplacementLength,
                        100 + GetKindPriority(item.Kind),
                        requireFragmentMatch: isCommandSpecificContext || !string.IsNullOrWhiteSpace(context.Fragment));
                    engineCandidateCount++;
                }
            }

            if (memberContext is not null)
            {
                foreach (var member in GetLocalMemberCompletionCandidates(documentText, memberContext))
                {
                    AddCandidate(
                        member.CompletionText,
                        member.DisplayText,
                        member.Description,
                        member.Kind,
                        memberContext.MemberReplacementOffset,
                        memberContext.MemberReplacementLength,
                        member.Priority,
                        requireFragmentMatch: !string.IsNullOrWhiteSpace(memberContext.MemberFragment));
                    localMemberCandidateCount++;
                }
            }

            if (staticMemberContext is not null)
            {
                foreach (var member in GetLocalStaticMemberCompletionCandidates(documentText, staticMemberContext))
                {
                    AddCandidate(
                        member.CompletionText,
                        member.DisplayText,
                        member.Description,
                        member.Kind,
                        staticMemberContext.MemberReplacementOffset,
                        staticMemberContext.MemberReplacementLength,
                        member.Priority,
                        requireFragmentMatch: !string.IsNullOrWhiteSpace(staticMemberContext.MemberFragment));
                    localMemberCandidateCount++;
                }
            }

            if (parameterContext is not null && HasUsableParameterMetadata(parameterCommandInfo))
            {
                foreach (var parameter in parameterCommandInfo!.Parameters)
                {
                    if (IsParameterAlreadyUsed(parameterContext, parameter))
                    {
                        continue;
                    }

                    var parameterText = "-" + parameter.Name;
                    AddCandidate(
                        parameterText,
                        parameterText,
                        BuildParameterCompletionDescription(parameterCommandInfo.Title, parameter),
                        CompletionItemKind.ParameterName,
                        context.ReplacementOffset,
                        context.ReplacementLength,
                        parameter.Mandatory ? 112 : 108,
                        requireFragmentMatch: true);
                    parameterMetadataCandidateCount++;

                    foreach (var alias in parameter.Aliases)
                    {
                        var aliasText = "-" + alias;
                        AddCandidate(
                            aliasText,
                            aliasText,
                            BuildParameterCompletionDescription(parameterCommandInfo.Title, parameter),
                            CompletionItemKind.ParameterName,
                            context.ReplacementOffset,
                            context.ReplacementLength,
                            parameter.Mandatory ? 107 : 104,
                            requireFragmentMatch: true);
                        parameterMetadataCandidateCount++;
                    }
                }
            }
            else if (parameterContext is not null)
            {
                // Last-resort, command-specific fallback for cases where the live engine
                // is still busy and the background metadata warmer has not reached this
                // command yet. These are intentionally narrow and are only used when the
                // command name is known; live PowerShell metadata replaces them as soon as
                // the cache is warm.
                foreach (var parameterName in GetEmergencyParameterFallbacks(parameterContext.CommandName))
                {
                    if (IsParameterAlreadyUsed(parameterContext, parameterName))
                    {
                        continue;
                    }

                    var parameterText = "-" + parameterName;
                    AddCandidate(
                        parameterText,
                        parameterText,
                        $"{parameterContext.CommandName} parameter: -{parameterName}",
                        CompletionItemKind.ParameterName,
                        context.ReplacementOffset,
                        context.ReplacementLength,
                        70,
                        requireFragmentMatch: true);
                    parameterFallbackCandidateCount++;
                }
            }

            if (parameterValueContext is not null)
            {
                foreach (var pathCandidate in GetPathValueCandidates(parameterValueContext, parameterCommandInfo))
                {
                    AddCandidate(
                        pathCandidate.CompletionText,
                        pathCandidate.DisplayText,
                        pathCandidate.Description,
                        pathCandidate.Kind,
                        parameterValueContext.ReplacementOffset,
                        parameterValueContext.ReplacementLength,
                        114,
                        requireFragmentMatch: false);
                    pathValueCandidateCount++;
                }
            }

            if (parameterValueContext is not null && HasUsableParameterMetadata(parameterCommandInfo))
            {
                foreach (var value in GetParameterValueCandidates(parameterCommandInfo!, parameterValueContext.ParameterName))
                {
                    AddCandidate(
                        FormatParameterValueCompletion(value, parameterValueContext),
                        value,
                        $"{parameterCommandInfo!.Title} -{parameterValueContext.ParameterName} value: {value}",
                        CompletionItemKind.ParameterValue,
                        parameterValueContext.ReplacementOffset,
                        parameterValueContext.ReplacementLength,
                        110,
                        requireFragmentMatch: !string.IsNullOrWhiteSpace(parameterValueContext.Fragment));
                    parameterValueCandidateCount++;
                }
            }
            else if (parameterValueContext is not null)
            {
                foreach (var value in GetEmergencyParameterValueFallbacks(parameterValueContext.CommandName, parameterValueContext.ParameterName))
                {
                    AddCandidate(
                        FormatParameterValueCompletion(value, parameterValueContext),
                        value,
                        $"{parameterValueContext.CommandName} -{parameterValueContext.ParameterName} value: {value}",
                        CompletionItemKind.ParameterValue,
                        parameterValueContext.ReplacementOffset,
                        parameterValueContext.ReplacementLength,
                        65,
                        requireFragmentMatch: !string.IsNullOrWhiteSpace(parameterValueContext.Fragment));
                    parameterValueCandidateCount++;
                }
            }

            if (isCommandSpecificContext)
            {
                // While typing a command parameter, command/snippet fallback items are more
                // harmful than helpful. Without this guard, a fragment like "-Rec" can match
                // unrelated commands containing a dash, which is how entries such as
                // Invoke-RestMethod ended up appearing after Get-ChildItem.
            }
            else
            {
            foreach (var function in ExtractDocumentFunctions(documentText))
            {
                AddCandidate(function, function, $"Function in this script: {function}", CompletionItemKind.Command,
                    context.ReplacementOffset, context.ReplacementLength, 92, requireFragmentMatch: true);
                localFallbackCandidateCount++;
            }

            foreach (var variable in ExtractParamBlockVariables(documentText))
            {
                AddCandidate(variable, variable, $"Parameter variable in this script: {variable}", CompletionItemKind.Variable,
                    context.ReplacementOffset, context.ReplacementLength, 91, requireFragmentMatch: true);
                localFallbackCandidateCount++;
            }

            foreach (var variable in ExtractDocumentVariables(documentText))
            {
                AddCandidate(variable, variable, $"Variable in this script: {variable}", CompletionItemKind.Variable,
                    context.ReplacementOffset, context.ReplacementLength, 90, requireFragmentMatch: true);
                localFallbackCandidateCount++;
            }

            AddStatic(candidates, seen, AutomaticVariables, CompletionItemKind.Variable, "Automatic variable: ", context, 72);

            foreach (var commandReference in cachedCommandReferences)
            {
                AddCandidate(
                    commandReference.Name,
                    commandReference.Name,
                    BuildCachedCommandDescription(
                        commandReference,
                        _completionService.TryGetCachedCommandQuickInfo(pwshExecutablePath, commandReference.Name, out var cachedCommandQuickInfo)
                            ? cachedCommandQuickInfo
                            : null),
                    CompletionItemKind.Command,
                    context.ReplacementOffset,
                    context.ReplacementLength,
                    commandReference.IsAlias ? 68 : 66,
                    requireFragmentMatch: true);
                cachedCommandCandidateCount++;
            }

            if (cachedCommandReferences.Count == 0)
            {
                AddStatic(candidates, seen, CommonAliases, CompletionItemKind.Command, "PowerShell alias: ", context, 63);
                AddStatic(candidates, seen, CommonCommands, CompletionItemKind.Command, "Common PowerShell command: ", context, 60);
            }

            AddStatic(candidates, seen, Keywords, CompletionItemKind.Keyword, "PowerShell keyword: ", context, 50);
            }

            var ordered = candidates
                .OrderByDescending(c => c.Priority)
                .ThenByDescending(c => c.MatchScore)
                .ThenBy(c => c.DisplayText, StringComparer.OrdinalIgnoreCase)
                .Take(500)
                .Select(c => new PowerShellCompletionData(
                    c.CompletionText,
                    c.DisplayText,
                    c.Description,
                    c.ReplacementOffset,
                    c.ReplacementLength,
                    c.Kind,
                    c.Priority))
                .Cast<ICompletionData>()
                .ToList();

            if (!isCommandSpecificContext && ShouldOfferSnippets(context))
            {
                foreach (var snippetItem in _snippetProvider.GetCompletions(context.Fragment))
                {
                    ordered.Add(snippetItem);
                    snippetCandidateCount++;
                }
            }

            if (ordered.Count == 0)
            {
                AppLogger.Debug(
                    "EditorCompletion",
                    $"Completion window build produced no visible items. Kind={completionKind}, ForceCompletion={forceCompletion}, Fragment='{context.Fragment}', EngineCandidates={engineCandidateCount}, CachedCommandCandidates={cachedCommandCandidateCount}, ParameterMetadataCandidates={parameterMetadataCandidateCount}, ParameterFallbackCandidates={parameterFallbackCandidateCount}, ParameterValueCandidates={parameterValueCandidateCount}, PathValueCandidates={pathValueCandidateCount}, LocalMemberCandidates={localMemberCandidateCount}, LocalFallbackCandidates={localFallbackCandidateCount}, SnippetCandidates={snippetCandidateCount}, CachedParameterCount={parameterCommandInfo?.Parameters.Count ?? 0}.");
                return null;
            }

            var window = new CompletionWindow(editor.TextArea)
            {
                CloseAutomatically = true,
                MinWidth = 460,
                MaxHeight = 360,
            };

            window.CompletionList.IsFiltering = true;

            foreach (var item in ordered)
            {
                window.CompletionList.CompletionData.Add(item);
            }

            // Keep AvalonEdit's live filtering tied to the text the user has actually typed.
            // Individual PowerShellCompletionData items still use their own replacement span,
            // so engine completions can replace the correct command/parameter/path segment.
            window.StartOffset = staticMemberContext?.MemberReplacementOffset ?? memberContext?.MemberReplacementOffset ?? context.ReplacementOffset;
            window.EndOffset = staticMemberContext is not null
                ? staticMemberContext.MemberReplacementOffset + staticMemberContext.MemberReplacementLength
                : memberContext is not null
                    ? memberContext.MemberReplacementOffset + memberContext.MemberReplacementLength
                    : context.ReplacementOffset + context.ReplacementLength;
            window.CompletionList.SelectItem(staticMemberContext?.MemberFragment ?? memberContext?.MemberFragment ?? context.Fragment);
            AppLogger.Debug(
                "EditorCompletion",
                $"Completion window built. Kind={completionKind}, ForceCompletion={forceCompletion}, Fragment='{context.Fragment}', ItemCount={ordered.Count}, EngineCandidates={engineCandidateCount}, CachedCommandCandidates={cachedCommandCandidateCount}, ParameterMetadataCandidates={parameterMetadataCandidateCount}, ParameterFallbackCandidates={parameterFallbackCandidateCount}, ParameterValueCandidates={parameterValueCandidateCount}, PathValueCandidates={pathValueCandidateCount}, LocalMemberCandidates={localMemberCandidateCount}, LocalFallbackCandidates={localFallbackCandidateCount}, SnippetCandidates={snippetCandidateCount}, CachedParameterCount={parameterCommandInfo?.Parameters.Count ?? 0}.");
            return window;
        }

        private static string BuildCachedCommandDescription(PowerShellCommandReference commandReference, PowerShellQuickInfo? quickInfo)
        {
            var builder = new StringBuilder();

            if (commandReference.IsAlias)
            {
                builder.Append(string.IsNullOrWhiteSpace(commandReference.ResolvedCommandName)
                    ? "PowerShell alias: " + commandReference.Name
                    : "PowerShell alias: " + commandReference.Name + " -> " + commandReference.ResolvedCommandName);
            }
            else
            {
                builder.Append(string.IsNullOrWhiteSpace(commandReference.Kind) ? "PowerShell command" : commandReference.Kind);
                builder.Append(": ").Append(commandReference.Name);
            }

            if (!string.IsNullOrWhiteSpace(commandReference.ModuleName))
            {
                builder.Append(" • Module: ").Append(commandReference.ModuleName);
            }

            if (quickInfo is not null)
            {
                var synopsis = quickInfo.Synopsis?.Trim();
                if (!string.IsNullOrWhiteSpace(synopsis))
                {
                    builder.AppendLine();
                    builder.AppendLine();
                    builder.Append(synopsis);
                }
                else if (!string.IsNullOrWhiteSpace(quickInfo.Syntax))
                {
                    var syntaxLine = quickInfo.Syntax
                        .Replace("\r\n", "\n", StringComparison.Ordinal)
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Select(line => line.Trim())
                        .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));

                    if (!string.IsNullOrWhiteSpace(syntaxLine))
                    {
                        builder.AppendLine();
                        builder.AppendLine();
                        builder.Append(syntaxLine);
                    }
                }
            }

            return builder.ToString();
        }


        private static bool RequiresParameterMetadata(
            ParameterCompletionContext? parameterContext,
            ParameterValueCompletionContext? parameterValueContext)
        {
            return parameterContext is not null || parameterValueContext is not null;
        }

        private static bool HasUsableParameterMetadata(PowerShellQuickInfo? quickInfo)
        {
            return quickInfo is not null && quickInfo.Parameters.Count > 0;
        }

        private static string BuildParameterCompletionDescription(string commandName, PowerShellParameterQuickInfo parameter)
        {
            var builder = new StringBuilder();
            builder.Append(commandName).Append(" parameter: -").Append(parameter.Name);

            if (!string.IsNullOrWhiteSpace(parameter.TypeName))
            {
                builder.Append(" <").Append(parameter.TypeName).Append('>');
            }

            if (parameter.Mandatory)
            {
                builder.Append(" required");
            }

            if (parameter.Aliases.Count > 0)
            {
                builder.Append(" aliases: ").Append(string.Join(", ", parameter.Aliases.Select(alias => "-" + alias)));
            }

            var values = parameter.ValidValues.Count > 0 ? parameter.ValidValues : parameter.EnumValues;
            if (values.Count > 0)
            {
                builder.Append(" values: ").Append(string.Join(", ", values.Take(8)));
                if (values.Count > 8) builder.Append(", …");
            }

            if (parameter.Position is int position)
            {
                builder.Append(" position ").Append(position);
            }

            return builder.ToString();
        }

        private static IEnumerable<string> GetEmergencyParameterFallbacks(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return Array.Empty<string>();
            }

            // This is not the primary IntelliSense source. It only prevents an empty
            // parameter popup while the live PowerShell metadata request/warmup is still
            // catching up. Keep this list tiny and for high-value built-in commands only.
            if (string.Equals(commandName, "Set-ExecutionPolicy", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    "ExecutionPolicy", "Scope", "Force", "Confirm", "WhatIf",
                    "Verbose", "Debug", "ErrorAction", "WarningAction", "InformationAction"
                };
            }

            return Array.Empty<string>();
        }

        private static IEnumerable<string> GetEmergencyParameterValueFallbacks(string commandName, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(commandName) || string.IsNullOrWhiteSpace(parameterName))
            {
                return Array.Empty<string>();
            }

            // Narrow fallback only. Real values still come from PowerShell metadata first.
            if (string.Equals(commandName, "Set-ExecutionPolicy", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(parameterName, "ExecutionPolicy", StringComparison.OrdinalIgnoreCase))
                {
                    return new[] { "Restricted", "AllSigned", "RemoteSigned", "Unrestricted", "Bypass", "Undefined" };
                }

                if (string.Equals(parameterName, "Scope", StringComparison.OrdinalIgnoreCase))
                {
                    return new[] { "Process", "CurrentUser", "LocalMachine", "MachinePolicy", "UserPolicy" };
                }
            }

            return Array.Empty<string>();
        }

        private static IEnumerable<PathValueCompletionCandidate> GetPathValueCandidates(
            ParameterValueCompletionContext context,
            PowerShellQuickInfo? commandInfo)
        {
            if (context is null || !LooksLikePathValueContext(context, commandInfo))
            {
                return Array.Empty<PathValueCompletionCandidate>();
            }

            return EnumerateLocalPathCompletions(context).Take(80).ToList();
        }

        private static bool LooksLikePathValueContext(
            ParameterValueCompletionContext context,
            PowerShellQuickInfo? commandInfo)
        {
            if (context is null || string.IsNullOrWhiteSpace(context.ParameterName))
            {
                return false;
            }

            var parameterName = context.ParameterName.TrimStart('-');
            if (IsPathLikeParameterName(parameterName))
            {
                return true;
            }

            var parameter = commandInfo?.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase) ||
                p.Aliases.Any(alias => string.Equals(alias, parameterName, StringComparison.OrdinalIgnoreCase)));

            if (parameter is null)
            {
                return false;
            }

            if (IsPathLikeParameterName(parameter.Name))
            {
                return true;
            }

            var typeName = parameter.TypeName ?? string.Empty;
            return typeName.Contains("FileInfo", StringComparison.OrdinalIgnoreCase) ||
                   typeName.Contains("DirectoryInfo", StringComparison.OrdinalIgnoreCase) ||
                   typeName.Contains("FileSystemInfo", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPathLikeParameterName(string parameterName)
        {
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                return false;
            }

            var normalized = parameterName.TrimStart('-');
            return string.Equals(normalized, "Path", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "LiteralPath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "FilePath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "FullName", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Destination", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "DestinationPath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "SourcePath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "InputPath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "OutputPath", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "OutFile", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "InFile", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Directory", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "Folder", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<PathValueCompletionCandidate> EnumerateLocalPathCompletions(ParameterValueCompletionContext context)
        {
            var fragment = context.Fragment ?? string.Empty;
            var unescapedFragment = fragment.Trim();

            if (unescapedFragment.Length >= 1 && unescapedFragment[0] is '\'' or '"')
            {
                unescapedFragment = unescapedFragment[1..];
            }

            unescapedFragment = unescapedFragment.Replace("` ", " ", StringComparison.Ordinal);

            var directoryPart = string.Empty;
            var namePrefix = string.Empty;
            var searchDirectory = string.Empty;
            var preservePrefix = string.Empty;

            if (string.IsNullOrWhiteSpace(unescapedFragment))
            {
                searchDirectory = Environment.CurrentDirectory;
            }
            else if (unescapedFragment.EndsWith("\\", StringComparison.Ordinal) ||
                     unescapedFragment.EndsWith("/", StringComparison.Ordinal))
            {
                directoryPart = unescapedFragment;
                searchDirectory = ExpandUserPath(directoryPart);
            }
            else
            {
                directoryPart = System.IO.Path.GetDirectoryName(unescapedFragment) ?? string.Empty;
                namePrefix = System.IO.Path.GetFileName(unescapedFragment);

                if (string.IsNullOrWhiteSpace(directoryPart))
                {
                    searchDirectory = Environment.CurrentDirectory;
                }
                else
                {
                    searchDirectory = ExpandUserPath(directoryPart);
                    preservePrefix = directoryPart;
                }
            }

            if (!string.IsNullOrWhiteSpace(directoryPart) &&
                !directoryPart.EndsWith("\\", StringComparison.Ordinal) &&
                !directoryPart.EndsWith("/", StringComparison.Ordinal))
            {
                preservePrefix = directoryPart + System.IO.Path.DirectorySeparatorChar;
            }
            else if (!string.IsNullOrWhiteSpace(directoryPart))
            {
                preservePrefix = directoryPart;
            }

            if (string.IsNullOrWhiteSpace(searchDirectory) || !System.IO.Directory.Exists(searchDirectory))
            {
                yield break;
            }

            IEnumerable<string> directories;
            IEnumerable<string> files;
            try
            {
                directories = System.IO.Directory.EnumerateDirectories(searchDirectory)
                    .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToList();

                files = System.IO.Directory.EnumerateFiles(searchDirectory)
                    .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                    .Take(80)
                    .ToList();
            }
            catch
            {
                yield break;
            }

            foreach (var directory in directories)
            {
                var name = System.IO.Path.GetFileName(directory);
                if (!MatchesPathPrefix(name, namePrefix)) continue;

                var value = BuildCompletedPathValue(preservePrefix, name, isDirectory: true);
                var completionText = FormatPathCompletionValue(value, context);
                yield return new PathValueCompletionCandidate(
                    completionText,
                    value,
                    "Folder: " + value,
                    CompletionItemKind.ProviderContainer);
            }

            foreach (var file in files)
            {
                var name = System.IO.Path.GetFileName(file);
                if (!MatchesPathPrefix(name, namePrefix)) continue;

                var value = BuildCompletedPathValue(preservePrefix, name, isDirectory: false);
                var completionText = FormatPathCompletionValue(value, context);
                yield return new PathValueCompletionCandidate(
                    completionText,
                    value,
                    "File: " + value,
                    CompletionItemKind.ProviderItem);
            }
        }

        private static string ExpandUserPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return path;
            }

            if (path == "~" ||
                path.StartsWith("~\\", StringComparison.Ordinal) ||
                path.StartsWith("~/", StringComparison.Ordinal))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrWhiteSpace(home))
                {
                    return path;
                }

                return path.Length == 1
                    ? home
                    : System.IO.Path.Combine(home, path[2..]);
            }

            return path;
        }

        private static bool MatchesPathPrefix(string value, string prefix)
        {
            if (string.IsNullOrWhiteSpace(prefix))
            {
                return true;
            }

            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildCompletedPathValue(string preservePrefix, string name, bool isDirectory)
        {
            var value = string.IsNullOrWhiteSpace(preservePrefix)
                ? name
                : preservePrefix + name;

            if (isDirectory &&
                !value.EndsWith("\\", StringComparison.Ordinal) &&
                !value.EndsWith("/", StringComparison.Ordinal))
            {
                value += System.IO.Path.DirectorySeparatorChar;
            }

            return value;
        }

        private static string FormatPathCompletionValue(string value, ParameterValueCompletionContext context)
        {
            if (context.IsQuotedValue)
            {
                return value;
            }

            return QuoteCompletionValueIfNeeded(value);
        }

        private static string FormatParameterValueCompletion(string value, ParameterValueCompletionContext context)
        {
            if (context.IsQuotedValue)
            {
                return value;
            }

            return QuoteCompletionValueIfNeeded(value);
        }

        private static IEnumerable<string> GetParameterValueCandidates(PowerShellQuickInfo commandInfo, string parameterName)
        {
            if (commandInfo is null || string.IsNullOrWhiteSpace(parameterName))
            {
                return Array.Empty<string>();
            }

            var parameter = commandInfo.Parameters.FirstOrDefault(p =>
                string.Equals(p.Name, parameterName, StringComparison.OrdinalIgnoreCase) ||
                p.Aliases.Any(alias => string.Equals(alias, parameterName, StringComparison.OrdinalIgnoreCase)));

            if (parameter is null || parameter.IsSwitch)
            {
                return Array.Empty<string>();
            }

            var values = new List<string>();
            void AddValues(IEnumerable<string> source)
            {
                foreach (var value in source)
                {
                    if (!string.IsNullOrWhiteSpace(value) &&
                        !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                    {
                        values.Add(value);
                    }
                }
            }

            AddValues(parameter.ValidValues);
            AddValues(parameter.EnumValues);

            if (values.Count == 0 &&
                (string.Equals(parameter.TypeName, "Boolean", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(parameter.TypeName, "bool", StringComparison.OrdinalIgnoreCase)))
            {
                values.Add("$true");
                values.Add("$false");
            }

            return values;
        }

        private static string QuoteCompletionValueIfNeeded(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            if (value.StartsWith("$", StringComparison.Ordinal) ||
                Regex.IsMatch(value, @"^[A-Za-z0-9_./\\:-]+$", RegexOptions.CultureInvariant))
            {
                return value;
            }

            return "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        }

        private static void AddStatic(
            List<CompletionCandidate> candidates,
            HashSet<string> seen,
            IEnumerable<string> values,
            CompletionItemKind kind,
            string descPrefix,
            CompletionContext context,
            double priority)
        {
            foreach (var value in values)
            {
                var matchScore = GetMatchScore(value, context.Fragment);
                if (matchScore < 0) continue;

                var key = $"{kind}:{value}:{value}";
                if (!seen.Add(key)) continue;

                candidates.Add(new CompletionCandidate(
                    value,
                    value,
                    descPrefix + value,
                    kind,
                    context.ReplacementOffset,
                    context.ReplacementLength,
                    priority,
                    matchScore));
            }
        }

        private static CompletionContext GetCompletionContext(string text, int caretOffset)
        {
            var scanOffset = Math.Clamp(caretOffset, 0, text.Length);

            while (scanOffset > 0)
            {
                var ch = text[scanOffset - 1];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '$' || ch == ':' || ch == '.' || ch == '\\' || ch == '/' || ch == '[' || ch == ']')
                {
                    scanOffset--;
                    continue;
                }
                break;
            }

            var replacementLength = caretOffset - scanOffset;
            var fragment = replacementLength > 0 ? text.Substring(scanOffset, replacementLength) : string.Empty;
            return new CompletionContext(scanOffset, replacementLength, fragment);
        }

        private static ParameterCompletionContext? TryGetParameterCompletionContext(string documentText, CompletionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Fragment))
            {
                return null;
            }

            var fragment = context.Fragment;
            if (!fragment.StartsWith("-", StringComparison.Ordinal))
            {
                var expandedFragment = TryExpandToParameterFragment(documentText, context);
                if (string.IsNullOrWhiteSpace(expandedFragment) ||
                    !expandedFragment.StartsWith("-", StringComparison.Ordinal))
                {
                    return null;
                }

                fragment = expandedFragment;
            }

            var safeOffset = Math.Clamp(context.ReplacementOffset, 0, documentText.Length);
            var statementStart = FindStatementStart(documentText, safeOffset);
            var segment = safeOffset > statementStart
                ? documentText.Substring(statementStart, safeOffset - statementStart)
                : string.Empty;

            if (string.IsNullOrWhiteSpace(segment) || Regex.IsMatch(segment, @"^\s*#", RegexOptions.CultureInvariant))
            {
                return null;
            }

            var commentIndex = segment.IndexOf('#');
            if (commentIndex >= 0)
            {
                segment = segment[..commentIndex];
            }

            var tokens = TokenizeSegment(segment).ToList();
            if (tokens.Count == 0)
            {
                return null;
            }

            var commandTokenIndex = 0;
            while (commandTokenIndex < tokens.Count &&
                   (string.Equals(tokens[commandTokenIndex].Text, "&", StringComparison.Ordinal) ||
                    string.Equals(tokens[commandTokenIndex].Text, ".", StringComparison.Ordinal)))
            {
                commandTokenIndex++;
            }

            if (commandTokenIndex >= tokens.Count)
            {
                return null;
            }

            var commandName = tokens[commandTokenIndex].Text;
            if (string.IsNullOrWhiteSpace(commandName) || commandName.StartsWith("-", StringComparison.Ordinal))
            {
                return null;
            }

            var usedParameterNames = ExtractUsedParameterNames(tokens, commandTokenIndex);
            return new ParameterCompletionContext(commandName, fragment.TrimStart('-'), usedParameterNames);
        }

        private static string? TryExpandToParameterFragment(string documentText, CompletionContext context)
        {
            if (string.IsNullOrWhiteSpace(documentText))
            {
                return null;
            }

            var tokenEnd = Math.Clamp(context.ReplacementOffset + context.ReplacementLength, 0, documentText.Length);
            var tokenStart = Math.Clamp(context.ReplacementOffset, 0, tokenEnd);

            while (tokenStart > 0)
            {
                var ch = documentText[tokenStart - 1];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                {
                    tokenStart--;
                    continue;
                }

                break;
            }

            if (tokenStart >= tokenEnd)
            {
                return null;
            }

            var token = documentText.Substring(tokenStart, tokenEnd - tokenStart);
            return token.StartsWith("-", StringComparison.Ordinal)
                ? token
                : null;
        }

        private static ParameterValueCompletionContext? TryGetParameterValueCompletionContext(
            string documentText,
            int caretOffset,
            CompletionContext context)
        {
            if (string.IsNullOrWhiteSpace(documentText) ||
                context.Fragment.StartsWith("-", StringComparison.Ordinal) ||
                context.Fragment.StartsWith("$", StringComparison.Ordinal))
            {
                return null;
            }

            var safeOffset = Math.Clamp(caretOffset, 0, documentText.Length);
            var statementStart = FindStatementStart(documentText, safeOffset);
            if (safeOffset <= statementStart)
            {
                return null;
            }

            var segment = documentText.Substring(statementStart, safeOffset - statementStart);
            if (string.IsNullOrWhiteSpace(segment) || Regex.IsMatch(segment, @"^\s*#", RegexOptions.CultureInvariant))
            {
                return null;
            }

            var commentIndex = segment.IndexOf('#');
            if (commentIndex >= 0)
            {
                segment = segment[..commentIndex];
            }

            var tokens = TokenizeSegment(segment).ToList();
            if (tokens.Count < 2)
            {
                return null;
            }

            var commandTokenIndex = 0;
            while (commandTokenIndex < tokens.Count &&
                   (string.Equals(tokens[commandTokenIndex].Text, "&", StringComparison.Ordinal) ||
                    string.Equals(tokens[commandTokenIndex].Text, ".", StringComparison.Ordinal)))
            {
                commandTokenIndex++;
            }

            if (commandTokenIndex >= tokens.Count)
            {
                return null;
            }

            var commandName = tokens[commandTokenIndex].Text;
            if (string.IsNullOrWhiteSpace(commandName) || commandName.StartsWith("-", StringComparison.Ordinal))
            {
                return null;
            }

            var fragmentLocalStart = context.ReplacementOffset - statementStart;
            var fragmentLocalEnd = fragmentLocalStart + context.ReplacementLength;
            var fragmentTokenIndex = tokens.FindIndex(token =>
                fragmentLocalStart >= token.Start &&
                fragmentLocalStart <= token.Start + token.Length &&
                fragmentLocalEnd <= token.Start + token.Length);

            var hasTypedValueFragment = !string.IsNullOrWhiteSpace(context.Fragment) && fragmentTokenIndex >= 0;
            var isQuotedValue = false;
            char? quoteChar = null;
            int parameterTokenIndex;

            if (hasTypedValueFragment)
            {
                var valueToken = tokens[fragmentTokenIndex].Text;
                if (valueToken.Length > 0 && valueToken[0] is '\'' or '"')
                {
                    isQuotedValue = true;
                    quoteChar = valueToken[0];
                }

                parameterTokenIndex = fragmentTokenIndex - 1;
            }
            else
            {
                var lastToken = tokens[^1].Text;
                if (IsQuotedValueToken(lastToken))
                {
                    isQuotedValue = true;
                    quoteChar = lastToken[0];
                    parameterTokenIndex = tokens.Count - 2;
                }
                else
                {
                    parameterTokenIndex = tokens.Count - 1;
                }
            }

            if (parameterTokenIndex <= commandTokenIndex || parameterTokenIndex >= tokens.Count)
            {
                return null;
            }

            var parameterToken = tokens[parameterTokenIndex].Text;
            if (!LooksLikeParameterToken(parameterToken))
            {
                return null;
            }

            var parameterName = parameterToken.TrimStart('-');
            if (string.IsNullOrWhiteSpace(parameterName))
            {
                return null;
            }

            return new ParameterValueCompletionContext(
                commandName,
                parameterName,
                context.Fragment,
                context.ReplacementOffset,
                context.ReplacementLength,
                isQuotedValue,
                quoteChar);
        }

        private static int FindStatementStart(string documentText, int safeOffset)
        {
            var statementStart = 0;
            for (var i = Math.Max(0, safeOffset - 1); i >= 0; i--)
            {
                var ch = documentText[i];
                if (ch is '\n' or '\r' or '|' or ';' or '{' or '}')
                {
                    statementStart = i + 1;
                    break;
                }
            }

            return statementStart;
        }

        private static bool IsQuotedValueToken(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            return token[0] is '\'' or '"';
        }

        private static bool LooksLikeParameterToken(string token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return false;
            }

            if (string.Equals(token, "--%", StringComparison.Ordinal))
            {
                return false;
            }

            return Regex.IsMatch(token, @"^-{1,2}[A-Za-z][\w]*$", RegexOptions.CultureInvariant);
        }

        private static HashSet<string> ExtractUsedParameterNames(IReadOnlyList<ScriptSegmentToken> tokens, int commandTokenIndex)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tokens.Count == 0 || commandTokenIndex < 0 || commandTokenIndex >= tokens.Count)
            {
                return used;
            }

            for (var i = commandTokenIndex + 1; i < tokens.Count; i++)
            {
                var token = tokens[i].Text;
                if (!LooksLikeParameterToken(token))
                {
                    continue;
                }

                var normalized = token.TrimStart('-');
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    used.Add(normalized);
                }
            }

            return used;
        }

        private static bool IsParameterAlreadyUsed(ParameterCompletionContext context, PowerShellParameterQuickInfo parameter)
        {
            if (context.UsedParameterNames.Count == 0)
            {
                return false;
            }

            if (context.UsedParameterNames.Contains(parameter.Name))
            {
                return true;
            }

            foreach (var alias in parameter.Aliases)
            {
                if (context.UsedParameterNames.Contains(alias))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsParameterAlreadyUsed(ParameterCompletionContext context, string parameterName)
        {
            return context.UsedParameterNames.Contains(parameterName);
        }

        private static IEnumerable<ScriptSegmentToken> TokenizeSegment(string segment)
        {
            if (string.IsNullOrEmpty(segment))
            {
                yield break;
            }

            var i = 0;
            while (i < segment.Length)
            {
                while (i < segment.Length && char.IsWhiteSpace(segment[i]))
                {
                    i++;
                }

                if (i >= segment.Length)
                {
                    yield break;
                }

                var start = i;
                var quote = segment[i] is '\'' or '"' ? segment[i] : '\0';
                if (quote != '\0')
                {
                    i++;
                    while (i < segment.Length)
                    {
                        if (segment[i] == quote)
                        {
                            i++;
                            break;
                        }

                        i++;
                    }
                }
                else
                {
                    while (i < segment.Length && !char.IsWhiteSpace(segment[i]))
                    {
                        i++;
                    }
                }

                var text = segment.Substring(start, i - start);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    yield return new ScriptSegmentToken(start, text.Length, text);
                }
            }
        }

        private static MemberCompletionContext? TryGetMemberCompletionContext(string documentText, CompletionContext context)
        {
            if (string.IsNullOrEmpty(documentText) || string.IsNullOrEmpty(context.Fragment))
            {
                return null;
            }

            var dotIndex = context.Fragment.LastIndexOf(".", StringComparison.Ordinal);
            if (dotIndex < 0)
            {
                return null;
            }

            // Do not treat provider/path fragments such as C:\Users or ./scripts as object-member access.
            if (LooksLikePathOrDriveFragment(context.Fragment, dotIndex))
            {
                return null;
            }

            var expressionText = context.Fragment[..dotIndex].Trim();
            if (string.IsNullOrWhiteSpace(expressionText))
            {
                return null;
            }

            var memberFragment = dotIndex + 1 < context.Fragment.Length
                ? context.Fragment[(dotIndex + 1)..]
                : string.Empty;

            var expressionOffset = context.ReplacementOffset;
            var memberReplacementOffset = context.ReplacementOffset + dotIndex + 1;
            var memberReplacementLength = Math.Max(0, context.ReplacementLength - dotIndex - 1);

            return new MemberCompletionContext(
                expressionText,
                memberFragment,
                expressionOffset,
                memberReplacementOffset,
                memberReplacementLength);
        }

        private static bool LooksLikePathOrDriveFragment(string fragment, int dotIndex)
        {
            if (string.IsNullOrWhiteSpace(fragment))
            {
                return false;
            }

            // C:\, C:/, .\, ./, ..\, ../, and anything containing a slash is path-ish, not member-ish.
            if (fragment.Contains('\\', StringComparison.Ordinal) || fragment.Contains('/', StringComparison.Ordinal))
            {
                return true;
            }

            if (fragment.Length >= 2 && char.IsLetter(fragment[0]) && fragment[1] == ':')
            {
                return true;
            }

            if (dotIndex == 0 || (dotIndex == 1 && fragment[0] == '.'))
            {
                return true;
            }

            return false;
        }

        private static StaticMemberCompletionContext? TryGetStaticMemberCompletionContext(string documentText, CompletionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Fragment))
            {
                return null;
            }

            var separatorIndex = context.Fragment.LastIndexOf("::", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return null;
            }

            var typeText = context.Fragment[..separatorIndex].Trim();
            if (string.IsNullOrWhiteSpace(typeText))
            {
                return null;
            }

            var memberFragment = separatorIndex + 2 < context.Fragment.Length
                ? context.Fragment[(separatorIndex + 2)..]
                : string.Empty;

            var memberReplacementOffset = context.ReplacementOffset + separatorIndex + 2;
            var memberReplacementLength = Math.Max(0, context.ReplacementLength - separatorIndex - 2);

            return new StaticMemberCompletionContext(
                NormalizeTypeExpression(typeText),
                memberFragment,
                context.ReplacementOffset,
                memberReplacementOffset,
                memberReplacementLength);
        }

        private static string NormalizeTypeExpression(string typeText)
        {
            if (string.IsNullOrWhiteSpace(typeText))
            {
                return string.Empty;
            }

            var normalized = typeText.Trim();
            if (normalized.StartsWith("[", StringComparison.Ordinal) && normalized.EndsWith("]", StringComparison.Ordinal))
            {
                normalized = normalized[1..^1].Trim();
            }

            return normalized;
        }

        private static IEnumerable<MemberCompletionCandidate> GetLocalStaticMemberCompletionCandidates(
            string documentText,
            StaticMemberCompletionContext context)
        {
            var typeName = NormalizeTypeExpression(context.TypeText);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in GetStaticMembersForType(typeName))
            {
                if (!seen.Add(member.DisplayText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(context.MemberFragment) &&
                    GetMatchScore(member.DisplayText, context.MemberFragment) < 0 &&
                    GetMatchScore(member.CompletionText, context.MemberFragment) < 0)
                {
                    continue;
                }

                yield return member;
            }

            foreach (var member in GetStaticMembersForDocumentClass(documentText, typeName))
            {
                if (!seen.Add(member.DisplayText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(context.MemberFragment) &&
                    GetMatchScore(member.DisplayText, context.MemberFragment) < 0 &&
                    GetMatchScore(member.CompletionText, context.MemberFragment) < 0)
                {
                    continue;
                }

                yield return member;
            }
        }

        private static IEnumerable<MemberCompletionCandidate> GetLocalMemberCompletionCandidates(
            string documentText,
            MemberCompletionContext context)
        {
            var objectKind = InferObjectKindForMemberCompletion(documentText, context);
            if (string.IsNullOrWhiteSpace(objectKind))
            {
                yield break;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in GetInferredDocumentMemberCandidates(documentText, context, objectKind))
            {
                if (!seen.Add(member.DisplayText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(context.MemberFragment) &&
                    GetMatchScore(member.DisplayText, context.MemberFragment) < 0 &&
                    GetMatchScore(member.CompletionText, context.MemberFragment) < 0)
                {
                    continue;
                }

                yield return member;
            }

            foreach (var member in GetMembersForObjectKind(objectKind))
            {
                if (!seen.Add(member.DisplayText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(context.MemberFragment) &&
                    GetMatchScore(member.DisplayText, context.MemberFragment) < 0 &&
                    GetMatchScore(member.CompletionText, context.MemberFragment) < 0)
                {
                    continue;
                }

                yield return member;
            }

            foreach (var member in GetMembersForObjectKind("Object"))
            {
                if (!seen.Add(member.DisplayText))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(context.MemberFragment) &&
                    GetMatchScore(member.DisplayText, context.MemberFragment) < 0 &&
                    GetMatchScore(member.CompletionText, context.MemberFragment) < 0)
                {
                    continue;
                }

                yield return member;
            }
        }

        private static string? InferObjectKindForMemberCompletion(string documentText, MemberCompletionContext context)
        {
            var expression = (context.ExpressionText ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            var unquotedExpression = expression.Trim('(', ')', ' ', '\t');
            if (unquotedExpression.StartsWith("$", StringComparison.Ordinal))
            {
                if ((string.Equals(unquotedExpression, "$_", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(unquotedExpression, "$PSItem", StringComparison.OrdinalIgnoreCase)) &&
                    TryInferPipelineObjectKind(documentText, context.ExpressionOffset, out var pipelineKind))
                {
                    return pipelineKind;
                }

                if (TryInferAutomaticVariableKind(unquotedExpression, out var automaticKind))
                {
                    return automaticKind;
                }

                return InferVariableKindFromAssignments(documentText, unquotedExpression, context.ExpressionOffset);
            }

            if (unquotedExpression.StartsWith("'", StringComparison.Ordinal) ||
                unquotedExpression.StartsWith("\"", StringComparison.Ordinal))
            {
                return "String";
            }

            if (unquotedExpression.StartsWith("@{", StringComparison.Ordinal))
            {
                return "Hashtable";
            }

            if (unquotedExpression.StartsWith("@(", StringComparison.Ordinal))
            {
                return "Array";
            }

            if (Regex.IsMatch(unquotedExpression, @"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant))
            {
                return "Number";
            }

            return InferKindFromExpressionText(unquotedExpression);
        }

        private static bool TryInferAutomaticVariableKind(string variableName, out string objectKind)
        {
            objectKind = variableName.ToLowerInvariant() switch
            {
                "$_" => "Object",
                "$psitem" => "Object",
                "$psversiontable" => "Hashtable",
                "$matches" => "Hashtable",
                "$psboundparameters" => "Hashtable",
                "$args" => "Array",
                "$input" => "Array",
                "$error" => "Collection",
                "$pwd" => "PathInfo",
                "$host" => "PSHost",
                "$myinvocation" => "InvocationInfo",
                "$psstyle" => "PSStyle",
                _ => string.Empty
            };

            return !string.IsNullOrWhiteSpace(objectKind);
        }

        private static string? InferVariableKindFromAssignments(string documentText, string variableName, int beforeOffset)
        {
            if (string.IsNullOrWhiteSpace(documentText) || string.IsNullOrWhiteSpace(variableName))
            {
                return null;
            }

            var safeLength = Math.Clamp(beforeOffset, 0, documentText.Length);
            var priorText = documentText[..safeLength];
            var bareName = Regex.Escape(variableName.TrimStart('$'));
            var assignmentPattern = @"(?im)(?:\[(?<type>[^\]]+)\]\s*)?\$" + bareName + @"\s*=\s*(?<rhs>[^\r\n;]+)";
            var matches = Regex.Matches(priorText, assignmentPattern);
            if (matches.Count == 0)
            {
                return null;
            }

            var match = matches[matches.Count - 1];
            var explicitType = match.Groups["type"].Success ? match.Groups["type"].Value : string.Empty;
            if (!string.IsNullOrWhiteSpace(explicitType))
            {
                var explicitKind = MapTypeNameToObjectKind(explicitType);
                if (!string.IsNullOrWhiteSpace(explicitKind))
                {
                    return explicitKind;
                }
            }

            var rhs = match.Groups["rhs"].Value.Trim();
            if (rhs.StartsWith("'", StringComparison.Ordinal) || rhs.StartsWith("\"", StringComparison.Ordinal)) return "String";
            if (Regex.IsMatch(rhs, @"^\[\s*pscustomobject\s*\]\s*@\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "PSCustomObject";
            if (rhs.StartsWith("@{", StringComparison.Ordinal)) return "Hashtable";
            if (rhs.StartsWith("@(", StringComparison.Ordinal)) return "Array";
            if (Regex.IsMatch(rhs, @"^-?\d+(\.\d+)?$", RegexOptions.CultureInvariant)) return "Number";

            var customClassName = InferCustomClassNameFromExpression(rhs);
            if (!string.IsNullOrWhiteSpace(customClassName) && DocumentDefinesClass(documentText, customClassName))
            {
                return "CustomClass:" + customClassName;
            }

            return InferKindFromExpressionText(rhs);
        }

        private static string? MapTypeNameToObjectKind(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            var normalized = typeName.Trim().TrimStart('[').TrimEnd(']');
            if (normalized.Contains("string", StringComparison.OrdinalIgnoreCase)) return "String";
            if (normalized.Contains("hashtable", StringComparison.OrdinalIgnoreCase)) return "Hashtable";
            if (normalized.Contains("dictionary", StringComparison.OrdinalIgnoreCase)) return "Hashtable";
            if (normalized.Contains("array", StringComparison.OrdinalIgnoreCase)) return "Array";
            if (normalized.Contains("object[]", StringComparison.OrdinalIgnoreCase)) return "Array";
            if (normalized.Contains("datetime", StringComparison.OrdinalIgnoreCase)) return "DateTime";
            if (normalized.Contains("int", StringComparison.OrdinalIgnoreCase) || normalized.Contains("double", StringComparison.OrdinalIgnoreCase) || normalized.Contains("decimal", StringComparison.OrdinalIgnoreCase)) return "Number";
            if (normalized.Contains("process", StringComparison.OrdinalIgnoreCase)) return "Process";
            if (normalized.Contains("servicecontroller", StringComparison.OrdinalIgnoreCase)) return "Service";
            if (normalized.Contains("directoryinfo", StringComparison.OrdinalIgnoreCase)) return "DirectoryInfo";
            if (normalized.Contains("fileinfo", StringComparison.OrdinalIgnoreCase)) return "FileInfo";
            if (normalized.Contains("filesysteminfo", StringComparison.OrdinalIgnoreCase)) return "FileSystemInfo";
            if (normalized.Contains("pscustomobject", StringComparison.OrdinalIgnoreCase)) return "PSCustomObject";
            return null;
        }

        private static string? InferKindFromExpressionText(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            if (Regex.IsMatch(expression, @"\bGet-Process\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Process";
            if (Regex.IsMatch(expression, @"\bGet-Service\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "Service";
            if (Regex.IsMatch(expression, @"\bGet-Date\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "DateTime";
            if (Regex.IsMatch(expression, @"\b(Get-ChildItem|Get-Item)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "FileSystemInfo";
            if (Regex.IsMatch(expression, @"\b(Invoke-RestMethod|Invoke-WebRequest|ConvertFrom-Json)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "PSCustomObject";
            return null;
        }

        private static IEnumerable<MemberCompletionCandidate> GetInferredDocumentMemberCandidates(
            string documentText,
            MemberCompletionContext context,
            string objectKind)
        {
            foreach (var property in ExtractPSCustomObjectPropertiesForExpression(documentText, context))
            {
                yield return new MemberCompletionCandidate(
                    property,
                    property,
                    "Inferred PSCustomObject property from this script: " + property,
                    CompletionItemKind.Property,
                    98);
            }

            var className = objectKind.StartsWith("CustomClass:", StringComparison.OrdinalIgnoreCase)
                ? objectKind["CustomClass:".Length..]
                : TryInferCustomClassNameForExpression(documentText, context);

            if (!string.IsNullOrWhiteSpace(className))
            {
                foreach (var member in GetInstanceMembersForDocumentClass(documentText, className))
                {
                    yield return member;
                }
            }
        }

        private static IEnumerable<MemberCompletionCandidate> GetStaticMembersForType(string typeName)
        {
            var normalized = NormalizeTypeExpression(typeName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                yield break;
            }

            foreach (var member in GetStaticMembersForNormalizedType(normalized))
            {
                yield return member;
            }
        }

        private static IEnumerable<MemberCompletionCandidate> GetStaticMembersForNormalizedType(string normalizedTypeName)
        {
            var simple = normalizedTypeName.Split('.').Last();
            var key = simple.ToLowerInvariant();

            string[] specs = key switch
            {
                "datetime" => new[]
                {
                    "Now|P|Current local date and time.",
                    "UtcNow|P|Current UTC date and time.",
                    "Today|P|Current local date.",
                    "MinValue|P|Smallest supported DateTime value.",
                    "MaxValue|P|Largest supported DateTime value.",
                    "Parse(|M|Parses text into a DateTime.",
                    "TryParse(|M|Attempts to parse text into a DateTime.",
                    "DaysInMonth(|M|Gets the number of days in a month.",
                    "IsLeapYear(|M|Determines whether a year is a leap year."
                },
                "timespan" => new[]
                {
                    "Zero|P|A zero-length TimeSpan.",
                    "MaxValue|P|Largest supported TimeSpan value.",
                    "MinValue|P|Smallest supported TimeSpan value.",
                    "FromDays(|M|Creates a TimeSpan from days.",
                    "FromHours(|M|Creates a TimeSpan from hours.",
                    "FromMinutes(|M|Creates a TimeSpan from minutes.",
                    "FromSeconds(|M|Creates a TimeSpan from seconds.",
                    "Parse(|M|Parses text into a TimeSpan.",
                    "TryParse(|M|Attempts to parse text into a TimeSpan."
                },
                "math" => new[]
                {
                    "PI|P|The value of pi.",
                    "E|P|The natural logarithmic base.",
                    "Abs(|M|Returns an absolute value.",
                    "Ceiling(|M|Rounds up.",
                    "Floor(|M|Rounds down.",
                    "Round(|M|Rounds a value.",
                    "Max(|M|Returns the larger value.",
                    "Min(|M|Returns the smaller value.",
                    "Pow(|M|Raises a value to a power.",
                    "Sqrt(|M|Returns the square root."
                },
                "guid" => new[]
                {
                    "Empty|P|The all-zero GUID.",
                    "NewGuid(|M|Creates a new GUID.",
                    "Parse(|M|Parses text into a GUID.",
                    "TryParse(|M|Attempts to parse text into a GUID."
                },
                "environment" => new[]
                {
                    "MachineName|P|Computer name.",
                    "OSVersion|P|Operating system version.",
                    "ProcessorCount|P|Logical processor count.",
                    "UserName|P|Current user name.",
                    "CurrentDirectory|P|Current process directory.",
                    "NewLine|P|Platform newline string.",
                    "GetEnvironmentVariable(|M|Gets an environment variable.",
                    "GetFolderPath(|M|Gets a special folder path.",
                    "Exit(|M|Terminates the process."
                },
                "file" => new[]
                {
                    "Exists(|M|Determines whether a file exists.",
                    "ReadAllText(|M|Reads a file as text.",
                    "ReadAllLines(|M|Reads all lines from a file.",
                    "WriteAllText(|M|Writes text to a file.",
                    "WriteAllLines(|M|Writes lines to a file.",
                    "Copy(|M|Copies a file.",
                    "Move(|M|Moves a file.",
                    "Delete(|M|Deletes a file.",
                    "OpenRead(|M|Opens a file for reading."
                },
                "directory" => new[]
                {
                    "Exists(|M|Determines whether a directory exists.",
                    "GetFiles(|M|Gets files in a directory.",
                    "GetDirectories(|M|Gets child directories.",
                    "EnumerateFiles(|M|Enumerates files lazily.",
                    "EnumerateDirectories(|M|Enumerates directories lazily.",
                    "CreateDirectory(|M|Creates a directory.",
                    "Delete(|M|Deletes a directory.",
                    "Move(|M|Moves a directory."
                },
                "path" => new[]
                {
                    "DirectorySeparatorChar|P|Platform directory separator.",
                    "PathSeparator|P|Platform path-list separator.",
                    "GetFileName(|M|Gets the file name from a path.",
                    "GetDirectoryName(|M|Gets the directory from a path.",
                    "GetExtension(|M|Gets a path extension.",
                    "ChangeExtension(|M|Changes a path extension.",
                    "Combine(|M|Combines path segments.",
                    "JoinPath(|M|Joins path segments.",
                    "GetTempPath(|M|Gets the temp folder.",
                    "GetTempFileName(|M|Creates a temp file name."
                },
                "regex" => new[]
                {
                    "Escape(|M|Escapes regex metacharacters.",
                    "Unescape(|M|Unescapes regex text.",
                    "IsMatch(|M|Tests whether text matches a pattern.",
                    "Match(|M|Finds the first regex match.",
                    "Matches(|M|Finds all regex matches.",
                    "Replace(|M|Replaces matches.",
                    "Split(|M|Splits text by pattern."
                },
                "convert" => new[]
                {
                    "ToBoolean(|M|Converts a value to Boolean.",
                    "ToInt32(|M|Converts a value to Int32.",
                    "ToInt64(|M|Converts a value to Int64.",
                    "ToString(|M|Converts a value to string.",
                    "ToBase64String(|M|Converts bytes to base64.",
                    "FromBase64String(|M|Converts base64 to bytes."
                },
                "console" => new[]
                {
                    "ForegroundColor|P|Console foreground color.",
                    "BackgroundColor|P|Console background color.",
                    "WindowWidth|P|Console window width.",
                    "WindowHeight|P|Console window height.",
                    "ReadLine(|M|Reads a line from standard input.",
                    "WriteLine(|M|Writes a line to standard output.",
                    "Clear(|M|Clears the console."
                },
                _ => Array.Empty<string>()
            };

            foreach (var member in MemberSet(normalizedTypeName, specs))
            {
                yield return member;
            }
        }

        private static IEnumerable<string> ExtractPSCustomObjectPropertiesForExpression(
            string documentText,
            MemberCompletionContext context)
        {
            var expression = (context.ExpressionText ?? string.Empty).Trim();
            if (!expression.StartsWith("$", StringComparison.Ordinal))
            {
                yield break;
            }

            var variableName = expression.Trim('(', ')', ' ', '\t');
            var literal = FindLastVariableAssignmentRhs(documentText, variableName, context.ExpressionOffset);
            if (string.IsNullOrWhiteSpace(literal) ||
                !Regex.IsMatch(literal, @"^\[\s*pscustomobject\s*\]\s*@\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                yield break;
            }

            foreach (var property in ExtractHashtableLikePropertyNames(literal))
            {
                yield return property;
            }
        }

        private static string? FindLastVariableAssignmentRhs(string documentText, string variableName, int beforeOffset)
        {
            if (string.IsNullOrWhiteSpace(documentText) || string.IsNullOrWhiteSpace(variableName))
            {
                return null;
            }

            var safeLength = Math.Clamp(beforeOffset, 0, documentText.Length);
            var priorText = documentText[..safeLength];
            var bareName = Regex.Escape(variableName.TrimStart('$'));
            var assignmentPattern = @"(?im)(?:\[[^\]]+\]\s*)?\$" + bareName + @"\s*=\s*";
            var matches = Regex.Matches(priorText, assignmentPattern);
            if (matches.Count == 0)
            {
                return null;
            }

            var match = matches[matches.Count - 1];
            var rhsStart = match.Index + match.Length;
            if (rhsStart >= priorText.Length)
            {
                return string.Empty;
            }

            var remaining = priorText[rhsStart..].TrimStart();
            if (remaining.Length == 0)
            {
                return string.Empty;
            }

            if (Regex.IsMatch(remaining, @"^\[\s*pscustomobject\s*\]\s*@\{", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
                remaining.StartsWith("@{", StringComparison.Ordinal))
            {
                var openIndex = remaining.IndexOf('{');
                if (openIndex >= 0)
                {
                    var depth = 0;
                    for (var i = openIndex; i < remaining.Length; i++)
                    {
                        if (remaining[i] == '{') depth++;
                        else if (remaining[i] == '}')
                        {
                            depth--;
                            if (depth == 0)
                            {
                                return remaining[..(i + 1)].Trim();
                            }
                        }
                    }

                    return remaining.Trim();
                }
            }

            var lineEnd = remaining.IndexOfAny(new[] { '\r', '\n', ';' });
            return lineEnd >= 0 ? remaining[..lineEnd].Trim() : remaining.Trim();
        }

        private static IEnumerable<string> ExtractHashtableLikePropertyNames(string literal)
        {
            if (string.IsNullOrWhiteSpace(literal))
            {
                yield break;
            }

            foreach (Match match in Regex.Matches(
                         literal,
                         @"(?im)(?:^|[;,{])\s*['""]?(?<name>[A-Za-z_][\w-]*)['""]?\s*=",
                         RegexOptions.CultureInvariant))
            {
                var name = match.Groups["name"].Value;
                if (!string.IsNullOrWhiteSpace(name))
                {
                    yield return name;
                }
            }
        }

        private static bool TryInferPipelineObjectKind(string documentText, int beforeOffset, out string objectKind)
        {
            objectKind = string.Empty;
            if (string.IsNullOrWhiteSpace(documentText))
            {
                return false;
            }

            var safeOffset = Math.Clamp(beforeOffset, 0, documentText.Length);
            var priorText = documentText[..safeOffset];
            var pipeIndex = priorText.LastIndexOf('|');
            if (pipeIndex < 0)
            {
                return false;
            }

            var statementStart = FindStatementStart(documentText, pipeIndex);
            var leftPipeline = documentText.Substring(statementStart, pipeIndex - statementStart);
            var leftCommand = leftPipeline.Split('|').LastOrDefault()?.Trim() ?? string.Empty;
            var inferred = InferKindFromExpressionText(leftCommand);
            if (string.IsNullOrWhiteSpace(inferred))
            {
                return false;
            }

            objectKind = inferred;
            return true;
        }

        private static string? InferCustomClassNameFromExpression(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                return null;
            }

            var newMatch = Regex.Match(expression, @"^\[(?<type>[A-Za-z_][\w.]*)\]\s*::\s*new\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (newMatch.Success)
            {
                return newMatch.Groups["type"].Value.Split('.').Last();
            }

            var newObjectMatch = Regex.Match(expression, @"^New-Object\s+(?<type>[A-Za-z_][\w.]*)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return newObjectMatch.Success ? newObjectMatch.Groups["type"].Value.Split('.').Last() : null;
        }

        private static string? TryInferCustomClassNameForExpression(string documentText, MemberCompletionContext context)
        {
            var expression = (context.ExpressionText ?? string.Empty).Trim().Trim('(', ')', ' ', '\t');
            if (!expression.StartsWith("$", StringComparison.Ordinal))
            {
                return null;
            }

            var variableName = expression;
            var safeLength = Math.Clamp(context.ExpressionOffset, 0, documentText.Length);
            var priorText = documentText[..safeLength];
            var bareName = Regex.Escape(variableName.TrimStart('$'));

            var typedAssignment = Regex.Matches(priorText, @"(?im)\[(?<type>[A-Za-z_][\w.]*)\]\s*\$" + bareName + @"\b");
            if (typedAssignment.Count > 0)
            {
                var typeName = typedAssignment[typedAssignment.Count - 1].Groups["type"].Value.Split('.').Last();
                if (DocumentDefinesClass(documentText, typeName))
                {
                    return typeName;
                }
            }

            var rhs = FindLastVariableAssignmentRhs(documentText, variableName, context.ExpressionOffset);
            var inferred = InferCustomClassNameFromExpression(rhs ?? string.Empty);
            return !string.IsNullOrWhiteSpace(inferred) && DocumentDefinesClass(documentText, inferred)
                ? inferred
                : null;
        }

        private static bool DocumentDefinesClass(string documentText, string className)
        {
            if (string.IsNullOrWhiteSpace(documentText) || string.IsNullOrWhiteSpace(className))
            {
                return false;
            }

            return Regex.IsMatch(documentText, @"(?im)\bclass\s+" + Regex.Escape(className) + @"\b", RegexOptions.CultureInvariant);
        }

        private static string? ExtractDocumentClassBody(string documentText, string className)
        {
            if (string.IsNullOrWhiteSpace(documentText) || string.IsNullOrWhiteSpace(className))
            {
                return null;
            }

            var match = Regex.Match(documentText, @"(?im)\bclass\s+" + Regex.Escape(className) + @"\b[^{]*\{", RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return null;
            }

            var bodyStart = match.Index + match.Length;
            var depth = 1;
            for (var i = bodyStart; i < documentText.Length; i++)
            {
                if (documentText[i] == '{') depth++;
                else if (documentText[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return documentText.Substring(bodyStart, i - bodyStart);
                    }
                }
            }

            return documentText[bodyStart..];
        }

        private static IEnumerable<MemberCompletionCandidate> GetInstanceMembersForDocumentClass(string documentText, string className)
        {
            var body = ExtractDocumentClassBody(documentText, className);
            if (string.IsNullOrWhiteSpace(body))
            {
                yield break;
            }

            foreach (var property in ExtractClassProperties(body, includeStatic: false))
            {
                yield return new MemberCompletionCandidate(
                    property,
                    property,
                    $"{className} property from class definition: {property}",
                    CompletionItemKind.Property,
                    99);
            }

            foreach (var method in ExtractClassMethods(body, includeStatic: false))
            {
                yield return new MemberCompletionCandidate(
                    method + "(",
                    method + "()",
                    $"{className} method from class definition: {method}()",
                    CompletionItemKind.Method,
                    97);
            }
        }

        private static IEnumerable<MemberCompletionCandidate> GetStaticMembersForDocumentClass(string documentText, string className)
        {
            var body = ExtractDocumentClassBody(documentText, className.Split('.').Last());
            if (string.IsNullOrWhiteSpace(body))
            {
                yield break;
            }

            foreach (var property in ExtractClassProperties(body, includeStatic: true))
            {
                yield return new MemberCompletionCandidate(
                    property,
                    property,
                    $"{className} static property from class definition: {property}",
                    CompletionItemKind.Property,
                    99);
            }

            foreach (var method in ExtractClassMethods(body, includeStatic: true))
            {
                yield return new MemberCompletionCandidate(
                    method + "(",
                    method + "()",
                    $"{className} static method from class definition: {method}()",
                    CompletionItemKind.Method,
                    97);
            }
        }

        private static IEnumerable<string> ExtractClassProperties(string body, bool includeStatic)
        {
            foreach (Match match in Regex.Matches(
                         body,
                         @"(?im)^\s*(?<static>static\s+)?(?:hidden\s+)?(?:\[[^\]]+\]\s*)?\$(?<name>[A-Za-z_][\w]*)\b",
                         RegexOptions.CultureInvariant))
            {
                var isStatic = match.Groups["static"].Success;
                if (isStatic == includeStatic)
                {
                    yield return match.Groups["name"].Value;
                }
            }
        }

        private static IEnumerable<string> ExtractClassMethods(string body, bool includeStatic)
        {
            foreach (Match match in Regex.Matches(
                         body,
                         @"(?im)^\s*(?<static>static\s+)?(?:hidden\s+)?(?:\[[^\]]+\]\s*)?(?<name>[A-Za-z_][\w]*)\s*\(",
                         RegexOptions.CultureInvariant))
            {
                var name = match.Groups["name"].Value;
                if (string.Equals(name, "if", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "for", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "foreach", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "switch", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, "while", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var isStatic = match.Groups["static"].Success;
                if (isStatic == includeStatic)
                {
                    yield return name;
                }
            }
        }

        private static IEnumerable<MemberCompletionCandidate> GetMembersForObjectKind(string objectKind)
        {
            if (string.IsNullOrWhiteSpace(objectKind))
            {
                yield break;
            }

            switch (objectKind)
            {
                case "String":
                    foreach (var member in MemberSet("String", new[]
                    {
                        "Length|P|String length.",
                        "Contains(|M|Tests whether the string contains a value.",
                        "StartsWith(|M|Tests whether the string starts with a value.",
                        "EndsWith(|M|Tests whether the string ends with a value.",
                        "IndexOf(|M|Returns the index of a value.",
                        "Replace(|M|Replaces text in the string.",
                        "Split(|M|Splits the string into an array.",
                        "Substring(|M|Returns part of the string.",
                        "Trim(|M|Removes leading and trailing whitespace.",
                        "ToLower(|M|Returns a lowercase copy.",
                        "ToUpper(|M|Returns an uppercase copy."
                    })) yield return member;
                    break;

                case "Hashtable":
                    foreach (var member in MemberSet("Hashtable", new[]
                    {
                        "Count|P|Number of entries.",
                        "Keys|P|Key collection.",
                        "Values|P|Value collection.",
                        "ContainsKey(|M|Tests whether a key exists.",
                        "Add(|M|Adds a key/value pair.",
                        "Remove(|M|Removes a key.",
                        "Clear(|M|Removes all entries.",
                        "GetEnumerator(|M|Returns an enumerator."
                    })) yield return member;
                    break;

                case "Array":
                case "Collection":
                    foreach (var member in MemberSet("Collection", new[]
                    {
                        "Count|P|Number of items.",
                        "Length|P|Array length, when available.",
                        "ForEach(|M|Runs a script block for each item.",
                        "Where(|M|Filters items with a script block.",
                        "GetEnumerator(|M|Returns an enumerator."
                    })) yield return member;
                    break;

                case "DateTime":
                    foreach (var member in MemberSet("DateTime", new[]
                    {
                        "Date|P|Date component.",
                        "Day|P|Day of month.",
                        "DayOfWeek|P|Day of week.",
                        "Hour|P|Hour component.",
                        "Minute|P|Minute component.",
                        "Month|P|Month component.",
                        "Second|P|Second component.",
                        "Year|P|Year component.",
                        "AddDays(|M|Adds days.",
                        "AddHours(|M|Adds hours.",
                        "ToString(|M|Formats the date/time."
                    })) yield return member;
                    break;

                case "Process":
                    foreach (var member in MemberSet("Process", new[]
                    {
                        "Id|P|Process ID.",
                        "Name|P|Process name.",
                        "ProcessName|P|Process name.",
                        "CPU|P|CPU time, when available.",
                        "WorkingSet|P|Working set size.",
                        "StartTime|P|Process start time.",
                        "Path|P|Executable path, when available.",
                        "Kill(|M|Terminates the process.",
                        "WaitForExit(|M|Waits for the process to exit.",
                        "CloseMainWindow(|M|Requests the main window to close."
                    })) yield return member;
                    break;

                case "Service":
                    foreach (var member in MemberSet("ServiceController", new[]
                    {
                        "Name|P|Service name.",
                        "DisplayName|P|Display name.",
                        "Status|P|Current service status.",
                        "ServiceType|P|Service type.",
                        "CanStop|P|Whether the service can be stopped.",
                        "Start(|M|Starts the service.",
                        "Stop(|M|Stops the service.",
                        "Pause(|M|Pauses the service.",
                        "Refresh(|M|Refreshes service state."
                    })) yield return member;
                    break;

                case "FileSystemInfo":
                case "FileInfo":
                case "DirectoryInfo":
                    foreach (var member in MemberSet(objectKind, new[]
                    {
                        "Name|P|File or folder name.",
                        "FullName|P|Full filesystem path.",
                        "Extension|P|File extension.",
                        "Exists|P|Whether the item exists.",
                        "CreationTime|P|Creation time.",
                        "LastWriteTime|P|Last write time.",
                        "Length|P|File length, when applicable.",
                        "Delete(|M|Deletes the item.",
                        "MoveTo(|M|Moves the item.",
                        "CopyTo(|M|Copies the file, when applicable."
                    })) yield return member;
                    break;

                case "PathInfo":
                    foreach (var member in MemberSet("PathInfo", new[]
                    {
                        "Path|P|Current path.",
                        "ProviderPath|P|Provider-specific path.",
                        "Provider|P|Current provider.",
                        "Drive|P|Current drive."
                    })) yield return member;
                    break;

                case "PSHost":
                    foreach (var member in MemberSet("PSHost", new[]
                    {
                        "Name|P|Host name.",
                        "Version|P|Host version.",
                        "UI|P|Host user interface.",
                        "CurrentCulture|P|Current culture.",
                        "CurrentUICulture|P|Current UI culture."
                    })) yield return member;
                    break;

                case "InvocationInfo":
                    foreach (var member in MemberSet("InvocationInfo", new[]
                    {
                        "MyCommand|P|Command information.",
                        "BoundParameters|P|Bound parameters.",
                        "Line|P|Source line.",
                        "ScriptName|P|Script path.",
                        "PSScriptRoot|P|Script root.",
                        "PSCommandPath|P|Script command path."
                    })) yield return member;
                    break;

                case "PSStyle":
                    foreach (var member in MemberSet("PSStyle", new[]
                    {
                        "Reset|P|ANSI reset sequence.",
                        "Bold|P|ANSI bold sequence.",
                        "Italic|P|ANSI italic sequence.",
                        "Underline|P|ANSI underline sequence.",
                        "Foreground|P|Foreground colors.",
                        "Background|P|Background colors.",
                        "Formatting|P|Formatting settings."
                    })) yield return member;
                    break;

                case "Number":
                    foreach (var member in MemberSet("Number", new[]
                    {
                        "CompareTo(|M|Compares this number to another value.",
                        "Equals(|M|Tests equality.",
                        "GetType(|M|Gets the runtime type.",
                        "ToString(|M|Converts the number to text."
                    })) yield return member;
                    break;

                case "PSCustomObject":
                    foreach (var member in MemberSet("PSCustomObject", new[]
                    {
                        "PSObject|P|PowerShell extended object adapter.",
                        "PSTypeNames|P|PowerShell type names.",
                        "ToString(|M|Converts the object to text.",
                        "GetType(|M|Gets the runtime type."
                    })) yield return member;
                    break;

                case "Object":
                    foreach (var member in MemberSet("Object", new[]
                    {
                        "PSObject|P|PowerShell extended object adapter.",
                        "PSTypeNames|P|PowerShell type names.",
                        "GetType(|M|Gets the runtime type.",
                        "ToString(|M|Converts the object to text.",
                        "Equals(|M|Tests equality.",
                        "GetHashCode(|M|Gets the object hash code."
                    })) yield return member;
                    break;
            }
        }

        private static IEnumerable<MemberCompletionCandidate> MemberSet(string typeLabel, IEnumerable<string> specs)
        {
            foreach (var spec in specs)
            {
                var parts = spec.Split('|');
                if (parts.Length < 3)
                {
                    continue;
                }

                var completionText = parts[0];
                var isMethod = string.Equals(parts[1], "M", StringComparison.OrdinalIgnoreCase);
                var displayText = isMethod && completionText.EndsWith("(", StringComparison.Ordinal)
                    ? completionText[..^1] + "()"
                    : completionText;
                yield return new MemberCompletionCandidate(
                    completionText,
                    displayText,
                    $"{typeLabel} {(isMethod ? "method" : "property")}: {displayText}. {parts[2]}",
                    isMethod ? CompletionItemKind.Method : CompletionItemKind.Property,
                    isMethod ? 92 : 94);
            }
        }

        private static bool ShouldOfferSnippets(CompletionContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Fragment)) return true;
            return !context.Fragment.StartsWith('$') && !context.Fragment.StartsWith('-');
        }

        private static double GetKindPriority(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.ParameterName => 9,
            CompletionItemKind.ParameterValue => 8,
            CompletionItemKind.Method => 7,
            CompletionItemKind.Property => 7,
            CompletionItemKind.Variable => 6,
            CompletionItemKind.Command => 5,
            CompletionItemKind.ProviderContainer => 4,
            CompletionItemKind.ProviderItem => 3,
            CompletionItemKind.Type => 2,
            CompletionItemKind.Keyword => 1,
            _ => 0,
        };

        private static int GetMatchScore(string value, string fragment)
        {
            if (string.IsNullOrWhiteSpace(fragment)) return 0;
            if (string.IsNullOrWhiteSpace(value)) return -1;

            var normalizedValue = NormalizeForMatching(value);
            var normalizedFragment = NormalizeForMatching(fragment);
            if (normalizedFragment.Length == 0) return 0;

            if (normalizedValue.StartsWith(normalizedFragment, StringComparison.OrdinalIgnoreCase)) return 100;
            if (MatchesPowerShellVerbNounPattern(normalizedValue, normalizedFragment)) return 94;
            if (MatchesWordStart(normalizedValue, normalizedFragment)) return 88;
            if (normalizedValue.Contains(normalizedFragment, StringComparison.OrdinalIgnoreCase)) return 70;
            if (IsSubsequence(normalizedValue, normalizedFragment)) return 45;
            return -1;
        }

        private static string NormalizeForMatching(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var builder = new StringBuilder(value.Length);
            foreach (var ch in value.Trim())
            {
                if (ch is '`' or '\'' or '"') continue;
                builder.Append(ch);
            }
            return builder.ToString();
        }

        private static bool MatchesPowerShellVerbNounPattern(string value, string fragment)
        {
            var valueDash = value.IndexOf('-', StringComparison.Ordinal);
            var fragmentDash = fragment.IndexOf('-', StringComparison.Ordinal);
            if (valueDash <= 0 || fragmentDash <= 0) return false;

            var valueVerb = value[..valueDash];
            var valueNoun = value[(valueDash + 1)..];
            var fragmentVerb = fragment[..fragmentDash];
            var fragmentNoun = fragment[(fragmentDash + 1)..];

            return valueVerb.StartsWith(fragmentVerb, StringComparison.OrdinalIgnoreCase) &&
                   valueNoun.Contains(fragmentNoun, StringComparison.OrdinalIgnoreCase);
        }

        private static bool MatchesWordStart(string value, string fragment)
        {
            if (fragment.Length == 0) return true;

            for (var i = 0; i < value.Length; i++)
            {
                if (i > 0 && value[i - 1] != '-' && value[i - 1] != '_' && value[i - 1] != '.' && !char.IsWhiteSpace(value[i - 1]))
                {
                    continue;
                }

                if (value.AsSpan(i).StartsWith(fragment.AsSpan(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsSubsequence(string value, string fragment)
        {
            var fragmentIndex = 0;
            foreach (var ch in value)
            {
                if (fragmentIndex < fragment.Length && char.ToUpperInvariant(ch) == char.ToUpperInvariant(fragment[fragmentIndex]))
                {
                    fragmentIndex++;
                    if (fragmentIndex == fragment.Length) return true;
                }
            }

            return false;
        }

        private static IEnumerable<string> ExtractDocumentVariables(string documentText)
        {
            if (string.IsNullOrWhiteSpace(documentText)) return Array.Empty<string>();
            return VariableRegex.Matches(documentText)
                .Select(m => m.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ExtractDocumentFunctions(string documentText)
        {
            if (string.IsNullOrWhiteSpace(documentText)) return Array.Empty<string>();
            return FunctionRegex.Matches(documentText)
                .Select(m => m.Groups[1].Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> ExtractParamBlockVariables(string documentText)
        {
            if (string.IsNullOrWhiteSpace(documentText)) return Array.Empty<string>();

            return ParamNameRegex.Matches(documentText)
                .SelectMany(match => ParamVariableRegex.Matches(match.Groups["body"].Value).Select(m => m.Value))
                .Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static EditorQuickInfo? BuildLocalCommandQuickInfo(string commandName)
        {
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return null;
            }

            if (CommonCommands.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                return new EditorQuickInfo(
                    commandName,
                    "Common PowerShell command. Rich syntax help will appear when the PowerShell 7 engine responds.");
            }

            if (CommonAliases.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                return new EditorQuickInfo(
                    commandName,
                    "PowerShell alias. Rich command details will appear when the PowerShell 7 engine responds.");
            }

            if (Keywords.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                return new EditorQuickInfo(commandName, "PowerShell language keyword.");
            }

            return null;
        }

        private EditorQuickInfo? BuildBestCommandQuickInfo(
            string invokedCommandName,
            PowerShellQuickInfo? commandInfo,
            string? preferredParameterName,
            string? pwshExecutablePath,
            EditorQuickInfo? fallbackQuickInfo)
        {
            PowerShellCommandReference? commandReference = null;
            if (!string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                _completionService.TryGetCachedCommandReference(pwshExecutablePath, invokedCommandName, out commandReference);
            }

            if (commandReference is not null &&
                commandReference.IsAlias &&
                !string.IsNullOrWhiteSpace(commandReference.ResolvedCommandName))
            {
                var body = new StringBuilder();
                body.Append("PowerShell alias: ")
                    .Append(invokedCommandName)
                    .Append(" -> ")
                    .Append(commandReference.ResolvedCommandName);

                if (!string.IsNullOrWhiteSpace(commandReference.ModuleName))
                {
                    body.Append(" • Module: ").Append(commandReference.ModuleName);
                }

                if (commandInfo is not null)
                {
                    var detailedQuickInfo = BuildCommandQuickInfo(commandInfo, preferredParameterName);
                    if (!string.IsNullOrWhiteSpace(detailedQuickInfo.Body))
                    {
                        body.AppendLine();
                        body.AppendLine();
                        body.Append(detailedQuickInfo.Body);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(fallbackQuickInfo?.Body))
                {
                    body.AppendLine();
                    body.AppendLine();
                    body.Append(fallbackQuickInfo.Body);
                }

                return new EditorQuickInfo(invokedCommandName, body.ToString());
            }

            if (commandInfo is not null)
            {
                return BuildCommandQuickInfo(commandInfo, preferredParameterName);
            }

            return fallbackQuickInfo;
        }

        private async Task<EditorQuickInfo?> BuildCommandContextQuickInfoAsync(
            string documentText,
            int offset,
            string? pwshExecutablePath,
            CancellationToken cancellationToken)
        {
            var commandContext = FindCommandContextAtOffset(documentText, offset);
            if (commandContext is null)
            {
                return null;
            }

            var localQuickInfo = BuildLocalCommandQuickInfo(commandContext.CommandName);
            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return localQuickInfo;
            }

            if (_completionService.TryGetCachedCommandQuickInfo(pwshExecutablePath, commandContext.CommandName, out var cachedCommandInfo) &&
                cachedCommandInfo is not null)
            {
                return BuildBestCommandQuickInfo(commandContext.CommandName, cachedCommandInfo, commandContext.PreferredParameterName, pwshExecutablePath, localQuickInfo);
            }

            using var quickInfoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quickInfoCts.CancelAfter(TimeSpan.FromMilliseconds(350));

            try
            {
                var commandInfo = await _completionService.GetCommandQuickInfoAsync(
                        commandContext.CommandName,
                        pwshExecutablePath,
                        cancellationToken: quickInfoCts.Token)
                    .ConfigureAwait(true);

                return BuildBestCommandQuickInfo(commandContext.CommandName, commandInfo, commandContext.PreferredParameterName, pwshExecutablePath, localQuickInfo);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return localQuickInfo;
            }
        }

        private async Task<EditorQuickInfo?> BuildParameterQuickInfoAsync(
            string parameterToken,
            string documentText,
            int parameterOffset,
            string? pwshExecutablePath,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(pwshExecutablePath))
            {
                return new EditorQuickInfo(parameterToken, $"Parameter reference: {parameterToken}");
            }

            var commandName = FindCommandNameForParameter(documentText, parameterOffset);
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return new EditorQuickInfo(parameterToken, $"Parameter reference: {parameterToken}");
            }

            if (_completionService.TryGetCachedCommandQuickInfo(pwshExecutablePath, commandName, out var cachedCommandInfo) &&
                cachedCommandInfo is not null &&
                HasUsableParameterMetadata(cachedCommandInfo))
            {
                return BuildBestCommandQuickInfo(commandName, cachedCommandInfo, parameterToken.TrimStart('-'), pwshExecutablePath, null);
            }

            using var quickInfoCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            quickInfoCts.CancelAfter(TimeSpan.FromMilliseconds(350));

            PowerShellQuickInfo? commandInfo;
            try
            {
                commandInfo = await _completionService.GetCommandQuickInfoAsync(
                        commandName,
                        pwshExecutablePath,
                        requireParameters: true,
                        cancellationToken: quickInfoCts.Token)
                    .ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                commandInfo = null;
            }

            if (commandInfo is null)
            {
                return new EditorQuickInfo(parameterToken, $"Parameter reference: {parameterToken}\nCommand context: {commandName}");
            }

            return BuildBestCommandQuickInfo(commandName, commandInfo, parameterToken.TrimStart('-'), pwshExecutablePath, null);
        }

        private static EditorQuickInfo? BuildVariableQuickInfo(string variableName, string documentText)
        {
            if (AutomaticVariableDescriptions.TryGetValue(variableName, out var description))
            {
                return new EditorQuickInfo(variableName, description);
            }

            var definedInParamBlock = ExtractParamBlockVariables(documentText)
                .Contains(variableName, StringComparer.OrdinalIgnoreCase);

            if (definedInParamBlock)
            {
                return new EditorQuickInfo(variableName, $"Parameter variable declared in this script: {variableName}");
            }

            var appearsInDocument = ExtractDocumentVariables(documentText)
                .Contains(variableName, StringComparer.OrdinalIgnoreCase);

            if (appearsInDocument)
            {
                return new EditorQuickInfo(variableName, $"Variable used in this script: {variableName}");
            }

            return null;
        }

        private static EditorQuickInfo BuildCommandQuickInfo(PowerShellQuickInfo commandInfo, string? preferredParameterName)
        {
            var builder = new StringBuilder();

            if (!string.IsNullOrWhiteSpace(commandInfo.Kind))
            {
                builder.Append(commandInfo.Kind);
            }

            if (!string.IsNullOrWhiteSpace(commandInfo.ModuleName))
            {
                if (builder.Length > 0) builder.Append(" • ");
                builder.Append("Module: ").Append(commandInfo.ModuleName);
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(commandInfo.Synopsis))
            {
                builder.AppendLine(commandInfo.Synopsis.Trim());
                builder.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(commandInfo.Syntax))
            {
                builder.AppendLine("Syntax:");
                foreach (var line in commandInfo.Syntax
                             .Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n')
                             .Split('\n')
                             .Where(line => !string.IsNullOrWhiteSpace(line))
                             .Take(6))
                {
                    builder.AppendLine("  " + line.Trim());
                }
                builder.AppendLine();
            }

            var parameters = commandInfo.Parameters;
            if (!string.IsNullOrWhiteSpace(preferredParameterName))
            {
                parameters = parameters
                    .OrderByDescending(p => string.Equals(p.Name, preferredParameterName, StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(p => p.Mandatory)
                    .ThenBy(p => p.Position ?? int.MaxValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else
            {
                parameters = parameters
                    .OrderByDescending(p => p.Mandatory)
                    .ThenBy(p => p.Position ?? int.MaxValue)
                    .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            if (parameters.Count > 0)
            {
                builder.AppendLine("Parameters:");
                foreach (var parameter in parameters.Take(10))
                {
                    var marker = !string.IsNullOrWhiteSpace(preferredParameterName) &&
                                 string.Equals(parameter.Name, preferredParameterName, StringComparison.OrdinalIgnoreCase)
                        ? "▶ "
                        : "  ";

                    builder.Append(marker).Append('-').Append(parameter.Name);
                    if (!string.IsNullOrWhiteSpace(parameter.TypeName))
                    {
                        builder.Append(" <").Append(parameter.TypeName).Append('>');
                    }

                    if (parameter.Mandatory)
                    {
                        builder.Append(" required");
                    }

                    if (parameter.Aliases.Count > 0)
                    {
                        builder.Append(" aliases ").Append(string.Join(", ", parameter.Aliases.Select(alias => "-" + alias)));
                    }

                    var values = parameter.ValidValues.Count > 0 ? parameter.ValidValues : parameter.EnumValues;
                    if (values.Count > 0)
                    {
                        builder.Append(" values ").Append(string.Join(", ", values.Take(6)));
                        if (values.Count > 6) builder.Append(", …");
                    }

                    if (parameter.Position is int position)
                    {
                        builder.Append(" pos ").Append(position);
                    }

                    builder.AppendLine();
                }
            }

            return new EditorQuickInfo(commandInfo.Title, builder.ToString().Trim());
        }

        private static PowerShellToken? GetTokenAtOffset(string text, int offset)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var safeOffset = Math.Clamp(offset, 0, text.Length);
            if (safeOffset == text.Length && safeOffset > 0)
            {
                safeOffset--;
            }

            if (safeOffset < text.Length && !IsPowerShellTokenChar(text[safeOffset]) && safeOffset > 0)
            {
                safeOffset--;
            }

            if (safeOffset < 0 || safeOffset >= text.Length || !IsPowerShellTokenChar(text[safeOffset]))
            {
                return null;
            }

            var start = safeOffset;
            while (start > 0 && IsPowerShellTokenChar(text[start - 1]))
            {
                start--;
            }

            var end = safeOffset + 1;
            while (end < text.Length && IsPowerShellTokenChar(text[end]))
            {
                end++;
            }

            var tokenText = text.Substring(start, end - start);
            return string.IsNullOrWhiteSpace(tokenText)
                ? null
                : new PowerShellToken(start, end - start, tokenText);
        }

        private static bool IsPowerShellTokenChar(char ch)
        {
            return char.IsLetterOrDigit(ch) ||
                   ch == '_' ||
                   ch == '-' ||
                   ch == '$' ||
                   ch == ':' ||
                   ch == '.';
        }

        private static CommandInvocationContext? FindCommandContextAtOffset(string documentText, int offset)
        {
            if (string.IsNullOrWhiteSpace(documentText))
            {
                return null;
            }

            var safeOffset = Math.Clamp(offset, 0, documentText.Length);
            var statementStart = 0;
            for (var i = Math.Max(0, safeOffset - 1); i >= 0; i--)
            {
                var ch = documentText[i];
                if (ch is '\n' or '\r' or '|' or ';' or '{' or '}')
                {
                    statementStart = i + 1;
                    break;
                }
            }

            if (safeOffset <= statementStart)
            {
                return null;
            }

            var segment = documentText.Substring(statementStart, safeOffset - statementStart);
            if (string.IsNullOrWhiteSpace(segment) || Regex.IsMatch(segment, @"^\s*#", RegexOptions.CultureInvariant))
            {
                return null;
            }

            var commentIndex = segment.IndexOf('#');
            if (commentIndex >= 0)
            {
                segment = segment[..commentIndex];
            }

            var commandMatch = Regex.Match(segment, @"^\s*(?:&|\.)?\s*(?<cmd>[A-Za-z_][\w-]*)\b", RegexOptions.CultureInvariant);
            if (!commandMatch.Success)
            {
                return null;
            }

            var commandName = commandMatch.Groups["cmd"].Value;
            if (Keywords.Contains(commandName, StringComparer.OrdinalIgnoreCase) &&
                !CommonCommands.Contains(commandName, StringComparer.OrdinalIgnoreCase) &&
                !CommonAliases.Contains(commandName, StringComparer.OrdinalIgnoreCase))
            {
                return null;
            }

            string? preferredParameterName = null;
            foreach (Match parameterMatch in Regex.Matches(segment, @"(?<![\w`])-{1,2}(?<name>[A-Za-z][\w]*)\b", RegexOptions.CultureInvariant))
            {
                preferredParameterName = parameterMatch.Groups["name"].Value;
            }

            return new CommandInvocationContext(commandName, preferredParameterName);
        }

        private static string? FindCommandNameForParameter(string documentText, int parameterOffset)
        {
            if (string.IsNullOrWhiteSpace(documentText))
            {
                return null;
            }

            var safeOffset = Math.Clamp(parameterOffset, 0, documentText.Length);
            var lineStart = documentText.LastIndexOf('\n', Math.Max(0, safeOffset - 1));
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            var beforeParameter = documentText.Substring(lineStart, safeOffset - lineStart);
            var commandBoundary = Math.Max(
                Math.Max(beforeParameter.LastIndexOf('|'), beforeParameter.LastIndexOf(';')),
                Math.Max(beforeParameter.LastIndexOf('{'), beforeParameter.LastIndexOf('}')));

            var segment = commandBoundary >= 0
                ? beforeParameter[(commandBoundary + 1)..]
                : beforeParameter;

            var match = Regex.Match(segment, @"^\s*(?<cmd>[A-Za-z_][\w-]*)");
            return match.Success ? match.Groups["cmd"].Value : null;
        }

        private static bool IsDocumentFunction(string commandName, string documentText)
        {
            return ExtractDocumentFunctions(documentText)
                .Contains(commandName, StringComparer.OrdinalIgnoreCase);
        }

        private sealed record PowerShellToken(int StartOffset, int Length, string Text);

        private sealed record CommandInvocationContext(string CommandName, string? PreferredParameterName);

        private sealed record ParameterCompletionContext(string CommandName, string FragmentWithoutDash, IReadOnlySet<string> UsedParameterNames);

        private sealed record ParameterValueCompletionContext(
            string CommandName,
            string ParameterName,
            string Fragment,
            int ReplacementOffset,
            int ReplacementLength,
            bool IsQuotedValue,
            char? QuoteChar);

        private sealed record MemberCompletionContext(
            string ExpressionText,
            string MemberFragment,
            int ExpressionOffset,
            int MemberReplacementOffset,
            int MemberReplacementLength);

        private sealed record StaticMemberCompletionContext(
            string TypeText,
            string MemberFragment,
            int TypeExpressionOffset,
            int MemberReplacementOffset,
            int MemberReplacementLength);

        private sealed record ScriptSegmentToken(int Start, int Length, string Text);

        private sealed record PathValueCompletionCandidate(
            string CompletionText,
            string DisplayText,
            string Description,
            CompletionItemKind Kind);

        private sealed record MemberCompletionCandidate(
            string CompletionText,
            string DisplayText,
            string Description,
            CompletionItemKind Kind,
            double Priority);

        private sealed record CompletionContext(int ReplacementOffset, int ReplacementLength, string Fragment);

        private sealed record CompletionCandidate(
            string CompletionText,
            string DisplayText,
            string Description,
            CompletionItemKind Kind,
            int ReplacementOffset,
            int ReplacementLength,
            double Priority,
            int MatchScore);
    }

    public sealed class EditorQuickInfo
    {
        public EditorQuickInfo(string title, string body)
        {
            Title = title;
            Body = body;
        }

        public string Title { get; }
        public string Body { get; }

        public override string ToString()
        {
            return string.IsNullOrWhiteSpace(Body) ? Title : $"{Title}\n\n{Body}";
        }
    }
}
