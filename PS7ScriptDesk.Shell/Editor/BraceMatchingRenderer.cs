using System;
using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// AvalonEdit <see cref="IBackgroundRenderer"/> that highlights the character at the
    /// caret and its matching brace partner when the caret rests on or immediately before
    /// <c>{</c>, <c>}</c>, <c>(</c>, <c>)</c>, <c>[</c>, or <c>]</c>.
    ///
    /// The highlight is a subtle teal background rectangle drawn on the Selection layer so
    /// it is visible above the normal text background but below the caret.
    /// </summary>
    public sealed class BraceMatchingRenderer : IBackgroundRenderer
    {
        private static readonly System.Windows.Media.Brush HighlightBrush = CreateHighlightBrush();

        private int _firstOffset  = -1;
        private int _secondOffset = -1;

        public KnownLayer Layer => KnownLayer.Selection;

        // -------------------------------------------------------------------------
        // IBackgroundRenderer
        // -------------------------------------------------------------------------

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (_firstOffset < 0 || _secondOffset < 0)
            {
                return;
            }

            textView.EnsureVisualLines();
            DrawHighlight(textView, drawingContext, _firstOffset);
            DrawHighlight(textView, drawingContext, _secondOffset);
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Recomputes the pair of offsets to highlight based on the current caret position.
        /// Call this whenever the caret moves, then trigger a redraw.
        /// </summary>
        public void UpdateFromCaret(TextEditor editor)
        {
            _firstOffset  = -1;
            _secondOffset = -1;

            if (editor.Document is null)
            {
                return;
            }

            var document = editor.Document;
            var caret  = editor.CaretOffset;
            var length = document.TextLength;

            // Check the character at the caret and one before it. Avoid reading
            // document.Text here; caret movement is frequent and allocating the
            // whole script on every move makes large files feel sticky.
            for (var checkOffset = caret; checkOffset >= Math.Max(0, caret - 1); checkOffset--)
            {
                if (checkOffset >= length)
                {
                    continue;
                }

                var ch = document.GetCharAt(checkOffset);
                if (!IsOpenBrace(ch) && !IsCloseBrace(ch))
                {
                    continue;
                }

                var matchOffset = FindMatchingBrace(document, checkOffset, ch);
                if (matchOffset < 0)
                {
                    continue;
                }

                _firstOffset  = checkOffset;
                _secondOffset = matchOffset;
                return;
            }
        }

        // -------------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------------

        private static void DrawHighlight(TextView textView, DrawingContext dc, int offset)
        {
            if (offset < 0 || offset >= (textView.Document?.TextLength ?? 0))
            {
                return;
            }

            var segment = new SimpleSegment(offset, 1);
            var rects   = BackgroundGeometryBuilder.GetRectsForSegment(textView, segment);
            foreach (var rect in rects)
            {
                dc.DrawRectangle(HighlightBrush, null, rect);
            }
        }

        private static int FindMatchingBrace(TextDocument document, int fromOffset, char fromChar)
        {
            if (IsOpenBrace(fromChar))
            {
                var close  = GetClosingBrace(fromChar);
                var depth  = 1;
                for (var i = fromOffset + 1; i < document.TextLength; i++)
                {
                    var current = document.GetCharAt(i);
                    if (current == fromChar) depth++;
                    else if (current == close) { depth--; if (depth == 0) return i; }
                }
            }
            else
            {
                var open  = GetOpeningBrace(fromChar);
                var depth = 1;
                for (var i = fromOffset - 1; i >= 0; i--)
                {
                    var current = document.GetCharAt(i);
                    if (current == fromChar) depth++;
                    else if (current == open) { depth--; if (depth == 0) return i; }
                }
            }

            return -1;
        }

        private static bool IsOpenBrace(char c)  => c == '{' || c == '(' || c == '[';
        private static bool IsCloseBrace(char c) => c == '}' || c == ')' || c == ']';

        private static char GetClosingBrace(char open) => open switch { '{' => '}', '(' => ')', '[' => ']', _ => '\0' };
        private static char GetOpeningBrace(char close) => close switch { '}' => '{', ')' => '(', ']' => '[', _ => '\0' };

        private static System.Windows.Media.Brush CreateHighlightBrush()
        {
            var b = new SolidColorBrush(System.Windows.Media.Color.FromArgb(60, 0, 188, 212));
            b.Freeze();
            return b;
        }

        // Minimal ISegment implementation (AvalonEdit's SimpleSegment is internal).
        private readonly struct SimpleSegment : ISegment
        {
            public int Offset { get; }
            public int Length { get; }
            public int EndOffset => Offset + Length;
            public SimpleSegment(int offset, int length) { Offset = offset; Length = length; }
        }
    }
}
