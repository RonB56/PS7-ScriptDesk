using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellApiMetadataServiceTests
{
    private readonly PowerShellApiMetadataService _service = new();

    [Fact]
    public void BasicFunction_IsDiscoveredAsTopLevelPublishableFunction()
    {
        var result = _service.Analyze("""
function Get-Test {
    param()
}
""");

        Assert.True(result.ParsedSuccessfully);
        var function = Assert.Single(result.Functions);
        Assert.Equal("Get-Test", function.Name);
        Assert.True(function.IsTopLevel);
        Assert.True(function.IsPublishable);
        Assert.False(function.IsAdvancedFunction);
        Assert.Empty(function.Parameters);
        Assert.Equal(ApiFunctionConstructKind.Function, function.ConstructKind);
        Assert.Equal(1, function.Extent.StartLine);
    }

    [Fact]
    public void AdvancedFunction_CmdletBindingIsDetected()
    {
        var function = SingleFunction("""
function Get-Test {
    [CmdletBinding()]
    param()
}
""");

        Assert.True(function.IsAdvancedFunction);
    }

    [Fact]
    public void TypedParameters_AreDiscoveredWithoutExecutingSource()
    {
        var function = SingleFunction("""
function Get-Test {
    param(
        [string]$Name,
        [int]$Count,
        [switch]$Force
    )
}
""");

        AssertParameter(function, "Name", "string", isSwitch: false);
        AssertParameter(function, "Count", "int", isSwitch: false);
        AssertParameter(function, "Force", "switch", isSwitch: true);
    }

    [Fact]
    public void MandatoryParameter_OmittedValueMeansTrue()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Parameter(Mandatory)]
        [string]$Name
    )
}
""");

        Assert.Equal(ApiParameterMandatoryState.Mandatory, parameter.MandatoryState);
    }

    [Fact]
    public void MandatoryParameter_ExplicitTrueMeansMandatory()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name
    )
}
""");

        Assert.Equal(ApiParameterMandatoryState.Mandatory, parameter.MandatoryState);
    }

    [Fact]
    public void MandatoryParameter_ExplicitFalseDoesNotMeanMandatory()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Parameter(Mandatory = $false)]
        [string]$Name
    )
}
""");

        Assert.Equal(ApiParameterMandatoryState.NotMandatory, parameter.MandatoryState);
    }

    [Fact]
    public void MandatoryParameter_DynamicExpressionIsUnknown()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Parameter(Mandatory = { Test-Path env:CI })]
        [string]$Name
    )
}
""");

        Assert.Equal(ApiParameterMandatoryState.Unknown, parameter.MandatoryState);
        Assert.False(parameter.IsMetadataComplete);
        Assert.Contains(parameter.Warnings, warning => warning.Code == "MandatoryStateUnknown");
    }

    [Fact]
    public void DefaultLiteral_IsRecordedAsExpressionText()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [string]$Name = "Test"
    )
}
""");

        Assert.Equal("\"Test\"", parameter.DefaultValueExpression);
    }

    [Fact]
    public void DefaultExpression_IsRecordedButNotExecuted()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [string]$Name = (Get-Date).ToString()
    )
}
""");

        Assert.Equal("(Get-Date).ToString()", parameter.DefaultValueExpression);
    }

    [Fact]
    public void AliasAttribute_ReturnsAllStaticAliases()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Alias("Computer", "Machine")]
        [string]$ComputerName
    )
}
""");

        Assert.Equal(["Computer", "Machine"], parameter.Aliases);
    }

    [Theory]
    [InlineData("ValidateSet", "[ValidateSet(\"One\", \"Two\")]", "One", "Two")]
    [InlineData("ValidateRange", "[ValidateRange(1, 10)]", "1", "10")]
    [InlineData("ValidateLength", "[ValidateLength(2, 20)]", "2", "20")]
    [InlineData("ValidatePattern", "[ValidatePattern(\"^[a-z]+$\")]", "^[a-z]+$", null)]
    public void ValidationAttributes_WithLiteralArguments_AreResolved(string expectedName, string attributeText, string firstValue, string? secondValue)
    {
        var parameter = SingleParameter($$"""
function Get-Test {
    param(
        {{attributeText}}
        [string]$Value
    )
}
""");

        var validation = Assert.Single(parameter.ValidationAttributes);
        Assert.Equal(expectedName, validation.Name);
        Assert.True(validation.IsFullyResolved);
        Assert.Equal(firstValue, validation.Arguments[0].Value);
        if (secondValue is not null)
        {
            Assert.Equal(secondValue, validation.Arguments[1].Value);
        }
    }

    [Theory]
    [InlineData("ValidateNotNull", "[ValidateNotNull()]")]
    [InlineData("ValidateNotNullOrEmpty", "[ValidateNotNullOrEmpty()]")]
    public void ValidationAttributes_WithoutArguments_AreRecorded(string expectedName, string attributeText)
    {
        var parameter = SingleParameter($$"""
function Get-Test {
    param(
        {{attributeText}}
        [string]$Value
    )
}
""");

        var validation = Assert.Single(parameter.ValidationAttributes);
        Assert.Equal(expectedName, validation.Name);
        Assert.True(validation.IsFullyResolved);
        Assert.Empty(validation.Arguments);
    }

    [Fact]
    public void ValidationAttribute_WithDynamicArgument_IsMarkedPartiallyUnknown()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [ValidateSet({ (Get-Date).DayOfWeek })]
        [string]$Value
    )
}
""");

        var validation = Assert.Single(parameter.ValidationAttributes);
        Assert.False(validation.IsFullyResolved);
        Assert.False(parameter.IsMetadataComplete);
        Assert.Contains(parameter.Warnings, warning => warning.Code == "ValidationAttributePartiallyUnknown");
    }

    [Fact]
    public void ArrayParameter_IsDetected()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [string[]]$Names
    )
}
""");

        Assert.Equal("string[]", parameter.DeclaredTypeName);
        Assert.True(parameter.IsArray);
    }

    [Fact]
    public void NullableParameter_IsDetectedWhereSyntaxAllowsIt()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Nullable[int]]$Count
    )
}
""");

        Assert.True(parameter.IsNullable);
        Assert.Contains("Nullable", parameter.DeclaredTypeName, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("[hashtable]$Value", "hashtable")]
    [InlineData("[pscustomobject]$Value", "pscustomobject")]
    [InlineData("[guid]$Value", "guid")]
    [InlineData("[datetime]$Value", "datetime")]
    [InlineData("[datetimeoffset]$Value", "datetimeoffset")]
    [InlineData("[ConsoleColor]$Value", "ConsoleColor")]
    public void DeclaredTypes_AreDiscoveredEvenWhenFutureRestBindingMayRejectThem(string declaration, string expectedType)
    {
        var parameter = SingleParameter($$"""
function Get-Test {
    param(
        {{declaration}}
    )
}
""");

        Assert.Equal(expectedType, parameter.DeclaredTypeName);
        Assert.True(parameter.HasExplicitType);
    }

    [Fact]
    public void OutputType_IsDiscoveredFromStaticTypeArguments()
    {
        var function = SingleFunction("""
function Get-Test {
    [OutputType([string], [System.Diagnostics.Process])]
    param()
}
""");

        Assert.Contains("string", function.DeclaredOutputTypes);
        Assert.Contains("System.Diagnostics.Process", function.DeclaredOutputTypes);
    }

    [Fact]
    public void MultipleFunctions_AreReturnedInSourceOrder()
    {
        var result = _service.Analyze("""
function Get-First { param() }
function Get-Second { param() }
""");

        Assert.Equal(["Get-First", "Get-Second"], result.Functions.Select(function => function.Name).ToArray());
    }

    [Fact]
    public void NestedFunction_IsIdentifiedWithParentAndNotPublishable()
    {
        var result = _service.Analyze("""
function Outer {
    function Inner {
    }
}
""");

        Assert.Equal(2, result.Functions.Count);
        var outer = result.Functions.Single(function => function.Name == "Outer");
        var inner = result.Functions.Single(function => function.Name == "Inner");
        Assert.True(outer.IsTopLevel);
        Assert.True(outer.IsPublishable);
        Assert.False(inner.IsTopLevel);
        Assert.False(inner.IsPublishable);
        Assert.Equal("Outer", inner.ParentFunctionName);
        Assert.Contains(inner.Warnings, warning => warning.Code == "NestedFunctionNotPublishableInV1");
    }

    [Fact]
    public void NoFunctions_IsSuccessfulWithEmptyFunctionList()
    {
        var result = _service.Analyze("$value = 1");

        Assert.True(result.ParsedSuccessfully);
        Assert.Empty(result.Functions);
    }

    [Fact]
    public void SyntaxError_IsReturnedInResultInsteadOfThrowing()
    {
        var result = _service.Analyze("function Get-Test { param(");

        Assert.False(result.ParsedSuccessfully);
        Assert.NotEmpty(result.SyntaxErrors);
        Assert.All(result.SyntaxErrors, error =>
        {
            Assert.False(string.IsNullOrWhiteSpace(error.Message));
            Assert.NotNull(error.Extent);
        });
    }

    [Fact]
    public void MaliciousLookingScript_IsParsedStaticallyAndNotExecuted()
    {
        var markerPath = Path.Combine(Path.GetTempPath(), $"PS7ScriptDeskApiMetadata_{Guid.NewGuid():N}.txt");
        var escapedMarkerPath = markerPath.Replace("'", "''", StringComparison.Ordinal);

        var result = _service.Analyze($$"""
Remove-Item 'C:\Something'
Invoke-WebRequest 'https://example.invalid'
Start-Process notepad.exe
New-Item -Path '{{escapedMarkerPath}}' -ItemType File
function Get-Test {
    param()
}
""");

        Assert.True(result.ParsedSuccessfully);
        Assert.Single(result.Functions);
        Assert.False(File.Exists(markerPath));
    }

    [Fact]
    public void DynamicFunctionCreation_IsWarnedButNotReturnedAsStaticFunction()
    {
        var result = _service.Analyze("""
Invoke-Expression 'function Get-Dynamic { }'
New-Item Function:\Get-Other -Value { }
""");

        Assert.Empty(result.Functions);
        Assert.Contains(result.Warnings, warning => warning.Code == "DynamicExecutionIgnored");
        Assert.Contains(result.Warnings, warning => warning.Code == "DynamicFunctionCreationIgnored");
    }

    [Fact]
    public void UnsupportedMetadata_ProducesWarningRatherThanCrashing()
    {
        var parameter = SingleParameter("""
function Get-Test {
    param(
        [Alias({ (Get-Date).DayOfWeek })]
        [string]$Value
    )
}
""");

        Assert.False(parameter.IsMetadataComplete);
        Assert.Contains(parameter.Warnings, warning => warning.Code == "AliasPartiallyUnknown");
    }

    [Fact]
    public void CommentBasedHelp_AdjacentLineCommentsAreConservativelyAttached()
    {
        var function = SingleFunction("""
# .SYNOPSIS
# Gets test data.
# .PARAMETER Name
# The name to read.
# .EXAMPLE
# Get-Test -Name One
function Get-Test {
    param([string]$Name)
}
""");

        Assert.NotNull(function.CommentHelp);
        Assert.Equal("Gets test data.", function.CommentHelp!.Synopsis);
        Assert.Equal("The name to read.", function.CommentHelp.ParameterDescriptions["Name"]);
        Assert.Contains("Get-Test -Name One", function.CommentHelp.Examples);
    }

    [Fact]
    public void PowerShellClasses_AreWarnedAndMethodsAreNotReturnedAsFunctions()
    {
        var result = _service.Analyze("""
class Widget {
    [string] GetName() { return "Widget" }
}
""");

        Assert.Empty(result.Functions);
        Assert.Contains(result.Warnings, warning => warning.Code == "PowerShellClassIgnoredInPhase1");
    }

    [Fact]
    public void Filters_AreClassifiedSeparatelyAndNotPublishable()
    {
        var function = SingleFunction("""
filter Get-Something {
    $_
}
""");

        Assert.Equal(ApiFunctionConstructKind.Filter, function.ConstructKind);
        Assert.False(function.IsPublishable);
        Assert.Contains(function.Warnings, warning => warning.Code == "FilterNotPublishableInV1");
    }

    private ApiFunctionMetadata SingleFunction(string script)
    {
        var result = _service.Analyze(script);
        Assert.True(result.ParsedSuccessfully, string.Join(Environment.NewLine, result.SyntaxErrors.Select(error => error.Message)));
        return Assert.Single(result.Functions);
    }

    private ApiParameterMetadata SingleParameter(string script)
    {
        var function = SingleFunction(script);
        return Assert.Single(function.Parameters);
    }

    private static void AssertParameter(ApiFunctionMetadata function, string name, string expectedType, bool isSwitch)
    {
        var parameter = function.Parameters.Single(parameter => parameter.Name == name);
        Assert.Equal(expectedType, parameter.DeclaredTypeName);
        Assert.True(parameter.HasExplicitType);
        Assert.Equal(isSwitch, parameter.IsSwitch);
    }
}
