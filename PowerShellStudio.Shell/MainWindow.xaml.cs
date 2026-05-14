using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Threading;
using WpfToolTip = System.Windows.Controls.ToolTip;
using WpfPoint = System.Windows.Point;
using WpfMenuItem = System.Windows.Controls.MenuItem;
using WpfColor = System.Windows.Media.Color;
using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfButton = System.Windows.Controls.Button;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfColors = System.Windows.Media.Colors;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.Shell.Debug;
using PowerShellStudio.Shell.Editor;
using PowerShellStudio.Shell.Help;
using PowerShellStudio.Shell.Services;
using PowerShellStudio.Shell.Themes;
using PowerShellStudio.UI.ViewModels;

namespace PowerShellStudio.Shell
{
    public partial class MainWindow : Window
    {
        private const double MinimumExplorerWidth = 220;
        private const double MinimumConsoleHeight = 160;
        private const double MinimumExplorerSectionHeight = 120;
        private const int SyntaxDiagnosticsDebounceMilliseconds = 175;
        private const int EditorFoldingDebounceMilliseconds = 350;
        private const int EditorHoverDelayMilliseconds = 450;
        private const int EditorMetadataWarmupDebounceMilliseconds = 150;
        private const int MetadataToastShowDelayMilliseconds = 650;
        private const int MetadataToastSuccessDismissMilliseconds = 2200;
        private const int MetadataToastWarningDismissMilliseconds = 6500;
        private const int MetadataToastFailureDismissMilliseconds = 9000;
        private const int DebugOutputPreservationWindowMilliseconds = 2000;
        private const int DebugVariableValueMaxLength = 160;
        private const int DebugHoverValueMaxLength = 300;
        private const string RecentScriptMenuItemTagPrefix = "RecentScript:";
        private const double DefaultDebugPaneWindowWidth = 420;
        private const double DefaultDebugPaneWindowHeight = 480;
        private static readonly HashSet<string> HiddenDebugVariableNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "?",
            "_",
            "args",
            "ConfirmPreference",
            "DebugPreference",
            "EnabledExperimentalFeatures",
            "Error",
            "ErrorActionPreference",
            "ExecutionContext",
            "false",
            "HOME",
            "Host",
            "InformationPreference",
            "input",
            "MyInvocation",
            "NestedPromptLevel",
            "null",
            "PID",
            "ProgressPreference",
            "PSBoundParameters",
            "PSCommandPath",
            "PSItem",
            "PSScriptRoot",
            "PSVersionTable",
            "PWD",
            "ShellId",
            "StackTrace",
            "this",
            "true",
            "VerbosePreference",
            "WarningPreference",
            "WhatIfPreference"
        };
        private static readonly HashSet<string> KnownUnsupportedDroppedFileExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".7z",
            ".avi",
            ".bmp",
            ".cab",
            ".cur",
            ".dll",
            ".doc",
            ".docx",
            ".exe",
            ".gif",
            ".gz",
            ".ico",
            ".iso",
            ".jar",
            ".jpeg",
            ".jpg",
            ".lnk",
            ".mov",
            ".mp3",
            ".mp4",
            ".msi",
            ".pdf",
            ".png",
            ".ppt",
            ".pptx",
            ".rar",
            ".wav",
            ".xls",
            ".xlsx",
            ".zip"
        };


        private readonly UserPromptService _userPromptService = new();
        private readonly HashSet<TextEditor> _configuredEditors = new();
        private readonly Dictionary<EditorTabViewModel, TextEditor> _editorByTab = new();
        // _pendingScrollToEnd removed: no longer needed (xterm.js handles scroll).
        private readonly Dictionary<TextEditor, EditorTabViewModel> _tabByEditor = new();
        private readonly Dictionary<TextEditor, BreakpointLineBackgroundRenderer> _breakpointRenderers = new();
        private readonly Dictionary<TextEditor, BreakpointGlyphMargin> _breakpointGlyphMargins = new();
        private readonly Dictionary<TextEditor, ErrorMarkerRenderer> _errorRenderers = new();
        private readonly Dictionary<TextEditor, DiagnosticGlyphMargin> _diagnosticGlyphMargins = new();
        private readonly Dictionary<TextEditor, PowerShellSyntaxColorizer> _syntaxColorizers = new();
        private readonly Dictionary<TextEditor, CancellationTokenSource> _diagnosticsCancellationSources = new();
        private readonly Dictionary<TextEditor, int> _diagnosticsRequestVersions = new();
        private readonly Dictionary<TextEditor, int> _editorRegistrationVersions = new();
        private readonly Dictionary<TextEditor, FoldingManager> _foldingManagers = new();
        private readonly Dictionary<TextEditor, CancellationTokenSource> _foldingCancellationSources = new();
        private readonly Dictionary<string, DebugVariableInfo> _liveDebugVariableCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly BraceFoldingStrategy _foldingStrategy = new();
        private readonly HashSet<TextEditor> _editorTextSynchronizationInProgress = new();
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly ApplicationSettings _loadedSettings;
        private readonly PowerShellIntelliSenseService _intelliSenseService = new();
        private readonly PowerShellDiagnosticsService _diagnosticsService = new();
        private readonly DispatcherTimer _editorHoverTimer;
        private readonly DispatcherTimer _editorMetadataWarmupTimer;
        private readonly DispatcherTimer _metadataToastShowDelayTimer;
        private readonly DispatcherTimer _metadataToastAutoHideTimer;

        private CompletionWindow? _activeCompletionWindow;
        private CancellationTokenSource? _activeCompletionCts;
        private CancellationTokenSource? _quickInfoCts;
        private WpfToolTip? _activeEditorToolTip;
        private TextView? _pendingHoverTextView;
        private WpfPoint _pendingHoverPoint;
        private FindReplaceWindow? _findReplaceWindow;
        private bool _allowWindowClose;
        private bool _shellLayoutApplied;
        private double _lastKnownExplorerWidth = 300;
        private string _lastFindText = string.Empty;
        private string _lastReplaceText = string.Empty;
        private bool _lastFindMatchCase;
        private bool _lastFindWholeWord;
        private bool _lastFindUseRegex;
        private readonly ThemeService _themeService = new();
        private IDebugSession? _debugSession;
        private Action<DebugSessionState>? _debugSessionStateChangedHandler;
        private EditorTabViewModel? _activeDebugTab;
        private string? _activeDebugLaunchPath;
        private string? _activeDebugSnapshotPath;
        private int _debugPanelRefreshVersion;
        private DebugPaneWindow? _debugPaneWindow;
        private IReadOnlyList<DebugVariableInfo>? _currentDebugVariables;
        private IReadOnlyList<DebugCallStackFrame>? _currentDebugCallStack;
        private ObservableCollection<BreakpointRow>? _currentBreakpointRows;
        private int _selectedDebugTabIndex;
        private bool _isSynchronizingDebugTabSelection;
        private Rect? _lastDebugPaneWindowBounds;
        private DateTimeOffset _lastDebugOutputWrittenAtUtc = DateTimeOffset.MinValue;
        private readonly Dictionary<TextEditor, BraceMatchingRenderer> _braceMatchingRenderers = new();
        private bool _terminalIsReady;
        private bool _terminalIsActive;
        private EditorMetadataWarmupPhase _lastEditorMetadataWarmupPhase = EditorMetadataWarmupPhase.Idle;
        private PowerShellRuntimeInfo? _pendingEditorMetadataWarmupRuntime;
        private string? _pendingEditorMetadataWarmupIdentity;
        private string? _lastScheduledEditorMetadataWarmupIdentity;
        private DateTimeOffset _lastScheduledEditorMetadataWarmupAtUtc = DateTimeOffset.MinValue;
        private EditorMetadataWarmupStatus? _pendingMetadataToastStatus;
        private EditorMetadataWarmupStatus? _visibleMetadataToastStatus;
        private bool _metadataToastVisible;

        private enum DebugTeardownReason
        {
            StartFailure,
            PreparationFailure,
            PreLaunchCleanup,
            UserStop,
            SessionEndedEvent,
            SessionStoppedState
        }

        public static readonly DependencyProperty IsContextHelpEnabledProperty = DependencyProperty.Register(
            nameof(IsContextHelpEnabled),
            typeof(bool),
            typeof(MainWindow),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsContextHelpEnabledChanged));

        public bool IsContextHelpEnabled
        {
            get => (bool)GetValue(IsContextHelpEnabledProperty);
            set => SetValue(IsContextHelpEnabledProperty, value);
        }

        private static void OnIsContextHelpEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not MainWindow window)
            {
                return;
            }

            var isEnabled = e.NewValue is bool value && value;
            ContextHelp.SetEnabled(isEnabled);

            if (window.ViewModel is not null)
            {
                window.ViewModel.StatusText = isEnabled ? "Context help enabled" : "Context help disabled";
            }
        }

        public MainWindow(IApplicationSettingsService applicationSettingsService, ApplicationSettings loadedSettings)
        {
            DeveloperDiagnostics.LogMethodEntry("UI", "MainWindow constructor entry.");
            _applicationSettingsService = applicationSettingsService;
            _loadedSettings = loadedSettings ?? new ApplicationSettings();

            if (IsUsableLength(_loadedSettings.ExplorerWidth, MinimumExplorerWidth))
            {
                _lastKnownExplorerWidth = _loadedSettings.ExplorerWidth!.Value;
            }

            InitializeComponent();

            _intelliSenseService.MetadataWarmupStatusChanged += IntelliSenseService_MetadataWarmupStatusChanged;

            _editorHoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(EditorHoverDelayMilliseconds)
            };
            _editorHoverTimer.Tick += EditorHoverTimer_Tick;
            _editorMetadataWarmupTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(EditorMetadataWarmupDebounceMilliseconds)
            };
            _editorMetadataWarmupTimer.Tick += EditorMetadataWarmupTimer_Tick;
            _metadataToastShowDelayTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(MetadataToastShowDelayMilliseconds)
            };
            _metadataToastShowDelayTimer.Tick += MetadataToastShowDelayTimer_Tick;
            _metadataToastAutoHideTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher);
            _metadataToastAutoHideTimer.Tick += MetadataToastAutoHideTimer_Tick;

            IsContextHelpEnabled = _loadedSettings.IsContextHelpEnabled;
            DeveloperDiagnostics.RegisterSummaryProvider(BuildDeveloperDiagnosticsSnapshot);
            DeveloperDiagnostics.RegisterUiThreadChecker(() => Dispatcher?.CheckAccess());
            UpdateDeveloperDiagnosticsMenuState();
            DeveloperDiagnostics.LogMethodExit(
                "UI",
                "MainWindow constructor exit.",
                new Dictionary<string, object?>
                {
                    ["developerDiagnosticsEnabled"] = _loadedSettings.IsDeveloperDiagnosticsEnabled,
                    ["settingsPath"] = _applicationSettingsService.SettingsFilePath
                });
        }

        private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            using var startupScope = DeveloperDiagnostics.BeginTimedOperation(
                "Startup",
                "WindowLoaded",
                "MainWindow.Window_Loaded executing.",
                operationId: $"WindowLoaded-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogEventHandlerEntry(
                "UI",
                "Window_Loaded",
                "Window_Loaded entered.",
                new Dictionary<string, object?> { ["windowTitle"] = Title });
            StartupTimingLogger.StartSession("MainWindow.Window_Loaded");
            var startupStopwatch = Stopwatch.StartNew();

            try
            {
                ApplyShellLayoutFromSettings();
                // Apply saved theme (5B) and zoom (2B) before anything is shown.
                _themeService.ApplyTheme(ViewModel?.CurrentThemeName ?? "Dark");
                ApplyEditorHighlightSettingsToAllEditors();
                DeveloperDiagnostics.LogInfo("Startup", "Shell layout, theme, and editor highlight settings applied.");
                StartupTimingLogger.Log("MainWindow", $"Shell layout applied in {startupStopwatch.ElapsedMilliseconds} ms");

                if (ViewModel is null)
                {
                    StartupTimingLogger.Log("MainWindow", "No view model was available during startup.");
                    return;
                }

                ViewModel.BindToCurrentSynchronizationContext();
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                ViewModel.PropertyChanged += ViewModel_PropertyChanged;
                DeveloperDiagnostics.LogInfo("Startup", "ViewModel bound to synchronization context and PropertyChanged handler attached.");
                ContextHelp.ValidateWindowTopics(this);
                ApplyExplorerVisibilityLayout();
                RefreshDebugCommandAvailability(false);
                StartEditorMetadataWarmup();
                UpdateRefreshEditorMetadataCommandAvailability();
                StartupTimingLogger.Log("MainWindow", $"View model hookup completed in {startupStopwatch.ElapsedMilliseconds} ms");

                // ── Wire up xterm.js terminal control ────────────────────────────
                // Register sinks so ViewModel output routes to xterm.js.
                ViewModel.SetTerminalSinks(
                    writeText:     text => Dispatcher.BeginInvoke(() => TerminalConsole.WriteText(text)),
                    clearTerminal: ()   => Dispatcher.BeginInvoke(() =>
                    {
                        TerminalConsole.Clear();
                        TerminalConsole.FocusTerminal();
                    }),
                    focusTerminal: ()   => Dispatcher.BeginInvoke(() => TerminalConsole.FocusTerminal()));

                // Forward raw (ANSI-intact) ConPTY output to xterm.js.
                ViewModel.SubscribeRawOutput(
                    raw => Dispatcher.BeginInvoke(() => TerminalConsole.WriteRaw(raw)));

                // Forward xterm.js keystrokes to ConPTY stdin.
                TerminalConsole.UserInput += async data =>
                {
                    AppLogger.Debug("Terminal", $"MainWindow received terminal input for forwarding. Length={data.Length}, Data='{FormatTerminalTextForLog(data)}'.");
                    DeveloperDiagnostics.LogUserAction(
                        "Terminal",
                        "TerminalInput",
                        "Terminal input received for forwarding to the view model.",
                        new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(data))
                        {
                            ["focusedElement"] = DescribeFocusedElement()
                        });
                    if (ViewModel is not null)
                    {
                        await ViewModel.WriteRawInputAsync(data).ConfigureAwait(false);
                        AppLogger.Debug("Terminal", "MainWindow forwarded terminal input to the view model.");
                        DeveloperDiagnostics.LogInfo("Terminal", "Terminal input forwarded to view model.");
                    }
                };

                TerminalConsole.TerminalActivated += source => OnTerminalActivated(source);

                // Resize ConPTY when xterm.js reports a new grid size.
                TerminalConsole.TerminalResized += (cols, rows) =>
                {
                    ViewModel?.ResizeConsole(cols, rows);
                };

                // When xterm.js signals ready, start the ConPTY session so the
                // terminal is live as soon as the user can see it.
                TerminalConsole.TerminalReady += async () =>
                {
                    _terminalIsReady = true;
                    AppLogger.Debug("Terminal", "MainWindow received terminal-ready signal.");
                    DeveloperDiagnostics.LogStateTransition("Terminal", "TerminalReady", "Initializing", "Ready", "Terminal ready signal received.");
                    // Apply the current app theme to the terminal colour scheme.
                    TerminalConsole.ApplyAppTheme(_themeService.CurrentTheme);
                    if (ViewModel is not null)
                        await ViewModel.EnsureConsoleRestoredAsync().ConfigureAwait(false);
                };

                // When the app theme changes, update the terminal colour scheme to match.
                _themeService.ThemeChanged += themeName =>
                    Dispatcher.BeginInvoke(() => TerminalConsole.ApplyAppTheme(themeName));

                // Notify the service that a host is attached (triggers session bookkeeping).
                var hostAttachStopwatch = Stopwatch.StartNew();
                await ViewModel.InitializeTerminalHostAsync(IntPtr.Zero, 120, 30);
                DeveloperDiagnostics.LogOperationStop(
                    "Startup",
                    "InitializeTerminalHost",
                    "Terminal host initialization completed.",
                    hostAttachStopwatch.ElapsedMilliseconds);
                StartupTimingLogger.Log("MainWindow", $"Terminal host attached in {hostAttachStopwatch.ElapsedMilliseconds} ms");

                _ = ViewModel.InitializeAsync();
                DeveloperDiagnostics.LogAsyncBoundary("Startup", "InitializeAsync", "Deferred ViewModel initialization launched.", "AsyncStart");
                StartupTimingLogger.Log("MainWindow", $"Deferred initialization launched at {startupStopwatch.ElapsedMilliseconds} ms");

                TerminalConsole.FocusTerminal();
                StartupTimingLogger.Log("MainWindow", $"Window_Loaded completed in {startupStopwatch.ElapsedMilliseconds} ms");
                DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_Loaded", "Window_Loaded completed successfully.");
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("MainWindow", $"Startup exception: {ex}");
                DeveloperDiagnostics.LogException("Startup", ex, "MainWindow.Window_Loaded failed.");
                System.Windows.MessageBox.Show(
                    this,
                    $"PS7 ScriptDesk failed during startup.\n\n{ex}",
                    "Startup Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void Window_ContentRendered(object? sender, EventArgs e)
        {
            DeveloperDiagnostics.LogEventHandlerEntry("UI", "Window_ContentRendered", "Window content rendered.");
            DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_ContentRendered", "Window content rendered handler completed.");
        }


        private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseUiEnabled())
            {
                DeveloperDiagnostics.LogUserAction(
                    "UI",
                    "KeyboardShortcut",
                    $"PreviewKeyDown received: {e.Key}.",
                    new Dictionary<string, object?>
                    {
                        ["key"] = e.Key.ToString(),
                        ["modifiers"] = Keyboard.Modifiers.ToString(),
                        ["focusedElement"] = DescribeFocusedElement(),
                        ["activeDocumentPath"] = ViewModel.SelectedTab?.FilePath,
                        ["activeDocumentDirtyState"] = ViewModel.SelectedTab?.IsDirty
                    });
            }

            var isCtrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            var isShift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;

            if (!isCtrl && !isShift && e.Key == Key.F1)
            {
                e.Handled = true;

                var activeEditor = FindActiveEditor();
                if (activeEditor is not null && activeEditor.IsKeyboardFocusWithin)
                {
                    var quickInfoShown = await ShowEditorQuickInfoAtCaretAsync(activeEditor, updateStatusOnly: false).ConfigureAwait(true);
                    if (!quickInfoShown)
                    {
                        ContextHelp.OpenTopic(this, "Editor.Area");
                    }
                }
                else
                {
                    ContextHelp.OpenForFocusedElement(this);
                }

                return;
            }

            if (isCtrl && !isShift && e.Key == Key.N)
            {
                e.Handled = true;
                ViewModel.NewScriptCommand.Execute(null);
                FocusActiveEditorSoon();
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.O)
            {
                e.Handled = true;
                OpenFile_Click(sender, new RoutedEventArgs());
                return;
            }

            if (isCtrl && isShift && e.Key == Key.O)
            {
                e.Handled = true;
                await OpenFolderFromShortcutAsync().ConfigureAwait(true);
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.S)
            {
                e.Handled = true;
                SaveFile_Click(sender, new RoutedEventArgs());
                return;
            }

            if (isCtrl && isShift && e.Key == Key.S)
            {
                e.Handled = true;
                SaveFileAs_Click(sender, new RoutedEventArgs());
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.W)
            {
                e.Handled = true;
                ViewModel.CloseTabCommand.Execute(ViewModel.SelectedTab);
                FocusActiveEditorSoon();
                return;
            }

            if (isCtrl && isShift && e.Key == Key.W)
            {
                e.Handled = true;
                ViewModel.CloseAllTabsCommand.Execute(null);
                FocusActiveEditorSoon();
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.Tab)
            {
                e.Handled = true;
                SelectAdjacentTab(+1);
                return;
            }

            if (isCtrl && isShift && e.Key == Key.Tab)
            {
                e.Handled = true;
                SelectAdjacentTab(-1);
                return;
            }

            if (!isCtrl && !isShift && e.Key == Key.F5)
            {
                e.Handled = true;

                if (_debugSession?.CurrentState == DebugSessionState.Paused)
                {
                    ContinueDebug_Click(sender, new RoutedEventArgs());
                    return;
                }

                StartDebug_Click(sender, new RoutedEventArgs());
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.F5)
            {
                e.Handled = true;
                await RunScriptWithBreakpointAwarenessAsync().ConfigureAwait(true);
                return;
            }

            if (!isCtrl && !isShift && e.Key == Key.F8)
            {
                if (FindActiveEditor() is TextEditor editorTextEditor &&
                    editorTextEditor.SelectionLength > 0 &&
                    !string.IsNullOrWhiteSpace(editorTextEditor.SelectedText))
                {
                    e.Handled = true;
                    await RunSelectionFromEditorAsync(editorTextEditor).ConfigureAwait(true);
                    return;
                }
            }

            if (!isCtrl && isShift && e.Key == Key.F5)
            {
                e.Handled = true;
                StopDebug_Click(sender, new RoutedEventArgs());
                return;
            }

            if (!isCtrl && !isShift && e.Key == Key.F10)
            {
                e.Handled = true;
                StepOver_Click(sender, new RoutedEventArgs());
                return;
            }

            if (!isCtrl && !isShift && e.Key == Key.F11)
            {
                e.Handled = true;
                StepInto_Click(sender, new RoutedEventArgs());
                return;
            }

            if (!isCtrl && isShift && e.Key == Key.F11)
            {
                e.Handled = true;
                StepOut_Click(sender, new RoutedEventArgs());
                return;
            }

            // Ctrl+G — Go to Line (2A)
            if (isCtrl && !isShift && e.Key == Key.G)
            {
                e.Handled = true;
                OpenGoToLineDialog();
                return;
            }

            // Ctrl+= or Ctrl+Plus — Zoom In (2B)
            if (isCtrl && !isShift && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                e.Handled = true;
                ViewModel.ZoomInCommand.Execute(null);
                return;
            }

            // Ctrl+- or Ctrl+Minus — Zoom Out (2B)
            if (isCtrl && !isShift && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                e.Handled = true;
                ViewModel.ZoomOutCommand.Execute(null);
                return;
            }

            // Ctrl+0 — Reset Zoom (2B)
            if (isCtrl && !isShift && e.Key == Key.D0)
            {
                e.Handled = true;
                ViewModel.ResetZoomCommand.Execute(null);
                return;
            }

            // F3 / Shift+F3 — Find Next / Find Prev (global shortcut)
            if (!isCtrl && e.Key == Key.F3)
            {
                e.Handled = true;
                if (isShift)
                    ExecuteFindPrev(_lastFindText, _lastFindMatchCase, _lastFindWholeWord, _lastFindUseRegex);
                else
                    ExecuteFindNext(_lastFindText, _lastFindMatchCase, _lastFindWholeWord, _lastFindUseRegex);
                return;
            }
        }

        private async System.Threading.Tasks.Task OpenFolderFromShortcutAsync()
        {
            if (ViewModel is null)
            {
                return;
            }

            DeveloperDiagnostics.LogUserAction("UI", "OpenFolderShortcut", "Open folder shortcut invoked.");
            var folderPath = _userPromptService.ShowOpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await ViewModel.LoadWorkspaceFolderAsync(folderPath).ConfigureAwait(true);
                DeveloperDiagnostics.LogInfo("UI", "Workspace folder loaded from shortcut.", new Dictionary<string, object?> { ["folderPath"] = folderPath });
            }
        }

        private void SelectAdjacentTab(int direction)
        {
            if (ViewModel is null || ViewModel.OpenTabs.Count == 0)
            {
                return;
            }

            var currentIndex = ViewModel.SelectedTab is null
                ? 0
                : ViewModel.OpenTabs.IndexOf(ViewModel.SelectedTab);

            if (currentIndex < 0)
            {
                currentIndex = 0;
            }

            var nextIndex = currentIndex + direction;
            if (nextIndex < 0)
            {
                nextIndex = ViewModel.OpenTabs.Count - 1;
            }
            else if (nextIndex >= ViewModel.OpenTabs.Count)
            {
                nextIndex = 0;
            }

            ViewModel.SelectedTab = ViewModel.OpenTabs[nextIndex];
            FocusActiveEditorSoon();
        }

        private void FocusActiveEditorSoon()
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var editorTextEditor = FindActiveEditor();
                if (editorTextEditor is null)
                {
                    return;
                }

                SetTerminalActive(false, "FocusActiveEditorSoon");
                editorTextEditor.Focus();
                editorTextEditor.TextArea?.Caret.BringCaretToView();
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        private void OpenFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            DeveloperDiagnostics.LogEventHandlerEntry("UI", "OpenFile_Click", "OpenFile menu/toolbar handler entered.");
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Open Script File",
                Filter = "PowerShell Files (*.ps1)|*.ps1|Text Files (*.txt)|*.txt|All Files (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                ViewModel.OpenFileFromPath(dialog.FileName);
                DeveloperDiagnostics.LogUserAction("Editor", "DocumentOpenRequested", "Open file dialog selected a document.", new Dictionary<string, object?> { ["filePath"] = dialog.FileName });
                FocusActiveEditorSoon();
            }

            DeveloperDiagnostics.LogEventHandlerExit("UI", "OpenFile_Click", "OpenFile handler exited.");
        }

        private void FileMenuItem_SubmenuOpened(object sender, RoutedEventArgs e)
        {
            PopulateRecentScriptsSection();
        }

        private void PopulateRecentScriptsSection()
        {
            if (FileMenuItem is null ||
                RecentScriptsSectionHeaderMenuItem is null ||
                RecentScriptsEmptyMenuItem is null ||
                RecentScriptsSectionTopSeparator is null ||
                RecentScriptsSectionBottomSeparator is null)
            {
                return;
            }

            for (var index = FileMenuItem.Items.Count - 1; index >= 0; index--)
            {
                if (FileMenuItem.Items[index] is WpfMenuItem existingMenuItem &&
                    existingMenuItem.Tag is string tag &&
                    tag.StartsWith(RecentScriptMenuItemTagPrefix, StringComparison.Ordinal))
                {
                    existingMenuItem.Click -= RecentScriptMenuItem_Click;
                    FileMenuItem.Items.RemoveAt(index);
                }
            }

            var recentPaths = ViewModel?.GetRecentFilePathsSnapshot() ?? Array.Empty<string>();
            var hasRecentPaths = recentPaths.Count > 0;
            RecentScriptsSectionTopSeparator.Visibility = Visibility.Visible;
            RecentScriptsSectionHeaderMenuItem.Visibility = Visibility.Visible;
            RecentScriptsSectionBottomSeparator.Visibility = Visibility.Visible;
            RecentScriptsEmptyMenuItem.Visibility = hasRecentPaths ? Visibility.Collapsed : Visibility.Visible;

            if (!hasRecentPaths)
            {
                return;
            }

            var insertIndex = FileMenuItem.Items.IndexOf(RecentScriptsEmptyMenuItem);
            if (insertIndex < 0)
            {
                return;
            }

            for (var index = 0; index < recentPaths.Count; index++)
            {
                var recentPath = recentPaths[index];
                var menuItem = new WpfMenuItem
                {
                    Header = $"{index + 1}  {EscapeMenuItemHeader(recentPath)}",
                    ToolTip = recentPath,
                    Tag = $"{RecentScriptMenuItemTagPrefix}{recentPath}"
                };
                menuItem.Click += RecentScriptMenuItem_Click;
                FileMenuItem.Items.Insert(insertIndex + index, menuItem);
            }
        }

        private void RecentScriptMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null ||
                sender is not WpfMenuItem menuItem ||
                menuItem.Tag is not string taggedPath ||
                !taggedPath.StartsWith(RecentScriptMenuItemTagPrefix, StringComparison.Ordinal))
            {
                return;
            }

            var recentPath = taggedPath.Substring(RecentScriptMenuItemTagPrefix.Length);

            DeveloperDiagnostics.LogUserAction(
                "Editor",
                "RecentScriptOpenRequested",
                "Recent script menu item selected.",
                new Dictionary<string, object?>
                {
                    ["filePath"] = recentPath
                });

            if (ViewModel.TryOpenFileFromPath(recentPath, out var failureReason))
            {
                FocusActiveEditorSoon();
                return;
            }

            var message = string.IsNullOrWhiteSpace(failureReason)
                ? "The recent script could not be opened."
                : failureReason!;
            var removedMissingPath = false;

            if (!File.Exists(recentPath))
            {
                removedMissingPath = ViewModel.RemoveRecentFilePath(recentPath);
                PopulateRecentScriptsSection();
            }

            if (removedMissingPath)
            {
                AppLogger.Warning("RecentScripts", $"Removed unavailable recent script '{recentPath}'. Reason={message}");
                DeveloperDiagnostics.LogDecision(
                    "Editor",
                    "RecentScriptRemoved",
                    "Recent script path was removed after open failed.",
                    "RemoveMissingRecentScript",
                    new Dictionary<string, object?>
                    {
                        ["filePath"] = recentPath,
                        ["failureReason"] = message
                    });
            }

            ViewModel.StatusText = $"Recent script open failed: {message}";
            System.Windows.MessageBox.Show(
                this,
                $"{message}{Environment.NewLine}{Environment.NewLine}{recentPath}",
                "Recent Script",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static string EscapeMenuItemHeader(string text)
        {
            return string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("_", "__", StringComparison.Ordinal);
        }

        private async void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            DeveloperDiagnostics.LogEventHandlerEntry("UI", "OpenFolder_Click", "OpenFolder menu/toolbar handler entered.");
            var folderPath = _userPromptService.ShowOpenFolderDialog();
            if (!string.IsNullOrWhiteSpace(folderPath))
            {
                await ViewModel.LoadWorkspaceFolderAsync(folderPath);
                DeveloperDiagnostics.LogUserAction("UI", "WorkspaceOpenRequested", "Workspace folder selected from dialog.", new Dictionary<string, object?> { ["folderPath"] = folderPath });
            }

            DeveloperDiagnostics.LogEventHandlerExit("UI", "OpenFolder_Click", "OpenFolder handler exited.");
        }

        private void SaveFile_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null || ViewModel.SelectedTab is null)
            {
                return;
            }

            DeveloperDiagnostics.LogUserAction(
                "Editor",
                "DocumentSaveRequested",
                "Save file requested.",
                new Dictionary<string, object?>
                {
                    ["filePath"] = ViewModel.SelectedTab.FilePath,
                    ["isDirty"] = ViewModel.SelectedTab.IsDirty
                });
            if (string.IsNullOrWhiteSpace(ViewModel.SelectedTab.FilePath))
            {
                SaveFileAs_Click(sender, e);
                return;
            }

            ViewModel.SaveSelectedTab();
        }

        private void SaveFileAs_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null || ViewModel.SelectedTab is null)
            {
                return;
            }

            DeveloperDiagnostics.LogUserAction("Editor", "DocumentSaveAsRequested", "Save As requested.");
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Save Script File",
                Filter = "PowerShell Files (*.ps1)|*.ps1|All Files (*.*)|*.*",
                DefaultExt = ".ps1",
                AddExtension = true,
                OverwritePrompt = true,
                CheckFileExists = false,
                CheckPathExists = true,
                CreatePrompt = false,
                CreateTestFile = false,
                ValidateNames = true,
                FileName = ViewModel.GetSuggestedSaveFileName()
            };

            if (dialog.ShowDialog() == true)
            {
                ViewModel.SaveSelectedTabAs(dialog.FileName);
                DeveloperDiagnostics.LogInfo("Editor", "Save As target selected.", new Dictionary<string, object?> { ["filePath"] = dialog.FileName });
                FocusActiveEditorSoon();
            }
        }

        private void WorkspaceTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (ViewModel is null)
            {
                return;
            }

            if (e.NewValue is WorkspaceTreeItemViewModel item && !item.IsPlaceholder)
            {
                ViewModel.SelectedWorkspaceItem = item;
            }
            else
            {
                ViewModel.SelectedWorkspaceItem = null;
            }
        }

        private void WorkspaceTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is not TreeViewItem treeViewItem)
            {
                return;
            }

            if (treeViewItem.DataContext is WorkspaceTreeItemViewModel item && !item.IsPlaceholder)
            {
                ViewModel.SelectedWorkspaceItem = item;
                ViewModel.OpenSelectedWorkspaceItem();
            }
        }

        private void EditorTextEditor_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            ConfigureEditorTextEditor(editorTextEditor);
            UpdateEditorCaretMetrics(editorTextEditor);
        }

        private void EditorTextEditor_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            UnregisterEditor(editorTextEditor);
        }

        private void EditorTextEditor_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            UnregisterEditor(editorTextEditor);
            RegisterEditor(editorTextEditor);
            UpdateEditorCaretMetrics(editorTextEditor);
        }

        private void EditorTextEditor_TextChanged(object? sender, EventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            if (!_editorTextSynchronizationInProgress.Contains(editorTextEditor) &&
                editorTextEditor.DataContext is EditorTabViewModel tab)
            {
                var editorText = editorTextEditor.Text ?? string.Empty;
                var lineCount = editorTextEditor.Document?.LineCount ?? 1;
                tab.UpdateContentFromEditor(editorText, lineCount);
            }

            UpdateEditorCaretMetrics(editorTextEditor);
            ClearParserTokensForEditor(editorTextEditor);

            // Folding is a whole-document operation. Debounce it so regular typing
            // stays smooth and AvalonEdit can repaint only the changed visual lines.
            ScheduleFolding(editorTextEditor);
            ScheduleDiagnostics(editorTextEditor);
        }

        private void EditorTextEditor_CaretPositionChanged(object? sender, EventArgs e)
        {
            var editorTextEditor = ResolveEditorFromEventSender(sender);
            if (editorTextEditor is null)
            {
                return;
            }

            UpdateEditorCaretMetrics(editorTextEditor);
        }

        private void EditorTextEditor_SelectionChanged(object? sender, EventArgs e)
        {
            var editorTextEditor = ResolveEditorFromEventSender(sender);
            if (editorTextEditor is null)
            {
                return;
            }

            UpdateEditorCaretMetrics(editorTextEditor);
        }

        private void EditorTextArea_TextEntered(object? sender, TextCompositionEventArgs e)
        {
            var editorTextEditor = ResolveEditorFromEventSender(sender);
            if (editorTextEditor is null || string.IsNullOrEmpty(e.Text))
            {
                return;
            }

            if (ShouldSuppressEditorInputFeatures(editorTextEditor, "TextEntered"))
            {
                return;
            }

            var ch = e.Text[0];

            // Auto-close matching delimiters only when typing in PowerShell code.
            // Legacy ISE does not feel good when braces are blindly inserted inside
            // comments or strings, so keep this feature contextual.
            if (ShouldAutoInsertClosingDelimiter(editorTextEditor))
            {
                switch (ch)
                {
                    case '{': AutoInsertClosingDelimiter(editorTextEditor, '}'); break;
                    case '(': AutoInsertClosingDelimiter(editorTextEditor, ')'); break;
                    case '[': AutoInsertClosingDelimiter(editorTextEditor, ']'); break;
                }
            }

            if (ch == '(' || ch == ',' || ch == ' ' || ch == '-')
            {
                ObserveFireAndForget(ShowEditorQuickInfoAtCaretAsync(editorTextEditor, updateStatusOnly: true), "editor quick-info update");
            }

            // Trigger IntelliSense. When a completion window is already open, let
            // AvalonEdit's live filtering keep it responsive instead of closing and
            // recreating the popup after every typed character.
            if (ch == '$' || ch == '-' || ch == '.' || ch == ':' || ch == '\\' || ch == '/')
            {
                ShowCompletionAsync(editorTextEditor, autoTriggered: false, includeEngine: ch is '-' or '.' or ':' or '\\' or '/');
            }
            else if (_activeCompletionWindow is null && char.IsLetter(ch))
            {
                var fragment = GetCurrentWordFragment(editorTextEditor);
                var isParameterToken = fragment.StartsWith("-", StringComparison.Ordinal) ||
                    IsCaretInsideParameterToken(editorTextEditor);
                if (isParameterToken)
                {
                    ShowCompletionAsync(editorTextEditor, autoTriggered: true, includeEngine: isParameterToken);
                }
                else if (fragment.Length >= 2)
                {
                    ShowCompletionAsync(editorTextEditor, autoTriggered: true, includeEngine: false);
                }
            }
        }

        private void EditorTextArea_TextEntering(object? sender, TextCompositionEventArgs e)
        {
            var editor = ResolveEditorFromEventSender(sender);
            if (editor is null)
            {
                return;
            }

            if (ShouldSuppressEditorInputFeatures(editor, "TextEntering"))
            {
                return;
            }

            if (!string.IsNullOrEmpty(e.Text))
            {
                var ch = e.Text[0];

                // Skip over an auto-inserted closing delimiter when the caret is already
                // sitting in front of that exact character (prevents double-brace syndrome).
                if (ch == '}' || ch == ')' || ch == ']')
                {
                    if (editor?.Document is not null &&
                        editor.CaretOffset < editor.Document.TextLength &&
                        editor.Document.GetCharAt(editor.CaretOffset) == ch)
                    {
                        editor.CaretOffset++;
                        e.Handled = true;
                        return;
                    }
                }

                // Let the active completion window commit when the user types a non-identifier character.
                if (_activeCompletionWindow is not null &&
                    !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-')
                {
                    _activeCompletionWindow.CompletionList.RequestInsertion(e);
                }
            }
        }

        private async void EditorTextEditor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

             if (_terminalIsActive)
            {
                AppLogger.Debug(
                    "EditorCompletion",
                    $"Editor preview key handler observed input while terminal is active. Key={e.Key}, Modifiers={Keyboard.Modifiers}, EditorFocused={editorTextEditor.IsKeyboardFocusWithin}.");
                return;
            }

            if (_activeCompletionWindow is not null && e.Key == Key.Tab)
            {
                e.Handled = true;
                _activeCompletionWindow.CompletionList.RequestInsertion(e);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Space)
            {
                e.Handled = true;
                ForceCompletionNow(editorTextEditor, "Ctrl+Space");
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.None && e.Key == Key.F1)
            {
                e.Handled = true;
                var quickInfoShown = await ShowEditorQuickInfoAtCaretAsync(editorTextEditor, updateStatusOnly: false).ConfigureAwait(true);
                if (!quickInfoShown)
                {
                    ContextHelp.OpenTopic(this, "Editor.Area");
                }
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.OemQuestion)
            {
                e.Handled = true;
                ToggleCommentForEditor(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Up)
            {
                e.Handled = true;
                MoveLineUp(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Down)
            {
                e.Handled = true;
                MoveLineDown(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
            {
                e.Handled = TryCopySelectedEditorTextToClipboard(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.X)
            {
                e.Handled = TryCutSelectedEditorTextToClipboard(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.V)
            {
                e.Handled = TryPasteClipboardTextIntoEditor(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.A)
            {
                e.Handled = true;
                editorTextEditor.SelectAll();
                UpdateEditorCaretMetrics(editorTextEditor);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
            {
                e.Handled = true;
                OpenFindReplaceWindow(showReplace: false);
                return;
            }

            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.H)
            {
                e.Handled = true;
                OpenFindReplaceWindow(showReplace: true);
                return;
            }

            if (e.Key == Key.F9)
            {
                e.Handled = true;
                ToggleBreakpointForEditor(editorTextEditor);
                return;
            }

            if (e.Key == Key.F8)
            {
                e.Handled = true;
                await RunSelectionFromEditorAsync(editorTextEditor);
            }
        }

        private void Find_Click(object sender, RoutedEventArgs e)
        {
            OpenFindReplaceWindow(showReplace: false);
        }

        private void Replace_Click(object sender, RoutedEventArgs e)
        {
            OpenFindReplaceWindow(showReplace: true);
        }

        private async void RunSelection_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is not TextEditor editorTextEditor)
            {
                return;
            }

            await RunSelectionFromEditorAsync(editorTextEditor);
        }

        private async void RunScript_Click(object sender, RoutedEventArgs e)
        {
            await RunScriptWithBreakpointAwarenessAsync().ConfigureAwait(true);
        }

        private void NewTabPlus_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.NewScriptCommand.CanExecute(null) == true)
            {
                ViewModel.NewScriptCommand.Execute(null);
                FocusActiveEditorSoon();
            }
        }

        private void HelpOverview_Click(object sender, RoutedEventArgs e)
        {
            ContextHelp.OpenOverview(this);
        }

        private void ContextHelp_Click(object sender, RoutedEventArgs e)
        {
            ContextHelp.OpenForFocusedElement(this);
        }

        private void ConsoleBottomPaneTab_Click(object sender, RoutedEventArgs e)
        {
            ConsoleBottomPaneTab.IsChecked = true;
            DiagnosticsBottomPaneTab.IsChecked = false;
            DeveloperDiagnostics.LogUserAction("UI", "BottomPaneConsoleTabSelected", "Console bottom pane tab selected.");
            Dispatcher.BeginInvoke(new Action(() => TerminalConsole.FocusTerminal()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void DiagnosticsBottomPaneTab_Click(object sender, RoutedEventArgs e)
        {
            ConsoleBottomPaneTab.IsChecked = false;
            DiagnosticsBottomPaneTab.IsChecked = true;

            var errorCount = ViewModel?.SelectedTab?.DiagnosticErrorCount ?? 0;
            var warningCount = ViewModel?.SelectedTab?.DiagnosticWarningCount ?? 0;
            DeveloperDiagnostics.LogUserAction(
                "UI",
                "BottomPaneDiagnosticsTabSelected",
                "Diagnostics bottom pane tab selected.",
                new Dictionary<string, object?>
                {
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount
                });
        }


        private void EditorTextEditor_PreviewDragOver(object sender, System.Windows.DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                e.Effects = System.Windows.DragDropEffects.None;
                e.Handled = true;
                return;
            }

            var droppedPaths = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
            e.Effects = CanAcceptAnyDroppedFile(droppedPaths)
                ? System.Windows.DragDropEffects.Copy
                : System.Windows.DragDropEffects.None;
            e.Handled = true;
        }

        private void EditorTextEditor_Drop(object sender, System.Windows.DragEventArgs e)
        {
            if (ViewModel is null || !e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop))
            {
                return;
            }

            if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is not string[] droppedPaths || droppedPaths.Length == 0)
            {
                ViewModel.StatusText = "Drop failed: no files were provided";
                e.Handled = true;
                return;
            }

            var openedFileCount = 0;
            var failedFiles = new List<string>();

            foreach (var droppedPath in droppedPaths)
            {
                var validationFailure = GetDroppedFileValidationFailure(droppedPath);
                if (!string.IsNullOrWhiteSpace(validationFailure))
                {
                    failedFiles.Add($"{GetDisplayNameForDroppedPath(droppedPath)} — {validationFailure}");
                    continue;
                }

                if (ViewModel.TryOpenFileFromPath(droppedPath, out var openFailureReason))
                {
                    openedFileCount++;
                    continue;
                }

                failedFiles.Add($"{GetDisplayNameForDroppedPath(droppedPath)} — {openFailureReason ?? "The file could not be opened."}");
            }

            if (openedFileCount > 0)
            {
                FocusActiveEditorSoon();
            }

            if (failedFiles.Count > 0)
            {
                var summary = openedFileCount == 0
                    ? "No dropped files were opened."
                    : $"Opened {openedFileCount} file(s). Some files could not be opened.";

                var failureDetails = string.Join(Environment.NewLine, failedFiles.Select(static failure => $"• {failure}"));
                System.Windows.MessageBox.Show(
                    this,
                    $"{summary}{Environment.NewLine}{Environment.NewLine}{failureDetails}",
                    "Dropped File Results",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);

                ViewModel.StatusText = openedFileCount == 0
                    ? "Drop failed"
                    : $"Opened {openedFileCount} file(s); {failedFiles.Count} file(s) could not be opened";
            }
            else if (openedFileCount > 0)
            {
                ViewModel.StatusText = openedFileCount == 1
                    ? "Dropped file opened"
                    : $"Opened {openedFileCount} dropped files";
            }

            e.Handled = true;
        }

        private void EditorCut_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editorTextEditor)
            {
                _ = TryCutSelectedEditorTextToClipboard(editorTextEditor);
            }
        }

        private void EditorCopy_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editorTextEditor)
            {
                _ = TryCopySelectedEditorTextToClipboard(editorTextEditor);
            }
        }

        private void EditorPaste_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editorTextEditor)
            {
                _ = TryPasteClipboardTextIntoEditor(editorTextEditor);
            }
        }

        private void EditorSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is not TextEditor editorTextEditor)
            {
                return;
            }

            editorTextEditor.SelectAll();
            UpdateEditorCaretMetrics(editorTextEditor);
        }

        private bool TryCopySelectedEditorTextToClipboard(TextEditor editorTextEditor)
        {
            var selectedText = editorTextEditor.SelectedText;
            if (string.IsNullOrEmpty(selectedText))
            {
                return false;
            }

            try
            {
                System.Windows.Clipboard.SetText(selectedText);
                return true;
            }
            catch (Exception ex)
            {
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Copy failed: {ex.Message}";
                }

                return true;
            }
        }

        private bool TryCutSelectedEditorTextToClipboard(TextEditor editorTextEditor)
        {
            var selectedText = editorTextEditor.SelectedText;
            if (string.IsNullOrEmpty(selectedText))
            {
                return false;
            }

            if (!TryCopySelectedEditorTextToClipboard(editorTextEditor))
            {
                return false;
            }

            ReplaceEditorSelection(editorTextEditor, string.Empty);
            return true;
        }

        private bool TryPasteClipboardTextIntoEditor(TextEditor editorTextEditor)
        {
            string clipboardText;
            try
            {
                if (!System.Windows.Clipboard.ContainsText())
                {
                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = "Paste skipped: clipboard does not contain text";
                    }

                    return true;
                }

                clipboardText = System.Windows.Clipboard.GetText();
            }
            catch (Exception ex)
            {
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Paste failed: {ex.Message}";
                }

                return true;
            }

            ReplaceEditorSelection(editorTextEditor, clipboardText);
            return true;
        }

        private void ReplaceEditorSelection(TextEditor editorTextEditor, string replacementText)
        {
            if (editorTextEditor.Document is null)
            {
                return;
            }

            var selectionStart = editorTextEditor.SelectionStart;
            var selectionLength = editorTextEditor.SelectionLength;
            var replacement = replacementText ?? string.Empty;

            editorTextEditor.Document.Replace(selectionStart, selectionLength, replacement);

            var newCaretOffset = Math.Clamp(selectionStart + replacement.Length, 0, editorTextEditor.Text.Length);
            editorTextEditor.Select(newCaretOffset, 0);
            editorTextEditor.CaretOffset = newCaretOffset;
            UpdateEditorCaretMetrics(editorTextEditor);
            editorTextEditor.Focus();
        }

        private static bool CanAcceptAnyDroppedFile(IEnumerable<string>? droppedPaths)
        {
            if (droppedPaths is null)
            {
                return false;
            }

            foreach (var droppedPath in droppedPaths)
            {
                if (GetDroppedFileValidationFailure(droppedPath) is null)
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetDisplayNameForDroppedPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return "(empty path)";
            }

            try
            {
                var fileName = Path.GetFileName(path);
                return string.IsNullOrWhiteSpace(fileName) ? path : fileName;
            }
            catch
            {
                return path;
            }
        }

        private static string? GetDroppedFileValidationFailure(string? droppedPath)
        {
            if (string.IsNullOrWhiteSpace(droppedPath))
            {
                return "The dropped path is empty or invalid.";
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(droppedPath);
            }
            catch
            {
                return "The dropped path is invalid.";
            }

            if (Directory.Exists(normalizedPath))
            {
                return "Folders cannot be opened by dropping onto the editor. Use Open Folder for workspace folders.";
            }

            if (!File.Exists(normalizedPath))
            {
                return "The file was not found.";
            }

            var extension = Path.GetExtension(normalizedPath);
            if (!string.IsNullOrWhiteSpace(extension) && KnownUnsupportedDroppedFileExtensions.Contains(extension))
            {
                return $"Unsupported file type '{extension}'. Drop a text-based script or source file instead.";
            }

            return LooksLikeTextFile(normalizedPath, out var readabilityFailureReason)
                ? null
                : readabilityFailureReason;
        }

        private static bool LooksLikeTextFile(string filePath, out string failureReason)
        {
            failureReason = "The file could not be read.";

            try
            {
                using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var bytesToInspect = (int)Math.Min(stream.Length, 4096);
                if (bytesToInspect <= 0)
                {
                    failureReason = string.Empty;
                    return true;
                }

                var buffer = new byte[bytesToInspect];
                var bytesRead = stream.Read(buffer, 0, buffer.Length);
                if (bytesRead <= 0)
                {
                    failureReason = string.Empty;
                    return true;
                }

                if (buffer.Take(bytesRead).Any(static value => value == 0))
                {
                    failureReason = "The file appears to contain binary or unreadable content.";
                    return false;
                }

                var suspiciousControlCharacterCount = 0;
                for (var index = 0; index < bytesRead; index++)
                {
                    var value = buffer[index];
                    var isAllowedControlCharacter = value is 9 or 10 or 12 or 13;
                    if (value < 32 && !isAllowedControlCharacter)
                    {
                        suspiciousControlCharacterCount++;
                    }
                }

                if (suspiciousControlCharacterCount > Math.Max(1, bytesRead / 20))
                {
                    failureReason = "The file appears to contain binary or unreadable content.";
                    return false;
                }

                failureReason = string.Empty;
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                failureReason = "The file is inaccessible or you do not have permission to read it.";
                return false;
            }
            catch (IOException)
            {
                failureReason = "The file is locked or otherwise inaccessible.";
                return false;
            }
        }

        private async void WorkspaceTreeItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not TreeViewItem treeViewItem ||
                treeViewItem.DataContext is not WorkspaceTreeItemViewModel item ||
                ViewModel is null)
            {
                return;
            }

            await ViewModel.LoadWorkspaceChildrenAsync(item).ConfigureAwait(true);
        }

        private void ToggleBreakpoint_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is not TextEditor editorTextEditor)
            {
                return;
            }

            ToggleBreakpointForEditor(editorTextEditor);
        }

        public void ExecuteFindNext(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (FindActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to find");
                return;
            }

            if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: true))
                _findReplaceWindow?.ShowStatus("The search text was not found");
            else
                _findReplaceWindow?.ShowStatus(null);
        }

        public void ExecuteFindPrev(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (FindActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to find");
                return;
            }

            if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: false))
                _findReplaceWindow?.ShowStatus("The search text was not found");
            else
                _findReplaceWindow?.ShowStatus(null);
        }

        public void ExecuteReplace(string findText, string replaceText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastReplaceText = replaceText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (FindActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to replace");
                return;
            }

            var sanitizedReplaceText = replaceText ?? string.Empty;
            string? statusMsg = null;
            try
            {
                if (!TryReplaceCurrent(editorTextEditor, findText, sanitizedReplaceText, matchCase, wholeWord, useRegex))
                    TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: true);
            }
            catch (ArgumentException ex)
            {
                statusMsg = $"Invalid regex: {ex.Message}";
            }
            _findReplaceWindow?.ShowStatus(statusMsg);
        }

        public void ExecuteReplaceAll(string findText, string replaceText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            _lastFindText = findText ?? string.Empty;
            _lastReplaceText = replaceText ?? string.Empty;
            _lastFindMatchCase = matchCase;
            _lastFindWholeWord = wholeWord;
            _lastFindUseRegex = useRegex;

            if (FindActiveEditor() is not TextEditor editorTextEditor)
                return;

            if (string.IsNullOrWhiteSpace(findText))
            {
                _findReplaceWindow?.ShowStatus("Enter text to replace");
                return;
            }

            var sanitizedReplaceText = replaceText ?? string.Empty;
            string? statusMsg = null;
            try
            {
                var replacements = ReplaceAll(editorTextEditor, findText, sanitizedReplaceText, matchCase, wholeWord, useRegex);
                statusMsg = replacements == 0 ? "No matches were found to replace" : null;
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = replacements == 0
                        ? "No matches were found to replace"
                        : $"Replaced {replacements} occurrence(s)";
                }
            }
            catch (ArgumentException ex)
            {
                statusMsg = $"Invalid regex: {ex.Message}";
            }
            _findReplaceWindow?.ShowStatus(statusMsg);
        }

        private void ConfigureEditorTextEditor(TextEditor editorTextEditor)
        {
            if (!_configuredEditors.Add(editorTextEditor))
            {
                RegisterEditor(editorTextEditor);
                return;
            }

            editorTextEditor.FontFamily = new System.Windows.Media.FontFamily("Consolas");
            editorTextEditor.ShowLineNumbers = true;
            editorTextEditor.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
            editorTextEditor.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            editorTextEditor.Options.AllowScrollBelowDocument = false;
            editorTextEditor.Options.ConvertTabsToSpaces = false;
            editorTextEditor.Options.EnableRectangularSelection = true;
            editorTextEditor.Options.IndentationSize = 4;
            editorTextEditor.Options.HighlightCurrentLine = true;
            ApplyEditorHighlightSettings(editorTextEditor);

            editorTextEditor.TextChanged += EditorTextEditor_TextChanged;
            editorTextEditor.TextArea.Caret.PositionChanged += EditorTextEditor_CaretPositionChanged;
            editorTextEditor.TextArea.SelectionChanged += EditorTextEditor_SelectionChanged;
            editorTextEditor.TextArea.TextEntered += EditorTextArea_TextEntered;
            editorTextEditor.TextArea.TextEntering += EditorTextArea_TextEntering;
            editorTextEditor.PreviewKeyDown += EditorTextEditor_PreviewKeyDown;
            editorTextEditor.GotKeyboardFocus += EditorTextEditor_GotKeyboardFocus;

            var syntaxColorizer = new PowerShellSyntaxColorizer();
            _syntaxColorizers[editorTextEditor] = syntaxColorizer;
            editorTextEditor.TextArea.TextView.LineTransformers.Add(syntaxColorizer);
            editorTextEditor.TextArea.IndentationStrategy = new PowerShellIndentationStrategy();

            // Apply current zoom level on first configuration (2B).
            editorTextEditor.FontSize = ViewModel?.EditorZoomLevel ?? 13.0;

            // Ctrl+MouseWheel zoom (2B).
            editorTextEditor.PreviewMouseWheel += EditorTextEditor_PreviewMouseWheel;

            // Brace matching renderer (2C).
            var braceRenderer = new BraceMatchingRenderer();
            _braceMatchingRenderers[editorTextEditor] = braceRenderer;
            editorTextEditor.TextArea.TextView.BackgroundRenderers.Add(braceRenderer);
            editorTextEditor.TextArea.Caret.PositionChanged += (_, _) =>
                braceRenderer.UpdateFromCaret(editorTextEditor);

            EnsureDiagnosticGlyphMarginAttached(editorTextEditor);

            // Error renderer, diagnostics glyphs, and hover events are managed per registration
            // so they survive Unload/Load cycles when the user switches tabs.
            RegisterEditor(editorTextEditor);
        }

        private void RegisterEditor(TextEditor editorTextEditor)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
            {
                CancelPendingDiagnostics(editorTextEditor);
                return;
            }

            IncrementEditorRegistrationVersion(editorTextEditor);

            if (_tabByEditor.TryGetValue(editorTextEditor, out var previousTab))
            {
                previousTab.PropertyChanged -= EditorTab_PropertyChanged;
                _editorByTab.Remove(previousTab);
                _tabByEditor.Remove(editorTextEditor);
            }

            _editorByTab[tab] = editorTextEditor;
            _tabByEditor[editorTextEditor] = tab;
            tab.PropertyChanged -= EditorTab_PropertyChanged;
            tab.PropertyChanged += EditorTab_PropertyChanged;

            SynchronizeEditorTextFromViewModel(editorTextEditor, tab.Content);
            ClearParserTokensForEditor(editorTextEditor);

            if (_breakpointRenderers.TryGetValue(editorTextEditor, out var existingBreakpointRenderer))
            {
                editorTextEditor.TextArea.TextView.BackgroundRenderers.Remove(existingBreakpointRenderer);
                _breakpointRenderers.Remove(editorTextEditor);
            }

            var breakpointRenderer = new BreakpointLineBackgroundRenderer(tab);
            _breakpointRenderers[editorTextEditor] = breakpointRenderer;
            EnsureBackgroundRendererAttached(editorTextEditor, breakpointRenderer);
            EnsureBreakpointGlyphMarginAttached(editorTextEditor, tab);

            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);
            ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);

            if (!_foldingManagers.TryGetValue(editorTextEditor, out var foldingManager))
            {
                foldingManager = FoldingManager.Install(editorTextEditor.TextArea);
                _foldingManagers[editorTextEditor] = foldingManager;
            }
            _foldingStrategy.UpdateFoldings(foldingManager, editorTextEditor.Document);

            editorTextEditor.TextArea.TextView.MouseMove -= OnTextViewMouseMove;
            editorTextEditor.TextArea.TextView.MouseMove += OnTextViewMouseMove;
            editorTextEditor.TextArea.TextView.MouseLeave -= OnTextViewMouseLeave;
            editorTextEditor.TextArea.TextView.MouseLeave += OnTextViewMouseLeave;
            editorTextEditor.TextArea.TextView.MouseHover -= OnTextViewMouseHover;
            editorTextEditor.TextArea.TextView.MouseHover += OnTextViewMouseHover;
            editorTextEditor.TextArea.TextView.MouseHoverStopped -= OnTextViewMouseHoverStopped;
            editorTextEditor.TextArea.TextView.MouseHoverStopped += OnTextViewMouseHoverStopped;

            editorTextEditor.TextArea.TextView.Redraw();
            ScheduleDiagnostics(editorTextEditor);
        }

        private int IncrementEditorRegistrationVersion(TextEditor editorTextEditor)
        {
            var nextVersion = _editorRegistrationVersions.TryGetValue(editorTextEditor, out var currentVersion)
                ? currentVersion + 1
                : 1;
            _editorRegistrationVersions[editorTextEditor] = nextVersion;
            return nextVersion;
        }

        private int IncrementDiagnosticsRequestVersion(TextEditor editorTextEditor)
        {
            var nextVersion = _diagnosticsRequestVersions.TryGetValue(editorTextEditor, out var currentVersion)
                ? currentVersion + 1
                : 1;
            _diagnosticsRequestVersions[editorTextEditor] = nextVersion;
            return nextVersion;
        }

        private void CancelPendingDiagnostics(TextEditor editorTextEditor)
        {
            if (_diagnosticsCancellationSources.TryGetValue(editorTextEditor, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _diagnosticsCancellationSources.Remove(editorTextEditor);
            }
        }

        private void CancelPendingFolding(TextEditor editorTextEditor)
        {
            if (_foldingCancellationSources.TryGetValue(editorTextEditor, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
                _foldingCancellationSources.Remove(editorTextEditor);
            }
        }

        private void ScheduleFolding(TextEditor editorTextEditor)
        {
            if (editorTextEditor.Document is null || !_foldingManagers.ContainsKey(editorTextEditor))
            {
                return;
            }

            CancelPendingFolding(editorTextEditor);

            var cts = new CancellationTokenSource();
            var token = cts.Token;
            _foldingCancellationSources[editorTextEditor] = cts;
            var registrationVersion = _editorRegistrationVersions.TryGetValue(editorTextEditor, out var version) ? version : 0;

            ObserveFireAndForget(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(EditorFoldingDebounceMilliseconds, token).ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (!_editorRegistrationVersions.TryGetValue(editorTextEditor, out var currentVersion) ||
                            currentVersion != registrationVersion ||
                            !_foldingManagers.TryGetValue(editorTextEditor, out var foldingManager) ||
                            editorTextEditor.Document is null)
                        {
                            return;
                        }

                        _foldingStrategy.UpdateFoldings(foldingManager, editorTextEditor.Document);
                    });
                }
                catch (OperationCanceledException) { }
                finally
                {
                    try
                    {
                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (_foldingCancellationSources.TryGetValue(editorTextEditor, out var active) && ReferenceEquals(active, cts))
                            {
                                _foldingCancellationSources.Remove(editorTextEditor);
                                active.Dispose();
                            }
                        });
                    }
                    catch { /* Application is closing or dispatcher is unavailable. */ }
                }
            }, token), "editor folding update");
        }

        private static void EnsureBackgroundRendererAttached(TextEditor editorTextEditor, IBackgroundRenderer renderer)
        {
            if (!editorTextEditor.TextArea.TextView.BackgroundRenderers.Contains(renderer))
            {
                editorTextEditor.TextArea.TextView.BackgroundRenderers.Add(renderer);
            }
        }

        private ErrorMarkerRenderer EnsureErrorRendererAttached(TextEditor editorTextEditor)
        {
            if (!_errorRenderers.TryGetValue(editorTextEditor, out var errorRenderer))
            {
                errorRenderer = new ErrorMarkerRenderer();
                _errorRenderers[editorTextEditor] = errorRenderer;
            }

            EnsureBackgroundRendererAttached(editorTextEditor, errorRenderer);
            return errorRenderer;
        }

        private void EnsureBreakpointGlyphMarginAttached(TextEditor editorTextEditor, EditorTabViewModel tab)
        {
            if (!_breakpointGlyphMargins.TryGetValue(editorTextEditor, out var margin))
            {
                margin = new BreakpointGlyphMargin(tab);
                margin.BreakpointLineClicked += lineNumber => OnBreakpointGlyphLineClicked(editorTextEditor, lineNumber);
                _breakpointGlyphMargins[editorTextEditor] = margin;
            }
            else
            {
                margin.SetTab(tab);
            }

            // Keep the breakpoint target as its own narrow column, separate from
            // diagnostics, folding, line numbers, and the editable text area.
            if (editorTextEditor.TextArea.LeftMargins.Contains(margin))
            {
                editorTextEditor.TextArea.LeftMargins.Remove(margin);
            }

            editorTextEditor.TextArea.LeftMargins.Insert(0, margin);
            margin.Refresh();
        }

        private void RefreshBreakpointGlyphMargin(TextEditor editorTextEditor)
        {
            if (_breakpointGlyphMargins.TryGetValue(editorTextEditor, out var margin))
            {
                margin.Refresh();
            }
        }

        private void EnsureDiagnosticGlyphMarginAttached(TextEditor editorTextEditor)
        {
            if (!_diagnosticGlyphMargins.TryGetValue(editorTextEditor, out var margin))
            {
                margin = new DiagnosticGlyphMargin();
                margin.DiagnosticLineClicked += lineNumber => OnDiagnosticGlyphLineClicked(editorTextEditor, lineNumber);
                _diagnosticGlyphMargins[editorTextEditor] = margin;
            }

            if (!editorTextEditor.TextArea.LeftMargins.Contains(margin))
            {
                editorTextEditor.TextArea.LeftMargins.Insert(0, margin);
            }
        }

        private static IReadOnlyList<ParseErrorInfo> BuildParseErrorsFromTab(EditorTabViewModel tab)
        {
            return tab.SyntaxDiagnosticSpans
                .Select(diagnostic => new ParseErrorInfo(diagnostic.Message, diagnostic.StartOffset, diagnostic.EndOffset))
                .ToList();
        }

        private void ApplyPersistedSyntaxDiagnosticsToEditor(ErrorMarkerRenderer errorRenderer, EditorTabViewModel tab, TextEditor editorTextEditor)
        {
            errorRenderer.SetErrors(BuildParseErrorsFromTab(tab));

            if (_diagnosticGlyphMargins.TryGetValue(editorTextEditor, out var diagnosticGlyphMargin))
            {
                diagnosticGlyphMargin.SetDiagnostics(tab.SyntaxDiagnosticSpans);
            }

            editorTextEditor.TextArea.TextView.Redraw();
        }

        private void SynchronizeEditorTextFromViewModel(TextEditor editorTextEditor, string? content)
        {
            var targetText = content ?? string.Empty;
            if (string.Equals(editorTextEditor.Text, targetText, StringComparison.Ordinal))
            {
                return;
            }

            var caretOffset = editorTextEditor.CaretOffset;

            try
            {
                _editorTextSynchronizationInProgress.Add(editorTextEditor);
                editorTextEditor.Text = targetText;
                editorTextEditor.CaretOffset = Math.Min(caretOffset, editorTextEditor.Text.Length);
            }
            finally
            {
                _editorTextSynchronizationInProgress.Remove(editorTextEditor);
            }
        }

        private void UnregisterEditor(TextEditor editorTextEditor)
        {
            IncrementEditorRegistrationVersion(editorTextEditor);
            CancelPendingDiagnostics(editorTextEditor);
            CancelPendingFolding(editorTextEditor);

            if (_tabByEditor.TryGetValue(editorTextEditor, out var tab))
            {
                tab.PropertyChanged -= EditorTab_PropertyChanged;
                _editorByTab.Remove(tab);
                _tabByEditor.Remove(editorTextEditor);
            }

            if (_breakpointRenderers.TryGetValue(editorTextEditor, out var renderer))
            {
                editorTextEditor.TextArea.TextView.BackgroundRenderers.Remove(renderer);
                _breakpointRenderers.Remove(editorTextEditor);
            }

            if (_breakpointGlyphMargins.TryGetValue(editorTextEditor, out var breakpointGlyphMargin))
            {
                editorTextEditor.TextArea.LeftMargins.Remove(breakpointGlyphMargin);
                _breakpointGlyphMargins.Remove(editorTextEditor);
            }

            editorTextEditor.TextArea.TextView.MouseMove -= OnTextViewMouseMove;
            editorTextEditor.TextArea.TextView.MouseLeave -= OnTextViewMouseLeave;
            editorTextEditor.TextArea.TextView.MouseHover -= OnTextViewMouseHover;
            editorTextEditor.TextArea.TextView.MouseHoverStopped -= OnTextViewMouseHoverStopped;
            _editorTextSynchronizationInProgress.Remove(editorTextEditor);
        }

        private void EditorTab_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is not EditorTabViewModel tab || !_editorByTab.TryGetValue(tab, out var editorTextEditor))
            {
                return;
            }

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    $"Editor tab property changed: {e.PropertyName}.",
                    new Dictionary<string, object?>
                    {
                        ["tabTitle"] = tab.Title,
                        ["filePath"] = tab.FilePath,
                        ["isDirty"] = tab.IsDirty
                    });
            }

            if (e.PropertyName == nameof(EditorTabViewModel.Content))
            {
                SynchronizeEditorTextFromViewModel(editorTextEditor, tab.Content);
                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab))
                {
                    RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
                }

                return;
            }

            if (e.PropertyName == nameof(EditorTabViewModel.BreakpointVersion))
            {
                editorTextEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                RefreshBreakpointGlyphMargin(editorTextEditor);
                RefreshBreakpointsList();
                RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
                return;
            }

            if (e.PropertyName == nameof(EditorTabViewModel.SyntaxDiagnosticSpans) ||
                e.PropertyName == nameof(EditorTabViewModel.SyntaxDiagnosticsStatusText))
            {
                var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);
                ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);
                return;
            }

            if (e.PropertyName == nameof(EditorTabViewModel.FilePath))
            {
                RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
            }
        }

        private void UpdateEditorCaretMetrics(TextEditor editorTextEditor)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab || editorTextEditor.Document is null)
            {
                return;
            }

            var caretOffset = Math.Clamp(editorTextEditor.CaretOffset, 0, editorTextEditor.Document.TextLength);
            var line = editorTextEditor.Document.GetLineByOffset(caretOffset);
            var lineNumber = line.LineNumber;
            var column = (caretOffset - line.Offset) + 1;
            tab.UpdateCaretPosition(lineNumber, column, editorTextEditor.SelectionLength);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Editor caret metrics updated.",
                    new Dictionary<string, object?>
                    {
                        ["filePath"] = tab.FilePath,
                        ["lineNumber"] = lineNumber,
                        ["column"] = column,
                        ["selectionLength"] = editorTextEditor.SelectionLength
                    });
            }
        }

        private TextEditor? FindActiveEditor()
        {
            if (ViewModel?.SelectedTab is null)
            {
                return null;
            }

            if (_editorByTab.TryGetValue(ViewModel.SelectedTab, out var editorTextEditor))
            {
                return editorTextEditor;
            }

            return null;
        }

        private TextEditor? ResolveEditorFromEventSender(object? sender)
        {
            if (sender is DependencyObject dependencyObject)
            {
                return FindAncestor<TextEditor>(dependencyObject);
            }

            if (Keyboard.FocusedElement is DependencyObject focusedDependencyObject)
            {
                return FindAncestor<TextEditor>(focusedDependencyObject);
            }

            return FindActiveEditor();
        }

        private void OnTerminalActivated(string source)
        {
            SetTerminalActive(true, source);
            AppLogger.Debug(
                "Terminal",
                $"Terminal activation routed to MainWindow. Source={source}, FocusedElement={DescribeFocusedElement()}.");
            DeveloperDiagnostics.LogUserAction(
                "Terminal",
                "TerminalActivated",
                "Terminal activation routed through MainWindow.",
                new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["focusedElement"] = DescribeFocusedElement()
                });
        }

        private void OpenConsolePrototype_Click(object sender, RoutedEventArgs e)
        {
            AppLogger.Info("ConsolePrototype", "Opening isolated console prototype window from the main shell.");
            var prototypeWindow = new ConsolePrototypeWindow
            {
                Owner = this
            };

            prototypeWindow.Show();
            prototypeWindow.Activate();
        }

        private void SetTerminalActive(bool isActive, string source)
        {
            if (_terminalIsActive == isActive)
            {
                if (isActive)
                {
                    CloseEditorCompletion("Terminal already active");
                }

                return;
            }

            _terminalIsActive = isActive;
            AppLogger.Debug("Terminal", $"Terminal active state changed. Active={isActive}, Source={source}, FocusedElement={DescribeFocusedElement()}.");

            if (isActive)
            {
                CloseEditorCompletion("Terminal activated");
            }
        }

        private bool ShouldSuppressEditorInputFeatures(TextEditor editorTextEditor, string source)
        {
            if (!_terminalIsActive)
            {
                return false;
            }

            AppLogger.Debug(
                "EditorCompletion",
                $"Suppressing editor IntelliSense/input helper logic because terminal is active. Source={source}, Editor={DescribeEditor(editorTextEditor)}, EditorFocused={editorTextEditor.IsKeyboardFocusWithin}, FocusedElement={DescribeFocusedElement()}.");
            return true;
        }

        private void CloseEditorCompletion(string reason)
        {
            var hadPendingRequest = _activeCompletionCts is not null;
            var hadCompletionWindow = _activeCompletionWindow is not null;
            if (!hadPendingRequest && !hadCompletionWindow)
            {
                return;
            }

            AppLogger.Debug(
                "EditorCompletion",
                $"Closing editor completion state. Reason={reason}, HadPendingRequest={hadPendingRequest}, HadPopup={hadCompletionWindow}.");

            _activeCompletionCts?.Cancel();
            _activeCompletionCts?.Dispose();
            _activeCompletionCts = null;

            _activeCompletionWindow?.Close();
            _activeCompletionWindow = null;
        }

        private string DescribeEditor(TextEditor editorTextEditor)
        {
            if (editorTextEditor.DataContext is EditorTabViewModel tab)
            {
                return tab.Title;
            }

            return $"Editor#{editorTextEditor.GetHashCode():x}";
        }

        private static string DescribeFocusedElement()
        {
            return Keyboard.FocusedElement is null
                ? "(null)"
                : Keyboard.FocusedElement.GetType().Name;
        }

        private static string FormatTerminalTextForLog(string text, int maxLength = 80)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder(Math.Min(text.Length * 2, maxLength + 8));
            foreach (var ch in text)
            {
                _ = ch switch
                {
                    '\r' => builder.Append("\\r"),
                    '\n' => builder.Append("\\n"),
                    '\t' => builder.Append("\\t"),
                    '\x1b' => builder.Append("\\x1b"),
                    _ when char.IsControl(ch) => builder.Append($"\\u{(int)ch:x4}"),
                    _ => builder.Append(ch)
                };

                if (builder.Length >= maxLength)
                {
                    builder.Append("...");
                    break;
                }
            }

            return builder.ToString();
        }

        private void ForceCompletionNow(TextEditor editorTextEditor, string invocationSource)
        {
            AppLogger.Debug(
                "EditorCompletion",
                $"Force completion requested. Source={invocationSource}, Handler=EditorTextEditor_PreviewKeyDown, Fragment='{GetCurrentWordFragment(editorTextEditor)}', InsideParameterToken={IsCaretInsideParameterToken(editorTextEditor)}, CaretOffset={editorTextEditor.CaretOffset}, MetadataPhase={_lastEditorMetadataWarmupPhase}, MetadataWarmupTriggered=False.");
            ShowCompletionAsync(editorTextEditor, autoTriggered: false, includeEngine: true, forceCompletion: true);
        }

        private async void ShowCompletionAsync(TextEditor editorTextEditor, bool autoTriggered, bool includeEngine = true, bool forceCompletion = false)
        {
            if (ShouldSuppressEditorInputFeatures(editorTextEditor, "ShowCompletionAsync"))
            {
                return;
            }

            _activeCompletionCts?.Cancel();
            _activeCompletionCts?.Dispose();
            var cts = new CancellationTokenSource();
            _activeCompletionCts = cts;

            try
            {
                if (autoTriggered && !forceCompletion && !IsCaretInsideParameterToken(editorTextEditor))
                    await Task.Delay(125, cts.Token).ConfigureAwait(true);

                if (cts.Token.IsCancellationRequested)
                {
                    AppLogger.Debug("EditorCompletion", $"Completion request canceled before popup generation began. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}.");
                    return;
                }

                if (ShouldSuppressEditorInputFeatures(editorTextEditor, "ShowCompletionAsync.BeforePopup"))
                {
                    return;
                }

                _activeCompletionWindow?.Close();

                var pwshPath = ViewModel?.EffectiveRuntimeExecutablePath;
                var fragment = GetCurrentWordFragment(editorTextEditor);
                var insideParameterToken = IsCaretInsideParameterToken(editorTextEditor);
                var engineWaitMilliseconds = includeEngine
                    ? (forceCompletion
                        ? (insideParameterToken ? 650 : 350)
                        : (autoTriggered
                        ? (IsCaretInsideParameterToken(editorTextEditor) ? 450 : 120)
                        : 220))
                    : 0;

                AppLogger.Debug(
                    "EditorCompletion",
                    $"Starting completion request. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}, IncludeEngine={includeEngine}, Fragment='{fragment}', InsideParameterToken={insideParameterToken}, EngineWaitMs={engineWaitMilliseconds}, CaretOffset={editorTextEditor.CaretOffset}.");

                var window = await _intelliSenseService.ShowCompletionAsync(
                        editorTextEditor,
                        pwshPath,
                        includeEngine,
                        engineWaitMilliseconds,
                        forceCompletion,
                        cts.Token)
                    .ConfigureAwait(true);

                if (window is null)
                {
                    AppLogger.Debug(
                        "EditorCompletion",
                        $"Completion request produced no popup. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}, Fragment='{fragment}', InsideParameterToken={insideParameterToken}.");
                    if (!autoTriggered && ViewModel is not null)
                        ViewModel.StatusText = "No IntelliSense suggestions were available";
                    return;
                }

                if (ShouldSuppressEditorInputFeatures(editorTextEditor, "ShowCompletionAsync.AfterResults"))
                {
                    window.Close();
                    return;
                }

                _activeCompletionWindow = window;
                _activeCompletionWindow.Closed += (_, _) => _activeCompletionWindow = null;
                _activeCompletionWindow.Show();
                AppLogger.Debug(
                    "EditorCompletion",
                    $"Completion popup shown. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}, Fragment='{fragment}', InsideParameterToken={insideParameterToken}, Items={window.CompletionList.CompletionData.Count}.");
            }
            catch (OperationCanceledException)
            {
                AppLogger.Debug("EditorCompletion", $"Completion request canceled while waiting for IntelliSense results. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}.");
            }
            catch (ObjectDisposedException)
            {
                AppLogger.Debug("EditorCompletion", $"Completion request aborted because the editor or completion window was disposed. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("EditorCompletion", "IntelliSense completion request failed.", ex);

                if (!autoTriggered && ViewModel is not null && !cts.Token.IsCancellationRequested)
                {
                    ViewModel.StatusText = "IntelliSense request failed.";
                }
            }
        }

        private static void AutoInsertClosingDelimiter(TextEditor editor, char closing)
        {
            if (editor.Document is null) return;
            var offset = editor.CaretOffset;
            editor.Document.Insert(offset, closing.ToString());
            editor.CaretOffset = offset;
        }

        private static bool ShouldAutoInsertClosingDelimiter(TextEditor editor)
        {
            if (editor.Document is null)
            {
                return false;
            }

            var text = editor.Text ?? string.Empty;
            var offset = Math.Clamp(editor.CaretOffset, 0, text.Length);

            // TextEntered fires after the opening delimiter has been inserted. Inspect
            // the code before the typed delimiter to decide whether it was inside a
            // single-line comment or quoted string.
            var scanLength = Math.Max(0, offset - 1);
            var lineStart = scanLength == 0 ? 0 : text.LastIndexOf('\n', scanLength - 1);
            lineStart = lineStart < 0 ? 0 : lineStart + 1;

            var inSingleQuote = false;
            var inDoubleQuote = false;

            for (var i = lineStart; i < scanLength; i++)
            {
                var ch = text[i];

                if (!inSingleQuote && !inDoubleQuote && ch == '#')
                {
                    return false;
                }

                if (ch == '`')
                {
                    i++;
                    continue;
                }

                if (!inDoubleQuote && ch == '\'')
                {
                    inSingleQuote = !inSingleQuote;
                    continue;
                }

                if (!inSingleQuote && ch == '"')
                {
                    inDoubleQuote = !inDoubleQuote;
                }
            }

            return !inSingleQuote && !inDoubleQuote;
        }

        private async Task<bool> ShowEditorQuickInfoAtCaretAsync(TextEditor editorTextEditor, bool updateStatusOnly)
        {
            if (editorTextEditor.Document is null)
            {
                return false;
            }

            var cts = BeginQuickInfoRequest();
            var cancellationToken = cts.Token;

            try
            {
                var quickInfo = await _intelliSenseService.GetQuickInfoAsync(
                    editorTextEditor,
                    editorTextEditor.CaretOffset,
                    ViewModel?.EffectiveRuntimeExecutablePath,
                    cancellationToken).ConfigureAwait(true);

                if (cancellationToken.IsCancellationRequested || quickInfo is null)
                {
                    return false;
                }

                if (ViewModel is not null)
                {
                    ViewModel.StatusText = BuildQuickInfoStatusText(quickInfo);
                }

                if (!updateStatusOnly)
                {
                    ShowEditorToolTip(editorTextEditor.TextArea.TextView, quickInfo.ToString());
                }

                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                // A hover/F1 request can be canceled while the editor is closing or while a newer
                // request takes over. Treat that as a normal stale quick-info request.
                return false;
            }
            finally
            {
                CompleteQuickInfoRequest(cts);
            }
        }

        private CancellationTokenSource BeginQuickInfoRequest()
        {
            var cts = new CancellationTokenSource();
            var previous = _quickInfoCts;
            _quickInfoCts = cts;

            try
            {
                previous?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            // Do not dispose the previous CTS here. The async operation that owns it
            // disposes it in its own finally block. Disposing it from a newer hover/F1
            // request can race with that older request and throw ObjectDisposedException
            // when it checks its token after await.
            return cts;
        }

        private void CancelActiveQuickInfoRequest()
        {
            try
            {
                _quickInfoCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void CompleteQuickInfoRequest(CancellationTokenSource cts)
        {
            if (ReferenceEquals(_quickInfoCts, cts))
            {
                _quickInfoCts = null;
            }

            // Do not dispose quick-info token sources on the UI/event path. Hover, F1,
            // and typed-signature updates intentionally overlap, and several call sites
            // are fire-and-forget. Disposing here creates a race where a still-running
            // async continuation can touch CancellationTokenSource.Token after another
            // request has completed and disposed the source. The CTS is small and will
            // be reclaimed normally; cancellation is enough for this short-lived editor
            // operation.
        }

        private static void ObserveFireAndForget(Task task, string operationName)
        {
            _ = task.ContinueWith(
                completedTask =>
                {
                    _ = completedTask.Exception;
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Background {operationName} failed: {completedTask.Exception}");
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private static string BuildQuickInfoStatusText(EditorQuickInfo quickInfo)
        {
            if (string.IsNullOrWhiteSpace(quickInfo.Body))
            {
                return quickInfo.Title;
            }

            var firstUsefulLine = quickInfo.Body
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(line => line.Trim())
                .FirstOrDefault(line =>
                    line.Length > 0 &&
                    !line.Equals("Syntax:", StringComparison.OrdinalIgnoreCase) &&
                    !line.Equals("Parameters:", StringComparison.OrdinalIgnoreCase));

            return string.IsNullOrWhiteSpace(firstUsefulLine)
                ? quickInfo.Title
                : $"{quickInfo.Title}: {firstUsefulLine}";
        }

        private static string GetCurrentWordFragment(TextEditor editor)
        {
            if (editor.Document is null) return string.Empty;
            var offset = editor.CaretOffset;
            var text = editor.Text ?? string.Empty;
            var start = offset;
            while (start > 0)
            {
                var ch = text[start - 1];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == '$')
                    start--;
                else
                    break;
            }
            return text.Substring(start, offset - start);
        }

        private static bool IsCaretInsideParameterToken(TextEditor editor)
        {
            if (editor.Document is null)
            {
                return false;
            }

            var text = editor.Text ?? string.Empty;
            var offset = Math.Clamp(editor.CaretOffset, 0, text.Length);
            var start = offset;
            while (start > 0)
            {
                var ch = text[start - 1];
                if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-')
                {
                    start--;
                    continue;
                }

                break;
            }

            return start < offset && text[start] == '-';
        }

        private static void ToggleCommentForEditor(TextEditor editor)
        {
            if (editor.Document is null) return;
            var doc = editor.Document;

            int firstLine, lastLine;
            if (editor.SelectionLength > 0)
            {
                firstLine = doc.GetLineByOffset(editor.SelectionStart).LineNumber;
                var selEnd = editor.SelectionStart + editor.SelectionLength;
                // Don't include a trailing line where the selection ends at column 1
                var lastLineCandidate = doc.GetLineByOffset(Math.Min(selEnd, doc.TextLength)).LineNumber;
                if (lastLineCandidate > firstLine && selEnd == doc.GetLineByNumber(lastLineCandidate).Offset)
                    lastLineCandidate--;
                lastLine = lastLineCandidate;
            }
            else
            {
                firstLine = lastLine = doc.GetLineByOffset(editor.CaretOffset).LineNumber;
            }

            // Determine whether ALL selected lines already start with #
            var allCommented = true;
            for (var ln = firstLine; ln <= lastLine; ln++)
            {
                var lineText = doc.GetText(doc.GetLineByNumber(ln)).TrimStart();
                if (!lineText.StartsWith("#", StringComparison.Ordinal))
                {
                    allCommented = false;
                    break;
                }
            }

            using (doc.RunUpdate())
            {
                for (var ln = lastLine; ln >= firstLine; ln--)
                {
                    var line = doc.GetLineByNumber(ln);
                    var lineText = doc.GetText(line);
                    var indent = lineText.Length - lineText.TrimStart().Length;

                    if (allCommented)
                    {
                        // Remove leading #
                        var hashIndex = lineText.IndexOf('#', StringComparison.Ordinal);
                        if (hashIndex >= 0)
                            doc.Remove(line.Offset + hashIndex, 1);
                    }
                    else
                    {
                        // Add # at the indentation level
                        doc.Insert(line.Offset + indent, "#");
                    }
                }
            }
        }

        private static void MoveLineUp(TextEditor editor)
        {
            if (editor.Document is null) return;
            var doc = editor.Document;
            var caretLine = doc.GetLineByOffset(editor.CaretOffset);
            if (caretLine.LineNumber <= 1) return;
            var prevLine = doc.GetLineByNumber(caretLine.LineNumber - 1);
            var col = editor.CaretOffset - caretLine.Offset;
            var targetLineNumber = caretLine.LineNumber - 1;

            using (doc.RunUpdate())
            {
                var currentText = doc.GetText(caretLine.Offset, caretLine.Length);
                var prevText = doc.GetText(prevLine.Offset, prevLine.Length);
                doc.Replace(caretLine.Offset, caretLine.Length, prevText);
                doc.Replace(prevLine.Offset, prevLine.Length, currentText);
            }

            var newLine = doc.GetLineByNumber(targetLineNumber);
            editor.CaretOffset = newLine.Offset + Math.Min(col, newLine.Length);
        }

        private static void MoveLineDown(TextEditor editor)
        {
            if (editor.Document is null) return;
            var doc = editor.Document;
            var caretLine = doc.GetLineByOffset(editor.CaretOffset);
            if (caretLine.LineNumber >= doc.LineCount) return;
            var nextLine = doc.GetLineByNumber(caretLine.LineNumber + 1);
            var col = editor.CaretOffset - caretLine.Offset;
            var targetLineNumber = caretLine.LineNumber + 1;

            using (doc.RunUpdate())
            {
                var currentText = doc.GetText(caretLine.Offset, caretLine.Length);
                var nextText = doc.GetText(nextLine.Offset, nextLine.Length);
                doc.Replace(nextLine.Offset, nextLine.Length, currentText);
                doc.Replace(caretLine.Offset, caretLine.Length, nextText);
            }

            var newLine = doc.GetLineByNumber(targetLineNumber);
            editor.CaretOffset = newLine.Offset + Math.Min(col, newLine.Length);
        }

        private async System.Threading.Tasks.Task RunSelectionFromEditorAsync(TextEditor editorTextEditor)
        {
            if (ViewModel?.IsRunAvailable != true)
            {
                DeveloperDiagnostics.LogDecision("Execution", "RunSelection", "Run Selection requested while execution was unavailable.", "Rejected");
                return;
            }

            var selectedText = editorTextEditor.SelectedText;

            // Match legacy ISE behavior more closely: Run Selection/F8 runs selected
            // text when a selection exists; otherwise it runs the current line. This
            // also prevents a lost or empty AvalonEdit selection from making the toolbar
            // button appear to do nothing after focus moves away from the editor.
            if (string.IsNullOrWhiteSpace(selectedText) && editorTextEditor.Document is not null)
            {
                var caretLine = editorTextEditor.Document.GetLineByOffset(editorTextEditor.CaretOffset);
                selectedText = editorTextEditor.Document.GetText(caretLine.Offset, caretLine.Length);
            }

            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"RunSelection-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction(
                "Execution",
                "RunSelectionRequested",
                "Run Selection requested from the editor.",
                new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(selectedText))
                {
                    ["filePath"] = (editorTextEditor.DataContext as EditorTabViewModel)?.FilePath,
                    ["caretOffset"] = editorTextEditor.CaretOffset
                });
            await ViewModel.RunSelectionAsync(selectedText).ConfigureAwait(true);
            DeveloperDiagnostics.LogInfo("Execution", "Run Selection dispatched to ViewModel.");
        }

        private async Task RunScriptWithBreakpointAwarenessAsync()
        {
            if (ViewModel?.SelectedTab is null)
            {
                DeveloperDiagnostics.LogDecision("Execution", "RunScript", "Run requested without a selected tab.", "Rejected");
                return;
            }

            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"Execution-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction(
                "Execution",
                "RunScriptRequested",
                "Run Script requested.",
                new Dictionary<string, object?>
                {
                    ["filePath"] = ViewModel.SelectedTab.FilePath,
                    ["isDirty"] = ViewModel.SelectedTab.IsDirty,
                    ["enabledBreakpointCount"] = ViewModel.SelectedTab.EnabledBreakpointCount
                });
            if (ViewModel.SelectedTab.EnabledBreakpointCount > 0)
            {
                ViewModel.StatusText = "Enabled breakpoints detected — starting a debug session instead of plain Run.";
                DeveloperDiagnostics.LogDecision("Execution", "RunScript", "Enabled breakpoints redirected Run into Debug.", "RedirectToDebug");
                StartDebug_Click(this, new RoutedEventArgs());
                return;
            }

            if (ViewModel.RunCommand.CanExecute(null))
            {
                ViewModel.RunCommand.Execute(null);
                DeveloperDiagnostics.LogInfo("Execution", "Run command executed.");
            }

            await Task.CompletedTask;
        }

        private void ToggleBreakpointForEditor(TextEditor editorTextEditor)
        {
            if (editorTextEditor.Document is null)
            {
                return;
            }

            var caretOffset = Math.Clamp(editorTextEditor.CaretOffset, 0, editorTextEditor.Document.TextLength);
            var lineNumber = editorTextEditor.Document.GetLineByOffset(caretOffset).LineNumber;
            ToggleBreakpointForEditorLine(editorTextEditor, lineNumber);
        }

        private void OnBreakpointGlyphLineClicked(TextEditor editorTextEditor, int lineNumber)
        {
            ToggleBreakpointForEditorLine(editorTextEditor, lineNumber);
        }

        private void ToggleBreakpointForEditorLine(TextEditor editorTextEditor, int lineNumber)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab || editorTextEditor.Document is null)
            {
                return;
            }

            if (lineNumber < 1 || lineNumber > editorTextEditor.Document.LineCount)
            {
                return;
            }

            var documentLine = editorTextEditor.Document.GetLineByNumber(lineNumber);
            editorTextEditor.CaretOffset = documentLine.Offset;

            var breakpointAdded = tab.ToggleBreakpoint(lineNumber);
            DeveloperDiagnostics.LogUserAction(
                "Debugger",
                "BreakpointChanged",
                breakpointAdded ? "Breakpoint added." : "Breakpoint removed.",
                new Dictionary<string, object?>
                {
                    ["filePath"] = tab.FilePath,
                    ["lineNumber"] = lineNumber,
                    ["enabled"] = breakpointAdded,
                    ["totalBreakpoints"] = tab.EnabledBreakpointCount
                });
            editorTextEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            RefreshBreakpointGlyphMargin(editorTextEditor);
            RefreshBreakpointsList();
            RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);

            if (ViewModel is not null)
            {
                ViewModel.StatusText = breakpointAdded
                    ? $"Breakpoint added on line {lineNumber}"
                    : $"Breakpoint removed from line {lineNumber}";
            }
        }

        private void EditorTextEditor_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            SetTerminalActive(false, $"EditorFocus:{DescribeEditor(editorTextEditor)}");
            AppLogger.Debug(
                "EditorCompletion",
                $"Editor received keyboard focus. Editor={DescribeEditor(editorTextEditor)}, NewFocus={e.NewFocus?.GetType().Name ?? "(null)"}.");
        }

        // -------------------------------------------------------------------------
        // Ctrl+MouseWheel zoom (2B)
        // -------------------------------------------------------------------------

        private void EditorTextEditor_PreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
        {
            if (ViewModel is null || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            {
                return;
            }

            e.Handled = true;
            if (e.Delta > 0)
            {
                ViewModel.ZoomInCommand.Execute(null);
            }
            else
            {
                ViewModel.ZoomOutCommand.Execute(null);
            }
        }

        // -------------------------------------------------------------------------
        // Go to Line dialog (2A)
        // -------------------------------------------------------------------------

        private void GoToLine_Click(object sender, RoutedEventArgs e)
        {
            OpenGoToLineDialog();
        }

        private void OpenGoToLineDialog()
        {
            var editorTextEditor = FindActiveEditor();
            if (editorTextEditor is null || editorTextEditor.Document is null)
            {
                return;
            }

            var maxLine = editorTextEditor.Document.LineCount;
            var caretLine = editorTextEditor.Document
                .GetLineByOffset(Math.Clamp(editorTextEditor.CaretOffset, 0, editorTextEditor.Document.TextLength))
                .LineNumber;
            var dialog = new GoToLineDialog(this, caretLine, maxLine);
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var lineNumber = dialog.SelectedLine;
            if (lineNumber < 1 || lineNumber > maxLine)
            {
                return;
            }

            editorTextEditor.ScrollToLine(lineNumber);
            var line = editorTextEditor.Document.GetLineByNumber(lineNumber);
            editorTextEditor.CaretOffset = line.Offset;
            editorTextEditor.Focus();

            if (ViewModel is not null)
            {
                ViewModel.StatusText = $"Went to line {lineNumber}";
            }
        }

        // -------------------------------------------------------------------------
        // Theme menu handlers (5B)
        // -------------------------------------------------------------------------

        private void ThemeDark_Click(object sender, RoutedEventArgs e)    => ApplyTheme("Dark");
        private void ThemeLight_Click(object sender, RoutedEventArgs e)   => ApplyTheme("Light");
        private void ThemeIseBlue_Click(object sender, RoutedEventArgs e) => ApplyTheme("IseBlue");

        private void ApplyTheme(string themeName)
        {
            _themeService.ApplyTheme(themeName);
            ApplyEditorHighlightSettingsToAllEditors();
            if (ViewModel is not null)
            {
                ViewModel.CurrentThemeName = themeName;
                ViewModel.StatusText = $"Theme: {themeName}";
            }

            // Repaint all open editors so syntax colours update immediately.
            foreach (var editor in _editorByTab.Values)
            {
                editor.TextArea.TextView.Redraw();
            }
        }


        // -------------------------------------------------------------------------
        // Editor highlight / selection color settings
        // -------------------------------------------------------------------------

        private void ForceHighContrastSelectionText_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            ViewModel.ForceHighContrastSelectedText = ForceHighContrastSelectionTextMenuItem.IsChecked == true;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = ViewModel.ForceHighContrastSelectedText
                ? "Editor selected text: high-contrast foreground enabled"
                : "Editor selected text: preserving syntax colors";
        }

        private void SelectionHighlightThemeDefault_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            ViewModel.EditorSelectionBackgroundHex = null;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = "Editor selection background: active theme default";
        }

        private void SelectionHighlightPreset_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null || sender is not WpfMenuItem menuItem || menuItem.Tag is not string tag)
            {
                return;
            }

            if (!TryNormalizeHexColor(tag, out var normalizedHex))
            {
                return;
            }

            ViewModel.EditorSelectionBackgroundHex = normalizedHex;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = $"Editor selection background: {normalizedHex}";
        }

        private void SelectionHighlightCustom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            var currentColor = GetEffectiveSelectionBackgroundColor();
            var selectedHex = PromptForEditorColorHex(
                "Custom Selection Background",
                "Enter the editor selection background color as #RRGGBB.",
                currentColor);

            if (selectedHex is null)
            {
                return;
            }

            ViewModel.EditorSelectionBackgroundHex = selectedHex;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = $"Editor selection background: {selectedHex}";
        }

        private void CurrentLineHighlightThemeDefault_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            ViewModel.EditorCurrentLineBackgroundHex = null;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = "Editor current-line highlight: active theme default";
        }

        private void CurrentLineHighlightPreset_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null || sender is not WpfMenuItem menuItem || menuItem.Tag is not string tag)
            {
                return;
            }

            if (!TryNormalizeHexColor(tag, out var normalizedHex))
            {
                return;
            }

            ViewModel.EditorCurrentLineBackgroundHex = normalizedHex;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = $"Editor current-line highlight: {normalizedHex}";
        }

        private void CurrentLineHighlightCustom_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            var currentColor = GetEffectiveCurrentLineBackgroundColor();
            var selectedHex = PromptForEditorColorHex(
                "Custom Current-Line Background",
                "Enter the editor current-line highlight color as #RRGGBB.",
                currentColor);

            if (selectedHex is null)
            {
                return;
            }

            ViewModel.EditorCurrentLineBackgroundHex = selectedHex;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = $"Editor current-line highlight: {selectedHex}";
        }

        private void RestoreEditorHighlightDefaults_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            ViewModel.EditorSelectionBackgroundHex = null;
            ViewModel.EditorCurrentLineBackgroundHex = null;
            ViewModel.ForceHighContrastSelectedText = true;
            ApplyEditorHighlightSettingsToAllEditors();
            ViewModel.StatusText = "Editor highlight colors restored to defaults";
        }

        private void ApplyEditorHighlightSettingsToAllEditors()
        {
            UpdateEditorHighlightMenuState();

            foreach (var editorTextEditor in _editorByTab.Values)
            {
                ApplyEditorHighlightSettings(editorTextEditor);
            }
        }

        private void ApplyEditorHighlightSettings(TextEditor editorTextEditor)
        {
            var selectionBackgroundColor = GetEffectiveSelectionBackgroundColor();
            var selectionBackgroundBrush = CreateFrozenBrush(selectionBackgroundColor);
            SetPropertyIfAvailable(editorTextEditor.TextArea, "SelectionBrush", selectionBackgroundBrush);

            if (ViewModel?.ForceHighContrastSelectedText ?? true)
            {
                var selectionForegroundBrush = CreateFrozenBrush(GetBestTextColorForBackground(selectionBackgroundColor));
                SetPropertyIfAvailable(editorTextEditor.TextArea, "SelectionForeground", selectionForegroundBrush);
            }
            else if (!ClearDependencyPropertyIfAvailable(editorTextEditor.TextArea, "SelectionForegroundProperty"))
            {
                SetPropertyIfAvailable(editorTextEditor.TextArea, "SelectionForeground", null);
            }

            var currentLineBackgroundBrush = CreateFrozenBrush(GetEffectiveCurrentLineBackgroundColor());
            SetPropertyIfAvailable(editorTextEditor.TextArea.TextView, "CurrentLineBackground", currentLineBackgroundBrush);

            editorTextEditor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
            editorTextEditor.TextArea.TextView.Redraw();
        }

        private void UpdateEditorHighlightMenuState()
        {
            if (ViewModel is null)
            {
                return;
            }

            ForceHighContrastSelectionTextMenuItem.IsChecked = ViewModel.ForceHighContrastSelectedText;
            UpdateSelectionPresetMenuState(ViewModel.EditorSelectionBackgroundHex);
            UpdateCurrentLinePresetMenuState(ViewModel.EditorCurrentLineBackgroundHex);
        }

        private void UpdateSelectionPresetMenuState(string? selectedHex)
        {
            var normalized = NormalizeComparableHex(selectedHex);
            SelectionThemeDefaultMenuItem.IsChecked = normalized is null;
            SetPresetMenuItemChecked(SelectionPowerShellBlueMenuItem, normalized);
            SetPresetMenuItemChecked(SelectionNavyMenuItem, normalized);
            SetPresetMenuItemChecked(SelectionCharcoalMenuItem, normalized);
            SetPresetMenuItemChecked(SelectionPurpleMenuItem, normalized);
            SetPresetMenuItemChecked(SelectionGoldMenuItem, normalized);
        }

        private void UpdateCurrentLinePresetMenuState(string? selectedHex)
        {
            var normalized = NormalizeComparableHex(selectedHex);
            CurrentLineThemeDefaultMenuItem.IsChecked = normalized is null;
            SetPresetMenuItemChecked(CurrentLineSubtleNavyMenuItem, normalized);
            SetPresetMenuItemChecked(CurrentLineSoftSlateMenuItem, normalized);
            SetPresetMenuItemChecked(CurrentLineSoftPurpleMenuItem, normalized);
            SetPresetMenuItemChecked(CurrentLineSoftGoldMenuItem, normalized);
        }

        private static void SetPresetMenuItemChecked(WpfMenuItem menuItem, string? selectedHex)
        {
            menuItem.IsChecked = selectedHex is not null &&
                                 menuItem.Tag is string tag &&
                                 string.Equals(NormalizeComparableHex(tag), selectedHex, StringComparison.OrdinalIgnoreCase);
        }

        private string? PromptForEditorColorHex(string title, string instruction, WpfColor initialColor)
        {
            var result = (string?)null;
            var initialHex = FormatColorHex(initialColor);

            var dialog = new Window
            {
                Owner = this,
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Background = TryFindResource("Theme.Surface.Primary") as WpfBrush ?? WpfBrushes.White,
                Foreground = TryFindResource("Theme.Text.Primary") as WpfBrush ?? WpfBrushes.Black
            };

            var root = new StackPanel
            {
                Margin = new Thickness(16),
                MinWidth = 360
            };

            var instructionText = new TextBlock
            {
                Text = instruction,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var input = new WpfTextBox
            {
                Text = initialHex,
                MinWidth = 220,
                Margin = new Thickness(0, 0, 0, 10)
            };

            var preview = new Border
            {
                Height = 28,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                BorderBrush = TryFindResource("Theme.Border.Strong") as WpfBrush ?? WpfBrushes.Gray,
                Background = CreateFrozenBrush(initialColor),
                Margin = new Thickness(0, 0, 0, 14)
            };

            input.TextChanged += (_, _) =>
            {
                if (TryNormalizeHexColor(input.Text, out var normalizedHex) && TryParseHexColor(normalizedHex, out var parsedColor))
                {
                    preview.Background = CreateFrozenBrush(parsedColor);
                }
            };

            var buttons = new StackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                HorizontalAlignment = WpfHorizontalAlignment.Right
            };

            var okButton = new WpfButton
            {
                Content = "OK",
                IsDefault = true,
                MinWidth = 82,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var cancelButton = new WpfButton
            {
                Content = "Cancel",
                IsCancel = true,
                MinWidth = 82
            };

            okButton.Click += (_, _) =>
            {
                if (!TryNormalizeHexColor(input.Text, out var normalizedHex))
                {
                    System.Windows.MessageBox.Show(
                        this,
                        "Please enter a valid color in #RRGGBB format. Example: #0F4C81",
                        "Invalid Color",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    input.Focus();
                    input.SelectAll();
                    return;
                }

                result = normalizedHex;
                dialog.DialogResult = true;
            };

            buttons.Children.Add(okButton);
            buttons.Children.Add(cancelButton);
            root.Children.Add(instructionText);
            root.Children.Add(input);
            root.Children.Add(preview);
            root.Children.Add(buttons);
            dialog.Content = root;

            input.SelectAll();
            input.Focus();

            return dialog.ShowDialog() == true ? result : null;
        }

        private WpfColor GetEffectiveSelectionBackgroundColor()
        {
            if (TryParseHexColor(ViewModel?.EditorSelectionBackgroundHex, out var customColor))
            {
                return customColor;
            }

            if (TryGetResourceBrushColor("Theme.Editor.SelectionBackground", out var editorSelectionColor))
            {
                return editorSelectionColor;
            }

            if (TryGetResourceBrushColor("Theme.Selection.Background", out var themeSelectionColor))
            {
                return themeSelectionColor;
            }

            return WpfColor.FromRgb(0x0F, 0x4C, 0x81);
        }

        private WpfColor GetEffectiveCurrentLineBackgroundColor()
        {
            if (TryParseHexColor(ViewModel?.EditorCurrentLineBackgroundHex, out var customColor))
            {
                return customColor;
            }

            if (TryGetResourceBrushColor("Theme.Editor.CurrentLineBackground", out var editorCurrentLineColor))
            {
                return editorCurrentLineColor;
            }

            if (TryGetResourceBrushColor("Theme.Editor.LineHighlight", out var lineHighlightColor))
            {
                return lineHighlightColor;
            }

            return WpfColor.FromRgb(0x17, 0x21, 0x31);
        }

        private bool TryGetResourceBrushColor(string resourceKey, out WpfColor color)
        {
            if (TryFindResource(resourceKey) is WpfSolidColorBrush solidColorBrush)
            {
                color = solidColorBrush.Color;
                return true;
            }

            color = WpfColors.Transparent;
            return false;
        }

        private static bool TryNormalizeHexColor(string? value, out string normalizedHex)
        {
            normalizedHex = string.Empty;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var text = value.Trim();
            if (!text.StartsWith("#", StringComparison.Ordinal))
            {
                text = "#" + text;
            }

            if (text.Length == 4)
            {
                text = $"#{text[1]}{text[1]}{text[2]}{text[2]}{text[3]}{text[3]}";
            }

            if (text.Length != 7)
            {
                return false;
            }

            if (!byte.TryParse(text.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                !byte.TryParse(text.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
                !byte.TryParse(text.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            {
                return false;
            }

            normalizedHex = text.ToUpperInvariant();
            return true;
        }

        private static string? NormalizeComparableHex(string? value)
        {
            return TryNormalizeHexColor(value, out var normalizedHex) ? normalizedHex : null;
        }

        private static bool TryParseHexColor(string? value, out WpfColor color)
        {
            if (!TryNormalizeHexColor(value, out var normalizedHex))
            {
                color = WpfColors.Transparent;
                return false;
            }

            color = WpfColor.FromRgb(
                byte.Parse(normalizedHex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedHex.Substring(3, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                byte.Parse(normalizedHex.Substring(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
            return true;
        }

        private static string FormatColorHex(WpfColor color)
        {
            return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        private static WpfSolidColorBrush CreateFrozenBrush(WpfColor color)
        {
            var brush = new WpfSolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        private static WpfColor GetBestTextColorForBackground(WpfColor backgroundColor)
        {
            var whiteContrast = GetContrastRatio(WpfColors.White, backgroundColor);
            var blackContrast = GetContrastRatio(WpfColors.Black, backgroundColor);
            return whiteContrast >= blackContrast ? WpfColors.White : WpfColors.Black;
        }

        private static double GetContrastRatio(WpfColor foreground, WpfColor background)
        {
            var foregroundLuminance = GetRelativeLuminance(foreground);
            var backgroundLuminance = GetRelativeLuminance(background);
            var lighter = Math.Max(foregroundLuminance, backgroundLuminance);
            var darker = Math.Min(foregroundLuminance, backgroundLuminance);
            return (lighter + 0.05d) / (darker + 0.05d);
        }

        private static double GetRelativeLuminance(WpfColor color)
        {
            return (0.2126d * LinearizeSrgbChannel(color.R)) +
                   (0.7152d * LinearizeSrgbChannel(color.G)) +
                   (0.0722d * LinearizeSrgbChannel(color.B));
        }

        private static double LinearizeSrgbChannel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928d
                ? normalized / 12.92d
                : Math.Pow((normalized + 0.055d) / 1.055d, 2.4d);
        }

        private static bool SetPropertyIfAvailable(object target, string propertyName, object? value)
        {
            try
            {
                var propertyInfo = target.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);

                if (propertyInfo is null || !propertyInfo.CanWrite)
                {
                    return false;
                }

                propertyInfo.SetValue(target, value);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ClearDependencyPropertyIfAvailable(DependencyObject target, string dependencyPropertyFieldName)
        {
            try
            {
                var fieldInfo = target.GetType().GetField(
                    dependencyPropertyFieldName,
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);

                if (fieldInfo?.GetValue(null) is not DependencyProperty dependencyProperty)
                {
                    return false;
                }

                target.ClearValue(dependencyProperty);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // -------------------------------------------------------------------------
        // Debug menu / toolbar handlers
        // -------------------------------------------------------------------------

        private void DebugToggle_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel?.IsDebugSessionActive == true)
                StopDebug_Click(sender, e);
            else
                StartDebug_Click(sender, e);
        }

        private async void StartDebug_Click(object sender, RoutedEventArgs e)
        {
            using var debugScope = DeveloperDiagnostics.BeginScope(operationId: $"DebugStart-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogEventHandlerEntry(
                "Debugger",
                "StartDebug_Click",
                "Start Debug requested.",
                BuildDebugActionProperties(sender));
            TraceDebugShell("StartDebug_Click", $"Entry; senderType={sender?.GetType().Name ?? "(null)"}; {DescribeDebugUiState()}");
            if (ViewModel is null)
            {
                TraceDebugShell("StartDebug_Click", "Aborted because ViewModel is null.");
                DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Start Debug aborted because ViewModel was null.", "Rejected");
                return;
            }

            if (_debugSession is not null)
            {
                TraceDebugShell("StartDebug_Click", $"Existing session detected; currentState={_debugSession.CurrentState}; {DescribeDebugUiState()}");
                if (_debugSession.CurrentState == DebugSessionState.Paused)
                {
                    TraceDebugShell("StartDebug_Click", "Existing paused session will route to Continue.");
                    DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Existing paused session routed to Continue.", "ContinueExistingSession");
                    ContinueDebug_Click(sender, e);
                    return;
                }

                ViewModel.StatusText = "A debug session is already in progress — use Stop Debug to cancel it first";
                DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "A debug session was already active.", "Rejected");
                return;
            }

            if (ViewModel.SelectedTab is null)
            {
                ViewModel.StatusText = "Select a script before starting a debug session";
                RefreshDebugCommandAvailability(false);
                TraceDebugShell("StartDebug_Click", "Aborted because no selected tab exists.");
                DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Start Debug aborted because no selected tab exists.", "Rejected");
                return;
            }

            var runtime = ViewModel.EffectiveRuntimeInfo;
            if (runtime is null)
            {
                ViewModel.StatusText = "Select a PowerShell runtime before debugging";
                RefreshDebugCommandAvailability(false);
                TraceDebugShell("StartDebug_Click", "Aborted because no runtime is selected.");
                DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Start Debug aborted because no runtime is selected.", "Rejected");
                return;
            }

            var selectedTab = ViewModel.SelectedTab;
            if (string.IsNullOrWhiteSpace(selectedTab.Content))
            {
                ViewModel.StatusText = "The selected script is empty";
                RefreshDebugCommandAvailability(false);
                TraceDebugShell("StartDebug_Click", $"Aborted because selected tab '{selectedTab.Title}' is empty.");
                DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Start Debug aborted because the selected script was empty.", "Rejected");
                return;
            }

            try
            {
                var requiresPreLaunchCleanup =
                    _debugSession is not null ||
                    _activeDebugTab is not null ||
                    !string.IsNullOrWhiteSpace(_activeDebugLaunchPath) ||
                    !string.IsNullOrWhiteSpace(_activeDebugSnapshotPath);
                if (requiresPreLaunchCleanup)
                {
                    TraceDebugShell("StartDebug_Click", $"Cleaning up stale debug state before launch-plan creation; activeLaunchPathPresent={!string.IsNullOrWhiteSpace(_activeDebugLaunchPath)}; activeSnapshotPresent={!string.IsNullOrWhiteSpace(_activeDebugSnapshotPath)}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogDecision(
                        "Debugger",
                        "StartDebug_Click",
                        "Stale debug state was cleaned up before preparing a new debug launch plan.",
                        "CleanupBeforeLaunchPlan",
                        new Dictionary<string, object?>
                        {
                            ["activeLaunchPathPresent"] = !string.IsNullOrWhiteSpace(_activeDebugLaunchPath),
                            ["activeSnapshotPresent"] = !string.IsNullOrWhiteSpace(_activeDebugSnapshotPath),
                            ["activeTabPresent"] = _activeDebugTab is not null,
                            ["activeSessionPresent"] = _debugSession is not null
                        });
                    TearDownDebugSession();
                }

                ClearLiveDebugVariableCache("Start Debug preparing new session");

                if (!TryBuildDebugLaunchPlan(selectedTab, out var launchScriptPath))
                {
                    ViewModel.StatusText = "Unable to prepare the script for debugging";
                    RefreshDebugCommandAvailability(false);
                    TraceDebugShell("StartDebug_Click", $"TryBuildDebugLaunchPlan returned false for tab '{selectedTab.Title}'.");
                    DeveloperDiagnostics.LogDecision("Debugger", "StartDebug_Click", "Debug launch plan could not be prepared.", "Rejected");
                    return;
                }

                var breakpoints = CollectBreakpoints(launchScriptPath, selectedTab);
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug launch plan prepared.",
                    new Dictionary<string, object?>
                    {
                        ["launchScriptPath"] = launchScriptPath,
                        ["breakpointCount"] = breakpoints.Count,
                        ["selectedTabPath"] = selectedTab.FilePath,
                        ["selectedTabDirty"] = selectedTab.IsDirty
                    });
                TraceDebugShell("StartDebug_Click", $"Launch plan prepared; tab='{selectedTab.Title}'; launchPath='{Path.GetFileName(launchScriptPath)}'; breakpointCount={breakpoints.Count}; before session creation; {DescribeDebugUiState()}");
                var debugSession = new PsesDebugSession();
                _debugSession = debugSession;
                _activeDebugTab = selectedTab;
                _activeDebugLaunchPath = launchScriptPath;
                TraceDebugShell("StartDebug_Click", $"Created PsesDebugSession; sessionHash={debugSession.GetHashCode()}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogInfo("Debugger", "PsesDebugSession object created.", new Dictionary<string, object?> { ["sessionHash"] = debugSession.GetHashCode() });
                _debugSessionStateChangedHandler = state => Dispatcher.BeginInvoke(new Action(() =>
                {
                    HandleDebugSessionStateChanged(debugSession, state);
                }));
                debugSession.StateChanged += _debugSessionStateChangedHandler;

                debugSession.BreakpointHit += (scriptPath, lineNumber) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ViewModel is null || !ReferenceEquals(_debugSession, debugSession))
                    {
                        return;
                    }

                    TraceDebugShell("DebugSession.BreakpointHit", $"scriptPathPresent={!string.IsNullOrWhiteSpace(scriptPath)}; lineNumber={lineNumber}; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogStateTransition(
                        "Debugger",
                        "BreakpointHit",
                        debugSession.CurrentState.ToString(),
                        DebugSessionState.Paused.ToString(),
                        "Breakpoint hit received from debug session.",
                        new Dictionary<string, object?>
                        {
                            ["scriptPath"] = scriptPath,
                            ["lineNumber"] = lineNumber
                        });
                    ViewModel.StatusText = lineNumber > 0
                        ? $"Breakpoint hit — line {lineNumber}"
                        : "Breakpoint hit";

                    SetDebugCurrentLocation(scriptPath, lineNumber);
                    RefreshDebugCommandAvailability(true);
                    ScheduleDebugPanelRefresh("BreakpointHit");
                    RefreshBreakpointsList();
                }));

                debugSession.SessionEnded += () => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ViewModel is null || !ReferenceEquals(_debugSession, debugSession))
                    {
                        return;
                    }

                    TraceDebugShell("DebugSession.SessionEnded", $"SessionEnded fired; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogInfo("Debugger", "Debug session ended event received.");
                    TearDownDebugSession(DebugTeardownReason.SessionEndedEvent);
                    ViewModel.StatusText = "Debug session ended";
                }));

                debugSession.OutputReceived += chunk => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ViewModel is null || !ReferenceEquals(_debugSession, debugSession))
                    {
                        return;
                    }

                    var containsPromptMarker = chunk?.Contains("__PSS_DEBUG_PROMPT__", StringComparison.Ordinal) == true;
                    var containsEndedMarker = chunk?.Contains("__PSS_DEBUG_SESSION_ENDED__", StringComparison.Ordinal) == true;
                    var containsBreakpointText = chunk?.Contains("breakpoint", StringComparison.OrdinalIgnoreCase) == true;
                    var containsAtLine = chunk?.Contains(" line ", StringComparison.OrdinalIgnoreCase) == true;
                    TraceDebugShell(
                        "DebugSession.OutputReceived",
                        $"chunkLength={chunk?.Length ?? 0}; sessionState={debugSession.CurrentState}; containsPromptMarker={containsPromptMarker}; containsEndedMarker={containsEndedMarker}; containsBreakpointText={containsBreakpointText}; containsAtLine={containsAtLine}; {DescribeDebugUiState()}");
                    if (DeveloperDiagnostics.IsVerboseDebuggerEnabled())
                    {
                        DeveloperDiagnostics.LogDebug(
                            "Debugger",
                            "Debug output chunk received.",
                            new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(chunk))
                            {
                                ["containsPromptMarker"] = containsPromptMarker,
                                ["containsEndedMarker"] = containsEndedMarker,
                                ["containsBreakpointText"] = containsBreakpointText,
                                ["containsAtLine"] = containsAtLine
                            });
                    }
                    ViewModel.AppendDebugOutput(chunk ?? string.Empty);
                    if (!string.IsNullOrWhiteSpace(chunk))
                    {
                        _lastDebugOutputWrittenAtUtc = DateTimeOffset.UtcNow;
                    }

                    var condensed = string.IsNullOrWhiteSpace(chunk)
                        ? string.Empty
                        : chunk.Replace(Environment.NewLine, " ").Trim();

                    if (!string.IsNullOrWhiteSpace(condensed) && debugSession.CurrentState != DebugSessionState.Paused)
                    {
                        ViewModel.StatusText = condensed.Length > 120 ? condensed[..120] : condensed;
                    }
                }));
                TraceDebugShell("StartDebug_Click", $"Subscribed debug session events; sessionHash={debugSession.GetHashCode()}; {DescribeDebugUiState()}");

                try
                {
                    RefreshDebugCommandAvailability(false);
                    SetDebugPanelVisible(true);
                    RefreshBreakpointsList();
                    ViewModel.StatusText = $"Starting debug session — {Path.GetFileName(selectedTab.FilePath ?? selectedTab.Title)}";
                    var launchScriptExists = File.Exists(launchScriptPath);
                    TraceDebugShell("StartDebug_Click", $"Before StartAsync; sessionHash={debugSession.GetHashCode()}; launchPath='{Path.GetFileName(launchScriptPath)}'; launchScriptExists={launchScriptExists}; breakpointCount={breakpoints.Count}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogInfo(
                        "Debugger",
                        "Verified debug launch script existence before StartAsync.",
                        new Dictionary<string, object?>
                        {
                            ["launchScriptPath"] = launchScriptPath,
                            ["launchScriptExists"] = launchScriptExists,
                            ["sessionHash"] = debugSession.GetHashCode()
                        });

                    await debugSession.StartAsync(runtime, launchScriptPath, breakpoints).ConfigureAwait(true);
                    TraceDebugShell("StartDebug_Click", $"After StartAsync; sessionHash={debugSession.GetHashCode()}; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogInfo("Debugger", "PsesDebugSession.StartAsync completed.", new Dictionary<string, object?> { ["sessionState"] = debugSession.CurrentState.ToString() });

                    if (!ReferenceEquals(_debugSession, debugSession))
                    {
                        TraceDebugShell("StartDebug_Click", $"Session reference changed after StartAsync; sessionHash={debugSession.GetHashCode()}.");
                        return;
                    }

                    var isPaused = debugSession.CurrentState == DebugSessionState.Paused;
                    RefreshDebugCommandAvailability(isPaused);
                    TraceDebugShell("StartDebug_Click", $"Post-StartAsync refresh; isPaused={isPaused}; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                    if (!isPaused)
                    {
                        ViewModel.StatusText = breakpoints.Count == 0
                            ? $"Debug session started — {Path.GetFileName(selectedTab.FilePath ?? selectedTab.Title)} (no breakpoints set)"
                            : $"Debug session started — {Path.GetFileName(selectedTab.FilePath ?? selectedTab.Title)}";
                    }
                }
                catch (Exception ex)
                {
                    TraceDebugShell("StartDebug_Click", $"StartAsync failed; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogException("Debugger", ex, "Debug session start failed.");
                    TearDownDebugSession(DebugTeardownReason.StartFailure);
                    ViewModel.StatusText = $"Debug start failed: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                TraceDebugShell("StartDebug_Click", $"Preparation failed; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogException("Debugger", ex, "Debug preparation failed.");
                TearDownDebugSession(DebugTeardownReason.PreparationFailure);
                ViewModel.StatusText = $"Debug preparation failed: {ex.Message}";
            }
            finally
            {
                DeveloperDiagnostics.LogEventHandlerExit("Debugger", "StartDebug_Click", "Start Debug handler exited.", BuildDebugActionProperties(sender));
            }
        }

        private async void StepInto_Click(object sender, RoutedEventArgs e)
        {
            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"StepInto-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction("Debugger", "DebuggerCommand", "Step Into requested.", BuildDebugActionProperties(sender));
            TraceDebugShell("StepInto_Click", $"Entry; {DescribeDebugUiState()}");
            if (_debugSession?.CurrentState == DebugSessionState.Paused)
            {
                RefreshDebugCommandAvailability(false);
                ClearDebugCurrentLine();
                InvalidateDebugPanelRefresh("StepInto requested");
                ClearLiveDebugVariableCache("StepInto requested");
                if (ViewModel is not null) ViewModel.StatusText = "Stepping in...";
                await ExecuteDebugControlAsync(_debugSession, session => session.StepIntoAsync(), "Step Into failed").ConfigureAwait(true);
            }
        }

        private async void StepOver_Click(object sender, RoutedEventArgs e)
        {
            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"StepOver-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction("Debugger", "DebuggerCommand", "Step Over requested.", BuildDebugActionProperties(sender));
            TraceDebugShell("StepOver_Click", $"Entry; {DescribeDebugUiState()}");
            if (_debugSession?.CurrentState == DebugSessionState.Paused)
            {
                RefreshDebugCommandAvailability(false);
                ClearDebugCurrentLine();
                InvalidateDebugPanelRefresh("StepOver requested");
                ClearLiveDebugVariableCache("StepOver requested");
                if (ViewModel is not null) ViewModel.StatusText = "Stepping over...";
                await ExecuteDebugControlAsync(_debugSession, session => session.StepOverAsync(), "Step Over failed").ConfigureAwait(true);
            }
        }

        private async void StepOut_Click(object sender, RoutedEventArgs e)
        {
            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"StepOut-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction("Debugger", "DebuggerCommand", "Step Out requested.", BuildDebugActionProperties(sender));
            TraceDebugShell("StepOut_Click", $"Entry; {DescribeDebugUiState()}");
            if (_debugSession?.CurrentState == DebugSessionState.Paused)
            {
                RefreshDebugCommandAvailability(false);
                ClearDebugCurrentLine();
                InvalidateDebugPanelRefresh("StepOut requested");
                ClearLiveDebugVariableCache("StepOut requested");
                if (ViewModel is not null) ViewModel.StatusText = "Stepping out...";
                await ExecuteDebugControlAsync(_debugSession, session => session.StepOutAsync(), "Step Out failed").ConfigureAwait(true);
            }
        }

        private async void ContinueDebug_Click(object? sender, RoutedEventArgs e)
        {
            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"Continue-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction("Debugger", "DebuggerCommand", "Continue requested.", BuildDebugActionProperties(sender));
            TraceDebugShell("ContinueDebug_Click", $"Entry; {DescribeDebugUiState()}");
            if (_debugSession?.CurrentState == DebugSessionState.Paused)
            {
                RefreshDebugCommandAvailability(false);
                ClearDebugCurrentLine();
                InvalidateDebugPanelRefresh("Continue requested");
                ClearLiveDebugVariableCache("Continue requested");
                if (ViewModel is not null) ViewModel.StatusText = "Continuing...";
                await ExecuteDebugControlAsync(_debugSession, session => session.ContinueAsync(), "Continue failed").ConfigureAwait(true);
            }
        }

        private void StopDebug_Click(object sender, RoutedEventArgs e)
        {
            using var scope = DeveloperDiagnostics.BeginScope(operationId: $"StopDebug-{Guid.NewGuid():N}");
            DeveloperDiagnostics.LogUserAction("Debugger", "DebuggerCommand", "Stop Debug requested.", BuildDebugActionProperties(sender));
            TraceDebugShell("StopDebug_Click", $"Entry; {DescribeDebugUiState()}");
            if (ViewModel is null || _debugSession is null)
            {
                TraceDebugShell("StopDebug_Click", "Ignored because ViewModel or debug session is null.");
                return;
            }

            InvalidateDebugPanelRefresh("Stop Debug requested");
            ClearLiveDebugVariableCache("Stop Debug requested");
            TearDownDebugSession(DebugTeardownReason.UserStop);
            ViewModel.StatusText = "Debug session stopped";
            TraceDebugShell("StopDebug_Click", $"Completed stop request; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogInfo("Debugger", "Debug session stop request completed.");
        }

        private List<DebugBreakpointInfo> CollectBreakpoints(string launchScriptPath, EditorTabViewModel launchTab)
        {
            var breakpoints = new List<DebugBreakpointInfo>();
            if (ViewModel is null)
            {
                return breakpoints;
            }

            foreach (var tab in ViewModel.OpenTabs)
            {
                var scriptPathForTab = ReferenceEquals(tab, launchTab)
                    ? launchScriptPath
                    : tab.FilePath;

                if (string.IsNullOrWhiteSpace(scriptPathForTab))
                {
                    continue;
                }

                foreach (var line in tab.GetEnabledBreakpointLines())
                {
                    breakpoints.Add(new DebugBreakpointInfo(scriptPathForTab, line));
                }
            }

            return breakpoints;
        }

        private bool CanStartDebugSession()
        {
            if (_debugSession is not null || ViewModel?.SelectedTab is null)
            {
                return false;
            }

            if (ViewModel.EffectiveRuntimeInfo is null)
            {
                return false;
            }

            return !string.IsNullOrWhiteSpace(ViewModel.SelectedTab.Content);
        }

        private bool TryBuildDebugLaunchPlan(EditorTabViewModel tab, out string launchScriptPath)
        {
            DeveloperDiagnostics.LogMethodEntry(
                "Debugger",
                "TryBuildDebugLaunchPlan entered.",
                new Dictionary<string, object?>
                {
                    ["activeDocumentPath"] = tab.FilePath,
                    ["isDocumentDirty"] = tab.IsDirty,
                    ["isUnsaved"] = string.IsNullOrWhiteSpace(tab.FilePath)
                });
            launchScriptPath = string.Empty;

            var existingSnapshot = _activeDebugSnapshotPath;
            if (!string.IsNullOrWhiteSpace(existingSnapshot) && File.Exists(existingSnapshot))
            {
                TryDeleteTemporaryDebugSnapshot(existingSnapshot);
            }

            _activeDebugSnapshotPath = null;

            if (TryPrepareSavedScriptPathForDebug(tab, out var savedScriptPath))
            {
                launchScriptPath = savedScriptPath;
                DeveloperDiagnostics.LogDecision("Debugger", "TryBuildDebugLaunchPlan", "Saved file path will be used for debug launch.", "UseSavedPath", new Dictionary<string, object?> { ["launchScriptPath"] = savedScriptPath });
                return true;
            }

            var safeName = string.IsNullOrWhiteSpace(tab.Title) ? "Untitled" : tab.Title;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                safeName = safeName.Replace(invalid, '_');
            }

            if (!AppTemporaryStorage.TryGetManagedRootDirectory("DebugSnapshots", createIfMissing: true, out var debugSnapshotRoot, out var failureReason))
            {
                throw new IOException($"Debug snapshot storage is unavailable. {failureReason}");
            }

            var snapshotPath = Path.Combine(
                debugSnapshotRoot,
                $"PS7ScriptDesk_Debug_{safeName}_{Guid.NewGuid():N}.ps1");

            File.WriteAllText(snapshotPath, tab.Content ?? string.Empty);
            _activeDebugSnapshotPath = snapshotPath;
            launchScriptPath = snapshotPath;
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Temporary debug snapshot created.",
                new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(tab.Content))
                {
                    ["snapshotPath"] = snapshotPath
                });
            return true;
        }

        private bool TryPrepareSavedScriptPathForDebug(EditorTabViewModel tab, out string savedScriptPath)
        {
            savedScriptPath = string.Empty;

            if (tab.IsDirty || string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return false;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(tab.FilePath);
            }
            catch (Exception ex)
            {
                MarkTabStaleForDebugSnapshot(tab, $"its saved path is invalid: {ex.Message}");
                return false;
            }

            if (!string.Equals(Path.GetExtension(normalizedPath), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!File.Exists(normalizedPath))
            {
                MarkTabStaleForDebugSnapshot(tab, $"the saved file no longer exists at {normalizedPath}");
                return false;
            }

            try
            {
                var diskContent = File.ReadAllText(normalizedPath);
                if (string.Equals(diskContent, tab.Content ?? string.Empty, StringComparison.Ordinal))
                {
                    savedScriptPath = normalizedPath;
                    return true;
                }

                MarkTabStaleForDebugSnapshot(tab, $"the visible editor content no longer matches {normalizedPath}");
                return false;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                MarkTabStaleForDebugSnapshot(tab, $"the saved file could not be read before debugging: {ex.Message}");
                return false;
            }
        }

        private void MarkTabStaleForDebugSnapshot(EditorTabViewModel tab, string reason)
        {
            tab.MarkExternallyStale();

            var viewModel = ViewModel;
            if (viewModel is not null)
            {
                viewModel.StatusText = "Saved file changed; debugging visible editor content";
                viewModel.RefreshCommandStates();
            }

            AppLogger.Warning("Debug", $"Saved script path was not used for Debug because {reason}. Tab='{tab.Title}'. Visible editor content will be debugged from a temporary snapshot.");
        }

        private void TryDeleteTemporaryDebugSnapshot(string? snapshotPath)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath))
            {
                return;
            }

            if (!AppTemporaryStorage.TryGetManagedRootDirectory("DebugSnapshots", createIfMissing: false, out var debugSnapshotRoot, out var rootFailureReason))
            {
                AppLogger.Warning("Debug", $"Skipped debug snapshot cleanup because the managed temp root could not be resolved. Path='{snapshotPath}'. {rootFailureReason}");
                return;
            }

            if (!AppTemporaryStorage.TryValidateManagedPath(debugSnapshotRoot, snapshotPath, out _, out var normalizedSnapshotPath, out var validationFailureReason))
            {
                AppLogger.Warning("Debug", $"Skipped debug snapshot cleanup outside the managed temp root. Path='{snapshotPath}'. {validationFailureReason}");
                return;
            }

            try
            {
                if (File.Exists(normalizedSnapshotPath))
                {
                    File.Delete(normalizedSnapshotPath);
                    AppLogger.Info("Debug", $"Deleted debug snapshot '{Path.GetFileName(normalizedSnapshotPath)}' from '{debugSnapshotRoot}'.");
                    DeveloperDiagnostics.LogInfo("Debugger", "Temporary debug snapshot deleted.", new Dictionary<string, object?> { ["snapshotPath"] = normalizedSnapshotPath });
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warning("Debug", $"Failed to delete debug snapshot '{normalizedSnapshotPath}'. {ex.Message}");
                DeveloperDiagnostics.LogException("Debugger", ex, "Failed to delete temporary debug snapshot.", new Dictionary<string, object?> { ["snapshotPath"] = normalizedSnapshotPath });
            }
        }

        private void RefreshDebugCommandAvailability(bool paused)
        {
            var hasSession = _debugSession is not null && _debugSession.CurrentState != DebugSessionState.Stopped;
            var canStart = !hasSession && CanStartDebugSession();

            StartDebugMenuItem.IsEnabled  = canStart;
            DebugToggleButton.IsEnabled   = canStart || hasSession;
            StepIntoMenuItem.IsEnabled    = hasSession && paused;
            StepOverMenuItem.IsEnabled    = hasSession && paused;
            StepOutMenuItem.IsEnabled     = hasSession && paused;
            ContinueMenuItem.IsEnabled    = hasSession && paused;
            StopDebugMenuItem.IsEnabled   = hasSession;
            StepIntoButton.IsEnabled      = hasSession && paused;
            StepOverButton.IsEnabled      = hasSession && paused;
            StepOutButton.IsEnabled       = hasSession && paused;
            ContinueButton.IsEnabled      = hasSession && paused;

            // Keep the ViewModel in sync so CanRunScript() can block the Run button
            // while a debug session is active.
            if (ViewModel is not null)
            {
                ViewModel.IsDebugSessionActive = hasSession;
            }

            TraceDebugShell("RefreshDebugCommandAvailability", $"pausedArgument={paused}; hasSession={hasSession}; canStart={canStart}; {DescribeDebugUiState()}");
            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseUiEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "UI",
                    "Debug command availability refreshed.",
                    new Dictionary<string, object?>
                    {
                        ["paused"] = paused,
                        ["hasSession"] = hasSession,
                        ["canStart"] = canStart,
                        ["startEnabled"] = StartDebugMenuItem.IsEnabled,
                        ["stepIntoEnabled"] = StepIntoMenuItem.IsEnabled,
                        ["stepOverEnabled"] = StepOverMenuItem.IsEnabled,
                        ["stepOutEnabled"] = StepOutMenuItem.IsEnabled,
                        ["continueEnabled"] = ContinueMenuItem.IsEnabled,
                        ["stopEnabled"] = StopDebugMenuItem.IsEnabled
                    });
            }
        }

        private void SetDebugControlsEnabled(bool paused)
        {
            RefreshDebugCommandAvailability(paused);
        }

        private async Task ExecuteDebugControlAsync(
            IDebugSession? debugSession,
            Func<IDebugSession, Task> debugAction,
            string failureStatusPrefix)
        {
            if (debugSession is null || !ReferenceEquals(_debugSession, debugSession))
            {
                TraceDebugShell("ExecuteDebugControlAsync", $"Skipped because session mismatch/null. activeMatches={ReferenceEquals(_debugSession, debugSession)}; {DescribeDebugUiState()}");
                return;
            }

            try
            {
                TraceDebugShell("ExecuteDebugControlAsync", $"Dispatching control action; failureStatusPrefix='{failureStatusPrefix}'; sessionStateBefore={debugSession.CurrentState}; {DescribeDebugUiState()}");
                await debugAction(debugSession).ConfigureAwait(true);
                TraceDebugShell("ExecuteDebugControlAsync", $"Control action completed without exception; failureStatusPrefix='{failureStatusPrefix}'; sessionStateAfter={debugSession.CurrentState}; {DescribeDebugUiState()}");
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_debugSession, debugSession))
                {
                    TraceDebugShell("ExecuteDebugControlAsync", $"Exception after session changed; exceptionType={ex.GetType().Name}; message={ex.Message}");
                    return;
                }

                RefreshDebugCommandAvailability(debugSession.CurrentState == DebugSessionState.Paused);
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"{failureStatusPrefix}: {ex.Message}";
                }

                TraceDebugShell("ExecuteDebugControlAsync", $"Control action failed; failureStatusPrefix='{failureStatusPrefix}'; exceptionType={ex.GetType().Name}; message={ex.Message}; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
            }
        }

        private void HandleDebugSessionStateChanged(IDebugSession debugSession, DebugSessionState state)
        {
            var currentSessionState = _debugSession?.CurrentState.ToString() ?? "(null)";
            TraceDebugShell("HandleDebugSessionStateChanged", $"Received state change; incomingState={state}; sessionMatches={ReferenceEquals(_debugSession, debugSession)}; currentSessionState={currentSessionState}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogStateTransition("Debugger", "DebugSessionStateChanged", currentSessionState, state.ToString(), "Debug session state changed.");
            if (!ReferenceEquals(_debugSession, debugSession))
            {
                return;
            }

            if (state == DebugSessionState.Stopped)
            {
                TearDownDebugSession(DebugTeardownReason.SessionStoppedState);
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = "Debug session ended";
                }

                TraceDebugShell("HandleDebugSessionStateChanged", $"Handled stopped state; {DescribeDebugUiState()}");
                return;
            }

            var isPaused = state == DebugSessionState.Paused;
            RefreshDebugCommandAvailability(isPaused);

            if (isPaused && ViewModel is not null)
            {
                ViewModel.StatusText = "Debug session paused — choose Continue, Step Over, Step Into, Step Out, or Stop Debug";
                ScheduleDebugPanelRefresh("StateChangedPaused");
            }
            else
            {
                ClearLiveDebugVariableCache($"Debug session state changed to {state}");
            }
        }

        private void TearDownDebugSession(DebugTeardownReason reason = DebugTeardownReason.PreLaunchCleanup)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var millisecondsSinceLastOutput = _lastDebugOutputWrittenAtUtc == DateTimeOffset.MinValue
                ? (double?)null
                : (nowUtc - _lastDebugOutputWrittenAtUtc).TotalMilliseconds;
            var debugOutputRecentlyWritten =
                millisecondsSinceLastOutput.HasValue &&
                millisecondsSinceLastOutput.Value >= 0 &&
                millisecondsSinceLastOutput.Value <= DebugOutputPreservationWindowMilliseconds;
            var endedNaturally =
                reason == DebugTeardownReason.SessionEndedEvent ||
                reason == DebugTeardownReason.SessionStoppedState;
            var shouldPreserveVisibleTranscript =
                debugOutputRecentlyWritten &&
                (endedNaturally || reason == DebugTeardownReason.UserStop);
            var skipImmediateConsoleRestore = endedNaturally || reason == DebugTeardownReason.UserStop;
            var skipImmediateTerminalFocus = endedNaturally || reason == DebugTeardownReason.UserStop;

            TraceDebugShell(
                "TearDownDebugSession",
                $"Entry; reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}; shouldPreserveVisibleTranscript={shouldPreserveVisibleTranscript}; millisecondsSinceLastOutput={(millisecondsSinceLastOutput?.ToString("F0", CultureInfo.InvariantCulture) ?? "(none)")}; skipImmediateConsoleRestore={skipImmediateConsoleRestore}; skipImmediateTerminalFocus={skipImmediateTerminalFocus}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogMethodEntry(
                "Debugger",
                "TearDownDebugSession entered.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["endedNaturally"] = endedNaturally,
                    ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                    ["shouldPreserveVisibleTranscript"] = shouldPreserveVisibleTranscript,
                    ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput,
                    ["skipImmediateConsoleRestore"] = skipImmediateConsoleRestore,
                    ["skipImmediateTerminalFocus"] = skipImmediateTerminalFocus
                });

            if (shouldPreserveVisibleTranscript)
            {
                TerminalConsole.PreserveVisibleTranscriptFor(
                    TimeSpan.FromMilliseconds(DebugOutputPreservationWindowMilliseconds),
                    $"Debug teardown reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}");
                TraceDebugShell(
                    "TearDownDebugSession",
                    $"Activated terminal transcript preservation; durationMs={DebugOutputPreservationWindowMilliseconds}; reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}; millisecondsSinceLastOutput={(millisecondsSinceLastOutput?.ToString("F0", CultureInfo.InvariantCulture) ?? "(none)")};");
                DeveloperDiagnostics.LogDecision(
                    "Debugger",
                    "TearDownDebugSession",
                    "Terminal transcript preservation was activated before debug teardown completed.",
                    "ActivateTranscriptPreservation",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["endedNaturally"] = endedNaturally,
                        ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                        ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput,
                        ["durationMs"] = DebugOutputPreservationWindowMilliseconds
                    });

                if (endedNaturally && ViewModel is not null)
                {
                    var promptText = BuildVisiblePromptTextForDebugCompletion();
                    TerminalConsole.RestoreVisiblePromptAfterDebug(
                        promptText,
                        $"Debug teardown prompt restore; reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}");
                    TraceDebugShell(
                        "TearDownDebugSession",
                        $"Requested non-destructive visible prompt restore; promptPreview='{DeveloperDiagnostics.SanitizePreview(promptText)}'; reason={reason}; endedNaturally={endedNaturally};");
                    DeveloperDiagnostics.LogDecision(
                        "Debugger",
                        "TearDownDebugSession",
                        "Non-destructive visible prompt restoration was requested after natural debug completion.",
                        "RequestVisiblePromptRestoreAfterDebug",
                        new Dictionary<string, object?>(DeveloperDiagnostics.CreateTextMetadata(promptText))
                        {
                            ["reason"] = reason.ToString(),
                            ["endedNaturally"] = endedNaturally,
                            ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                            ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput
                        });
                }
                else
                {
                    DeveloperDiagnostics.LogDecision(
                        "Debugger",
                        "TearDownDebugSession",
                        "Visible prompt restoration was skipped because debug completion was not natural or the view model was unavailable.",
                        "SkipVisiblePromptRestoreAfterDebug",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = reason.ToString(),
                            ["endedNaturally"] = endedNaturally,
                            ["viewModelAvailable"] = ViewModel is not null
                        });
                }
            }
            if (_debugSession is not null && _debugSessionStateChangedHandler is not null)
            {
                _debugSession.StateChanged -= _debugSessionStateChangedHandler;
            }

            _debugSessionStateChangedHandler = null;
            Interlocked.Increment(ref _debugPanelRefreshVersion);
            _debugSession?.Dispose();
            _debugSession = null;
            _activeDebugTab = null;
            _activeDebugLaunchPath = null;
            var snapshotToDelete = _activeDebugSnapshotPath;
            _activeDebugSnapshotPath = null;
            TryDeleteTemporaryDebugSnapshot(snapshotToDelete);
            RefreshDebugCommandAvailability(false);
            ClearDebugCurrentLine();
            ClearDebugPanels();
            SetDebugPanelVisible(false);
            if (ViewModel is not null)
            {
                if (skipImmediateConsoleRestore)
                {
                    TraceDebugShell(
                        "TearDownDebugSession",
                        $"Skipped EnsureConsoleRestoredAsync; reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}; millisecondsSinceLastOutput={(millisecondsSinceLastOutput?.ToString("F0", CultureInfo.InvariantCulture) ?? "(none)")};");
                    DeveloperDiagnostics.LogDecision(
                        "Debugger",
                        "TearDownDebugSession",
                        "Immediate console restore was skipped to preserve visible debug transcript output.",
                        "SkipImmediateConsoleRestore",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = reason.ToString(),
                            ["endedNaturally"] = endedNaturally,
                            ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                            ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput
                        });
                }
                else
                {
                    _ = ViewModel.EnsureConsoleRestoredAsync();
                    TraceDebugShell("TearDownDebugSession", $"Requested EnsureConsoleRestoredAsync; reason={reason}; debugOutputRecentlyWritten={debugOutputRecentlyWritten};");
                    DeveloperDiagnostics.LogDecision(
                        "Debugger",
                        "TearDownDebugSession",
                        "Immediate console restore was requested during debug teardown.",
                        "RestoreConsoleImmediately",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = reason.ToString(),
                            ["endedNaturally"] = endedNaturally,
                            ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                            ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput
                        });
                }
            }

            if (skipImmediateTerminalFocus)
            {
                TraceDebugShell(
                    "TearDownDebugSession",
                    $"Skipped TerminalConsole.FocusTerminal; reason={reason}; endedNaturally={endedNaturally}; debugOutputRecentlyWritten={debugOutputRecentlyWritten}; millisecondsSinceLastOutput={(millisecondsSinceLastOutput?.ToString("F0", CultureInfo.InvariantCulture) ?? "(none)")};");
                DeveloperDiagnostics.LogDecision(
                    "Debugger",
                    "TearDownDebugSession",
                    "Immediate terminal focus was skipped to avoid a prompt redraw overwriting recent debug output.",
                    "SkipImmediateTerminalFocus",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["endedNaturally"] = endedNaturally,
                        ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                        ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput
                    });
            }
            else
            {
                TerminalConsole.FocusTerminal();
                TraceDebugShell("TearDownDebugSession", $"Requested TerminalConsole.FocusTerminal; reason={reason}; debugOutputRecentlyWritten={debugOutputRecentlyWritten};");
                DeveloperDiagnostics.LogDecision(
                    "Debugger",
                    "TearDownDebugSession",
                    "Immediate terminal focus was requested during debug teardown.",
                    "FocusTerminalImmediately",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason.ToString(),
                        ["endedNaturally"] = endedNaturally,
                        ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                        ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput
                    });
            }

            TraceDebugShell("TearDownDebugSession", $"Completed; reason={reason}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogMethodExit(
                "Debugger",
                "TearDownDebugSession completed.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["endedNaturally"] = endedNaturally,
                    ["debugOutputRecentlyWritten"] = debugOutputRecentlyWritten,
                    ["shouldPreserveVisibleTranscript"] = shouldPreserveVisibleTranscript,
                    ["millisecondsSinceLastOutput"] = millisecondsSinceLastOutput,
                    ["skipImmediateConsoleRestore"] = skipImmediateConsoleRestore,
                    ["skipImmediateTerminalFocus"] = skipImmediateTerminalFocus
                });
        }

        private string BuildVisiblePromptTextForDebugCompletion()
        {
            var promptText = ViewModel?.ConsolePromptText?.Trim();
            if (string.IsNullOrWhiteSpace(promptText))
            {
                return "PS>";
            }

            return string.Equals(promptText, "PS >", StringComparison.Ordinal)
                ? "PS>"
                : promptText;
        }

        private void TraceDebugShell(string source, string message)
        {
            DebuggerTraceLogger.Write($"MainWindow.{source}", message);
        }

        private string DescribeDebugUiState()
        {
            var sessionState = _debugSession?.CurrentState.ToString() ?? "(null)";

            if (!Dispatcher.CheckAccess())
            {
                return
                    $"debugSessionNull={(_debugSession is null)}; " +
                    $"debugSessionState={sessionState}; " +
                    "uiThreadAccess=False";
            }

            var isDebugSessionActive = ViewModel?.IsDebugSessionActive.ToString() ?? "(null)";

            return
                $"debugSessionNull={(_debugSession is null)}; " +
                $"debugSessionState={sessionState}; " +
                $"isDebugSessionActive={isDebugSessionActive}; " +
                $"startDebugMenuEnabled={StartDebugMenuItem.IsEnabled}; " +
                $"stopDebugMenuEnabled={StopDebugMenuItem.IsEnabled}; " +
                $"continueMenuEnabled={ContinueMenuItem.IsEnabled}; " +
                $"stepOverMenuEnabled={StepOverMenuItem.IsEnabled}; " +
                $"stepIntoMenuEnabled={StepIntoMenuItem.IsEnabled}; " +
                $"stepOutMenuEnabled={StepOutMenuItem.IsEnabled}; " +
                $"debugToggleEnabled={DebugToggleButton.IsEnabled}; " +
                $"continueButtonEnabled={ContinueButton.IsEnabled}; " +
                $"stepOverButtonEnabled={StepOverButton.IsEnabled}; " +
                $"stepIntoButtonEnabled={StepIntoButton.IsEnabled}; " +
                $"stepOutButtonEnabled={StepOutButton.IsEnabled}";
        }

        private void OpenFindReplaceWindow(bool showReplace)
        {
            _findReplaceWindow ??= new FindReplaceWindow(this, _lastFindText, _lastReplaceText, _lastFindMatchCase);
            _findReplaceWindow.Title = showReplace ? "Replace" : "Find";
            _findReplaceWindow.FindText = _lastFindText;
            _findReplaceWindow.ReplaceText = _lastReplaceText;
            _findReplaceWindow.MatchCase = _lastFindMatchCase;
            _findReplaceWindow.WholeWord = _lastFindWholeWord;
            _findReplaceWindow.UseRegex = _lastFindUseRegex;
            _findReplaceWindow.ShowStatus(null);
            _findReplaceWindow.Show();
            _findReplaceWindow.Activate();
        }

        // Throws ArgumentException for invalid regex patterns.
        private static Regex BuildFindRegex(string findText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var pattern = useRegex ? findText : Regex.Escape(findText);
            if (wholeWord) pattern = $@"\b{pattern}\b";
            var options = RegexOptions.None;
            if (!matchCase) options |= RegexOptions.IgnoreCase;
            return new Regex(pattern, options);
        }

        private bool TryFindNext(TextEditor editorTextEditor, string findText, bool matchCase, bool wholeWord, bool useRegex, bool forward)
        {
            var text = editorTextEditor.Text ?? string.Empty;
            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);

            int searchFrom;
            if (forward)
            {
                searchFrom = editorTextEditor.SelectionLength > 0
                    ? editorTextEditor.SelectionStart + editorTextEditor.SelectionLength
                    : editorTextEditor.CaretOffset;
            }
            else
            {
                searchFrom = editorTextEditor.SelectionLength > 0
                    ? editorTextEditor.SelectionStart
                    : editorTextEditor.CaretOffset;
            }
            searchFrom = Math.Clamp(searchFrom, 0, text.Length);

            Match m;
            if (forward)
            {
                m = rx.Match(text, searchFrom);
                if (!m.Success && searchFrom > 0)
                    m = rx.Match(text, 0, searchFrom);
            }
            else
            {
                // Find last match before searchFrom — collect all matches up to that point.
                var allMatches = rx.Matches(text);
                m = Match.Empty;
                foreach (Match candidate in allMatches)
                {
                    if (candidate.Index < searchFrom)
                        m = candidate;
                }
                if (!m.Success)
                {
                    // Wrap: take the last match in the whole document.
                    foreach (Match candidate in allMatches)
                        m = candidate;
                }
            }

            if (!m.Success)
            {
                if (ViewModel is not null)
                    ViewModel.StatusText = "Search text was not found";
                return false;
            }

            editorTextEditor.Select(m.Index, m.Length);
            editorTextEditor.ScrollTo(editorTextEditor.Document.GetLineByOffset(m.Index).LineNumber, 1);
            editorTextEditor.CaretOffset = forward ? m.Index + m.Length : m.Index;
            editorTextEditor.Focus();

            if (ViewModel is not null)
                ViewModel.StatusText = $"Found '{findText}'";

            return true;
        }

        private bool TryReplaceCurrent(TextEditor editorTextEditor, string findText, string replaceText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var selectedText = editorTextEditor.SelectedText ?? string.Empty;
            var m = rx.Match(selectedText);
            if (!m.Success || m.Index != 0 || m.Length != selectedText.Length)
                return false;

            var selectionStart = editorTextEditor.SelectionStart;
            var replacement = useRegex ? m.Result(replaceText ?? string.Empty) : replaceText ?? string.Empty;
            editorTextEditor.Document.Replace(selectionStart, editorTextEditor.SelectionLength, replacement);
            editorTextEditor.Select(selectionStart, replacement.Length);
            editorTextEditor.CaretOffset = selectionStart + replacement.Length;
            editorTextEditor.Focus();

            if (ViewModel is not null)
                ViewModel.StatusText = $"Replaced '{findText}'";

            return true;
        }

        private int ReplaceAll(TextEditor editorTextEditor, string findText, string replaceText, bool matchCase, bool wholeWord, bool useRegex)
        {
            var originalText = editorTextEditor.Text ?? string.Empty;
            if (string.IsNullOrEmpty(originalText) || string.IsNullOrEmpty(findText))
                return 0;

            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var replacement = replaceText ?? string.Empty;
            var replacements = 0;

            var newText = rx.Replace(originalText, m => { replacements++; return useRegex ? m.Result(replacement) : replacement; });

            if (replacements == 0)
                return 0;

            editorTextEditor.Text = newText;
            editorTextEditor.CaretOffset = Math.Min(editorTextEditor.Text.Length, editorTextEditor.CaretOffset);
            editorTextEditor.Focus();
            return replacements;
        }

        // ConsoleOutputBox_TextChanged, ConsoleOutputBox_SizeChanged, and
        // ConsoleCommandBox_KeyDown have been removed: the TextBox and command-
        // input row were replaced by TerminalControl (xterm.js inside WebView2).
        // Resize and input events now flow through TerminalControl.TerminalResized
        // and TerminalControl.UserInput, wired in Window_Loaded.

        // NavigateCommandHistory removed: command history navigation is now handled
        // natively by xterm.js (Up/Down arrow keys in the terminal).

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            if (!TryPrepareForWindowClose())
            {
                return;
            }

            _diagnosticsService.Dispose();
            _intelliSenseService.Dispose();
            _activeCompletionCts?.Cancel();
            _activeCompletionCts?.Dispose();
            CancelActiveQuickInfoRequest();
            CloseActiveEditorToolTip();
            _allowWindowClose = true;
            Close();
        }

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            DeveloperDiagnostics.LogEventHandlerEntry("UI", "Window_Closing", "Window_Closing entered.");
            if (ViewModel is not null)
            {
                ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            }

            _intelliSenseService.MetadataWarmupStatusChanged -= IntelliSenseService_MetadataWarmupStatusChanged;

            if (_allowWindowClose)
            {
                _debugPaneWindow?.CloseForOwnerShutdown();
                _diagnosticsService.Dispose();
                _intelliSenseService.Dispose();
                _activeCompletionCts?.Cancel();
                _activeCompletionCts?.Dispose();
                CancelActiveQuickInfoRequest();
                CloseActiveEditorToolTip();
                DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_Closing", "Window_Closing exited on final close path.");
                return;
            }

            if (!TryPrepareForWindowClose())
            {
                e.Cancel = true;
                return;
            }

            _allowWindowClose = true;
            DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_Closing", "Window_Closing prepared app for close.");
        }

        private bool TryPrepareForWindowClose()
        {
            if (ViewModel is null)
            {
                return true;
            }

            DeveloperDiagnostics.LogInfo("Startup", "Preparing for window close.");

            if (!ViewModel.TryPrepareForApplicationClose())
            {
                return false;
            }

            // Ensure an owned debug PowerShell process cannot continue after a
            // normal user-initiated app close.  This deliberately runs only after
            // unsaved-work prompts have allowed the close to proceed.
            if (_debugSession is not null)
            {
                TearDownDebugSession();
            }

            try
            {
                SaveApplicationSettings();
            }
            catch
            {
                // Best effort persistence only. The application should still be allowed to close.
                DeveloperDiagnostics.LogWarning("Settings", "SaveApplicationSettings failed during window close.");
            }

            return true;
        }

        private void ApplyShellLayoutFromSettings()
        {
            if (_shellLayoutApplied)
            {
                return;
            }

            _shellLayoutApplied = true;

            if (IsUsableLength(_loadedSettings.WindowWidth, MinWidth))
            {
                Width = _loadedSettings.WindowWidth!.Value;
            }

            if (IsUsableLength(_loadedSettings.WindowHeight, MinHeight))
            {
                Height = _loadedSettings.WindowHeight!.Value;
            }

            if (IsFiniteCoordinate(_loadedSettings.WindowLeft) && IsFiniteCoordinate(_loadedSettings.WindowTop))
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _loadedSettings.WindowLeft!.Value;
                Top = _loadedSettings.WindowTop!.Value;
            }

            if (IsUsableLength(_loadedSettings.ExplorerWidth, MinimumExplorerWidth))
            {
                _lastKnownExplorerWidth = _loadedSettings.ExplorerWidth!.Value;
                ExplorerColumnDefinition.Width = new GridLength(_lastKnownExplorerWidth, GridUnitType.Pixel);
            }

            if (IsUsableLength(_loadedSettings.ConsoleHeight, MinimumConsoleHeight))
            {
                ConsoleRowDefinition.Height = new GridLength(_loadedSettings.ConsoleHeight!.Value, GridUnitType.Pixel);
            }

            if (IsUsableLength(_loadedSettings.WorkspaceSectionHeight, MinimumExplorerSectionHeight))
            {
                WorkspaceTreeRowDefinition.Height = new GridLength(_loadedSettings.WorkspaceSectionHeight!.Value, GridUnitType.Pixel);
            }

            if (IsUsableLength(_loadedSettings.OpenTabsSectionHeight, MinimumExplorerSectionHeight))
            {
                OpenTabsRowDefinition.Height = new GridLength(_loadedSettings.OpenTabsSectionHeight!.Value, GridUnitType.Pixel);
            }

            if (IsFiniteCoordinate(_loadedSettings.DebugPaneWindowLeft) &&
                IsFiniteCoordinate(_loadedSettings.DebugPaneWindowTop) &&
                IsUsableLength(_loadedSettings.DebugPaneWindowWidth, 240) &&
                IsUsableLength(_loadedSettings.DebugPaneWindowHeight, 180))
            {
                _lastDebugPaneWindowBounds = new Rect(
                    _loadedSettings.DebugPaneWindowLeft!.Value,
                    _loadedSettings.DebugPaneWindowTop!.Value,
                    _loadedSettings.DebugPaneWindowWidth!.Value,
                    _loadedSettings.DebugPaneWindowHeight!.Value);
            }

            ApplyExplorerVisibilityLayout();

            if (_loadedSettings.StartMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }


        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsExplorerVisible))
            {
                DeveloperDiagnostics.LogStateTransition("UI", "ExplorerVisibilityChanged", string.Empty, ViewModel?.IsExplorerVisible.ToString() ?? string.Empty, "Explorer visibility changed.");
                Dispatcher.BeginInvoke(new Action(ApplyExplorerVisibilityLayout));
                return;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.SelectedTab))
            {
                DeveloperDiagnostics.LogInfo(
                    "Editor",
                    "Selected tab changed.",
                    new Dictionary<string, object?>
                    {
                        ["selectedTabTitle"] = ViewModel?.SelectedTab?.Title,
                        ["selectedTabPath"] = ViewModel?.SelectedTab?.FilePath,
                        ["selectedTabDirty"] = ViewModel?.SelectedTab?.IsDirty
                    });
                FocusActiveEditorSoon();
                RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);

                if (FindActiveEditor() is TextEditor activeEditor)
                {
                    ScheduleDiagnostics(activeEditor);
                }

                return;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.EffectiveRuntimeItem))
            {
                DeveloperDiagnostics.LogInfo(
                    "Startup",
                    "Effective runtime changed.",
                    new Dictionary<string, object?>
                    {
                        ["runtimeDisplayName"] = ViewModel?.EffectiveRuntimeInfo?.DisplayName,
                        ["runtimePath"] = ViewModel?.EffectiveRuntimeInfo?.ExecutablePath
                    });
                RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
                RescheduleDiagnosticsForAllEditors();
                StartEditorMetadataWarmup();
                UpdateRefreshEditorMetadataCommandAvailability();
                // If xterm.js is already ready and the runtime just became available,
                // start the ConPTY session now (handles the race where runtime discovery
                // completes after the terminal fires its ready event).
                if (_terminalIsReady)
                    _ = ViewModel?.EnsureConsoleRestoredAsync();
                return;
            }

            // Apply font-size zoom to all open editors (2B).
            if (e.PropertyName == nameof(MainWindowViewModel.EditorZoomLevel))
            {
                var zoomLevel = ViewModel?.EditorZoomLevel ?? 13.0;
                DeveloperDiagnostics.LogInfo("Editor", $"Editor zoom level changed to {zoomLevel}.", new Dictionary<string, object?> { ["zoomLevel"] = zoomLevel });
                foreach (var editor in _editorByTab.Values)
                {
                    editor.FontSize = zoomLevel;
                }
            }

            if (e.PropertyName == nameof(MainWindowViewModel.StatusText))
            {
                DeveloperDiagnostics.LogInfo(
                    "UI",
                    "Status text changed.",
                    new Dictionary<string, object?>
                    {
                        ["statusText"] = ViewModel?.StatusText,
                        ["focusedElement"] = DescribeFocusedElement()
                    });
            }
        }


        private void IntelliSenseService_MetadataWarmupStatusChanged(object? sender, EditorMetadataWarmupStatusChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => ApplyEditorMetadataWarmupStatus(e.Status)));
        }

        private void ApplyEditorMetadataWarmupStatus(EditorMetadataWarmupStatus status)
        {
            if (status is null)
            {
                return;
            }

            _lastEditorMetadataWarmupPhase = status.Phase;

            var detailText = string.IsNullOrWhiteSpace(status.DetailText)
                ? "PS7 ScriptDesk is loading editor command metadata in the background."
                : status.DetailText;
            var metadataSummary = status.CommandCount > 0 || status.QuickInfoCount > 0
                ? $"{Environment.NewLine}Catalog={status.CommandCount:N0}, QuickInfo={status.QuickInfoCount:N0}, ParameterizedQuickInfos={status.ParameterizedQuickInfoCount:N0}, Get-ChildItemParameters={status.GetChildItemParameterCount:N0}"
                : string.Empty;
            var runtimeCaption = string.IsNullOrWhiteSpace(status.RuntimePath)
                ? string.Empty
                : $"Runtime: {status.RuntimePath}";
            var tooltipText = string.IsNullOrWhiteSpace(runtimeCaption)
                ? $"{status.Message}{Environment.NewLine}{detailText}{metadataSummary}"
                : $"{status.Message}{Environment.NewLine}{detailText}{metadataSummary}{Environment.NewLine}{runtimeCaption}";
            var progressText = status.HasProgress
                ? $"{status.ProcessedCount:N0} of {status.TotalCount:N0}"
                : string.Empty;

            switch (status.Phase)
            {
                case EditorMetadataWarmupPhase.Scheduled:
                case EditorMetadataWarmupPhase.BuildingCommandCatalog:
                case EditorMetadataWarmupPhase.LoadingCommandMetadata:
                    if (status.Reason == EditorMetadataWarmupReason.CachedLoad)
                    {
                        EditorMetadataStatusItem.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        EditorMetadataStatusItem.Visibility = Visibility.Visible;
                        EditorMetadataStatusGlyph.Text = "⏳";
                        EditorMetadataStatusTextBlock.Text = status.HasProgress
                            ? $"{status.Message} - {progressText}"
                            : status.Message;
                        ApplyEditorMetadataBadgeColors(GetLoadingBadgeBackgroundBrush(), GetLoadingBadgeBorderBrush(), GetLoadingBadgeForegroundBrush());
                        EditorMetadataStatusBadge.ToolTip = tooltipText;
                    }

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                case EditorMetadataWarmupPhase.RefreshingCachedMetadata:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "↻";
                    EditorMetadataStatusTextBlock.Text = status.HasProgress
                        ? $"{status.Message} - {progressText}"
                        : status.Message;
                    ApplyEditorMetadataBadgeColors(GetRefreshBadgeBackgroundBrush(), GetRefreshBadgeBorderBrush(), GetRefreshBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                case EditorMetadataWarmupPhase.Completed:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "✓";
                    EditorMetadataStatusTextBlock.Text = status.ReadinessCaption;
                    ApplyEditorMetadataBadgeColors(GetReadyBadgeBackgroundBrush(), GetReadyBadgeBorderBrush(), GetReadyBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.ReadinessCaption;
                    }

                    break;

                case EditorMetadataWarmupPhase.Warning:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "!";
                    EditorMetadataStatusTextBlock.Text = status.WarningCaption;
                    ApplyEditorMetadataBadgeColors(GetWarningBadgeBackgroundBrush(), GetWarningBadgeBorderBrush(), GetWarningBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                case EditorMetadataWarmupPhase.Failed:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "!";
                    EditorMetadataStatusTextBlock.Text = "Editor metadata failed; see log";
                    ApplyEditorMetadataBadgeColors(GetFailureBadgeBackgroundBrush(), GetFailureBadgeBorderBrush(), GetFailureBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = "Editor metadata failed; see log";
                    }

                    break;

                case EditorMetadataWarmupPhase.Canceled:
                    EditorMetadataStatusItem.Visibility = Visibility.Collapsed;
                    break;

                default:
                    break;
            }

            UpdateMetadataToast(status);
            UpdateRefreshEditorMetadataCommandAvailability();

            AppLogger.Debug(
                "EditorMetadata",
                $"Ribbon state applied. Phase={status.Phase}, Caption='{EditorMetadataStatusTextBlock.Text}', HasFullParameterMetadata={status.HasFullParameterMetadata}, CommandCount={status.CommandCount:N0}, QuickInfoCount={status.QuickInfoCount:N0}, ParameterizedQuickInfoCount={status.ParameterizedQuickInfoCount:N0}, Get-ChildItemParameterCount={status.GetChildItemParameterCount:N0}.");
        }

        private void ApplyEditorMetadataBadgeColors(System.Windows.Media.Brush background, System.Windows.Media.Brush border, System.Windows.Media.Brush foreground)
        {
            EditorMetadataStatusBadge.Background = background;
            EditorMetadataStatusBadge.BorderBrush = border;
            EditorMetadataStatusGlyph.Foreground = foreground;
            EditorMetadataStatusTextBlock.Foreground = foreground;
        }

        private static System.Windows.Media.Brush GetLoadingBadgeBackgroundBrush() => CreateFrozenBrush(0xF5, 0x7C, 0x00);
        private static System.Windows.Media.Brush GetLoadingBadgeBorderBrush() => CreateFrozenBrush(0xE6, 0x51, 0x00);
        private static System.Windows.Media.Brush GetLoadingBadgeForegroundBrush() => CreateFrozenBrush(0xFF, 0xFF, 0xFF);
        private static System.Windows.Media.Brush GetRefreshBadgeBackgroundBrush() => CreateFrozenBrush(0x2F, 0x85, 0x3A);
        private static System.Windows.Media.Brush GetRefreshBadgeBorderBrush() => CreateFrozenBrush(0x14, 0x53, 0x2D);
        private static System.Windows.Media.Brush GetRefreshBadgeForegroundBrush() => CreateFrozenBrush(0xFF, 0xFF, 0xFF);
        private static System.Windows.Media.Brush GetWarningBadgeBackgroundBrush() => CreateFrozenBrush(0xEF, 0xA8, 0x2C);
        private static System.Windows.Media.Brush GetWarningBadgeBorderBrush() => CreateFrozenBrush(0xB7, 0x79, 0x1F);
        private static System.Windows.Media.Brush GetWarningBadgeForegroundBrush() => CreateFrozenBrush(0x22, 0x22, 0x22);
        private static System.Windows.Media.Brush GetReadyBadgeBackgroundBrush() => CreateFrozenBrush(0x2E, 0x7D, 0x32);
        private static System.Windows.Media.Brush GetReadyBadgeBorderBrush() => CreateFrozenBrush(0x1B, 0x5E, 0x20);
        private static System.Windows.Media.Brush GetReadyBadgeForegroundBrush() => CreateFrozenBrush(0xFF, 0xFF, 0xFF);
        private static System.Windows.Media.Brush GetFailureBadgeBackgroundBrush() => CreateFrozenBrush(0xC6, 0x28, 0x28);
        private static System.Windows.Media.Brush GetFailureBadgeBorderBrush() => CreateFrozenBrush(0x8E, 0x00, 0x00);
        private static System.Windows.Media.Brush GetFailureBadgeForegroundBrush() => CreateFrozenBrush(0xFF, 0xFF, 0xFF);

        private void UpdateMetadataToast(EditorMetadataWarmupStatus status)
        {
            if (status is null)
            {
                return;
            }

            switch (status.Phase)
            {
                case EditorMetadataWarmupPhase.Scheduled:
                case EditorMetadataWarmupPhase.BuildingCommandCatalog:
                case EditorMetadataWarmupPhase.LoadingCommandMetadata:
                case EditorMetadataWarmupPhase.RefreshingCachedMetadata:
                    if (!ShouldShowInformationalMetadataToast(status))
                    {
                        CancelPendingMetadataToast();
                        return;
                    }

                    _metadataToastAutoHideTimer.Stop();
                    _pendingMetadataToastStatus = status;
                    if (_metadataToastVisible)
                    {
                        ApplyMetadataToastContent(status);
                    }
                    else if (!_metadataToastShowDelayTimer.IsEnabled)
                    {
                        _metadataToastShowDelayTimer.Start();
                    }

                    return;

                case EditorMetadataWarmupPhase.Completed:
                    CancelPendingMetadataToast();
                    if (!_metadataToastVisible)
                    {
                        return;
                    }

                    ApplyMetadataToastContent(status);
                    ScheduleMetadataToastAutoHide(MetadataToastSuccessDismissMilliseconds);
                    return;

                case EditorMetadataWarmupPhase.Warning:
                    CancelPendingMetadataToast();
                    ApplyMetadataToastContent(status);
                    ShowMetadataToastIfNeeded(status, logReason: "warning");
                    ScheduleMetadataToastAutoHide(MetadataToastWarningDismissMilliseconds);
                    return;

                case EditorMetadataWarmupPhase.Failed:
                    CancelPendingMetadataToast();
                    ApplyMetadataToastContent(status);
                    ShowMetadataToastIfNeeded(status, logReason: "failure");
                    ScheduleMetadataToastAutoHide(MetadataToastFailureDismissMilliseconds);
                    return;

                case EditorMetadataWarmupPhase.Canceled:
                    CancelPendingMetadataToast();
                    HideMetadataToast("metadata canceled");
                    return;

                default:
                    return;
            }
        }

        private void MetadataToastShowDelayTimer_Tick(object? sender, EventArgs e)
        {
            _metadataToastShowDelayTimer.Stop();

            if (_pendingMetadataToastStatus is null)
            {
                return;
            }

            ApplyMetadataToastContent(_pendingMetadataToastStatus);
            ShowMetadataToastIfNeeded(_pendingMetadataToastStatus, logReason: "background metadata build");
        }

        private void MetadataToastAutoHideTimer_Tick(object? sender, EventArgs e)
        {
            _metadataToastAutoHideTimer.Stop();
            HideMetadataToast("auto-dismiss");
        }

        private void CancelPendingMetadataToast()
        {
            _metadataToastShowDelayTimer.Stop();
            _pendingMetadataToastStatus = null;
        }

        private void ScheduleMetadataToastAutoHide(int delayMilliseconds)
        {
            _metadataToastAutoHideTimer.Stop();
            _metadataToastAutoHideTimer.Interval = TimeSpan.FromMilliseconds(delayMilliseconds);
            _metadataToastAutoHideTimer.Start();
        }

        private bool ShouldShowInformationalMetadataToast(EditorMetadataWarmupStatus status)
        {
            return status.Reason == EditorMetadataWarmupReason.FirstRunBuild ||
                   status.Reason == EditorMetadataWarmupReason.CacheRebuild ||
                   status.Reason == EditorMetadataWarmupReason.ManualRefresh;
        }

        private void ApplyMetadataToastContent(EditorMetadataWarmupStatus status)
        {
            _visibleMetadataToastStatus = status;
            var (title, body, phaseText, glyph, showProgress, background, border, foreground) = BuildMetadataToastVisual(status);

            MetadataToastTitleTextBlock.Text = title;
            MetadataToastBodyTextBlock.Text = body;
            MetadataToastPhaseTextBlock.Text = phaseText;
            MetadataToastGlyph.Text = glyph;
            MetadataToastCard.Background = background;
            MetadataToastCard.BorderBrush = border;
            MetadataToastTitleTextBlock.Foreground = foreground;
            MetadataToastBodyTextBlock.Foreground = foreground;
            MetadataToastPhaseTextBlock.Foreground = foreground;
            MetadataToastGlyph.Foreground = foreground;
            MetadataToastProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            MetadataToastProgressBar.IsIndeterminate = showProgress;
        }

        private (string Title, string Body, string PhaseText, string Glyph, bool ShowProgress, System.Windows.Media.Brush Background, System.Windows.Media.Brush Border, System.Windows.Media.Brush Foreground) BuildMetadataToastVisual(EditorMetadataWarmupStatus status)
        {
            var detailText = string.IsNullOrWhiteSpace(status.DetailText)
                ? "PS7 ScriptDesk is preparing editor metadata in the background."
                : status.DetailText.Trim();

            var progressText = status.HasProgress
                ? $"Processed {status.ProcessedCount:N0} of {status.TotalCount:N0} commands."
                : "You can keep using the editor while this runs.";

            if (status.Phase == EditorMetadataWarmupPhase.Warning)
            {
                return (
                    "Metadata refresh did not complete",
                    "PS7 ScriptDesk could not finish rebuilding editor metadata. The previous cached metadata is still being used. Details were written to the app log.",
                    detailText,
                    "!",
                    false,
                    CreateFrozenBrush(0xFF, 0xF3, 0xCD),
                    CreateFrozenBrush(0xB7, 0x79, 0x1F),
                    CreateFrozenBrush(0x4B, 0x35, 0x00));
            }

            if (status.Phase == EditorMetadataWarmupPhase.Failed)
            {
                return (
                    "PowerShell editor metadata failed",
                    "PS7 ScriptDesk could not prepare editor metadata for this PowerShell runtime. Basic editor features may still work, but IntelliSense may be limited. Details were written to the app log.",
                    detailText,
                    "!",
                    false,
                    CreateFrozenBrush(0xFD, 0xE8, 0xE8),
                    CreateFrozenBrush(0xC6, 0x28, 0x28),
                    CreateFrozenBrush(0x7F, 0x1D, 0x1D));
            }

            if (status.Phase == EditorMetadataWarmupPhase.Completed)
            {
                return (
                    "PowerShell editor metadata ready",
                    "PS7 ScriptDesk finished preparing full editor metadata for this PowerShell runtime. IntelliSense and autofill now have richer command, parameter, syntax, and help details.",
                    detailText,
                    "✓",
                    false,
                    CreateFrozenBrush(0xE6, 0xF4, 0xEA),
                    CreateFrozenBrush(0x2E, 0x7D, 0x32),
                    CreateFrozenBrush(0x1B, 0x5E, 0x20));
            }

            if (status.Reason == EditorMetadataWarmupReason.ManualRefresh)
            {
                return (
                    "Refreshing PowerShell editor metadata",
                    "PS7 ScriptDesk is rebuilding command, parameter, syntax, and help metadata for the selected PowerShell runtime.\n\nYou can keep using the editor. The existing metadata cache will remain available until the refresh completes successfully.",
                    $"{detailText} {progressText}".Trim(),
                    "↻",
                    true,
                    CreateFrozenBrush(0xE8, 0xF2, 0xEA),
                    CreateFrozenBrush(0x2F, 0x85, 0x3A),
                    CreateFrozenBrush(0x14, 0x53, 0x2D));
            }

            var body = status.Reason == EditorMetadataWarmupReason.CacheRebuild
                ? "PS7 ScriptDesk is rebuilding command, parameter, syntax, and help metadata for this PowerShell runtime because the saved metadata cache could not be reused.\n\nYou can keep using the editor while this runs. IntelliSense will improve when loading completes."
                : "PS7 ScriptDesk is loading command, parameter, syntax, and help metadata for this PowerShell runtime.\n\nThis can take a while the first time a PowerShell version is used. You can keep using the editor while this runs. IntelliSense will improve when loading completes.";

            return (
                "Preparing PowerShell editor metadata",
                body,
                $"{detailText} {progressText}".Trim(),
                "⏳",
                true,
                CreateFrozenBrush(0xE8, 0xF2, 0xFF),
                CreateFrozenBrush(0x4A, 0x90, 0xE2),
                CreateFrozenBrush(0x1E, 0x3A, 0x5F));
        }

        private void ShowMetadataToastIfNeeded(EditorMetadataWarmupStatus status, string logReason)
        {
            if (_metadataToastVisible)
            {
                return;
            }

            _metadataToastVisible = true;
            MetadataToastHost.Visibility = Visibility.Visible;
            MetadataToastHost.BeginAnimation(UIElement.OpacityProperty, null);
            var animation = new DoubleAnimation
            {
                From = MetadataToastHost.Opacity,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220),
            };
            MetadataToastHost.BeginAnimation(UIElement.OpacityProperty, animation);
            AppLogger.Info(
                "MainWindow",
                $"Metadata toast shown. Reason={status.Reason}, Phase={status.Phase}, Runtime='{status.RuntimePath ?? "(unknown)"}', Detail='{logReason}'.");
        }

        private void HideMetadataToast(string dismissalReason)
        {
            _metadataToastAutoHideTimer.Stop();
            _metadataToastShowDelayTimer.Stop();
            _pendingMetadataToastStatus = null;

            if (!_metadataToastVisible)
            {
                return;
            }

            var status = _visibleMetadataToastStatus;
            _metadataToastVisible = false;
            MetadataToastHost.BeginAnimation(UIElement.OpacityProperty, null);
            var animation = new DoubleAnimation
            {
                From = MetadataToastHost.Opacity,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(260),
            };
            animation.Completed += (_, _) =>
            {
                MetadataToastHost.Visibility = Visibility.Collapsed;
                MetadataToastHost.Opacity = 0;
            };
            MetadataToastHost.BeginAnimation(UIElement.OpacityProperty, animation);
            AppLogger.Info(
                "MainWindow",
                $"Metadata toast dismissed. Reason={status?.Reason ?? EditorMetadataWarmupReason.None}, Phase={status?.Phase.ToString() ?? "Unknown"}, Dismissal='{dismissalReason}'.");
            _visibleMetadataToastStatus = null;
        }

        private static System.Windows.Media.Brush CreateFrozenBrush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        private void StartEditorMetadataWarmup()
        {
            var runtimeInfo = ViewModel?.EffectiveRuntimeItem?.RuntimeInfo;
            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                return;
            }

            if (!runtimeInfo.IsPowerShell7OrLater || !runtimeInfo.IsValidated)
            {
                AppLogger.Warning(
                    "MainWindow",
                    $"Editor metadata warmup will report failure because the selected runtime is not a validated PowerShell 7 runtime. DisplayPath='{runtimeInfo.ExecutablePath}', LaunchPath='{runtimeInfo.LaunchExecutablePath}', " +
                    $"Version='{runtimeInfo.VersionText}', Edition='{runtimeInfo.Edition}', Validated={runtimeInfo.IsValidated}.");
                StartupTimingLogger.Log("MainWindow", $"Editor metadata warmup scheduled for invalid runtime '{runtimeInfo.LaunchExecutablePath}' so diagnostics can capture the failure.");
            }

            var runtimeIdentity = BuildRuntimeIdentityKey(runtimeInfo);
            if (string.Equals(_pendingEditorMetadataWarmupIdentity, runtimeIdentity, StringComparison.OrdinalIgnoreCase))
            {
                AppLogger.Info("MainWindow", $"Skipped duplicate editor metadata warmup request while debounce is pending for runtime '{runtimeInfo.LaunchExecutablePath}'.");
                return;
            }

            _pendingEditorMetadataWarmupRuntime = runtimeInfo;
            _pendingEditorMetadataWarmupIdentity = runtimeIdentity;
            _editorMetadataWarmupTimer.Stop();
            _editorMetadataWarmupTimer.Start();
            StartupTimingLogger.Log("MainWindow", $"Editor command metadata warmup requested for '{runtimeInfo.LaunchExecutablePath}'.");
        }

        private void EditorMetadataWarmupTimer_Tick(object? sender, EventArgs e)
        {
            _editorMetadataWarmupTimer.Stop();

            var runtimeInfo = _pendingEditorMetadataWarmupRuntime;
            var runtimeIdentity = _pendingEditorMetadataWarmupIdentity;
            _pendingEditorMetadataWarmupRuntime = null;
            _pendingEditorMetadataWarmupIdentity = null;

            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath) || string.IsNullOrWhiteSpace(runtimeIdentity))
            {
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            if (string.Equals(_lastScheduledEditorMetadataWarmupIdentity, runtimeIdentity, StringComparison.OrdinalIgnoreCase) &&
                nowUtc - _lastScheduledEditorMetadataWarmupAtUtc <= TimeSpan.FromSeconds(15))
            {
                AppLogger.Info("MainWindow", $"Skipped duplicate editor metadata warmup request for runtime '{runtimeInfo.LaunchExecutablePath}' because the same runtime was already scheduled during startup.");
                StartupTimingLogger.Log("MainWindow", $"Skipped duplicate editor metadata warmup schedule for '{runtimeInfo.LaunchExecutablePath}'.");
                return;
            }

            _lastScheduledEditorMetadataWarmupIdentity = runtimeIdentity;
            _lastScheduledEditorMetadataWarmupAtUtc = nowUtc;
            _intelliSenseService.StartMetadataWarmup(runtimeInfo);
            StartupTimingLogger.Log("MainWindow", $"Editor command metadata warmup scheduled for '{runtimeInfo.LaunchExecutablePath}'.");
        }

        private static string BuildRuntimeIdentityKey(PowerShellRuntimeInfo runtimeInfo)
        {
            return string.Join(
                "|",
                runtimeInfo.LaunchExecutablePath?.Trim() ?? string.Empty,
                runtimeInfo.PsHome?.Trim() ?? string.Empty,
                runtimeInfo.VersionText?.Trim() ?? string.Empty,
                runtimeInfo.Edition?.Trim() ?? string.Empty,
                runtimeInfo.Architecture?.Trim() ?? string.Empty);
        }

        private void RefreshEditorMetadata_Click(object sender, RoutedEventArgs e)
        {
            var runtimeInfo = ViewModel?.EffectiveRuntimeItem?.RuntimeInfo;
            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                return;
            }

            AppLogger.Info("MainWindow", $"Manual PowerShell editor metadata refresh requested for runtime '{runtimeInfo.LaunchExecutablePath}'.");
            StartupTimingLogger.Log("MainWindow", "Manual PowerShell editor metadata refresh requested.");
            _intelliSenseService.RefreshMetadata(runtimeInfo);
            UpdateRefreshEditorMetadataCommandAvailability();
        }

        private void DeleteCurrentEditorMetadataCache_Click(object sender, RoutedEventArgs e)
        {
            var runtimeInfo = ViewModel?.EffectiveRuntimeItem?.RuntimeInfo;
            if (runtimeInfo is null || string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                System.Windows.MessageBox.Show(
                    this,
                    "No PowerShell runtime is currently selected.",
                    "PowerShell Metadata Cache",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var cacheEntries = EditorMetadataCacheStore.GetCacheEntries();
            var normalizedRuntimePath = EditorMetadataCacheStore.NormalizeRuntimePath(runtimeInfo.LaunchExecutablePath);
            var matchingEntries = cacheEntries
                .Where(entry => MetadataCacheEntryMatchesRuntime(entry, runtimeInfo))
                .ToList();
            var cacheSummary = matchingEntries.Count == 0
                ? "No existing cache folder was found for this runtime. PS7 ScriptDesk will still attempt a fresh rebuild if you continue."
                : $"Cache folders found: {matchingEntries.Count:N0}\nApproximate size: {FormatByteSize(matchingEntries.Sum(entry => entry.SizeBytes))}";

            var message =
                "Delete the saved editor metadata cache for the current PowerShell runtime?\n\n" +
                $"Runtime: {normalizedRuntimePath}\n" +
                $"Version: {runtimeInfo.VersionText ?? "unknown"}\n" +
                $"Edition: {runtimeInfo.Edition ?? "unknown"}\n" +
                $"Architecture: {runtimeInfo.Architecture ?? "unknown"}\n\n" +
                cacheSummary + "\n\n" +
                "After deletion, PS7 ScriptDesk will rebuild metadata for this runtime in the background.";

            var confirmation = System.Windows.MessageBox.Show(
                this,
                message,
                "Delete Current Runtime Metadata Cache",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            AppLogger.Info("MainWindow", $"User requested deletion of current runtime metadata cache. Runtime='{normalizedRuntimePath}', Version={runtimeInfo.VersionText}, Edition={runtimeInfo.Edition}, Architecture={runtimeInfo.Architecture}.");

            var deleted = EditorMetadataCacheStore.DeleteCacheForRuntime(
                runtimeInfo.LaunchExecutablePath,
                runtimeInfo.VersionText ?? string.Empty,
                runtimeInfo.Edition ?? string.Empty,
                runtimeInfo.Architecture ?? string.Empty,
                runtimeInfo.PsHome ?? string.Empty,
                out var resultMessage);

            ViewModel!.StatusText = deleted
                ? "Deleted current runtime metadata cache; rebuilding editor metadata."
                : resultMessage;
            AppLogger.Info("MainWindow", $"Current runtime metadata cache deletion result. Deleted={deleted}. Message={resultMessage}");

            _intelliSenseService.RefreshMetadata(runtimeInfo);
            UpdateRefreshEditorMetadataCommandAvailability();
        }

        private void DeleteAllEditorMetadataCaches_Click(object sender, RoutedEventArgs e)
        {
            var cacheEntries = EditorMetadataCacheStore.GetCacheEntries();
            var totalSize = cacheEntries.Sum(entry => entry.SizeBytes);
            var message =
                "Delete all saved PowerShell editor metadata caches?\n\n" +
                $"Cache folders found: {cacheEntries.Count:N0}\n" +
                $"Approximate size: {FormatByteSize(totalSize)}\n\n" +
                "This does not delete app logs or user scripts. Metadata will be rebuilt the next time each PowerShell runtime is used.";

            var confirmation = System.Windows.MessageBox.Show(
                this,
                message,
                "Delete All PowerShell Metadata Caches",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (confirmation != MessageBoxResult.Yes)
            {
                return;
            }

            AppLogger.Info("MainWindow", $"User requested deletion of all metadata caches. CacheCount={cacheEntries.Count:N0}, SizeBytes={totalSize:N0}.");
            var deletedAll = EditorMetadataCacheStore.DeleteAllCaches(out var resultMessage);
            ViewModel!.StatusText = resultMessage;
            AppLogger.Info("MainWindow", $"All metadata cache deletion result. DeletedAll={deletedAll}. Message={resultMessage}");

            var runtimeInfo = ViewModel?.EffectiveRuntimeItem?.RuntimeInfo;
            if (runtimeInfo is not null && !string.IsNullOrWhiteSpace(runtimeInfo.LaunchExecutablePath))
            {
                _intelliSenseService.RefreshMetadata(runtimeInfo);
            }

            UpdateRefreshEditorMetadataCommandAvailability();
        }

        private static bool MetadataCacheEntryMatchesRuntime(EditorMetadataCacheEntryInfo entry, PowerShellRuntimeInfo runtimeInfo)
        {
            if (entry.Manifest is null || runtimeInfo is null)
            {
                return false;
            }

            return string.Equals(EditorMetadataCacheStore.NormalizeRuntimePath(entry.Manifest.RuntimePath), EditorMetadataCacheStore.NormalizeRuntimePath(runtimeInfo.LaunchExecutablePath), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((entry.Manifest.RuntimeVersion ?? string.Empty).Trim(), (runtimeInfo.VersionText ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((entry.Manifest.PowerShellEdition ?? string.Empty).Trim(), (runtimeInfo.Edition ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase) &&
                   string.Equals((entry.Manifest.RuntimeArchitecture ?? string.Empty).Trim(), (runtimeInfo.Architecture ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatByteSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            var value = Math.Max(0, bytes);
            var suffixIndex = 0;
            var displayValue = (double)value;
            while (displayValue >= 1024 && suffixIndex < suffixes.Length - 1)
            {
                displayValue /= 1024;
                suffixIndex++;
            }

            return suffixIndex == 0
                ? $"{value:N0} {suffixes[suffixIndex]}"
                : $"{displayValue:N1} {suffixes[suffixIndex]}";
        }

        private void UpdateRefreshEditorMetadataCommandAvailability()
        {
            var hasRuntime = ViewModel?.EffectiveRuntimeItem?.RuntimeInfo is not null;
            var isBusy = _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.Scheduled ||
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.BuildingCommandCatalog ||
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.LoadingCommandMetadata ||
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.RefreshingCachedMetadata;

            if (RefreshEditorMetadataMenuItem is not null)
            {
                RefreshEditorMetadataMenuItem.IsEnabled = hasRuntime && !isBusy;
            }

            if (DeleteCurrentEditorMetadataCacheMenuItem is not null)
            {
                DeleteCurrentEditorMetadataCacheMenuItem.IsEnabled = hasRuntime && !isBusy;
            }

            if (DeleteAllEditorMetadataCachesMenuItem is not null)
            {
                DeleteAllEditorMetadataCachesMenuItem.IsEnabled = !isBusy;
            }
        }

        private void ApplyExplorerVisibilityLayout()
        {
            var isVisible = ViewModel?.IsExplorerVisible ?? true;

            if (isVisible)
            {
                ExplorerColumnDefinition.Width = new GridLength(Math.Max(_lastKnownExplorerWidth, MinimumExplorerWidth), GridUnitType.Pixel);
                ExplorerSplitterColumnDefinition.Width = new GridLength(6, GridUnitType.Pixel);
            }
            else
            {
                if (ExplorerColumnDefinition.ActualWidth >= MinimumExplorerWidth)
                {
                    _lastKnownExplorerWidth = ExplorerColumnDefinition.ActualWidth;
                }

                ExplorerColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
                ExplorerSplitterColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            }

            EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            EditorPaneBorder.Margin = new Thickness(0, 0, 0, 8);
        }

        private void SaveApplicationSettings()
        {
            var settings = ViewModel?.CreateApplicationSettingsSnapshot() ?? new ApplicationSettings();
            var restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            CaptureDebugPaneWindowBounds();

            if (IsUsableLength(restoreBounds.Width, MinWidth))
            {
                settings.WindowWidth = restoreBounds.Width;
            }

            if (IsUsableLength(restoreBounds.Height, MinHeight))
            {
                settings.WindowHeight = restoreBounds.Height;
            }

            if (IsFiniteCoordinate(restoreBounds.Left))
            {
                settings.WindowLeft = restoreBounds.Left;
            }

            if (IsFiniteCoordinate(restoreBounds.Top))
            {
                settings.WindowTop = restoreBounds.Top;
            }

            settings.StartMaximized = WindowState == WindowState.Maximized;
            settings.IsExplorerVisible = ViewModel?.IsExplorerVisible ?? settings.IsExplorerVisible;
            settings.IsContextHelpEnabled = IsContextHelpEnabled;
            CopyDeveloperDiagnosticsSettings(_loadedSettings, settings);

            if (ViewModel?.IsExplorerVisible == true && ExplorerColumnDefinition.ActualWidth >= MinimumExplorerWidth)
            {
                _lastKnownExplorerWidth = ExplorerColumnDefinition.ActualWidth;
            }

            settings.ExplorerWidth = _lastKnownExplorerWidth;

            if (ConsoleRowDefinition.ActualHeight >= MinimumConsoleHeight)
            {
                settings.ConsoleHeight = ConsoleRowDefinition.ActualHeight;
            }

            if (WorkspaceTreeRowDefinition.ActualHeight >= MinimumExplorerSectionHeight)
            {
                settings.WorkspaceSectionHeight = WorkspaceTreeRowDefinition.ActualHeight;
            }

            if (OpenTabsRowDefinition.ActualHeight >= MinimumExplorerSectionHeight)
            {
                settings.OpenTabsSectionHeight = OpenTabsRowDefinition.ActualHeight;
            }

            if (_lastDebugPaneWindowBounds is Rect debugPaneBounds)
            {
                settings.DebugPaneWindowWidth = debugPaneBounds.Width;
                settings.DebugPaneWindowHeight = debugPaneBounds.Height;
                settings.DebugPaneWindowLeft = debugPaneBounds.Left;
                settings.DebugPaneWindowTop = debugPaneBounds.Top;

                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug pane window size and position saved.",
                    new Dictionary<string, object?>
                    {
                        ["left"] = debugPaneBounds.Left,
                        ["top"] = debugPaneBounds.Top,
                        ["width"] = debugPaneBounds.Width,
                        ["height"] = debugPaneBounds.Height
                    });
            }

            _applicationSettingsService.SaveSettings(settings);
            DeveloperDiagnostics.LogInfo(
                "Settings",
                "SaveApplicationSettings completed from MainWindow.",
                new Dictionary<string, object?>
                {
                    ["settingsPath"] = _applicationSettingsService.SettingsFilePath,
                    ["developerDiagnosticsEnabled"] = settings.IsDeveloperDiagnosticsEnabled
                });
        }

        private static void CopyDeveloperDiagnosticsSettings(ApplicationSettings source, ApplicationSettings destination)
        {
            destination.IsDeveloperDiagnosticsEnabled = source.IsDeveloperDiagnosticsEnabled;
            destination.IsDeveloperDiagnosticsVerboseUiEnabled = source.IsDeveloperDiagnosticsVerboseUiEnabled;
            destination.IsDeveloperDiagnosticsVerboseDebuggerEnabled = source.IsDeveloperDiagnosticsVerboseDebuggerEnabled;
            destination.IsDeveloperDiagnosticsVerboseTerminalEnabled = source.IsDeveloperDiagnosticsVerboseTerminalEnabled;
            destination.IsDeveloperDiagnosticsVerboseEditorEnabled = source.IsDeveloperDiagnosticsVerboseEditorEnabled;
            destination.IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled = source.IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled;
            destination.DeveloperDiagnosticsPreviewCharacterLimit = source.DeveloperDiagnosticsPreviewCharacterLimit;
            destination.DeveloperDiagnosticsRetentionHours = source.DeveloperDiagnosticsRetentionHours;
            destination.DeveloperDiagnosticsWriteJsonLines = source.DeveloperDiagnosticsWriteJsonLines;
            destination.DeveloperDiagnosticsWriteReadableLog = source.DeveloperDiagnosticsWriteReadableLog;
        }

        private DeveloperDiagnosticsStateSnapshot BuildDeveloperDiagnosticsSnapshot()
        {
            var selectedTab = ViewModel?.SelectedTab;
            var activeTabIndex = selectedTab is null || ViewModel is null ? (int?)null : ViewModel.OpenTabs.IndexOf(selectedTab);
            return new DeveloperDiagnosticsStateSnapshot
            {
                ActiveDocumentPath = selectedTab?.FilePath,
                ActiveDocumentDirtyState = selectedTab?.IsDirty,
                ActiveTabIndex = activeTabIndex,
                OpenTabCount = ViewModel?.OpenTabs.Count,
                IsDebugSessionActive = ViewModel?.IsDebugSessionActive,
                DebugSessionState = _debugSession?.CurrentState.ToString(),
                TerminalState = _terminalIsReady
                    ? (_terminalIsActive ? "ReadyActive" : "ReadyInactive")
                    : "Initializing",
                PowerShellExecutablePath = ViewModel?.EffectiveRuntimeInfo?.ExecutablePath,
                SelectedRuntimeDisplayName = ViewModel?.EffectiveRuntimeInfo?.DisplayName
            };
        }

        private Dictionary<string, object?> BuildDebugActionProperties(object? sender)
        {
            var selectedTab = ViewModel?.SelectedTab;
            var activeEditor = FindActiveEditor();
            return new Dictionary<string, object?>
            {
                ["senderType"] = sender?.GetType().FullName,
                ["focusedElement"] = DescribeFocusedElement(),
                ["activeTabTitle"] = selectedTab?.Title,
                ["activeTabFilePath"] = selectedTab?.FilePath,
                ["activeDocumentDirtyState"] = selectedTab?.IsDirty,
                ["activeDocumentUntitled"] = string.IsNullOrWhiteSpace(selectedTab?.FilePath),
                ["selectedTextLength"] = activeEditor?.SelectionLength ?? 0,
                ["caretLine"] = selectedTab?.CaretLine,
                ["caretColumn"] = selectedTab?.CaretColumn,
                ["currentBreakpointCount"] = selectedTab?.EnabledBreakpointCount,
                ["debugSessionState"] = _debugSession?.CurrentState.ToString()
            };
        }

        private void UpdateDeveloperDiagnosticsMenuState()
        {
            EnableDeveloperDiagnosticsMenuItem.IsChecked = _loadedSettings.IsDeveloperDiagnosticsEnabled;
            VerboseUiLoggingMenuItem.IsChecked = _loadedSettings.IsDeveloperDiagnosticsVerboseUiEnabled;
            VerboseDebuggerLoggingMenuItem.IsChecked = _loadedSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled;
            VerboseTerminalLoggingMenuItem.IsChecked = _loadedSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled;
            VerboseEditorLoggingMenuItem.IsChecked = _loadedSettings.IsDeveloperDiagnosticsVerboseEditorEnabled;

            var enabled = _loadedSettings.IsDeveloperDiagnosticsEnabled;
            VerboseUiLoggingMenuItem.IsEnabled = enabled;
            VerboseDebuggerLoggingMenuItem.IsEnabled = enabled;
            VerboseTerminalLoggingMenuItem.IsEnabled = enabled;
            VerboseEditorLoggingMenuItem.IsEnabled = enabled;
        }

        private void PersistDeveloperDiagnosticsSettings(string statusText)
        {
            SaveApplicationSettings();
            DeveloperDiagnostics.ConfigureFromSettings(_loadedSettings, "MainWindow updated developer diagnostics settings");
            UpdateDeveloperDiagnosticsMenuState();
            if (ViewModel is not null)
            {
                ViewModel.StatusText = statusText;
            }

            DeveloperDiagnostics.RefreshSummaryFile();
        }

        private void EnableDeveloperDiagnostics_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.IsDeveloperDiagnosticsEnabled = !_loadedSettings.IsDeveloperDiagnosticsEnabled;
            PersistDeveloperDiagnosticsSettings(_loadedSettings.IsDeveloperDiagnosticsEnabled
                ? "Developer diagnostics enabled"
                : "Developer diagnostics disabled");
            DeveloperDiagnostics.LogUserAction("Settings", "DeveloperDiagnosticsToggle", _loadedSettings.IsDeveloperDiagnosticsEnabled ? "Developer diagnostics enabled in-app." : "Developer diagnostics disabled in-app.");
        }

        private void VerboseUiLogging_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.IsDeveloperDiagnosticsVerboseUiEnabled = !_loadedSettings.IsDeveloperDiagnosticsVerboseUiEnabled;
            PersistDeveloperDiagnosticsSettings($"Developer diagnostics UI verbosity: {_loadedSettings.IsDeveloperDiagnosticsVerboseUiEnabled}");
        }

        private void VerboseDebuggerLogging_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled = !_loadedSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled;
            PersistDeveloperDiagnosticsSettings($"Developer diagnostics debugger verbosity: {_loadedSettings.IsDeveloperDiagnosticsVerboseDebuggerEnabled}");
        }

        private void VerboseTerminalLogging_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled = !_loadedSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled;
            PersistDeveloperDiagnosticsSettings($"Developer diagnostics terminal verbosity: {_loadedSettings.IsDeveloperDiagnosticsVerboseTerminalEnabled}");
        }

        private void VerboseEditorLogging_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.IsDeveloperDiagnosticsVerboseEditorEnabled = !_loadedSettings.IsDeveloperDiagnosticsVerboseEditorEnabled;
            PersistDeveloperDiagnosticsSettings($"Developer diagnostics editor verbosity: {_loadedSettings.IsDeveloperDiagnosticsVerboseEditorEnabled}");
        }

        private void OpenDeveloperDebuggingFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderInExplorer(DeveloperDiagnostics.DeveloperDebuggingRootDirectory);
        }

        private void OpenLogsFolder_Click(object sender, RoutedEventArgs e)
        {
            var target = ResolveDiagnosticLogsFolder();
            if (OpenFolderInExplorer(target, copyPathToClipboard: true))
            {
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Opened diagnostic logs folder and copied path: {target}";
                }
            }
        }

        private void CopyLogsFolderPath_Click(object sender, RoutedEventArgs e)
        {
            var target = ResolveDiagnosticLogsFolder();
            Directory.CreateDirectory(target);
            TrySetClipboardText(target);
            DeveloperDiagnostics.LogUserAction(
                "UI",
                "CopyLogsFolderPath",
                "Copied diagnostic logs folder path to clipboard.",
                new Dictionary<string, object?> { ["path"] = target });

            if (ViewModel is not null)
            {
                ViewModel.StatusText = $"Diagnostic logs folder path copied: {target}";
            }
        }

        private void CreateSupportLogsZip_Click(object sender, RoutedEventArgs e)
        {
            CreateSupportLogsPackage(openContainingFolder: true, showConfirmation: true);
        }

        private void OpenLatestDiagnosticSessionFolder_Click(object sender, RoutedEventArgs e)
        {
            var target = DeveloperDiagnostics.CurrentSessionDirectory;
            if (string.IsNullOrWhiteSpace(target))
            {
                try
                {
                    target = File.Exists(DeveloperDiagnostics.LatestSessionPointerFilePath)
                        ? File.ReadAllText(DeveloperDiagnostics.LatestSessionPointerFilePath).Trim()
                        : null;
                }
                catch
                {
                    target = null;
                }
            }

            if (string.IsNullOrWhiteSpace(target))
            {
                ViewModel!.StatusText = "No developer diagnostics session folder is available";
                return;
            }

            OpenFolderInExplorer(target);
        }

        private void CopyDiagnosticsSummaryToClipboard_Click(object sender, RoutedEventArgs e)
        {
            var summary = DeveloperDiagnostics.BuildSummaryText();
            System.Windows.Clipboard.SetText(summary);
            DeveloperDiagnostics.RefreshSummaryFile();
            ViewModel!.StatusText = "Developer diagnostics summary copied to clipboard";
        }

        private void PackageDeveloperDiagnosticsForSupport_Click(object sender, RoutedEventArgs e)
        {
            CreateSupportLogsPackage(openContainingFolder: true, showConfirmation: false);
        }

        private void ClearDeveloperDiagnosticsLogs_Click(object sender, RoutedEventArgs e)
        {
            var decision = System.Windows.MessageBox.Show(
                this,
                "Delete all files under the Developer Debugging folder? This does not delete normal app logs.",
                "Clear Developer Diagnostics Logs",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (decision != MessageBoxResult.Yes)
            {
                return;
            }

            if (_loadedSettings.IsDeveloperDiagnosticsEnabled)
            {
                _loadedSettings.IsDeveloperDiagnosticsEnabled = false;
                PersistDeveloperDiagnosticsSettings("Developer diagnostics disabled before clearing logs");
            }

            try
            {
                if (Directory.Exists(DeveloperDiagnostics.DeveloperDebuggingRootDirectory))
                {
                    Directory.Delete(DeveloperDiagnostics.DeveloperDebuggingRootDirectory, recursive: true);
                }

                ViewModel!.StatusText = "Developer diagnostics logs cleared";
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("Settings", ex, "Failed to clear developer diagnostics logs.");
                ViewModel!.StatusText = $"Clear developer diagnostics logs failed: {ex.Message}";
            }
        }

        private static string ResolveDiagnosticLogsFolder()
        {
            return string.IsNullOrWhiteSpace(AppLogger.CurrentLogDirectory)
                ? Path.Combine(ApplicationBranding.LocalApplicationDataRoot, "Logs")
                : AppLogger.CurrentLogDirectory;
        }

        private void CreateSupportLogsPackage(bool openContainingFolder, bool showConfirmation)
        {
            try
            {
                var packagePath = DeveloperDiagnostics.CreateSupportPackage();
                TrySetClipboardText(packagePath);
                var containingFolder = Path.GetDirectoryName(packagePath) ?? DeveloperDiagnostics.DeveloperDebuggingPackagesDirectory;

                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Support logs ZIP created and copied to clipboard: {packagePath}";
                }

                if (showConfirmation)
                {
                    System.Windows.MessageBox.Show(
                        this,
                        $"A support logs ZIP was created and its full path was copied to the clipboard. Send this ZIP for support.\n\n{packagePath}",
                        "Support Logs ZIP Created",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                }

                if (openContainingFolder)
                {
                    OpenFolderInExplorer(containingFolder);
                }
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("UI", ex, "Failed to create support logs package.");
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Create support logs ZIP failed: {ex.Message}";
                }

                System.Windows.MessageBox.Show(
                    this,
                    $"PS7 ScriptDesk could not create the support logs ZIP.\n\n{ex.Message}",
                    "Support Logs ZIP Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private bool OpenFolderInExplorer(string path, bool copyPathToClipboard = false)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("The folder path was empty.", nameof(path));
                }

                var normalizedPath = Path.GetFullPath(path.Trim());
                Directory.CreateDirectory(normalizedPath);

                if (!Directory.Exists(normalizedPath))
                {
                    throw new DirectoryNotFoundException($"The folder could not be created or found: {normalizedPath}");
                }

                if (copyPathToClipboard)
                {
                    TrySetClipboardText(normalizedPath);
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = normalizedPath,
                    UseShellExecute = true
                });

                DeveloperDiagnostics.LogUserAction(
                    "UI",
                    "OpenFolder",
                    "Opened folder in Explorer.",
                    new Dictionary<string, object?> { ["path"] = normalizedPath, ["copiedToClipboard"] = copyPathToClipboard });
                return true;
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("UI", ex, "Failed to open folder in Explorer.", new Dictionary<string, object?> { ["path"] = path });
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = $"Open folder failed: {ex.Message}";
                }

                return false;
            }
        }

        private static bool TrySetClipboardText(string text)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return false;
                }

                System.Windows.Clipboard.SetText(text);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current)
            where T : DependencyObject
        {
            while (current is not null)
            {
                if (current is T match)
                {
                    return match;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private static bool IsFiniteCoordinate(double? value)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value);
        }

        private static bool IsUsableLength(double? value, double minimum)
        {
            return value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= minimum;
        }

        // -------------------------------------------------------------------------
        // Syntax error diagnostics
        // -------------------------------------------------------------------------

        /// <summary>
        /// Debounces and schedules a background syntax-parse for <paramref name="editorTextEditor"/>.
        /// Any in-flight parse for the same editor is cancelled before the new one starts.
        /// The results are applied on the UI thread via <see cref="Dispatcher"/>.
        /// </summary>
        private void ScheduleDiagnostics(TextEditor editorTextEditor)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
            {
                CancelPendingDiagnostics(editorTextEditor);
                return;
            }

            CancelPendingDiagnostics(editorTextEditor);

            if (!_tabByEditor.TryGetValue(editorTextEditor, out var currentTab) || !ReferenceEquals(currentTab, tab))
            {
                return;
            }

            var registrationVersion = _editorRegistrationVersions.TryGetValue(editorTextEditor, out var editorRegistrationVersion)
                ? editorRegistrationVersion
                : IncrementEditorRegistrationVersion(editorTextEditor);
            var requestVersion = IncrementDiagnosticsRequestVersion(editorTextEditor);

            var pwshPath = ViewModel?.EffectiveRuntimeExecutablePath;
            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);

            if (string.IsNullOrWhiteSpace(pwshPath))
            {
                ClearParserTokensForEditor(editorTextEditor);
                errorRenderer.SetErrors(Array.Empty<ParseErrorInfo>());
                tab.SetSyntaxDiagnosticsStatus("No PowerShell runtime is available for syntax checking", clearErrors: true);
                ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);

                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab) &&
                    !string.Equals(ViewModel.StatusText, "No PowerShell runtime is available for syntax checking", StringComparison.Ordinal))
                {
                    ViewModel.StatusText = "No PowerShell runtime is available for syntax checking";
                }

                return;
            }

            var cts = new CancellationTokenSource();
            _diagnosticsCancellationSources[editorTextEditor] = cts;

            tab.SetSyntaxDiagnosticsStatus("Syntax checking…");

            var scriptSnapshot = editorTextEditor.Text ?? string.Empty;
            var token = cts.Token;

            ObserveFireAndForget(Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(SyntaxDiagnosticsDebounceMilliseconds, token).ConfigureAwait(false);

                    var parseResult = await _diagnosticsService
                        .ParseAsync(scriptSnapshot, pwshPath, token)
                        .ConfigureAwait(false);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsDiagnosticsRequestCurrent(editorTextEditor, tab, cts, registrationVersion, requestVersion, scriptSnapshot))
                        {
                            return;
                        }

                        if (!parseResult.Succeeded)
                        {
                            ClearParserTokensForEditor(editorTextEditor);
                            errorRenderer.SetErrors(Array.Empty<ParseErrorInfo>());

                            if (!string.Equals(parseResult.FailureMessage, "Syntax checking was canceled.", StringComparison.Ordinal))
                            {
                                tab.SetSyntaxDiagnosticsStatus(parseResult.FailureMessage ?? "Syntax checking failed.", clearErrors: true);
                                ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);
                                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab))
                                {
                                    ViewModel.StatusText = parseResult.FailureMessage ?? "Syntax checking failed.";
                                }
                            }

                            return;
                        }

                        ApplyParserTokensToEditor(editorTextEditor, parseResult.SyntaxTokens);

                        var parserDiagnostics = parseResult.Errors;
                        var authoringDiagnostics = PowerShellAuthoringDiagnostics
                            .Analyze(scriptSnapshot, parseResult)
                            .Select(ParseErrorInfo.AsWarning);

                        var editorDiagnostics = parserDiagnostics
                            .Concat(authoringDiagnostics)
                            .OrderBy(error => error.StartOffset)
                            .ToList();

                        errorRenderer.SetErrors(editorDiagnostics);
                        editorTextEditor.TextArea.TextView.Redraw();
                        UpdateSyntaxDiagnosticsForTab(editorTextEditor, editorDiagnostics);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Superseded by a newer keystroke — nothing to do.
                }
                catch (Exception ex)
                {
                    _ = Dispatcher.InvokeAsync(() =>
                    {
                        if (!IsDiagnosticsRequestCurrent(editorTextEditor, tab, cts, registrationVersion, requestVersion, scriptSnapshot))
                        {
                            return;
                        }

                        ClearParserTokensForEditor(editorTextEditor);
                        errorRenderer.SetErrors(Array.Empty<ParseErrorInfo>());
                        tab.SetSyntaxDiagnosticsStatus($"Syntax checking failed: {ex.Message}", clearErrors: true);
                        ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);
                        if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab))
                        {
                            ViewModel.StatusText = $"Syntax checking failed: {ex.Message}";
                        }
                    });
                }
            }, token), "syntax diagnostics update");
        }

        private bool IsDiagnosticsRequestCurrent(
            TextEditor editorTextEditor,
            EditorTabViewModel tab,
            CancellationTokenSource requestTokenSource,
            int registrationVersion,
            int requestVersion,
            string scriptSnapshot)
        {
            return _errorRenderers.ContainsKey(editorTextEditor)
                && _tabByEditor.TryGetValue(editorTextEditor, out var currentTab)
                && ReferenceEquals(currentTab, tab)
                && _diagnosticsCancellationSources.TryGetValue(editorTextEditor, out var currentCts)
                && ReferenceEquals(currentCts, requestTokenSource)
                && _editorRegistrationVersions.TryGetValue(editorTextEditor, out var currentRegistrationVersion)
                && currentRegistrationVersion == registrationVersion
                && _diagnosticsRequestVersions.TryGetValue(editorTextEditor, out var currentRequestVersion)
                && currentRequestVersion == requestVersion
                && string.Equals(editorTextEditor.Text ?? string.Empty, scriptSnapshot, StringComparison.Ordinal);
        }

        private void RescheduleDiagnosticsForAllEditors()
        {
            foreach (var editor in _editorByTab.Values.ToList())
            {
                ScheduleDiagnostics(editor);
            }
        }

        private void ApplyParserTokensToEditor(TextEditor editorTextEditor, IReadOnlyList<SyntaxTokenInfo> syntaxTokens)
        {
            if (_syntaxColorizers.TryGetValue(editorTextEditor, out var colorizer))
            {
                colorizer.SetParserTokens(syntaxTokens);
            }
        }

        private void ClearParserTokensForEditor(TextEditor editorTextEditor)
        {
            if (_syntaxColorizers.TryGetValue(editorTextEditor, out var colorizer))
            {
                colorizer.ClearParserTokens();
            }
        }

        private void UpdateSyntaxDiagnosticsForTab(TextEditor editorTextEditor, IReadOnlyList<ParseErrorInfo> errors)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab || editorTextEditor.Document is null)
            {
                return;
            }

            var document = editorTextEditor.Document;
            var diagnostics = errors
                .Select(error =>
                {
                    var safeOffset = Math.Clamp(error.StartOffset, 0, document.TextLength);
                    var line = document.GetLineByOffset(safeOffset);
                    var lineNumber = line.LineNumber;
                    var columnNumber = Math.Max(1, safeOffset - line.Offset + 1);

                    return new EditorDiagnosticSpanViewModel(lineNumber, columnNumber, error.Message, error.StartOffset, error.EndOffset, error.Severity);
                })
                .ToList();

            tab.SetSyntaxDiagnostics(diagnostics, "Diagnostics: OK");

            var errorCount = diagnostics.Count(static diagnostic => diagnostic.IsError);
            var warningCount = diagnostics.Count(static diagnostic => diagnostic.IsWarning);
            DeveloperDiagnostics.LogDebug(
                "Editor",
                "Editor diagnostics applied to active document tab.",
                new Dictionary<string, object?>
                {
                    ["documentTitle"] = tab.Title,
                    ["filePath"] = tab.FilePath,
                    ["diagnosticCount"] = diagnostics.Count,
                    ["errorCount"] = errorCount,
                    ["warningCount"] = warningCount
                });

            if (ViewModel is null || !ReferenceEquals(ViewModel.SelectedTab, tab))
            {
                return;
            }

            if (diagnostics.Count == 1)
            {
                ViewModel.StatusText = $"{diagnostics[0].Severity}: {diagnostics[0].DisplayText}";
            }
            else if (diagnostics.Count > 1)
            {
                ViewModel.StatusText = $"{tab.SyntaxErrorSummaryText} detected";
            }
            else
            {
                ViewModel.StatusText = "Diagnostics: OK";
            }
        }

        // -------------------------------------------------------------------------
        // Error tooltip (mouse hover)
        // -------------------------------------------------------------------------

        private TextEditor? ResolveEditorFromTextView(TextView textView)
        {
            foreach (var editor in _tabByEditor.Keys)
            {
                if (ReferenceEquals(editor.TextArea.TextView, textView))
                {
                    return editor;
                }
            }

            foreach (var editor in _configuredEditors)
            {
                if (ReferenceEquals(editor.TextArea.TextView, textView))
                {
                    return editor;
                }
            }

            return null;
        }

        private void ShowEditorToolTip(TextView textView, string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                CloseActiveEditorToolTip();
                return;
            }

            CloseActiveEditorToolTip();

            _activeEditorToolTip = new WpfToolTip
            {
                Content = content,
                PlacementTarget = textView,
                Placement = PlacementMode.Mouse,
                StaysOpen = true,
                MaxWidth = 760,
                IsOpen = true,
            };
        }

        private void CloseActiveEditorToolTip()
        {
            if (_activeEditorToolTip is null)
            {
                return;
            }

            _activeEditorToolTip.IsOpen = false;
            _activeEditorToolTip = null;
        }

        private void OnTextViewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not TextView textView)
            {
                return;
            }

            var point = e.GetPosition(textView);
            if (ReferenceEquals(_pendingHoverTextView, textView) &&
                GetPointDistanceSquared(point, _pendingHoverPoint) < 9)
            {
                return;
            }

            _pendingHoverTextView = textView;
            _pendingHoverPoint = point;
            _editorHoverTimer.Stop();
            _editorHoverTimer.Start();

            CancelActiveQuickInfoRequest();
            CloseActiveEditorToolTip();
        }

        private void OnTextViewMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _editorHoverTimer.Stop();
            _pendingHoverTextView = null;
            CancelActiveQuickInfoRequest();
            CloseActiveEditorToolTip();
        }

        private async void EditorHoverTimer_Tick(object? sender, EventArgs e)
        {
            _editorHoverTimer.Stop();

            var textView = _pendingHoverTextView;
            if (textView is null)
            {
                return;
            }

            await ShowEditorHoverAsync(textView, _pendingHoverPoint).ConfigureAwait(true);
        }

        private async void OnTextViewMouseHover(object sender, System.Windows.Input.MouseEventArgs e)
        {
            if (sender is not TextView textView)
            {
                return;
            }

            _editorHoverTimer.Stop();
            _pendingHoverTextView = textView;
            _pendingHoverPoint = e.GetPosition(textView);
            await ShowEditorHoverAsync(textView, _pendingHoverPoint).ConfigureAwait(true);
        }

        private async Task ShowEditorHoverAsync(TextView textView, WpfPoint hoverPoint)
        {
            textView.EnsureVisualLines();

            var position = textView.GetPositionFloor(hoverPoint + textView.ScrollOffset);
            if (position is null || textView.Document is null)
            {
                return;
            }

            var offset = textView.Document.GetOffset(position.Value.Location);

            var ownerEditor = ResolveEditorFromTextView(textView);
            if (ownerEditor is null)
            {
                return;
            }

            if (_errorRenderers.TryGetValue(ownerEditor, out var renderer))
            {
                var error = renderer.FindErrorAt(offset);
                if (error is not null)
                {
                    ShowEditorToolTip(textView, error.Message);
                    return;
                }
            }

            if (TryShowLiveDebugVariableHover(textView, ownerEditor, offset))
            {
                return;
            }

            var cts = BeginQuickInfoRequest();
            var cancellationToken = cts.Token;

            try
            {
                var quickInfo = await _intelliSenseService.GetQuickInfoAsync(
                    ownerEditor,
                    offset,
                    ViewModel?.EffectiveRuntimeExecutablePath,
                    cancellationToken).ConfigureAwait(true);

                if (!cancellationToken.IsCancellationRequested && quickInfo is not null)
                {
                    ShowEditorToolTip(textView, quickInfo.ToString());
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException)
            {
                // The hover was canceled because the mouse moved, the editor closed, or a newer
                // quick-info request superseded this one. Do not surface that as a runtime error.
            }
            finally
            {
                CompleteQuickInfoRequest(cts);
            }
        }

        private bool TryShowLiveDebugVariableHover(TextView textView, TextEditor ownerEditor, int offset)
        {
            var token = TryGetHoveredDebugVariableToken(ownerEditor, offset);
            var paused = _debugSession?.CurrentState == DebugSessionState.Paused;
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Live debug hover requested.",
                new Dictionary<string, object?>
                {
                    ["token"] = token ?? string.Empty,
                    ["debuggerPaused"] = paused,
                    ["cacheCount"] = _liveDebugVariableCache.Count
                });

            if (string.IsNullOrWhiteSpace(token))
            {
                DeveloperDiagnostics.LogDecision("Debugger", "TryShowLiveDebugVariableHover", "Live debug hover fell back to static help because no simple variable token was detected.", "FallbackNoVariableToken", new Dictionary<string, object?> { ["debuggerPaused"] = paused });
                return false;
            }

            var normalizedName = NormalizeDebugVariableName(token);
            DeveloperDiagnostics.LogInfo("Debugger", "Live debug hover token detected.", new Dictionary<string, object?> { ["token"] = token, ["normalizedVariableName"] = normalizedName, ["debuggerPaused"] = paused });

            if (!paused)
            {
                DeveloperDiagnostics.LogDecision("Debugger", "TryShowLiveDebugVariableHover", "Live debug hover fell back to static help because the debugger was not paused.", "FallbackDebuggerNotPaused", new Dictionary<string, object?> { ["variableName"] = normalizedName });
                return false;
            }

            if (ownerEditor.DataContext is not EditorTabViewModel tab || tab.CurrentDebugLine <= 0)
            {
                DeveloperDiagnostics.LogDecision("Debugger", "TryShowLiveDebugVariableHover", "Live debug hover fell back to static help because the hovered editor is not the current paused debug location.", "FallbackNotCurrentDebugLocation", new Dictionary<string, object?> { ["variableName"] = normalizedName });
                return false;
            }

            if (!_liveDebugVariableCache.TryGetValue(normalizedName, out var variable))
            {
                DeveloperDiagnostics.LogDecision("Debugger", "TryShowLiveDebugVariableHover", "Live debug hover cache miss; static help will be used.", "FallbackCacheMiss", new Dictionary<string, object?> { ["variableName"] = normalizedName, ["cacheCount"] = _liveDebugVariableCache.Count });
                return false;
            }

            var tooltip = BuildLiveDebugVariableHoverText(token, variable);
            ShowEditorToolTip(textView, tooltip);
            DeveloperDiagnostics.LogDecision("Debugger", "TryShowLiveDebugVariableHover", "Live debug hover cache hit; live tooltip was shown.", "LiveHoverCacheHit", new Dictionary<string, object?> { ["variableName"] = normalizedName, ["type"] = variable.Type });
            return true;
        }

        private void OnTextViewMouseHoverStopped(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _editorHoverTimer.Stop();
            _pendingHoverTextView = null;
            CancelActiveQuickInfoRequest();
            CloseActiveEditorToolTip();
        }

        private static double GetPointDistanceSquared(WpfPoint left, WpfPoint right)
        {
            var x = left.X - right.X;
            var y = left.Y - right.Y;
            return (x * x) + (y * y);
        }

        private void OnDiagnosticGlyphLineClicked(TextEditor editorTextEditor, int lineNumber)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
            {
                return;
            }

            var diagnostic = tab.SyntaxErrors.FirstOrDefault(error => error.LineNumber == lineNumber);
            if (diagnostic is not null)
            {
                NavigateToSyntaxDiagnostic(tab, diagnostic);
            }
        }

        private void SyntaxDiagnosticItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement frameworkElement &&
                frameworkElement.DataContext is SyntaxErrorViewModel diagnostic &&
                ViewModel?.SelectedTab is EditorTabViewModel selectedTab)
            {
                NavigateToSyntaxDiagnostic(selectedTab, diagnostic);
            }
        }

        private void NavigateToSyntaxDiagnostic(EditorTabViewModel tab, SyntaxErrorViewModel diagnostic)
        {
            if (!_editorByTab.TryGetValue(tab, out var editorTextEditor))
            {
                if (ViewModel is not null)
                {
                    ViewModel.SelectedTab = tab;
                    Dispatcher.BeginInvoke(new Action(() => NavigateToSyntaxDiagnostic(tab, diagnostic)), System.Windows.Threading.DispatcherPriority.Loaded);
                }

                return;
            }

            var safeStartOffset = Math.Clamp(diagnostic.StartOffset, 0, editorTextEditor.Text.Length);
            var safeSelectionLength = Math.Max(1, Math.Min(diagnostic.EndOffset, editorTextEditor.Text.Length) - safeStartOffset);

            editorTextEditor.Focus();
            editorTextEditor.CaretOffset = safeStartOffset;
            editorTextEditor.Select(safeStartOffset, safeSelectionLength);
            editorTextEditor.ScrollTo(diagnostic.LineNumber, diagnostic.ColumnNumber);
            editorTextEditor.TextArea.Caret.BringCaretToView();

            if (ViewModel is not null)
            {
                ViewModel.StatusText = diagnostic.DisplayText;
            }
        }

        // -------------------------------------------------------------------------
        // Debug panels (Variables, Call Stack, Breakpoints) — Part 3
        // -------------------------------------------------------------------------

        private const double DebugPanelWidth = 290;
        private const double MinimumSavedDebugPaneWindowWidth = 240;
        private const double MinimumSavedDebugPaneWindowHeight = 180;

        /// <summary>Shows or hides the right-side debug panel column.</summary>
        private void SetDebugPanelVisible(bool visible)
        {
            if (visible)
            {
                DebugPanelColumn.Width         = new GridLength(DebugPanelWidth, GridUnitType.Pixel);
                DebugPanelColumn.MinWidth      = 160;
                DebugPanelSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
                DebugPanelSplitter.Visibility  = Visibility.Visible;
                DebugPanelBorder.Visibility    = Visibility.Visible;
            }
            else
            {
                DebugPanelColumn.Width         = new GridLength(0, GridUnitType.Pixel);
                DebugPanelColumn.MinWidth      = 0;
                DebugPanelSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                DebugPanelSplitter.Visibility  = Visibility.Collapsed;
                DebugPanelBorder.Visibility    = Visibility.Collapsed;
            }

            ShowDebugPanelMenuItem.IsChecked = visible;
            ApplyDebugPanePresentationState();
        }

        private void ShowDebugPanel_Click(object sender, RoutedEventArgs e)
        {
            SetDebugPanelVisible(ShowDebugPanelMenuItem.IsChecked);
        }

        private void PopOutDebugPaneButton_Click(object sender, RoutedEventArgs e)
        {
            PopOutDebugPane("HeaderButton");
        }

        private void PopOutDebugPaneMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PopOutDebugPane("ViewMenu");
        }

        private void DockDebugPaneButton_Click(object sender, RoutedEventArgs e)
        {
            DockDebugPane("PlaceholderButton");
        }

        private void DockDebugPaneMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DockDebugPane("ViewMenu");
        }

        private void DebugPaneTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!ReferenceEquals(e.Source, DebugPaneTabControl))
            {
                return;
            }

            SyncDebugPaneTabSelection(DebugPaneTabControl.SelectedIndex, "DockedTabControl");
        }

        private void PopOutDebugPane(string reason)
        {
            DeveloperDiagnostics.LogUserAction(
                "Debugger",
                "DebugPanePopOutRequested",
                "Debug pane pop-out requested.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["alreadyPoppedOut"] = _debugPaneWindow is not null,
                    ["selectedTabIndex"] = _selectedDebugTabIndex
                });

            SetDebugPanelVisible(true);

            if (_debugPaneWindow is not null)
            {
                _debugPaneWindow.Activate();
                DeveloperDiagnostics.LogDecision("Debugger", "PopOutDebugPane", "Debug pane pop-out request reused the existing floating window.", "AlreadyPoppedOut", new Dictionary<string, object?> { ["reason"] = reason });
                return;
            }

            var debugPaneWindow = new DebugPaneWindow
            {
                Owner = this
            };

            _debugPaneWindow = debugPaneWindow;
            debugPaneWindow.DockBackRequested += DebugPaneWindow_DockBackRequested;
            debugPaneWindow.SelectedTabIndexChanged += DebugPaneWindow_SelectedTabIndexChanged;
            debugPaneWindow.RemoveSelectedBreakpointRequested += DebugPaneWindow_RemoveSelectedBreakpointRequested;
            debugPaneWindow.Closed += DebugPaneWindow_Closed;
            debugPaneWindow.LocationChanged += DebugPaneWindow_LocationChanged;
            debugPaneWindow.SizeChanged += DebugPaneWindow_SizeChanged;

            RestoreDebugPaneWindowBounds(debugPaneWindow);
            ApplyDebugPaneItemsSources("PopOutCreated");
            RefreshBreakpointsList();
            SyncDebugPaneTabSelection(_selectedDebugTabIndex, "PopOutCreated");
            ApplyDebugPanePresentationState();

            DeveloperDiagnostics.LogInfo("Debugger", "Floating Debug pane window created.", new Dictionary<string, object?> { ["reason"] = reason });
            debugPaneWindow.Show();
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Floating Debug pane window shown.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["selectedTabIndex"] = _selectedDebugTabIndex
                });
        }

        private void DockDebugPane(string reason)
        {
            DeveloperDiagnostics.LogUserAction(
                "Debugger",
                "DebugPaneDockBackRequested",
                "Debug pane dock-back requested.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["wasPoppedOut"] = _debugPaneWindow is not null
                });

            var debugPaneWindow = _debugPaneWindow;
            if (debugPaneWindow is null)
            {
                DeveloperDiagnostics.LogDecision("Debugger", "DockDebugPane", "Debug pane dock-back was skipped because no floating window was open.", "SkippedNoFloatingWindow", new Dictionary<string, object?> { ["reason"] = reason });
                return;
            }

            CaptureDebugPaneWindowBounds(debugPaneWindow);
            SyncDebugPaneTabSelection(debugPaneWindow.SelectedTabIndex, "DockBack");
            _debugPaneWindow = null;
            ApplyDebugPanePresentationState();
            ApplyDebugPaneItemsSources("DockBack");
            debugPaneWindow.CloseForDockBack();
        }

        private void DebugPaneWindow_DockBackRequested(object? sender, EventArgs e)
        {
            DockDebugPane("FloatingWindowRequest");
        }

        private void DebugPaneWindow_SelectedTabIndexChanged(object? sender, DebugPaneTabChangedEventArgs e)
        {
            SyncDebugPaneTabSelection(e.SelectedIndex, "FloatingWindowTabControl");
        }

        private void DebugPaneWindow_RemoveSelectedBreakpointRequested(object? sender, EventArgs e)
        {
            if (sender is DebugPaneWindow debugPaneWindow)
            {
                RemoveSelectedBreakpoint(debugPaneWindow.SelectedBreakpointItem);
            }
        }

        private void DebugPaneWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not DebugPaneWindow debugPaneWindow)
            {
                return;
            }

            CaptureDebugPaneWindowBounds(debugPaneWindow);

            if (ReferenceEquals(_debugPaneWindow, debugPaneWindow))
            {
                _debugPaneWindow = null;
                ApplyDebugPanePresentationState();
                ApplyDebugPaneItemsSources("FloatingWindowClosed");
            }

            DeveloperDiagnostics.LogInfo("Debugger", "Floating Debug pane window closed.", new Dictionary<string, object?> { ["selectedTabIndex"] = _selectedDebugTabIndex });
        }

        private void DebugPaneWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (sender is DebugPaneWindow debugPaneWindow)
            {
                CaptureDebugPaneWindowBounds(debugPaneWindow);
            }
        }

        private void DebugPaneWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is DebugPaneWindow debugPaneWindow)
            {
                CaptureDebugPaneWindowBounds(debugPaneWindow);
            }
        }

        private void ApplyDebugPanePresentationState()
        {
            var isPoppedOut = _debugPaneWindow is not null;
            DebugPaneTabControl.Visibility = isPoppedOut ? Visibility.Collapsed : Visibility.Visible;
            DebugPanePoppedOutPlaceholder.Visibility = isPoppedOut ? Visibility.Visible : Visibility.Collapsed;
            PopOutDebugPaneButton.Visibility = isPoppedOut ? Visibility.Collapsed : Visibility.Visible;
            PopOutDebugPaneMenuItem.Visibility = isPoppedOut ? Visibility.Collapsed : Visibility.Visible;
            DockDebugPaneMenuItem.Visibility = isPoppedOut ? Visibility.Visible : Visibility.Collapsed;

            if (isPoppedOut)
            {
                DeveloperDiagnostics.LogInfo("Debugger", "Docked Debug pane placeholder shown because the pane is popped out.", new Dictionary<string, object?> { ["selectedTabIndex"] = _selectedDebugTabIndex });
            }
        }

        private void SyncDebugPaneTabSelection(int selectedTabIndex, string reason)
        {
            if (selectedTabIndex < 0 || _isSynchronizingDebugTabSelection)
            {
                return;
            }

            var previousIndex = _selectedDebugTabIndex;
            _selectedDebugTabIndex = selectedTabIndex;
            _isSynchronizingDebugTabSelection = true;
            try
            {
                if (DebugPaneTabControl.SelectedIndex != selectedTabIndex)
                {
                    DebugPaneTabControl.SelectedIndex = selectedTabIndex;
                }

                _debugPaneWindow?.SetSelectedTabIndex(selectedTabIndex);
            }
            finally
            {
                _isSynchronizingDebugTabSelection = false;
            }

            if (previousIndex != selectedTabIndex)
            {
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Selected debug tab changed.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["previousIndex"] = previousIndex,
                        ["selectedTabIndex"] = selectedTabIndex
                    });
            }
        }

        private void RestoreDebugPaneWindowBounds(DebugPaneWindow debugPaneWindow)
        {
            var bounds = _lastDebugPaneWindowBounds;
            if (bounds is not Rect restoredBounds)
            {
                restoredBounds = new Rect(
                    Left + 40,
                    Top + 40,
                    DefaultDebugPaneWindowWidth,
                    DefaultDebugPaneWindowHeight);
            }

            debugPaneWindow.Left = restoredBounds.Left;
            debugPaneWindow.Top = restoredBounds.Top;
            debugPaneWindow.Width = restoredBounds.Width;
            debugPaneWindow.Height = restoredBounds.Height;

            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Debug pane window size and position restored.",
                new Dictionary<string, object?>
                {
                    ["left"] = restoredBounds.Left,
                    ["top"] = restoredBounds.Top,
                    ["width"] = restoredBounds.Width,
                    ["height"] = restoredBounds.Height
                });
        }

        private void CaptureDebugPaneWindowBounds()
        {
            if (_debugPaneWindow is not null)
            {
                CaptureDebugPaneWindowBounds(_debugPaneWindow);
            }
        }

        private void CaptureDebugPaneWindowBounds(DebugPaneWindow debugPaneWindow)
        {
            if (debugPaneWindow.WindowState != WindowState.Normal)
            {
                return;
            }

            if (!IsFiniteCoordinate(debugPaneWindow.Left) ||
                !IsFiniteCoordinate(debugPaneWindow.Top) ||
                !IsUsableLength(debugPaneWindow.Width, MinimumSavedDebugPaneWindowWidth) ||
                !IsUsableLength(debugPaneWindow.Height, MinimumSavedDebugPaneWindowHeight))
            {
                return;
            }

            _lastDebugPaneWindowBounds = new Rect(debugPaneWindow.Left, debugPaneWindow.Top, debugPaneWindow.Width, debugPaneWindow.Height);
        }

        private void ApplyDebugPaneItemsSources(string reason)
        {
            ApplyDebugVariablesItemsSource(_currentDebugVariables, reason, null);
            ApplyDebugCallStackItemsSource(_currentDebugCallStack, reason, null);
            ApplyDebugBreakpointsItemsSource(_currentBreakpointRows, reason);
        }

        private void ApplyDebugVariablesItemsSource(IReadOnlyList<DebugVariableInfo>? variables, string reason, int? refreshVersion)
        {
            _currentDebugVariables = variables;
            DebugVariablesGrid.ItemsSource = variables;

            if (_debugPaneWindow is not null)
            {
                _debugPaneWindow.DebugVariablesGrid.ItemsSource = variables;
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug Variables synchronized to floating window.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["refreshVersion"] = refreshVersion,
                        ["variableCount"] = variables?.Count ?? 0,
                        ["variableNamePreview"] = variables is null
                            ? string.Empty
                            : DeveloperDiagnostics.SanitizePreview(string.Join(", ", variables.Take(12).Select(variable => variable.Name)))
                    });
            }
        }

        private void ApplyDebugCallStackItemsSource(IReadOnlyList<DebugCallStackFrame>? callStack, string reason, int? refreshVersion)
        {
            _currentDebugCallStack = callStack;
            DebugCallStackGrid.ItemsSource = callStack;

            if (_debugPaneWindow is not null)
            {
                _debugPaneWindow.DebugCallStackGrid.ItemsSource = callStack;
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug Call Stack synchronized to floating window.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["refreshVersion"] = refreshVersion,
                        ["callStackCount"] = callStack?.Count ?? 0
                    });
            }
        }

        private void ApplyDebugBreakpointsItemsSource(ObservableCollection<BreakpointRow>? breakpoints, string reason)
        {
            _currentBreakpointRows = breakpoints;
            DebugBreakpointsGrid.ItemsSource = breakpoints;

            if (_debugPaneWindow is not null)
            {
                _debugPaneWindow.DebugBreakpointsGrid.ItemsSource = breakpoints;
                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug Breakpoints synchronized to floating window.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["breakpointCount"] = breakpoints?.Count ?? 0
                    });
            }
        }

        private void ScheduleDebugPanelRefresh(string reason)
        {
            var debugSession = _debugSession;
            if (debugSession is null)
            {
                TraceDebugShell("ScheduleDebugPanelRefresh", $"Skipped because debug session is null; reason={reason}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogDecision("Debugger", "ScheduleDebugPanelRefresh", "Debug panel refresh was skipped because the debug session was null.", "SkippedNoSession", new Dictionary<string, object?> { ["reason"] = reason });
                return;
            }

            if (debugSession.CurrentState != DebugSessionState.Paused)
            {
                TraceDebugShell("ScheduleDebugPanelRefresh", $"Skipped because session is not paused; reason={reason}; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogDecision("Debugger", "ScheduleDebugPanelRefresh", "Debug panel refresh was skipped because the debug session was not paused.", "SkippedNotPaused", new Dictionary<string, object?> { ["reason"] = reason, ["sessionState"] = debugSession.CurrentState.ToString() });
                return;
            }

            var refreshVersion = Interlocked.Increment(ref _debugPanelRefreshVersion);
            TraceDebugShell("ScheduleDebugPanelRefresh", $"Scheduled; reason={reason}; refreshVersion={refreshVersion}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Debug panel refresh scheduled.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["refreshVersion"] = refreshVersion,
                    ["sessionState"] = debugSession.CurrentState.ToString(),
                    ["hasCurrentDebugLocation"] = HasActiveDebugCurrentLocation()
                });

            _ = RefreshDebugPanelsAsync(debugSession, refreshVersion, reason).ContinueWith(
                task =>
                {
                    if (task.Exception is not null)
                    {
                        TraceDebugShell("ScheduleDebugPanelRefresh", $"Unhandled failure; reason={reason}; refreshVersion={refreshVersion}; exceptionType={task.Exception.GetBaseException().GetType().Name}; message={task.Exception.GetBaseException().Message}; {DescribeDebugUiState()}");
                        DeveloperDiagnostics.LogException("Debugger", task.Exception.GetBaseException(), "Scheduled debug panel refresh failed unexpectedly.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        private int InvalidateDebugPanelRefresh(string reason)
        {
            var refreshVersion = Interlocked.Increment(ref _debugPanelRefreshVersion);
            TraceDebugShell("InvalidateDebugPanelRefresh", $"Invalidated; reason={reason}; refreshVersion={refreshVersion}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogInfo("Debugger", "Debug panel refresh invalidated.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
            return refreshVersion;
        }

        private sealed record DebugPanelRefreshSnapshot(
            int CurrentVersion,
            bool SessionMatches,
            string SessionState,
            bool HasCurrentDebugLocation,
            bool WindowLoaded);

        private bool HasActiveDebugCurrentLocation()
        {
            return ViewModel?.OpenTabs.Any(tab => tab.CurrentDebugLine > 0) == true;
        }

        private async Task<DebugPanelRefreshSnapshot?> GetDebugPanelRefreshSnapshotOnUiThreadAsync(
            IDebugSession debugSession,
            int refreshVersion,
            string reason,
            string stage)
        {
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Debug panel refresh UI-thread snapshot requested.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["stage"] = stage,
                    ["refreshVersion"] = refreshVersion
                });

            try
            {
                var snapshot = await Dispatcher.InvokeAsync(() =>
                {
                    var currentVersion = Volatile.Read(ref _debugPanelRefreshVersion);
                    var sessionMatches = ReferenceEquals(_debugSession, debugSession);
                    var sessionState = debugSession.CurrentState.ToString();
                    var hasCurrentDebugLocation = HasActiveDebugCurrentLocation();
                    var windowLoaded = IsLoaded;
                    return new DebugPanelRefreshSnapshot(
                        currentVersion,
                        sessionMatches,
                        sessionState,
                        hasCurrentDebugLocation,
                        windowLoaded);
                });

                DeveloperDiagnostics.LogInfo(
                    "Debugger",
                    "Debug panel refresh UI-thread snapshot succeeded.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["stage"] = stage,
                        ["refreshVersion"] = refreshVersion,
                        ["currentVersion"] = snapshot.CurrentVersion,
                        ["sessionMatches"] = snapshot.SessionMatches,
                        ["sessionState"] = snapshot.SessionState,
                        ["hasCurrentDebugLocation"] = snapshot.HasCurrentDebugLocation,
                        ["windowLoaded"] = snapshot.WindowLoaded
                    });
                return snapshot;
            }
            catch (Exception ex)
            {
                TraceDebugShell("GetDebugPanelRefreshSnapshotOnUiThreadAsync", $"Failed; reason={reason}; stage={stage}; refreshVersion={refreshVersion}; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogException(
                    "Debugger",
                    ex,
                    "Debug panel refresh UI-thread snapshot failed.",
                    new Dictionary<string, object?>
                    {
                        ["reason"] = reason,
                        ["stage"] = stage,
                        ["refreshVersion"] = refreshVersion
                    });
                return null;
            }
        }

        /// <summary>
        /// Queries variables and call stack from the live debug session and populates
        /// the Variables and Call Stack grids when the session remains paused.
        /// </summary>
        private async Task RefreshDebugPanelsAsync(IDebugSession debugSession, int refreshVersion, string reason)
        {
            try
            {
                await Task.Delay(250).ConfigureAwait(false);

                var preQuerySnapshot = await GetDebugPanelRefreshSnapshotOnUiThreadAsync(debugSession, refreshVersion, reason, "AfterDelay").ConfigureAwait(false);
                if (preQuerySnapshot is null)
                {
                    DeveloperDiagnostics.LogDecision("Debugger", "RefreshDebugPanelsAsync", "Debug panel refresh was skipped because the UI-thread snapshot could not be captured.", "SkippedSnapshotFailure", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["stage"] = "AfterDelay" });
                    return;
                }

                if (!CanRefreshDebugPanels(preQuerySnapshot, refreshVersion, out var skipReason))
                {
                    TraceDebugShell("RefreshDebugPanelsAsync", $"Skipped after delay; reason={reason}; skipReason={skipReason}; refreshVersion={refreshVersion}; currentVersion={preQuerySnapshot.CurrentVersion}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogDecision("Debugger", "RefreshDebugPanelsAsync", "Debug panel refresh was skipped after the debounce delay.", "SkippedAfterDelay", new Dictionary<string, object?> { ["reason"] = reason, ["skipReason"] = skipReason, ["refreshVersion"] = refreshVersion, ["currentVersion"] = preQuerySnapshot.CurrentVersion });
                    return;
                }

                DeveloperDiagnostics.LogInfo("Debugger", "Debug variable query starting.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                TraceDebugShell("RefreshDebugPanelsAsync", $"Variables query starting; reason={reason}; refreshVersion={refreshVersion}; {DescribeDebugUiState()}");
                IReadOnlyList<DebugVariableInfo> variables;
                try
                {
                    variables = await debugSession.GetVariablesAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException("Debugger", ex, "Debug variable query failed.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                    throw;
                }
                DeveloperDiagnostics.LogInfo("Debugger", "Debug variable query completed.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["variableCount"] = variables.Count });
                var filteredVariables = FilterDebugVariablesForDisplay(variables, reason, refreshVersion);

                var postVariablesSnapshot = await GetDebugPanelRefreshSnapshotOnUiThreadAsync(debugSession, refreshVersion, reason, "AfterVariables").ConfigureAwait(false);
                if (postVariablesSnapshot is null)
                {
                    DeveloperDiagnostics.LogDecision("Debugger", "RefreshDebugPanelsAsync", "Debug panel refresh was skipped because the post-variables UI-thread snapshot could not be captured.", "SkippedSnapshotFailure", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["stage"] = "AfterVariables" });
                    return;
                }

                if (!CanRefreshDebugPanels(postVariablesSnapshot, refreshVersion, out skipReason))
                {
                    TraceDebugShell("RefreshDebugPanelsAsync", $"Skipped after variables; reason={reason}; skipReason={skipReason}; refreshVersion={refreshVersion}; currentVersion={postVariablesSnapshot.CurrentVersion}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogDecision("Debugger", "RefreshDebugPanelsAsync", "Debug panel refresh became stale after the variables query.", "SkippedAfterVariables", new Dictionary<string, object?> { ["reason"] = reason, ["skipReason"] = skipReason, ["refreshVersion"] = refreshVersion, ["currentVersion"] = postVariablesSnapshot.CurrentVersion, ["variableCount"] = variables.Count });
                    return;
                }

                DeveloperDiagnostics.LogInfo("Debugger", "Debug call stack query starting.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                TraceDebugShell("RefreshDebugPanelsAsync", $"Call stack query starting; reason={reason}; refreshVersion={refreshVersion}; {DescribeDebugUiState()}");
                IReadOnlyList<DebugCallStackFrame> callStack;
                try
                {
                    callStack = await debugSession.GetCallStackAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    DeveloperDiagnostics.LogException("Debugger", ex, "Debug call stack query failed.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                    throw;
                }
                DeveloperDiagnostics.LogInfo("Debugger", "Debug call stack query completed.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["callStackCount"] = callStack.Count });

                await Dispatcher.InvokeAsync(() =>
                {
                    DeveloperDiagnostics.LogInfo("Debugger", "Debug panel grid update starting.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["variableCount"] = filteredVariables.Count, ["callStackCount"] = callStack.Count });
                    var uiSnapshot = new DebugPanelRefreshSnapshot(
                        Volatile.Read(ref _debugPanelRefreshVersion),
                        ReferenceEquals(_debugSession, debugSession),
                        debugSession.CurrentState.ToString(),
                        HasActiveDebugCurrentLocation(),
                        IsLoaded);
                    if (!CanRefreshDebugPanels(uiSnapshot, refreshVersion, out var uiSkipReason))
                    {
                        TraceDebugShell("RefreshDebugPanelsAsync", $"Skipped UI update; reason={reason}; skipReason={uiSkipReason}; refreshVersion={refreshVersion}; currentVersion={uiSnapshot.CurrentVersion}; {DescribeDebugUiState()}");
                        DeveloperDiagnostics.LogDecision("Debugger", "RefreshDebugPanelsAsync", "Debug panel UI update was skipped because the refresh became stale.", "SkippedUiUpdate", new Dictionary<string, object?> { ["reason"] = reason, ["skipReason"] = uiSkipReason, ["refreshVersion"] = refreshVersion, ["currentVersion"] = uiSnapshot.CurrentVersion, ["variableCount"] = filteredVariables.Count, ["callStackCount"] = callStack.Count });
                        return;
                    }

                    ApplyDebugVariablesItemsSource(filteredVariables, reason, refreshVersion);
                    ApplyDebugCallStackItemsSource(callStack, reason, refreshVersion);
                    UpdateLiveDebugVariableCache(filteredVariables, reason, refreshVersion);
                    RefreshBreakpointsList();
                    TraceDebugShell("RefreshDebugPanelsAsync", $"Updated UI grids; reason={reason}; refreshVersion={refreshVersion}; variableCount={filteredVariables.Count}; callStackCount={callStack.Count}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogInfo("Debugger", "Debug panel UI grids updated.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion, ["variableCount"] = filteredVariables.Count, ["callStackCount"] = callStack.Count });
                });
            }
            catch (Exception ex)
            {
                TraceDebugShell("RefreshDebugPanelsAsync", $"Failed; reason={reason}; exceptionType={ex.GetType().Name}; message={ex.Message}; refreshVersion={refreshVersion}; currentVersion={Volatile.Read(ref _debugPanelRefreshVersion)}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogException("Debugger", ex, "Debug panel refresh failed.", new Dictionary<string, object?> { ["reason"] = reason, ["refreshVersion"] = refreshVersion });
                ClearLiveDebugVariableCache($"Debug panel refresh failed: {reason}");
                await Dispatcher.InvokeAsync(() =>
                {
                    if (ViewModel is not null && ReferenceEquals(_debugSession, debugSession))
                    {
                        ViewModel.StatusText = $"Debug panel refresh failed: {ex.Message}";
                        RefreshDebugCommandAvailability(debugSession.CurrentState == DebugSessionState.Paused);
                    }
                });
            }
        }

        private bool CanRefreshDebugPanels(DebugPanelRefreshSnapshot snapshot, int refreshVersion, out string reason)
        {
            if (refreshVersion != snapshot.CurrentVersion)
            {
                reason = $"Refresh version {refreshVersion} is stale; current version is {snapshot.CurrentVersion}.";
                return false;
            }

            if (!snapshot.SessionMatches)
            {
                reason = "Active debug session changed.";
                return false;
            }

            if (!string.Equals(snapshot.SessionState, DebugSessionState.Paused.ToString(), StringComparison.Ordinal))
            {
                reason = $"Debug session state is {snapshot.SessionState}, not Paused.";
                return false;
            }

            if (!snapshot.WindowLoaded)
            {
                reason = "Window is not loaded.";
                return false;
            }

            if (!snapshot.HasCurrentDebugLocation)
            {
                reason = "No active paused source location is available yet.";
                return false;
            }

            reason = "Ready";
            return true;
        }

        private IReadOnlyList<DebugVariableInfo> FilterDebugVariablesForDisplay(
            IReadOnlyList<DebugVariableInfo> variables,
            string reason,
            int refreshVersion)
        {
            var filteredVariables = new List<DebugVariableInfo>(variables.Count);
            var hiddenCount = 0;

            foreach (var variable in variables)
            {
                if (ShouldHideDebugVariable(variable))
                {
                    hiddenCount++;
                    continue;
                }

                filteredVariables.Add(new DebugVariableInfo(
                    variable.Name,
                    variable.Type,
                    TruncateDebugVariableValue(variable.Value)));
            }

            filteredVariables.Sort(static (left, right) => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

            var displayedNamePreview = filteredVariables.Count == 0
                ? string.Empty
                : DeveloperDiagnostics.SanitizePreview(string.Join(", ", filteredVariables.Take(12).Select(variable => variable.Name)));
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Debug variables filtered for display.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["refreshVersion"] = refreshVersion,
                    ["rawVariableCount"] = variables.Count,
                    ["filteredVariableCount"] = filteredVariables.Count,
                    ["hiddenVariableCount"] = hiddenCount,
                    ["displayedVariableNamePreview"] = displayedNamePreview
                });

            return filteredVariables;
        }

        private static bool ShouldHideDebugVariable(DebugVariableInfo variable)
        {
            if (string.IsNullOrWhiteSpace(variable.Name))
            {
                return true;
            }

            if (HiddenDebugVariableNames.Contains(variable.Name))
            {
                return true;
            }

            return variable.Name.StartsWith("__PSS", StringComparison.OrdinalIgnoreCase) ||
                   variable.Name.StartsWith("__PS7", StringComparison.OrdinalIgnoreCase) ||
                   variable.Name.StartsWith("PSScriptDesk", StringComparison.OrdinalIgnoreCase);
        }

        private static string TruncateDebugVariableValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            return normalized.Length <= DebugVariableValueMaxLength
                ? normalized
                : normalized[..DebugVariableValueMaxLength] + "...";
        }

        private void UpdateLiveDebugVariableCache(
            IReadOnlyList<DebugVariableInfo> variables,
            string reason,
            int refreshVersion)
        {
            _liveDebugVariableCache.Clear();
            foreach (var variable in variables)
            {
                var normalizedName = NormalizeDebugVariableName(variable.Name);
                if (string.IsNullOrWhiteSpace(normalizedName))
                {
                    continue;
                }

                _liveDebugVariableCache[normalizedName] = variable;
            }

            var preview = _liveDebugVariableCache.Count == 0
                ? string.Empty
                : DeveloperDiagnostics.SanitizePreview(string.Join(", ", _liveDebugVariableCache.Keys.Take(12)));
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Live debug variable hover cache updated.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["refreshVersion"] = refreshVersion,
                    ["cacheCount"] = _liveDebugVariableCache.Count,
                    ["variableNamePreview"] = preview
                });
        }

        private void ClearLiveDebugVariableCache(string reason)
        {
            var previousCount = _liveDebugVariableCache.Count;
            _liveDebugVariableCache.Clear();
            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Live debug variable hover cache cleared.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["previousCount"] = previousCount
                });
        }

        private static string? TryGetHoveredDebugVariableToken(TextEditor editor, int offset)
        {
            var text = editor.Document?.Text;
            if (string.IsNullOrEmpty(text) || offset < 0 || offset > text.Length)
            {
                return null;
            }

            var scanIndex = Math.Min(offset, text.Length - 1);
            if (scanIndex < 0)
            {
                return null;
            }

            if (!IsDebugVariableTokenCharacter(text[scanIndex]) && scanIndex > 0)
            {
                scanIndex--;
            }

            if (scanIndex < 0 || !IsDebugVariableTokenCharacter(text[scanIndex]))
            {
                return null;
            }

            var start = scanIndex;
            var end = scanIndex;
            while (start > 0 && IsDebugVariableTokenCharacter(text[start - 1]))
            {
                start--;
            }

            while (end + 1 < text.Length && IsDebugVariableTokenCharacter(text[end + 1]))
            {
                end++;
            }

            var token = text.Substring(start, end - start + 1);
            if (token.StartsWith("${", StringComparison.Ordinal) && token.EndsWith("}", StringComparison.Ordinal))
            {
                return token.Length > 3 ? token : null;
            }

            return token.Length > 1 && token[0] == '$' ? token : null;
        }

        private static bool IsDebugVariableTokenCharacter(char character)
        {
            return char.IsLetterOrDigit(character) ||
                   character == '$' ||
                   character == '_' ||
                   character == '{' ||
                   character == '}';
        }

        private static string NormalizeDebugVariableName(string variableName)
        {
            var normalized = variableName.Trim();
            if (normalized.StartsWith("${", StringComparison.Ordinal) && normalized.EndsWith("}", StringComparison.Ordinal))
            {
                normalized = normalized[2..^1];
            }
            else if (normalized.StartsWith("$", StringComparison.Ordinal))
            {
                normalized = normalized[1..];
            }

            return normalized.Trim();
        }

        private static string BuildLiveDebugVariableHoverText(string token, DebugVariableInfo variable)
        {
            var valuePreview = SanitizeDebugHoverValue(variable.Value);
            return $"{token}{Environment.NewLine}Type: {variable.Type}{Environment.NewLine}Value: {valuePreview}";
        }

        private static string SanitizeDebugHoverValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value
                .Replace("\r\n", " ", StringComparison.Ordinal)
                .Replace('\r', ' ')
                .Replace('\n', ' ')
                .Trim();

            return normalized.Length <= DebugHoverValueMaxLength
                ? normalized
                : normalized[..DebugHoverValueMaxLength] + "...";
        }

        /// <summary>Rebuilds the Breakpoints DataGrid from every open tab's breakpoint set.</summary>
        private void RefreshBreakpointsList()
        {
            if (ViewModel is null)
            {
                return;
            }

            var rows = new ObservableCollection<BreakpointRow>();
            foreach (var tab in ViewModel.OpenTabs)
            {
                var fileName = string.IsNullOrWhiteSpace(tab.FilePath)
                    ? "(unsaved)"
                    : Path.GetFileName(tab.FilePath);

                foreach (var lineNum in tab.BreakpointLineNumbers)
                {
                    rows.Add(new BreakpointRow(tab, lineNum, fileName, tab.IsBreakpointEnabled(lineNum), OnBreakpointRowEnabledChanged));
                }
            }

            ApplyDebugBreakpointsItemsSource(rows, "RefreshBreakpointsList");
        }

        private void ClearDebugPanels()
        {
            ClearLiveDebugVariableCache("ClearDebugPanels");
            ApplyDebugVariablesItemsSource(null, "ClearDebugPanels", null);
            ApplyDebugCallStackItemsSource(null, "ClearDebugPanels", null);
        }

        /// <summary>
        /// Highlights the debug stop location, selecting the matching tab when PowerShell
        /// reported a script path for the paused frame.
        /// </summary>
        private void SetDebugCurrentLocation(string? scriptPath, int lineNumber)
        {
            if (lineNumber <= 0 || ViewModel is null)
            {
                return;
            }

            ClearDebugCurrentLine();

            EditorTabViewModel? targetTab = ViewModel.SelectedTab;
            if (!string.IsNullOrWhiteSpace(scriptPath))
            {
                if (!string.IsNullOrWhiteSpace(_activeDebugLaunchPath) &&
                    string.Equals(Path.GetFullPath(_activeDebugLaunchPath), Path.GetFullPath(scriptPath), StringComparison.OrdinalIgnoreCase) &&
                    _activeDebugTab is not null)
                {
                    targetTab = _activeDebugTab;
                }
                else
                {
                    targetTab = ViewModel.OpenTabs.FirstOrDefault(tab =>
                        !string.IsNullOrWhiteSpace(tab.FilePath) &&
                        string.Equals(Path.GetFullPath(tab.FilePath), Path.GetFullPath(scriptPath), StringComparison.OrdinalIgnoreCase))
                        ?? targetTab;
                }
            }

            if (targetTab is null)
            {
                return;
            }

            if (!ReferenceEquals(ViewModel.SelectedTab, targetTab))
            {
                ViewModel.SelectedTab = targetTab;
            }

            targetTab.SetCurrentDebugLine(lineNumber);

            if (_editorByTab.TryGetValue(targetTab, out var editor))
            {
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                RefreshBreakpointGlyphMargin(editor);

                if (editor.Document is not null && lineNumber <= editor.Document.LineCount)
                {
                    editor.ScrollToLine(lineNumber);
                    editor.CaretOffset = editor.Document.GetLineByNumber(lineNumber).Offset;
                    editor.Focus();
                }
            }
        }

        /// <summary>Clears the debug current-line highlight from all open editors.</summary>
        private void ClearDebugCurrentLine()
        {
            if (ViewModel is null) return;

            foreach (var tab in ViewModel.OpenTabs)
            {
                if (tab.CurrentDebugLine <= 0) continue;
                tab.ClearCurrentDebugLine();

                if (_editorByTab.TryGetValue(tab, out var editor))
                {
                    editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                    RefreshBreakpointGlyphMargin(editor);
                }
            }
        }

        private void DebugBreakpointRemove_Click(object sender, RoutedEventArgs e)
        {
            RemoveSelectedBreakpoint(DebugBreakpointsGrid.SelectedItem);
        }

        private void RemoveSelectedBreakpoint(object? selectedItem)
        {
            if (selectedItem is not BreakpointRow row)
            {
                return;
            }

            row.Tab.ToggleBreakpoint(row.LineNumber);

            // Force the renderer and breakpoint gutter for this tab's editor to redraw.
            if (_editorByTab.TryGetValue(row.Tab, out var editor))
            {
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                RefreshBreakpointGlyphMargin(editor);
            }

            RefreshBreakpointsList();
            RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
        }

        /// <summary>Row model for the Breakpoints DataGrid.</summary>
        private sealed class BreakpointRow : INotifyPropertyChanged
        {
            private readonly Action<BreakpointRow> _onEnabledChanged;
            private bool _isEnabled;

            public BreakpointRow(EditorTabViewModel tab, int lineNumber, string fileName, bool isEnabled, Action<BreakpointRow> onEnabledChanged)
            {
                Tab = tab;
                LineNumber = lineNumber;
                FileName = fileName;
                _isEnabled = isEnabled;
                _onEnabledChanged = onEnabledChanged;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            // Not shown in the grid — used by Remove_Click.
            public EditorTabViewModel Tab { get; }

            public string FileName { get; }
            public int LineNumber { get; }

            public bool IsEnabled
            {
                get => _isEnabled;
                set
                {
                    if (_isEnabled == value)
                    {
                        return;
                    }

                    _isEnabled = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
                    _onEnabledChanged(this);
                }
            }
        }

        private void OnBreakpointRowEnabledChanged(BreakpointRow row)
        {
            row.Tab.SetBreakpointEnabled(row.LineNumber, row.IsEnabled);

            if (_editorByTab.TryGetValue(row.Tab, out var editor))
            {
                editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
                RefreshBreakpointGlyphMargin(editor);
            }

            RefreshDebugCommandAvailability(_debugSession?.CurrentState == DebugSessionState.Paused);
        }
    }
}
