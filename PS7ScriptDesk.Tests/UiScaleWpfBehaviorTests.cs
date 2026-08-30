using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.Shell;
using PS7ScriptDesk.Shell.Dialogs;
using PS7ScriptDesk.Shell.Editor;

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
            var application = EnsureShellApplication();
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

    [Fact]
    public void PublishAsApiWindow_ReceivesCurrentApplicationUiScaleOnCreationAndLiveChanges()
    {
        RunOnStaThread(() =>
        {
            var application = EnsureShellApplication();
            var service = new UiScaleService(150);
            UiScaleServiceHost.SetCurrent(service);
            UiScaleBehavior.EnableForApplication(application);

            var window = CreatePublishAsApiWindow();
            try
            {
                PositionOffscreen(window);
                window.Show();
                DrainLayout(window);

                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                AssertScaleTransform(root, 1.5);

                service.SetPercentage(200, "UnitTest");
                DrainLayout(window);

                AssertScaleTransform(root, 2.0);
                var transform = Assert.IsType<ScaleTransform>(root.LayoutTransform);
                Assert.Equal(2.0, transform.ScaleX, 3);
                Assert.Equal(2.0, transform.ScaleY, 3);

                service.Reset("UnitTest");
                DrainLayout(window);

                Assert.True(root.LayoutTransform is null || root.LayoutTransform == Transform.Identity);
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
    public void OtherCustomDialog_ReceivesApplicationUiScaleThroughSharedWindowHook()
    {
        RunOnStaThread(() =>
        {
            var application = EnsureShellApplication();
            var service = new UiScaleService(200);
            UiScaleServiceHost.SetCurrent(service);
            UiScaleBehavior.EnableForApplication(application);

            var owner = CreateTestWindow(new Grid { Width = 100, Height = 40 });
            var dialog = new GoToLineDialog(owner, currentLine: 1, maxLine: 20);
            try
            {
                PositionOffscreen(owner);
                PositionOffscreen(dialog);
                owner.Show();
                dialog.Show();
                DrainLayout(dialog);

                var root = Assert.IsAssignableFrom<FrameworkElement>(dialog.Content);
                AssertScaleTransform(root, 2.0);
            }
            finally
            {
                if (dialog.IsVisible)
                {
                    dialog.Close();
                }

                if (owner.IsVisible)
                {
                    owner.Close();
                }

                UiScaleServiceHost.SetCurrent(new UiScaleService());
            }
        });
    }

    [Fact]
    public void AttachedWindow_ReappliesScaleWhenContentRootIsReplaced()
    {
        RunOnStaThread(() =>
        {
            var service = new UiScaleService(150);
            UiScaleServiceHost.SetCurrent(service);

            var firstRoot = new Grid { Width = 240, Height = 120 };
            var secondRoot = new Grid { Width = 240, Height = 120 };
            var window = CreateTestWindow(firstRoot);
            try
            {
                UiScaleBehavior.SetIsEnabled(window, true);
                window.Show();
                DrainLayout(window);

                AssertScaleTransform(firstRoot, 1.5);

                window.Content = secondRoot;
                DrainLayout(window);

                Assert.True(firstRoot.LayoutTransform is null || firstRoot.LayoutTransform == Transform.Identity);
                AssertScaleTransform(secondRoot, 1.5);
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

    private static RestApiPublishWizardWindow CreatePublishAsApiWindow()
    {
        var request = new ApiPublishWizardRequest("Widget API", @"C:\Temp\Widget.ps1", "function Get-Widget {}");
        var extent = new ApiSourceExtent(1, 1, 1, 22, 0, 21, "function Get-Widget {}");
        var metadata = new ApiMetadataResult(
            parsedSuccessfully: true,
            sourcePath: request.SourceScriptPath,
            syntaxErrors: [],
            functions:
            [
                new ApiFunctionMetadata(
                    "Get-Widget",
                    ApiFunctionConstructKind.Function,
                    isAdvancedFunction: false,
                    isTopLevel: true,
                    parentFunctionName: null,
                    isPublishable: true,
                    extent,
                    parameters: [],
                    commentHelp: null,
                    declaredOutputTypes: [],
                    warnings: [])
            ],
            warnings: []);
        var configuration = ApiPublishConfiguration.CreateDefaultForScriptPath(request.SourceScriptPath);
        configuration.Endpoints.Add(ApiEndpointConfiguration.CreateRest("Get-Widget", ApiHttpMethod.Get, "/api/get-widget"));
        return new RestApiPublishWizardWindow(
            request,
            metadata,
            configuration,
            new FakeApiPublishConfigurationStore(),
            new FakeApiLocalTestHostService(),
            new FakeApiBuildPublishService())
        {
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
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

    private static void PositionOffscreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -2000;
        window.Top = -2000;
        window.ShowInTaskbar = false;
    }

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

    private sealed class FakeApiPublishConfigurationStore : IApiPublishConfigurationStore
    {
        public string? GetCompanionPath(string? sourceScriptPath) => Path.ChangeExtension(sourceScriptPath, ".ps7api.json");

        public bool ConfigurationExists(string sourceScriptPath) => false;

        public ApiPublishConfiguration Load(string sourceScriptPath) => throw new InvalidOperationException("The fake store does not load configurations.");

        public void Save(string sourceScriptPath, ApiPublishConfiguration configuration)
        {
        }
    }

    private sealed class FakeApiLocalTestHostService : IApiLocalTestHostService
    {
        public event EventHandler<ApiLocalTestHostStatus>? StatusChanged;

        public ApiLocalTestHostStatus CurrentStatus { get; } = new();

        public Task<ApiLocalTestHostStartResult> StartAsync(ApiLocalTestHostRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiLocalTestHostStartResult.Failure("Not started by test.", string.Empty, CurrentStatus));

        public Task<ApiLocalTestHostStartResult> RestartAsync(ApiLocalTestHostRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(ApiLocalTestHostStartResult.Failure("Not restarted by test.", string.Empty, CurrentStatus));

        public Task<ApiLocalTestHostStatus> StopAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(CurrentStatus);

        public ValueTask DisposeAsync()
        {
            StatusChanged = null;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeApiBuildPublishService : IApiBuildPublishService
    {
        public Task<ApiBuildPublishResult> GenerateProjectAsync(ApiBuildPublishRequest request, CancellationToken cancellationToken = default, IProgress<ApiBuildPublishProgressUpdate>? progress = null)
            => Task.FromResult(ApiBuildPublishResult.Failure("Not generated by test.", string.Empty));

        public Task<ApiBuildPublishResult> BuildAsync(ApiBuildPublishRequest request, CancellationToken cancellationToken = default, IProgress<ApiBuildPublishProgressUpdate>? progress = null)
            => Task.FromResult(ApiBuildPublishResult.Failure("Not built by test.", string.Empty));

        public Task<ApiBuildPublishResult> PublishAsync(ApiBuildPublishRequest request, CancellationToken cancellationToken = default, IProgress<ApiBuildPublishProgressUpdate>? progress = null)
            => Task.FromResult(ApiBuildPublishResult.Failure("Not published by test.", string.Empty));
    }
}
