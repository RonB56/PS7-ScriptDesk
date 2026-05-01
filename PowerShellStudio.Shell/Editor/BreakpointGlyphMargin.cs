using System;
using WpfKeyboard = System.Windows.Input.Keyboard;
using WpfModifierKeys = System.Windows.Input.ModifierKeys;
using WpfMouseButton = System.Windows.Input.MouseButton;
using WpfMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using WpfMouseButtonState = System.Windows.Input.MouseButtonState;
using WpfMouseEventArgs = System.Windows.Input.MouseEventArgs;
using ICSharpCode.AvalonEdit.Editing;
using PowerShellStudio.UI.ViewModels;
using WpfBrush = System.Windows.Media.Brush;
using WpfDrawingContext = System.Windows.Media.DrawingContext;
using WpfHitTestResult = System.Windows.Media.HitTestResult;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPointHitTestParameters = System.Windows.Media.PointHitTestParameters;
using WpfPointHitTestResult = System.Windows.Media.PointHitTestResult;
using WpfPoint = System.Windows.Point;
using WpfRect = System.Windows.Rect;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfStreamGeometry = System.Windows.Media.StreamGeometry;
using WpfSize = System.Windows.Size;

namespace PowerShellStudio.Shell.Editor
{
    /// <summary>
    /// Dedicated AvalonEdit left-margin column for breakpoint clicks and breakpoint glyphs.
    ///
    /// This intentionally keeps breakpoint toggling away from the text area, line numbers,
    /// folding margin, and diagnostics margin so text selection does not accidentally set
    /// or remove a breakpoint.
    /// </summary>
    public sealed class BreakpointGlyphMargin : AbstractMargin
    {
        private const double MarginWidth = 18.0;
        private const double ClickDragTolerance = 4.0;

        private readonly WpfBrush _backgroundBrush;
        private readonly WpfBrush _enabledFillBrush;
        private readonly WpfBrush _disabledFillBrush;
        private readonly WpfBrush _disabledCenterBrush;
        private readonly WpfBrush _debugCurrentLineBrush;
        private readonly WpfPen _enabledStrokePen;
        private readonly WpfPen _disabledStrokePen;
        private readonly WpfPen _separatorPen;

        private EditorTabViewModel _tab;
        private int? _pendingLineNumber;
        private WpfPoint _mouseDownPoint;
        private bool _mouseMovedTooFar;

        public BreakpointGlyphMargin(EditorTabViewModel tab)
        {
            _tab = tab ?? throw new ArgumentNullException(nameof(tab));

            _backgroundBrush = CreateFrozenBrush(WpfColor.FromArgb(18, 15, 23, 42));
            _enabledFillBrush = CreateFrozenBrush(WpfColor.FromRgb(220, 20, 60));
            _disabledFillBrush = CreateFrozenBrush(WpfColor.FromRgb(148, 163, 184));
            _disabledCenterBrush = CreateFrozenBrush(WpfColor.FromRgb(248, 250, 252));
            _debugCurrentLineBrush = CreateFrozenBrush(WpfColor.FromRgb(245, 158, 11));
            _enabledStrokePen = CreateFrozenPen(WpfColor.FromRgb(127, 29, 29), 1.0);
            _disabledStrokePen = CreateFrozenPen(WpfColor.FromRgb(71, 85, 105), 1.0);
            _separatorPen = CreateFrozenPen(WpfColor.FromArgb(90, 148, 163, 184), 1.0);

            ToolTip = "Click here to toggle a breakpoint for this line. Dragging/selecting text will not change breakpoints.";
        }

        public event Action<int>? BreakpointLineClicked;

        public void SetTab(EditorTabViewModel tab)
        {
            _tab = tab ?? throw new ArgumentNullException(nameof(tab));
            CancelPendingClick();
            InvalidateVisual();
        }

        public void Refresh()
        {
            InvalidateVisual();
        }

        protected override WpfSize MeasureOverride(WpfSize availableSize)
        {
            return new WpfSize(MarginWidth, 0);
        }

        protected override void OnRender(WpfDrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            drawingContext.DrawRectangle(_backgroundBrush, null, new WpfRect(0, 0, ActualWidth, ActualHeight));
            drawingContext.DrawLine(_separatorPen, new WpfPoint(ActualWidth - 0.5, 0), new WpfPoint(ActualWidth - 0.5, ActualHeight));

            if (TextView?.VisualLinesValid != true || TextView.VisualLines is null)
            {
                return;
            }

            foreach (var visualLine in TextView.VisualLines)
            {
                var lineNumber = visualLine.FirstDocumentLine.LineNumber;
                var hasBreakpoint = _tab.HasBreakpoint(lineNumber);
                var isCurrentDebugLine = _tab.CurrentDebugLine == lineNumber;

                if (!hasBreakpoint && !isCurrentDebugLine)
                {
                    continue;
                }

                var y = visualLine.VisualTop - TextView.ScrollOffset.Y;
                var center = new WpfPoint(ActualWidth / 2.0, y + (visualLine.Height / 2.0));
                var radius = Math.Max(4.5, Math.Min(6.0, (visualLine.Height - 4.0) / 2.0));

                if (isCurrentDebugLine)
                {
                    DrawExecutionPointer(drawingContext, center, radius);
                }

                if (!hasBreakpoint)
                {
                    continue;
                }

                if (_tab.IsBreakpointEnabled(lineNumber))
                {
                    drawingContext.DrawEllipse(_enabledFillBrush, _enabledStrokePen, center, radius, radius);
                }
                else
                {
                    drawingContext.DrawEllipse(_disabledFillBrush, _disabledStrokePen, center, radius, radius);
                    drawingContext.DrawEllipse(_disabledCenterBrush, null, center, Math.Max(1.8, radius - 3.0), Math.Max(1.8, radius - 3.0));
                }
            }
        }

        protected override WpfHitTestResult HitTestCore(WpfPointHitTestParameters hitTestParameters)
        {
            return new WpfPointHitTestResult(this, hitTestParameters.HitPoint);
        }

        protected override void OnMouseLeftButtonDown(WpfMouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonDown(e);

            CancelPendingClick();

            if (e.ChangedButton != WpfMouseButton.Left || WpfKeyboard.Modifiers != WpfModifierKeys.None)
            {
                return;
            }

            if (TryGetVisualLineNumberFromPoint(e.GetPosition(this), out var lineNumber))
            {
                _pendingLineNumber = lineNumber;
                _mouseDownPoint = e.GetPosition(this);
                _mouseMovedTooFar = false;
                CaptureMouse();
                e.Handled = true;
            }
        }

        protected override void OnMouseMove(WpfMouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_pendingLineNumber is null)
            {
                return;
            }

            if (e.LeftButton != WpfMouseButtonState.Pressed)
            {
                CancelPendingClick();
                return;
            }

            var currentPoint = e.GetPosition(this);
            if (Math.Abs(currentPoint.X - _mouseDownPoint.X) > ClickDragTolerance ||
                Math.Abs(currentPoint.Y - _mouseDownPoint.Y) > ClickDragTolerance)
            {
                _mouseMovedTooFar = true;
            }
        }

        protected override void OnMouseLeftButtonUp(WpfMouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            var pendingLineNumber = _pendingLineNumber;
            var movedTooFar = _mouseMovedTooFar;
            CancelPendingClick();

            if (pendingLineNumber is null || movedTooFar)
            {
                return;
            }

            if (TryGetVisualLineNumberFromPoint(e.GetPosition(this), out var releasedLineNumber) &&
                releasedLineNumber == pendingLineNumber.Value)
            {
                BreakpointLineClicked?.Invoke(releasedLineNumber);
                e.Handled = true;
            }
        }

        private bool TryGetVisualLineNumberFromPoint(WpfPoint point, out int lineNumber)
        {
            lineNumber = -1;

            if (point.X < 0 || point.X > ActualWidth || TextView?.VisualLinesValid != true || TextView.VisualLines is null)
            {
                return false;
            }

            var y = point.Y + TextView.ScrollOffset.Y;
            foreach (var visualLine in TextView.VisualLines)
            {
                var lineTop = visualLine.VisualTop;
                var lineBottom = lineTop + visualLine.Height;
                if (y >= lineTop && y <= lineBottom)
                {
                    lineNumber = visualLine.FirstDocumentLine.LineNumber;
                    return lineNumber > 0;
                }
            }

            return false;
        }

        private void CancelPendingClick()
        {
            _pendingLineNumber = null;
            _mouseMovedTooFar = false;

            if (IsMouseCaptured)
            {
                ReleaseMouseCapture();
            }
        }

        private void DrawExecutionPointer(WpfDrawingContext drawingContext, WpfPoint center, double radius)
        {
            var geometry = new WpfStreamGeometry();
            using (var context = geometry.Open())
            {
                context.BeginFigure(new WpfPoint(center.X - radius + 1.0, center.Y - radius), isFilled: true, isClosed: true);
                context.LineTo(new WpfPoint(center.X + radius + 1.0, center.Y), isStroked: true, isSmoothJoin: true);
                context.LineTo(new WpfPoint(center.X - radius + 1.0, center.Y + radius), isStroked: true, isSmoothJoin: true);
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(_debugCurrentLineBrush, null, geometry);
        }

        private static WpfBrush CreateFrozenBrush(WpfColor color)
        {
            var brush = new WpfSolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static WpfPen CreateFrozenPen(WpfColor color, double thickness)
        {
            var pen = new WpfPen(CreateFrozenBrush(color), thickness);
            pen.Freeze();
            return pen;
        }
    }
}
