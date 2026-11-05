using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;

namespace PS7ScriptDesk.Tests;

[Collection(PsesOwnedSessionEnvironmentCollection.Name)]
public sealed class Bug005RunFileOutputPersistenceTests
{
    [Fact(Timeout = 90_000)]
    public async Task SavedThreeLineRunFile_OutputSurvivesDapCompletionAndPromptHandoff()
    {
        var runtimePath = Environment.GetEnvironmentVariable("PS7SCRIPT_DESK_TEST_PWSH")
            ?? @"C:\Program Files\PowerShell\7\pwsh.exe";
        var psesRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".vscode", "extensions", "ms-vscode.powershell-2025.4.0", "modules");
        if (!File.Exists(runtimePath) ||
            !File.Exists(Path.Combine(psesRoot, "PowerShellEditorServices", "Start-EditorServices.ps1")))
            throw Xunit.Sdk.SkipException.ForSkip("BUG-005 requires PowerShell 7 and PSES.");

        var previousRoot = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT");
        Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", psesRoot);
        var filePath = Path.Combine(Path.GetTempPath(), $"PS7ScriptDesk-R11-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(filePath,
            "Write-Host \"R11 FILE START\" -ForegroundColor Green\n" +
            "Write-Output \"R11 FILE OUTPUT\"\n" +
            "Write-Host \"R11 FILE END\" -ForegroundColor Cyan\n");
        var runtime = new PowerShellRuntimeInfo("PowerShell 7", "Core", "7.x", new Version(7, 0),
            "x64", runtimePath, "BUG005-R11", true, false, true, true);
        using var service = new PsesOwnedInteractiveSessionService();
        var chunks = new ConcurrentQueue<string>();
        var preFilterChunks = new ConcurrentQueue<string>();
        var promptEvents = 0;
        service.RawOutputReceived += (_, text) => chunks.Enqueue(text);
        service.OutputObservedBeforeDisplayFiltering += (_, text) => preFilterChunks.Enqueue(text);
        service.PromptReadyObserved += (_, _) => Interlocked.Increment(ref promptEvents);
        try
        {
            await service.StartSessionAsync(runtime, _ => { }, Environment.CurrentDirectory)
                .WaitAsync(TimeSpan.FromSeconds(45));
            await WaitForAsync(() => Regex.IsMatch(string.Concat(chunks), @"PS\s+[^\r\n>]*>"));
            var promptBeforeLocation = promptEvents;
            await service.WriteRawInputAsync("Set-Location 'C:\\'\r").WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForAsync(() => promptEvents > promptBeforeLocation);
            var startOffset = string.Concat(chunks).Length;
            var preFilterStartOffset = string.Concat(preFilterChunks).Length;
            var promptCount = promptEvents;

            await service.ExecuteScriptAsync(filePath, await File.ReadAllTextAsync(filePath), _ => { })
                .WaitAsync(TimeSpan.FromSeconds(30));
            var runOutput = string.Concat(chunks)[startOffset..];
            Assert.Contains("R11 FILE START", runOutput, StringComparison.Ordinal);
            Assert.Contains("R11 FILE OUTPUT", runOutput, StringComparison.Ordinal);
            Assert.Contains("R11 FILE END", runOutput, StringComparison.Ordinal);
            Assert.DoesNotContain("__ps7sd_editor_", runOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("__ps7sd_editor_", string.Concat(preFilterChunks)[preFilterStartOffset..], StringComparison.OrdinalIgnoreCase);
            Assert.True(promptEvents > promptCount, "No prompt arrived after the editor run.");
            Assert.DoesNotContain("__ps7sd_status_", runOutput, StringComparison.OrdinalIgnoreCase);

            var allOutput = string.Concat(chunks);
            var outputEnd = allOutput.IndexOf("R11 FILE END", StringComparison.Ordinal) + "R11 FILE END".Length;
            var lineEnd = allOutput.IndexOf('\n', outputEnd);
            Assert.True(lineEnd >= outputEnd, "Expected the final script-output line to finish before the prompt.");
            var afterOutput = allOutput[(lineEnd + 1)..];
            Assert.Single(Regex.Matches(afterOutput, @"PS\s+[^\r\n>]*>"));
            Assert.True(allOutput.LastIndexOf("\u001b[?25h", StringComparison.Ordinal) >
                allOutput.LastIndexOf("\u001b[?25l", StringComparison.Ordinal), "The prompt cursor must be visible.");
            var screenBeforeHandoff = ReplayVtScreen(allOutput[..(lineEnd + 1)]);
            var screenAfterHandoff = ReplayVtScreen(allOutput);
            var captureDirectory = Environment.GetEnvironmentVariable("PS7SCRIPTDESK_RAW_CONPTY_CAPTURE_DIR");
            if (!string.IsNullOrWhiteSpace(captureDirectory))
            {
                await File.WriteAllTextAsync(Path.Combine(captureDirectory, "Screen_Before_Handoff.txt"), screenBeforeHandoff);
                await File.WriteAllTextAsync(Path.Combine(captureDirectory, "Screen_After_Handoff.txt"), screenAfterHandoff);
            }
            Assert.Contains("R11 FILE START", screenBeforeHandoff, StringComparison.Ordinal);
            Assert.Contains("R11 FILE OUTPUT", screenBeforeHandoff, StringComparison.Ordinal);
            Assert.Contains("R11 FILE END", screenBeforeHandoff, StringComparison.Ordinal);
            Assert.Contains("R11 FILE START", screenAfterHandoff, StringComparison.Ordinal);
            Assert.Contains("R11 FILE OUTPUT", screenAfterHandoff, StringComparison.Ordinal);
            Assert.Contains("R11 FILE END", screenAfterHandoff, StringComparison.Ordinal);
            var finalRows = screenAfterHandoff.Split(Environment.NewLine);
            var outputRow = Array.FindIndex(finalRows, line => line.Contains("R11 FILE END", StringComparison.Ordinal));
            var promptRow = Array.FindLastIndex(finalRows, line => line.StartsWith("PS ", StringComparison.Ordinal));
            Assert.True(promptRow > outputRow, "The recovered prompt must be below the script output.");

            for (var repeat = 0; repeat < 2; repeat++)
            {
                var beforeRepeat = string.Concat(chunks).Length;
                await service.ExecuteScriptAsync(filePath, await File.ReadAllTextAsync(filePath), _ => { })
                    .WaitAsync(TimeSpan.FromSeconds(30));
                var repeatedOutput = string.Concat(chunks)[beforeRepeat..];
                var repeatedEnd = repeatedOutput.IndexOf("R11 FILE END", StringComparison.Ordinal);
                Assert.True(repeatedEnd >= 0, "Repeated Run File lost the last output line.");
                Assert.DoesNotContain("__ps7sd_editor_", repeatedOutput, StringComparison.OrdinalIgnoreCase);
                Assert.Single(Regex.Matches(repeatedOutput[(repeatedEnd + "R11 FILE END".Length)..], @"PS\s+[^\r\n>]*>"));
                var repeatedScreen = ReplayVtScreen(string.Concat(chunks));
                Assert.Contains("R11 FILE START", repeatedScreen, StringComparison.Ordinal);
                Assert.Contains("R11 FILE OUTPUT", repeatedScreen, StringComparison.Ordinal);
                Assert.Contains("R11 FILE END", repeatedScreen, StringComparison.Ordinal);
            }

            var beforeSelection = string.Concat(chunks).Length;
            await service.ExecuteScriptAsync("R12 selection",
                "$R12SelectionValue = 17\nWrite-Output \"R12 SELECTION $R12SelectionValue\"\n",
                _ => { }, executeInCurrentScope: true).WaitAsync(TimeSpan.FromSeconds(30));
            var selectionOutput = string.Concat(chunks)[beforeSelection..];
            Assert.DoesNotContain("__ps7sd_editor_", selectionOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("R12 SELECTION 17", selectionOutput, StringComparison.Ordinal);
            Assert.Contains("R12 SELECTION 17", ReplayVtScreen(string.Concat(chunks)), StringComparison.Ordinal);

            var beforeRepeatedSelection = string.Concat(chunks).Length;
            var promptCountBeforeRepeatedSelection = promptEvents;
            await service.ExecuteScriptAsync("R12 consecutive selection",
                "Write-Output 'R12 CONSECUTIVE SELECTION'\n",
                _ => { }, executeInCurrentScope: true).WaitAsync(TimeSpan.FromSeconds(30));
            var repeatedSelectionOutput = string.Concat(chunks)[beforeRepeatedSelection..];
            Assert.DoesNotContain("__ps7sd_editor_", repeatedSelectionOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("R12 CONSECUTIVE SELECTION", repeatedSelectionOutput, StringComparison.Ordinal);
            Assert.True(promptEvents > promptCountBeforeRepeatedSelection,
                "No prompt arrived after the consecutive Run Selection request.");

            var beforeRunFileAfterSelection = string.Concat(chunks).Length;
            var promptCountBeforeRunFileAfterSelection = promptEvents;
            await service.ExecuteScriptAsync(filePath, await File.ReadAllTextAsync(filePath), _ => { })
                .WaitAsync(TimeSpan.FromSeconds(30));
            var runFileAfterSelectionOutput = string.Concat(chunks)[beforeRunFileAfterSelection..];
            Assert.DoesNotContain("__ps7sd_editor_", runFileAfterSelectionOutput, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("R11 FILE OUTPUT", runFileAfterSelectionOutput, StringComparison.Ordinal);
            Assert.True(promptEvents > promptCountBeforeRunFileAfterSelection,
                "No prompt arrived after Run File following Run Selection.");
            Assert.True(service.IsSessionRunning, "The PSES session stopped after Run File following Run Selection.");

            var beforeInteractive = string.Concat(chunks).Length;
            await service.WriteRawInputAsync("Write-Host \"R12 INTERACTIVE $R12SelectionValue\"\r");
            await WaitForAsync(() => string.Concat(chunks)[beforeInteractive..].Contains("R12 INTERACTIVE 17", StringComparison.Ordinal));
            Assert.Contains("R12 INTERACTIVE 17", string.Concat(chunks)[beforeInteractive..], StringComparison.Ordinal);

            var beforeMixed = string.Concat(chunks).Length;
            await service.ExecuteScriptAsync("R12 mixed output",
                "1..3 | ForEach-Object { Write-Output \"R12 MULTI $_\" }\n" +
                "Write-Warning 'R12 WARNING'\n" +
                "Write-Error 'R12 ERROR' -ErrorAction Continue\n" +
                "Write-Progress -Activity 'R12 progress' -PercentComplete 50\n" +
                "cmd /c echo R12-NATIVE-OK\n" +
                "Write-Progress -Activity 'R12 progress' -Completed\n",
                _ => { }).WaitAsync(TimeSpan.FromSeconds(30));
            var mixedOutput = string.Concat(chunks)[beforeMixed..];
            Assert.Contains("R12 MULTI 1", mixedOutput, StringComparison.Ordinal);
            Assert.Contains("R12 MULTI 3", mixedOutput, StringComparison.Ordinal);
            Assert.Contains("R12 WARNING", mixedOutput, StringComparison.Ordinal);
            Assert.Contains("R12 ERROR", mixedOutput, StringComparison.Ordinal);
            Assert.Contains("R12-NATIVE-OK", mixedOutput, StringComparison.Ordinal);
            Assert.Contains("R12-NATIVE-OK", ReplayVtScreen(string.Concat(chunks)), StringComparison.Ordinal);
            Assert.True(service.IsSessionRunning,
                "The PSES session stopped after mixed output. Tail: " + mixedOutput[^Math.Min(mixedOutput.Length, 300)..]);

            var slowRun = service.ExecuteScriptAsync("R12 overlapping run",
                "Start-Sleep -Milliseconds 500\nWrite-Output 'R12 OVERLAP DONE'", _ => { });
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteScriptAsync(
                "R12 rejected run", "Write-Output 'R12 SHOULD NOT RUN'", _ => { }));
            await slowRun.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.DoesNotContain("R12 SHOULD NOT RUN", string.Concat(chunks), StringComparison.Ordinal);
            await service.ExecuteScriptAsync("R12 after overlap", "Write-Output 'R12 RECOVERED'", _ => { })
                .WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Contains("R12 RECOVERED", ReplayVtScreen(string.Concat(chunks)), StringComparison.Ordinal);
        }
        finally
        {
            await service.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Environment.SetEnvironmentVariable("PS7SCRIPTDESK_PSES_ROOT", previousRoot);
            try { File.Delete(filePath); } catch { }
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + 15 * Stopwatch.Frequency;
        while (Stopwatch.GetTimestamp() < deadline)
        {
            if (condition()) return;
            await Task.Delay(50);
        }
        Assert.True(condition(), "Expected PSES prompt did not arrive.");
    }

    private static string ReplayVtScreen(string raw)
    {
        const int columns = 160;
        const int rows = 24;
        var cells = new char[rows, columns];
        var row = 0;
        var column = 0;
        void AdvanceRow()
        {
            if (row < rows - 1) { row++; return; }
            for (var y = 1; y < rows; y++)
                for (var x = 0; x < columns; x++) cells[y - 1, x] = cells[y, x];
            for (var x = 0; x < columns; x++) cells[rows - 1, x] = '\0';
        }
        for (var i = 0; i < raw.Length; i++)
        {
            var value = raw[i];
            if (value == '\u001b' && i + 1 < raw.Length)
            {
                if (raw[i + 1] == ']')
                {
                    i += 2;
                    while (i < raw.Length && raw[i] != '\a') i++;
                    continue;
                }
                if (raw[i + 1] == '[')
                {
                    var start = i + 2;
                    var end = start;
                    while (end < raw.Length && raw[end] is not (>= '@' and <= '~')) end++;
                    if (end >= raw.Length) break;
                    var parameters = raw[start..end];
                    switch (raw[end])
                    {
                        case 'H':
                        case 'f':
                            var coordinates = parameters.Split(';');
                            row = Math.Clamp(int.TryParse(coordinates[0], out var parsedRow) ? parsedRow - 1 : 0, 0, rows - 1);
                            column = Math.Clamp(coordinates.Length > 1 && int.TryParse(coordinates[1], out var parsedColumn) ? parsedColumn - 1 : 0, 0, columns - 1);
                            break;
                        case 'J' when parameters == "2":
                            Array.Clear(cells);
                            break;
                        case 'K':
                            for (var eraseColumn = column; eraseColumn < columns; eraseColumn++) cells[row, eraseColumn] = '\0';
                            break;
                    }
                    i = end;
                    continue;
                }
                i++;
                continue;
            }
            if (value == '\r') { column = 0; continue; }
            if (value == '\n') { AdvanceRow(); continue; }
            if (char.IsControl(value)) continue;
            cells[row, column++] = value;
            if (column == columns) { column = 0; AdvanceRow(); }
        }
        var lines = new string[rows];
        for (var y = 0; y < rows; y++)
        {
            var line = new char[columns];
            for (var x = 0; x < columns; x++) line[x] = cells[y, x] == '\0' ? ' ' : cells[y, x];
            lines[y] = new string(line).TrimEnd();
        }
        return string.Join(Environment.NewLine, lines);
    }
}
