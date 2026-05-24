using System;
using System.Windows;

namespace PS7ScriptDesk.Shell.Themes
{
    /// <summary>
    /// Applies one of the built-in themes at runtime by swapping the active
    /// <see cref="ResourceDictionary"/> in <c>Application.Current.Resources.MergedDictionaries</c>.
    ///
    /// Supported theme names: "Dark", "Light", "IseBlue".
    /// The active theme name is stored via <see cref="CurrentTheme"/> so it can be
    /// persisted in <c>ApplicationSettings</c> and restored on startup.
    /// </summary>
    public sealed class ThemeService
    {
        public const string Dark    = "Dark";
        public const string Light   = "Light";
        public const string IseBlue = "IseBlue";

        private const string ThemeDictionarySource = "pack://application:,,,/PS7ScriptDesk.Shell;component/Themes/{0}Theme.xaml";

        private static readonly string[] KnownThemes = { Dark, Light, IseBlue };

        public string CurrentTheme { get; private set; } = Dark;

        public event Action<string>? ThemeChanged;

        /// <summary>
        /// Applies the named theme.  Falls back to Dark if the name is unrecognised.
        /// Must be called on the UI thread.
        /// </summary>
        public void ApplyTheme(string? themeName)
        {
            var name = IsKnownTheme(themeName) ? themeName! : Dark;
            var uri  = new Uri(string.Format(ThemeDictionarySource, name), UriKind.Absolute);

            var mergedDicts = System.Windows.Application.Current.Resources.MergedDictionaries;

            // Remove the old theme dictionary (if one is already loaded).
            ResourceDictionary? existing = null;
            foreach (var dict in mergedDicts)
            {
                if (dict.Source is not null && dict.Source.OriginalString.Contains("Theme.xaml",
                        StringComparison.OrdinalIgnoreCase))
                {
                    existing = dict;
                    break;
                }
            }

            if (existing is not null)
            {
                mergedDicts.Remove(existing);
            }

            var newDict = new ResourceDictionary { Source = uri };
            mergedDicts.Add(newDict);

            CurrentTheme = name;
            ThemeChanged?.Invoke(name);
        }

        private static bool IsKnownTheme(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var k in KnownThemes)
            {
                if (string.Equals(k, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }
    }
}
