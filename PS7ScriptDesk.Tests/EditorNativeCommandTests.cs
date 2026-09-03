using System.Runtime.ExceptionServices;
using System.Threading;
using ICSharpCode.AvalonEdit;
using PS7ScriptDesk.Shell.Editor;

namespace PS7ScriptDesk.Tests;

public sealed class EditorNativeCommandTests
{
    [Fact]
    public void LeadingTabsAndSpacesUseAvalonEditNativeCommandsWithConfiguredIndentWidth()
    {
        RunOnStaThread(() =>
        {
            var editor = new TextEditor { Text = "\tone\r\n\t\ttwo" };
            editor.Options.IndentationSize = 2;
            editor.Select(0, editor.Document.TextLength);
            AvalonEditCommands.ConvertLeadingTabsToSpaces.Execute(null, editor.TextArea);
            Assert.Equal("  one\r\n    two", editor.Text);

            editor.Select(0, editor.Document.TextLength);
            AvalonEditCommands.ConvertLeadingSpacesToTabs.Execute(null, editor.TextArea);
            Assert.Equal("\tone\r\n\t\ttwo", editor.Text);
        });
    }

    [Fact]
    public void WpfNativeDeleteWordCommandsAreAvailableForEditorRouting()
    {
        RunOnStaThread(() =>
        {
            var editor = new TextEditor { Text = "Get-Process $Name" };
            editor.CaretOffset = editor.Text.Length;
            System.Windows.Documents.EditingCommands.DeletePreviousWord.Execute(null, editor.TextArea);
            Assert.NotEqual("Get-Process $Name", editor.Text);
            editor.CaretOffset = 0;
            System.Windows.Documents.EditingCommands.DeleteNextWord.Execute(null, editor.TextArea);
            Assert.NotEqual("Get-Process $Name", editor.Text);
        });
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
