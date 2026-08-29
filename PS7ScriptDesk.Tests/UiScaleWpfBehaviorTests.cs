using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Shell;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class UiScaleWpfBehaviorTests
{
    [Fact]
    public void AttachedWindowRoot_UpdatesVisualBoundsWhenScaleServiceChanges()
    {
        RunOnStaThread(() =>
        {
            var service = new UiScaleService();
            UiScaleServiceHost.SetCurrent(service);

            var measuredElement = new Border
            {
                Width = 100,
                Height = 40
            };
            var root = new Grid
            {
                Width = 240,
                Height = 120
            };
            root.Children.Add(measuredElement);

            var window = CreateTestWindow(root);
            try
            {
                UiScaleBehavior.SetIsEnabled(window, true);
                window.Show();
                DrainLayout(window);

                AssertTransformedWidth(window, measuredElement, 100);

                service.SetPercentage(150, "UnitTest");
                DrainLayout(window);

                AssertScaleTransform(root, 1.5);
                AssertTransformedWidth(window, measuredElement, 150);

                service.SetPercentage(75, "UnitTest");
                DrainLayout(window);

                AssertScaleTransform(root, 0.75);
                AssertTransformedWidth(window, measuredElement, 75);

                service.Reset("UnitTest");
                DrainLayout(window);

                Assert.True(root.LayoutTransform is null || root.LayoutTransform == Transform.Identity);
                AssertTransformedWidth(window, measuredElement, 100);
            }
            finally
            {
                UiScaleBehavior.SetIsEnabled(window, false);
                window.Close();
                UiScaleServiceHost.SetCurrent(new UiScaleService());
            }
        });
    }

    [Fact]
    public void NewlyAttachedWindowRoot_ReceivesCurrentScaleAndDoesNotStackScaleTransforms()
    {
        RunOnStaThread(() =>
        {
            var service = new UiScaleService(150);
            UiScaleServiceHost.SetCurrent(service);

            var measuredElement = new Border
            {
                Width = 100,
                Height = 40
            };
            var originalTransform = new TranslateTransform(3, 4);
            var root = new Grid
            {
                Width = 240,
                Height = 120,
                LayoutTransform = originalTransform
            };
            root.Children.Add(measuredElement);

            var window = CreateTestWindow(root);
            try
            {
                UiScaleBehavior.SetIsEnabled(window, true);
                window.Show();
                DrainLayout(window);

                var firstTransform = Assert.IsType<TransformGroup>(root.LayoutTransform);
                Assert.Same(originalTransform, firstTransform.Children[0]);
                AssertScaleTransform(firstTransform.Children[1], 1.5);
                AssertTransformedWidth(window, measuredElement, 150);

                service.SetPercentage(175, "UnitTest");
                DrainLayout(window);

                var secondTransform = Assert.IsType<TransformGroup>(root.LayoutTransform);
                Assert.Equal(2, secondTransform.Children.Count);
                Assert.Same(originalTransform, secondTransform.Children[0]);
                AssertScaleTransform(secondTransform.Children[1], 1.75);

                UiScaleBehavior.SetIsEnabled(window, false);
                DrainLayout(window);
                Assert.Same(originalTransform, root.LayoutTransform);

                service.SetPercentage(200, "UnitTest");
                DrainLayout(window);
                Assert.Same(originalTransform, root.LayoutTransform);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.Close();
                }

                UiScaleServiceHost.SetCurrent(new UiScaleService());
            }
        });
    }

    [Fact]
    public void ApplicationWindowHook_AttachesNewWindowsToTheCurrentScaleService()
    {
        RunOnStaThread(() =>
        {
            var application = System.Windows.Application.Current ?? new System.Windows.Application();
            var service = new UiScaleService(125);
            UiScaleServiceHost.SetCurrent(service);
            UiScaleBehavior.EnableForApplication(application);

            var measuredElement = new Border
            {
                Width = 100,
                Height = 40
            };
            var root = new Grid
            {
                Width = 240,
                Height = 120
            };
            root.Children.Add(measuredElement);

            var window = CreateTestWindow(root);
            try
            {
                window.Show();
                DrainLayout(window);

                AssertScaleTransform(root, 1.25);
                AssertTransformedWidth(window, measuredElement, 125);

                service.SetPercentage(150, "UnitTest");
                DrainLayout(window);

                AssertScaleTransform(root, 1.5);
                AssertTransformedWidth(window, measuredElement, 150);
            }
            finally
            {
                UiScaleBehavior.SetIsEnabled(window, false);
                window.Close();
                UiScaleServiceHost.SetCurrent(new UiScaleService());
            }
        });
    }

    private static Window CreateTestWindow(FrameworkElement root)
        => new()
        {
            Width = 400,
            Height = 300,
            Content = root,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -2000,
            Top = -2000
        };

    private static void AssertScaleTransform(FrameworkElement root, double expected)
        => AssertScaleTransform(Assert.IsType<ScaleTransform>(root.LayoutTransform), expected);

    private static void AssertScaleTransform(Transform transform, double expected)
    {
        var scale = Assert.IsType<ScaleTransform>(transform);
        Assert.Equal(expected, scale.ScaleX, 3);
        Assert.Equal(expected, scale.ScaleY, 3);
    }

    private static void AssertTransformedWidth(Window window, FrameworkElement element, double expected)
    {
        var bounds = element.TransformToAncestor(window).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        Assert.InRange(bounds.Width, expected - 1, expected + 1);
    }

    private static void DrainLayout(Window window)
    {
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
