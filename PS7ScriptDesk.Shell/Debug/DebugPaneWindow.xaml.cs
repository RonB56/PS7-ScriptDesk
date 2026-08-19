using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell.Debug
{
    public partial class DebugPaneWindow : Window
    {
        private bool _allowClose;

        public DebugPaneWindow()
        {
            InitializeComponent();
        }

        public event EventHandler? DockBackRequested;

        public event EventHandler<DebugPaneTabChangedEventArgs>? SelectedTabIndexChanged;

        public event EventHandler? RemoveSelectedBreakpointRequested;

        public int SelectedTabIndex => DebugTabControl.SelectedIndex;

        public object? SelectedBreakpointItem => DebugBreakpointsGrid.SelectedItem;

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

        public void SetSelectedTabIndex(int selectedIndex)
        {
            if (selectedIndex >= 0 && DebugTabControl.SelectedIndex != selectedIndex)
            {
                DebugTabControl.SelectedIndex = selectedIndex;
            }
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

        private void DockBackButton_Click(object sender, RoutedEventArgs e)
        {
            DockBackRequested?.Invoke(this, EventArgs.Empty);
        }

        private void DebugTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, DebugTabControl))
            {
                return;
            }

            SelectedTabIndexChanged?.Invoke(this, new DebugPaneTabChangedEventArgs(DebugTabControl.SelectedIndex));
        }

        private void RemoveSelectedBreakpointButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedBreakpointRequested?.Invoke(this, EventArgs.Empty);
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

    public sealed class DebugPaneTabChangedEventArgs : EventArgs
    {
        public DebugPaneTabChangedEventArgs(int selectedIndex)
        {
            SelectedIndex = selectedIndex;
        }

        public int SelectedIndex { get; }
    }
}
