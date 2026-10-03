using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Debug;

namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class DebuggerPopOutLifecycleTests
{
    [Fact]
    public void XClose_CancelsCurrentClosingCallback_ThenDocksBackWithoutReentrantClose()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var window = new DebugPaneWindow();
            var dockBackRequests = 0;
            window.DockBackRequested += (_, _) =>
            {
                dockBackRequests++;
                window.CloseForDockBack();
            };

            try
            {
                window.Show();
                window.Close();
                window.Close();

                Assert.Equal(0, dockBackRequests);

                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

                Assert.Equal(1, dockBackRequests);
                Assert.False(window.IsVisible);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    [Fact]
    public void BottomToolXClose_UsesTheSameDeferredDockBackLifecycle()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var window = new BottomToolWindow();
            var dockBackRequests = 0;
            window.DockBackRequested += (_, _) =>
            {
                dockBackRequests++;
                window.CloseForDockBack();
            };

            try
            {
                window.Show();
                window.Close();
                window.Close();
                Assert.Equal(0, dockBackRequests);

                window.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() => { }));

                Assert.Equal(1, dockBackRequests);
                Assert.False(window.IsVisible);
            }
            finally
            {
                if (window.IsVisible)
                {
                    window.CloseForOwnerShutdown();
                }
            }
        });
    }

    [Fact]
    public void OwnerShutdown_ClosesDockableWindowsWithoutRaisingDockBack()
    {
        RunOnStaThread(() =>
        {
            EnsureShellApplication();
            var debugWindow = new DebugPaneWindow();
            var bottomWindow = new BottomToolWindow();
            var dockBackRequests = 0;
            debugWindow.DockBackRequested += (_, _) => dockBackRequests++;
            bottomWindow.DockBackRequested += (_, _) => dockBackRequests++;

            try
            {
                debugWindow.Show();
                bottomWindow.Show();
                debugWindow.CloseForOwnerShutdown();
                bottomWindow.CloseForOwnerShutdown();

                Assert.Equal(0, dockBackRequests);
                Assert.False(debugWindow.IsVisible);
                Assert.False(bottomWindow.IsVisible);
            }
            finally
            {
                if (debugWindow.IsVisible)
                {
                    debugWindow.CloseForOwnerShutdown();
                }

                if (bottomWindow.IsVisible)
                {
                    bottomWindow.CloseForOwnerShutdown();
                }
            }
        });
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

    private static App EnsureShellApplication()
    {
        if (System.Windows.Application.Current is App existingApp)
        {
            return existingApp;
        }

        var app = new App();
        app.InitializeComponent();
        return app;
    }
}
