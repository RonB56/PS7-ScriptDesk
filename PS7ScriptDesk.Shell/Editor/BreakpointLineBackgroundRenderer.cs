using System;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using ICSharpCode.AvalonEdit.Rendering;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell.Editor
{
    public sealed class BreakpointLineBackgroundRenderer : IBackgroundRenderer
    {
        private readonly EditorTabViewModel _tab;

        private readonly WpfBrush _enabledBreakpointBrush;
        private readonly WpfBrush _disabledBreakpointBrush;
        // Yellow-amber highlight for the line where execution is currently paused.
        private readonly WpfBrush _debugCurrentLineBrush;

        public BreakpointLineBackgroundRenderer(EditorTabViewModel tab)
        {
            _tab = tab ?? throw new ArgumentNullException(nameof(tab));

            var enabledBreakpointBrush = new WpfSolidColorBrush(WpfColor.FromArgb(48, 220, 20, 60));
            enabledBreakpointBrush.Freeze();
            _enabledBreakpointBrush = enabledBreakpointBrush;

            var disabledBreakpointBrush = new WpfSolidColorBrush(WpfColor.FromArgb(30, 169, 169, 169));
            disabledBreakpointBrush.Freeze();
            _disabledBreakpointBrush = disabledBreakpointBrush;

            var debugCurrentLineBrush = new WpfSolidColorBrush(WpfColor.FromArgb(100, 255, 213, 0));  // amber-yellow
            debugCurrentLineBrush.Freeze();
            _debugCurrentLineBrush = debugCurrentLineBrush;
        }

        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            var hasBreakpoints = _tab.BreakpointCount > 0;
            var debugLine = _tab.CurrentDebugLine;
            var hasDebugLine = debugLine > 0;

            if (!hasBreakpoints && !hasDebugLine)
            {
                return;
            }

            if (textView.VisualLines.Count == 0)
            {
                return;
            }

            textView.EnsureVisualLines();

            foreach (var visualLine in textView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                WpfBrush? brush = null;

                if (lineNumber == debugLine)
                {
                    // Debug current-line highlight takes priority over breakpoint marker.
                    brush = _debugCurrentLineBrush;
                }
                else if (hasBreakpoints && _tab.HasBreakpoint(lineNumber))
                {
                    brush = _tab.IsBreakpointEnabled(lineNumber)
                        ? _enabledBreakpointBrush
                        : _disabledBreakpointBrush;
                }

                if (brush is null)
                {
                    continue;
                }

                var rects = BackgroundGeometryBuilder.GetRectsForSegment(
                    textView,
                    visualLine.FirstDocumentLine,
                    true);

                foreach (var rect in rects)
                {
                    var fullWidthRect = new System.Windows.Rect(0, rect.Top, Math.Max(textView.ActualWidth, rect.Right), rect.Height);
                    drawingContext.DrawRectangle(brush, null, fullWidthRect);
                }
            }
        }
    }
}
