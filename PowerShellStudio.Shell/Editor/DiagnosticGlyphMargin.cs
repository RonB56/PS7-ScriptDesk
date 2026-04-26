using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using PowerShellStudio.UI.ViewModels;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Lightweight left-side glyph margin that shows one error marker per visible line with diagnostics.
    /// The margin stays inside AvalonEdit's existing architecture and does not replace the text renderer.
    /// </summary>
    public sealed class DiagnosticGlyphMargin : AbstractMargin
    {
        private readonly HashSet<int> _diagnosticLineNumbers = new();

        public event Action<int>? DiagnosticLineClicked;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            return new System.Windows.Size(18, 0);
        }

        public void SetDiagnostics(IEnumerable<EditorDiagnosticSpanViewModel> diagnostics)
        {
            _diagnosticLineNumbers.Clear();

            foreach (var lineNumber in (diagnostics ?? Enumerable.Empty<EditorDiagnosticSpanViewModel>())
                         .Select(static diagnostic => Math.Max(1, diagnostic.LineNumber))
                         .Distinct())
            {
                _diagnosticLineNumbers.Add(lineNumber);
            }

            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            if (TextView?.VisualLinesValid != true)
            {
                return;
            }

            var textView = TextView;
            if (textView.VisualLines is null)
            {
                return;
            }

            var fillBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(220, 38, 38));
            var strokePen = new System.Windows.Media.Pen(new SolidColorBrush(System.Windows.Media.Color.FromRgb(127, 29, 29)), 1);
            fillBrush.Freeze();
            strokePen.Freeze();

            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (!_diagnosticLineNumbers.Contains(lineNumber))
                {
                    continue;
                }

                var y = visualLine.VisualTop - textView.ScrollOffset.Y;
                var glyphBounds = new Rect(3, y + 3, 12, Math.Max(10, visualLine.Height - 6));
                var radiusX = Math.Min(6, glyphBounds.Width / 2);
                var radiusY = Math.Min(6, glyphBounds.Height / 2);
                drawingContext.DrawRoundedRectangle(fillBrush, strokePen, glyphBounds, radiusX, radiusY);

                var exclamationCenter = new System.Windows.Point(glyphBounds.Left + (glyphBounds.Width / 2), glyphBounds.Top + (glyphBounds.Height / 2));
                drawingContext.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.White, 1.5), new System.Windows.Point(exclamationCenter.X, glyphBounds.Top + 2.5), new System.Windows.Point(exclamationCenter.X, glyphBounds.Bottom - 4.5));
                drawingContext.DrawEllipse(System.Windows.Media.Brushes.White, null, new System.Windows.Point(exclamationCenter.X, glyphBounds.Bottom - 2.5), 1.2, 1.2);
            }
        }

        protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters)
        {
            return new PointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            if (TextView?.VisualLinesValid != true)
            {
                return;
            }

            var clickY = e.GetPosition(this).Y + TextView.ScrollOffset.Y;
            foreach (var visualLine in TextView.VisualLines)
            {
                var lineTop = visualLine.VisualTop;
                var lineBottom = lineTop + visualLine.Height;
                if (clickY >= lineTop && clickY <= lineBottom)
                {
                    var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                    if (_diagnosticLineNumbers.Contains(lineNumber))
                    {
                        DiagnosticLineClicked?.Invoke(lineNumber);
                        e.Handled = true;
                    }

                    return;
                }
            }
        }
    }
}
