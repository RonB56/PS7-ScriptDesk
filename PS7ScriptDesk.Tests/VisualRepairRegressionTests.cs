using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.CodeCompletion;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class VisualRepairRegressionTests
{
    [Fact]
    public void CompletionDocumentationStaysTextAndTooltipTemplateCanPresentVisualContent()
    {
        WpfTestDispatcher.Run(() =>
        {
            var sourceControl = new TextBox { Text = "documentation control content" };
            var presenter = new ContentPresenter { Content = sourceControl };
            presenter.ApplyTemplate();

            Assert.Same(sourceControl, presenter.Content);
            Assert.Equal("documentation control content", sourceControl.Text);

            var documented = new PowerShellCompletionData(
                "Get-Item", "Get-Item", "Returns an item at the specified location.", 0, 0,
                CompletionItemKind.Command);
            var undocumented = new PowerShellCompletionData(
                "Get-Thing", "Get-Thing", string.Empty, 0, 0,
                CompletionItemKind.Command);
            var snippet = new PowerShellSnippetProvider().GetCompletions("if").First();

            var documentedDescription = Assert.IsType<string>(documented.Description);
            var undocumentedDescription = Assert.IsType<string>(undocumented.Description);
            var snippetDescription = Assert.IsType<string>(snippet.Description);

            Assert.Contains("Returns an item", documentedDescription, StringComparison.Ordinal);
            Assert.Equal("cmdlet / function", undocumentedDescription);
            Assert.DoesNotContain(typeof(TextBox).FullName!, documentedDescription, StringComparison.Ordinal);
            Assert.DoesNotContain(typeof(StackPanel).FullName!, documentedDescription, StringComparison.Ordinal);
            Assert.DoesNotContain(typeof(TextBlock).FullName!, documentedDescription, StringComparison.Ordinal);
            Assert.DoesNotContain(typeof(TextBox).FullName!, snippetDescription, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CompletionCommitPreservesEarlierLinesWhenReplacingTheCurrentFragment()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = "Write-Output 1\r\nGet-Chi" };
            var offset = editor.Text.LastIndexOf("Get-Chi", StringComparison.Ordinal);
            var completion = new PowerShellCompletionData(
                "Get-ChildItem", "Get-ChildItem", "Returns items at a location.", offset, "Get-Chi".Length,
                CompletionItemKind.Command);

            completion.Complete(
                editor.TextArea,
                new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = offset, Length = "Get-Chi".Length },
                EventArgs.Empty);

            Assert.Equal("Write-Output 1\r\nGet-ChildItem", editor.Text);
        });
    }

    [Theory]
    [InlineData("[System.IO.fi]", "[System.IO.FileInfo]", 13)]
    [InlineData("[System.IO.fi", "[System.IO.FileInfo", 13)]
    [InlineData("[System.IO.file]", "[System.IO.FileInfo]", 15)]
    public void QualifiedTypeCommitReplacesProviderRangeAndPreservesBracketSemantics(
        string source, string expected, int caretOffset)
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = source };
            editor.CaretOffset = caretOffset;
            var item = new PowerShellCompletionData(
                "System.IO.FileInfo", "FileInfo", "", 1, caretOffset - 1, CompletionItemKind.Type);
            var popupSegment = new ICSharpCode.AvalonEdit.Document.TextSegment
            {
                StartOffset = 11,
                Length = caretOffset - 11
            };

            item.Complete(editor.TextArea, popupSegment, EventArgs.Empty);

            Assert.Equal(expected, editor.Text);
            Assert.Equal(expected.Length - (source.EndsWith("]", StringComparison.Ordinal) ? 1 : 0), editor.CaretOffset);
        });
    }

    [Fact]
    public void QualifiedTypeCommitPreservesSuffixOnlyProviderRangeAndText()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = "[System.IO.fi]" };
            editor.CaretOffset = 13;
            var item = new PowerShellCompletionData("FileInfo", "FileInfo", "", 11, 2, CompletionItemKind.Type);

            item.Complete(
                editor.TextArea,
                new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = 11, Length = 2 },
                EventArgs.Empty);

            Assert.Equal("[System.IO.FileInfo]", editor.Text);
            Assert.Equal(19, editor.CaretOffset);
        });
    }

    [Fact]
    public void QualifiedTypeCommitIncludesCharactersTypedAfterPopupCreation()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = "[System.IO.file]" };
            editor.CaretOffset = 15;
            var item = new PowerShellCompletionData("System.IO.FileInfo", "FileInfo", "", 1, 12, CompletionItemKind.Type);

            item.Complete(
                editor.TextArea,
                new ICSharpCode.AvalonEdit.Document.TextSegment { StartOffset = 11, Length = 2 },
                EventArgs.Empty);

            Assert.Equal("[System.IO.FileInfo]", editor.Text);
            Assert.Equal(19, editor.CaretOffset);
        });
    }

    [Fact]
    public void CompletionListCanSelectDocumentedItemsForKeyboardAndHoverPresentation()
    {
        WpfTestDispatcher.Run(() =>
        {
            var editor = new ICSharpCode.AvalonEdit.TextEditor { Text = "Get-Chi" };
            var item = new PowerShellCompletionData(
                "Get-ChildItem", "Get-ChildItem", "Returns items at a location.", 0, editor.Text.Length,
                CompletionItemKind.Command);
            var window = new CompletionWindow(editor.TextArea);
            window.CompletionList.CompletionData.Add(item);

            window.CompletionList.SelectItem("Get-Chi");

            Assert.Same(item, window.CompletionList.SelectedItem);
            Assert.IsType<string>(window.CompletionList.SelectedItem.Description);
        });
    }

    [Fact]
    public void NativeToolbarOverflowUsesTheDarkAccessibleStyleAndStillOpens()
    {
        WpfTestDispatcher.Run(() =>
        {
            var application = EnsureShellApplication();
            var window = new Window
            {
                Left = -32000,
                Top = -32000,
                Width = 250,
                Height = 80,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false
            };
            var tray = new ToolBarTray();
            var toolbar = new ToolBar { Width = 220 };
            for (var index = 0; index < 10; index++)
            {
                toolbar.Items.Add(new Button { Content = $"Action {index}", Width = 70 });
            }

            tray.ToolBars.Add(toolbar);
            window.Content = tray;

            try
            {
                window.Show();
                window.UpdateLayout();
                toolbar.UpdateLayout();

                Assert.True(toolbar.HasOverflowItems);
                Assert.True(MainWindow.ApplyToolbarOverflowButtonStyle(toolbar));

                var overflow = Assert.IsType<ToggleButton>(toolbar.Template.FindName("OverflowButton", toolbar));
                Assert.Equal("More toolbar commands", AutomationProperties.GetName(overflow));
                Assert.Equal("More toolbar commands", overflow.ToolTip);
                Assert.Equal(Color.FromRgb(0x18, 0x21, 0x2F), Assert.IsType<SolidColorBrush>(overflow.Background).Color);

                overflow.IsChecked = true;
                Assert.True(toolbar.IsOverflowOpen);
                overflow.IsChecked = false;
                Assert.False(toolbar.IsOverflowOpen);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static App EnsureShellApplication()
    {
        if (System.Windows.Application.Current is App existingApp)
        {
            return existingApp;
        }

        var app = new App();
        app.InitializeComponent();
        return app;
    }
}
