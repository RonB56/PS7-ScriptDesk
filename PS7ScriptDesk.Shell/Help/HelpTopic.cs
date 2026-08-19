using System;
using System.Collections.Generic;
using System.Linq;

namespace PS7ScriptDesk.Shell.Help
{
    public sealed class HelpTopic
    {
        public HelpTopic(
            string key,
            string title,
            string quickSummary,
            string whenToUse,
            string limitationOrGotcha,
            IEnumerable<HelpSection>? sections = null,
            IEnumerable<string>? relatedTopicKeys = null,
            IEnumerable<string>? keywords = null,
            string? categoryKey = null)
        {
            Key = string.IsNullOrWhiteSpace(key) ? "App.Overview" : key;
            Title = string.IsNullOrWhiteSpace(title) ? "Help" : title;
            QuickSummary = Normalize(quickSummary);
            WhenToUse = Normalize(whenToUse);
            LimitationOrGotcha = Normalize(limitationOrGotcha);
            Sections = (sections ?? Array.Empty<HelpSection>())
                .Where(static section => section is not null && section.HasItems)
                .ToArray();
            RelatedTopicKeys = (relatedTopicKeys ?? Array.Empty<string>())
                .Where(static keyValue => !string.IsNullOrWhiteSpace(keyValue))
                .ToArray();
            Keywords = (keywords ?? Array.Empty<string>())
                .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
                .ToArray();
            CategoryKey = string.IsNullOrWhiteSpace(categoryKey) ? "GettingStarted" : categoryKey.Trim();
        }

        public string Key { get; }

        public string Title { get; }

        public string QuickSummary { get; }

        public string WhenToUse { get; }

        public string LimitationOrGotcha { get; }

        public IReadOnlyList<HelpSection> Sections { get; }

        public IReadOnlyList<string> RelatedTopicKeys { get; }

        public IReadOnlyList<string> Keywords { get; }

        public string CategoryKey { get; }

        public bool MatchesSearch(string? searchText)
        {
            if (string.IsNullOrWhiteSpace(searchText))
            {
                return true;
            }

            var searchableText = string.Join(
                " ",
                new[] { Key, Title, QuickSummary, WhenToUse, LimitationOrGotcha }
                    .Concat(Keywords)
                    .Concat(Sections.SelectMany(section => section.Items)));

            return searchText
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .All(term => searchableText.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        private static string Normalize(string? value)
        {
            return string.IsNullOrWhiteSpace(value)
                ? string.Empty
                : value.Trim();
        }
    }
}
