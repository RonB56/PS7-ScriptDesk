using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellExecutionRequestClassifierTests
{
    private readonly PowerShellExecutionRequestClassifier _classifier = new();

    [Theory]
    [InlineData("Read-Host 'Name'")]
    [InlineData("Read-Host -AsSecureString 'Password'")]
    [InlineData("Microsoft.PowerShell.Utility\\Read-Host 'Name'")]
    [InlineData("$Host.UI.PromptForChoice('Title', 'Message', @(), 0)")]
    [InlineData("$Host.UI.PromptForCredential('Title', 'Message', 'user', '')")]
    [InlineData("$Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')")]
    public void InteractiveAndHostInput_IsKnownInteractive(string script)
    {
        var result = _classifier.Classify(Request(script));

        Assert.Equal(ExecutionRequestClassification.KnownInteractive, result.Classification);
        Assert.True(result.RequiredCapabilities.RequiresInteractiveInput);
    }

    [Fact]
    public void SecurePrompt_RequiresSecureInput()
    {
        var result = _classifier.Classify(Request("Read-Host -AsSecureString 'Password'"));

        Assert.True(result.RequiredCapabilities.RequiresSecureInput);
    }

    [Theory]
    [InlineData("Write-Output 'hello'")]
    [InlineData("Write-Host 'hello'")]
    [InlineData("Get-Process | Select-Object -First 1")]
    [InlineData("Get-Location; Set-Location $pwd")]
    [InlineData("function Get-Value { Write-Output 'value' }; Get-Value")]
    public void ProvenSafeCommands_AreKnownNonInteractive(string script)
    {
        var result = _classifier.Classify(Request(script));

        Assert.Equal(ExecutionRequestClassification.KnownNonInteractive, result.Classification);
    }

    [Theory]
    [InlineData("Invoke-Expression $code")]
    [InlineData("$command = 'Get-Process'; & $command")]
    [InlineData(". $path")]
    [InlineData("Import-Module SomeModule")]
    [InlineData("Some-UnknownCommand")]
    public void DynamicExternalOrUnclassifiedCommands_AreUnknown(string script)
    {
        var result = _classifier.Classify(Request(script));

        Assert.Equal(ExecutionRequestClassification.Unknown, result.Classification);
    }

    [Theory]
    [InlineData("# Read-Host is only a comment")]
    [InlineData("'Read-Host is only a string'")]
    public void CommentsAndStrings_DoNotBecomeInteractive(string script)
    {
        var result = _classifier.Classify(Request(script));

        Assert.NotEqual(ExecutionRequestClassification.KnownInteractive, result.Classification);
    }

    private static EditorExecutionRequest Request(string script)
        => new(Guid.NewGuid(), 1, EditorExecutionMode.ScriptCall, "Classifier.ps1", script);
}
