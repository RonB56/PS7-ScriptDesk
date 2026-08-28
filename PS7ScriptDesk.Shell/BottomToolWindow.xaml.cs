using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell
{
    public partial class BottomToolWindow : Window
    {
        private bool _allowClose;

        public BottomToolWindow()
        {
            InitializeComponent();
        }

        public event EventHandler? DockBackRequested;

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

        public void CloseForDockBack()
        {
            _allowClose = true;
            Close();
        }

        public void CloseForOwnerShutdown()
        {
            _allowClose = true;
            Close();
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                DockBackRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            base.OnClosing(e);
        }
    }
}
