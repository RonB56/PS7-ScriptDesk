using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using System.Management.Automation.Language;

namespace PS7ScriptDesk.PowerShell.Services;

public sealed class PowerShellApiMetadataService : IApiMetadataService
{
    private static readonly HashSet<string> ValidationAttributeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ValidateSet",
        "ValidateRange",
        "ValidateLength",
        "ValidatePattern",
        "ValidateNotNull",
        "ValidateNotNullOrEmpty"
    };

    private static readonly Regex SectionMarkerRegex = new(@"^\.(?<name>[A-Za-z][A-Za-z0-9]*)\b(?<rest>.*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public ApiMetadataResult Analyze(string sourceText, string? sourcePath = null)
    {
        var normalizedSource = sourceText ?? string.Empty;
        var ast = Parser.ParseInput(normalizedSource, out _, out var parseErrors);
        var syntaxErrors = parseErrors
            .Select(error => new ApiSyntaxError(
                error.ErrorId ?? string.Empty,
                error.Message ?? string.Empty,
                error.Extent is null ? null : ToExtent(error.Extent)))
            .ToList();

        var warnings = new List<ApiMetadataWarning>();
        warnings.AddRange(FindUnsupportedScriptWarnings(ast));

        var functions = ast
            .FindAll(static node => node is FunctionDefinitionAst, searchNestedScriptBlocks: true)
            .OfType<FunctionDefinitionAst>()
            .Where(static function => !HasAncestor<TypeDefinitionAst>(function))
            .OrderBy(function => function.Extent.StartOffset)
            .Select(function => BuildFunctionMetadata(function, normalizedSource))
            .ToList();

        return new ApiMetadataResult(
            syntaxErrors.Count == 0,
            sourcePath,
            syntaxErrors,
            functions,
            warnings);
    }

    private static ApiFunctionMetadata BuildFunctionMetadata(FunctionDefinitionAst function, string sourceText)
    {
        var warnings = new List<ApiMetadataWarning>();
        var parentFunction = FindParentFunction(function);
        var isTopLevel = parentFunction is null;
        var constructKind = function.IsFilter ? ApiFunctionConstructKind.Filter : ApiFunctionConstructKind.Function;
        var paramBlock = function.Body?.ParamBlock;
        var parameters = paramBlock?.Parameters
            .Select(BuildParameterMetadata)
            .ToList() ?? new List<ApiParameterMetadata>();
        var isAdvanced = HasAttribute(paramBlock?.Attributes, "CmdletBinding");
        var outputTypes = ExtractOutputTypes(paramBlock?.Attributes, warnings);

        if (function.IsFilter)
        {
            warnings.Add(new ApiMetadataWarning(
                "FilterNotPublishableInV1",
                "PowerShell filters are discovered as function-like constructs but are not treated as ordinary publishable REST API functions in Phase 1.",
                ToExtent(function.Extent)));
        }

        if (!isTopLevel)
        {
            warnings.Add(new ApiMetadataWarning(
                "NestedFunctionNotPublishableInV1",
                "Nested functions are discovered for metadata but are not top-level publishable REST API endpoint candidates.",
                ToExtent(function.Extent)));
        }

        return new ApiFunctionMetadata(
            function.Name,
            constructKind,
            isAdvanced,
            isTopLevel,
            parentFunction?.Name,
            isTopLevel && constructKind == ApiFunctionConstructKind.Function,
            ToExtent(function.Extent),
            parameters,
            TryExtractCommentHelp(function, sourceText),
            outputTypes,
            warnings);
    }

    private static ApiParameterMetadata BuildParameterMetadata(ParameterAst parameter)
    {
        var warnings = new List<ApiMetadataWarning>();
        var aliases = new List<string>();
        var validationAttributes = new List<ApiValidationAttributeMetadata>();
        var mandatoryState = ApiParameterMandatoryState.NotMandatory;
        var metadataComplete = true;

        foreach (var attribute in parameter.Attributes.OfType<AttributeAst>())
        {
            var attributeName = NormalizeAttributeName(attribute.TypeName.Name);
            if (string.Equals(attributeName, "Parameter", StringComparison.OrdinalIgnoreCase))
            {
                mandatoryState = ResolveMandatoryState(attribute, warnings, parameter.Extent, mandatoryState);
            }
            else if (string.Equals(attributeName, "Alias", StringComparison.OrdinalIgnoreCase))
            {
                var resolvedAliases = ResolveAttributeArguments(attribute.PositionalArguments);
                aliases.AddRange(resolvedAliases.Where(argument => argument.IsStaticallyResolved && !string.IsNullOrWhiteSpace(argument.Value)).Select(argument => argument.Value!));
                if (resolvedAliases.Any(argument => !argument.IsStaticallyResolved))
                {
                    metadataComplete = false;
                    warnings.Add(new ApiMetadataWarning(
                        "AliasPartiallyUnknown",
                        $"One or more aliases for parameter '{GetParameterName(parameter)}' could not be resolved statically.",
                        ToExtent(attribute.Extent)));
                }
            }
            else if (ValidationAttributeNames.Contains(attributeName))
            {
                var metadata = BuildValidationAttributeMetadata(attribute);
                validationAttributes.Add(metadata);
                if (!metadata.IsFullyResolved)
                {
                    metadataComplete = false;
                    warnings.Add(new ApiMetadataWarning(
                        "ValidationAttributePartiallyUnknown",
                        $"Validation attribute '{attributeName}' on parameter '{GetParameterName(parameter)}' contains metadata that cannot be resolved statically.",
                        ToExtent(attribute.Extent)));
                }
            }
        }

        var explicitType = parameter.Attributes
            .OfType<TypeConstraintAst>()
            .FirstOrDefault();
        var declaredTypeName = explicitType?.TypeName.FullName;
        var staticType = parameter.StaticType;
        var isSwitch = IsSwitchType(declaredTypeName, staticType);
        var isArray = staticType.IsArray || (declaredTypeName?.Contains("[]", StringComparison.Ordinal) == true);
        var isNullable = ResolveNullableState(declaredTypeName, staticType, parameter);

        if (mandatoryState == ApiParameterMandatoryState.Unknown)
        {
            metadataComplete = false;
        }

        return new ApiParameterMetadata(
            GetParameterName(parameter),
            declaredTypeName,
            explicitType is not null,
            isSwitch,
            isArray,
            isNullable,
            mandatoryState,
            parameter.DefaultValue?.Extent.Text,
            aliases.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            validationAttributes,
            ToExtent(parameter.Extent),
            metadataComplete,
            warnings);
    }

    private static ApiParameterMandatoryState ResolveMandatoryState(
        AttributeAst attribute,
        List<ApiMetadataWarning> warnings,
        IScriptExtent parameterExtent,
        ApiParameterMandatoryState current)
    {
        var mandatoryArgument = attribute.NamedArguments.FirstOrDefault(argument =>
            string.Equals(argument.ArgumentName, "Mandatory", StringComparison.OrdinalIgnoreCase));

        if (mandatoryArgument is null)
        {
            return current;
        }

        if (mandatoryArgument.ExpressionOmitted)
        {
            return ApiParameterMandatoryState.Mandatory;
        }

        var resolved = TryResolveBoolean(mandatoryArgument.Argument);
        if (resolved.HasValue)
        {
            return resolved.Value ? ApiParameterMandatoryState.Mandatory : ApiParameterMandatoryState.NotMandatory;
        }

        warnings.Add(new ApiMetadataWarning(
            "MandatoryStateUnknown",
            "Parameter mandatory state is present but cannot be resolved statically.",
            ToExtent(parameterExtent)));
        return ApiParameterMandatoryState.Unknown;
    }

    private static ApiValidationAttributeMetadata BuildValidationAttributeMetadata(AttributeAst attribute)
    {
        var positionalArguments = ResolveAttributeArguments(attribute.PositionalArguments);
        var namedArguments = attribute.NamedArguments.ToDictionary(
            argument => argument.ArgumentName,
            argument => ResolveNamedAttributeArgument(argument),
            StringComparer.OrdinalIgnoreCase);
        var isFullyResolved = positionalArguments.All(argument => argument.IsStaticallyResolved) &&
                              namedArguments.Values.All(argument => argument.IsStaticallyResolved);

        return new ApiValidationAttributeMetadata(
            NormalizeAttributeName(attribute.TypeName.Name),
            positionalArguments,
            namedArguments,
            isFullyResolved,
            ToExtent(attribute.Extent));
    }

    private static IReadOnlyList<ApiAttributeArgumentMetadata> ResolveAttributeArguments(IEnumerable<ExpressionAst> arguments)
    {
        return arguments.Select(argument =>
        {
            var resolved = TryResolveLiteral(argument);
            return new ApiAttributeArgumentMetadata(
                null,
                argument.Extent.Text,
                resolved.Value,
                resolved.IsResolved);
        }).ToList();
    }

    private static ApiAttributeArgumentMetadata ResolveNamedAttributeArgument(NamedAttributeArgumentAst argument)
    {
        if (argument.ExpressionOmitted)
        {
            return new ApiAttributeArgumentMetadata(argument.ArgumentName, argument.Extent.Text, "true", true);
        }

        var resolved = TryResolveLiteral(argument.Argument);
        return new ApiAttributeArgumentMetadata(
            argument.ArgumentName,
            argument.Argument?.Extent.Text ?? argument.Extent.Text,
            resolved.Value,
            resolved.IsResolved);
    }

    private static (bool IsResolved, string? Value) TryResolveLiteral(ExpressionAst? expression)
    {
        return expression switch
        {
            null => (false, null),
            StringConstantExpressionAst stringConstant => (true, stringConstant.Value),
            ConstantExpressionAst constant => (true, Convert.ToString(constant.Value, CultureInfo.InvariantCulture)),
            VariableExpressionAst variable when string.Equals(variable.VariablePath.UserPath, "true", StringComparison.OrdinalIgnoreCase) => (true, "true"),
            VariableExpressionAst variable when string.Equals(variable.VariablePath.UserPath, "false", StringComparison.OrdinalIgnoreCase) => (true, "false"),
            TypeExpressionAst typeExpression => (true, typeExpression.TypeName.FullName),
            ArrayLiteralAst arrayLiteral => ResolveArrayLiteral(arrayLiteral),
            _ => (false, null)
        };
    }

    private static (bool IsResolved, string? Value) ResolveArrayLiteral(ArrayLiteralAst arrayLiteral)
    {
        var values = new List<string>();
        foreach (var element in arrayLiteral.Elements)
        {
            var resolved = TryResolveLiteral(element);
            if (!resolved.IsResolved)
            {
                return (false, null);
            }

            values.Add(resolved.Value ?? string.Empty);
        }

        return (true, string.Join(",", values));
    }

    private static bool? TryResolveBoolean(ExpressionAst? expression)
    {
        var resolved = TryResolveLiteral(expression);
        if (!resolved.IsResolved || string.IsNullOrWhiteSpace(resolved.Value))
        {
            return null;
        }

        if (bool.TryParse(resolved.Value, out var value))
        {
            return value;
        }

        return null;
    }

    private static List<string> ExtractOutputTypes(IEnumerable<AttributeAst>? attributes, List<ApiMetadataWarning> warnings)
    {
        var outputTypes = new List<string>();
        if (attributes is null)
        {
            return outputTypes;
        }

        foreach (var attribute in attributes.Where(attribute => string.Equals(NormalizeAttributeName(attribute.TypeName.Name), "OutputType", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var argument in attribute.PositionalArguments)
            {
                var resolved = TryResolveLiteral(argument);
                if (resolved.IsResolved && !string.IsNullOrWhiteSpace(resolved.Value))
                {
                    outputTypes.Add(resolved.Value!);
                    continue;
                }

                warnings.Add(new ApiMetadataWarning(
                    "OutputTypePartiallyUnknown",
                    "An OutputType attribute contains metadata that cannot be resolved statically.",
                    ToExtent(attribute.Extent)));
            }
        }

        return outputTypes.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ApiCommentHelpMetadata? TryExtractCommentHelp(FunctionDefinitionAst function, string sourceText)
    {
        var lines = SplitLinesWithEndings(sourceText);
        var startLineIndex = Math.Max(0, function.Extent.StartLineNumber - 2);
        var blockLines = new List<string>();

        for (var index = startLineIndex; index >= 0; index--)
        {
            var line = lines[index].TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(line))
            {
                if (blockLines.Count == 0)
                {
                    continue;
                }

                break;
            }

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("#", StringComparison.Ordinal))
            {
                break;
            }

            blockLines.Add(line);
        }

        blockLines.Reverse();
        if (blockLines.Count == 0)
        {
            return null;
        }

        var cleanedLines = blockLines
            .Select(line =>
            {
                var trimmed = line.TrimStart();
                return trimmed.StartsWith("#", StringComparison.Ordinal)
                    ? trimmed[1..].TrimStart()
                    : trimmed;
            })
            .ToList();

        return BuildCommentHelpMetadata(string.Join(Environment.NewLine, cleanedLines), isPartial: true);
    }

    private static ApiCommentHelpMetadata BuildCommentHelpMetadata(string rawText, bool isPartial)
    {
        string? currentSection = null;
        var synopsis = new StringBuilder();
        var description = new StringBuilder();
        var examples = new List<string>();
        var parameterDescriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var currentParameterName = string.Empty;
        var currentParameterText = new StringBuilder();
        var currentExampleText = new StringBuilder();

        void FlushParameter()
        {
            if (!string.IsNullOrWhiteSpace(currentParameterName))
            {
                parameterDescriptions[currentParameterName] = currentParameterText.ToString().Trim();
            }

            currentParameterName = string.Empty;
            currentParameterText.Clear();
        }

        void FlushExample()
        {
            if (currentExampleText.Length > 0)
            {
                examples.Add(currentExampleText.ToString().Trim());
                currentExampleText.Clear();
            }
        }

        foreach (var rawLine in rawText.Split(["\r\n", "\n"], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            var marker = SectionMarkerRegex.Match(line);
            if (marker.Success)
            {
                FlushParameter();
                if (!string.Equals(currentSection, "EXAMPLE", StringComparison.OrdinalIgnoreCase))
                {
                    FlushExample();
                }

                currentSection = marker.Groups["name"].Value.ToUpperInvariant();
                var rest = marker.Groups["rest"].Value.Trim();
                if (string.Equals(currentSection, "PARAMETER", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = rest.Split([' ', '\t'], 2, StringSplitOptions.RemoveEmptyEntries);
                    currentParameterName = parts.Length > 0 ? parts[0] : string.Empty;
                    if (parts.Length > 1)
                    {
                        currentParameterText.AppendLine(parts[1]);
                    }
                }
                else if (string.Equals(currentSection, "EXAMPLE", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(rest))
                {
                    currentExampleText.AppendLine(rest);
                }

                continue;
            }

            switch (currentSection)
            {
                case "SYNOPSIS":
                    synopsis.AppendLine(line);
                    break;
                case "DESCRIPTION":
                    description.AppendLine(line);
                    break;
                case "PARAMETER":
                    currentParameterText.AppendLine(line);
                    break;
                case "EXAMPLE":
                    currentExampleText.AppendLine(line);
                    break;
            }
        }

        FlushParameter();
        FlushExample();

        return new ApiCommentHelpMetadata(
            rawText,
            synopsis.ToString().Trim(),
            description.ToString().Trim(),
            parameterDescriptions,
            examples,
            isPartial);
    }

    private static IReadOnlyList<ApiMetadataWarning> FindUnsupportedScriptWarnings(Ast ast)
    {
        var warnings = new List<ApiMetadataWarning>();

        foreach (var typeDefinition in ast.FindAll(static node => node is TypeDefinitionAst, searchNestedScriptBlocks: true).OfType<TypeDefinitionAst>())
        {
            warnings.Add(new ApiMetadataWarning(
                "PowerShellClassIgnoredInPhase1",
                "PowerShell class definitions are ignored by the Phase 1 API metadata parser.",
                ToExtent(typeDefinition.Extent)));
        }

        foreach (var command in ast.FindAll(static node => node is CommandAst, searchNestedScriptBlocks: true).OfType<CommandAst>())
        {
            var commandName = command.GetCommandName();
            if (string.IsNullOrWhiteSpace(commandName))
            {
                continue;
            }

            if (string.Equals(commandName, "Invoke-Expression", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(commandName, "iex", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add(new ApiMetadataWarning(
                    "DynamicExecutionIgnored",
                    "Invoke-Expression can create runtime behavior that static API metadata discovery will not execute or expand.",
                    ToExtent(command.Extent)));
            }

            if (CreatesDynamicFunction(commandName, command))
            {
                warnings.Add(new ApiMetadataWarning(
                    "DynamicFunctionCreationIgnored",
                    "Runtime function creation is not treated as a statically declared publishable function.",
                    ToExtent(command.Extent)));
            }
        }

        return warnings;
    }

    private static bool CreatesDynamicFunction(string commandName, CommandAst command)
    {
        if (!string.Equals(commandName, "New-Item", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(commandName, "Set-Item", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return command.CommandElements.Any(element =>
            element.Extent.Text.Contains("Function:", StringComparison.OrdinalIgnoreCase));
    }

    private static FunctionDefinitionAst? FindParentFunction(Ast ast)
    {
        var parent = ast.Parent;
        while (parent is not null)
        {
            if (parent is FunctionDefinitionAst function)
            {
                return function;
            }

            parent = parent.Parent;
        }

        return null;
    }

    private static bool HasAncestor<TAst>(Ast ast)
        where TAst : Ast
    {
        var parent = ast.Parent;
        while (parent is not null)
        {
            if (parent is TAst)
            {
                return true;
            }

            parent = parent.Parent;
        }

        return false;
    }

    private static bool HasAttribute(IEnumerable<AttributeAst>? attributes, string attributeName)
    {
        return attributes?.Any(attribute => string.Equals(NormalizeAttributeName(attribute.TypeName.Name), attributeName, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static string NormalizeAttributeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return name.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase)
            ? name[..^"Attribute".Length]
            : name;
    }

    private static string GetParameterName(ParameterAst parameter)
    {
        return parameter.Name.VariablePath.UserPath;
    }

    private static bool IsSwitchType(string? declaredTypeName, Type staticType)
    {
        return string.Equals(staticType.FullName, "System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(declaredTypeName, "switch", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(declaredTypeName, "System.Management.Automation.SwitchParameter", StringComparison.OrdinalIgnoreCase);
    }

    private static bool? ResolveNullableState(string? declaredTypeName, Type staticType, ParameterAst parameter)
    {
        if (staticType.IsGenericType && staticType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(declaredTypeName) &&
            (declaredTypeName.StartsWith("System.Nullable[", StringComparison.OrdinalIgnoreCase) ||
             declaredTypeName.StartsWith("Nullable[", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return parameter.Attributes.OfType<TypeConstraintAst>().Any()
            ? false
            : null;
    }

    private static ApiSourceExtent ToExtent(IScriptExtent extent)
    {
        return new ApiSourceExtent(
            extent.StartLineNumber,
            extent.StartColumnNumber,
            extent.EndLineNumber,
            extent.EndColumnNumber,
            extent.StartOffset,
            extent.EndOffset,
            extent.Text ?? string.Empty);
    }

    private static IReadOnlyList<string> SplitLinesWithEndings(string text)
    {
        if (text.Length == 0)
        {
            return Array.Empty<string>();
        }

        var lines = new List<string>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] != '\n')
            {
                continue;
            }

            lines.Add(text[start..(index + 1)]);
            start = index + 1;
        }

        if (start < text.Length)
        {
            lines.Add(text[start..]);
        }

        return lines;
    }
}
