using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class IntelliSenseParameterProjectionTests
{
    [Fact]
    public void NewEmptyEditorKeepsCompletionTooltipsReadableAcrossRepeatedMultilineRequests()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new TextEditor { Document = new ICSharpCode.AvalonEdit.Document.TextDocument() };
            using var service = new PowerShellIntelliSenseService(new FixedCompletionProvider(
                new AuthoritativeCompletionResult(0, 0, new[]
                {
                    Item("Get-ChildItem"), Item("-ErrorAction"), Item("Name")
                })));

            foreach (var text in new[] { "", "Get-ChildItem", "Get-ChildItem\nGet-ChildItem", "Get-ChildItem\n$entry." })
            {
                editor.Document.Text = text;
                editor.CaretOffset = editor.Document.TextLength;
                var window = service.ShowCompletionAsync(editor, "pwsh.exe", includeEngine: true, engineWaitMilliseconds: 100)
                    .GetAwaiter().GetResult();

                Assert.NotNull(window);
                Assert.NotEmpty(window!.CompletionList.CompletionData);
                foreach (var completion in window.CompletionList.CompletionData.Cast<PowerShellCompletionData>())
                {
                    Assert.IsType<string>(completion.Description);
                    Assert.DoesNotContain("System.Windows.Controls.StackPanel", completion.Description.ToString(), StringComparison.Ordinal);
                }
                window.Close();
            }
        });
    }

    [Fact]
    public void ReproductionCommandKeepsRawCommonParametersInTheDisplayedCompletionList()
    {
        WpfTestDispatcher.Run(() =>
        {
            const string script = "Get-ChildItem -Path C:\\ -Recurse -Force";
            var editor = new TextEditor { Document = new ICSharpCode.AvalonEdit.Document.TextDocument(script) };
            editor.CaretOffset = script.LastIndexOf("-Force", StringComparison.Ordinal) + 1;
            var rawItems = new[]
            {
                Item("-Force"), Item("-Filter"), Item("-ErrorAction"), Item("-WarningAction"),
                Item("-InformationAction"), Item("-Verbose"), Item("-Debug"), Item("-WhatIf"), Item("-Confirm")
            };
            using var service = new PowerShellIntelliSenseService(new FixedCompletionProvider(
                new AuthoritativeCompletionResult(editor.CaretOffset, 0, rawItems)));

            var window = service.ShowCompletionAsync(editor, pwshExecutablePath: "pwsh.exe", includeEngine: true, engineWaitMilliseconds: 100)
                .GetAwaiter().GetResult();

            Assert.NotNull(window);
            var displayed = window!.CompletionList.CompletionData
                .Cast<PowerShellCompletionData>()
                .Select(item => item.Text)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Contains("-ErrorAction", displayed);
            Assert.Contains("-WarningAction", displayed);
            Assert.Contains("-Force", displayed);
            Assert.DoesNotContain("-Path", displayed);
            Assert.DoesNotContain("-Recurse", displayed);
        });
    }

    [Fact]
    public void InitialPopupAndTabProviderExposeTheSameValidParameterCandidates()
    {
        WpfTestDispatcher.Run(() =>
        {
            const string script = "Get-ChildItem -Path C:\\ -Recurse -";
            var caretOffset = script.Length;
            var rawItems = new[]
            {
                Item("-Force"), Item("-Filter"), Item("-ErrorAction"), Item("-WarningAction"),
                Item("-InformationAction"), Item("-Verbose"), Item("-Debug"), Item("-WhatIf"),
                Item("-Confirm"), Item("-Path"), Item("-Recurse")
            };
            var provider = new FixedCompletionProvider(new AuthoritativeCompletionResult(caretOffset, 0, rawItems));
            using var service = new PowerShellIntelliSenseService(provider);
            var editor = new TextEditor { Document = new ICSharpCode.AvalonEdit.Document.TextDocument(script) };
            editor.CaretOffset = caretOffset;

            var initialPopup = service.ShowCompletionAsync(
                editor, "pwsh.exe", includeEngine: true, engineWaitMilliseconds: 100)
                .GetAwaiter().GetResult();
            Assert.NotNull(initialPopup);
            var initialCandidates = ReadCandidateTexts(initialPopup!);
            initialPopup.Close();

            var tabProvider = service.ShowCompletionAsync(
                editor, "pwsh.exe", includeEngine: true, engineWaitMilliseconds: 100, providerOnly: true)
                .GetAwaiter().GetResult();
            Assert.NotNull(tabProvider);
            var tabCandidates = ReadCandidateTexts(tabProvider!);
            tabProvider.Close();

            Assert.Equal(initialCandidates.Order(StringComparer.OrdinalIgnoreCase), tabCandidates.Order(StringComparer.OrdinalIgnoreCase));
            foreach (var commonParameter in new[] { "-ErrorAction", "-WarningAction", "-InformationAction", "-Verbose", "-Debug" })
            {
                Assert.Contains(commonParameter, initialCandidates, StringComparer.OrdinalIgnoreCase);
            }
            Assert.DoesNotContain("-Path", initialCandidates, StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("-Recurse", initialCandidates, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(2, provider.RequestCount);
        });
    }

    [Fact]
    public void MissingDocumentationUsesAReadableKindFallback()
    {
        WpfTestDispatcher.Run(() =>
        {
            var completion = new PowerShellCompletionData(
                "-ErrorAction", "-ErrorAction", string.Empty, 0, 0,
                CompletionItemKind.ParameterName);

            Assert.Equal("parameter", completion.Description);

            var documentedCompletion = new PowerShellCompletionData(
                "-ErrorAction", "-ErrorAction", "Controls error handling.", 0, 0,
                CompletionItemKind.ParameterName);
            Assert.Equal($"parameter{Environment.NewLine}Controls error handling.", documentedCompletion.Description);
        });
    }

    [Fact]
    public void CompletionRequestsUseTheCurrentDocumentWhenSwitchingTabs()
    {
        WpfTestDispatcher.Run(() =>
        {
            const string firstText = "Get-Ch";
            const string secondText = "Write-Ou";
            var provider = new FixedCompletionProvider(new AuthoritativeCompletionResult(0, 0, new[]
            {
                Item("Get-ChildItem"), Item("Write-Output")
            }));
            using var service = new PowerShellIntelliSenseService(provider);
            var firstEditor = new TextEditor { Document = new ICSharpCode.AvalonEdit.Document.TextDocument(firstText) };
            var secondEditor = new TextEditor { Document = new ICSharpCode.AvalonEdit.Document.TextDocument(secondText) };
            firstEditor.CaretOffset = firstText.Length;
            secondEditor.CaretOffset = secondText.Length;

            foreach (var editor in new[] { firstEditor, secondEditor, firstEditor })
            {
                var window = service.ShowCompletionAsync(editor, "pwsh.exe", includeEngine: true, engineWaitMilliseconds: 100)
                    .GetAwaiter().GetResult();
                Assert.NotNull(window);
                window!.Close();
            }

            Assert.Equal(new[] { firstText, secondText, firstText }, provider.RequestedDocumentTexts);
        });
    }

    private static string[] ReadCandidateTexts(CompletionWindow window)
        => window.CompletionList.CompletionData
            .Cast<PowerShellCompletionData>()
            .Select(item => item.Text)
            .ToArray();

    private static AuthoritativeCompletionItem Item(string name)
        => new(name, name, 6, $"Get-ChildItem parameter: {name}");

    private sealed class FixedCompletionProvider(AuthoritativeCompletionResult result) : IAuthoritativeLiveCompletionProvider
    {
        public int RequestCount { get; private set; }
        public List<string> RequestedDocumentTexts { get; } = [];

        public bool IsLiveCompletionAvailable => true;

        public Task<AuthoritativeCompletionResult?> GetLiveCompletionsAsync(
            string documentUri,
            string documentText,
            int cursorOffset,
            CancellationToken cancellationToken = default)
        {
            RequestCount++;
            RequestedDocumentTexts.Add(documentText);
            return Task.FromResult<AuthoritativeCompletionResult?>(result);
        }
    }
}
