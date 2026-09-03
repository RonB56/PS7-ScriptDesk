using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class PowerShellCompletionTransportFramingTests
{
    private const string Start = "##PSSTUDIO_COMP_START_request##";
    private const string End = "##PSSTUDIO_COMP_END_request##";
    private const string ForeignStart = "##PSSTUDIO_COMP_START_foreign##";
    private const string ForeignEnd = "##PSSTUDIO_COMP_END_foreign##";

    [Fact]
    public void ExtractsNormalFramedResponse()
    {
        AssertExtracts($"{Start}\r\nPAYLOAD:QUJD\r\n{End}\r\n", "QUJD");
    }

    [Fact]
    public void ExtractsLfFramedResponse()
    {
        AssertExtracts($"{Start}\nPAYLOAD:QUJD\n{End}\n", "QUJD");
    }

    [Fact]
    public void ExtractsObservedDelimiterFreeResponse()
    {
        AssertExtracts($"{Start}PAYLOAD:QUJD{End}", "QUJD");
    }

    [Fact]
    public void ExtractsResponseDeliveredAcrossArbitraryChunks()
    {
        var response = $"prefix {Start}\nPAYLOAD:QUJD\n{End} suffix";
        var capture = string.Empty;

        foreach (var chunk in new[] { response[..3], response[3..17], response[17..31], response[31..] })
        {
            capture += chunk;
        }

        AssertExtracts(capture, "QUJD");
    }

    [Fact]
    public void IgnoresUnrelatedOutputAroundValidResponse()
    {
        AssertExtracts($"warning\r\n{Start}PAYLOAD:QUJD{End}\r\nready", "QUJD");
    }

    [Fact]
    public void ForeignRequestMarkersDoNotSatisfyActiveRequest()
    {
        var response = $"{ForeignStart}PAYLOAD:RE9O\n{ForeignEnd}{Start}PAYLOAD:QUJD{End}";

        AssertExtracts(response, "QUJD");
    }

    [Fact]
    public void RejectsIncompleteStartMarker()
    {
        Assert.False(PowerShellCompletionService.TryExtractPayloadBlock(Start[..^2], Start, End, out _));
    }

    [Fact]
    public void RejectsMissingEndMarker()
    {
        Assert.False(PowerShellCompletionService.TryExtractPayloadBlock($"{Start}PAYLOAD:QUJD", Start, End, out _));
    }

    [Fact]
    public void RejectsMalformedPayloadPrefix()
    {
        Assert.False(PowerShellCompletionService.TryExtractPayloadBlock($"{Start}DATA:QUJD{End}", Start, End, out _));
    }

    [Fact]
    public void RejectsEmptyPayload()
    {
        Assert.False(PowerShellCompletionService.TryExtractPayloadBlock($"{Start}PAYLOAD:{End}", Start, End, out _));
    }

    [Fact]
    public void ConsecutiveRequestEnvelopesUseTheirOwnMarkers()
    {
        var first = $"{Start}PAYLOAD:QUJD{End}";
        var secondStart = Start.Replace("request", "request2", StringComparison.Ordinal);
        var secondEnd = End.Replace("request", "request2", StringComparison.Ordinal);

        AssertExtracts(first + secondStart + "PAYLOAD:REVG" + secondEnd, "QUJD");
        AssertExtracts(first + secondStart + "PAYLOAD:REVG" + secondEnd, secondStart, secondEnd, "REVG");
    }

    [Fact]
    public void GenerationBoundaryCanUseNewMarkersWithoutStalePayload()
    {
        var generationOne = $"{ForeignStart}PAYLOAD:RE9O{ForeignEnd}";
        var generationTwo = $"{Start}PAYLOAD:QUJD{End}";

        AssertExtracts(generationOne + generationTwo, "QUJD");
    }

    [Fact]
    public void GeneratedCompletionCommandDoesNotEchoLiteralActiveMarkers()
    {
        var command = PowerShellCompletionService.BuildCompletionCommand("Get-Ch", 6, Start, End);

        Assert.DoesNotContain(Start, command, StringComparison.Ordinal);
        Assert.DoesNotContain(End, command, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedQuickInfoCommandDoesNotEchoLiteralActiveMarkers()
    {
        var command = PowerShellCompletionService.BuildCommandQuickInfoCommand("Get-ChildItem", Start, End, includeHelp: false);

        Assert.DoesNotContain(Start, command, StringComparison.Ordinal);
        Assert.DoesNotContain(End, command, StringComparison.Ordinal);
    }

    [Fact]
    public void QuickInfoPreloadCommandIsBoundedAndDoesNotEchoActiveMarkers()
    {
        var command = PowerShellCompletionService.BuildQuickInfoPreloadCommand();

        Assert.True(command.Length < 2500);
        Assert.DoesNotContain(Start, command, StringComparison.Ordinal);
        Assert.DoesNotContain(End, command, StringComparison.Ordinal);
    }

    [Fact]
    public void GeneratedCommandCatalogCommandDoesNotEchoLiteralActiveMarkers()
    {
        var command = PowerShellCompletionService.BuildCommandCatalogCommand(Start, End);

        Assert.DoesNotContain(Start, command, StringComparison.Ordinal);
        Assert.DoesNotContain(End, command, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QuickInfoTransportStillCompletesWithEncodedMarkers()
    {
        var runtime = ResolvePwshExecutablePath();
        if (runtime is null)
        {
            return;
        }

        using var service = new PowerShellCompletionService();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var quickInfo = await service.GetCommandQuickInfoAsync("Get-ChildItem", runtime, requireParameters: false, cancellationToken: cancellation.Token);

        Assert.NotNull(quickInfo);
        Assert.Equal("Get-ChildItem", quickInfo!.Title, ignoreCase: true);
    }

    private static void AssertExtracts(string response, string expectedPayload)
        => AssertExtracts(response, Start, End, expectedPayload);

    private static void AssertExtracts(string response, string startMarker, string endMarker, string expectedPayload)
    {
        var extracted = PowerShellCompletionService.TryExtractPayloadBlock(
            response,
            startMarker,
            endMarker,
            out var payload);

        Assert.True(extracted);
        Assert.Equal(expectedPayload, payload);
    }

    private static string? ResolvePwshExecutablePath()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PWSH"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cache", "codex-runtimes", "codex-primary-runtime", "dependencies", "native", "powershell", "pwsh.exe"),
            @"C:\Program Files\PowerShell\7\pwsh.exe"
        };

        return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate));
    }
}
