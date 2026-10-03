using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell
{
public partial class BottomToolWindow : DockableToolWindow
    {
        public BottomToolWindow()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            ContextHelp.ValidateWindowTopics(this);
        }

        private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != Key.F1)
            {
                return;
            }

            e.Handled = true;
            ContextHelp.OpenForFocusedElement(this);
        }

        public void SetToolContent(UIElement content)
        {
            ToolContentHost.Content = content;
        }

        public void ClearToolContent()
        {
            ToolContentHost.Content = null;
        }

    }
}
