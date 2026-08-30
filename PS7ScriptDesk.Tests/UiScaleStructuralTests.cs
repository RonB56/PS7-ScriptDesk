namespace PS7ScriptDesk.Tests;

public sealed class UiScaleStructuralTests
{
    [Fact]
    public void UiScale_IsCentralizedAndAppliedToEveryWindowRoot()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var behaviorCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "UiScaleBehavior.cs");

        Assert.Contains("shell:UiScaleBehavior.IsEnabled", appXaml, StringComparison.Ordinal);
        Assert.Contains("UiScaleBehavior.EnableForApplication(this)", ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml.cs"), StringComparison.Ordinal);
        Assert.Contains("EventManager.RegisterClassHandler", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("LayoutTransform", behaviorCode, StringComparison.Ordinal);
        Assert.DoesNotContain("RenderTransform", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("_scaleService.ScaleChanged += ScaleService_ScaleChanged", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("var service = UiScaleServiceHost.Current", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("service.CurrentFactor", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("InvalidateMeasure", behaviorCode, StringComparison.Ordinal);
        Assert.Contains("Window_Closed", behaviorCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewMenu_ExposesUiScaleAndKeepsEditorZoomShortcutsSeparate()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var viewModelCode = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");

        Assert.Contains("x:Name=\"UiScaleMenuItem\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"Increase UI Scale\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("InputGestureText=\"Ctrl+Alt+=\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("InputGestureText=\"Ctrl+Alt+-\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("InputGestureText=\"Ctrl+Alt+0\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding UiScaleText}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("UI: {UiScalePercentage}%", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("Ctrl+Alt variants are reserved for application UI Scale", mainCode, StringComparison.Ordinal);
        Assert.Contains("Ctrl+= or Ctrl+Plus", mainCode, StringComparison.Ordinal);
        Assert.Contains("ViewModel.ZoomInCommand.Execute(null)", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void Startup_LoadsUiScaleBeforeMainWindowCreationAndPersistenceRetainsIt()
    {
        var appCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml.cs");
        var bootstrapCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "Composition", "AppBootstrapper.cs");
        var mainCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        Assert.Contains("new UiScaleService(applicationSettings.UiScalePercent)", appCode, StringComparison.Ordinal);
        Assert.Contains("UiScaleServiceHost.SetCurrent(uiScaleService)", appCode, StringComparison.Ordinal);
        Assert.Contains("AppBootstrapper.CreateMainWindow(applicationSettingsService, applicationSettings, startupRuntime, uiScaleService)", appCode, StringComparison.Ordinal);
        Assert.Contains("uiScaleService", bootstrapCode, StringComparison.Ordinal);
        Assert.Contains("UiScaleServiceHost.SetCurrent(uiScaleService)", bootstrapCode, StringComparison.Ordinal);
        Assert.Contains("settings.UiScalePercent = _uiScaleService.CurrentPercentage", mainCode, StringComparison.Ordinal);
    }

    [Fact]
    public void ExistingEditorZoomStatus_IsExplicitlyNamedAndTerminalPreferenceIsNotRewritten()
    {
        var viewModelCode = ReadRepositoryFile("PS7ScriptDesk.UI", "ViewModels", "MainWindowViewModel.cs");
        var terminalCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "Controls", "TerminalControl.xaml.cs");

        Assert.Contains("Editor: {(int)Math.Round(_editorZoomLevel)} pt", viewModelCode, StringComparison.Ordinal);
        Assert.Contains("EditorZoomLevel", viewModelCode, StringComparison.Ordinal);
        Assert.DoesNotContain("EditorZoomLevel =", terminalCode, StringComparison.Ordinal);
        Assert.DoesNotContain("UiScale", terminalCode, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] segments)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(segments)));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "PS7ScriptDesk.slnx")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the PowerShellStudio repository root.");
    }
}
