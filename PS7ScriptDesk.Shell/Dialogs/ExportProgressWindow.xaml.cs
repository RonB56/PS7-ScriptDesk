using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class ExportProgressWindow : Window
{
    private const int MaximumVisibleDetailLength = 12000;
    private string _outputExecutablePath;
    private bool _isCompleted;

    public ExportProgressWindow(string? outputExecutablePath)
    {
        _outputExecutablePath = outputExecutablePath ?? string.Empty;
        InitializeComponent();
        UpdateDestinationText();
        StageText.Text = "Preparing export";
        StatusText.Text = "Preparing the selected script for export.";
    }

    public void ApplyUpdate(ExeExportProgressUpdate update)
    {
        if (update is null)
        {
            return;
        }

        if (_isCompleted && !update.IsCompleted)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(update.OutputExecutablePath))
        {
            _outputExecutablePath = update.OutputExecutablePath;
            UpdateDestinationText();
        }

        StageText.Text = update.Stage;
        StatusText.Text = update.StatusMessage;
        ExportProgressBar.IsIndeterminate = update.IsIndeterminate && !update.IsCompleted;

        if (!update.IsCompleted)
        {
            return;
        }

        _isCompleted = true;
        ExportProgressBar.IsIndeterminate = false;
        ExportProgressBar.Value = update.Succeeded ? ExportProgressBar.Maximum : 0;
        CompletionBorder.Visibility = Visibility.Visible;
        CompletionText.Text = update.Succeeded
            ? $"Export completed successfully. Created: {_outputExecutablePath}"
            : $"Export failed. {update.StatusMessage}";
        CloseButton.IsEnabled = true;
        OpenFolderButton.IsEnabled = update.Succeeded && File.Exists(_outputExecutablePath);

        if (!string.IsNullOrWhiteSpace(update.DetailedLog))
        {
            DetailsTextBox.Text = CreateVisibleDetailPreview(update.DetailedLog);
            DetailsExpander.Visibility = Visibility.Visible;
            DetailsExpander.IsExpanded = !update.Succeeded;
        }
    }

    public void CloseForOwnerShutdown()
    {
        _isCompleted = true;
        Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ContextHelp.ValidateWindowTopics(this);
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.F1)
        {
            return;
        }

        e.Handled = true;
        ContextHelp.OpenForFocusedElement(this);
    }

    private void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var outputDirectory = Path.GetDirectoryName(_outputExecutablePath);
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Directory.Exists(outputDirectory))
        {
            StatusText.Text = "The exported executable folder is no longer available.";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = outputDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Could not open the output folder: {ex.Message}";
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_isCompleted)
        {
            e.Cancel = true;
            StatusText.Text = "Export is still running. This window will be available when the operation completes.";
            return;
        }

        base.OnClosing(e);
    }

    private void UpdateDestinationText()
    {
        OutputFileNameText.Text = $"Executable: {Path.GetFileName(_outputExecutablePath)}";
        OutputPathText.Text = $"Destination: {_outputExecutablePath}";
    }

    private static string CreateVisibleDetailPreview(string detail)
    {
        var normalized = detail.Trim();
        return normalized.Length <= MaximumVisibleDetailLength
            ? normalized
            : normalized[..MaximumVisibleDetailLength] + Environment.NewLine + "[Details truncated in this window. See the application log for the complete record.]";
    }
}
