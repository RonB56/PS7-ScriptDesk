using System;
using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models;

public sealed class ApiMetadataResult
{
    public ApiMetadataResult(
        bool parsedSuccessfully,
        string? sourcePath,
        IReadOnlyList<ApiSyntaxError>? syntaxErrors,
        IReadOnlyList<ApiFunctionMetadata>? functions,
        IReadOnlyList<ApiMetadataWarning>? warnings)
    {
        ParsedSuccessfully = parsedSuccessfully;
        SourcePath = string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath;
        SyntaxErrors = syntaxErrors ?? Array.Empty<ApiSyntaxError>();
        Functions = functions ?? Array.Empty<ApiFunctionMetadata>();
        Warnings = warnings ?? Array.Empty<ApiMetadataWarning>();
    }

    public bool ParsedSuccessfully { get; }

    public string? SourcePath { get; }

    public IReadOnlyList<ApiSyntaxError> SyntaxErrors { get; }

    public IReadOnlyList<ApiFunctionMetadata> Functions { get; }

    public IReadOnlyList<ApiMetadataWarning> Warnings { get; }

    public bool HasFunctions => Functions.Count > 0;
}

public sealed record ApiSourceExtent(
    int StartLine,
    int StartColumn,
    int EndLine,
    int EndColumn,
    int StartOffset,
    int EndOffset,
    string Text);

public sealed record ApiSyntaxError(
    string ErrorId,
    string Message,
    ApiSourceExtent? Extent);

public sealed record ApiMetadataWarning(
    string Code,
    string Message,
    ApiSourceExtent? Extent = null);

public enum ApiFunctionConstructKind
{
    Function,
    Filter
}

public sealed class ApiFunctionMetadata
{
    public ApiFunctionMetadata(
        string name,
        ApiFunctionConstructKind constructKind,
        bool isAdvancedFunction,
        bool isTopLevel,
        string? parentFunctionName,
        bool isPublishable,
        ApiSourceExtent extent,
        IReadOnlyList<ApiParameterMetadata>? parameters,
        ApiCommentHelpMetadata? commentHelp,
        IReadOnlyList<string>? declaredOutputTypes,
        IReadOnlyList<ApiMetadataWarning>? warnings)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
        ConstructKind = constructKind;
        IsAdvancedFunction = isAdvancedFunction;
        IsTopLevel = isTopLevel;
        ParentFunctionName = string.IsNullOrWhiteSpace(parentFunctionName) ? null : parentFunctionName;
        IsPublishable = isPublishable;
        Extent = extent;
        Parameters = parameters ?? Array.Empty<ApiParameterMetadata>();
        CommentHelp = commentHelp;
        DeclaredOutputTypes = declaredOutputTypes ?? Array.Empty<string>();
        Warnings = warnings ?? Array.Empty<ApiMetadataWarning>();
    }

    public string Name { get; }

    public ApiFunctionConstructKind ConstructKind { get; }

    public bool IsAdvancedFunction { get; }

    public bool IsTopLevel { get; }

    public string? ParentFunctionName { get; }

    public bool IsPublishable { get; }

    public ApiSourceExtent Extent { get; }

    public IReadOnlyList<ApiParameterMetadata> Parameters { get; }

    public ApiCommentHelpMetadata? CommentHelp { get; }

    public IReadOnlyList<string> DeclaredOutputTypes { get; }

    public IReadOnlyList<ApiMetadataWarning> Warnings { get; }
}

public enum ApiParameterMandatoryState
{
    NotMandatory,
    Mandatory,
    Unknown
}

public sealed class ApiParameterMetadata
{
    public ApiParameterMetadata(
        string name,
        string? declaredTypeName,
        bool hasExplicitType,
        bool isSwitch,
        bool isArray,
        bool? isNullable,
        ApiParameterMandatoryState mandatoryState,
        string? defaultValueExpression,
        IReadOnlyList<string>? aliases,
        IReadOnlyList<ApiValidationAttributeMetadata>? validationAttributes,
        ApiSourceExtent extent,
        bool isMetadataComplete,
        IReadOnlyList<ApiMetadataWarning>? warnings)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
        DeclaredTypeName = string.IsNullOrWhiteSpace(declaredTypeName) ? null : declaredTypeName;
        HasExplicitType = hasExplicitType;
        IsSwitch = isSwitch;
        IsArray = isArray;
        IsNullable = isNullable;
        MandatoryState = mandatoryState;
        DefaultValueExpression = string.IsNullOrWhiteSpace(defaultValueExpression) ? null : defaultValueExpression;
        Aliases = aliases ?? Array.Empty<string>();
        ValidationAttributes = validationAttributes ?? Array.Empty<ApiValidationAttributeMetadata>();
        Extent = extent;
        IsMetadataComplete = isMetadataComplete;
        Warnings = warnings ?? Array.Empty<ApiMetadataWarning>();
    }

    public string Name { get; }

    public string? DeclaredTypeName { get; }

    public bool HasExplicitType { get; }

    public bool IsSwitch { get; }

    public bool IsArray { get; }

    public bool? IsNullable { get; }

    public ApiParameterMandatoryState MandatoryState { get; }

    public string? DefaultValueExpression { get; }

    public IReadOnlyList<string> Aliases { get; }

    public IReadOnlyList<ApiValidationAttributeMetadata> ValidationAttributes { get; }

    public ApiSourceExtent Extent { get; }

    public bool IsMetadataComplete { get; }

    public IReadOnlyList<ApiMetadataWarning> Warnings { get; }
}

public sealed class ApiValidationAttributeMetadata
{
    public ApiValidationAttributeMetadata(
        string name,
        IReadOnlyList<ApiAttributeArgumentMetadata>? arguments,
        IReadOnlyDictionary<string, ApiAttributeArgumentMetadata>? namedArguments,
        bool isFullyResolved,
        ApiSourceExtent extent)
    {
        Name = string.IsNullOrWhiteSpace(name) ? string.Empty : name;
        Arguments = arguments ?? Array.Empty<ApiAttributeArgumentMetadata>();
        NamedArguments = namedArguments ?? new Dictionary<string, ApiAttributeArgumentMetadata>();
        IsFullyResolved = isFullyResolved;
        Extent = extent;
    }

    public string Name { get; }

    public IReadOnlyList<ApiAttributeArgumentMetadata> Arguments { get; }

    public IReadOnlyDictionary<string, ApiAttributeArgumentMetadata> NamedArguments { get; }

    public bool IsFullyResolved { get; }

    public ApiSourceExtent Extent { get; }
}

public sealed record ApiAttributeArgumentMetadata(
    string? Name,
    string Text,
    string? Value,
    bool IsStaticallyResolved);

public sealed class ApiCommentHelpMetadata
{
    public ApiCommentHelpMetadata(
        string rawText,
        string? synopsis,
        string? description,
        IReadOnlyDictionary<string, string>? parameterDescriptions,
        IReadOnlyList<string>? examples,
        bool isPartial)
    {
        RawText = rawText;
        Synopsis = string.IsNullOrWhiteSpace(synopsis) ? null : synopsis;
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
        ParameterDescriptions = parameterDescriptions ?? new Dictionary<string, string>();
        Examples = examples ?? Array.Empty<string>();
        IsPartial = isPartial;
    }

    public string RawText { get; }

    public string? Synopsis { get; }

    public string? Description { get; }

    public IReadOnlyDictionary<string, string> ParameterDescriptions { get; }

    public IReadOnlyList<string> Examples { get; }

    public bool IsPartial { get; }
}
