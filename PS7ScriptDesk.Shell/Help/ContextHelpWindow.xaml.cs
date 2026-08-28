using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfKey = System.Windows.Input.Key;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace PS7ScriptDesk.Shell.Help
{
    public partial class ContextHelpWindow : Window
    {
        private const int MaximumHistoryEntries = 32;
        private readonly List<HelpNavigationState> _history = new();
        private HelpNavigationState _navigationState;
        private bool _isSynchronizingSearchText;

        public ContextHelpWindow(HelpTopic topic)
        {
            var initialTopic = topic ?? HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey, "ContextHelpWindow constructor");
            _navigationState = HelpNavigationState.ForTopic(initialTopic.Key);
            InitializeComponent();
            RenderNavigationState();
        }

        public void ShowTopic(HelpTopic topic)
        {
            var resolvedTopic = topic ?? HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey, "ContextHelpWindow.ShowTopic");
            NavigateTo(HelpNavigationState.ForTopic(resolvedTopic.Key));
        }

        public void ShowHome()
        {
            NavigateTo(HelpNavigationState.Home);
        }

        internal bool IsShowingHome => _navigationState.Kind == HelpNavigationKind.Home;

        internal bool CanNavigateBack => _history.Count > 0;

        private void NavigateTo(HelpNavigationState target, bool addHistory = true)
        {
            if (_navigationState == target)
            {
                return;
            }

            if (addHistory)
            {
                _history.Add(_navigationState);
                if (_history.Count > MaximumHistoryEntries)
                {
                    _history.RemoveAt(0);
                }
            }

            _navigationState = target;
            SynchronizeSearchText();
            RenderNavigationState();
        }

        private void RenderNavigationState()
        {
            BackButton.IsEnabled = CanNavigateBack;

            switch (_navigationState.Kind)
            {
                case HelpNavigationKind.Home:
                    RenderHome();
                    break;
                case HelpNavigationKind.Category:
                    RenderCategory(_navigationState.Value);
                    break;
                case HelpNavigationKind.Search:
                    RenderSearchResults(_navigationState.Value);
                    break;
                default:
                    RenderTopic(HelpTopicCatalog.Get(_navigationState.Value, "ContextHelpWindow.RenderTopic"));
                    break;
            }
        }

        private void RenderTopic(HelpTopic topic)
        {
            TopicTitleText.Text = topic.Title;
            TopicQuickSummaryText.Text = topic.QuickSummary;
            FooterText.Text = "Tip: hover for quick help, press F1 for focused help, or right-click many controls and choose 'What does this do?'.";
            BodyPanel.Children.Clear();

            BodyPanel.Children.Add(CreateInfoCard("What this is", topic.QuickSummary));
            BodyPanel.Children.Add(CreateInfoCard("When to use it", topic.WhenToUse));
            BodyPanel.Children.Add(CreateInfoCard("Important note", topic.LimitationOrGotcha));

            foreach (var section in topic.Sections)
            {
                BodyPanel.Children.Add(CreateSection(section));
            }

            var relatedTopics = HelpTopicCatalog.GetRelatedTopics(topic);
            RelatedTopicsButton.Visibility = relatedTopics.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private void RenderHome()
        {
            TopicTitleText.Text = "PS7 ScriptDesk Help";
            TopicQuickSummaryText.Text = "Browse Help by area, search the current catalog, or use F1 and What does this do? for contextual guidance.";
            FooterText.Text = "Choose a category to browse its topics. Search matches titles, keys, summaries, and Help details.";
            RelatedTopicsButton.Visibility = Visibility.Collapsed;
            BodyPanel.Children.Clear();
            BodyPanel.Children.Add(CreateBrowseHeading("Browse Help"));

            foreach (var category in HelpTopicCatalog.GetCategories())
            {
                BodyPanel.Children.Add(CreateCategoryButton(category));
            }

            BodyPanel.Children.Add(CreateBrowseHeading("Start here", new Thickness(0, 12, 0, 8)));
            BodyPanel.Children.Add(CreateTopicButton(HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey, "ContextHelpWindow.Home")));
        }

        private void RenderCategory(string? categoryKey)
        {
            var category = HelpTopicCatalog.GetCategories()
                .FirstOrDefault(item => string.Equals(item.Key, categoryKey, StringComparison.OrdinalIgnoreCase));
            if (category is null)
            {
                RenderHome();
                return;
            }

            TopicTitleText.Text = category.Title;
            TopicQuickSummaryText.Text = category.Description;
            FooterText.Text = "Select a topic to read its detailed Help. Use Home to browse another area.";
            RelatedTopicsButton.Visibility = Visibility.Collapsed;
            BodyPanel.Children.Clear();

            foreach (var topic in HelpTopicCatalog.GetTopicsForCategory(category.Key))
            {
                BodyPanel.Children.Add(CreateTopicButton(topic));
            }
        }

        private void RenderSearchResults(string? searchText)
        {
            var normalizedSearchText = searchText?.Trim() ?? string.Empty;
            var results = HelpTopicCatalog.Search(normalizedSearchText);
            TopicTitleText.Text = "Help search";
            TopicQuickSummaryText.Text = string.IsNullOrWhiteSpace(normalizedSearchText)
                ? "Type a term to search Help topics."
                : $"Results for '{normalizedSearchText}'.";
            FooterText.Text = "Search is case-insensitive and matches titles, keys, summaries, and topic details.";
            RelatedTopicsButton.Visibility = Visibility.Collapsed;
            BodyPanel.Children.Clear();

            if (results.Count == 0)
            {
                BodyPanel.Children.Add(new TextBlock
                {
                    Text = "No Help topics match your search. Try a shorter or different term.",
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 4, 0, 0)
                });
                return;
            }

            BodyPanel.Children.Add(CreateBrowseHeading($"{results.Count} matching topic{(results.Count == 1 ? string.Empty : "s")}"));
            foreach (var topic in results)
            {
                BodyPanel.Children.Add(CreateTopicButton(topic));
            }
        }

        private TextBlock CreateBrowseHeading(string text, Thickness? margin = null)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                Margin = margin ?? new Thickness(0, 0, 0, 8)
            };
        }

        private WpfButton CreateCategoryButton(HelpCategory category)
        {
            var button = new WpfButton
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalContentAlignment = WpfHorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = category.Title, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = category.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }
                    }
                }
            };
            AutomationProperties.SetName(button, $"Browse {category.Title} Help topics");
            button.Click += (_, _) => NavigateTo(HelpNavigationState.ForCategory(category.Key));
            return button;
        }

        private WpfButton CreateTopicButton(HelpTopic topic)
        {
            var button = new WpfButton
            {
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 10, 14, 10),
                HorizontalContentAlignment = WpfHorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = topic.Title, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = topic.QuickSummary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) }
                    }
                }
            };
            AutomationProperties.SetName(button, $"Open Help topic {topic.Title}");
            button.Click += (_, _) => ShowTopic(topic);
            return button;
        }

        private void HomeButton_Click(object sender, RoutedEventArgs e)
        {
            ShowHome();
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateBack();
        }

        internal bool NavigateBack()
        {
            if (_history.Count == 0)
            {
                return false;
            }

            var index = _history.Count - 1;
            _navigationState = _history[index];
            _history.RemoveAt(index);
            SynchronizeSearchText();
            RenderNavigationState();
            return true;
        }

        private void HelpSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isSynchronizingSearchText)
            {
                return;
            }

            var searchText = HelpSearchBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(searchText))
            {
                if (_navigationState.Kind == HelpNavigationKind.Search)
                {
                    NavigateTo(HelpNavigationState.Home, addHistory: false);
                }

                return;
            }

            NavigateTo(HelpNavigationState.ForSearch(searchText), addHistory: _navigationState.Kind != HelpNavigationKind.Search);
        }

        private void HelpSearchBox_KeyDown(object sender, WpfKeyEventArgs e)
        {
            if (e.Key != WpfKey.Enter || _navigationState.Kind != HelpNavigationKind.Search)
            {
                return;
            }

            var firstResult = HelpTopicCatalog.Search(_navigationState.Value).FirstOrDefault();
            if (firstResult is not null)
            {
                e.Handled = true;
                ShowTopic(firstResult);
            }
        }

        private void SynchronizeSearchText()
        {
            _isSynchronizingSearchText = true;
            HelpSearchBox.Text = _navigationState.Kind == HelpNavigationKind.Search ? _navigationState.Value ?? string.Empty : string.Empty;
            _isSynchronizingSearchText = false;
        }

        private UIElement CreateInfoCard(string heading, string body)
        {
            return CreateSectionContainer(
                heading,
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap
                });
        }

        private UIElement CreateSection(HelpSection section)
        {
            var panel = new StackPanel();
            for (var i = 0; i < section.Items.Count; i++)
            {
                var textBlock = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0)
                };
                textBlock.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary");

                var markerRun = new Run(section.IsNumbered ? $"{i + 1}. " : "• ")
                {
                    FontWeight = FontWeights.SemiBold
                };
                markerRun.SetResourceReference(TextElement.ForegroundProperty, "Theme.Accent.Primary");
                textBlock.Inlines.Add(markerRun);
                textBlock.Inlines.Add(new Run(section.Items[i]));
                panel.Children.Add(textBlock);
            }

            return CreateSectionContainer(section.Heading, panel);
        }

        private Border CreateSectionContainer(string heading, UIElement content)
        {
            var border = new Border
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock
                        {
                            Text = heading,
                            FontSize = 16,
                            FontWeight = FontWeights.Bold,
                            Margin = new Thickness(0, 0, 0, 8),
                        },
                        content
                    }
                }
            };
            border.SetResourceReference(Border.BackgroundProperty, "Theme.Surface.Secondary");
            border.SetResourceReference(Border.BorderBrushProperty, "Theme.Border.Subtle");

            var headingText = (TextBlock)((StackPanel)border.Child).Children[0];
            headingText.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary");
            if (content is TextBlock contentTextBlock)
            {
                contentTextBlock.SetResourceReference(TextBlock.ForegroundProperty, "Theme.Text.Primary");
            }

            return border;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RelatedTopicsButton_Click(object sender, RoutedEventArgs e)
        {
            var relatedTopics = _navigationState.Kind == HelpNavigationKind.Topic
                ? HelpTopicCatalog.GetRelatedTopics(HelpTopicCatalog.Get(_navigationState.Value, "ContextHelpWindow.RelatedTopics"))
                : Array.Empty<HelpTopic>();
            if (relatedTopics.Count == 0)
            {
                return;
            }

            var menu = new WpfContextMenu();
            foreach (var topic in relatedTopics)
            {
                var item = new WpfMenuItem
                {
                    Header = topic.Title,
                    Tag = topic
                };
                item.Click += (_, _) => ShowTopic((HelpTopic)item.Tag);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = RelatedTopicsButton;
            menu.IsOpen = true;
        }

        private enum HelpNavigationKind
        {
            Home,
            Category,
            Topic,
            Search
        }

        private sealed record HelpNavigationState(HelpNavigationKind Kind, string? Value)
        {
            public static HelpNavigationState Home { get; } = new(HelpNavigationKind.Home, null);

            public static HelpNavigationState ForCategory(string categoryKey) => new(HelpNavigationKind.Category, categoryKey);

            public static HelpNavigationState ForTopic(string topicKey) => new(HelpNavigationKind.Topic, topicKey);

            public static HelpNavigationState ForSearch(string searchText) => new(HelpNavigationKind.Search, searchText);
        }
    }
}
