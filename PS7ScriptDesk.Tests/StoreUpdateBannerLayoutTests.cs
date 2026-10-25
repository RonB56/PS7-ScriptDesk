using System;
using System.Windows;
using System.Windows.Controls;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Services;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class StoreUpdateBannerLayoutTests
{
    [Fact]
    public void BannerAndTerminalHaveDedicatedNonOverlappingLayoutRegions()
    {
        RunOnStaThread(() =>
        {
            var root = new DockPanel { Width = 1000, Height = 700 };
            var bannerHost = new ContentControl { Visibility = Visibility.Visible };
            var banner = new StoreUpdateBanner(new StoreUpdateService(), CreateAvailableResult());
            bannerHost.Content = banner;
            DockPanel.SetDock(bannerHost, Dock.Top);
            var terminal = new Border { Height = 600 };
            root.Children.Add(bannerHost);
            root.Children.Add(terminal);

            root.Measure(new Size(1000, 700));
            root.Arrange(new Rect(0, 0, 1000, 700));
            root.UpdateLayout();

            Assert.True(bannerHost.ActualHeight > 0);
            Assert.True(terminal.ActualHeight > 0);
            Assert.True(bannerHost.TransformToAncestor(root).Transform(new Point(0, 0)).Y + bannerHost.ActualHeight <=
                        terminal.TransformToAncestor(root).Transform(new Point(0, 0)).Y + 0.1);
        });
    }

    [Fact]
    public void CollapsingBannerRemovesItsVerticalLayoutSpace()
    {
        RunOnStaThread(() =>
        {
            var root = new DockPanel { Width = 1000, Height = 700 };
            var bannerHost = new ContentControl { Visibility = Visibility.Visible, Content = new StoreUpdateBanner(new StoreUpdateService(), CreateAvailableResult()) };
            DockPanel.SetDock(bannerHost, Dock.Top);
            var terminal = new Border();
            root.Children.Add(bannerHost);
            root.Children.Add(terminal);

            root.Measure(new Size(1000, 700));
            root.Arrange(new Rect(0, 0, 1000, 700));
            root.UpdateLayout();
            var visibleHeight = bannerHost.ActualHeight;

            bannerHost.Visibility = Visibility.Collapsed;
            root.UpdateLayout();

            Assert.True(visibleHeight > 0);
            Assert.Equal(0, bannerHost.ActualHeight);
            Assert.Equal(700, terminal.ActualHeight, precision: 1);
        });
    }

    [Fact]
    public void BannerProjectsCurrentAndAvailableVersionsAndDismisses()
    {
        RunOnStaThread(() =>
        {
            var banner = new StoreUpdateBanner(new StoreUpdateService(), CreateAvailableResult());
            var details = (TextBlock)banner.FindName("DetailsTextBlock");
            var dismiss = (Button)banner.FindName("DismissButton");
            var dismissed = false;
            banner.DismissRequested += (_, _) => dismissed = true;

            Assert.Contains("1.0.71.0", details.Text, StringComparison.Ordinal);
            Assert.Contains("1.0.72.0", details.Text, StringComparison.Ordinal);
            dismiss.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dismissed);
        });
    }

    private static StoreUpdateCheckResult CreateAvailableResult()
        => new()
        {
            PackagingKind = StoreUpdatePackagingKind.StoreInstalledManaged,
            AvailabilityState = StoreUpdateAvailabilityState.ConfirmedUpdateAvailable,
            StoreUpdateCheckAvailable = true,
            PackageVersion = "1.0.71.0",
            UpdateCount = 1,
            Updates = new() { new StoreUpdatePackageInfo("PS7ScriptDesk", isMandatory: false, version: "1.0.72.0") }
        };

    private static void RunOnStaThread(Action action) => WpfTestDispatcher.Run(action);
}
