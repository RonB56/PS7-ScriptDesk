using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PS7ScriptDesk.Shell.Help;

namespace PS7ScriptDesk.Shell.Debug
{
    public partial class DebugPaneWindow : DockableToolWindow
    {
        public DebugPaneWindow()
        {
            InitializeComponent();
        }

        public event EventHandler<DebugPaneTabChangedEventArgs>? SelectedTabIndexChanged;

        public event EventHandler<DebugCallStackFrameSelectionChangedEventArgs>? CallStackFrameSelectionChanged;

        public event EventHandler? RemoveSelectedBreakpointRequested;

        public int SelectedTabIndex => DebugTabControl.SelectedIndex;

        public object? SelectedBreakpointItem => DebugBreakpointsGrid.SelectedItem;

        public void SetDebugStaleIndicator(bool isVisible)
        {
            DebugStaleIndicator.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
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

        public void SetSelectedTabIndex(int selectedIndex)
        {
            if (selectedIndex >= 0 && DebugTabControl.SelectedIndex != selectedIndex)
            {
                DebugTabControl.SelectedIndex = selectedIndex;
            }
        }

        private void DockBackButton_Click(object sender, RoutedEventArgs e)
        {
            RequestDockBackFromControl();
        }

        private void DebugTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, DebugTabControl))
            {
                return;
            }

            SelectedTabIndexChanged?.Invoke(this, new DebugPaneTabChangedEventArgs(DebugTabControl.SelectedIndex));
        }

        private void DebugCallStackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DebugCallStackGrid.SelectedItem is DebugCallStackFrame frame)
            {
                CallStackFrameSelectionChanged?.Invoke(this, new DebugCallStackFrameSelectionChangedEventArgs(frame));
            }
        }

        private void RemoveSelectedBreakpointButton_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedBreakpointRequested?.Invoke(this, EventArgs.Empty);
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

    public sealed class DebugCallStackFrameSelectionChangedEventArgs : EventArgs
    {
        public DebugCallStackFrameSelectionChangedEventArgs(DebugCallStackFrame frame)
        {
            Frame = frame;
        }

        public DebugCallStackFrame Frame { get; }
    }
}
