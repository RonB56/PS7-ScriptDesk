using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace PS7ScriptDesk.Shell.Dialogs;

public partial class TextInputDialog : Window, INotifyPropertyChanged
{
    private string _inputText;

    public TextInputDialog(Window? owner, string title, string prompt, string? initialValue = null)
    {
        InitializeComponent();
        Owner = owner;
        DialogTitle = title;
        Prompt = prompt;
        _inputText = initialValue ?? string.Empty;
        DataContext = this;
    }

    public string DialogTitle { get; }
    public string Prompt { get; }
    public string InputText
    {
        get => _inputText;
        set
        {
            if (string.Equals(_inputText, value, StringComparison.Ordinal)) return;
            _inputText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsInputValid));
        }
    }

    public bool IsInputValid => true;
    public string? Result { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InputBox.Focus();
        InputBox.SelectAll();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            DialogResult = false;
        }
    }

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Accept();
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Accept();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Accept()
    {
        Result = InputText;
        DialogResult = true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
