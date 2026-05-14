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
    /// Lightweight left-side glyph margin that shows one diagnostic marker per visible line.
    /// Error lines render red; warning-only lines render orange.
    /// The margin stays inside AvalonEdit's existing architecture and does not replace the text renderer.
    /// </summary>
    public sealed class DiagnosticGlyphMargin : AbstractMargin
    {
        private readonly Dictionary<int, string> _diagnosticLineSeverities = new();

        public event Action<int>? DiagnosticLineClicked;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            return new System.Windows.Size(18, 0);
        }

        public void SetDiagnostics(IEnumerable<EditorDiagnosticSpanViewModel> diagnostics)
        {
            _diagnosticLineSeverities.Clear();

            foreach (var diagnostic in diagnostics ?? Enumerable.Empty<EditorDiagnosticSpanViewModel>())
            {
                var lineNumber = Math.Max(1, diagnostic.LineNumber);
                if (!_diagnosticLineSeverities.TryGetValue(lineNumber, out var existingSeverity) ||
                    IsWarningSeverity(existingSeverity) && diagnostic.IsError)
                {
                    _diagnosticLineSeverities[lineNumber] = diagnostic.IsWarning
                        ? EditorDiagnosticSpanViewModel.WarningSeverity
                        : EditorDiagnosticSpanViewModel.ErrorSeverity;
                }
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

            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                if (!_diagnosticLineSeverities.TryGetValue(lineNumber, out var severity))
                {
                    continue;
                }

                var fillBrush = new SolidColorBrush(IsWarningSeverity(severity)
                    ? System.Windows.Media.Color.FromRgb(245, 158, 11)
                    : System.Windows.Media.Color.FromRgb(220, 38, 38));
                var strokeBrush = new SolidColorBrush(IsWarningSeverity(severity)
                    ? System.Windows.Media.Color.FromRgb(180, 83, 9)
                    : System.Windows.Media.Color.FromRgb(127, 29, 29));
                fillBrush.Freeze();
                strokeBrush.Freeze();
                var strokePen = new System.Windows.Media.Pen(strokeBrush, 1);

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
                    if (_diagnosticLineSeverities.ContainsKey(lineNumber))
                    {
                        DiagnosticLineClicked?.Invoke(lineNumber);
                        e.Handled = true;
                    }

                    return;
                }
            }
        }

        private static bool IsWarningSeverity(string? severity)
        {
            return string.Equals(severity, EditorDiagnosticSpanViewModel.WarningSeverity, StringComparison.OrdinalIgnoreCase);
        }
    }
}
