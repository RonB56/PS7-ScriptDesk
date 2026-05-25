using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Rendering;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor
{
    /// <summary>
    /// Lightweight left-side glyph margin that shows one diagnostic marker per visible line.
    /// Error lines render red; warning-only lines render orange.
    /// The margin stays inside AvalonEdit's existing architecture and does not replace the text renderer.
    /// </summary>
    public sealed class DiagnosticGlyphMargin : AbstractMargin
    {
        private static readonly WpfBrush ErrorFillBrush = FreezeBrush(WpfColor.FromRgb(220, 38, 38));
        private static readonly WpfBrush ErrorStrokeBrush = FreezeBrush(WpfColor.FromRgb(127, 29, 29));
        private static readonly WpfBrush WarningFillBrush = FreezeBrush(WpfColor.FromRgb(245, 158, 11));
        private static readonly WpfBrush WarningStrokeBrush = FreezeBrush(WpfColor.FromRgb(180, 83, 9));
        private static readonly WpfBrush ExclamationBrush = System.Windows.Media.Brushes.White;
        private static readonly WpfPen ErrorStrokePen = FreezePen(ErrorStrokeBrush, 1);
        private static readonly WpfPen WarningStrokePen = FreezePen(WarningStrokeBrush, 1);
        private static readonly WpfPen ExclamationPen = FreezePen(ExclamationBrush, 1.5);

        private readonly Dictionary<int, string> _diagnosticLineSeverities = new();

        public event Action<int>? DiagnosticLineClicked;

        protected override System.Windows.Size MeasureOverride(System.Windows.Size availableSize)
        {
            return new System.Windows.Size(18, 0);
        }

        public bool SetDiagnostics(IEnumerable<EditorDiagnosticSpanViewModel> diagnostics)
        {
            var nextDiagnosticLineSeverities = new Dictionary<int, string>();

            foreach (var diagnostic in diagnostics ?? Enumerable.Empty<EditorDiagnosticSpanViewModel>())
            {
                var lineNumber = Math.Max(1, diagnostic.LineNumber);
                if (!nextDiagnosticLineSeverities.TryGetValue(lineNumber, out var existingSeverity) ||
                    IsWarningSeverity(existingSeverity) && diagnostic.IsError)
                {
                    nextDiagnosticLineSeverities[lineNumber] = diagnostic.IsWarning
                        ? EditorDiagnosticSpanViewModel.WarningSeverity
                        : EditorDiagnosticSpanViewModel.ErrorSeverity;
                }
            }

            if (DiagnosticLineSeveritiesAreEquivalent(_diagnosticLineSeverities, nextDiagnosticLineSeverities))
            {
                return false;
            }

            _diagnosticLineSeverities.Clear();
            foreach (var pair in nextDiagnosticLineSeverities)
            {
                _diagnosticLineSeverities[pair.Key] = pair.Value;
            }

            InvalidateVisual();
            return true;
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

                var isWarning = IsWarningSeverity(severity);
                var fillBrush = isWarning ? WarningFillBrush : ErrorFillBrush;
                var strokePen = isWarning ? WarningStrokePen : ErrorStrokePen;

                var y = visualLine.VisualTop - textView.ScrollOffset.Y;
                var glyphBounds = new Rect(3, y + 3, 12, Math.Max(10, visualLine.Height - 6));
                var radiusX = Math.Min(6, glyphBounds.Width / 2);
                var radiusY = Math.Min(6, glyphBounds.Height / 2);
                drawingContext.DrawRoundedRectangle(fillBrush, strokePen, glyphBounds, radiusX, radiusY);

                var exclamationCenter = new System.Windows.Point(glyphBounds.Left + (glyphBounds.Width / 2), glyphBounds.Top + (glyphBounds.Height / 2));
                drawingContext.DrawLine(ExclamationPen, new System.Windows.Point(exclamationCenter.X, glyphBounds.Top + 2.5), new System.Windows.Point(exclamationCenter.X, glyphBounds.Bottom - 4.5));
                drawingContext.DrawEllipse(ExclamationBrush, null, new System.Windows.Point(exclamationCenter.X, glyphBounds.Bottom - 2.5), 1.2, 1.2);
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


        private static bool DiagnosticLineSeveritiesAreEquivalent(IReadOnlyDictionary<int, string> existing, IReadOnlyDictionary<int, string> next)
        {
            if (existing.Count != next.Count)
            {
                return false;
            }

            foreach (var pair in existing)
            {
                if (!next.TryGetValue(pair.Key, out var nextSeverity) ||
                    !string.Equals(pair.Value, nextSeverity, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static System.Windows.Media.SolidColorBrush FreezeBrush(WpfColor color)
        {
            var brush = new System.Windows.Media.SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static WpfPen FreezePen(WpfBrush brush, double thickness)
        {
            var pen = new WpfPen(brush, thickness);
            pen.Freeze();
            return pen;
        }

        private static bool IsWarningSeverity(string? severity)
        {
            return string.Equals(severity, EditorDiagnosticSpanViewModel.WarningSeverity, StringComparison.OrdinalIgnoreCase);
        }
    }
}
