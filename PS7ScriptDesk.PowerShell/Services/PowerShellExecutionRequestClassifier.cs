using System.Management.Automation.Language;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.PowerShell.Services;

/// <summary>
/// Conservative, syntax-based editor execution classifier. Anything that cannot be
/// proven noninteractive remains unknown and therefore stays on the legacy console.
/// </summary>
public sealed class PowerShellExecutionRequestClassifier : IEditorExecutionRequestClassifier
{
    private static readonly HashSet<string> KnownNonInteractiveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "Write-Output", "Write-Host", "Get-Location", "Set-Location", "Get-Process",
        "Get-Command", "Get-Date", "ForEach-Object", "Where-Object", "Select-Object",
        "Sort-Object", "Measure-Object", "Write-Verbose", "Write-Warning", "Write-Debug",
        "Write-Information", "Test-Path", "Join-Path", "Split-Path", "Resolve-Path",
        "ConvertTo-Json", "ConvertFrom-Json", "Out-String"
    };

    public ExecutionRequestClassificationResult Classify(EditorExecutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var scriptText = request.ScriptText ?? string.Empty;
        if (string.IsNullOrWhiteSpace(scriptText))
        {
            return NonInteractive("The request contains no executable script text.");
        }

        ScriptBlockAst ast;
        try
        {
            ast = Parser.ParseInput(scriptText, out _, out var errors);
            if (errors.Count() > 0)
            {
                return Unknown("The PowerShell parser reported syntax errors.");
            }
        }
        catch (ArgumentException)
        {
            return Unknown("The PowerShell parser could not classify the request.");
        }

        var memberTexts = ast.FindAll(node => node is MemberExpressionAst, searchNestedScriptBlocks: true)
            .OfType<MemberExpressionAst>()
            .Select(member => member.Extent.Text)
            .ToArray();
        if (memberTexts.Any(IsInteractiveHostMember))
        {
            var secure = memberTexts.Any(text => text.Contains("PromptForCredential", StringComparison.OrdinalIgnoreCase));
            return Interactive(
                secure ? "The request uses host credential prompting." : "The request uses host UI interaction.",
                secure);
        }

        if (ast.FindAll(node => node is VariableExpressionAst variable &&
                                string.Equals(variable.VariablePath.UserPath, "Host", StringComparison.OrdinalIgnoreCase),
                                searchNestedScriptBlocks: true).Count() > 0)
        {
            return Interactive("The request references the PowerShell host object.", secure: false);
        }

        var functionNames = ast.FindAll(node => node is FunctionDefinitionAst, true)
            .OfType<FunctionDefinitionAst>()
            .Select(function => function.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var commandAsts = ast.FindAll(node => node is CommandAst, true)
            .OfType<CommandAst>()
            .ToArray();
        foreach (var command in commandAsts)
        {
            var commandName = command.GetCommandName();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                return Unknown("The request contains dynamic command invocation.");
            }

            var leafName = commandName.Contains('\\', StringComparison.Ordinal)
                ? commandName[(commandName.LastIndexOf('\\') + 1)..]
                : commandName;
            if (leafName.Equals("Read-Host", StringComparison.OrdinalIgnoreCase))
            {
                var secure = command.CommandElements.OfType<CommandParameterAst>()
                    .Any(parameter => parameter.ParameterName.Equals("AsSecureString", StringComparison.OrdinalIgnoreCase));
                return Interactive(
                    secure ? "The request uses Read-Host secure input." : "The request uses Read-Host input.",
                    secure);
            }

            if (leafName.Equals("Invoke-Expression", StringComparison.OrdinalIgnoreCase) ||
                leafName.Equals("Invoke-Command", StringComparison.OrdinalIgnoreCase) ||
                leafName.Equals("Start-Process", StringComparison.OrdinalIgnoreCase) ||
                leafName.Equals("Import-Module", StringComparison.OrdinalIgnoreCase) ||
                leafName.Equals(".", StringComparison.OrdinalIgnoreCase) ||
                leafName.Equals("&", StringComparison.OrdinalIgnoreCase))
            {
                return Unknown($"The request contains a command with dynamic, module, or external execution semantics: {leafName}.");
            }

            if (!KnownNonInteractiveCommands.Contains(leafName) && !functionNames.Contains(leafName))
            {
                return Unknown($"The request contains an unclassified command: {leafName}.");
            }
        }

        return NonInteractive("All parsed commands are in the conservative noninteractive allow-list.");
    }

    private static bool IsInteractiveHostMember(string text)
    {
        return text.Contains("$Host.UI", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PromptForChoice", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("PromptForCredential", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("RawUI", StringComparison.OrdinalIgnoreCase);
    }

    private static ExecutionRequestClassificationResult Interactive(string reason, bool secure)
        => new(
            ExecutionRequestClassification.KnownInteractive,
            new ExecutionCapabilityRequirements(
                RequiresInteractiveInput: true,
                RequiresSecureInput: secure,
                RequiresTerminalInterrupt: true,
                RequiresWorkingDirectoryContinuity: true),
            reason);

    private static ExecutionRequestClassificationResult NonInteractive(string reason)
        => new(
            ExecutionRequestClassification.KnownNonInteractive,
            new ExecutionCapabilityRequirements(RequiresWorkingDirectoryContinuity: true),
            reason);

    private static ExecutionRequestClassificationResult Unknown(string reason)
        => new(
            ExecutionRequestClassification.Unknown,
            new ExecutionCapabilityRequirements(RequiresWorkingDirectoryContinuity: true),
            reason);
}
