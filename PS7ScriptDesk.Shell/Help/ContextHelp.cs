using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfSeparator = System.Windows.Controls.Separator;
using WpfToolTip = System.Windows.Controls.ToolTip;

namespace PS7ScriptDesk.Shell.Help
{
    public static class ContextHelp
    {
        private const string HelpMenuItemTag = "PS7ScriptDesk.ContextHelpMenuItem";
        private static readonly Dictionary<Window, ContextHelpWindow> OpenWindows = new();
        private static readonly Dictionary<Window, WeakReference<DependencyObject>> LastHelpTargets = new();
        private static bool _isEnabled = true;

        private static readonly DependencyProperty OriginalToolTipProperty = DependencyProperty.RegisterAttached(
            "OriginalToolTip",
            typeof(object),
            typeof(ContextHelp),
            new PropertyMetadata(null));

        private static readonly DependencyProperty CreatedContextMenuProperty = DependencyProperty.RegisterAttached(
            "CreatedContextMenu",
            typeof(bool),
            typeof(ContextHelp),
            new PropertyMetadata(false));

        private static readonly DependencyProperty TrackingHandlersAttachedProperty = DependencyProperty.RegisterAttached(
            "TrackingHandlersAttached",
            typeof(bool),
            typeof(ContextHelp),
            new PropertyMetadata(false));

        private static readonly DependencyProperty OriginalShowOnDisabledProperty = DependencyProperty.RegisterAttached(
            "OriginalShowOnDisabled",
            typeof(object),
            typeof(ContextHelp),
            new PropertyMetadata(null));

        public static readonly DependencyProperty KeyProperty = DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(ContextHelp),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, OnKeyChanged));

        public static bool IsEnabled => _isEnabled;

        public static void SetEnabled(bool enabled)
        {
            if (_isEnabled == enabled)
            {
                return;
            }

            _isEnabled = enabled;

            if (!enabled)
            {
                foreach (var window in OpenWindows.Values.ToList())
                {
                    try
                    {
                        window.Close();
                    }
                    catch
                    {
                    }
                }

                OpenWindows.Clear();
            }

            RefreshAllAttachedHelp();
        }

        public static void SetKey(DependencyObject element, string? value)
        {
            element.SetValue(KeyProperty, value);
        }

        public static string? GetKey(DependencyObject element)
        {
            return (string?)element.GetValue(KeyProperty);
        }

        public static void OpenOverview(Window owner)
        {
            OpenTopic(owner, HelpTopicCatalog.OverviewKey);
        }

        public static bool OpenForFocusedElement(Window owner)
        {
            if (!IsEnabled)
            {
                return false;
            }

            var focusedElement = Keyboard.FocusedElement as DependencyObject;
            var focusedKey = ResolveHelpKey(focusedElement);

            var hoveredElement = GetHoveredElement(owner);
            var hoveredKey = ResolveHelpKey(hoveredElement);

            var rememberedElement = GetRememberedHelpTarget(owner);
            var rememberedKey = ResolveHelpKey(rememberedElement);

            var helpKey = ChooseBestHelpKey(focusedKey, hoveredKey, rememberedKey);
            OpenTopic(owner, helpKey);
            return !string.Equals(helpKey, "App.Overview", StringComparison.OrdinalIgnoreCase);
        }

        public static void OpenTopic(Window owner, string? key)
        {
            if (!IsEnabled)
            {
                return;
            }

            var topic = HelpTopicCatalog.Get(key, $"ContextHelp.OpenTopic ({owner.GetType().Name})");
            if (!OpenWindows.TryGetValue(owner, out var window) || !window.IsLoaded)
            {
                window = new ContextHelpWindow(topic)
                {
                    Owner = owner
                };
                window.Closed += (_, _) => OpenWindows.Remove(owner);
                OpenWindows[owner] = window;
                window.Show();
                return;
            }

            window.ShowTopic(topic);
            window.Show();
            window.Activate();
        }

        public static IReadOnlyList<string> ValidateWindowTopics(Window window)
        {
            if (window is null)
            {
                throw new ArgumentNullException(nameof(window));
            }

            if (window is not FrameworkElement root)
            {
                return Array.Empty<string>();
            }

            var keys = EnumerateFrameworkElements(root)
                .Select(GetKey)
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(static key => key!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            return HelpTopicCatalog.ValidateKeys(keys, $"Window:{window.GetType().Name}");
        }

        private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element)
            {
                return;
            }

            RoutedEventHandler loadedHandler = null!;
            loadedHandler = (_, _) =>
            {
                element.Loaded -= loadedHandler;
                ApplyHelp(element);
            };

            element.Loaded += loadedHandler;

            if (element.IsLoaded)
            {
                ApplyHelp(element);
            }
        }

        private static void ApplyHelp(FrameworkElement element)
        {
            StoreOriginalToolTip(element);
            StoreOriginalShowOnDisabled(element);
            EnsureTrackingHandlers(element);

            if (!IsEnabled)
            {
                SuppressHelp(element);
                return;
            }

            var key = ResolveHelpKey(element);
            var topic = HelpTopicCatalog.Get(key, $"ContextHelp.ApplyHelp ({element.GetType().Name})");
            element.ToolTip = BuildQuickHelpTooltip(topic);
            ToolTipService.SetShowOnDisabled(element, true);
            EnsureContextMenuHelpItem(element, key);
        }

        private static void SuppressHelp(FrameworkElement element)
        {
            RestoreOriginalToolTip(element);
            RestoreOriginalShowOnDisabled(element);
            RemoveContextMenuHelpItem(element);
        }

        private static void StoreOriginalToolTip(FrameworkElement element)
        {
            if (element.ReadLocalValue(OriginalToolTipProperty) != DependencyProperty.UnsetValue)
            {
                return;
            }

            element.SetValue(OriginalToolTipProperty, element.ToolTip);
        }

        private static void RestoreOriginalToolTip(FrameworkElement element)
        {
            var originalToolTip = element.ReadLocalValue(OriginalToolTipProperty);
            if (originalToolTip == DependencyProperty.UnsetValue)
            {
                element.ClearValue(FrameworkElement.ToolTipProperty);
                return;
            }

            if (originalToolTip is null)
            {
                element.ClearValue(FrameworkElement.ToolTipProperty);
            }
            else
            {
                element.ToolTip = originalToolTip;
            }
        }

        private static void StoreOriginalShowOnDisabled(FrameworkElement element)
        {
            if (element.ReadLocalValue(OriginalShowOnDisabledProperty) != DependencyProperty.UnsetValue)
            {
                return;
            }

            var originalValue = element.ReadLocalValue(ToolTipService.ShowOnDisabledProperty);
            element.SetValue(OriginalShowOnDisabledProperty, originalValue == DependencyProperty.UnsetValue ? null : originalValue);
        }

        private static void RestoreOriginalShowOnDisabled(FrameworkElement element)
        {
            var originalValue = element.GetValue(OriginalShowOnDisabledProperty);
            if (originalValue is null)
            {
                element.ClearValue(ToolTipService.ShowOnDisabledProperty);
                return;
            }

            element.SetValue(ToolTipService.ShowOnDisabledProperty, originalValue);
        }

        private static WpfToolTip BuildQuickHelpTooltip(HelpTopic topic)
        {
            var toolTip = new WpfToolTip
            {
                Placement = PlacementMode.Mouse,
                MaxWidth = 360,
                Content = BuildQuickHelpContent(topic)
            };

            toolTip.SetResourceReference(FrameworkElement.StyleProperty, "ContextHelpToolTipStyle");
            return toolTip;
        }

        private static object BuildQuickHelpContent(HelpTopic topic)
        {
            var root = new StackPanel
            {
                Width = 320
            };

            root.Children.Add(new TextBlock
            {
                Text = topic.Title,
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });

            root.Children.Add(CreateQuickHelpBlock("What it is", topic.QuickSummary));
            root.Children.Add(CreateQuickHelpBlock("When to use it", topic.WhenToUse));
            root.Children.Add(CreateQuickHelpBlock("Important note", topic.LimitationOrGotcha));

            root.Children.Add(new Border
            {
                Margin = new Thickness(0, 10, 0, 0),
                Padding = new Thickness(10, 8, 10, 8),
                Background = new SolidColorBrush(MediaColor.FromRgb(235, 245, 255)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(171, 201, 232)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock
                {
                    Text = "Press F1 for detailed help, or right-click and choose 'What does this do?' when that option is available.",
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 12,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(28, 78, 128))
                }
            });

            return root;
        }

        private static UIElement CreateQuickHelpBlock(string heading, string value)
        {
            var textBlock = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 6)
            };
            textBlock.Inlines.Add(new Run($"{heading}: ") { FontWeight = FontWeights.SemiBold });
            textBlock.Inlines.Add(new Run(value));
            return textBlock;
        }

        private static void EnsureContextMenuHelpItem(FrameworkElement element, string? key)
        {
            if (element is WpfMenuItem)
            {
                return;
            }

            var contextMenu = element.ContextMenu;
            var createdMenu = false;
            if (contextMenu is null)
            {
                contextMenu = new WpfContextMenu();
                createdMenu = true;
            }

            var alreadyAdded = contextMenu.Items
                .OfType<object>()
                .Any(static item => Equals((item as FrameworkElement)?.Tag, HelpMenuItemTag));
            if (alreadyAdded)
            {
                if (createdMenu)
                {
                    element.ContextMenu = contextMenu;
                    element.SetValue(CreatedContextMenuProperty, true);
                }
                return;
            }

            if (contextMenu.Items.Count > 0)
            {
                contextMenu.Items.Add(new WpfSeparator { Tag = HelpMenuItemTag });
            }

            var helpItem = new WpfMenuItem
            {
                Header = "What does this do?",
                Tag = HelpMenuItemTag,
                InputGestureText = "F1"
            };
            helpItem.Click += (_, _) =>
            {
                if (!IsEnabled)
                {
                    return;
                }

                if (Window.GetWindow(element) is Window ownerWindow)
                {
                    OpenTopic(ownerWindow, key);
                }
            };
            contextMenu.Items.Add(helpItem);
            element.ContextMenu = contextMenu;
            if (createdMenu)
            {
                element.SetValue(CreatedContextMenuProperty, true);
            }
        }

        private static void RemoveContextMenuHelpItem(FrameworkElement element)
        {
            var contextMenu = element.ContextMenu;
            if (contextMenu is null)
            {
                return;
            }

            for (var index = contextMenu.Items.Count - 1; index >= 0; index--)
            {
                if (contextMenu.Items[index] is FrameworkElement taggedItem && Equals(taggedItem.Tag, HelpMenuItemTag))
                {
                    contextMenu.Items.RemoveAt(index);
                }
            }

            if (Equals(element.GetValue(CreatedContextMenuProperty), true) && contextMenu.Items.Count == 0)
            {
                element.ClearValue(FrameworkElement.ContextMenuProperty);
                element.ClearValue(CreatedContextMenuProperty);
            }
        }

        private static void RefreshAllAttachedHelp()
        {
            if (System.Windows.Application.Current is null)
            {
                return;
            }

            foreach (Window window in System.Windows.Application.Current.Windows)
            {
                if (window is FrameworkElement frameworkElement)
                {
                    foreach (var element in EnumerateFrameworkElements(frameworkElement))
                    {
                        if (!string.IsNullOrWhiteSpace(GetKey(element)))
                        {
                            ApplyHelp(element);
                        }
                    }
                }
            }
        }


        private static void EnsureTrackingHandlers(FrameworkElement element)
        {
            if (Equals(element.GetValue(TrackingHandlersAttachedProperty), true))
            {
                return;
            }

            element.MouseEnter += OnHelpElementMouseEnter;
            element.ToolTipOpening += OnHelpElementToolTipOpening;
            element.GotKeyboardFocus += OnHelpElementGotKeyboardFocus;
            element.SetValue(TrackingHandlersAttachedProperty, true);
        }

        private static void OnHelpElementMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            RememberHelpTarget(sender as DependencyObject);
        }

        private static void OnHelpElementToolTipOpening(object sender, ToolTipEventArgs e)
        {
            RememberHelpTarget(sender as DependencyObject);
        }

        private static void OnHelpElementGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            RememberHelpTarget(sender as DependencyObject);
        }

        private static void RememberHelpTarget(DependencyObject? element)
        {
            if (element is null)
            {
                return;
            }

            if (Window.GetWindow(element) is not Window owner)
            {
                return;
            }

            LastHelpTargets[owner] = new WeakReference<DependencyObject>(element);
        }

        private static DependencyObject? GetHoveredElement(Window owner)
        {
            if (Mouse.DirectlyOver is DependencyObject hoveredElement && Window.GetWindow(hoveredElement) == owner)
            {
                return hoveredElement;
            }

            return null;
        }

        private static DependencyObject? GetRememberedHelpTarget(Window owner)
        {
            if (!LastHelpTargets.TryGetValue(owner, out var weakReference))
            {
                return null;
            }

            if (weakReference.TryGetTarget(out var target))
            {
                return target;
            }

            LastHelpTargets.Remove(owner);
            return null;
        }

        private static string ChooseBestHelpKey(string? focusedKey, string? hoveredKey, string? rememberedKey)
        {
            if (IsSpecificHelpKey(focusedKey))
            {
                return focusedKey!;
            }

            if (IsSpecificHelpKey(hoveredKey))
            {
                return hoveredKey!;
            }

            if (IsSpecificHelpKey(rememberedKey))
            {
                return rememberedKey!;
            }

            if (!string.IsNullOrWhiteSpace(focusedKey))
            {
                return focusedKey!;
            }

            if (!string.IsNullOrWhiteSpace(hoveredKey))
            {
                return hoveredKey!;
            }

            if (!string.IsNullOrWhiteSpace(rememberedKey))
            {
                return rememberedKey!;
            }

            return HelpTopicCatalog.OverviewKey;
        }

        private static bool IsSpecificHelpKey(string? key)
        {
            return !string.IsNullOrWhiteSpace(key)
                && !string.Equals(key, "App.Overview", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<FrameworkElement> EnumerateFrameworkElements(FrameworkElement root)
        {
            var visited = new HashSet<DependencyObject>();
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!visited.Add(current))
                {
                    continue;
                }

                if (current is FrameworkElement frameworkElement)
                {
                    yield return frameworkElement;
                }

                foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
                {
                    queue.Enqueue(child);
                }

                if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
                {
                    var visualChildrenCount = VisualTreeHelper.GetChildrenCount(current);
                    for (var index = 0; index < visualChildrenCount; index++)
                    {
                        queue.Enqueue(VisualTreeHelper.GetChild(current, index));
                    }
                }
            }
        }

        private static string ResolveHelpKey(DependencyObject? start)
        {
            var current = start;
            while (current != null)
            {
                var key = GetKey(current);
                if (!string.IsNullOrWhiteSpace(key))
                {
                    return key!;
                }

                current = GetParent(current);
            }

            return HelpTopicCatalog.OverviewKey;
        }

        private static DependencyObject? GetParent(DependencyObject current)
        {
            if (current is FrameworkContentElement frameworkContentElement)
            {
                if (frameworkContentElement.Parent is not null)
                {
                    return frameworkContentElement.Parent;
                }

                if (frameworkContentElement.TemplatedParent is not null)
                {
                    return frameworkContentElement.TemplatedParent;
                }
            }

            if (current is ContentElement contentElement)
            {
                var logicalParent = ContentOperations.GetParent(contentElement);
                if (logicalParent is not null)
                {
                    return logicalParent;
                }
            }

            if (current is FrameworkElement frameworkElement)
            {
                if (frameworkElement.Parent is not null)
                {
                    return frameworkElement.Parent;
                }

                if (frameworkElement.TemplatedParent is not null)
                {
                    return frameworkElement.TemplatedParent;
                }
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                return VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
