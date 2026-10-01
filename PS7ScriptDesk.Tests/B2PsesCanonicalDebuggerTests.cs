using System.Collections.Concurrent;
using System.Text.Json;
using PS7ScriptDesk.Tests.Dap;

namespace PS7ScriptDesk.Tests;

public sealed class B2PsesCanonicalDebuggerTests
{
    [Fact(Timeout = 180_000)]
    public async Task CanonicalScriptLaunchesStopsAndReturnsFrameScopedVariables()
    {
        var scriptDirectory = Path.Combine(Path.GetTempPath(), "PS7ScriptDesk-B2", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scriptDirectory);
        var scriptPath = Path.Combine(scriptDirectory, "Canonical.ps1");
        var script = string.Join(Environment.NewLine, new[]
        {
            "function FunctionA {",
            "    param(",
            "        [string]$ParamA",
            "    )",
            "",
            "    $LocalA = \"Local-A\"",
            "    FunctionB -ParamB \"Parameter-B\"",
            "}",
            "",
            "function FunctionB {",
            "    param(",
            "        [string]$ParamB",
            "    )",
            "",
            "    $LocalB = \"Local-B\"",
            "    FunctionC",
            "}",
            "",
            "function FunctionC {",
            "    $LocalC = \"Local-C\"",
            "    Write-Host \"Pause Here\"",
            "}",
            "",
            "FunctionA -ParamA \"Parameter-A\""
        });
        await File.WriteAllTextAsync(scriptPath, script);
        var breakpointLine = Array.FindIndex(script.Split(Environment.NewLine), line => line.Contains("Write-Host \"Pause Here\"", StringComparison.Ordinal)) + 1;
        var events = new DapEventInbox();
        var traffic = new ConcurrentQueue<DapMessage>();

        try
        {
            await using var host = await PsesHostProcess.StartAsync(
                Environment.GetEnvironmentVariable("PS7SD_B1_PSES_ROOT"),
                TimeSpan.FromSeconds(90));
            await using var connection = await host.ConnectDebugPipeAsync(TimeSpan.FromSeconds(15), events.Add, messageSent: traffic.Enqueue, messageReceived: traffic.Enqueue);

            var initialize = await connection.SendRequestAsync(
                "initialize",
                new
                {
                    adapterID = "PS7ScriptDesk-B2",
                    clientID = "PS7ScriptDesk-B2",
                    clientName = "PS7 ScriptDesk B2 Harness",
                    linesStartAt1 = true,
                    columnsStartAt1 = true,
                    pathFormat = "path",
                    supportsVariablePaging = false,
                    supportsRunInTerminalRequest = false
                },
                TimeSpan.FromSeconds(15));

            Assert.True(initialize.Success);
            var capabilities = initialize.Body!.Value;
            var supportsConfigurationDone = capabilities.TryGetProperty("supportsConfigurationDoneRequest", out var configurationDone) && configurationDone.ValueKind == JsonValueKind.True;

            var launch = await connection.SendRequestAsync(
                "launch",
                new
                {
                    program = scriptPath,
                    script = scriptPath,
                    cwd = scriptDirectory,
                    args = Array.Empty<string>(),
                    noDebug = false,
                    createTemporaryIntegratedConsole = false,
                    internalConsoleOptions = "neverOpen"
                },
                TimeSpan.FromSeconds(20));
            Assert.True(launch.Success, launch.Message);

            var initialized = await events.WaitForAsync("initialized", TimeSpan.FromSeconds(20));
            Assert.Equal("initialized", initialized.Event);

            var breakpoints = await connection.SendRequestAsync(
                "setBreakpoints",
                new
                {
                    source = new { path = scriptPath },
                    breakpoints = new[] { new { line = breakpointLine } },
                    lines = new[] { breakpointLine }
                },
                TimeSpan.FromSeconds(15));
            Assert.True(breakpoints.Success, breakpoints.Message);
            var breakpoint = breakpoints.Body!.Value.GetProperty("breakpoints")[0];
            Assert.True(!breakpoint.TryGetProperty("verified", out var verified) || verified.ValueKind != JsonValueKind.False);
            Assert.Equal(breakpointLine, breakpoint.GetProperty("line").GetInt32());

            if (supportsConfigurationDone)
            {
                var configuration = await connection.SendRequestAsync("configurationDone", null, TimeSpan.FromSeconds(15));
                Assert.True(configuration.Success, configuration.Message);
            }

            var stopped = await events.WaitForAsync("stopped", TimeSpan.FromSeconds(30));
            var stoppedBody = stopped.Body!.Value;
            var stoppedThreadId = stoppedBody.GetProperty("threadId").GetInt32();
            Assert.Equal("breakpoint", stoppedBody.GetProperty("reason").GetString());

            var threads = await connection.SendRequestAsync("threads", null, TimeSpan.FromSeconds(15));
            Assert.True(threads.Success, threads.Message);
            var thread = threads.Body!.Value.GetProperty("threads").EnumerateArray().Single(value => value.GetProperty("id").GetInt32() == stoppedThreadId);

            var stack = await connection.SendRequestAsync("stackTrace", new { threadId = stoppedThreadId, levels = 50 }, TimeSpan.FromSeconds(15));
            Assert.True(stack.Success, stack.Message);
            var frames = stack.Body!.Value.GetProperty("stackFrames").EnumerateArray().ToArray();
            var functionC = frames.Single(frame => frame.GetProperty("name").GetString() == "FunctionC");
            var functionB = frames.Single(frame => frame.GetProperty("name").GetString() == "FunctionB");
            var functionA = frames.Single(frame => frame.GetProperty("name").GetString() == "FunctionA");
            Assert.NotEqual(functionC.GetProperty("id").GetInt32(), functionB.GetProperty("id").GetInt32());
            Assert.NotEqual(functionB.GetProperty("id").GetInt32(), functionA.GetProperty("id").GetInt32());

            var functionCVariables = await ReadFrameVariablesAsync(connection, functionC.GetProperty("id").GetInt32());
            var functionBVariables = await ReadFrameVariablesAsync(connection, functionB.GetProperty("id").GetInt32());
            var functionAVariables = await ReadFrameVariablesAsync(connection, functionA.GetProperty("id").GetInt32());

            Assert.True(
                TryGetScopeValue(functionCVariables, "Command", "LocalC") == "Local-C" &&
                TryGetScopeValue(functionBVariables, "Command", "LocalB") == "Local-B" &&
                TryGetScopeValue(functionBVariables, "Command", "ParamB") == "Parameter-B" &&
                TryGetScopeValue(functionAVariables, "Command", "LocalA") == "Local-A" &&
                TryGetScopeValue(functionAVariables, "Command", "ParamA") == "Parameter-A",
                $"C={FormatVariables("FunctionC", functionCVariables)}; B={FormatVariables("FunctionB", functionBVariables)}; A={FormatVariables("FunctionA", functionAVariables)}; Traffic={FormatTraffic(traffic)}");
            Assert.DoesNotContain("LocalA", functionCVariables["Command"].Keys);
            Assert.DoesNotContain("LocalB", functionCVariables["Command"].Keys);
            Assert.DoesNotContain("LocalA", functionBVariables["Command"].Keys);
            Assert.DoesNotContain("LocalB", functionAVariables["Command"].Keys);
            Assert.DoesNotContain("LocalC", functionAVariables["Command"].Keys);

            var disconnect = await connection.SendRequestAsync("disconnect", new { terminateDebuggee = true, restart = false }, TimeSpan.FromSeconds(15));
            Assert.True(disconnect.Success, disconnect.Message);
        }
        finally
        {
            try { Directory.Delete(scriptDirectory, recursive: true); } catch { }
        }
    }

    private static string FormatVariables(string frameName, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> scopes) => $"{frameName}: {string.Join("; ", scopes.Select(scope => $"{scope.Key}[{string.Join(", ", scope.Value.Select(pair => $"{pair.Key}={pair.Value}"))}]"))}";

    private static string? TryGetScopeValue(IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> scopes, string scopeName, string variableName) => scopes.TryGetValue(scopeName, out var scope) && scope.TryGetValue(variableName, out var value) ? value : null;

    private static string FormatTraffic(IEnumerable<DapMessage> traffic) => string.Join(" | ", traffic.Where(message => message.Command is "stackTrace" or "scopes" or "variables" || message.Event == "stopped").Select(message =>
    {
        var detail = message.Body is JsonElement body && body.TryGetProperty("scopes", out var scopes)
            ? $"scopes={string.Join(",", scopes.EnumerateArray().Select(scope => $"{scope.GetProperty("name").GetString()}:{scope.GetProperty("variablesReference").GetInt32()}"))}"
            : message.Body is JsonElement variableBody && variableBody.TryGetProperty("variables", out var variables)
                ? $"variables={string.Join(",", variables.EnumerateArray().Take(20).Select(variable => variable.GetProperty("name").GetString()))}"
                : message.Arguments is JsonElement arguments && arguments.TryGetProperty("frameId", out var frameId)
                    ? $"frameId={frameId.GetInt32()}"
                    : message.Event is not null ? $"event={message.Event}" : string.Empty;
        return $"{message.Type} seq={message.Seq} cmd={message.Command} request_seq={message.RequestSeq} {detail}";
    }));

    private static async Task<Dictionary<string, IReadOnlyDictionary<string, string>>> ReadFrameVariablesAsync(DapConnection connection, int frameId)
    {
        var scopes = await connection.SendRequestAsync("scopes", new { frameId }, TimeSpan.FromSeconds(15));
        Assert.True(scopes.Success, scopes.Message);
        var allScopes = scopes.Body!.Value.GetProperty("scopes").EnumerateArray().ToArray();
        var result = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var scope in allScopes)
        {
            var variables = await connection.SendRequestAsync("variables", new { variablesReference = scope.GetProperty("variablesReference").GetInt32() }, TimeSpan.FromSeconds(15));
            Assert.True(variables.Success, variables.Message);
            result[scope.GetProperty("name").GetString()!] = variables.Body!.Value.GetProperty("variables").EnumerateArray().Where(variable => variable.TryGetProperty("name", out _)).ToDictionary(variable => variable.GetProperty("name").GetString()!.TrimStart('$'), variable => NormalizeValue(variable.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty), StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    private static string NormalizeValue(string value) => value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;
}

internal sealed class DapEventInbox
{
    private readonly ConcurrentQueue<DapMessage> _events = new();

    public void Add(DapMessage message) => _events.Enqueue(message);

    public async Task<DapMessage> WaitForAsync(string eventName, TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        while (true)
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_events.TryDequeue(out var message))
            {
                if (string.Equals(message.Event, eventName, StringComparison.OrdinalIgnoreCase)) return message;
                continue;
            }

            await Task.Delay(25, cancellation.Token);
        }
    }
}
