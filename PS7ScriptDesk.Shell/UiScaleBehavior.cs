using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;

namespace PS7ScriptDesk.Shell
{
    /// <summary>
    /// Applies the application UI Scale once to each window's layout root.
    /// LayoutTransform keeps WPF measurement, hit testing, and scroll extents
    /// aligned with the visual size instead of scaling only the rendered pixels.
    /// </summary>
    public static class UiScaleBehavior
    {
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(UiScaleBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        private static readonly Dictionary<Window, WindowScaleState> States = new();
        private static bool _applicationWindowHookRegistered;

        public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

        public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

        public static void EnableForApplication(System.Windows.Application application)
        {
            ArgumentNullException.ThrowIfNull(application);

            if (!_applicationWindowHookRegistered)
            {
                EventManager.RegisterClassHandler(
                    typeof(Window),
                    FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(Window_LoadedForApplication),
                    handledEventsToo: true);
                _applicationWindowHookRegistered = true;
            }

            foreach (Window window in application.Windows)
            {
                EnsureEnabled(window);
            }

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Application UI Scale window hook was initialized.",
                new Dictionary<string, object?>
                {
                    ["openWindowCount"] = application.Windows.Count
                });
        }

        private static void OnIsEnabledChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
        {
            if (dependencyObject is not Window window)
            {
                return;
            }

            if (args.NewValue is true)
            {
                if (States.ContainsKey(window))
                {
                    return;
                }

                var state = new WindowScaleState(window);
                States[window] = state;
                state.Attach();
                return;
            }

            if (States.Remove(window, out var existingState))
            {
                existingState.Detach();
            }
        }

        private static void Window_LoadedForApplication(object sender, RoutedEventArgs args)
        {
            if (sender is Window window)
            {
                EnsureEnabled(window);
            }
        }

        private static void EnsureEnabled(Window window)
        {
            if (!GetIsEnabled(window))
            {
                SetIsEnabled(window, true);
            }
        }

        private sealed class WindowScaleState
        {
            private readonly Window _window;
            private IUiScaleService? _scaleService;
            private FrameworkElement? _scaledContent;
            private Transform? _originalLayoutTransform;
            private bool _originalTransformCaptured;
            private bool _contentChangeHandlerAttached;

            public WindowScaleState(Window window)
            {
                _window = window;
            }

            public void Attach()
            {
                _window.Loaded += Window_Loaded;
                _window.Closed += Window_Closed;
                AttachContentChangeHandler();
                UiScaleServiceHost.CurrentChanged += UiScaleServiceHost_CurrentChanged;
                SubscribeToCurrentScaleService();
                ApplyCurrentScaleOnUiThread();
            }

            public void Detach()
            {
                _window.Loaded -= Window_Loaded;
                _window.Closed -= Window_Closed;
                DetachContentChangeHandler();
                UiScaleServiceHost.CurrentChanged -= UiScaleServiceHost_CurrentChanged;
                UnsubscribeFromScaleService();
                RestoreOriginalTransform();
            }

            private void Window_Loaded(object sender, RoutedEventArgs args) => ApplyCurrentScale();

            private void Window_Closed(object? sender, EventArgs args)
            {
                Detach();
                States.Remove(_window);
            }

            private void UiScaleServiceHost_CurrentChanged(object? sender, EventArgs args)
            {
                SubscribeToCurrentScaleService();
                ApplyCurrentScaleOnUiThread();
            }

            private void ScaleService_ScaleChanged(object? sender, EventArgs args) => ApplyCurrentScaleOnUiThread();

            private void Window_ContentChanged(object? sender, EventArgs args) => ApplyCurrentScaleOnUiThread();

            private void AttachContentChangeHandler()
            {
                if (_contentChangeHandlerAttached)
                {
                    return;
                }

                DependencyPropertyDescriptor
                    .FromProperty(ContentControl.ContentProperty, typeof(Window))
                    ?.AddValueChanged(_window, Window_ContentChanged);
                _contentChangeHandlerAttached = true;
            }

            private void DetachContentChangeHandler()
            {
                if (!_contentChangeHandlerAttached)
                {
                    return;
                }

                DependencyPropertyDescriptor
                    .FromProperty(ContentControl.ContentProperty, typeof(Window))
                    ?.RemoveValueChanged(_window, Window_ContentChanged);
                _contentChangeHandlerAttached = false;
            }

            private void SubscribeToCurrentScaleService()
            {
                var current = UiScaleServiceHost.Current;
                if (ReferenceEquals(_scaleService, current))
                {
                    return;
                }

                UnsubscribeFromScaleService();
                _scaleService = current;
                _scaleService.ScaleChanged += ScaleService_ScaleChanged;
            }

            private void UnsubscribeFromScaleService()
            {
                if (_scaleService is not null)
                {
                    _scaleService.ScaleChanged -= ScaleService_ScaleChanged;
                    _scaleService = null;
                }
            }

            private void ApplyCurrentScaleOnUiThread()
            {
                if (_window.Dispatcher.CheckAccess())
                {
                    ApplyCurrentScale();
                    return;
                }

                _window.Dispatcher.BeginInvoke(
                    DispatcherPriority.Loaded,
                    new Action(ApplyCurrentScale));
            }

            private void ApplyCurrentScale()
            {
                if (_window.Content is not FrameworkElement content)
                {
                    return;
                }

                if (!ReferenceEquals(_scaledContent, content))
                {
                    RestoreOriginalTransform();
                    _scaledContent = content;
                    _originalLayoutTransform = content.LayoutTransform;
                    _originalTransformCaptured = true;
                }

                var service = UiScaleServiceHost.Current;
                var factor = service.CurrentFactor;
                content.LayoutTransform = Math.Abs(factor - 1.0) < 0.0001
                    ? _originalLayoutTransform
                    : CreateCombinedTransform(_originalLayoutTransform, factor);
                content.InvalidateMeasure();
                content.InvalidateArrange();
                _window.InvalidateMeasure();
                _window.InvalidateArrange();

                DeveloperDiagnostics.LogInfo(
                    "UI",
                    "Application UI Scale was applied to a WPF window root.",
                    new Dictionary<string, object?>
                    {
                        ["windowType"] = _window.GetType().FullName,
                        ["contentType"] = content.GetType().FullName,
                        ["percentage"] = service.CurrentPercentage,
                        ["scaleX"] = factor,
                        ["scaleY"] = factor,
                        ["preservedExistingLayoutTransform"] = _originalLayoutTransform is not null && _originalLayoutTransform != Transform.Identity
                    });
            }

            private void RestoreOriginalTransform()
            {
                if (_originalTransformCaptured && _scaledContent is not null)
                {
                    _scaledContent.LayoutTransform = _originalLayoutTransform;
                }

                _scaledContent = null;
                _originalLayoutTransform = null;
                _originalTransformCaptured = false;
            }

            private static Transform CreateCombinedTransform(Transform? original, double factor)
            {
                if (original is null || original == Transform.Identity)
                {
                    return new ScaleTransform(factor, factor);
                }

                return new TransformGroup
                {
                    Children =
                    {
                        original,
                        new ScaleTransform(factor, factor)
                    }
                };
            }
        }
    }
}
