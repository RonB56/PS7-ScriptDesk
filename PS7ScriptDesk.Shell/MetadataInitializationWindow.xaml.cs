using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PS7ScriptDesk.Shell;

public partial class MetadataInitializationWindow : Window
{
    private Window? _ownerWindow;

    public MetadataInitializationWindow()
    {
        InitializeComponent();
    }

    internal void ApplyContent(
        string title,
        string body,
        string phaseText,
        string glyph,
        bool showProgress,
        string backgroundResourceKey,
        string borderResourceKey,
        string foregroundResourceKey)
    {
        TitleTextBlock.Text = title;
        BodyTextBlock.Text = body;
        PhaseTextBlock.Text = phaseText;
        GlyphTextBlock.Text = glyph;
        CardBorder.SetResourceReference(Border.BackgroundProperty, backgroundResourceKey);
        CardBorder.SetResourceReference(Border.BorderBrushProperty, borderResourceKey);
        TitleTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
        BodyTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
        PhaseTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
        GlyphTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
        ProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.IsIndeterminate = showProgress;
    }

    internal void ShowForOwner(Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        if (_ownerWindow is null)
        {
            _ownerWindow = owner;
            Owner = owner;
            owner.LocationChanged += Owner_LocationChanged;
            owner.SizeChanged += Owner_SizeChanged;
            owner.StateChanged += Owner_StateChanged;
        }

        if (!IsVisible)
        {
            Show();
        }

        PositionRelativeToOwner();
    }

    internal void HideForOwner()
    {
        if (IsVisible)
        {
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_ownerWindow is not null)
        {
            _ownerWindow.LocationChanged -= Owner_LocationChanged;
            _ownerWindow.SizeChanged -= Owner_SizeChanged;
            _ownerWindow.StateChanged -= Owner_StateChanged;
            _ownerWindow = null;
        }

        base.OnClosed(e);
    }

    private void Owner_LocationChanged(object? sender, EventArgs e) => PositionRelativeToOwner();

    private void Owner_SizeChanged(object? sender, SizeChangedEventArgs e) => PositionRelativeToOwner();

    private void Owner_StateChanged(object? sender, EventArgs e)
    {
        if (_ownerWindow?.WindowState == WindowState.Minimized)
        {
            Hide();
            return;
        }

        if (_ownerWindow?.IsVisible == true)
        {
            Show();
            PositionRelativeToOwner();
        }
    }

    private void PositionRelativeToOwner()
    {
        if (_ownerWindow is null || !_ownerWindow.IsVisible || _ownerWindow.WindowState == WindowState.Minimized)
        {
            return;
        }

        var popupWidth = ActualWidth > 0 ? ActualWidth : Width;
        var popupHeight = ActualHeight > 0 ? ActualHeight : MinHeight;
        var margin = 24d;
        var topOffset = 96d;
        var ownerWidth = _ownerWindow.ActualWidth > 0 ? _ownerWindow.ActualWidth : _ownerWindow.Width;
        var ownerHeight = _ownerWindow.ActualHeight > 0 ? _ownerWindow.ActualHeight : _ownerWindow.Height;
        var ownerLocalX = Math.Max(margin, ownerWidth - popupWidth - margin);
        var ownerLocalY = Math.Max(margin, Math.Min(topOffset, Math.Max(margin, ownerHeight - popupHeight - margin)));
        var screenPoint = _ownerWindow.PointToScreen(new Point(ownerLocalX, ownerLocalY));
        var transformFromDevice = PresentationSource.FromVisual(_ownerWindow)?.CompositionTarget?.TransformFromDevice;
        var targetPoint = transformFromDevice?.Transform(screenPoint) ?? screenPoint;

        Left = targetPoint.X;
        Top = targetPoint.Y;
    }
}
