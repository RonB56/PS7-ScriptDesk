using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerShellStudio.Shell.Help
{
    public sealed class HelpSection
    {
        public HelpSection(string heading, IEnumerable<string>? items = null, bool isNumbered = false)
        {
            Heading = string.IsNullOrWhiteSpace(heading) ? "Details" : heading;
            Items = (items ?? Array.Empty<string>())
                .Where(static item => !string.IsNullOrWhiteSpace(item))
                .ToArray();
            IsNumbered = isNumbered;
        }

        public string Heading { get; }

        public IReadOnlyList<string> Items { get; }

        public bool IsNumbered { get; }

        public bool HasItems => Items.Count > 0;
    }
}
