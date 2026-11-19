using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class IntelliSenseProviderCoordinatorTests
{
    [Fact]
    public async Task PrefersPsesAndNormalizesDuplicateCandidates()
    {
        var legacyCalls = 0;
        var coordinator = new IntelliSenseProviderCoordinator();
        var result = await coordinator.QueryAsync(
            queryPses: true,
            queryLegacy: true,
            documentLength: 10,
            _ => Task.FromResult<CompletionServiceResult?>(new CompletionServiceResult(2, 3,
                new[]
                {
                    new CompletionServiceItem("Zulu", "Zulu", CompletionItemKind.Command, ""),
                    new CompletionServiceItem("alpha", "Alpha", CompletionItemKind.Command, ""),
                    new CompletionServiceItem("ALPHA", "Alpha", CompletionItemKind.Command, "")
                })),
            _ =>
            {
                legacyCalls++;
                return Task.FromResult<CompletionServiceResult?>(CompletionServiceResult.Empty);
            },
            CancellationToken.None);

        Assert.Equal("authoritative-live", result.Source);
        Assert.Equal(0, legacyCalls);
        Assert.Equal(new[] { "alpha", "Zulu" }, result.Result!.Items.Select(item => item.CompletionText));
    }

    [Fact]
    public async Task InvalidPsesReplacementRangeFallsBackToPersistentHelper()
    {
        var coordinator = new IntelliSenseProviderCoordinator();
        var result = await coordinator.QueryAsync(
            true, true, 4,
            _ => Task.FromResult<CompletionServiceResult?>(new CompletionServiceResult(4, 1,
                new[] { new CompletionServiceItem("bad", "bad", CompletionItemKind.Command, "") })),
            _ => Task.FromResult<CompletionServiceResult?>(new CompletionServiceResult(0, 2,
                new[] { new CompletionServiceItem("Get-Item", "Get-Item", CompletionItemKind.Command, "") })),
            CancellationToken.None);

        Assert.Equal("engine", result.Source);
        Assert.Equal("Get-Item", Assert.Single(result.Result!.Items).CompletionText);
    }

    [Fact]
    public async Task CancellationDoesNotDispatchFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var legacyCalls = 0;
        var coordinator = new IntelliSenseProviderCoordinator();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => coordinator.QueryAsync(
            true, true, 4,
            _ =>
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            },
            _ =>
            {
                legacyCalls++;
                return Task.FromResult<CompletionServiceResult?>(CompletionServiceResult.Empty);
            },
            cancellation.Token));
        Assert.Equal(0, legacyCalls);
    }
}
