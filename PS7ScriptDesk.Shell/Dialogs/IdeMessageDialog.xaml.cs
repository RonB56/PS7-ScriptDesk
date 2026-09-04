using System.Windows;
using System.Windows.Input;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class IdeMessageDialog : Window
{
    public IdeMessageDialog(Window? owner, string title, string message, string primaryText = "OK", string? secondaryText = null)
    {
        InitializeComponent();
        Owner = owner;
        DialogTitle = title;
        Message = message;
        PrimaryText = primaryText;
        SecondaryText = secondaryText;
        SecondaryButton.Visibility = secondaryText is null ? Visibility.Collapsed : Visibility.Visible;
        DataContext = this;
    }

    public string DialogTitle { get; }
    public string Message { get; }
    public string PrimaryText { get; }
    public string? SecondaryText { get; }
    public bool PrimaryAccepted { get; private set; }
    public bool SecondaryAccepted { get; private set; }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        PrimaryAccepted = true;
        DialogResult = true;
    }

    private void SecondaryButton_Click(object sender, RoutedEventArgs e)
    {
        SecondaryAccepted = true;
        DialogResult = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SecondaryText is not null)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }
}
