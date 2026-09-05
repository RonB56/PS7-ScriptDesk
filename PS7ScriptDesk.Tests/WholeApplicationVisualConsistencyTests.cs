namespace PS7ScriptDesk.Tests;

[Collection("WpfUi")]
public sealed class WholeApplicationVisualConsistencyTests
{
    [Fact]
    public void PhaseSeven_AddsSharedDialogStylesAndFlattensContextHelpTooltip()
    {
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");

        Assert.Contains("x:Key=\"IdeDialogButtonBaseStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogPrimaryButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogSecondaryButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogTertiaryButtonStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"IdeDialogPanelStyle\"", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"30\" />", appXaml, StringComparison.Ordinal);
        Assert.Contains("CornerRadius=\"4\"", appXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"12,0,0,12\"", appXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseSeven_HelpAndEditorDialogsUseSharedStylesWithoutDecorativeCards()
    {
        var aboutXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Help", "AboutWindow.xaml");
        var helpXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Help", "ContextHelpWindow.xaml");
        var findReplaceXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "FindReplaceWindow.xaml");
        var goToLineXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Editor", "GoToLineDialog.xaml");
        var conflictXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Dialogs", "ExternalFileConflictDialog.xaml");

        Assert.DoesNotContain("PanelCardBorderStyle", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PanelCardBorderStyle", helpXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogPrimaryButtonStyle}\"", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogSecondaryButtonStyle}\"", helpXaml, StringComparison.Ordinal);

        Assert.DoesNotContain("FindReplaceActionButtonStyle", findReplaceXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FindReplacePrimaryActionButtonStyle", findReplaceXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogPrimaryButtonStyle}\"", findReplaceXaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(findReplaceXaml, "Style=\"{StaticResource IdeDialogSecondaryButtonStyle}\"") >= 3);

        Assert.DoesNotContain("Foreground=\"DimGray\"", goToLineXaml, StringComparison.Ordinal);
        Assert.Contains("Foreground=\"{DynamicResource Theme.Text.Secondary}\"", goToLineXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource IdeDialogPrimaryButtonStyle}\"", goToLineXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource IdeDialogPrimaryButtonStyle}\"", conflictXaml, StringComparison.Ordinal);
        Assert.True(CountOccurrences(conflictXaml, "Style=\"{DynamicResource IdeDialogSecondaryButtonStyle}\"") >= 3);
    }

    [Fact]
    public void PhaseSeven_RuntimeAndUpdateWindowsAreThemeAwareAndUseCompactActionAreas()
    {
        var runtimeXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "RuntimeResolverWindow.xaml");
        var updateXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "StoreUpdateWindow.xaml");

        foreach (var xaml in new[] { runtimeXaml, updateXaml })
        {
            Assert.Contains("Background=\"{DynamicResource Theme.App.Background}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Style=\"{DynamicResource IdeDialogPanelStyle}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Style=\"{DynamicResource IdeDialogSecondaryButtonStyle}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("<WrapPanel Grid.Row=", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("<UniformGrid Grid.Row=", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("#F8FBFF", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("#172033", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("CornerRadius=\"8\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("CornerRadius=\"10\"", xaml, StringComparison.Ordinal);
        }

        Assert.Contains("Content=\"Browse for pwsh.exe\"", runtimeXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource IdeDialogPrimaryButtonStyle}\"", runtimeXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Install Update Now\"", updateXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{DynamicResource IdeDialogPrimaryButtonStyle}\"", updateXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseSeven_WizardsAndFloatingDebugPaneReuseRestrainedIdeResources()
    {
        var wizardStylesXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardStyles.xaml");
        var debugPaneXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml");

        Assert.Contains("<Setter Property=\"Padding\" Value=\"12,5\" />", wizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"30\" />", wizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogPanelStyle}\"", wizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{StaticResource Padding.Card}\"", wizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{StaticResource Radius.Medium}\"", wizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"Margin\" Value=\"0,0,0,6\" />", wizardStylesXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"Padding\" Value=\"16,7\" />", wizardStylesXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"CornerRadius\" Value=\"5\" />", wizardStylesXaml, StringComparison.Ordinal);

        Assert.True(
            debugPaneXaml.Contains("Style=\"{DynamicResource IdeToolWindowHeaderTextStyle}\"", StringComparison.Ordinal) ||
            debugPaneXaml.Contains("Style=\"{DynamicResource IdeSectionHeaderTextStyle}\"", StringComparison.Ordinal));
        Assert.Equal(3, CountOccurrences(debugPaneXaml, "Style=\"{DynamicResource IdeToolWindowDataGridStyle}\""));
        Assert.True(CountOccurrences(debugPaneXaml, "Style=\"{DynamicResource IdeDialogSecondaryButtonStyle}\"") >= 2);
    }

    [Fact]
    public void PhaseEightB_HelpMenuContainsSingleCheckForUpdatesCommand()
    {
        var mainWindowXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var mainWindowCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");

        // The command is intentionally presented as a status action because
        // local/unpackaged builds may only have manual Store instructions.
        Assert.Equal(1, CountOccurrences(mainWindowXaml, "Header=\"Store Update _Status\""));
        Assert.Contains("Click=\"CheckForUpdates_Click\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("help:ContextHelp.Key=\"App.StoreUpdate\"", mainWindowXaml, StringComparison.Ordinal);
        Assert.Contains("private void CheckForUpdates_Click", mainWindowCode, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"Store Update _Status\"", mainWindowXaml[..mainWindowXaml.IndexOf("<MenuItem Header=\"_Help\"", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseEightB_ManualUpdateWorkflowIsWiredForReentryAndAllResultWindows()
    {
        var mainWindowCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml.cs");
        var appCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml.cs");

        // Re-entry is now represented by the cached startup result and a
        // status window; the former private in-progress gate was removed.
        Assert.Contains("StoreUpdateStartupState.Read()", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("snapshot.CheckInProgress", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("ShowManualStoreUpdateWindow(snapshot.Service, checkResult)", mainWindowCode, StringComparison.Ordinal);
        Assert.Contains("new StoreUpdateWindow(storeUpdateService, checkResult, isMandatory: checkResult.HasMandatoryUpdate)", mainWindowCode, StringComparison.Ordinal);

        Assert.Contains("!storeUpdateCheckResult.ShouldShowAutomaticNotification", appCode, StringComparison.Ordinal);
        Assert.Contains("isMandatory: storeUpdateCheckResult.HasMandatoryUpdate", appCode, StringComparison.Ordinal);
    }

    [Fact]
    public void PhaseEightB_ProductionAuxiliaryWindowsDeclareThemeAwareWindowShells()
    {
        var windowPaths = new[]
        {
            Path.Combine("PS7ScriptDesk.Shell", "FindReplaceWindow.xaml"),
            Path.Combine("PS7ScriptDesk.Shell", "Editor", "GoToLineDialog.xaml"),
            Path.Combine("PS7ScriptDesk.Shell", "Dialogs", "ExternalFileConflictDialog.xaml"),
            Path.Combine("PS7ScriptDesk.Shell", "Debug", "DebugPaneWindow.xaml")
        };

        foreach (var path in windowPaths)
        {
            var xaml = ReadRepositoryFile(path.Split(Path.DirectorySeparatorChar));
            Assert.Contains("Background=\"{DynamicResource Theme.App.Background}\"", xaml, StringComparison.Ordinal);
            Assert.Contains("Foreground=\"{DynamicResource Theme.Text.Primary}\"", xaml, StringComparison.Ordinal);
            Assert.DoesNotContain("Background=\"White\"", xaml, StringComparison.Ordinal);
        }

        var consolePrototypeXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "ConsolePrototypeWindow.xaml");
        Assert.DoesNotContain("Theme.App.Background", consolePrototypeXaml, StringComparison.Ordinal);

        foreach (var themeFile in new[] { "DarkTheme.xaml", "LightTheme.xaml", "IseBlueTheme.xaml" })
        {
            var themeXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Themes", themeFile);
            Assert.Contains("x:Key=\"Theme.App.Background\"", themeXaml, StringComparison.Ordinal);
            Assert.Contains("x:Key=\"Theme.Text.Primary\"", themeXaml, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PhaseEightB_StoreUpdateWindowManualResultStatesExposeUsefulFeedback()
    {
        RunOnStaThread(() =>
        {
            var service = new PS7ScriptDesk.Shell.Services.StoreUpdateService();

            var localWindow = new PS7ScriptDesk.Shell.StoreUpdateWindow(
                service,
                new PS7ScriptDesk.Shell.Services.StoreUpdateCheckResult
                {
                    PackagingKind = PS7ScriptDesk.Shell.Services.StoreUpdatePackagingKind.UnpackagedLocalBuild,
                    AvailabilityState = PS7ScriptDesk.Shell.Services.StoreUpdateAvailabilityState.UpdateCheckUnavailable,
                    StatusMessage = "This is an unpackaged or local build. Microsoft Store update checks are not available."
                },
                isMandatory: false);
            Assert.Contains("unpackaged or local build", ReadTextBlock(localWindow, "MessageTextBlock"), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Build state", ReadTextBlock(localWindow, "UpdatesHeadingTextBlock"));
            Assert.Contains("not running as a Store-managed package", string.Join(" ", ReadItems(localWindow, "UpdatesItemsControl")), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("No Store update action was started.", ReadTextBlock(localWindow, "StatusMessageTextBlock"));
            Assert.False(ReadButton(localWindow, "InstallNowButton").IsEnabled);

            var noUpdateWindow = new PS7ScriptDesk.Shell.StoreUpdateWindow(
                service,
                new PS7ScriptDesk.Shell.Services.StoreUpdateCheckResult
                {
                    PackagingKind = PS7ScriptDesk.Shell.Services.StoreUpdatePackagingKind.StoreInstalledManaged,
                    AvailabilityState = PS7ScriptDesk.Shell.Services.StoreUpdateAvailabilityState.NoUpdateAvailable,
                    StoreUpdateCheckAvailable = true,
                    StatusMessage = "No Microsoft Store updates were available."
                },
                isMandatory: false);
            Assert.Contains("No Microsoft Store updates", ReadTextBlock(noUpdateWindow, "MessageTextBlock"), StringComparison.Ordinal);
            Assert.False(ReadButton(noUpdateWindow, "InstallNowButton").IsEnabled);

            var manualInstructionsWindow = new PS7ScriptDesk.Shell.StoreUpdateWindow(
                service,
                new PS7ScriptDesk.Shell.Services.StoreUpdateCheckResult
                {
                    PackagingKind = PS7ScriptDesk.Shell.Services.StoreUpdatePackagingKind.StoreInstalledManaged,
                    AvailabilityState = PS7ScriptDesk.Shell.Services.StoreUpdateAvailabilityState.ManualCheckRequired,
                    ShouldShowManualInstructions = true,
                    StatusMessage = "No per-package Microsoft Store update list was returned for this build."
                },
                isMandatory: false);
            Assert.Contains("could not confirm an installable Microsoft Store update package", ReadTextBlock(manualInstructionsWindow, "MessageTextBlock"), StringComparison.Ordinal);
            Assert.False(ReadButton(manualInstructionsWindow, "InstallNowButton").IsEnabled);

            var mandatoryWindow = new PS7ScriptDesk.Shell.StoreUpdateWindow(
                service,
                new PS7ScriptDesk.Shell.Services.StoreUpdateCheckResult
                {
                    PackagingKind = PS7ScriptDesk.Shell.Services.StoreUpdatePackagingKind.StoreInstalledManaged,
                    AvailabilityState = PS7ScriptDesk.Shell.Services.StoreUpdateAvailabilityState.ConfirmedUpdateAvailable,
                    HasMandatoryUpdate = true,
                    UpdateCount = 1,
                    StatusMessage = "A mandatory Microsoft Store update is required before using PS7 ScriptDesk."
                },
                isMandatory: true);
            Assert.Contains("required", ReadTextBlock(mandatoryWindow, "TitleTextBlock"), StringComparison.OrdinalIgnoreCase);
            Assert.Equal("Exit", ReadButton(mandatoryWindow, "CloseOrExitButton").Content);
        });
    }

    [Fact]
    public void FinalVisualFix_HelpCardsAndRestControlsUseThemeAwareResources()
    {
        var helpCode = ReadRepositoryFile("PS7ScriptDesk.Shell", "Help", "ContextHelpWindow.xaml.cs");
        var appXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "App.xaml");
        var restWizardXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Dialogs", "RestApiPublishWizardWindow.xaml");

        Assert.Contains("Theme.Surface.Secondary", helpCode, StringComparison.Ordinal);
        Assert.Contains("Theme.Border.Subtle", helpCode, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Primary", helpCode, StringComparison.Ordinal);
        Assert.DoesNotContain("MediaColor.FromRgb", helpCode, StringComparison.Ordinal);
        Assert.DoesNotContain("new SolidColorBrush", helpCode, StringComparison.Ordinal);

        Assert.Contains("<ControlTemplate TargetType=\"{x:Type ComboBox}\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"ComboBoxItem\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("<ControlTemplate TargetType=\"{x:Type CheckBox}\">", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Input.Background", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Surface.Secondary", appXaml, StringComparison.Ordinal);
        Assert.Contains("Theme.Text.Secondary", appXaml, StringComparison.Ordinal);

        Assert.Contains("BasedOn=\"{StaticResource {x:Type ComboBoxItem}}\"", restWizardXaml, StringComparison.Ordinal);
    }

    [Fact]
    public void FinalConsistencyCleanup_WizardNavigationAndPrioritySurfacesAvoidLegacyChrome()
    {
        var mainXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "MainWindow.xaml");
        var exportWizardXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardWindow.xaml");
        var exportWizardStylesXaml = ReadRepositoryFile("PS7ScriptDesk.Shell", "Dialogs", "ExportWizardStyles.xaml");

        Assert.Contains("Content=\"Preset\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Application\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Platform\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Dependencies\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Advanced\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"Review\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"1  Preset\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"1  Preset\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Header=\"6  Review\"", exportWizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Ellipse", exportWizardXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StepBadge", exportWizardXaml, StringComparison.Ordinal);

        Assert.Contains("<Setter Property=\"Padding\" Value=\"12,5\" />", exportWizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource IdeDialogResultPanelStyle}\"", exportWizardStylesXaml, StringComparison.Ordinal);
        Assert.Contains("Value=\"{StaticResource Radius.Small}\"", exportWizardStylesXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CornerRadius=\"18\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Background=\"#", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Foreground=\"#", mainXaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), Path.Combine(pathParts)));

    private static string ReadTextBlock(System.Windows.FrameworkElement root, string name)
        => ((System.Windows.Controls.TextBlock)root.FindName(name)).Text;

    private static System.Windows.Controls.Button ReadButton(System.Windows.FrameworkElement root, string name)
        => (System.Windows.Controls.Button)root.FindName(name);

    private static IEnumerable<object> ReadItems(System.Windows.FrameworkElement root, string name)
        => ((System.Windows.Controls.ItemsControl)root.FindName(name)).Items.Cast<object>();

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new System.Threading.Thread(() =>
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

        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

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
