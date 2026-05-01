using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MediaColor = System.Windows.Media.Color;
using WpfContextMenu = System.Windows.Controls.ContextMenu;
using WpfMenuItem = System.Windows.Controls.MenuItem;

namespace PowerShellStudio.Shell.Help
{
    public partial class ContextHelpWindow : Window
    {
        private HelpTopic _topic;

        public ContextHelpWindow(HelpTopic topic)
        {
            _topic = topic ?? HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey, "ContextHelpWindow constructor");
            InitializeComponent();
            RenderTopic();
        }

        public void ShowTopic(HelpTopic topic)
        {
            _topic = topic ?? HelpTopicCatalog.Get(HelpTopicCatalog.OverviewKey, "ContextHelpWindow.ShowTopic");
            RenderTopic();
        }

        private void RenderTopic()
        {
            TopicTitleText.Text = _topic.Title;
            TopicQuickSummaryText.Text = _topic.QuickSummary;
            FooterText.Text = "Tip: hover for quick help, press F1 for focused help, or right-click many controls and choose 'What does this do?'.";
            BodyPanel.Children.Clear();

            BodyPanel.Children.Add(CreateInfoCard("What this is", _topic.QuickSummary));
            BodyPanel.Children.Add(CreateInfoCard("When to use it", _topic.WhenToUse));
            BodyPanel.Children.Add(CreateInfoCard("Important note", _topic.LimitationOrGotcha));

            foreach (var section in _topic.Sections)
            {
                BodyPanel.Children.Add(CreateSection(section));
            }

            var relatedTopics = HelpTopicCatalog.GetRelatedTopics(_topic);
            RelatedTopicsButton.Visibility = relatedTopics.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private UIElement CreateInfoCard(string heading, string body)
        {
            return CreateSectionContainer(
                heading,
                new TextBlock
                {
                    Text = body,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(30, 30, 30))
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
                    Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0),
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(30, 30, 30))
                };

                textBlock.Inlines.Add(new Run(section.IsNumbered ? $"{i + 1}. " : "• ")
                {
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(MediaColor.FromRgb(27, 78, 122))
                });
                textBlock.Inlines.Add(new Run(section.Items[i]));
                panel.Children.Add(textBlock);
            }

            return CreateSectionContainer(section.Heading, panel);
        }

        private Border CreateSectionContainer(string heading, UIElement content)
        {
            return new Border
            {
                Margin = new Thickness(0, 0, 0, 12),
                Padding = new Thickness(14),
                Background = new SolidColorBrush(MediaColor.FromRgb(248, 250, 252)),
                BorderBrush = new SolidColorBrush(MediaColor.FromRgb(207, 219, 231)),
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
                            Foreground = new SolidColorBrush(MediaColor.FromRgb(21, 46, 79))
                        },
                        content
                    }
                }
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void RelatedTopicsButton_Click(object sender, RoutedEventArgs e)
        {
            var relatedTopics = HelpTopicCatalog.GetRelatedTopics(_topic);
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
    }
}
