using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace PowerShellStudio.Shell.Editor
{
    public sealed class PowerShellCompletionData : ICompletionData
    {
        private static readonly ImageSource[] KindIcons = BuildKindIcons();
        private ImageSource? _cachedIcon;

        public PowerShellCompletionData(
            string text,
            string content,
            string description,
            int replacementOffset,
            int replacementLength,
            CompletionItemKind kind = CompletionItemKind.Text,
            double priority = 0)
        {
            Text = text;
            Content = content;
            RawDescription = description;
            ReplacementOffset = replacementOffset;
            ReplacementLength = replacementLength;
            Kind = kind;
            Priority = priority;
        }

        public CompletionItemKind Kind { get; }

        public ImageSource Image => _cachedIcon ??= KindIcons[(int)Kind % KindIcons.Length];

        public string Text { get; }

        public object Content { get; }

        public string RawDescription { get; }

        /// <summary>Rich WPF description panel shown in the tooltip alongside the completion list.</summary>
        public object Description => BuildDescriptionPanel(RawDescription, Kind);

        public double Priority { get; }

        public int ReplacementOffset { get; }

        public int ReplacementLength { get; }

        public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
        {
            if (textArea?.Document is null)
            {
                return;
            }

            var segment = ResolveLiveReplacementSegment(textArea, completionSegment);
            textArea.Document.Replace(segment, Text);
            textArea.Caret.Offset = segment.StartOffset + Text.Length;
        }

        private TextSegment ResolveLiveReplacementSegment(TextArea textArea, ISegment? completionSegment)
        {
            var document = textArea.Document;
            var documentLength = document.TextLength;

            var startOffset = Math.Clamp(ReplacementOffset, 0, documentLength);
            var endOffset = Math.Clamp(ReplacementOffset + Math.Max(0, ReplacementLength), startOffset, documentLength);

            // AvalonEdit keeps the completionSegment in sync with the text typed after
            // the popup opened. Prefer it when it covers the stored replacement span.
            // This prevents the stale-span bug where typing "Get-Chi" opens completion
            // at "Get-C" and Tab inserts "Get-ChildItem" while leaving the later "hi"
            // behind.
            if (completionSegment is not null &&
                completionSegment.Offset >= 0 &&
                completionSegment.EndOffset <= documentLength &&
                completionSegment.Offset <= startOffset &&
                completionSegment.EndOffset >= endOffset)
            {
                startOffset = completionSegment.Offset;
                endOffset = completionSegment.EndOffset;
            }

            // Some AvalonEdit commit paths do not extend completionSegment far enough
            // when committing with Tab. If the caret has advanced through PowerShell
            // token characters since the list opened, include those characters too.
            var caretOffset = Math.Clamp(textArea.Caret.Offset, 0, documentLength);
            while (endOffset < caretOffset && IsPowerShellCompletionCharacter(document.GetCharAt(endOffset)))
            {
                endOffset++;
            }

            return new TextSegment
            {
                StartOffset = startOffset,
                Length = Math.Max(0, endOffset - startOffset)
            };
        }

        private static bool IsPowerShellCompletionCharacter(char ch)
        {
            return char.IsLetterOrDigit(ch) ||
                   ch == '_' ||
                   ch == '-' ||
                   ch == '$' ||
                   ch == ':' ||
                   ch == '.' ||
                   ch == '\\' ||
                   ch == '/';
        }

        // ─────────────────────────────────────────────────────────────────────
        // Icon generation — small 14×14 DrawingImage per completion kind
        // ─────────────────────────────────────────────────────────────────────

        private static ImageSource[] BuildKindIcons()
        {
            // Order must match CompletionItemKind enum values 0..13
            var colors = new[]
            {
                Color.FromRgb(150, 150, 150), // Text          0
                Color.FromRgb(180, 180, 180), // History       1
                Color.FromRgb(86,  156,  214), // Command      2  (blue — cmdlet)
                Color.FromRgb(78,  201, 176), // ProviderItem  3  (teal — file)
                Color.FromRgb(78,  201, 176), // ProviderContainer 4 (teal — folder)
                Color.FromRgb(220, 220, 170), // Property      5  (yellow)
                Color.FromRgb(255, 165,   0), // Method        6  (orange)
                Color.FromRgb(86,  156, 214), // ParameterName 7  (blue)
                Color.FromRgb(180, 200, 180), // ParameterValue 8
                Color.FromRgb(156, 220, 254), // Variable      9  (light blue)
                Color.FromRgb(206,  92,   0), // Namespace     10 (brown)
                Color.FromRgb(197, 134, 192), // Type          11 (purple)
                Color.FromRgb(86,  156, 214), // Keyword       12 (blue)
                Color.FromRgb(86,  156, 214), // DynamicKeyword 13
            };

            var labels = new[]
            {
                " ",  // Text
                "H",  // History
                ">",  // Command/Cmdlet
                "F",  // ProviderItem (File)
                "D",  // ProviderContainer (Directory)
                "P",  // Property
                "M",  // Method
                "-",  // ParameterName
                "V",  // ParameterValue
                "$",  // Variable
                "N",  // Namespace
                "T",  // Type
                "K",  // Keyword
                "K",  // DynamicKeyword
            };

            var icons = new ImageSource[colors.Length];
            for (var i = 0; i < colors.Length; i++)
            {
                icons[i] = CreateBadgeIcon(colors[i], labels[i]);
            }
            return icons;
        }

        private static ImageSource CreateBadgeIcon(Color background, string label)
        {
            const double size = 14;
            const double radius = 2;

            var brush = new SolidColorBrush(background);
            brush.Freeze();

            var foreground = Brushes.White;

            var geom = new RectangleGeometry(new Rect(0, 0, size, size), radius, radius);
            var rectDrawing = new GeometryDrawing(brush, null, geom);

            var typeface = new Typeface(new FontFamily("Consolas"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
            var glyphText = new FormattedText(
                label,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                typeface,
                8.5,
                foreground,
                1.0);

            var textGeom = glyphText.BuildGeometry(new Point(
                (size - glyphText.Width) / 2,
                (size - glyphText.Height) / 2));

            var textDrawing = new GeometryDrawing(foreground, null, textGeom);

            var group = new DrawingGroup();
            group.Children.Add(rectDrawing);
            group.Children.Add(textDrawing);
            group.Freeze();

            var image = new DrawingImage(group);
            image.Freeze();
            return image;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Rich description panel
        // ─────────────────────────────────────────────────────────────────────

        private static object BuildDescriptionPanel(string description, CompletionItemKind kind)
        {
            if (string.IsNullOrWhiteSpace(description))
                return string.Empty;

            var kindLabel = GetKindLabel(kind);
            var panel = new StackPanel { Margin = new Thickness(4) };

            if (!string.IsNullOrEmpty(kindLabel))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = kindLabel,
                    FontSize = 10,
                    Foreground = Brushes.Gray,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }

            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 500,
            });

            return panel;
        }

        private static string GetKindLabel(CompletionItemKind kind) => kind switch
        {
            CompletionItemKind.Command       => "cmdlet / function",
            CompletionItemKind.ParameterName => "parameter",
            CompletionItemKind.ParameterValue => "parameter value",
            CompletionItemKind.Variable      => "variable",
            CompletionItemKind.Property      => "property",
            CompletionItemKind.Method        => "method",
            CompletionItemKind.Type          => "type",
            CompletionItemKind.Keyword       => "keyword",
            CompletionItemKind.Namespace     => "namespace",
            CompletionItemKind.ProviderItem  => "file",
            CompletionItemKind.ProviderContainer => "directory",
            _ => string.Empty,
        };
    }
}
