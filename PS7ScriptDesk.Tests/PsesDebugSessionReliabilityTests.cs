using System.Collections;
using System.Diagnostics;
using System.Reflection;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection("DiagnosticReliability")]
public sealed class PsesDebugSessionReliabilityTests
{
    private static readonly Type SessionType = typeof(DebugSessionState).Assembly.GetType("PS7ScriptDesk.Shell.Debug.PsesDebugSession", throwOnError: true)!;

    [Fact]
    public void ThrowingBreakpointSubscriber_DoesNotFaultLaterReaderProcessing()
    {
        var session = CreateSession();
        AddEventHandler(session, "BreakpointHit", (Action<string?, int>)((_, _) => throw new InvalidOperationException("breakpoint subscriber failure")));
        var exception = Record.Exception(() =>
        {
            Assert.True((bool)Invoke(session, "TryHandleObservedDebugPauseOutput", "At C:\\test.ps1:4 char:1", "stdout")!);
            Invoke(session, "ProcessIncomingLine", "reader remains available", false);
        });

        Assert.Null(exception);
        Assert.Equal(DebugSessionState.Paused, GetCurrentState(session));
    }

    [Fact]
    public void ThrowingStateChangedSubscriber_PreservesStateAndDoesNotFaultReaderProcessing()
    {
        var session = CreateSession();
        AddEventHandler(session, "StateChanged", (Action<DebugSessionState>)(_ => throw new InvalidOperationException("state subscriber failure")));

        var exception = Record.Exception(() =>
        {
            Invoke(session, "SetCurrentState", DebugSessionState.Running, "test", null);
            Invoke(session, "ProcessIncomingLine", "reader remains available", false);
        });

        Assert.Null(exception);
        Assert.Equal(DebugSessionState.Running, GetCurrentState(session));
    }

    [Fact]
    public void ThrowingOutputSubscriber_DoesNotFaultStdoutProcessing()
    {
        var session = CreateSession();
        var delivered = 0;
        AddEventHandler(session, "OutputReceived", (Action<string>)(_ => throw new InvalidOperationException("stdout subscriber failure")));
        AddEventHandler(session, "OutputReceived", (Action<string>)(_ => delivered++));

        var exception = Record.Exception(() => Invoke(session, "ProcessIncomingLine", "stdout content", false));

        Assert.Null(exception);
        Assert.Equal(0, delivered); // Combined multicast semantics stop after the throwing subscriber.
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Fact]
    public void ThrowingOutputSubscriber_DoesNotFaultStderrProcessing()
    {
        var session = CreateSession();
        AddEventHandler(session, "OutputReceived", (Action<string>)(_ => throw new InvalidOperationException("stderr subscriber failure")));

        var exception = Record.Exception(() => Invoke(session, "ProcessIncomingLine", "stderr content", true));

        Assert.Null(exception);
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Fact]
    public async Task ThrowingSessionEndedSubscriber_IsContainedAndRecordedAtErrorSeverity()
    {
        var session = CreateSession();
        AddEventHandler(session, "SessionEnded", (Action)(() => throw new InvalidOperationException("session-ended subscriber failure")));

        var diagnostics = await CaptureDiagnosticsAsync(() => Invoke(session, "HandleSessionEndedMarker"));

        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
        Assert.Contains("\"Level\": \"Error\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("Debugger SessionEnded subscriber failed", diagnostics, StringComparison.Ordinal);
        Assert.Contains("session-ended subscriber failure", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"eventName\": \"SessionEnded\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"publicationOrigin\": \"SessionEndedMarker\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"debuggerState\": \"Stopped\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"processId\": -1", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"subscriberCount\": 1", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmedProcessExit_PublishesSessionEndedExactlyOnce()
    {
        var session = CreateSession();
        var sessionEndedCount = 0;
        AddEventHandler(session, "SessionEnded", (Action)(() => sessionEndedCount++));

        using var process = StartExitedProcess();
        SetField(session, "_process", process);

        await InvokeTaskAsync(session, "ReadLoopAsync", process, new StringReader(string.Empty), false, CancellationToken.None);
        Invoke(session, "HandleProcessExited", process);

        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
        Assert.Equal(1, sessionEndedCount);
    }

    [Fact]
    public async Task Cancellation_DoesNotIndependentlyPublishSessionEnded()
    {
        var session = CreateSession();
        var sessionEndedCount = 0;
        AddEventHandler(session, "SessionEnded", (Action)(() => sessionEndedCount++));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await InvokeTaskAsync(session, "ReadLoopAsync", new Process(), new StringReader(string.Empty), false, cancellation.Token);

        Assert.Equal(0, sessionEndedCount);
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Fact]
    public async Task DisposedReader_DoesNotIndependentlyPublishSessionEnded()
    {
        var session = CreateSession();
        var sessionEndedCount = 0;
        AddEventHandler(session, "SessionEnded", (Action)(() => sessionEndedCount++));
        var reader = new StringReader(string.Empty);
        reader.Dispose();

        await InvokeTaskAsync(session, "ReadLoopAsync", new Process(), reader, false, CancellationToken.None);

        Assert.Equal(0, sessionEndedCount);
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Fact]
    public async Task EndOfStreamWhileProcessIsAlive_InitiatesBoundedTeardownWithoutDeadlock()
    {
        var session = CreateSession();
        using var process = new Process();
        SetField(session, "_process", process);

        var readLoop = GetTask(session, "ReadLoopAsync", process, new StringReader(string.Empty), false, CancellationToken.None);
        Assert.Same(readLoop, await Task.WhenAny(readLoop, Task.Delay(TimeSpan.FromSeconds(2))));
        await (Task<bool>)Invoke(session, "StopAsync", CancellationToken.None)!;

        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnexpectedReaderFailure_InitiatesBoundedTeardownAndCancelsPeerReader(bool isErrorStream)
    {
        var session = CreateSession();
        using var process = new Process();
        using var lifetime = new CancellationTokenSource();
        SetField(session, "_process", process);
        SetField(session, "_lifetimeCancellationTokenSource", lifetime);

        var readLoop = GetTask(session, "ReadLoopAsync", process, new ThrowingReader(), isErrorStream, lifetime.Token);
        Assert.Same(readLoop, await Task.WhenAny(readLoop, Task.Delay(TimeSpan.FromSeconds(2))));
        await (Task<bool>)Invoke(session, "StopAsync", CancellationToken.None)!;

        Assert.True(lifetime.IsCancellationRequested);
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Fact]
    public async Task ExitAndTeardownRace_PublishesSessionEndedExactlyOnce()
    {
        var session = CreateSession();
        var sessionEndedCount = 0;
        AddEventHandler(session, "SessionEnded", (Action)(() => sessionEndedCount++));
        using var process = StartExitedProcess();
        SetField(session, "_process", process);

        await InvokeTaskAsync(session, "ReadLoopAsync", process, new StringReader(string.Empty), false, CancellationToken.None);
        await (Task<bool>)Invoke(session, "StopAsync", CancellationToken.None)!;

        Assert.Equal(1, sessionEndedCount);
    }

    [Theory]
    [InlineData("{ invalid json")]
    [InlineData("[ { invalid json")]
    public async Task MalformedVariablesPayload_LogsOnceAndReturnsEmpty(string payload)
    {
        var session = CreateSession();
        IReadOnlyList<object>? result = null;

        var diagnostics = await CaptureDiagnosticsAsync(() => result = DeserializeList(session, typeof(DebugVariableInfo), payload, "VariablesRequest"));

        Assert.Empty(result!);
        Assert.Equal(1, CountOccurrences(diagnostics, "Debugger protocol payload could not be parsed."));
        Assert.Contains("\"requestSource\": \"VariablesRequest\"", diagnostics, StringComparison.Ordinal);
        Assert.Contains("\"collectionParseFailed\": true", diagnostics, StringComparison.Ordinal);
        Assert.DoesNotContain(payload, diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedCallStackPayload_LogsOnceAndReturnsEmpty()
    {
        var session = CreateSession();
        IReadOnlyList<object>? result = null;

        var diagnostics = await CaptureDiagnosticsAsync(() => result = DeserializeList(session, typeof(DebugCallStackFrame), "{ invalid call-stack json", "CallStackRequest"));

        Assert.Empty(result!);
        Assert.Equal(1, CountOccurrences(diagnostics, "Debugger protocol payload could not be parsed."));
        Assert.Contains("\"requestSource\": \"CallStackRequest\"", diagnostics, StringComparison.Ordinal);
    }

    [Fact]
    public void SingleObjectListFallback_ReturnsOneItemWithoutDiagnostic()
    {
        var session = CreateSession();

        var variables = DeserializeList(session, typeof(DebugVariableInfo), "{\"Name\":\"v\",\"Type\":\"String\",\"Value\":\"x\"}", "VariablesRequest");
        var callStack = DeserializeList(session, typeof(DebugCallStackFrame), "{\"FunctionName\":\"f\",\"ScriptName\":\"s.ps1\",\"LineNumber\":1}", "CallStackRequest");

        Assert.Single(variables);
        Assert.Single(callStack);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("")]
    [InlineData("   \r\n\t")]
    public void EmptyListAndWhitespacePayloads_ReturnEmptyWithoutFailure(string payload)
    {
        var session = CreateSession();

        Assert.Empty(DeserializeList(session, typeof(DebugVariableInfo), payload, "VariablesRequest"));
    }

    [Theory]
    [InlineData("{ malformed location")]
    [InlineData("")]
    public void InvalidLegacyBreakpointPayload_RemainsPausedWithoutLocationNotificationOrNullHit(string payload)
    {
        var session = CreateSession();
        var hits = 0;
        AddEventHandler(session, "BreakpointHit", (Action<string?, int>)((_, _) => hits++));

        Invoke(session, "HandleBreakpointPayload", payload);

        Assert.Equal(DebugSessionState.Paused, GetCurrentState(session));
        Assert.False((bool)GetField(session, "_ignoreNextDebugPrompt")!);
        Assert.Equal(0L, (long)GetField(session, "_lastLocationNotificationTicks")!);
        Assert.Equal(0, hits);
    }

    [Fact]
    public void ValidLegacyBreakpointPayload_SetsPromptSuppressionAndRaisesResolvableLocation()
    {
        var session = CreateSession();
        string? actualPath = null;
        var actualLine = 0;
        AddEventHandler(session, "BreakpointHit", (Action<string?, int>)((path, line) => { actualPath = path; actualLine = line; }));

        Invoke(session, "HandleBreakpointPayload", "{\"ScriptPath\":\"C:\\\\test.ps1\",\"LineNumber\":4}");

        Assert.True((bool)GetField(session, "_ignoreNextDebugPrompt")!);
        Assert.True((long)GetField(session, "_lastLocationNotificationTicks")! > 0);
        Assert.Equal("C:\\test.ps1", actualPath);
        Assert.Equal(4, actualLine);
    }

    [Fact]
    public void CurrentFrameDefaultObject_IsValidNoLocationResult_NotMalformed()
    {
        var session = CreateSession();
        var locationType = SessionType.GetNestedType("BreakpointLocation", BindingFlags.NonPublic)!;
        var arguments = new object?[] { "{\"ScriptPath\":\"\",\"LineNumber\":0}", "CurrentFrameRequest", true, null };

        var result = InvokeGeneric(session, "DeserializeSingle", locationType, arguments);

        Assert.NotNull(result);
        Assert.False((bool)arguments[3]!);
        Assert.Equal(DebugSessionState.Stopped, GetCurrentState(session));
    }

    [Theory]
    [InlineData("{ malformed current frame")]
    [InlineData("")]
    public void InvalidCurrentFramePayload_IsMarkedMalformedAndDoesNotChangePausedState(string payload)
    {
        var session = CreateSession();
        Invoke(session, "SetCurrentState", DebugSessionState.Paused, "test", null);
        var locationType = SessionType.GetNestedType("BreakpointLocation", BindingFlags.NonPublic)!;
        var arguments = new object?[] { payload, "CurrentFrameRequest", true, null };

        var result = InvokeGeneric(session, "DeserializeSingle", locationType, arguments);

        Assert.Null(result);
        Assert.True((bool)arguments[3]!);
        Assert.Equal(DebugSessionState.Paused, GetCurrentState(session));
        Assert.Equal(0L, (long)GetField(session, "_lastLocationNotificationTicks")!);
    }

    [Fact]
    public void ActiveInspectionStderrLocation_IsNotPublishedAsBreakpointHit()
    {
        var session = CreateSession();
        var hits = 0;
        AddEventHandler(session, "BreakpointHit", (Action<string?, int>)((_, _) => hits++));

        var requestType = SessionType.GetNestedType("ActiveRequest", BindingFlags.NonPublic)!;
        var requestConstructor = requestType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(Guid), typeof(string), typeof(string), typeof(bool) },
            modifiers: null)!;
        var request = requestConstructor.Invoke(new object?[] { Guid.NewGuid(), "__BEGIN__", "__END__", true });
        SetField(session, "_activeRequest", request);

        Invoke(session, "ProcessIncomingLine", "At C:\\Users\\rbarn\\Downloads\\TestDebug.ps1:1 char:1", true);

        Assert.Equal(0, hits);
    }

    [Fact]
    public void StderrLocationWithoutInspectionRequest_RemainsAValidBreakpointSource()
    {
        var session = CreateSession();
        var hits = 0;
        AddEventHandler(session, "BreakpointHit", (Action<string?, int>)((_, _) => hits++));

        Invoke(session, "ProcessIncomingLine", "At C:\\Users\\rbarn\\Downloads\\TestDebug.ps1:5 char:1", true);

        Assert.Equal(1, hits);
    }

    [Fact]
    public void CallStackHelper_UsesScalarLineNumberAndAlwaysClosesItsOwnedMarker()
    {
        var method = SessionType.GetMethod("BuildCallStackRequestScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var script = (string)method.Invoke(null, new object?[] { "__BEGIN_REQUEST__", "__END_REQUEST__" })!;

        Assert.Contains("function __PSS_ConvertSingleFrameInt32", script, StringComparison.Ordinal);
        Assert.Contains("Frame field ' + $FieldName + ' contained '", script, StringComparison.Ordinal);
        Assert.Contains("$normalizedLineNumber = __PSS_ConvertSingleFrameInt32 $scriptLineNumber 'ScriptLineNumber'", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[int]@($_.ScriptLineNumber)", script, StringComparison.Ordinal);
        Assert.Contains("$ErrorActionPreference = 'Stop'", script, StringComparison.Ordinal);
        Assert.Contains("__PSS_REQUEST_ERROR__", script, StringComparison.Ordinal);
        Assert.Contains("} finally {", script, StringComparison.Ordinal);
        Assert.Contains("[Console]::Out.WriteLine('__END_REQUEST__')", script, StringComparison.Ordinal);
        Assert.Contains("GET_PSCALLSTACK_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("CURRENT_SCOPE_VARIABLES_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("CURRENT_SCOPE_VARIABLE_ITEM_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("CURRENT_SCOPE_VARIABLE_ITEM_THROW", script, StringComparison.Ordinal);
        Assert.Contains("CURRENT_SCOPE_VARIABLE_ITEM_END", script, StringComparison.Ordinal);
        Assert.Contains("CURRENT_SCOPE_VARIABLES_END", script, StringComparison.Ordinal);
        Assert.Contains("HOST_EXECUTION_ERROR", script, StringComparison.Ordinal);
        Assert.Contains("__PSS_D4A4ExceptionChain", script, StringComparison.Ordinal);
        Assert.Contains("Get-PSCallStack -ErrorAction Stop -ErrorVariable __PSSCallStackErrors", script, StringComparison.Ordinal);
        Assert.Contains("GET_PSCALLSTACK_ERROR_RECORD", script, StringComparison.Ordinal);
        Assert.Contains("GET_PSCALLSTACK_CATCH_ENTER", script, StringComparison.Ordinal);
        Assert.Contains("FRAME_ACQUIRED", script, StringComparison.Ordinal);
        Assert.Contains("GET_FRAME_VARIABLES_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("GET_FRAME_VARIABLES_THROW", script, StringComparison.Ordinal);
        Assert.Contains("VARIABLE_PROJECTION_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("SCRIPT_LINE_NORMALIZE_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("OBJECT_CONSTRUCTION_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("MATERIALIZATION_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("JSON_BEGIN", script, StringComparison.Ordinal);
        Assert.Contains("ENVELOPE_OUTPUT_END", script, StringComparison.Ordinal);
        Assert.Contains("__PSS_D4A4_FORENSIC__", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallStackHelper_HandlesObservedSingleElementObjectArrayFrameLineNumber()
    {
        var method = SessionType.GetMethod("BuildCallStackRequestScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var helper = (string)method.Invoke(null, new object?[] { "__BEGIN_REQUEST__", "__END_REQUEST__" })!;
        var harness = """
            $rawLineNumber = New-Object object[] 1
            $rawLineNumber[0] = 83
            $frame = [pscustomobject]@{
                ScriptLineNumber = $rawLineNumber
                FunctionName = 'Invoke-Level2'
                ScriptName = 'C:\Users\rbarn\Downloads\TestDebug.ps1'
                InvocationInfo = $null
            }
            $frame | Add-Member -MemberType ScriptMethod -Name GetFrameVariables -Value { @{} }
            function Get-PSCallStack { ,$frame }
            """ + Environment.NewLine + helper;
        var harnessPath = Path.Combine(Path.GetTempPath(), $"ps7scriptdesk-d4a2-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);

            Assert.True(process.Start());
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.ExitCode == 0, $"pwsh exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
            Assert.True(!stdout.Contains("__PSS_REQUEST_ERROR__", StringComparison.Ordinal), $"Unexpected helper error output: {stdout}");
            Assert.Contains("\"LineNumber\":83", stdout, StringComparison.Ordinal);
            Assert.Contains("__END_REQUEST__", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Cannot convert", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    [Fact]
    public async Task CallStackHelper_IsolatesScopeProjectionFromTypedUserValueParameter()
    {
        var method = SessionType.GetMethod("BuildCallStackRequestScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var helper = (string)method.Invoke(null, new object?[] { "__BEGIN_REQUEST__", "__END_REQUEST__" })!;
        var harness = """
            function Invoke-Level2 {
                param([int]$Value)
            """ + Environment.NewLine + helper + Environment.NewLine + """
            }
            Invoke-Level2 -Value 5
            """;
        var harnessPath = Path.Combine(Path.GetTempPath(), $"ps7scriptdesk-scope-collision-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);

            Assert.True(process.Start());
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.ExitCode == 0, $"pwsh exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
            Assert.Contains("CURRENT_SCOPE_VARIABLES_END", stdout, StringComparison.Ordinal);
            Assert.Contains("CURRENT_SCOPE_VARIABLE_ITEM_END|name=args", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("Cannot convert the \"System.Object[]\" value", stdout, StringComparison.Ordinal);
            Assert.False(stdout.Contains("__PSS_REQUEST_ERROR__", StringComparison.Ordinal), $"Unexpected helper error. stdout={stdout}; stderr={stderr}");
            Assert.DoesNotContain("ParserError", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    [Fact]
    public async Task CallStackHelper_RecordsGetPsCallStackFailureBoundary()
    {
        var method = SessionType.GetMethod("BuildCallStackRequestScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var helper = (string)method.Invoke(null, new object?[] { "__BEGIN_REQUEST__", "__END_REQUEST__" })!;
        var harness = """
            function Get-PSCallStack {
                [CmdletBinding()]
                param()
                Write-Error 'synthetic Get-PSCallStack failure'
            }
            """ + Environment.NewLine + helper;
        var harnessPath = Path.Combine(Path.GetTempPath(), $"ps7scriptdesk-d4a2-failure-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);

            Assert.True(process.Start());
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.ExitCode == 0, $"pwsh exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
            Assert.Contains("GET_PSCALLSTACK_BEGIN", stdout, StringComparison.Ordinal);
            Assert.Contains("GET_PSCALLSTACK_CATCH_ENTER", stdout, StringComparison.Ordinal);
            Assert.Contains("GET_PSCALLSTACK_THROW", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("GET_PSCALLSTACK_END", stdout, StringComparison.Ordinal);
            Assert.Contains("__PSS_REQUEST_ERROR__", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    [Fact]
    public async Task CallStackHelper_CapturesOuterExecutionErrorMetadataForPreambleFailure()
    {
        var method = SessionType.GetMethod("BuildCallStackRequestScript", BindingFlags.Static | BindingFlags.NonPublic)!;
        var helper = (string)method.Invoke(null, new object?[] { "__BEGIN_REQUEST__", "__END_REQUEST__" })!;
        var harness = """
            function Get-Variable {
                [CmdletBinding()]
                param([int]$Scope)
                throw [InvalidOperationException]::new('synthetic scope snapshot failure')
            }
            """ + Environment.NewLine + helper;
        var harnessPath = Path.Combine(Path.GetTempPath(), $"ps7scriptdesk-d4a7-preamble-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(harnessPath, harness);

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = @"C:\Program Files\PowerShell\7\pwsh.exe",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };
            process.StartInfo.ArgumentList.Add("-NoLogo");
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-File");
            process.StartInfo.ArgumentList.Add(harnessPath);

            Assert.True(process.Start());
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(process.ExitCode == 0, $"pwsh exit={process.ExitCode}; stdout={stdout}; stderr={stderr}");
            Assert.Contains("CURRENT_SCOPE_VARIABLES_BEGIN", stdout, StringComparison.Ordinal);
            Assert.Contains("HOST_EXECUTION_ERROR", stdout, StringComparison.Ordinal);
            Assert.Contains("stage=<stage-marker-before-failure>", stdout, StringComparison.Ordinal);
            Assert.Contains("System.InvalidOperationException", stdout, StringComparison.Ordinal);
            Assert.Contains("synthetic scope snapshot failure", stdout, StringComparison.Ordinal);
            Assert.Contains("__PSS_REQUEST_ERROR__", stdout, StringComparison.Ordinal);
            Assert.Contains("__END_REQUEST__", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(harnessPath);
        }
    }

    private static object CreateSession()
        => Activator.CreateInstance(SessionType, nonPublic: true)!;

    private static DebugSessionState GetCurrentState(object session)
        => (DebugSessionState)SessionType.GetProperty("CurrentState")!.GetValue(session)!;

    private static void AddEventHandler(object session, string eventName, Delegate handler)
        => SessionType.GetEvent(eventName)!.AddEventHandler(session, handler);

    private static void SetField(object session, string fieldName, object? value)
        => SessionType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, value);

    private static object? GetField(object session, string fieldName)
        => SessionType.GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session);

    private static object? Invoke(object session, string methodName, params object?[] arguments)
    {
        try
        {
            return GetMethod(methodName, arguments.Length).Invoke(session, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static object? InvokeGeneric(object session, string methodName, Type genericType, object?[] arguments)
    {
        try
        {
            return SessionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method => method.Name == methodName && method.IsGenericMethodDefinition)
                .MakeGenericMethod(genericType)
                .Invoke(session, arguments);
        }
        catch (TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw exception.InnerException;
        }
    }

    private static MethodInfo GetMethod(string methodName, int parameterCount)
        => SessionType.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(method => method.Name == methodName && method.GetParameters().Length == parameterCount);

    private static Task GetTask(object session, string methodName, params object?[] arguments)
        => (Task)Invoke(session, methodName, arguments)!;

    private static async Task InvokeTaskAsync(object session, string methodName, params object?[] arguments)
        => await GetTask(session, methodName, arguments).WaitAsync(TimeSpan.FromSeconds(3));

    private static IReadOnlyList<object> DeserializeList(object session, Type itemType, string payload, string source)
        => ((IEnumerable)InvokeGeneric(session, "DeserializeList", itemType, new object?[] { payload, source })!).Cast<object>().ToArray();

    private static Process StartExitedProcess()
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = "/c exit 0",
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        Assert.True(process.Start());
        Assert.True(process.WaitForExit(3000));
        return process;
    }

    private static async Task<string> CaptureDiagnosticsAsync(Action action)
    {
        DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings
        {
            IsDeveloperDiagnosticsEnabled = true,
            DeveloperDiagnosticsWriteJsonLines = true,
            DeveloperDiagnosticsWriteReadableLog = false
        }, "PsesDebugSession reliability test");

        var path = Path.Combine(DeveloperDiagnostics.CurrentSessionDirectory!, "developer-diagnostics.ndjson");
        var lengthBefore = File.Exists(path) ? new FileInfo(path).Length : 0;
        try
        {
            action();
        }
        finally
        {
            DeveloperDiagnostics.ConfigureFromSettings(new ApplicationSettings(), "PsesDebugSession reliability test cleanup");
        }

        var content = File.Exists(path) ? await File.ReadAllTextAsync(path) : string.Empty;
        return lengthBefore >= content.Length ? string.Empty : content[(int)lengthBefore..];
    }

    private static int CountOccurrences(string value, string fragment)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(fragment, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += fragment.Length;
        }

        return count;
    }

    private sealed class ThrowingReader : TextReader
    {
        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException<int>(new IOException("Injected reader failure."));
    }
}
