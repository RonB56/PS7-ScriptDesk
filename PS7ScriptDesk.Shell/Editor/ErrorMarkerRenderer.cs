using System;
using System.Collections.Generic;
using System.Windows;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// AvalonEdit <see cref="IBackgroundRenderer"/> that draws red zigzag squiggles under
    /// syntax-error ranges reported by <see cref="PowerShellDiagnosticsService"/>.
    ///
    /// All public members must be called from the UI thread.
    /// </summary>
    public sealed class ErrorMarkerRenderer : IBackgroundRenderer
    {
        // Zigzag geometry constants (in device-independent pixels).
        private const double ZigzagHeight = 3.0;
        private const double ZigzagWidth  = 4.0;

        private static readonly System.Windows.Media.Pen ErrorPen = CreateErrorPen();
        private static readonly System.Windows.Media.Pen WarningPen = CreateWarningPen();
        private static readonly System.Windows.Media.Brush ErrorBackgroundBrush = CreateErrorBackgroundBrush();
        private static readonly System.Windows.Media.Brush WarningBackgroundBrush = CreateWarningBackgroundBrush();

        private IReadOnlyList<ParseErrorInfo> _errors = Array.Empty<ParseErrorInfo>();

        // SimpleSegment is internal in AvalonEdit, so we use our own ISegment implementation.
        private readonly struct OffsetSegment : ISegment
        {
            public int Offset { get; }
            public int Length { get; }
            public int EndOffset => Offset + Length;

            public OffsetSegment(int offset, int length)
            {
                Offset = offset;
                Length = length;
            }
        }

        // -------------------------------------------------------------------------
        // IBackgroundRenderer
        // -------------------------------------------------------------------------

        /// <summary>
        /// Render on the Background layer so squiggles appear under text, not over it.
        /// </summary>
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, System.Windows.Media.DrawingContext drawingContext)
        {
            if (_errors.Count == 0 || textView.Document is null)
            {
                return;
            }

            textView.EnsureVisualLines();

            foreach (var error in _errors)
            {
                var startOffset = Math.Clamp(error.StartOffset, 0, textView.Document.TextLength);
                var endOffset   = Math.Clamp(error.EndOffset,   0, textView.Document.TextLength);

                if (startOffset >= endOffset)
                {
                    continue;
                }

                var segment = new OffsetSegment(startOffset, endOffset - startOffset);
                var rects = BackgroundGeometryBuilder.GetRectsForSegment(textView, segment);

                foreach (var rect in rects)
                {
                    DrawBackground(drawingContext, rect, error);
                    DrawZigzag(drawingContext, rect, error);
                }
            }
        }

        // -------------------------------------------------------------------------
        // Public API
        // -------------------------------------------------------------------------

        /// <summary>
        /// Replaces the current error list.  Call this on the UI thread, then call
        /// <c>textView.Redraw()</c> to repaint.
        /// </summary>
        public void SetErrors(IReadOnlyList<ParseErrorInfo> errors)
        {
            _errors = errors ?? Array.Empty<ParseErrorInfo>();
        }

        /// <summary>
        /// Returns the first error whose span contains <paramref name="offset"/>, or
        /// <c>null</c> if the offset is not inside any error span.
        /// </summary>
        public ParseErrorInfo? FindErrorAt(int offset)
        {
            foreach (var error in _errors)
            {
                if (offset >= error.StartOffset && offset < error.EndOffset)
                {
                    return error;
                }
            }
            return null;
        }

        // -------------------------------------------------------------------------
        // Zigzag drawing
        // -------------------------------------------------------------------------

        private static void DrawBackground(System.Windows.Media.DrawingContext dc, Rect rect, ParseErrorInfo error)
        {
            var backgroundRect = new Rect(rect.Left, rect.Top, Math.Max(rect.Width, ZigzagWidth), rect.Height);
            dc.DrawRectangle(error.IsWarning ? WarningBackgroundBrush : ErrorBackgroundBrush, null, backgroundRect);
        }

        private static void DrawZigzag(System.Windows.Media.DrawingContext dc, Rect rect, ParseErrorInfo error)
        {
            // Draw the zigzag along the bottom edge of the error-highlighted rect.
            var baseline = rect.Bottom;
            var x        = rect.Left;
            var right    = rect.Right;

            // Clamp to at least one full zigzag step.
            if (right <= x)
            {
                right = x + ZigzagWidth;
            }

            var geometry = new System.Windows.Media.StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new System.Windows.Point(x, baseline), false, false);

                var goingUp = true;
                while (x < right)
                {
                    x += ZigzagWidth / 2.0;
                    var y = goingUp ? baseline - ZigzagHeight : baseline;
                    ctx.LineTo(new System.Windows.Point(Math.Min(x, right), y), true, false);
                    goingUp = !goingUp;
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, error.IsWarning ? WarningPen : ErrorPen, geometry);
        }

        private static System.Windows.Media.Pen CreateErrorPen()
        {
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.Red, 1.5);
            pen.Freeze();
            return pen;
        }

        private static System.Windows.Media.Pen CreateWarningPen()
        {
            var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.DarkOrange, 1.5);
            pen.Freeze();
            return pen;
        }

        private static System.Windows.Media.Brush CreateErrorBackgroundBrush()
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(26, 255, 0, 0));
            brush.Freeze();
            return brush;
        }

        private static System.Windows.Media.Brush CreateWarningBackgroundBrush()
        {
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(31, 245, 158, 11));
            brush.Freeze();
            return brush;
        }
    }
}
