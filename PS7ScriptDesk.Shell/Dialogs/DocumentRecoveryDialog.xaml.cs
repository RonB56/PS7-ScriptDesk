using System;
using System.Windows;
using WpfButton = System.Windows.Controls.Button;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;
using PS7ScriptDesk.Domain.Models;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class DocumentRecoveryDialog : Window
{
    public DocumentRecoveryDialog(DocumentRecoveryCandidate candidate, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        InitializeComponent();
        Owner = owner;
        RecoveryDetailsText.Text =
            $"Document: {candidate.DisplayName}{Environment.NewLine}" +
            $"Original: {(string.IsNullOrWhiteSpace(candidate.OriginalFilePath) ? "Untitled document" : candidate.OriginalFilePath)}{Environment.NewLine}" +
            $"Recovered: {candidate.LastRecoveryWriteUtc.ToLocalTime():G}{Environment.NewLine}" +
            $"Disk status: {candidate.StatusDescription}";
    }

    public DocumentRecoveryAction Decision { get; private set; } = DocumentRecoveryAction.KeepForLater;

    private void RecoveryAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is WpfButton { Tag: string value } && Enum.TryParse<DocumentRecoveryAction>(value, out var action))
        {
            Decision = action;
        }

        DialogResult = true;
    }

    private void Window_PreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (e.Key == WpfKey.Escape)
        {
            e.Handled = true;
            Decision = DocumentRecoveryAction.KeepForLater;
            DialogResult = true;
        }
    }
}
