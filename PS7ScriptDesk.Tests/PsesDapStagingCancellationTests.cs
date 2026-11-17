using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

public sealed class PsesDapStagingCancellationTests
{
    [Fact]
    public async Task CancellationDuringStage_WaitsForTheMatchingDapTerminalResponse()
    {
        var pipeName = "PS7SD-Test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = new CancellationTokenSource();
        var submitted = false;
        var stageSettled = false;
        var evaluation = PsesDapEvaluationClient.EvaluateAsync(
            @"\\.\pipe\" + pipeName, Guid.NewGuid(), 4, "$global:testLauncher = {}", cancellation.Token,
            () => submitted = true, _ => stageSettled = true);

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var initialize = await ReadMessageAsync(server))
        {
            Assert.Equal("initialize", initialize.RootElement.GetProperty("command").GetString());
            await WriteResponseAsync(server, 1, "{}");
        }
        using (var stage = await ReadMessageAsync(server))
        {
            Assert.Equal("evaluate", stage.RootElement.GetProperty("command").GetString());
            Assert.Equal(2, stage.RootElement.GetProperty("seq").GetInt32());
        }

        cancellation.Cancel();
        using (var cancel = await ReadMessageAsync(server).WaitAsync(TimeSpan.FromSeconds(5)))
        {
            Assert.Equal("cancel", cancel.RootElement.GetProperty("command").GetString());
            Assert.False(evaluation.IsCompleted);
            Assert.False(stageSettled);
            await WriteResponseAsync(server, 2, "{\"result\":\"staged\"}");
        }

        var result = await evaluation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(submitted);
        Assert.True(stageSettled);
        Assert.Equal(PS7ScriptDesk.Domain.Models.EditorExecutionStatus.Completed, result.Status);
        Assert.True(result.ResponseReceived);
    }

    [Theory]
    [InlineData("PS7SD_CLEANUP_ACK_", true)]
    [InlineData("PS7SD_CLEANUP_PRESENT_", false)]
    public async Task CleanupRequiresCorrelatedLauncherAbsenceAcknowledgment(string markerPrefix, bool expected)
    {
        var pipeName = "PS7SD-Test-" + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var requestId = Guid.NewGuid();
        const string launcher = "__ps7sd_editor_request";
        var cleanup = PsesDapEvaluationClient.RemoveStagedScriptAsync(@"\\.\pipe\" + pipeName, requestId, 4, launcher);

        await server.WaitForConnectionAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using (var initialize = await ReadMessageAsync(server)) await WriteResponseAsync(server, 1, "{}");
        using (var evaluate = await ReadMessageAsync(server))
        {
            var expression = evaluate.RootElement.GetProperty("arguments").GetProperty("expression").GetString();
            Assert.Contains($"Remove-Variable -Name '{launcher}' -Scope Global", expression, StringComparison.Ordinal);
            Assert.Contains($"Get-Variable -Name '{launcher}' -Scope Global", expression, StringComparison.Ordinal);
            var marker = markerPrefix + requestId.ToString("N");
            await WriteResponseAsync(server, 2, "{\"result\":\"" + marker + "\"}");
        }
        if (expected)
        {
            using var disconnect = await ReadMessageAsync(server);
            Assert.Equal("disconnect", disconnect.RootElement.GetProperty("command").GetString());
            await WriteResponseAsync(server, 3, "{}");
        }

        Assert.Equal(expected, await cleanup.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static async Task<JsonDocument> ReadMessageAsync(Stream stream)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one);
            header.Add(one[0]);
            if (header.Count >= 4 && header[^4..].SequenceEqual(new byte[] { 13, 10, 13, 10 })) break;
        }
        var text = Encoding.ASCII.GetString(header.ToArray());
        var length = int.Parse(text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim());
        var body = new byte[length];
        await stream.ReadExactlyAsync(body);
        return JsonDocument.Parse(body);
    }

    private static async Task WriteResponseAsync(Stream stream, int requestSequence, string body)
    {
        var message = Encoding.UTF8.GetBytes($"{{\"type\":\"response\",\"request_seq\":{requestSequence},\"success\":true,\"body\":{body}}}");
        var header = Encoding.ASCII.GetBytes($"Content-Length: {message.Length}\r\n\r\n");
        await stream.WriteAsync(header);
        await stream.WriteAsync(message);
        await stream.FlushAsync();
    }
}
