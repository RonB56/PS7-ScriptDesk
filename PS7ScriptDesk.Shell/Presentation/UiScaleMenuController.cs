using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;

namespace PS7ScriptDesk.Shell.Presentation
{
    internal sealed class UiScaleMenuController : IDisposable
    {
        private readonly IUiScaleService _service;
        private readonly MenuItem _menu;
        private bool _isInitialized;
        private volatile bool _isDisposed;

        internal UiScaleMenuController(IUiScaleService service, MenuItem menu)
        {
            _service = service;
            _menu = menu;
        }

        internal void Initialize()
        {
            if (_isInitialized || _isDisposed)
            {
                return;
            }

            _isInitialized = true;
            _menu.Items.Clear();
            foreach (var percentage in _service.SupportedPercentages)
            {
                var menuItem = new MenuItem
                {
                    Header = $"{percentage}%",
                    IsCheckable = true,
                    Tag = percentage,
                    ToolTip = $"Set application UI Scale to {percentage}%"
                };
                AutomationProperties.SetName(menuItem, $"UI Scale {percentage} percent");
                menuItem.Click += UiScalePresetMenuItem_Click;
                _menu.Items.Add(menuItem);
            }

            RefreshUiScaleMenuChecks();
            _service.ScaleChanged += UiScaleService_ScaleChanged;
        }

        private void UiScalePresetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: int percentage })
            {
                _service.SetPercentage(percentage, "Menu");
            }
        }

        private void UiScaleService_ScaleChanged(object? sender, EventArgs e)
        {
            if (_isDisposed)
            {
                return;
            }

            if (!_menu.Dispatcher.CheckAccess())
            {
                _menu.Dispatcher.BeginInvoke(new Action(RefreshUiScaleMenuChecksIfActive), DispatcherPriority.DataBind);
                return;
            }

            RefreshUiScaleMenuChecks();
        }

        private void RefreshUiScaleMenuChecksIfActive()
        {
            if (!_isDisposed)
            {
                RefreshUiScaleMenuChecks();
            }
        }

        private void RefreshUiScaleMenuChecks()
        {
            foreach (var item in _menu.Items.OfType<MenuItem>())
            {
                item.IsChecked = item.Tag is int percentage && percentage == _service.CurrentPercentage;
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;
            _service.ScaleChanged -= UiScaleService_ScaleChanged;
            foreach (var item in _menu.Items.OfType<MenuItem>())
            {
                item.Click -= UiScalePresetMenuItem_Click;
            }
        }
    }
}
