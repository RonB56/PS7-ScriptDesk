using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
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
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Rendering;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Application.Services;
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.PowerShell.Services;
using PS7ScriptDesk.Shell.Dialogs;
using PS7ScriptDesk.Shell.Debug;
using PS7ScriptDesk.Shell.Editor;
using PS7ScriptDesk.Shell.Help;
using PS7ScriptDesk.Shell.Services;
using PS7ScriptDesk.Shell.Themes;
using PS7ScriptDesk.UI.ViewModels;

namespace PS7ScriptDesk.Shell
{
    public partial class MainWindow : Window
    {
        private const double MinimumExplorerWidth = 190;
        private const double MinimumConsoleHeight = 160;
        private const double MinimumConsoleSideWidth = 260;
        private const double MinimumBottomToolWindowHeight = 120;
        private const double BottomToolWindowSplitterThickness = 6;
        private const double DefaultConsoleHeight = 180;
        private const double DefaultConsoleSideWidth = 420;
        private const double DefaultBottomToolWindowHeight = 180;
        private const double MinimumExplorerSectionHeight = 120;
        private const double DebugPanelWidth = 220;
        private const double MinimumDebugPanelWidth = 160;
        private const int LiveSyntaxDiagnosticsQuietDelayMilliseconds = 0;
        private const int LiveSyntaxDiagnosticsLargeFileQuietDelayMilliseconds = 125;
        private const int LiveSyntaxDiagnosticsMinimumIntervalMilliseconds = 16;
        private const int LiveSyntaxDiagnosticsLargeFileMinimumIntervalMilliseconds = 250;
        private const int LiveSyntaxDiagnosticsLargeFileCharacterThreshold = 45000;
        private const int LiveSyntaxDiagnosticsLargeFileLineThreshold = 800;
        private const int AuthoringDiagnosticsSmallDocumentDelayMilliseconds = 1400;
        private const int AuthoringDiagnosticsMediumDocumentDelayMilliseconds = 2200;
        private const int AuthoringDiagnosticsLargeDocumentDelayMilliseconds = 4000;
        private const int AuthoringDiagnosticsMediumCharacterThreshold = 20000;
        private const int AuthoringDiagnosticsMediumLineThreshold = 400;
        private const int AuthoringDiagnosticsLargeCharacterThreshold = 45000;
        private const int AuthoringDiagnosticsLargeLineThreshold = 800;
        private const int AuthoringDiagnosticsVeryLargeCharacterThreshold = 120000;
        private const int AuthoringDiagnosticsVeryLargeLineThreshold = 2000;
        private const int MaximumQueuedTerminalOutputEnvelopes = 8192;
        private const int EditorFoldingDebounceMilliseconds = 350;
        private const int EditorHoverDelayMilliseconds = 450;
        private const int EditorMetadataWarmupDebounceMilliseconds = 150;
        private const int MetadataToastShowDelayMilliseconds = 650;
        private const int MetadataToastSuccessDismissMilliseconds = 2200;
        private const int MetadataToastWarningDismissMilliseconds = 6500;
        private const int MetadataToastFailureDismissMilliseconds = 9000;
        private const string ThemeAccentPrimaryResourceKey = "Theme.Accent.Primary";
        private const string ThemeBorderStrongResourceKey = "Theme.Border.Strong";
        private const string ThemeIconAccentResourceKey = "Theme.Icon.Accent";
        private readonly ConcurrentQueue<TerminalOutputEnvelope> _terminalOutputEnvelopeQueue = new();
        private readonly Dispatcher _terminalOutputDispatcher;
        private MainWindowViewModel? _viewModel;
        private CommandPaletteWindow? _commandPaletteWindow;
        private int _terminalOutputDrainScheduled;
        private int _terminalOutputQueuedEnvelopeCount;
        private const string ThemeIconSuccessResourceKey = "Theme.Icon.Success";
        private const string ThemeStatusErrorBackgroundResourceKey = "Theme.Status.Error.Background";
        private const string ThemeStatusErrorBorderResourceKey = "Theme.Status.Error.Border";
        private const string ThemeStatusErrorForegroundResourceKey = "Theme.Status.Error.Foreground";
        private const string ThemeStatusWarningBackgroundResourceKey = "Theme.Status.Warning.Background";
        private const string ThemeStatusWarningBorderResourceKey = "Theme.Status.Warning.Border";
        private const string ThemeStatusWarningForegroundResourceKey = "Theme.Status.Warning.Foreground";
        private const string ThemeSurfacePrimaryResourceKey = "Theme.Surface.Primary";
        private const string ThemeTextPrimaryResourceKey = "Theme.Text.Primary";
        private const int DebugVariableValueMaxLength = 160;
        private const int DebugHoverValueMaxLength = 300;
        private const string RecentScriptMenuItemTagPrefix = "RecentScript:";
        private const double DefaultDebugPaneWindowWidth = 420;
        private const double DefaultDebugPaneWindowHeight = 480;
        private const double DefaultBottomToolWindowWidth = 640;
        private const double DefaultFloatingBottomToolWindowHeight = 360;
        private const double MinimumSavedBottomToolWindowWidth = 360;
        private const double MinimumSavedBottomToolWindowHeight = 220;
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
        private readonly Dictionary<TextEditor, LiveSyntaxPumpState> _liveSyntaxPumpStates = new();
        private readonly Dictionary<TextEditor, int> _liveSyntaxRequestVersions = new();
        private readonly Dictionary<TextEditor, AuthoringDiagnosticsPumpState> _authoringDiagnosticsPumpStates = new();
        private readonly Dictionary<TextEditor, int> _diagnosticsRequestVersions = new();
        private readonly Dictionary<TextEditor, DiagnosticLayerSnapshot> _liveSyntaxDiagnosticLayers = new();
        private readonly Dictionary<TextEditor, DiagnosticLayerSnapshot> _authoringDiagnosticLayers = new();
        private readonly Dictionary<TextEditor, DiagnosticLayerSnapshot> _analyzerDiagnosticLayers = new();
        private readonly HashSet<(Guid DocumentId, long Revision)> _liveAnalyzerEligibleRevisions = new();
        private readonly Dictionary<TextEditor, int> _editorRegistrationVersions = new();
        private readonly Dictionary<TextEditor, FoldingManager> _foldingManagers = new();
        private readonly Dictionary<TextEditor, CancellationTokenSource> _foldingCancellationSources = new();
        private readonly Dictionary<string, DebugVariableInfo> _liveDebugVariableCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly BraceFoldingStrategy _foldingStrategy = new();
        private readonly HashSet<TextEditor> _editorTextSynchronizationInProgress = new();
        private readonly IApplicationSettingsService _applicationSettingsService;
        private readonly IUiScaleService _uiScaleService;
        private readonly ApplicationSettings _loadedSettings;
        private readonly PowerShellIntelliSenseService _intelliSenseService = new();
        private readonly InProcessPowerShellSyntaxDiagnosticsService _liveSyntaxDiagnosticsService = new();
        private readonly PowerShellDiagnosticsService _diagnosticsService = new();
        private readonly ScriptDiagnosticStore _scriptDiagnosticStore = new();
        private PSScriptAnalyzerService? _psScriptAnalyzerService;
        private PSScriptAnalyzerDiagnosticsCoordinator? _psScriptAnalyzerCoordinator;
        private CancellationTokenSource? _manualAnalyzerCancellation;
        private PSScriptAnalyzerLiveAnalysisScheduler? _liveAnalyzerScheduler;
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
        private AboutWindow? _aboutWindow;
        private readonly AdministratorModeBannerState _administratorModeBannerState;
        private bool _allowWindowClose;
        private bool _terminalShutdownInProgress;
        private Task? _deferredInitializationTask;
        private bool _shellLayoutApplied;
        private double _lastKnownExplorerWidth = 220;
        private double _lastKnownConsoleHeight = DefaultConsoleHeight;
        private double _lastKnownConsoleSideWidth = DefaultConsoleSideWidth;
        private double _lastKnownBottomToolWindowHeight = DefaultBottomToolWindowHeight;
        private double _lastKnownDebugPanelWidth = DebugPanelWidth;
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
        private BottomToolWindow? _bottomToolWindow;
        private DebugPaneWindow? _debugPaneWindow;
        private ExportProgressWindow? _exportProgressWindow;
        private IReadOnlyList<DebugVariableInfo>? _currentDebugVariables;

        private enum WorkspaceLayoutMode
        {
            Default,
            EditorMaximized,
            ConsoleMaximized,
            HorizontalSplit,
            SideBySideSplit
        }

        private WorkspaceLayoutMode _workspaceLayoutMode = WorkspaceLayoutMode.HorizontalSplit;
        private enum BottomToolTab
        {
            Problems,
            DebugOutput,
            Activity
        }

        private BottomToolTab _selectedBottomToolTab = BottomToolTab.Problems;
        private bool _isBottomToolWindowVisible;
        private bool _isBottomToolWindowFloating;
        private bool _isSynchronizingBottomToolWindowTab;
        private Rect? _lastBottomToolWindowBounds;
        private IReadOnlyList<DebugCallStackFrame>? _currentDebugCallStack;
        private ObservableCollection<BreakpointRow>? _currentBreakpointRows;
        private int _selectedDebugTabIndex;
        private bool _isSynchronizingDebugTabSelection;
        private Rect? _lastDebugPaneWindowBounds;
        private readonly Dictionary<TextEditor, BraceMatchingRenderer> _braceMatchingRenderers = new();
        private bool _terminalIsReady;
        private bool _terminalHostAttached;
        private Task? _consoleWarmStartTask;
        private readonly object _consoleWarmStartLock = new();
        private bool _terminalIsActive;
        private EditorMetadataWarmupPhase _lastEditorMetadataWarmupPhase = EditorMetadataWarmupPhase.Idle;
        private PowerShellRuntimeInfo? _pendingEditorMetadataWarmupRuntime;
        private string? _pendingEditorMetadataWarmupIdentity;
        private string? _lastScheduledEditorMetadataWarmupIdentity;
        private DateTimeOffset _lastScheduledEditorMetadataWarmupAtUtc = DateTimeOffset.MinValue;
        private PowerShellCompletionEnginePhase _lastCompletionEnginePhase = PowerShellCompletionEnginePhase.Idle;
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
            SessionStoppedState,
            ApplicationShutdown
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

        public Visibility AdministratorModeBannerVisibility => _administratorModeBannerState.Visibility;

        public string AdministratorModeBannerDetail => _administratorModeBannerState.Detail;

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

        public MainWindow(IApplicationSettingsService applicationSettingsService, ApplicationSettings loadedSettings, IUiScaleService? uiScaleService = null)
        {
            DeveloperDiagnostics.LogMethodEntry("UI", "MainWindow constructor entry.");
            _terminalOutputDispatcher = Dispatcher;
            _applicationSettingsService = applicationSettingsService;
            _loadedSettings = loadedSettings ?? new ApplicationSettings();
            _scriptDiagnosticStore.Changed += ScriptDiagnosticStore_Changed;
            UpdateAnalyzerSettingsMenu();
            _uiScaleService = uiScaleService ?? UiScaleServiceHost.Current;
            var processElevation = CurrentProcessElevation.TryGetIsElevated();
            _administratorModeBannerState = AdministratorModeBannerState.Create(processElevation == true);
            DeveloperDiagnostics.LogDecision(
                "Startup",
                "AdministratorModeBanner",
                "Administrator-mode banner visibility was determined from the current process token.",
                processElevation == true ? "Visible" : processElevation == false ? "Collapsed" : "Unavailable",
                new Dictionary<string, object?>
                {
                    ["isElevated"] = processElevation,
                    ["bannerVisibility"] = _administratorModeBannerState.Visibility.ToString()
                });

            if (IsUsableLength(_loadedSettings.ExplorerWidth, MinimumExplorerWidth))
            {
                _lastKnownExplorerWidth = _loadedSettings.ExplorerWidth!.Value;
            }

            if (IsUsableLength(_loadedSettings.ConsoleHeight, MinimumConsoleHeight))
            {
                _lastKnownConsoleHeight = _loadedSettings.ConsoleHeight!.Value;
            }

            if (IsUsableLength(_loadedSettings.ConsoleSideWidth, MinimumConsoleSideWidth))
            {
                _lastKnownConsoleSideWidth = _loadedSettings.ConsoleSideWidth!.Value;
            }

            if (IsUsableLength(_loadedSettings.DockedBottomToolWindowHeight, MinimumBottomToolWindowHeight))
            {
                _lastKnownBottomToolWindowHeight = _loadedSettings.DockedBottomToolWindowHeight!.Value;
            }

            if (IsUsableLength(_loadedSettings.DockedDebugPanelWidth, MinimumDebugPanelWidth))
            {
                _lastKnownDebugPanelWidth = _loadedSettings.DockedDebugPanelWidth!.Value;
            }

            _selectedBottomToolTab = RestoreBottomToolTab(_loadedSettings.SelectedBottomToolTab);
            _isBottomToolWindowVisible = _loadedSettings.IsBottomToolWindowVisible;
            _isBottomToolWindowFloating = _loadedSettings.IsBottomToolWindowFloating;

            if (IsFiniteCoordinate(_loadedSettings.BottomToolWindowLeft) &&
                IsFiniteCoordinate(_loadedSettings.BottomToolWindowTop) &&
                IsUsableLength(_loadedSettings.BottomToolWindowWidth, MinimumSavedBottomToolWindowWidth) &&
                IsUsableLength(_loadedSettings.BottomToolWindowHeight, MinimumSavedBottomToolWindowHeight))
            {
                _lastBottomToolWindowBounds = new Rect(
                    _loadedSettings.BottomToolWindowLeft!.Value,
                    _loadedSettings.BottomToolWindowTop!.Value,
                    _loadedSettings.BottomToolWindowWidth!.Value,
                    _loadedSettings.BottomToolWindowHeight!.Value);
            }

            InitializeComponent();
            UpdateAnalyzerSettingsMenu();
            InitializeUiScaleMenu();
            _uiScaleService.ScaleChanged += UiScaleService_ScaleChanged;

            _intelliSenseService.MetadataWarmupStatusChanged += IntelliSenseService_MetadataWarmupStatusChanged;
            _intelliSenseService.CompletionEngineStatusChanged += IntelliSenseService_CompletionEngineStatusChanged;

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

        internal void AttachViewModel(MainWindowViewModel viewModel)
        {
            ArgumentNullException.ThrowIfNull(viewModel);
            Dispatcher.VerifyAccess();
            Volatile.Write(ref _viewModel, viewModel);
            DataContext = viewModel;
            DeveloperDiagnostics.LogInfo(
                "Startup",
                "MainWindow attached its view model reference before DataContext exposure.",
                new Dictionary<string, object?>
                {
                    ["viewModelType"] = viewModel.GetType().FullName
                });
        }

        private MainWindowViewModel? ViewModel => Volatile.Read(ref _viewModel);

        private void OnRawTerminalOutputReceived(int generation, string raw)
        {
            var rawOutput = raw ?? string.Empty;
            if (rawOutput.Length == 0)
            {
                return;
            }

            TerminalCriticalTrace.LogStage(
                "MainWindow.RawOutputReceivedSubscriber.Begin",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = generation,
                    ["outputCharacterLength"] = rawOutput.Length,
                    ["contentOmitted"] = true
                });
            var viewModel = Volatile.Read(ref _viewModel);
            if (viewModel is null)
            {
                TerminalCriticalTrace.LogStage(
                    "MainWindow.RawOutputReceivedSubscriber.DroppedNoViewModel",
                    new Dictionary<string, object?>
                    {
                        ["terminalSessionGeneration"] = generation,
                        ["rendererGeneration"] = generation,
                        ["outputCharacterLength"] = rawOutput.Length,
                        ["contentOmitted"] = true
                    });
                return;
            }

            viewModel.PublishInteractiveTerminalOutput(generation, rawOutput);
            TerminalCriticalTrace.LogStage(
                "MainWindow.RawOutputReceivedSubscriber.End",
                new Dictionary<string, object?>
                {
                    ["terminalSessionGeneration"] = generation,
                    ["rendererGeneration"] = generation,
                    ["outputCharacterLength"] = rawOutput.Length,
                    ["contentOmitted"] = true
                });
        }

        private void EnqueueTerminalOutputForRenderer(TerminalOutputEnvelope envelope)
        {
            TerminalCriticalTrace.LogStage(
                "MainWindow.EnqueueTerminalOutputForRenderer.Begin",
                CreateTerminalEnvelopeMetadata(envelope));
            var queuedCount = Interlocked.Increment(ref _terminalOutputQueuedEnvelopeCount);
            if (queuedCount > MaximumQueuedTerminalOutputEnvelopes)
            {
                Interlocked.Decrement(ref _terminalOutputQueuedEnvelopeCount);
                AppLogger.Warning("Terminal", $"Dropping terminal output envelope because the UI renderer queue is full. Sequence={envelope.Sequence}, Source={envelope.Source}, ContentOmitted=True.");
                DeveloperDiagnostics.LogWarning(
                    "Terminal",
                    "Terminal output envelope dropped before UI dispatch because the renderer queue was full.",
                    new Dictionary<string, object?>
                    {
                        ["sequence"] = envelope.Sequence,
                        ["source"] = envelope.Source.ToString(),
                        ["maxQueuedEnvelopes"] = MaximumQueuedTerminalOutputEnvelopes,
                        ["contentOmitted"] = true
                    });
                TerminalCriticalTrace.LogStage(
                    "MainWindow.EnqueueTerminalOutputForRenderer.DroppedQueueFull",
                    CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                    {
                        ["queuedEnvelopeCount"] = queuedCount,
                        ["maximumQueuedEnvelopes"] = MaximumQueuedTerminalOutputEnvelopes
                    }));
                return;
            }

            if (_terminalOutputDispatcher.HasShutdownStarted ||
                _terminalOutputDispatcher.HasShutdownFinished)
            {
                Interlocked.Decrement(ref _terminalOutputQueuedEnvelopeCount);
                AppLogger.Warning("Terminal", $"Dropping terminal output envelope because the UI dispatcher is shutting down. Sequence={envelope.Sequence}, Source={envelope.Source}, ContentOmitted=True.");
                DeveloperDiagnostics.LogWarning(
                    "Terminal",
                    "Terminal output envelope dropped because the UI dispatcher is shutting down.",
                    new Dictionary<string, object?>
                    {
                        ["sequence"] = envelope.Sequence,
                        ["source"] = envelope.Source.ToString(),
                        ["contentOmitted"] = true
                    });
                TerminalCriticalTrace.LogStage(
                    "MainWindow.EnqueueTerminalOutputForRenderer.DroppedDispatcherShutdown",
                    CreateTerminalEnvelopeMetadata(envelope));
                return;
            }

            _terminalOutputEnvelopeQueue.Enqueue(envelope);
            TerminalCriticalTrace.LogStage(
                "MainWindow.EnqueueTerminalOutputForRenderer.Queued",
                CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                {
                    ["queuedEnvelopeCount"] = queuedCount,
                    ["dispatcherShutdownStarted"] = _terminalOutputDispatcher.HasShutdownStarted,
                    ["dispatcherShutdownFinished"] = _terminalOutputDispatcher.HasShutdownFinished
                }));
            if (Interlocked.Exchange(ref _terminalOutputDrainScheduled, 1) == 1)
            {
                TerminalCriticalTrace.LogStage(
                    "MainWindow.DispatcherDrain.AlreadyScheduled",
                    CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                    {
                        ["queuedEnvelopeCount"] = queuedCount
                    }));
                return;
            }

            try
            {
                _terminalOutputDispatcher.BeginInvoke(
                    new Action(DrainTerminalOutputForRenderer),
                    DispatcherPriority.Background);
                TerminalCriticalTrace.LogStage(
                    "MainWindow.DispatcherBeginInvoke.Scheduled",
                    CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                    {
                        ["queuedEnvelopeCount"] = queuedCount,
                        ["dispatcherPriority"] = DispatcherPriority.Background.ToString()
                    }));
            }
            catch (Exception ex)
            {
                Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
                TerminalCriticalTrace.LogException(
                    "MainWindow.DispatcherBeginInvoke.Exception",
                    ex,
                    CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                    {
                        ["queuedEnvelopeCount"] = queuedCount,
                        ["uiDispatcherTransition"] = "before-dispatcher-drain"
                    }));
                AppLogger.Error("Terminal", "Unable to schedule terminal output renderer drain.", ex);
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Unable to schedule terminal output renderer drain.",
                    new Dictionary<string, object?> { ["contentOmitted"] = true });
            }
        }

        private void DrainTerminalOutputForRenderer()
        {
            TerminalCriticalTrace.LogStage(
                "MainWindow.DrainTerminalOutputForRenderer.Begin",
                new Dictionary<string, object?>
                {
                    ["queuedEnvelopeCount"] = Volatile.Read(ref _terminalOutputQueuedEnvelopeCount)
                });
            try
            {
                while (_terminalOutputEnvelopeQueue.TryDequeue(out var envelope))
                {
                    Interlocked.Decrement(ref _terminalOutputQueuedEnvelopeCount);
                    try
                    {
                        TerminalCriticalTrace.LogStage(
                            "MainWindow.DrainTerminalOutputForRenderer.EnvelopeBegin",
                            CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                            {
                                ["uiDispatcherTransition"] = "during-dispatcher-drain"
                            }));
                        if (envelope.Source == TerminalOutputSource.StructuredEditor)
                        {
                            TerminalConsole.WriteStructuredOutput(envelope.RendererGeneration, envelope.Payload);
                            TerminalCriticalTrace.LogStage(
                                "MainWindow.DrainTerminalOutputForRenderer.EnvelopeEnd",
                                CreateTerminalEnvelopeMetadata(envelope));
                            continue;
                        }

                        TerminalConsole.WriteRaw(envelope.InteractiveTerminalSessionGeneration, envelope.Payload);
                        TerminalCriticalTrace.LogStage(
                            "MainWindow.DrainTerminalOutputForRenderer.EnvelopeEnd",
                            CreateTerminalEnvelopeMetadata(envelope));
                    }
                    catch (Exception ex)
                    {
                        TerminalCriticalTrace.LogException(
                            "MainWindow.DrainTerminalOutputForRenderer.EnvelopeException",
                            ex,
                            CreateTerminalEnvelopeMetadata(envelope, new Dictionary<string, object?>
                            {
                                ["uiDispatcherTransition"] = "during-dispatcher-drain"
                            }));
                        AppLogger.Error("Terminal", $"Terminal output renderer consumer failed. Sequence={envelope.Sequence}, Source={envelope.Source}, ContentOmitted=True.", ex);
                        DeveloperDiagnostics.LogException(
                            "Terminal",
                            ex,
                            "Terminal output renderer consumer failed during UI dispatcher drain.",
                            new Dictionary<string, object?>
                            {
                                ["sequence"] = envelope.Sequence,
                                ["source"] = envelope.Source.ToString(),
                                ["rendererGeneration"] = envelope.RendererGeneration,
                                ["interactiveTerminalSessionGeneration"] = envelope.InteractiveTerminalSessionGeneration,
                                ["brokerSessionGeneration"] = envelope.BrokerSessionGeneration,
                                ["contentOmitted"] = true
                            });
                    }
                }
            }
            finally
            {
                TerminalCriticalTrace.LogStage(
                    "MainWindow.DrainTerminalOutputForRenderer.End",
                    new Dictionary<string, object?>
                    {
                        ["queuedEnvelopeCount"] = Volatile.Read(ref _terminalOutputQueuedEnvelopeCount),
                        ["queueIsEmpty"] = _terminalOutputEnvelopeQueue.IsEmpty
                    });
                Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
                if (!_terminalOutputEnvelopeQueue.IsEmpty &&
                    Interlocked.Exchange(ref _terminalOutputDrainScheduled, 1) == 0)
                {
                    try
                    {
                        if (!_terminalOutputDispatcher.HasShutdownStarted &&
                            !_terminalOutputDispatcher.HasShutdownFinished)
                        {
                            _terminalOutputDispatcher.BeginInvoke(
                                new Action(DrainTerminalOutputForRenderer),
                                DispatcherPriority.Background);
                            TerminalCriticalTrace.LogStage(
                                "MainWindow.DispatcherBeginInvoke.Rescheduled",
                                new Dictionary<string, object?>
                                {
                                    ["queuedEnvelopeCount"] = Volatile.Read(ref _terminalOutputQueuedEnvelopeCount),
                                    ["dispatcherPriority"] = DispatcherPriority.Background.ToString()
                                });
                        }
                        else
                        {
                            Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
                        }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Exchange(ref _terminalOutputDrainScheduled, 0);
                        TerminalCriticalTrace.LogException(
                            "MainWindow.DispatcherBeginInvoke.RescheduleException",
                            ex,
                            new Dictionary<string, object?>
                            {
                                ["queuedEnvelopeCount"] = Volatile.Read(ref _terminalOutputQueuedEnvelopeCount),
                                ["uiDispatcherTransition"] = "after-dispatcher-drain"
                            });
                        AppLogger.Error("Terminal", "Unable to reschedule terminal output renderer drain.", ex);
                        DeveloperDiagnostics.LogException(
                            "Terminal",
                            ex,
                            "Unable to reschedule terminal output renderer drain.",
                            new Dictionary<string, object?> { ["contentOmitted"] = true });
                    }
                }
            }
        }

        private static Dictionary<string, object?> CreateTerminalEnvelopeMetadata(
            TerminalOutputEnvelope envelope,
            IReadOnlyDictionary<string, object?>? additionalMetadata = null)
        {
            var metadata = new Dictionary<string, object?>
            {
                ["source"] = envelope.Source.ToString(),
                ["sequence"] = envelope.Sequence,
                ["brokerSessionGeneration"] = envelope.BrokerSessionGeneration,
                ["terminalSessionGeneration"] = envelope.InteractiveTerminalSessionGeneration,
                ["rendererGeneration"] = envelope.RendererGeneration,
                ["sourceSequence"] = envelope.SourceSequence,
                ["streamKind"] = envelope.StreamKind.ToString(),
                ["outputCharacterLength"] = envelope.Payload?.Length ?? 0,
                ["contentOmitted"] = true
            };

            if (additionalMetadata is not null)
            {
                foreach (var item in additionalMetadata)
                {
                    metadata[item.Key] = item.Value;
                }
            }

            return metadata;
        }

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
                ViewModel.ExeExportProgressChanged -= ViewModel_ExeExportProgressChanged;
                ViewModel.ExeExportProgressChanged += ViewModel_ExeExportProgressChanged;
                DeveloperDiagnostics.LogInfo("Startup", "ViewModel bound to synchronization context and PropertyChanged handler attached.");
                ViewModel.ProcessStartupDocumentRecovery();
                ContextHelp.ValidateWindowTopics(this);
                ApplyExplorerVisibilityLayout();
                RefreshDebugCommandAvailability(false);
                StartEditorMetadataWarmup();
                UpdateRefreshEditorMetadataCommandAvailability();
                StartupTimingLogger.Log("MainWindow", $"View model hookup completed in {startupStopwatch.ElapsedMilliseconds} ms");

                // ── Wire up xterm.js terminal control ────────────────────────────
                // Session controls do not expose a generic text-write delegate.
                ViewModel.SetTerminalSessionControls(
                    clearTerminal: ()   => Dispatcher.BeginInvoke(() =>
                    {
                        TerminalConsole.Clear();
                        TerminalConsole.FocusTerminal();
                    }),
                    focusTerminal: ()   => Dispatcher.BeginInvoke(() => TerminalConsole.FocusTerminal()),
                    normalizeTerminalInteractiveState: () => Dispatcher.BeginInvoke(() => TerminalConsole.NormalizeInteractiveState()),
                    beginTerminalOutputGeneration: TerminalConsole.BeginTerminalOutputGeneration,
                    invalidateTerminalOutputGeneration: TerminalConsole.InvalidateTerminalOutputGeneration,
                    isTerminalFocused: () => TerminalConsole.IsKeyboardFocusWithin,
                    terminalFocusRestoreReadiness: GetTerminalFocusRestoreReadiness,
                    restoreTerminalFocus: (generation, cancellationToken) =>
                        TerminalConsole.RestoreTerminalFocusAsync(generation, cancellationToken));

                // Forward raw (ANSI-intact) ConPTY output to xterm.js.
                // TerminalControl applies its own bounded dispatcher/WebView flow control,
                // so the reader callback does not queue one dispatcher operation per chunk.
                ViewModel.SubscribeRawOutput(OnRawTerminalOutputReceived);
                ViewModel.TerminalOutputPublished += EnqueueTerminalOutputForRenderer;

                // Forward xterm.js keystrokes to ConPTY stdin.
                TerminalConsole.UserInput += async data =>
                {
                    AppLogger.Debug("Terminal", $"MainWindow received terminal input for forwarding. Length={data.Length}, ContentOmitted=True.");
                    DeveloperDiagnostics.LogUserAction(
                        "Terminal",
                        "TerminalInput",
                        "Terminal input received for forwarding to the view model.",
                        new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(data))
                        {
                            ["focusedElement"] = DescribeFocusedElement()
                        });
                    if (ViewModel is not null)
                    {
                        try
                        {
                            await ViewModel.WriteRawInputAsync(data).ConfigureAwait(false);
                            AppLogger.Debug("Terminal", "MainWindow forwarded terminal input to the view model.");
                            DeveloperDiagnostics.LogInfo("Terminal", "Terminal input forwarded to view model.");
                        }
                        catch (Exception ex)
                        {
                            AppLogger.Warning(
                                "Terminal",
                                $"MainWindow could not forward terminal input. Length={data.Length}, ExceptionType={ex.GetType().Name}, ContentOmitted=True.");
                        }
                    }
                };

                TerminalConsole.TerminalActivated += source => OnTerminalActivated(source);
                TerminalConsole.AppShortcutRequested += command => HandleTerminalAppShortcutRequested(command);
                TerminalConsole.TerminalRendererUnavailable += reason =>
                {
                    _terminalIsReady = false;
                    AppLogger.Warning("Terminal", $"Integrated terminal renderer became unavailable. Reason={reason}; ConPTY recovery remains independent until Reset Console creates a fresh renderer.");
                    DeveloperDiagnostics.LogWarning(
                        "Terminal",
                        "Integrated terminal renderer became unavailable; ConPTY session recovery remains independent.",
                        new Dictionary<string, object?> { ["reason"] = reason });
                };

                // Resize ConPTY when xterm.js reports a new grid size.
                TerminalConsole.TerminalResized += (cols, rows) =>
                {
                    ViewModel?.ResizeConsole(cols, rows);
                };

                // When xterm.js signals ready, flush any warm-started PowerShell
                // output that arrived before the WebView terminal finished loading.
                // The ConPTY session is requested as soon as the host is attached below
                // so pwsh.exe startup can overlap with WebView2/xterm.js initialization.
                TerminalConsole.TerminalReady += () =>
                {
                    _terminalIsReady = true;
                    AppLogger.Debug("Terminal", "MainWindow received terminal-ready signal.");
                    DeveloperDiagnostics.LogStateTransition("Terminal", "TerminalReady", "Initializing", "Ready", "Terminal ready signal received.");
                    // Apply the current app theme to the terminal colour scheme.
                    TerminalConsole.ApplyAppTheme(_themeService.CurrentTheme);
                    ViewModel?.NotifyTerminalRendererReady();
                    RequestConsoleWarmStart("TerminalReadyFallback");
                };

                // When the app theme changes, update the terminal colour scheme to match.
                _themeService.ThemeChanged += themeName =>
                    Dispatcher.BeginInvoke(() => TerminalConsole.ApplyAppTheme(themeName));

                // Notify the service that a host is attached (triggers session bookkeeping).
                var hostAttachStopwatch = Stopwatch.StartNew();
                var terminalHostWidth = TerminalConsole.ActualWidth > 0
                    ? Math.Max(1, (int)Math.Round(TerminalConsole.ActualWidth))
                    : 120;
                var terminalHostHeight = TerminalConsole.ActualHeight > 0
                    ? Math.Max(1, (int)Math.Round(TerminalConsole.ActualHeight))
                    : 30;
                DeveloperDiagnostics.LogInfo(
                    "Terminal",
                    "Initializing terminal host with the measured WPF terminal bounds.",
                    new Dictionary<string, object?>
                    {
                        ["widthPixels"] = terminalHostWidth,
                        ["heightPixels"] = terminalHostHeight,
                        ["usedFallbackBounds"] = TerminalConsole.ActualWidth <= 0 || TerminalConsole.ActualHeight <= 0
                    });
                await ViewModel.InitializeTerminalHostAsync(IntPtr.Zero, terminalHostWidth, terminalHostHeight);
                _terminalHostAttached = true;
                DeveloperDiagnostics.LogOperationStop(
                    "Startup",
                    "InitializeTerminalHost",
                    "Terminal host initialization completed.",
                    hostAttachStopwatch.ElapsedMilliseconds);
                StartupTimingLogger.Log("MainWindow", $"Terminal host attached in {hostAttachStopwatch.ElapsedMilliseconds} ms");
                RequestConsoleWarmStart("TerminalHostAttached");

                StartDeferredInitialization(ViewModel);
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
                ShowIdeMessage("Startup Error", $"PS7 ScriptDesk failed during startup.\n\n{ex}");
            }
        }

        private void StartDeferredInitialization(MainWindowViewModel viewModel)
        {
            if (_deferredInitializationTask is not null)
            {
                return;
            }

            _deferredInitializationTask = viewModel.InitializeAsync();
            _ = ObserveDeferredInitializationAsync(_deferredInitializationTask);
        }

        private static async Task ObserveDeferredInitializationAsync(Task initializationTask)
        {
            try
            {
                await initializationTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Startup", "Deferred ViewModel initialization faulted outside its expected recovery boundary.", ex);
                DeveloperDiagnostics.LogException("Startup", ex, "Deferred ViewModel initialization faulted outside its expected recovery boundary.");
            }
        }

        private void RequestConsoleWarmStart(string reason)
        {
            if (!_terminalHostAttached)
            {
                DeveloperDiagnostics.LogInfo(
                    "Startup",
                    "Console warm-start request deferred because the terminal host has not been attached yet.",
                    new Dictionary<string, object?> { ["reason"] = reason });
                return;
            }

            var viewModel = ViewModel;
            if (viewModel is null)
            {
                DeveloperDiagnostics.LogInfo(
                    "Startup",
                    "Console warm-start request skipped because no view model is available.",
                    new Dictionary<string, object?> { ["reason"] = reason });
                return;
            }

            lock (_consoleWarmStartLock)
            {
                if (_consoleWarmStartTask is { IsCompleted: false })
                {
                    DeveloperDiagnostics.LogInfo(
                        "Startup",
                        "Console warm-start request skipped because a console start is already in progress.",
                        new Dictionary<string, object?> { ["reason"] = reason });
                    return;
                }

                _consoleWarmStartTask = Task.Run(async () =>
                {
                    var stopwatch = Stopwatch.StartNew();
                    var runtime = viewModel.EffectiveRuntimeInfo;
                    DeveloperDiagnostics.LogAsyncBoundary(
                        "Startup",
                        "ConsoleWarmStart",
                        "Console warm-start launched.",
                        "AsyncStart",
                        new Dictionary<string, object?>
                        {
                            ["reason"] = reason,
                            ["runtimeDisplayName"] = runtime?.DisplayName,
                            ["runtimePath"] = runtime?.ExecutablePath,
                            ["terminalIsReady"] = _terminalIsReady
                        });
                    StartupTimingLogger.Log("MainWindow", $"Console warm-start requested. Reason={reason}; Runtime={runtime?.DisplayName ?? "(none)"}; TerminalReady={_terminalIsReady}.");

                    try
                    {
                        await viewModel.EnsureConsoleRestoredAsync().ConfigureAwait(false);
                        DeveloperDiagnostics.LogOperationStop(
                            "Startup",
                            "ConsoleWarmStart",
                            "Console warm-start completed.",
                            stopwatch.ElapsedMilliseconds,
                            new Dictionary<string, object?>
                            {
                                ["reason"] = reason,
                                ["runtimeDisplayName"] = viewModel.EffectiveRuntimeInfo?.DisplayName,
                                ["terminalIsReady"] = _terminalIsReady
                            });
                        StartupTimingLogger.Log("MainWindow", $"Console warm-start completed in {stopwatch.ElapsedMilliseconds} ms. Reason={reason}.");
                    }
                    catch (Exception ex)
                    {
                        DeveloperDiagnostics.LogException("Startup", ex, $"Console warm-start failed. Reason={reason}.");
                        StartupTimingLogger.Log("MainWindow", $"Console warm-start failed after {stopwatch.ElapsedMilliseconds} ms. Reason={reason}; Error={ex}");
                    }
                });
            }
        }

        private void Window_ContentRendered(object? sender, EventArgs e)
        {
            DeveloperDiagnostics.LogEventHandlerEntry("UI", "Window_ContentRendered", "Window content rendered.");
            DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_ContentRendered", "Window content rendered handler completed.");
        }

        private void ResetConsoleButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Capture this before WPF transfers focus from the WebView2/xterm host to
            // the button. The ViewModel will consume it only for this replacement session.
            TerminalConsole.ResetRendererForRetry();
            ViewModel?.PrepareTerminalFocusRestoreForReset();
        }

        private void Window_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (ViewModel is null || IsResetConsoleFocusTarget(e.NewFocus))
            {
                return;
            }

            ViewModel.NotifyTerminalFocusOwnershipChanged(IsTerminalFocusTarget(e.NewFocus));
        }

        private TerminalFocusRestoreReadiness GetTerminalFocusRestoreReadiness()
        {
            return new TerminalFocusRestoreReadiness(
                RendererReady: _terminalIsReady,
                ConsoleVisible: ConsoleBottomPaneTab.IsChecked == true && TerminalConsole.IsVisible,
                ApplicationActive: IsActive && !_allowWindowClose && !_terminalShutdownInProgress,
                ModalDialogOpen: System.Windows.Interop.ComponentDispatcher.IsThreadModal);
        }

        private bool IsTerminalFocusTarget(IInputElement? focusedElement)
        {
            return focusedElement is DependencyObject dependencyObject &&
                   (ReferenceEquals(dependencyObject, TerminalConsole) || TerminalConsole.IsAncestorOf(dependencyObject));
        }

        private bool IsResetConsoleFocusTarget(IInputElement? focusedElement)
        {
            return focusedElement is DependencyObject dependencyObject &&
                   (ReferenceEquals(dependencyObject, ResetConsoleButton) || ResetConsoleButton.IsAncestorOf(dependencyObject));
        }


        private async void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (ViewModel is null)
            {
                return;
            }

            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == (ModifierKeys.Control | ModifierKeys.Shift) &&
                e.Key == Key.P)
            {
                e.Handled = true;
                OpenCommandPalette();
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
            var isAlt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

            // xterm owns terminal-focused key combinations so PSReadLine and console
            // applications receive their normal Ctrl/F-key input. The terminal's one
            // documented app override (Ctrl+Shift+F6) is raised by xterm itself.
            if (TerminalConsole.IsKeyboardFocusWithin)
            {
                return;
            }

            // Ctrl+Alt variants are reserved for application UI Scale. The existing
            // Ctrl+=, Ctrl+-, and Ctrl+0 shortcuts remain editor zoom controls.
            if (isCtrl && isAlt && !isShift && (e.Key == Key.OemPlus || e.Key == Key.Add))
            {
                e.Handled = true;
                ViewModel.IncreaseUiScaleCommand.Execute(null);
                return;
            }

            if (isCtrl && isAlt && !isShift && (e.Key == Key.OemMinus || e.Key == Key.Subtract))
            {
                e.Handled = true;
                ViewModel.DecreaseUiScaleCommand.Execute(null);
                return;
            }

            if (isCtrl && isAlt && !isShift && e.Key == Key.D0)
            {
                e.Handled = true;
                ViewModel.ResetUiScaleCommand.Execute(null);
                return;
            }

            // Handle the app-level find/replace shortcuts at the window level so they
            // work even when focus is not inside the AvalonEdit editor. The editor
            // preview-key handler keeps these shortcuts as a fallback for older paths.
            if (isCtrl && !isShift && e.Key == Key.F)
            {
                e.Handled = true;
                OpenFindReplaceWindow(showReplace: false);
                return;
            }

            if (isCtrl && !isShift && e.Key == Key.H)
            {
                e.Handled = true;
                OpenFindReplaceWindow(showReplace: true);
                return;
            }

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
            ShowIdeMessage("Recent Script", $"{message}{Environment.NewLine}{Environment.NewLine}{recentPath}");
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
                var contentChanged = !string.Equals(tab.Content, editorText, StringComparison.Ordinal);
                var lineCount = editorTextEditor.Document?.LineCount ?? 1;
                tab.UpdateContentFromEditor(editorText, lineCount);
                if (contentChanged) _liveAnalyzerEligibleRevisions.Add((tab.DiagnosticDocument.DocumentId, tab.DiagnosticDocument.Revision));
            }

            UpdateEditorCaretMetrics(editorTextEditor);

            // Keep the last parser tokens visible until the live syntax pump replaces
            // them. Clearing tokens on every keystroke made the editor appear slower
            // because syntax coloring disappeared while the background parse was pending.

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
                    case '"': AutoInsertClosingDelimiter(editorTextEditor, '"'); break;
                    case '\'': AutoInsertClosingDelimiter(editorTextEditor, '\''); break;
                }
            }

            if (ch == '(' || ch == ',' || ch == ' ' || ch == '-')
            {
                var registrationVersion = _editorRegistrationVersions.TryGetValue(editorTextEditor, out var version) ? version : 0;
                _ = ObserveFireAndForget(
                    ShowEditorQuickInfoAtCaretAsync(editorTextEditor, updateStatusOnly: true),
                    "editor quick-info update",
                    new Dictionary<string, object?>
                    {
                        ["editorRegistrationVersion"] = registrationVersion,
                        ["editorIdentity"] = RuntimeHelpers.GetHashCode(editorTextEditor)
                    });
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

                if (editor.SelectionLength > 0 && TryGetMatchingDelimiter(ch, out var surroundCloser))
                {
                    e.Handled = true;
                    ApplyEditorCommand(editor, "SurroundSelection", () => EditorProductivityCommands.SurroundSelection(editor.Document!, editor.SelectionStart, editor.SelectionLength, ch, surroundCloser));
                    return;
                }

                // Skip over an auto-inserted closing delimiter when the caret is already
                // sitting in front of that exact character (prevents double-brace syndrome).
                if (ch == '}' || ch == ')' || ch == ']')
                {
                    var closingDocument = editor?.Document;
                    if (closingDocument is not null &&
                        (editor?.CaretOffset ?? 0) < closingDocument.TextLength &&
                        closingDocument.GetCharAt(editor?.CaretOffset ?? 0) == ch)
                    {
                        editor!.CaretOffset++;
                        e.Handled = true;
                        return;
                    }
                }

                var document = editor?.Document;
                if ((ch == '\'' || ch == '"') &&
                    document is not null &&
                    (editor?.CaretOffset ?? 0) < document.TextLength &&
                    document.GetCharAt(editor?.CaretOffset ?? 0) == ch)
                {
                    editor!.CaretOffset++;
                    e.Handled = true;
                    return;
                }

                if (_activeCompletionWindow is not null && ShouldDismissActivePathCompletionForTextInput(ch))
                {
                    CloseEditorCompletion("Whitespace typed while path completion selected");
                    return;
                }

                // Let the active completion window commit when the user types a non-identifier character.
                if (_activeCompletionWindow is not null &&
                    ShouldCommitCompletionForTextInput(ch))
                {
                    _activeCompletionWindow.CompletionList.RequestInsertion(e);
                }
            }
        }

        private static bool TryGetMatchingDelimiter(char opener, out char closer)
        {
            closer = opener switch
            {
                '(' => ')',
                '[' => ']',
                '{' => '}',
                '"' => '"',
                '\'' => '\'',
                _ => '\0'
            };
            return closer != '\0';
        }

        private bool ShouldDismissActivePathCompletionForTextInput(char ch)
        {
            return _activeCompletionWindow?.CompletionList.SelectedItem is PowerShellCompletionData completionData &&
                   ShouldDismissPathCompletionForTextInput(completionData.Kind, ch);
        }

        internal static bool ShouldDismissPathCompletionForTextInput(CompletionItemKind completionKind, char ch)
        {
            return char.IsWhiteSpace(ch) &&
                   completionKind is CompletionItemKind.ProviderItem or CompletionItemKind.ProviderContainer;
        }

        internal static bool ShouldCommitCompletionForTextInput(char ch)
        {
            return !char.IsLetterOrDigit(ch) && ch != '_' && ch != '-' && ch != '?' && ch != '$';
        }

        private async void EditorTextEditor_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (sender is not TextEditor editorTextEditor)
            {
                return;
            }

            var key = EditorShortcutRouting.ResolveKey(e.Key, e.SystemKey);
            var modifiers = Keyboard.Modifiers;

             if (_terminalIsActive)
            {
                AppLogger.Debug(
                    "EditorCompletion",
                    $"Editor preview key handler observed input while terminal is active. Key={e.Key}, Modifiers={Keyboard.Modifiers}, EditorFocused={editorTextEditor.IsKeyboardFocusWithin}.");
                return;
            }

            if (_activeCompletionWindow is not null && key == Key.Tab)
            {
                e.Handled = true;
                _activeCompletionWindow.CompletionList.RequestInsertion(e);
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.Space)
            {
                e.Handled = true;
                ForceCompletionNow(editorTextEditor, "Ctrl+Space");
                return;
            }

            if (modifiers == ModifierKeys.None && key == Key.F1)
            {
                e.Handled = true;
                var quickInfoShown = await ShowEditorQuickInfoAtCaretAsync(editorTextEditor, updateStatusOnly: false).ConfigureAwait(true);
                if (!quickInfoShown)
                {
                    ContextHelp.OpenTopic(this, "Editor.Area");
                }
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.OemQuestion)
            {
                e.Handled = true;
                ToggleCommentForEditor(editorTextEditor);
                return;
            }

            if (modifiers == ModifierKeys.Shift && key == Key.Tab)
            {
                if (HasMultiLineEditorSelection(editorTextEditor))
                {
                    e.Handled = true;
                    ApplyEditorCommand(editorTextEditor, "Outdent", () => EditorProductivityCommands.Outdent(editorTextEditor.Document, editorTextEditor.SelectionStart, editorTextEditor.SelectionLength, editorTextEditor.Options.IndentationSize));
                }
                return;
            }

            if (modifiers == ModifierKeys.None && key == Key.Tab && HasMultiLineEditorSelection(editorTextEditor))
            {
                e.Handled = true;
                ApplyEditorCommand(editorTextEditor, "Indent", () => EditorProductivityCommands.Indent(editorTextEditor.Document, editorTextEditor.SelectionStart, editorTextEditor.SelectionLength, editorTextEditor.Options.IndentationSize));
                return;
            }

            var registeredShortcutRegistry = CreateEditorCommandRegistry(editorTextEditor);
            if (EditorShortcutRouting.TryGetRegisteredCommand(registeredShortcutRegistry, e.Key, e.SystemKey, modifiers, out var registeredShortcut) &&
                registeredShortcut!.CanExecute())
            {
                e.Handled = true;
                DeveloperDiagnostics.LogUserAction(
                    "Editor",
                    "RegisteredShortcut",
                    $"Editor shortcut routed to registered command {registeredShortcut.Id}.",
                    new Dictionary<string, object?>
                    {
                        ["commandId"] = registeredShortcut.Id,
                        ["key"] = key.ToString(),
                        ["modifiers"] = modifiers.ToString()
                    });
                registeredShortcut.Execute();
                return;
            }

            if (EditorShortcutRouting.TryGetMoveLineDirection(key, modifiers, out var moveDirection))
            {
                e.Handled = true;
                if (moveDirection < 0)
                {
                    MoveLineUp(editorTextEditor);
                }
                else
                {
                    MoveLineDown(editorTextEditor);
                }
                return;
            }

            if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.Down)
            {
                e.Handled = true;
                ApplyEditorCommand(editorTextEditor, "DuplicateDown", () => EditorProductivityCommands.DuplicateLines(editorTextEditor.Document, editorTextEditor.SelectionStart, editorTextEditor.SelectionLength, 1));
                return;
            }

            if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.Up)
            {
                e.Handled = true;
                ApplyEditorCommand(editorTextEditor, "DuplicateUp", () => EditorProductivityCommands.DuplicateLines(editorTextEditor.Document, editorTextEditor.SelectionStart, editorTextEditor.SelectionLength, -1));
                return;
            }

            if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.K)
            {
                e.Handled = true;
                ApplyEditorCommand(editorTextEditor, "DeleteLine", () => EditorProductivityCommands.DeleteLines(editorTextEditor.Document, editorTextEditor.SelectionStart, editorTextEditor.SelectionLength));
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.C)
            {
                e.Handled = TryCopySelectedEditorTextToClipboard(editorTextEditor);
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.X)
            {
                e.Handled = TryCutSelectedEditorTextToClipboard(editorTextEditor);
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.V)
            {
                e.Handled = TryPasteClipboardTextIntoEditor(editorTextEditor);
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.A)
            {
                e.Handled = true;
                editorTextEditor.SelectAll();
                UpdateEditorCaretMetrics(editorTextEditor);
                return;
            }

            if (modifiers == ModifierKeys.Control && key == Key.F)
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

        private void CheckForUpdates_Click(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.LogUserAction(
                "StoreUpdate",
                "StartupStatusRequested",
                "User requested the Microsoft Store update status captured during application startup.");

            var snapshot = StoreUpdateStartupState.Read();
            if (snapshot.CheckInProgress)
            {
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = "Startup update check is still in progress.";
                }

                ShowIdeMessage("PS7 ScriptDesk - Store Update Status",
                    "PS7 ScriptDesk is still completing the one-time Microsoft Store update check that started with the application.\n\n" +
                    "Open Store Update Status again in a moment.");
                return;
            }

            if (snapshot.Service is null || snapshot.Result is null)
            {
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = "No startup update status is available.";
                }

                ShowIdeMessage("PS7 ScriptDesk - Store Update Status",
                    "No Microsoft Store update status was captured for this application session.\n\n" +
                    "ScriptDesk checks Microsoft Store once after startup and does not poll again during the session.");
                return;
            }

            var checkResult = snapshot.Result;
            var resultKind = ClassifyStoreUpdateResult(checkResult);
            DeveloperDiagnostics.LogDecision(
                "StoreUpdate",
                "StartupStatusDisplay",
                $"Displayed cached startup Store update status '{resultKind}'.",
                resultKind,
                BuildStoreUpdateDiagnosticsProperties(checkResult, "StartupCache"));

            ShowManualStoreUpdateWindow(snapshot.Service, checkResult);

            if (ViewModel is not null)
            {
                ViewModel.StatusText = resultKind switch
                {
                    "MandatoryUpdate" => "Mandatory update detected at startup.",
                    "UpdateAvailable" => "Update available.",
                    "NoUpdateAvailable" => "No updates were available at startup.",
                    "ManualInstructions" => "Startup Store status requires manual Store instructions.",
                    "LocalBuildUnavailable" => "Store updates are not available for this local build.",
                    "CheckUnavailable" => "Startup Store update check was unavailable.",
                    "Failure" => "Startup Store update check failed.",
                    _ => "Store update status displayed."
                };
            }
        }

        private void ShowManualStoreUpdateWindow(StoreUpdateService storeUpdateService, StoreUpdateCheckResult checkResult)
        {
            var updateWindow = new StoreUpdateWindow(storeUpdateService, checkResult, isMandatory: checkResult.HasMandatoryUpdate)
            {
                Owner = this
            };

            var resultKind = ClassifyStoreUpdateResult(checkResult);
            DeveloperDiagnostics.LogDecision(
                "StoreUpdate",
                "ManualUpdateWindowDisplay",
                "Manual Store update result window displayed.",
                checkResult.HasMandatoryUpdate ? "ShownMandatoryDialog" : "ShownModelessWindow",
                BuildStoreUpdateDiagnosticsProperties(checkResult, "HelpMenu"));

            if (ViewModel is not null)
            {
                ViewModel.StatusText = resultKind switch
                {
                    "MandatoryUpdate" => "Mandatory update detected.",
                    "UpdateAvailable" => "Update available.",
                    "NoUpdateAvailable" => "No updates available.",
                    "ManualInstructions" => "Manual Store update instructions shown.",
                    "LocalBuildUnavailable" => "Store updates are not available for this local build.",
                    "CheckUnavailable" => "Update check unavailable.",
                    "Failure" => "Update check failed.",
                    _ => "Update check complete."
                };
            }

            if (checkResult.HasMandatoryUpdate)
            {
                updateWindow.ShowDialog();
                System.Windows.Application.Current?.Shutdown(0);
                return;
            }

            updateWindow.Show();
            updateWindow.Activate();
        }

        private static IReadOnlyDictionary<string, object?> BuildStoreUpdateDiagnosticsProperties(StoreUpdateCheckResult? checkResult, string invocation)
        {
            var properties = new Dictionary<string, object?>
            {
                ["invocation"] = invocation,
                ["cancellationSupported"] = false
            };

            if (checkResult is null)
            {
                properties["resultAvailable"] = false;
                return properties;
            }

            properties["resultAvailable"] = true;
            properties["packagingKind"] = checkResult.PackagingKind.ToString();
            properties["availabilityState"] = checkResult.AvailabilityState.ToString();
            properties["storeUpdateCheckAvailable"] = checkResult.StoreUpdateCheckAvailable;
            properties["storeContextAttempted"] = checkResult.StoreContextAttempted;
            properties["storeContextAvailable"] = checkResult.StoreContextAvailable;
            properties["isDevelopmentMode"] = checkResult.IsDevelopmentMode;
            properties["packageName"] = checkResult.PackageName;
            properties["packageFamilyName"] = checkResult.PackageFamilyName;
            properties["packagePublisherId"] = checkResult.PackagePublisherId;
            properties["packageVersion"] = checkResult.PackageVersion;
            properties["packageSignatureKind"] = checkResult.PackageSignatureKind;
            properties["packageIdentityApi"] = checkResult.PackageIdentityApi;
            properties["packageTypeAvailable"] = checkResult.PackageTypeAvailable;
            properties["packageCurrentAvailable"] = checkResult.PackageCurrentAvailable;
            properties["packageIdentityReadSucceeded"] = checkResult.PackageIdentityReadSucceeded;
            properties["packageIdentityReadFailure"] = checkResult.PackageIdentityReadFailure;
            properties["packageIdentityFallbackSource"] = checkResult.PackageIdentityFallbackSource;
            properties["perPackageUpdateListReturned"] = checkResult.PerPackageUpdateListReturned;
            properties["updateCount"] = checkResult.UpdateCount;
            properties["hasMandatoryUpdate"] = checkResult.HasMandatoryUpdate;
            properties["hasConfirmedInstallableUpdate"] = checkResult.HasConfirmedInstallableUpdate;
            properties["shouldShowManualInstructions"] = checkResult.ShouldShowManualInstructions;
            properties["resultKind"] = ClassifyStoreUpdateResult(checkResult);
            properties["exceptionSummary"] = string.IsNullOrWhiteSpace(checkResult.ExceptionSummary) ? null : checkResult.ExceptionSummary;
            return properties;
        }

        private static string ClassifyStoreUpdateResult(StoreUpdateCheckResult checkResult)
        {
            if (!string.IsNullOrWhiteSpace(checkResult.ExceptionSummary))
            {
                return "Failure";
            }

            if (checkResult.HasMandatoryUpdate)
            {
                return "MandatoryUpdate";
            }

            if (checkResult.HasConfirmedInstallableUpdate)
            {
                return "UpdateAvailable";
            }

            if (checkResult.PackagingKind == StoreUpdatePackagingKind.UnpackagedLocalBuild)
            {
                return "LocalBuildUnavailable";
            }

            return checkResult.AvailabilityState switch
            {
                StoreUpdateAvailabilityState.NoUpdateAvailable => "NoUpdateAvailable",
                StoreUpdateAvailabilityState.ManualCheckRequired => "ManualInstructions",
                StoreUpdateAvailabilityState.UpdateCheckUnavailable => "CheckUnavailable",
                StoreUpdateAvailabilityState.ConfirmedUpdateAvailable => "UpdateAvailable",
                _ => "Unknown"
            };
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            DeveloperDiagnostics.LogUserAction("Help", "AboutRequested", "About window requested.");

            try
            {
                if (_aboutWindow is { IsLoaded: true })
                {
                    _aboutWindow.Activate();
                    DeveloperDiagnostics.LogDecision("Help", "AboutRequested", "Existing About window activated.", "ReuseExistingWindow");
                    return;
                }

                var aboutWindow = new AboutWindow
                {
                    Owner = this
                };
                aboutWindow.Closed += AboutWindow_Closed;
                _aboutWindow = aboutWindow;
                aboutWindow.Show();
                aboutWindow.Activate();
                DeveloperDiagnostics.LogDecision("Help", "AboutRequested", "About window created and shown.", "CreateWindow");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Help", "Failed to show the About window.", ex);
                DeveloperDiagnostics.LogException("Help", ex, "Failed to show the About window.");
                ViewModel?.StatusText = "Unable to open About";
            }
        }

        private void AboutWindow_Closed(object? sender, EventArgs e)
        {
            if (ReferenceEquals(sender, _aboutWindow))
            {
                _aboutWindow = null;
            }

            DeveloperDiagnostics.LogInfo("Help", "About window closed.");
        }

        private void ContextHelp_Click(object sender, RoutedEventArgs e)
        {
            ContextHelp.OpenForFocusedElement(this);
        }

        private void ConsoleBottomPaneTab_Click(object sender, RoutedEventArgs e)
        {
            ConsoleBottomPaneTab.IsChecked = true;
            DeveloperDiagnostics.LogUserAction("UI", "BottomPaneConsoleTabSelected", "Console bottom pane tab selected.");
            Dispatcher.BeginInvoke(new Action(() => TerminalConsole.FocusTerminal()), System.Windows.Threading.DispatcherPriority.Loaded);
        }

        private void BottomProblemsToolTab_Click(object sender, RoutedEventArgs e)
        {
            SelectBottomToolTab(BottomToolTab.Problems, "UserSelected");
        }

        private void BottomDebugOutputToolTab_Click(object sender, RoutedEventArgs e)
        {
            SelectBottomToolTab(BottomToolTab.DebugOutput, "UserSelected");
        }

        private void SelectDebugOutputBottomPane(string reason)
        {
            ShowBottomToolWindow(BottomToolTab.DebugOutput, reason);
        }

        private void BottomActivityToolTab_Click(object sender, RoutedEventArgs e)
        {
            SelectBottomToolTab(BottomToolTab.Activity, "UserSelected");
        }

        private void ShowBottomToolWindow_Click(object sender, RoutedEventArgs e)
        {
            if (ShowBottomToolWindowMenuItem.IsChecked)
            {
                ShowBottomToolWindow(_selectedBottomToolTab, "ViewMenu");
            }
            else
            {
                HideBottomToolWindow("ViewMenu");
            }
        }

        private void PopOutBottomToolWindowButton_Click(object sender, RoutedEventArgs e)
        {
            PopOutBottomToolWindow("HeaderButton");
        }

        private void PopOutBottomToolWindowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            PopOutBottomToolWindow("ViewMenu");
        }

        private void DockBottomToolWindowButton_Click(object sender, RoutedEventArgs e)
        {
            DockBottomToolWindow("HeaderButton");
        }

        private void DockBottomToolWindowMenuItem_Click(object sender, RoutedEventArgs e)
        {
            DockBottomToolWindow("ViewMenu");
        }

        private void HideBottomToolWindowButton_Click(object sender, RoutedEventArgs e)
        {
            HideBottomToolWindow("HeaderButton");
        }

        private void ShowBottomToolWindow(BottomToolTab selectedTab, string reason)
        {
            SelectBottomToolTab(selectedTab, reason);
            _isBottomToolWindowVisible = true;

            if (_isBottomToolWindowFloating)
            {
                PopOutBottomToolWindow(reason);
            }
            else
            {
                ApplyBottomToolWindowPresentationState(reason);
            }

            DeveloperDiagnostics.LogUserAction(
                "UI",
                "BottomToolWindowShown",
                "Problems / Debug Output / Activity tool group shown.",
                BuildBottomToolWindowDiagnostics(reason));
        }

        private void HideBottomToolWindow(string reason)
        {
            CaptureDockedBottomToolWindowHeight();
            CaptureBottomToolWindowBounds();

            var bottomToolWindow = _bottomToolWindow;
            if (bottomToolWindow is not null)
            {
                bottomToolWindow.DockBackRequested -= BottomToolWindow_DockBackRequested;
                bottomToolWindow.Closed -= BottomToolWindow_Closed;
                bottomToolWindow.LocationChanged -= BottomToolWindow_LocationChanged;
                bottomToolWindow.SizeChanged -= BottomToolWindow_SizeChanged;
                bottomToolWindow.ClearToolContent();
                _bottomToolWindow = null;
                bottomToolWindow.CloseForOwnerShutdown();
            }

            _isBottomToolWindowVisible = false;
            EnsureBottomToolWindowContentDocked();
            ApplyBottomToolWindowPresentationState(reason);

            DeveloperDiagnostics.LogUserAction(
                "UI",
                "BottomToolWindowHidden",
                "Problems / Debug Output / Activity tool group hidden.",
                BuildBottomToolWindowDiagnostics(reason));
        }

        private void PopOutBottomToolWindow(string reason)
        {
            CaptureDockedBottomToolWindowHeight();
            _isBottomToolWindowVisible = true;
            _isBottomToolWindowFloating = true;

            if (_bottomToolWindow is { IsLoaded: true } existingWindow)
            {
                ApplyBottomToolWindowPresentationState(reason);
                existingWindow.Activate();
                DeveloperDiagnostics.LogDecision(
                    "UI",
                    "BottomToolWindowPopOut",
                    "Existing floating bottom tool window activated.",
                    "ReuseExistingWindow",
                    BuildBottomToolWindowDiagnostics(reason));
                return;
            }

            var bottomToolWindow = new BottomToolWindow
            {
                Owner = this
            };

            _bottomToolWindow = bottomToolWindow;
            bottomToolWindow.DockBackRequested += BottomToolWindow_DockBackRequested;
            bottomToolWindow.Closed += BottomToolWindow_Closed;
            bottomToolWindow.LocationChanged += BottomToolWindow_LocationChanged;
            bottomToolWindow.SizeChanged += BottomToolWindow_SizeChanged;

            RestoreBottomToolWindowBounds(bottomToolWindow);
            EnsureBottomToolWindowContentFloating(bottomToolWindow);
            ApplyBottomToolWindowPresentationState(reason);
            bottomToolWindow.Show();

            DeveloperDiagnostics.LogUserAction(
                "UI",
                "BottomToolWindowPoppedOut",
                "Problems / Debug Output / Activity tool group popped out.",
                BuildBottomToolWindowDiagnostics(reason));
        }

        private void DockBottomToolWindow(string reason)
        {
            CaptureBottomToolWindowBounds();

            var bottomToolWindow = _bottomToolWindow;
            if (bottomToolWindow is not null)
            {
                bottomToolWindow.DockBackRequested -= BottomToolWindow_DockBackRequested;
                bottomToolWindow.Closed -= BottomToolWindow_Closed;
                bottomToolWindow.LocationChanged -= BottomToolWindow_LocationChanged;
                bottomToolWindow.SizeChanged -= BottomToolWindow_SizeChanged;
                bottomToolWindow.ClearToolContent();
                _bottomToolWindow = null;
            }

            _isBottomToolWindowVisible = true;
            _isBottomToolWindowFloating = false;
            EnsureBottomToolWindowContentDocked();
            ApplyBottomToolWindowPresentationState(reason);
            bottomToolWindow?.CloseForDockBack();

            DeveloperDiagnostics.LogUserAction(
                "UI",
                "BottomToolWindowDocked",
                "Problems / Debug Output / Activity tool group docked below the console.",
                BuildBottomToolWindowDiagnostics(reason));
        }

        private void SelectBottomToolTab(BottomToolTab selectedTab, string reason)
        {
            if (_isSynchronizingBottomToolWindowTab)
            {
                return;
            }

            var previousTab = _selectedBottomToolTab;
            _selectedBottomToolTab = selectedTab;
            _isSynchronizingBottomToolWindowTab = true;
            try
            {
                BottomProblemsToolTab.IsChecked = selectedTab == BottomToolTab.Problems;
                BottomDebugOutputToolTab.IsChecked = selectedTab == BottomToolTab.DebugOutput;
                BottomActivityToolTab.IsChecked = selectedTab == BottomToolTab.Activity;
            }
            finally
            {
                _isSynchronizingBottomToolWindowTab = false;
            }

            if (previousTab == selectedTab && string.Equals(reason, "SettingsRestore", StringComparison.Ordinal))
            {
                return;
            }

            DeveloperDiagnostics.LogInfo(
                selectedTab == BottomToolTab.DebugOutput ? "Debugger" : "UI",
                "Bottom tool window selected tab changed.",
                BuildBottomToolWindowDiagnostics(reason, previousTab));
        }

        private void RestoreBottomToolWindowFromSettings()
        {
            _selectedBottomToolTab = RestoreBottomToolTab(_loadedSettings.SelectedBottomToolTab);
            SelectBottomToolTab(_selectedBottomToolTab, "SettingsRestore");

            if (!_isBottomToolWindowVisible)
            {
                ApplyBottomToolWindowPresentationState("SettingsRestore");
                return;
            }

            if (_isBottomToolWindowFloating)
            {
                PopOutBottomToolWindow("SettingsRestore");
            }
            else
            {
                ApplyBottomToolWindowPresentationState("SettingsRestore");
            }
        }

        private void ApplyBottomToolWindowPresentationState(string reason)
        {
            var dockedVisible =
                _isBottomToolWindowVisible &&
                !_isBottomToolWindowFloating &&
                _workspaceLayoutMode != WorkspaceLayoutMode.EditorMaximized;

            if (dockedVisible)
            {
                EnsureBottomToolWindowContentDocked();
                BottomToolWindowSplitterRowDefinition.Height = new GridLength(BottomToolWindowSplitterThickness, GridUnitType.Pixel);
                BottomToolWindowRowDefinition.Height = new GridLength(Math.Max(_lastKnownBottomToolWindowHeight, MinimumBottomToolWindowHeight), GridUnitType.Pixel);
                BottomToolWindowRowDefinition.MinHeight = MinimumBottomToolWindowHeight;
                BottomToolWindowSplitter.Visibility = Visibility.Visible;
                BottomToolWindowBorder.Visibility = Visibility.Visible;
            }
            else
            {
                BottomToolWindowSplitter.Visibility = Visibility.Collapsed;
                BottomToolWindowBorder.Visibility = Visibility.Collapsed;
                BottomToolWindowSplitterRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                BottomToolWindowRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                BottomToolWindowRowDefinition.MinHeight = 0;
            }

            ApplyHorizontalConsoleRegionHeight(dockedVisible);

            BottomToolWindowPopOutButton.Visibility = _isBottomToolWindowFloating ? Visibility.Collapsed : Visibility.Visible;
            BottomToolWindowDockBackButton.Visibility = _isBottomToolWindowFloating ? Visibility.Visible : Visibility.Collapsed;
            ShowBottomToolWindowMenuItem.IsChecked = _isBottomToolWindowVisible;
            PopOutBottomToolWindowMenuItem.IsEnabled = _isBottomToolWindowVisible && !_isBottomToolWindowFloating;
            DockBottomToolWindowMenuItem.IsEnabled = _isBottomToolWindowVisible && _isBottomToolWindowFloating;
            DockBottomToolWindowMenuItem.Visibility = _isBottomToolWindowVisible && _isBottomToolWindowFloating ? Visibility.Visible : Visibility.Collapsed;
            PopOutBottomToolWindowMenuItem.Visibility = _isBottomToolWindowVisible && _isBottomToolWindowFloating ? Visibility.Collapsed : Visibility.Visible;

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Bottom tool window presentation state applied.",
                BuildBottomToolWindowDiagnostics(reason));
        }

        private void EnsureBottomToolWindowContentDocked()
        {
            if (BottomToolWindowContent.Parent is ContentControl contentControl)
            {
                contentControl.Content = null;
            }
            else if (BottomToolWindowContent.Parent is System.Windows.Controls.Panel panel && !ReferenceEquals(panel, BottomToolWindowDockHost))
            {
                panel.Children.Remove(BottomToolWindowContent);
            }

            if (!BottomToolWindowDockHost.Children.Contains(BottomToolWindowContent))
            {
                BottomToolWindowDockHost.Children.Clear();
                BottomToolWindowDockHost.Children.Add(BottomToolWindowContent);
            }
        }

        private void EnsureBottomToolWindowContentFloating(BottomToolWindow bottomToolWindow)
        {
            if (BottomToolWindowContent.Parent is System.Windows.Controls.Panel panel)
            {
                panel.Children.Remove(BottomToolWindowContent);
            }
            else if (BottomToolWindowContent.Parent is ContentControl contentControl)
            {
                contentControl.Content = null;
            }

            bottomToolWindow.SetToolContent(BottomToolWindowContent);
        }

        private void BottomToolWindow_DockBackRequested(object? sender, EventArgs e)
        {
            DockBottomToolWindow("FloatingWindowRequest");
        }

        private void BottomToolWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is not BottomToolWindow bottomToolWindow)
            {
                return;
            }

            CaptureBottomToolWindowBounds(bottomToolWindow);

            if (ReferenceEquals(_bottomToolWindow, bottomToolWindow))
            {
                bottomToolWindow.DockBackRequested -= BottomToolWindow_DockBackRequested;
                bottomToolWindow.Closed -= BottomToolWindow_Closed;
                bottomToolWindow.LocationChanged -= BottomToolWindow_LocationChanged;
                bottomToolWindow.SizeChanged -= BottomToolWindow_SizeChanged;
                bottomToolWindow.ClearToolContent();
                _bottomToolWindow = null;
                _isBottomToolWindowFloating = false;
                EnsureBottomToolWindowContentDocked();
                ApplyBottomToolWindowPresentationState("FloatingWindowClosed");
            }

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Floating bottom tool window closed.",
                BuildBottomToolWindowDiagnostics("FloatingWindowClosed"));
        }

        private void BottomToolWindow_LocationChanged(object? sender, EventArgs e)
        {
            if (sender is BottomToolWindow bottomToolWindow)
            {
                CaptureBottomToolWindowBounds(bottomToolWindow);
            }
        }

        private void BottomToolWindow_SizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is BottomToolWindow bottomToolWindow)
            {
                CaptureBottomToolWindowBounds(bottomToolWindow);
            }
        }

        private void CaptureDockedBottomToolWindowHeight()
        {
            if (!_isBottomToolWindowFloating &&
                BottomToolWindowBorder.Visibility == Visibility.Visible &&
                BottomToolWindowRowDefinition.ActualHeight >= MinimumBottomToolWindowHeight)
            {
                _lastKnownBottomToolWindowHeight = BottomToolWindowRowDefinition.ActualHeight;
            }
        }

        private void ApplyHorizontalConsoleRegionHeight(bool dockedBottomToolVisible)
        {
            if (_workspaceLayoutMode is not (WorkspaceLayoutMode.Default or WorkspaceLayoutMode.HorizontalSplit))
            {
                return;
            }

            var consoleHeight = Math.Max(_lastKnownConsoleHeight, MinimumConsoleHeight);
            if (dockedBottomToolVisible)
            {
                consoleHeight += BottomToolWindowSplitterThickness + Math.Max(_lastKnownBottomToolWindowHeight, MinimumBottomToolWindowHeight);
            }

            ConsoleRowDefinition.Height = new GridLength(consoleHeight, GridUnitType.Pixel);
        }

        private void CaptureBottomToolWindowBounds()
        {
            if (_bottomToolWindow is not null)
            {
                CaptureBottomToolWindowBounds(_bottomToolWindow);
            }
        }

        private void CaptureBottomToolWindowBounds(BottomToolWindow bottomToolWindow)
        {
            if (bottomToolWindow.WindowState != WindowState.Normal)
            {
                return;
            }

            if (!IsFiniteCoordinate(bottomToolWindow.Left) ||
                !IsFiniteCoordinate(bottomToolWindow.Top) ||
                !IsUsableLength(bottomToolWindow.Width, MinimumSavedBottomToolWindowWidth) ||
                !IsUsableLength(bottomToolWindow.Height, MinimumSavedBottomToolWindowHeight))
            {
                return;
            }

            _lastBottomToolWindowBounds = new Rect(bottomToolWindow.Left, bottomToolWindow.Top, bottomToolWindow.Width, bottomToolWindow.Height);
        }

        private void RestoreBottomToolWindowBounds(BottomToolWindow bottomToolWindow)
        {
            var fallbackBounds = new Rect(
                Left + 48,
                Top + 64,
                DefaultBottomToolWindowWidth,
                DefaultFloatingBottomToolWindowHeight);
            var hasVisibleSavedBounds = _lastBottomToolWindowBounds is Rect savedBounds && IsWindowBoundsVisible(savedBounds);
            var restoredBounds = hasVisibleSavedBounds ? savedBounds : fallbackBounds;

            bottomToolWindow.Left = restoredBounds.Left;
            bottomToolWindow.Top = restoredBounds.Top;
            bottomToolWindow.Width = restoredBounds.Width;
            bottomToolWindow.Height = restoredBounds.Height;

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Bottom tool window size and position restored.",
                new Dictionary<string, object?>
                {
                    ["left"] = restoredBounds.Left,
                    ["top"] = restoredBounds.Top,
                    ["width"] = restoredBounds.Width,
                    ["height"] = restoredBounds.Height,
                    ["usedFallback"] = !hasVisibleSavedBounds
                });
        }

        private static bool IsWindowBoundsVisible(Rect bounds)
        {
            if (!IsFiniteCoordinate(bounds.Left) ||
                !IsFiniteCoordinate(bounds.Top) ||
                !IsUsableLength(bounds.Width, MinimumSavedBottomToolWindowWidth) ||
                !IsUsableLength(bounds.Height, MinimumSavedBottomToolWindowHeight))
            {
                return false;
            }

            var virtualScreen = new Rect(
                SystemParameters.VirtualScreenLeft,
                SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenHeight);

            return virtualScreen.IntersectsWith(bounds);
        }

        private static BottomToolTab RestoreBottomToolTab(string? persistedTab)
        {
            if (Enum.TryParse<BottomToolTab>(persistedTab, ignoreCase: true, out var tab))
            {
                return tab;
            }

            return BottomToolTab.Problems;
        }

        private Dictionary<string, object?> BuildBottomToolWindowDiagnostics(string reason, BottomToolTab? previousTab = null)
        {
            return new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["visible"] = _isBottomToolWindowVisible,
                ["floating"] = _isBottomToolWindowFloating,
                ["selectedTab"] = _selectedBottomToolTab.ToString(),
                ["previousTab"] = previousTab?.ToString(),
                ["dockedHeight"] = _lastKnownBottomToolWindowHeight,
                ["floatingWindowOpen"] = _bottomToolWindow is not null,
                ["workspaceLayoutMode"] = _workspaceLayoutMode.ToString(),
                ["debugOutputLength"] = ViewModel?.DebuggerOutputText?.Length ?? 0,
                ["activityLength"] = ViewModel?.ApplicationActivityText?.Length ?? 0,
                ["errorCount"] = ViewModel?.SelectedTab?.DiagnosticErrorCount ?? 0,
                ["warningCount"] = ViewModel?.SelectedTab?.DiagnosticWarningCount ?? 0
            };
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
                ShowIdeMessage("Dropped File Results", $"{summary}{Environment.NewLine}{Environment.NewLine}{failureDetails}");

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

        private void CommandPalette_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

        private void EditorContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is not System.Windows.Controls.ContextMenu contextMenu || contextMenu.PlacementTarget is not TextEditor editor)
            {
                return;
            }

            var availableMenu = contextMenu.Items
                .OfType<WpfMenuItem>()
                .FirstOrDefault(item => string.Equals(item.Header as string, "Available Commands", StringComparison.Ordinal));
            if (availableMenu is null)
            {
                return;
            }

            var registry = CreateEditorCommandRegistry(editor);
            var commands = EditorContextMenuBuilder.Populate(
                availableMenu,
                registry,
                editor.SelectionLength > 0,
                command => ExecuteEditorContextCommand(editor, command));

            DeveloperDiagnostics.LogInfo("EditorProductivity", "Available Commands context submenu rebuilt.", new Dictionary<string, object?>
            {
                ["commandCount"] = commands.Count,
                ["selectionLength"] = editor.SelectionLength,
                ["editorReadOnly"] = editor.IsReadOnly
            });
        }

        private void ExecuteEditorContextCommand(TextEditor editor, EditorCommandDefinition command)
        {
            if (editor.SelectionLength <= 0 || !command.CanExecute())
            {
                return;
            }

            DeveloperDiagnostics.LogUserAction("EditorProductivity", "ContextCommandInvoked", "Available Commands entry invoked.", new Dictionary<string, object?>
            {
                ["commandId"] = command.Id,
                ["selectionLength"] = editor.SelectionLength
            });
            command.Execute();
        }

        private void OpenCommandPalette()
        {
            if (_commandPaletteWindow is not null)
            {
                _commandPaletteWindow.Activate();
                return;
            }

            var editor = FindActiveEditor();
            var registry = CreateEditorCommandRegistry(editor);
            var palette = new CommandPaletteWindow(this, registry);
            _commandPaletteWindow = palette;
            palette.Closed += (_, _) =>
            {
                _commandPaletteWindow = null;
                FocusActiveEditorSoon();
            };
            DeveloperDiagnostics.LogInfo("Editor", "Command Palette opened.", new Dictionary<string, object?>
            {
                ["commandCount"] = registry.Commands.Count,
                ["editorAvailable"] = editor?.Document is not null
            });
            palette.Show();
        }

        private EditorCommandRegistry CreateEditorCommandRegistry(TextEditor? editor)
        {
            bool CanEdit() => editor?.Document is not null && !editor.IsReadOnly;
            bool CanCaretEdit() => CanEdit() && editor!.SelectionLength == 0;
            bool CanSelectionEdit() => CanEdit() && editor!.SelectionLength > 0;
            void Apply(string name, Func<EditorCommandResult> command)
            {
                if (editor is not null)
                {
                    ApplyEditorCommand(editor, name, command);
                }
            }
            void ApplyTransform(string name, Func<TextDocument, int, int, EditorCommandResult> command) =>
                Apply(name, () => command(editor!.Document!, editor.SelectionStart, editor.SelectionLength));
            void ApplyNative(string name, RoutedCommand command) =>
                Apply(name, () =>
                {
                    command.Execute(null, editor!.TextArea);
                    return new EditorCommandResult(editor.SelectionStart, editor.SelectionLength);
                });

            var commands = new List<EditorCommandDefinition>
            {
                new("diagnostics.analyzeCurrentDocument", "Analyze Current Document", "Diagnostics", "", new[] { "analyze", "script analyzer", "psscriptanalyzer", "diagnostics" },
                    () => ViewModel?.SelectedTab is not null,
                    () => _ = AnalyzeCurrentDocumentAsync()),
                new("editor.toggleComment", "Toggle Line Comment", "Editor", "Ctrl+/", new[] { "comment", "uncomment", "hash" }, CanEdit, () => ToggleCommentForEditor(editor!)),
                new("editor.indent", "Indent Selection", "Editor", "Tab", new[] { "indent", "tab" }, CanEdit, () => ApplyTransform("Indent", (document, start, length) => EditorProductivityCommands.Indent(document, start, length))),
                new("editor.outdent", "Outdent Selection", "Editor", "Shift+Tab", new[] { "outdent", "unindent" }, CanEdit, () => ApplyTransform("Outdent", (document, start, length) => EditorProductivityCommands.Outdent(document, start, length))),
                new("editor.moveUp", "Move Line/Selection Up", "Editor", "Alt+Up", new[] { "move", "line", "block" }, CanEdit, () => MoveLineUp(editor!)),
                new("editor.moveDown", "Move Line/Selection Down", "Editor", "Alt+Down", new[] { "move", "line", "block" }, CanEdit, () => MoveLineDown(editor!)),
                new("editor.duplicateUp", "Duplicate Line/Selection Up", "Editor", "Shift+Alt+Up", new[] { "duplicate", "copy", "line" }, CanEdit, () => ApplyTransform("DuplicateUp", (document, start, length) => EditorProductivityCommands.DuplicateLines(document, start, length, -1))),
                new("editor.duplicateDown", "Duplicate Line/Selection Down", "Editor", "Shift+Alt+Down", new[] { "duplicate", "copy", "line" }, CanEdit, () => ApplyTransform("DuplicateDown", (document, start, length) => EditorProductivityCommands.DuplicateLines(document, start, length, 1))),
                new("editor.deleteLine", "Delete Current Line", "Editor", "Ctrl+Shift+K", new[] { "delete", "line" }, CanEdit, () => ApplyTransform("DeleteLine", EditorProductivityCommands.DeleteLines)),
                new("editor.selectLine", "Select Current Line", "Selection", "", new[] { "select", "line" }, () => editor?.Document is not null, () => SelectCurrentLine(editor!)),
                new("editor.insertLineAbove", "Insert Line Above", "Editor", "Ctrl+Shift+Enter", new[] { "insert", "line", "above" }, CanCaretEdit, () => Apply("InsertLineAbove", () => EditorProductivityCommands.InsertLineAbove(editor!.Document!, editor.CaretOffset)), ShortcutGesture: new KeyGesture(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift)),
                new("editor.insertLineBelow", "Insert Line Below", "Editor", "Ctrl+Enter", new[] { "insert", "line", "below" }, CanCaretEdit, () => Apply("InsertLineBelow", () => EditorProductivityCommands.InsertLineBelow(editor!.Document!, editor.CaretOffset)), ShortcutGesture: new KeyGesture(Key.Enter, ModifierKeys.Control)),
                new("editor.deleteToLineStart", "Delete to Beginning of Line", "Editor", "", new[] { "delete", "beginning", "line" }, CanCaretEdit, () => Apply("DeleteToLineStart", () => EditorProductivityCommands.DeleteToLineStart(editor!.Document!, editor.CaretOffset, editor.SelectionLength))),
                new("editor.deleteToLineEnd", "Delete to End of Line", "Editor", "", new[] { "delete", "end", "line" }, CanCaretEdit, () => Apply("DeleteToLineEnd", () => EditorProductivityCommands.DeleteToLineEnd(editor!.Document!, editor.CaretOffset, editor.SelectionLength))),
                new("editor.deleteWordLeft", "Delete Word Left", "Editor", "Ctrl+Backspace", new[] { "delete", "word", "left" }, CanCaretEdit, () => ApplyNative("DeleteWordLeft", System.Windows.Documents.EditingCommands.DeletePreviousWord)),
                new("editor.deleteWordRight", "Delete Word Right", "Editor", "Ctrl+Delete", new[] { "delete", "word", "right" }, CanCaretEdit, () => ApplyNative("DeleteWordRight", System.Windows.Documents.EditingCommands.DeleteNextWord)),
                new("selection.duplicate", "Duplicate Selection", "Selection", "", new[] { "duplicate", "selection" }, CanSelectionEdit, () => ApplyTransform("DuplicateSelection", EditorProductivityCommands.DuplicateSelection)),
                new("transform.tabsToSpaces", "Convert Tabs to Spaces", "Transform", "", new[] { "tabs", "spaces", "indent" }, CanSelectionEdit, () => ApplyNative("ConvertTabsToSpaces", AvalonEditCommands.ConvertLeadingTabsToSpaces)),
                new("transform.spacesToTabs", "Convert Spaces to Tabs", "Transform", "", new[] { "spaces", "tabs", "indent" }, CanSelectionEdit, () => ApplyNative("ConvertSpacesToTabs", AvalonEditCommands.ConvertLeadingSpacesToTabs)),
                new("transform.trimDocumentTrailingWhitespace", "Trim Document Trailing Whitespace", "Transform", "", new[] { "trim", "trailing", "document", "whitespace" }, CanEdit, () => Apply("TrimDocumentTrailingWhitespace", () => EditorTransformCommands.TrimDocumentTrailingWhitespace(editor!.Document!)), CommandSurfaces.CommandPalette),
                new("transform.sortIgnoreCaseAsc", "Sort Lines Case-Insensitive Ascending", "Transform", "", new[] { "sort", "case insensitive", "ascending" }, CanSelectionEdit, () => ApplyTransform("SortLinesIgnoreCaseAscending", EditorTransformCommands.SortLinesIgnoreCaseAscending)),
                new("transform.sortIgnoreCaseDesc", "Sort Lines Case-Insensitive Descending", "Transform", "", new[] { "sort", "case insensitive", "descending" }, CanSelectionEdit, () => ApplyTransform("SortLinesIgnoreCaseDescending", EditorTransformCommands.SortLinesIgnoreCaseDescending)),
                new("transform.joinLines", "Join Lines", "Transform", "", new[] { "join", "lines" }, CanSelectionEdit, () => ApplyTransform("JoinLines", EditorTransformCommands.JoinLines)),
                new("transform.sortByLength", "Sort Lines by Length", "Transform", "", new[] { "sort", "length", "shortest" }, CanSelectionEdit, () => ApplyTransform("SortLinesByLength", EditorTransformCommands.SortLinesByLength)),
                new("transform.uniqueSort", "Unique + Sort Lines", "Transform", "", new[] { "unique", "sort", "deduplicate" }, CanSelectionEdit, () => ApplyTransform("UniqueSortLines", EditorTransformCommands.UniqueSortLines)),
                new("transform.collapseBlankLines", "Collapse Consecutive Blank Lines", "Transform", "", new[] { "collapse", "blank", "whitespace" }, CanSelectionEdit, () => ApplyTransform("CollapseConsecutiveBlankLines", EditorTransformCommands.CollapseConsecutiveBlankLines)),
                new("transform.addLineNumbers", "Add Line Numbers", "Transform", "", new[] { "number", "numbering", "lines" }, CanSelectionEdit, () => ApplyTransform("AddLineNumbers", EditorTransformCommands.AddLineNumbers)),
                new("transform.removeLineNumbers", "Remove Line Numbers", "Transform", "", new[] { "remove", "number", "numbering" }, CanSelectionEdit, () => ApplyTransform("RemoveLineNumbers", EditorTransformCommands.RemoveLineNumbers)),
                new("transform.convertToCrlf", "Convert Line Endings to CRLF", "Transform", "", new[] { "line endings", "crlf", "windows" }, CanEdit, () => Apply("ConvertLineEndingsToCrlf", () => EditorTransformCommands.ConvertLineEndingsToCrlf(editor!.Document!)), CommandSurfaces.CommandPalette),
                new("transform.convertToLf", "Convert Line Endings to LF", "Transform", "", new[] { "line endings", "lf", "unix" }, CanEdit, () => Apply("ConvertLineEndingsToLf", () => EditorTransformCommands.ConvertLineEndingsToLf(editor!.Document!)), CommandSurfaces.CommandPalette),
                new("transform.urlEncode", "URL Encode", "Transform", "", new[] { "url", "encode", "percent", "escape" }, CanSelectionEdit, () => ApplyTransform("UrlEncode", EditorTransformCommands.UrlEncode)),
                new("transform.urlDecode", "URL Decode", "Transform", "", new[] { "url", "decode", "percent", "unescape" }, CanSelectionEdit, () => ApplyTransform("UrlDecode", EditorTransformCommands.UrlDecode)),
                new("transform.base64Encode", "Base64 Encode", "Transform", "", new[] { "base64", "encode", "utf8" }, CanSelectionEdit, () => ApplyTransform("Base64Encode", EditorTransformCommands.Base64Encode)),
                new("transform.base64Decode", "Base64 Decode", "Transform", "", new[] { "base64", "decode", "utf8" }, CanSelectionEdit, () => ApplyTransform("Base64Decode", EditorTransformCommands.Base64Decode)),
                new("transform.jsonPrettyPrint", "JSON Pretty Print", "Transform", "", new[] { "json", "pretty", "format", "indent" }, CanSelectionEdit, () => ApplyTransform("JsonPrettyPrint", EditorTransformCommands.JsonPrettyPrint)),
                new("transform.jsonMinify", "JSON Minify", "Transform", "", new[] { "json", "minify", "compact" }, CanSelectionEdit, () => ApplyTransform("JsonMinify", EditorTransformCommands.JsonMinify)),
                new("transform.sortAsc", "Sort Lines Ascending", "Transform", "", new[] { "sort", "ascending", "alphabetize" }, CanEdit, () => ApplyTransform("SortLinesAscending", EditorTransformCommands.SortLinesAscending)),
                new("transform.sortDesc", "Sort Lines Descending", "Transform", "", new[] { "sort", "descending", "reverse" }, CanEdit, () => ApplyTransform("SortLinesDescending", EditorTransformCommands.SortLinesDescending)),
                new("transform.removeDuplicates", "Remove Duplicate Lines", "Transform", "", new[] { "unique", "deduplicate", "duplicates" }, CanEdit, () => ApplyTransform("RemoveDuplicateLines", EditorTransformCommands.RemoveDuplicateLines)),
                new("transform.reverse", "Reverse Lines", "Transform", "", new[] { "reverse", "lines" }, CanEdit, () => ApplyTransform("ReverseLines", EditorTransformCommands.ReverseLines)),
                new("transform.trimLines", "Trim Leading and Trailing Whitespace", "Transform", "", new[] { "trim", "whitespace", "spaces" }, CanEdit, () => ApplyTransform("TrimLines", EditorTransformCommands.TrimLines)),
                new("transform.trimTrailing", "Trim Trailing Whitespace", "Transform", "", new[] { "trim", "trailing", "whitespace" }, CanEdit, () => ApplyTransform("TrimTrailingWhitespace", EditorTransformCommands.TrimTrailingWhitespace)),
                new("transform.removeBlank", "Remove Blank Lines", "Transform", "", new[] { "blank", "empty", "remove" }, CanEdit, () => ApplyTransform("RemoveBlankLines", EditorTransformCommands.RemoveBlankLines)),
                new("transform.upper", "Convert Selection to Uppercase", "Transform", "", new[] { "upper", "uppercase", "caps" }, CanEdit, () => ApplyTransform("UppercaseSelection", EditorTransformCommands.UppercaseSelection)),
                new("transform.lower", "Convert Selection to Lowercase", "Transform", "", new[] { "lower", "lowercase" }, CanEdit, () => ApplyTransform("LowercaseSelection", EditorTransformCommands.LowercaseSelection)),
                new("transform.title", "Convert Selection to Title Case", "Transform", "", new[] { "title", "titlecase" }, CanEdit, () => ApplyTransform("TitleCaseSelection", EditorTransformCommands.TitleCaseSelection)),
                new("transform.prefix", "Prefix Each Line", "Transform", "", new[] { "prefix", "prepend" }, CanEdit, () => PromptAndApplyLineText(editor!, true)),
                new("transform.suffix", "Suffix Each Line", "Transform", "", new[] { "suffix", "append" }, CanEdit, () => PromptAndApplyLineText(editor!, false)),
                new("transform.listToPowerShellArray", "Convert List to PowerShell Array", "Transform", "", new[] { "list", "array", "powershell", "selection", "convert", "transform" }, CanSelectionEdit, () => ApplyTransform("ConvertListToPowerShellArray", (document, start, length) => EditorTransformCommands.ConvertListToPowerShellArray(document, start, length, editor!.Options.IndentationSize))),
                new("transform.powerShellArrayToList", "Convert PowerShell Array to List", "Transform", "", new[] { "array", "list", "powershell", "selection", "convert", "transform" }, CanSelectionEdit, () => ApplyTransform("ConvertPowerShellArrayToList", EditorTransformCommands.ConvertPowerShellArrayToList)),
                new("transform.quoteSingle", "Quote Each Line with Single Quotes", "Transform", "", new[] { "quote", "single", "apostrophe" }, CanEdit, () => ApplyTransform("QuoteSingle", (document, start, length) => EditorTransformCommands.QuoteLines(document, start, length, '\''))),
                new("transform.quoteDouble", "Quote Each Line with Double Quotes", "Transform", "", new[] { "quote", "double" }, CanEdit, () => ApplyTransform("QuoteDouble", (document, start, length) => EditorTransformCommands.QuoteLines(document, start, length, '"'))),
                new("transform.addComma", "Add Trailing Comma to Each Line", "Transform", "", new[] { "comma", "append" }, CanEdit, () => ApplyTransform("AddTrailingComma", EditorTransformCommands.AddTrailingComma)),
                new("transform.removeComma", "Remove Trailing Comma from Each Line", "Transform", "", new[] { "comma", "remove" }, CanEdit, () => ApplyTransform("RemoveTrailingComma", EditorTransformCommands.RemoveTrailingComma))
            };
            // All current editor productivity commands are valid on both command surfaces.
            // Future registry entries can opt out by retaining the default palette-only surface.
            return new EditorCommandRegistry(commands.Select(command => command with
            {
                Surfaces = command.Surfaces == CommandSurfaces.CommandPalette &&
                           command.Id is not "transform.trimDocumentTrailingWhitespace" and
                           not "transform.convertToCrlf" and
                           not "transform.convertToLf"
                    ? CommandSurfaces.CommandPalette | CommandSurfaces.EditorContextMenu
                    : command.Surfaces
            }));
        }

        private void SelectCurrentLine(TextEditor editor)
        {
            if (editor.Document is null) return;
            var line = editor.Document.GetLineByOffset(Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength));
            editor.Select(line.Offset, line.Length);
        }

        private void PromptAndApplyLineText(TextEditor editor, bool prefix)
        {
            var dialog = new TextInputDialog(
                this,
                prefix ? "Prefix Lines" : "Suffix Lines",
                prefix ? "Prefix for each selected line:" : "Suffix for each selected line:",
                prefix ? "# " : string.Empty);
            if (dialog.ShowDialog() != true || dialog.Result is null) return;
            var value = dialog.Result;
            ApplyEditorCommand(editor, prefix ? "PrefixLines" : "SuffixLines", () => prefix
                ? EditorTransformCommands.PrefixLines(editor.Document!, editor.SelectionStart, editor.SelectionLength, value)
                : EditorTransformCommands.SuffixLines(editor.Document!, editor.SelectionStart, editor.SelectionLength, value));
        }

        private void ConvertListToPowerShellArray_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor)
            {
                ApplyEditorCommand(editor, "ConvertListToPowerShellArray", () => EditorTransformCommands.ConvertListToPowerShellArray(editor.Document!, editor.SelectionStart, editor.SelectionLength, editor.Options.IndentationSize));
            }
        }

        private void ConvertPowerShellArrayToList_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor)
            {
                ApplyEditorCommand(editor, "ConvertPowerShellArrayToList", () => EditorTransformCommands.ConvertPowerShellArrayToList(editor.Document!, editor.SelectionStart, editor.SelectionLength));
            }
        }

        private void EditorToggleComment_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor)
            {
                ToggleCommentForEditor(editor);
            }
        }

        private void EditorIndent_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "Indent", () => EditorProductivityCommands.Indent(editor.Document, editor.SelectionStart, editor.SelectionLength, editor.Options.IndentationSize));
            }
        }

        private void EditorOutdent_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "Outdent", () => EditorProductivityCommands.Outdent(editor.Document, editor.SelectionStart, editor.SelectionLength, editor.Options.IndentationSize));
            }
        }

        private void EditorMoveLineUp_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "MoveLineUp", () => EditorProductivityCommands.MoveLines(editor.Document, editor.SelectionStart, editor.SelectionLength, -1));
            }
        }

        private void EditorMoveLineDown_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "MoveLineDown", () => EditorProductivityCommands.MoveLines(editor.Document, editor.SelectionStart, editor.SelectionLength, 1));
            }
        }

        private void EditorDuplicateDown_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "DuplicateDown", () => EditorProductivityCommands.DuplicateLines(editor.Document, editor.SelectionStart, editor.SelectionLength, 1));
            }
        }

        private void EditorDeleteLine_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is TextEditor editor && editor.Document is not null)
            {
                ApplyEditorCommand(editor, "DeleteLine", () => EditorProductivityCommands.DeleteLines(editor.Document, editor.SelectionStart, editor.SelectionLength));
            }
        }

        private void EditorSelectCurrentLine_Click(object sender, RoutedEventArgs e)
        {
            if (FindActiveEditor() is not TextEditor editor || editor.Document is null)
            {
                return;
            }

            var line = editor.Document.GetLineByOffset(Math.Clamp(editor.CaretOffset, 0, editor.Document.TextLength));
            editor.Select(line.Offset, line.Length);
            DeveloperDiagnostics.LogInfo(
                "EditorProductivity",
                "Selected the current logical line.",
                new Dictionary<string, object?>
                {
                    ["lineNumber"] = line.LineNumber,
                    ["selectionLength"] = line.Length
                });
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

            try
            {
                if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: true))
                    _findReplaceWindow?.ShowStatus("The search text was not found");
                else
                    _findReplaceWindow?.ShowStatus(null);
            }
            catch (ArgumentException ex)
            {
                _findReplaceWindow?.ShowStatus($"Invalid regex: {ex.Message}");
            }
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

            try
            {
                if (!TryFindNext(editorTextEditor, findText, matchCase, wholeWord, useRegex, forward: false))
                    _findReplaceWindow?.ShowStatus("The search text was not found");
                else
                    _findReplaceWindow?.ShowStatus(null);
            }
            catch (ArgumentException ex)
            {
                _findReplaceWindow?.ShowStatus($"Invalid regex: {ex.Message}");
            }
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

        private int IncrementLiveSyntaxRequestVersion(TextEditor editorTextEditor)
        {
            var nextVersion = _liveSyntaxRequestVersions.TryGetValue(editorTextEditor, out var currentVersion)
                ? currentVersion + 1
                : 1;
            _liveSyntaxRequestVersions[editorTextEditor] = nextVersion;
            return nextVersion;
        }

        private void CancelPendingDiagnostics(TextEditor editorTextEditor)
        {
            DisposeAuthoringDiagnosticsPump(editorTextEditor);
        }

        private void DisposeAuthoringDiagnosticsPump(TextEditor editorTextEditor)
        {
            if (_authoringDiagnosticsPumpStates.TryGetValue(editorTextEditor, out var state))
            {
                state.Dispose();
                _authoringDiagnosticsPumpStates.Remove(editorTextEditor);
            }

            _diagnosticsRequestVersions.Remove(editorTextEditor);
        }

        private void DisposeLiveSyntaxPump(TextEditor editorTextEditor)
        {
            if (_liveSyntaxPumpStates.TryGetValue(editorTextEditor, out var state))
            {
                state.Dispose();
                _liveSyntaxPumpStates.Remove(editorTextEditor);
            }

            _liveSyntaxRequestVersions.Remove(editorTextEditor);
        }

        private void DisposeLiveSyntaxPumps()
        {
            foreach (var state in _liveSyntaxPumpStates.Values.ToList())
            {
                state.Dispose();
            }

            _liveSyntaxPumpStates.Clear();
            _liveSyntaxRequestVersions.Clear();
        }

        private void DisposeAuthoringDiagnosticsPumps()
        {
            foreach (var state in _authoringDiagnosticsPumpStates.Values.ToList())
            {
                state.Dispose();
            }

            _authoringDiagnosticsPumpStates.Clear();
            _diagnosticsRequestVersions.Clear();
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

            _ = ObserveFireAndForget(Task.Run(async () =>
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
            }, token), "editor folding update", new Dictionary<string, object?>
            {
                ["editorRegistrationVersion"] = registrationVersion,
                ["editorIdentity"] = RuntimeHelpers.GetHashCode(editorTextEditor)
            });
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

        private bool ApplyPersistedSyntaxDiagnosticsToEditor(ErrorMarkerRenderer errorRenderer, EditorTabViewModel tab, TextEditor editorTextEditor)
        {
            var rendererChanged = errorRenderer.SetErrors(BuildParseErrorsFromTab(tab));
            var glyphMarginChanged = false;

            if (_diagnosticGlyphMargins.TryGetValue(editorTextEditor, out var diagnosticGlyphMargin))
            {
                glyphMarginChanged = diagnosticGlyphMargin.SetDiagnostics(tab.SyntaxDiagnosticSpans);
            }

            var visualsChanged = rendererChanged || glyphMarginChanged;
            if (visualsChanged)
            {
                editorTextEditor.TextArea.TextView.Redraw();
            }

            return visualsChanged;
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
            DisposeLiveSyntaxPump(editorTextEditor);
            ClearDiagnosticLayers(editorTextEditor);
            CancelPendingFolding(editorTextEditor);

            if (_tabByEditor.TryGetValue(editorTextEditor, out var tab))
            {
                _liveAnalyzerEligibleRevisions.RemoveWhere(item => item.DocumentId == tab.DiagnosticDocument.DocumentId);
                _scriptDiagnosticStore.ClearDocument(tab.DiagnosticDocument.DocumentId);
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

        private void HandleTerminalAppShortcutRequested(string command)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AppLogger.Debug("Terminal", $"Terminal requested app shortcut. Command={command}.");
                DeveloperDiagnostics.LogUserAction(
                    "Terminal",
                    "TerminalAppShortcut",
                    "Terminal requested an app-level shortcut.",
                    new Dictionary<string, object?>
                    {
                        ["command"] = command,
                        ["focusedElement"] = DescribeFocusedElement()
                    });

                if (string.Equals(command, "find", StringComparison.OrdinalIgnoreCase))
                {
                    OpenFindReplaceWindow(showReplace: false);
                }
                else if (string.Equals(command, "replace", StringComparison.OrdinalIgnoreCase))
                {
                    OpenFindReplaceWindow(showReplace: true);
                }
                else if (string.Equals(command, "leave_terminal", StringComparison.OrdinalIgnoreCase))
                {
                    FocusEditorOrConsoleTabAfterTerminalShortcut();
                }
            }));
        }

        private void FocusEditorOrConsoleTabAfterTerminalShortcut()
        {
            var editorTextEditor = FindActiveEditor();
            if (editorTextEditor is not null)
            {
                SetTerminalActive(false, "TerminalShortcut:LeaveTerminal");
                editorTextEditor.Focus();
                editorTextEditor.TextArea?.Caret.BringCaretToView();
                return;
            }

            SetTerminalActive(false, "TerminalShortcut:LeaveTerminalNoEditor");
            ConsoleBottomPaneTab.Focus();
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

        private void ForceCompletionNow(TextEditor editorTextEditor, string invocationSource)
        {
            AppLogger.Debug(
                "EditorCompletion",
                $"Force completion requested. Source={invocationSource}, Handler=EditorTextEditor_PreviewKeyDown, Fragment='{GetCurrentWordFragment(editorTextEditor)}', InsideParameterToken={IsCaretInsideParameterToken(editorTextEditor)}, CaretOffset={editorTextEditor.CaretOffset}, MetadataPhase={_lastEditorMetadataWarmupPhase}, CompletionEnginePhase={_lastCompletionEnginePhase}, MetadataWarmupTriggered=False.");
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
                    $"Starting completion request. AutoTriggered={autoTriggered}, ForceCompletion={forceCompletion}, IncludeEngine={includeEngine}, Fragment='{fragment}', InsideParameterToken={insideParameterToken}, EngineWaitMs={engineWaitMilliseconds}, CaretOffset={editorTextEditor.CaretOffset}, CompletionEnginePhase={_lastCompletionEnginePhase}.");

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
            DeveloperDiagnostics.LogInfo(
                "EditorProductivity",
                "Auto-inserted a matching closing delimiter.",
                new Dictionary<string, object?>
                {
                    ["openingDelimiter"] = closing switch { '}' => '{', ')' => '(', ']' => '[', _ => closing },
                    ["closingDelimiter"] = closing,
                    ["offset"] = offset
                });
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

        private static Task ObserveFireAndForget(
            Task task,
            string operationName,
            IReadOnlyDictionary<string, object?>? operationMetadata = null)
        {
            return task.ContinueWith(
                completedTask =>
                {
                    var aggregateException = completedTask.Exception;
                    if (aggregateException is null)
                    {
                        return;
                    }

                    var metadata = operationMetadata is null
                        ? new Dictionary<string, object?>()
                        : new Dictionary<string, object?>(operationMetadata);
                    var innerExceptions = aggregateException.Flatten().InnerExceptions;
                    metadata["operationName"] = operationName;
                    metadata["aggregateInnerExceptionCount"] = innerExceptions.Count;
                    metadata["aggregateInnerExceptionTypes"] = string.Join(
                        ",",
                        innerExceptions.Take(5).Select(exception => exception.GetType().Name));
                    metadata["aggregateInnerExceptionTypesTruncated"] = innerExceptions.Count > 5;

                    AppLogger.Error("Editor", $"Detached editor task failed. Operation={operationName}.", aggregateException);
                    DeveloperDiagnostics.LogException(
                        "Editor",
                        aggregateException,
                        "Detached editor task failed.",
                        metadata);
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
            ApplyEditorCommand(editor, "ToggleComment", () => EditorProductivityCommands.ToggleComment(editor.Document, editor.SelectionStart, editor.SelectionLength));
        }

        private static void MoveLineUp(TextEditor editor)
        {
            if (editor.Document is null) return;
            ApplyEditorCommand(editor, "MoveLineUp", () => EditorProductivityCommands.MoveLines(editor.Document, editor.SelectionStart, editor.SelectionLength, -1));
        }

        private static void MoveLineDown(TextEditor editor)
        {
            if (editor.Document is null) return;
            ApplyEditorCommand(editor, "MoveLineDown", () => EditorProductivityCommands.MoveLines(editor.Document, editor.SelectionStart, editor.SelectionLength, 1));
        }

        private static bool HasMultiLineEditorSelection(TextEditor editor)
        {
            return editor.Document is not null &&
                   editor.SelectionLength > 0 &&
                   editor.Document.GetLineByOffset(editor.SelectionStart).LineNumber !=
                   editor.Document.GetLineByOffset(Math.Min(editor.SelectionStart + editor.SelectionLength, editor.Document.TextLength)).LineNumber;
        }

        private static void ApplyEditorCommand(TextEditor editor, string commandName, Func<EditorCommandResult> command)
        {
            if (editor.Document is null || editor.IsReadOnly)
            {
                DeveloperDiagnostics.LogDecision("Editor", commandName, "Editor command ignored because the document is unavailable or read-only.", "Rejected");
                return;
            }

            var result = command();
            editor.Select(Math.Clamp(result.SelectionStart, 0, editor.Document.TextLength), Math.Clamp(result.SelectionLength, 0, editor.Document.TextLength - Math.Clamp(result.SelectionStart, 0, editor.Document.TextLength)));
            DeveloperDiagnostics.LogInfo("Editor", $"Editor productivity command applied: {commandName}.", new Dictionary<string, object?>
            {
                ["selectionStart"] = result.SelectionStart,
                ["selectionLength"] = result.SelectionLength,
                ["documentLength"] = editor.Document.TextLength
            });
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
                new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(selectedText))
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
            SetPropertyIfAvailable(editorTextEditor.TextArea, "SelectionBorder", null);
            SetPropertyIfAvailable(editorTextEditor.TextArea, "SelectionCornerRadius", 0d);

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
                    ShowIdeMessage("Invalid Color", "Please enter a valid color in #RRGGBB format. Example: #0F4C81");
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
                    await TearDownDebugSessionAsync().ConfigureAwait(true);
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
                ViewModel.ClearDebugOutput();
                ViewModel.AppendDebugOutput($"Starting debugger for {Path.GetFileName(launchScriptPath)}");
                SelectDebugOutputBottomPane("Debug session started; showing output from the separate debugger process.");
                TraceDebugShell("StartDebug_Click", $"Created PsesDebugSession; sessionHash={debugSession.GetHashCode()}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogInfo("Debugger", "PsesDebugSession object created.", new Dictionary<string, object?> { ["sessionHash"] = debugSession.GetHashCode() });
                _debugSessionStateChangedHandler = state => Dispatcher.BeginInvoke(new Action(() =>
                {
                    _ = HandleDebugSessionStateChangedAsync(debugSession, state);
                }));
                debugSession.StateChanged += _debugSessionStateChangedHandler;

                debugSession.BreakpointHit += (scriptPath, lineNumber) => Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (ViewModel is null || !ReferenceEquals(_debugSession, debugSession))
                    {
                        return;
                    }

                    var currentState = debugSession.CurrentState;
                    TraceDebugShell("DebugSession.BreakpointHit", $"scriptPathPresent={!string.IsNullOrWhiteSpace(scriptPath)}; lineNumber={lineNumber}; sessionState={currentState}; {DescribeDebugUiState()}");
                    if (currentState != DebugSessionState.Paused)
                    {
                        TraceDebugShell("DebugSession.BreakpointHit", $"Ignored stale breakpoint notification because the session is no longer paused; currentState={currentState}; {DescribeDebugUiState()}");
                        DeveloperDiagnostics.LogDecision(
                            "Debugger",
                            "BreakpointHit",
                            "Breakpoint hit notification was ignored because the active debug session was no longer paused.",
                            "IgnoredStaleBreakpointHit",
                            new Dictionary<string, object?>
                            {
                                ["scriptPath"] = scriptPath,
                                ["lineNumber"] = lineNumber,
                                ["currentState"] = currentState.ToString()
                            });
                        RefreshDebugCommandAvailability(false);
                        return;
                    }

                    DeveloperDiagnostics.LogStateTransition(
                        "Debugger",
                        "BreakpointHit",
                        currentState.ToString(),
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

                debugSession.SessionEnded += () => Dispatcher.BeginInvoke(new Action(async () =>
                {
                    if (ViewModel is null || !ReferenceEquals(_debugSession, debugSession))
                    {
                        return;
                    }

                    TraceDebugShell("DebugSession.SessionEnded", $"SessionEnded fired; sessionState={debugSession.CurrentState}; {DescribeDebugUiState()}");
                    DeveloperDiagnostics.LogInfo("Debugger", "Debug session ended event received.");
                    ViewModel.AppendDebugOutput("Debugger session ended.");
                    await TearDownDebugSessionAsync(DebugTeardownReason.SessionEndedEvent).ConfigureAwait(true);
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
                            new Dictionary<string, object?>(DeveloperDiagnostics.CreatePrivateTextMetadata(chunk))
                            {
                                ["containsPromptMarker"] = containsPromptMarker,
                                ["containsEndedMarker"] = containsEndedMarker,
                                ["containsBreakpointText"] = containsBreakpointText,
                                ["containsAtLine"] = containsAtLine
                            });
                    }
                    ViewModel.AppendDebugOutput(chunk ?? string.Empty);

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
                    ViewModel.AppendDebugOutput($"Debugger start failed: {ex.Message}");
                    await TearDownDebugSessionAsync(DebugTeardownReason.StartFailure).ConfigureAwait(true);
                    ViewModel.StatusText = $"Debug start failed: {ex.Message}";
                }
            }
            catch (Exception ex)
            {
                TraceDebugShell("StartDebug_Click", $"Preparation failed; exceptionType={ex.GetType().Name}; message={ex.Message}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogException("Debugger", ex, "Debug preparation failed.");
                ViewModel.AppendDebugOutput($"Debugger preparation failed: {ex.Message}");
                await TearDownDebugSessionAsync(DebugTeardownReason.PreparationFailure).ConfigureAwait(true);
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

        private async void StopDebug_Click(object sender, RoutedEventArgs e)
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
            var stopped = await TearDownDebugSessionAsync(DebugTeardownReason.UserStop).ConfigureAwait(true);
            ViewModel.AppendDebugOutput(stopped
                ? "Debugger session stopped."
                : "Debugger teardown did not fully complete within the bounded shutdown window.");
            ViewModel.StatusText = stopped
                ? "Debug session stopped"
                : "Debug session stop incomplete — see Debug Output and developer diagnostics";
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
            var sessionState = _debugSession?.CurrentState;
            var hasSession = _debugSession is not null && sessionState != DebugSessionState.Stopped;
            var isPaused = hasSession && sessionState == DebugSessionState.Paused;
            var canStart = !hasSession && CanStartDebugSession();

            if (paused != isPaused)
            {
                TraceDebugShell("RefreshDebugCommandAvailability", $"Ignoring stale paused argument; pausedArgument={paused}; actualPaused={isPaused}; sessionState={sessionState?.ToString() ?? "(null)"}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogDecision(
                    "Debugger",
                    "RefreshDebugCommandAvailability",
                    "Debug command availability used the active session state instead of a stale caller-supplied paused value.",
                    "UseActualDebugSessionState",
                    new Dictionary<string, object?>
                    {
                        ["pausedArgument"] = paused,
                        ["actualPaused"] = isPaused,
                        ["sessionState"] = sessionState?.ToString()
                    });
            }

            StartDebugMenuItem.IsEnabled  = canStart;
            DebugToggleButton.IsEnabled   = canStart || hasSession;
            StepIntoMenuItem.IsEnabled    = isPaused;
            StepOverMenuItem.IsEnabled    = isPaused;
            StepOutMenuItem.IsEnabled     = isPaused;
            ContinueMenuItem.IsEnabled    = isPaused;
            StopDebugMenuItem.IsEnabled   = hasSession;
            StepIntoButton.IsEnabled      = isPaused;
            StepOverButton.IsEnabled      = isPaused;
            StepOutButton.IsEnabled       = isPaused;
            ContinueButton.IsEnabled      = isPaused;

            // Keep the ViewModel in sync so CanRunScript() can block the Run button
            // while a debug session is active.
            if (ViewModel is not null)
            {
                ViewModel.IsDebugSessionActive = hasSession;
            }

            TraceDebugShell("RefreshDebugCommandAvailability", $"pausedArgument={paused}; actualPaused={isPaused}; sessionState={sessionState?.ToString() ?? "(null)"}; hasSession={hasSession}; canStart={canStart}; {DescribeDebugUiState()}");
            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseUiEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "UI",
                    "Debug command availability refreshed.",
                    new Dictionary<string, object?>
                    {
                        ["pausedArgument"] = paused,
                        ["actualPaused"] = isPaused,
                        ["sessionState"] = sessionState?.ToString(),
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

        private async Task HandleDebugSessionStateChangedAsync(IDebugSession debugSession, DebugSessionState state)
        {
            var actualState = debugSession.CurrentState;
            var currentSessionState = _debugSession?.CurrentState.ToString() ?? "(null)";
            TraceDebugShell("HandleDebugSessionStateChanged", $"Received state change; incomingState={state}; actualState={actualState}; sessionMatches={ReferenceEquals(_debugSession, debugSession)}; currentSessionState={currentSessionState}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogStateTransition("Debugger", "DebugSessionStateChanged", currentSessionState, actualState.ToString(), "Debug session state changed.");
            if (!ReferenceEquals(_debugSession, debugSession))
            {
                return;
            }

            if (state != actualState)
            {
                TraceDebugShell("HandleDebugSessionStateChanged", $"Using current session state instead of stale queued state event; incomingState={state}; actualState={actualState}; {DescribeDebugUiState()}");
                DeveloperDiagnostics.LogDecision(
                    "Debugger",
                    "HandleDebugSessionStateChanged",
                    "A queued debug state notification was stale by the time it reached the UI thread, so the shell used the session's current state.",
                    "UseActualDebugSessionState",
                    new Dictionary<string, object?>
                    {
                        ["incomingState"] = state.ToString(),
                        ["actualState"] = actualState.ToString()
                    });
            }

            if (actualState == DebugSessionState.Stopped)
            {
                ViewModel?.AppendDebugOutput("Debugger session ended.");
                await TearDownDebugSessionAsync(DebugTeardownReason.SessionStoppedState).ConfigureAwait(true);
                if (ViewModel is not null)
                {
                    ViewModel.StatusText = "Debug session ended";
                }

                TraceDebugShell("HandleDebugSessionStateChanged", $"Handled stopped state; {DescribeDebugUiState()}");
                return;
            }

            var isPaused = actualState == DebugSessionState.Paused;
            RefreshDebugCommandAvailability(isPaused);

            if (isPaused && ViewModel is not null)
            {
                ViewModel.StatusText = "Debug session paused — choose Continue, Step Over, Step Into, Step Out, or Stop Debug";
                ScheduleDebugPanelRefresh("StateChangedPaused");
            }
            else
            {
                ClearLiveDebugVariableCache($"Debug session state changed to {actualState}");
            }
        }

        private async Task<bool> TearDownDebugSessionAsync(
            DebugTeardownReason reason = DebugTeardownReason.PreLaunchCleanup)
        {
            var debugSession = _debugSession;
            var operationId = $"DebugTeardown-{Guid.NewGuid():N}";
            TraceDebugShell(
                "TearDownDebugSessionAsync",
                $"Entry; reason={reason}; sessionPresent={debugSession is not null}; operationId={operationId}; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogMethodEntry(
                "Debugger",
                "TearDownDebugSessionAsync entered.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["sessionPresent"] = debugSession is not null,
                    ["operationId"] = operationId,
                    ["terminalMutationRequested"] = false
                });

            if (debugSession is not null && _debugSessionStateChangedHandler is not null)
            {
                debugSession.StateChanged -= _debugSessionStateChangedHandler;
            }

            _debugSessionStateChangedHandler = null;
            Interlocked.Increment(ref _debugPanelRefreshVersion);
            _debugSession = null;
            _activeDebugTab = null;
            _activeDebugLaunchPath = null;
            var snapshotToDelete = _activeDebugSnapshotPath;
            _activeDebugSnapshotPath = null;
            RefreshDebugCommandAvailability(false);
            ClearDebugCurrentLine();
            ClearDebugPanels();
            SetDebugPanelVisible(false);

            var stopped = true;
            if (debugSession is not null)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    stopped = await debugSession.StopAsync(timeout.Token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    stopped = false;
                    AppLogger.Warning(
                        "Debug",
                        $"Debugger teardown exceeded the shell timeout. OperationId={operationId}, Reason={reason}.");
                    DeveloperDiagnostics.LogWarning(
                        "Debugger",
                        "Debugger teardown exceeded the shell timeout.",
                        new Dictionary<string, object?>
                        {
                            ["operationId"] = operationId,
                            ["reason"] = reason.ToString(),
                            ["timeoutMs"] = 5000
                        });
                }
                catch (Exception ex)
                {
                    stopped = false;
                    AppLogger.Error("Debug", $"Debugger teardown failed. OperationId={operationId}, Reason={reason}.", ex);
                    DeveloperDiagnostics.LogException(
                        "Debugger",
                        ex,
                        "Debugger teardown failed.",
                        new Dictionary<string, object?>
                        {
                            ["operationId"] = operationId,
                            ["reason"] = reason.ToString()
                        });
                }
                finally
                {
                    debugSession.Dispose();
                }
            }

            TryDeleteTemporaryDebugSnapshot(snapshotToDelete);
            TraceDebugShell(
                "TearDownDebugSessionAsync",
                $"Completed; reason={reason}; stopped={stopped}; operationId={operationId}; terminalMutationRequested=false; {DescribeDebugUiState()}");
            DeveloperDiagnostics.LogMethodExit(
                "Debugger",
                "TearDownDebugSessionAsync completed without writing to or focusing the interactive terminal.",
                new Dictionary<string, object?>
                {
                    ["reason"] = reason.ToString(),
                    ["stopped"] = stopped,
                    ["operationId"] = operationId,
                    ["terminalMutationRequested"] = false
                });
            return stopped;
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
            var selectedFindText = GetActiveEditorSelectedSearchText();
            if (!string.IsNullOrEmpty(selectedFindText))
            {
                _lastFindText = selectedFindText;
            }

            _findReplaceWindow ??= new FindReplaceWindow(this, _lastFindText, _lastReplaceText, _lastFindMatchCase);
            _findReplaceWindow.FindText = _lastFindText;
            _findReplaceWindow.ReplaceText = _lastReplaceText;
            _findReplaceWindow.MatchCase = _lastFindMatchCase;
            _findReplaceWindow.WholeWord = _lastFindWholeWord;
            _findReplaceWindow.UseRegex = _lastFindUseRegex;
            _findReplaceWindow.ShowStatus(null);
            _findReplaceWindow.RefreshResultList();
            _findReplaceWindow.Show();
            _findReplaceWindow.Activate();
            _findReplaceWindow.SetMode(showReplace);
        }

        private string GetActiveEditorSelectedSearchText()
        {
            if (FindActiveEditor() is not TextEditor editorTextEditor ||
                editorTextEditor.Document is null ||
                editorTextEditor.SelectionLength <= 0)
            {
                return string.Empty;
            }

            var selectedText = editorTextEditor.SelectedText ?? string.Empty;
            if (string.IsNullOrWhiteSpace(selectedText))
            {
                return string.Empty;
            }

            // The Find/Replace input is intentionally a single-line field. Do not
            // push a multi-line selection into it because that makes the dialog hard
            // to read and can look broken to non-advanced users.
            if (selectedText.Contains('\r') || selectedText.Contains('\n'))
            {
                return string.Empty;
            }

            return selectedText;
        }

        public IReadOnlyList<FindResultRow> GetFindResults(string findText, bool matchCase, bool wholeWord = false, bool useRegex = false)
        {
            var results = new List<FindResultRow>();
            if (FindActiveEditor() is not TextEditor editorTextEditor ||
                editorTextEditor.Document is null ||
                string.IsNullOrWhiteSpace(findText))
            {
                return results;
            }

            var text = editorTextEditor.Text ?? string.Empty;
            if (text.Length == 0)
            {
                return results;
            }

            var rx = BuildFindRegex(findText, matchCase, wholeWord, useRegex);
            var matches = rx.Matches(text);
            var resultNumber = 1;

            foreach (Match match in matches)
            {
                if (!match.Success)
                {
                    continue;
                }

                var safeOffset = Math.Clamp(match.Index, 0, text.Length);
                var line = editorTextEditor.Document.GetLineByOffset(safeOffset);
                var column = safeOffset - line.Offset + 1;
                var lineText = editorTextEditor.Document.GetText(line.Offset, line.Length).Trim();
                if (string.IsNullOrEmpty(lineText))
                {
                    lineText = "(blank line)";
                }

                results.Add(new FindResultRow(
                    resultNumber++,
                    line.LineNumber,
                    Math.Max(1, column),
                    safeOffset,
                    match.Length,
                    lineText));
            }

            return results;
        }

        public void NavigateToFindResult(int offset, int length, int lineNumber, int column)
        {
            if (FindActiveEditor() is not TextEditor editorTextEditor)
            {
                return;
            }

            var textLength = editorTextEditor.Text?.Length ?? 0;
            if (textLength == 0)
            {
                return;
            }

            var safeOffset = Math.Clamp(offset, 0, textLength);
            var safeLength = Math.Clamp(length, 0, textLength - safeOffset);
            editorTextEditor.Select(safeOffset, safeLength);
            editorTextEditor.ScrollTo(lineNumber, Math.Max(1, column));
            editorTextEditor.CaretOffset = Math.Min(textLength, safeOffset + safeLength);
            editorTextEditor.Focus();

            if (ViewModel is not null)
            {
                ViewModel.StatusText = $"Jumped to search result at line {lineNumber}, column {column}";
            }
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
            Close();
        }

        private void PSScriptAnalyzerEnabled_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.PSScriptAnalyzerEnabled = PSScriptAnalyzerEnabledMenuItem.IsChecked;
            if (!_loadedSettings.PSScriptAnalyzerEnabled)
            {
                _liveAnalyzerScheduler?.CancelAll();
                foreach (var tab in _editorByTab.Keys.ToList()) _scriptDiagnosticStore.ClearDiagnostics(tab.DiagnosticDocument.DocumentId, ScriptDiagnosticSource.PSScriptAnalyzer);
            }
            SaveAnalyzerSettings();
            if (ViewModel is not null) ViewModel.StatusText = _loadedSettings.PSScriptAnalyzerEnabled ? "PSScriptAnalyzer enabled" : "PSScriptAnalyzer disabled";
        }

        private void AnalyzeCurrentDocument_Click(object sender, RoutedEventArgs e)
        {
            _ = AnalyzeCurrentDocumentAsync();
        }

        private void PSScriptAnalyzerAnalyzeWhileEditing_Click(object sender, RoutedEventArgs e)
        {
            _loadedSettings.PSScriptAnalyzerAnalyzeWhileEditing = PSScriptAnalyzerAnalyzeWhileEditingMenuItem.IsChecked;
            if (!_loadedSettings.PSScriptAnalyzerAnalyzeWhileEditing) _liveAnalyzerScheduler?.CancelAll();
            else if (FindActiveEditor() is TextEditor activeEditor) ScheduleDiagnostics(activeEditor);
            SaveAnalyzerSettings();
            if (ViewModel is not null) ViewModel.StatusText = _loadedSettings.PSScriptAnalyzerAnalyzeWhileEditing ? "PSScriptAnalyzer live analysis enabled" : "PSScriptAnalyzer live analysis disabled";
        }

        private void PSScriptAnalyzerSeverity_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not WpfMenuItem item) return;
            _loadedSettings.PSScriptAnalyzerSeverityFilter = item.Header?.ToString() switch
            {
                "Error" => "Error",
                "Warning" => "Warning",
                _ => "All"
            };
            UpdateAnalyzerSettingsMenu();
            if (ViewModel?.SelectedTab is EditorTabViewModel tab)
                _liveAnalyzerEligibleRevisions.Add((tab.DiagnosticDocument.DocumentId, tab.DiagnosticDocument.Revision));
            SaveAnalyzerSettings();
        }

        private void SaveAnalyzerSettings()
        {
            try { _applicationSettingsService.SaveSettings(_loadedSettings); }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogOperationFailure("Settings", "SaveAnalyzerSettings", "Analyzer settings could not be persisted.", ex);
                if (ViewModel is not null) ViewModel.StatusText = "Analyzer settings could not be saved";
            }
        }

        private void UpdateAnalyzerSettingsMenu()
        {
            if (PSScriptAnalyzerEnabledMenuItem is null) return;
            PSScriptAnalyzerEnabledMenuItem.IsChecked = _loadedSettings.PSScriptAnalyzerEnabled;
            PSScriptAnalyzerAnalyzeWhileEditingMenuItem.IsChecked = _loadedSettings.PSScriptAnalyzerAnalyzeWhileEditing;
            var severity = _loadedSettings.PSScriptAnalyzerSeverityFilter;
            PSScriptAnalyzerSeverityAllMenuItem.IsChecked = severity.Equals("All", StringComparison.OrdinalIgnoreCase);
            PSScriptAnalyzerSeverityErrorMenuItem.IsChecked = severity.Equals("Error", StringComparison.OrdinalIgnoreCase);
            PSScriptAnalyzerSeverityWarningMenuItem.IsChecked = severity.Equals("Warning", StringComparison.OrdinalIgnoreCase);
        }

        private async Task AnalyzeCurrentDocumentAsync()
        {
            var viewModel = ViewModel;
            var tab = viewModel?.SelectedTab;
            if (tab is null || viewModel is null) { if (viewModel is not null) viewModel.StatusText = "No current document"; return; }
            if (!_loadedSettings.PSScriptAnalyzerEnabled) { viewModel.StatusText = "PSScriptAnalyzer is disabled"; return; }
            var runtimePath = viewModel.EffectiveRuntimeExecutablePath;
            if (string.IsNullOrWhiteSpace(runtimePath)) { viewModel.StatusText = "PSScriptAnalyzer unavailable: no PowerShell 7 runtime"; return; }

            _manualAnalyzerCancellation?.Cancel();
            _manualAnalyzerCancellation?.Dispose();
            _manualAnalyzerCancellation = new CancellationTokenSource();
            var cancellationToken = _manualAnalyzerCancellation.Token;
            var snapshot = tab.DiagnosticDocument.Capture();
            viewModel.StatusText = "PSScriptAnalyzer: Analyzing...";
            try
            {
                _psScriptAnalyzerCoordinator ??= CreatePSScriptAnalyzerCoordinator(runtimePath);
                var request = new PSScriptAnalyzerRequest($"manual-{Guid.NewGuid():N}", snapshot.DocumentId.ToString(), snapshot.DocumentRevision, tab.FilePath, tab.Content, _loadedSettings.PSScriptAnalyzerSeverityFilter);
                var published = await _psScriptAnalyzerCoordinator.AnalyzeAndPublishAsync(request, cancellationToken).ConfigureAwait(true);
                if (published && tab.DiagnosticDocument.Revision == snapshot.DocumentRevision)
                {
                    var count = _scriptDiagnosticStore.GetDiagnostics(snapshot.DocumentId, ScriptDiagnosticSource.PSScriptAnalyzer).Count;
                    viewModel.StatusText = count > 0 ? $"PSScriptAnalyzer: Completed with {count} findings" : "PSScriptAnalyzer: No findings";
                }
                else if (!cancellationToken.IsCancellationRequested) viewModel.StatusText = "PSScriptAnalyzer: analysis failed";
            }
            catch (OperationCanceledException) { viewModel.StatusText = "PSScriptAnalyzer: Canceled"; }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogOperationFailure("PSScriptAnalyzer", "ManualAnalyzeCurrentDocument", "Manual analyzer command failed.", ex);
                viewModel.StatusText = "PSScriptAnalyzer: unavailable";
            }
        }

        private PSScriptAnalyzerDiagnosticsCoordinator CreatePSScriptAnalyzerCoordinator(string runtimePath)
        {
            _psScriptAnalyzerService = new PSScriptAnalyzerService(runtimePath);
            return new PSScriptAnalyzerDiagnosticsCoordinator(_psScriptAnalyzerService, _scriptDiagnosticStore);
        }

        private void ScheduleLiveAnalyzer(TextEditor editorTextEditor, string scriptSnapshot, bool parserHasErrors)
        {
            if (!_loadedSettings.PSScriptAnalyzerEnabled || !_loadedSettings.PSScriptAnalyzerAnalyzeWhileEditing || parserHasErrors || editorTextEditor.DataContext is not EditorTabViewModel tab) return;
            var snapshot = tab.DiagnosticDocument.Capture();
            if (!_liveAnalyzerEligibleRevisions.Contains((snapshot.DocumentId, snapshot.DocumentRevision))) return;
            if (scriptSnapshot.Length >= AuthoringDiagnosticsVeryLargeCharacterThreshold || CountLines(scriptSnapshot) >= AuthoringDiagnosticsVeryLargeLineThreshold)
            {
                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab)) ViewModel.StatusText = "PSScriptAnalyzer: large document — manual analysis only";
                _liveAnalyzerScheduler?.Cancel(tab.DiagnosticDocument.DocumentId);
                return;
            }
            var runtimePath = ViewModel?.EffectiveRuntimeExecutablePath;
            if (string.IsNullOrWhiteSpace(runtimePath)) return;
            _psScriptAnalyzerCoordinator ??= CreatePSScriptAnalyzerCoordinator(runtimePath);
            _liveAnalyzerScheduler ??= new PSScriptAnalyzerLiveAnalysisScheduler(
                (request, cancellationToken) => _psScriptAnalyzerCoordinator?.AnalyzeAndPublishAsync(request, cancellationToken) ?? Task.FromResult(false));
            _liveAnalyzerScheduler.ActivityChanged -= LiveAnalyzerScheduler_ActivityChanged;
            _liveAnalyzerScheduler.ActivityChanged += LiveAnalyzerScheduler_ActivityChanged;
            _liveAnalyzerEligibleRevisions.Remove((snapshot.DocumentId, snapshot.DocumentRevision));
            _liveAnalyzerScheduler.Schedule(snapshot.DocumentId, snapshot.DocumentRevision, tab.FilePath, scriptSnapshot, _loadedSettings.PSScriptAnalyzerSeverityFilter);
            if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab)) ViewModel.StatusText = "PSScriptAnalyzer: waiting for edit pause";
        }

        private void LiveAnalyzerScheduler_ActivityChanged(object? sender, PSScriptAnalyzerActivity activity)
        {
            _ = Dispatcher.BeginInvoke(new Action(() =>
            {
                if (ViewModel?.SelectedTab is not EditorTabViewModel tab || tab.DiagnosticDocument.DocumentId != activity.DocumentId || tab.DiagnosticDocument.Revision != activity.Revision) return;
                ViewModel.StatusText = activity.State switch
                {
                    "Analyzing" => "PSScriptAnalyzer: Analyzing...",
                    "Canceled" => "PSScriptAnalyzer: Canceled",
                    _ => "PSScriptAnalyzer: waiting for edit pause"
                };
            }), DispatcherPriority.Background);
        }

        private void ResetPSScriptAnalyzerRuntime()
        {
            _liveAnalyzerScheduler?.CancelAll();
            _manualAnalyzerCancellation?.Cancel();
            _psScriptAnalyzerService?.Dispose();
            _psScriptAnalyzerService = null;
            _psScriptAnalyzerCoordinator = null;
            foreach (var tab in _editorByTab.Keys.ToList()) _scriptDiagnosticStore.ClearDiagnostics(tab.DiagnosticDocument.DocumentId, ScriptDiagnosticSource.PSScriptAnalyzer);
        }

        private void ScriptDiagnosticStore_Changed(object? sender, ScriptDiagnosticsChangedEventArgs e)
        {
            if (!Dispatcher.CheckAccess()) { _ = Dispatcher.BeginInvoke(new Action(() => ScriptDiagnosticStore_Changed(sender, e))); return; }
            foreach (var pair in _editorByTab.Where(pair => pair.Key.DiagnosticDocument.DocumentId == e.DocumentId).ToList())
            {
                var tab = pair.Key;
                var editor = pair.Value;
                var diagnostics = _scriptDiagnosticStore.GetDiagnostics(tab.DiagnosticDocument.DocumentId)
                    .Where(diagnostic => diagnostic.DocumentRevision == tab.DiagnosticDocument.Revision)
                    .Select(diagnostic =>
                    {
                        var range = ScriptDiagnosticRangeMapper.Map(tab.Content, diagnostic.StartLine, diagnostic.StartColumn, diagnostic.EndLine, diagnostic.EndColumn);
                        return new ParseErrorInfo(diagnostic.Message, range.StartOffset, range.EndOffset, diagnostic.Severity == ScriptDiagnosticSeverity.Error ? "Error" : "Warning", diagnostic.SourceId.ToString(), diagnostic.RuleId);
                    }).ToList();
                _analyzerDiagnosticLayers[editor] = new DiagnosticLayerSnapshot(tab.Content, diagnostics);
                ApplyCombinedDiagnosticsToTab(editor, tab.Content, "Diagnostics: OK");
            }
        }

        private async void Window_Closing(object? sender, CancelEventArgs e)
        {
            DeveloperDiagnostics.LogEventHandlerEntry("UI", "Window_Closing", "Window_Closing entered.");
            if (_allowWindowClose)
            {
                if (ViewModel is not null)
                {
                    ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                    ViewModel.ExeExportProgressChanged -= ViewModel_ExeExportProgressChanged;
                }

                _intelliSenseService.MetadataWarmupStatusChanged -= IntelliSenseService_MetadataWarmupStatusChanged;
                _intelliSenseService.CompletionEngineStatusChanged -= IntelliSenseService_CompletionEngineStatusChanged;
                _scriptDiagnosticStore.Changed -= ScriptDiagnosticStore_Changed;
                _bottomToolWindow?.CloseForOwnerShutdown();
                _debugPaneWindow?.CloseForOwnerShutdown();
                _exportProgressWindow?.CloseForOwnerShutdown();
                DisposeLiveSyntaxPumps();
                DisposeAuthoringDiagnosticsPumps();
                _liveSyntaxDiagnosticsService.Dispose();
                _diagnosticsService.Dispose();
                _manualAnalyzerCancellation?.Cancel();
                _manualAnalyzerCancellation?.Dispose();
                _psScriptAnalyzerService?.Dispose();
                if (_liveAnalyzerScheduler is not null) _liveAnalyzerScheduler.ActivityChanged -= LiveAnalyzerScheduler_ActivityChanged;
                _liveAnalyzerScheduler?.Dispose();
                _intelliSenseService.Dispose();
                _activeCompletionCts?.Cancel();
                _activeCompletionCts?.Dispose();
                CancelActiveQuickInfoRequest();
                CloseActiveEditorToolTip();
                DeveloperDiagnostics.LogEventHandlerExit("UI", "Window_Closing", "Window_Closing exited on final close path.");
                return;
            }

            e.Cancel = true;
            if (_terminalShutdownInProgress)
            {
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "Window_Closing",
                    "A duplicate close request was deferred while terminal teardown was already running.",
                    "AwaitExistingShutdown");
                return;
            }

            if (!TryPrepareForWindowClose())
            {
                return;
            }

            _terminalShutdownInProgress = true;
            DeveloperDiagnostics.LogStateTransition(
                "Terminal",
                "Window_Closing",
                "Running",
                "Stopping",
                "Window close is awaiting bounded terminal teardown.");

            try
            {
                var debuggerStopped = await TearDownDebugSessionAsync(
                    DebugTeardownReason.ApplicationShutdown).ConfigureAwait(true);
                if (!debuggerStopped)
                {
                    AppLogger.Warning(
                        "Debug",
                        "Application close is continuing after bounded debugger teardown reported incomplete cleanup.");
                }

                var terminalStopped = ViewModel is null ||
                    await ViewModel.ShutdownTerminalAsync().ConfigureAwait(true);
                if (!terminalStopped)
                {
                    AppLogger.Warning(
                        "Terminal",
                        "Application close is continuing after bounded terminal teardown reported incomplete cleanup.");
                }
            }
            finally
            {
                _terminalShutdownInProgress = false;
                _allowWindowClose = true;
            }

            DeveloperDiagnostics.LogEventHandlerExit(
                "UI",
                "Window_Closing",
                "Bounded terminal teardown completed; queuing the final close request.");

            // Window_Closing is an async-void WPF event handler. Even though the
            // original close request was cancelled above, WPF still considers the
            // Window to be inside its Closing operation until this handler returns.
            // Calling Close() directly from this continuation can therefore throw:
            // "Cannot ... call ... Close ... while a Window is closing."
            //
            // Queue the final Close() so the current Closing handler can return first.
            // _allowWindowClose is already true, so the queued close takes the final
            // cleanup path without starting terminal/debug teardown again.
            _ = Dispatcher.BeginInvoke(
                new Action(Close),
                DispatcherPriority.Normal);
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
                _lastKnownConsoleHeight = _loadedSettings.ConsoleHeight!.Value;
                ConsoleRowDefinition.Height = new GridLength(_lastKnownConsoleHeight, GridUnitType.Pixel);
            }

            if (IsUsableLength(_loadedSettings.ConsoleSideWidth, MinimumConsoleSideWidth))
            {
                _lastKnownConsoleSideWidth = _loadedSettings.ConsoleSideWidth!.Value;
            }

            if (IsUsableLength(_loadedSettings.DockedBottomToolWindowHeight, MinimumBottomToolWindowHeight))
            {
                _lastKnownBottomToolWindowHeight = _loadedSettings.DockedBottomToolWindowHeight!.Value;
            }

            if (IsUsableLength(_loadedSettings.DockedDebugPanelWidth, MinimumDebugPanelWidth))
            {
                _lastKnownDebugPanelWidth = _loadedSettings.DockedDebugPanelWidth!.Value;
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

            ApplyWorkspaceLayoutMode(RestoreWorkspaceLayoutMode(_loadedSettings.WorkspaceLayoutMode), "SettingsRestore");
            ApplyExplorerVisibilityLayout();
            SetDebugPanelVisible(_loadedSettings.IsDebugPanelVisible);
            RestoreBottomToolWindowFromSettings();

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
                ResetPSScriptAnalyzerRuntime();
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
                // If runtime discovery changes the selected runtime after startup,
                // request a background console warm-start/revalidation. The view model
                // serializes actual session starts so duplicate requests are harmless.
                RequestConsoleWarmStart("EffectiveRuntimeChanged");
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

        private void InitializeUiScaleMenu()
        {
            UiScaleMenuItem.Items.Clear();
            foreach (var percentage in _uiScaleService.SupportedPercentages)
            {
                var menuItem = new WpfMenuItem
                {
                    Header = $"{percentage}%",
                    IsCheckable = true,
                    Tag = percentage,
                    ToolTip = $"Set application UI Scale to {percentage}%"
                };
                AutomationProperties.SetName(menuItem, $"UI Scale {percentage} percent");
                menuItem.Click += UiScalePresetMenuItem_Click;
                UiScaleMenuItem.Items.Add(menuItem);
            }

            RefreshUiScaleMenuChecks();
        }

        private void UiScalePresetMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is WpfMenuItem { Tag: int percentage })
            {
                _uiScaleService.SetPercentage(percentage, "Menu");
            }
        }

        private void UiScaleService_ScaleChanged(object? sender, EventArgs e)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshUiScaleMenuChecks), DispatcherPriority.DataBind);
                return;
            }

            RefreshUiScaleMenuChecks();
        }

        private void RefreshUiScaleMenuChecks()
        {
            foreach (var item in UiScaleMenuItem.Items.OfType<WpfMenuItem>())
            {
                item.IsChecked = item.Tag is int percentage && percentage == _uiScaleService.CurrentPercentage;
            }
        }

        private void Window_Closed(object? sender, EventArgs e)
        {
            _uiScaleService.ScaleChanged -= UiScaleService_ScaleChanged;
        }

        private void ViewModel_ExeExportProgressChanged(object? sender, ExeExportProgressUpdate update)
        {
            if (update is null)
            {
                return;
            }

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(() => ViewModel_ExeExportProgressChanged(sender, update)));
                return;
            }

            if (_exportProgressWindow is null || !_exportProgressWindow.IsLoaded)
            {
                _exportProgressWindow = new ExportProgressWindow(update.OutputExecutablePath)
                {
                    Owner = this
                };
                _exportProgressWindow.Closed += ExportProgressWindow_Closed;
                _exportProgressWindow.Show();
                DeveloperDiagnostics.LogInfo(
                    "ExeExport",
                    "Export progress window opened.",
                    new Dictionary<string, object?>
                    {
                        ["destinationFileName"] = Path.GetFileName(update.OutputExecutablePath)
                    });
            }

            _exportProgressWindow.ApplyUpdate(update);
        }

        private void ExportProgressWindow_Closed(object? sender, EventArgs e)
        {
            if (sender is ExportProgressWindow exportProgressWindow)
            {
                exportProgressWindow.Closed -= ExportProgressWindow_Closed;
            }

            _exportProgressWindow = null;
        }


        private void IntelliSenseService_MetadataWarmupStatusChanged(object? sender, EditorMetadataWarmupStatusChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => ApplyEditorMetadataWarmupStatus(e.Status)));
        }

        private void IntelliSenseService_CompletionEngineStatusChanged(object? sender, PowerShellCompletionEngineStatusChangedEventArgs e)
        {
            if (e is null)
            {
                return;
            }

            Dispatcher.BeginInvoke(new Action(() => ApplyCompletionEngineStatus(e.Status)));
        }

        private void ApplyCompletionEngineStatus(PowerShellCompletionEngineStatus status)
        {
            if (status is null)
            {
                return;
            }

            _lastCompletionEnginePhase = status.Phase;

            var metadataIsActive = _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.Scheduled ||
                                   _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.CoreReady ||
                                   _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.BuildingCommandCatalog ||
                                   _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.DiscoveringModules ||
                                   _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.LoadingCommandMetadata ||
                                   _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.RefreshingCachedMetadata;
            if (metadataIsActive && status.Phase != PowerShellCompletionEnginePhase.Failed)
            {
                return;
            }

            var detailText = string.IsNullOrWhiteSpace(status.DetailText)
                ? "PS7 ScriptDesk is starting the live PowerShell completion engine."
                : status.DetailText;
            var runtimeCaption = string.IsNullOrWhiteSpace(status.RuntimePath)
                ? string.Empty
                : $"{Environment.NewLine}Runtime: {status.RuntimePath}";
            var elapsedText = status.ElapsedMilliseconds > 0
                ? $"{Environment.NewLine}Elapsed={status.ElapsedMilliseconds:N0} ms"
                : string.Empty;
            var tooltipText = $"{status.Message}{Environment.NewLine}{detailText}{elapsedText}{runtimeCaption}";

            switch (status.Phase)
            {
                case PowerShellCompletionEnginePhase.Initializing:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "⏳";
                    EditorMetadataStatusTextBlock.Text = status.Message;
                    ApplyEditorMetadataBadgeColors(GetLoadingBadgeBackgroundBrush(), GetLoadingBadgeBorderBrush(), GetLoadingBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;
                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                case PowerShellCompletionEnginePhase.Ready:
                    EditorMetadataStatusItem.Visibility = Visibility.Collapsed;
                    EditorMetadataStatusGlyph.Text = "✓";
                    EditorMetadataStatusTextBlock.Text = status.Message;
                    ApplyEditorMetadataBadgeColors(GetReadyBadgeBackgroundBrush(), GetReadyBadgeBorderBrush(), GetReadyBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;
                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                case PowerShellCompletionEnginePhase.Failed:
                    EditorMetadataStatusItem.Visibility = Visibility.Visible;
                    EditorMetadataStatusGlyph.Text = "!";
                    EditorMetadataStatusTextBlock.Text = status.Message;
                    ApplyEditorMetadataBadgeColors(GetFailureBadgeBackgroundBrush(), GetFailureBadgeBorderBrush(), GetFailureBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;
                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = status.Message;
                    }

                    break;

                default:
                    break;
            }

            AppLogger.Debug(
                "EditorCompletion",
                $"Completion engine readiness applied. Phase={status.Phase}, Runtime='{status.RuntimePath ?? "(unknown)"}', MetadataPhase={_lastEditorMetadataWarmupPhase}, ElapsedMs={status.ElapsedMilliseconds:N0}.");
        }

        private void ApplyEditorMetadataWarmupStatus(EditorMetadataWarmupStatus status)
        {
            if (status is null)
            {
                return;
            }

            _lastEditorMetadataWarmupPhase = status.Phase;

            var detailText = string.IsNullOrWhiteSpace(status.DetailText)
                ? "PS7 ScriptDesk is loading PowerShell IntelliSense metadata in the background."
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
                case EditorMetadataWarmupPhase.CoreReady:
                case EditorMetadataWarmupPhase.BuildingCommandCatalog:
                case EditorMetadataWarmupPhase.DiscoveringModules:
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
                    EditorMetadataStatusItem.Visibility = Visibility.Collapsed;
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
                    EditorMetadataStatusTextBlock.Text = "IntelliSense: Failed; see log";
                    ApplyEditorMetadataBadgeColors(GetFailureBadgeBackgroundBrush(), GetFailureBadgeBorderBrush(), GetFailureBadgeForegroundBrush());
                    EditorMetadataStatusBadge.ToolTip = tooltipText;

                    if (ViewModel is not null)
                    {
                        ViewModel.StatusText = "IntelliSense: Failed; see log";
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
            _ = background;
            _ = border;

            EditorMetadataStatusBadge.Background = System.Windows.Media.Brushes.Transparent;
            EditorMetadataStatusBadge.BorderBrush = System.Windows.Media.Brushes.Transparent;
            EditorMetadataStatusGlyph.Foreground = foreground;
            EditorMetadataStatusTextBlock.Foreground = foreground;
        }

        private System.Windows.Media.Brush GetThemeBrush(string resourceKey, byte fallbackRed, byte fallbackGreen, byte fallbackBlue)
        {
            return TryFindResource(resourceKey) as System.Windows.Media.Brush
                ?? CreateFrozenBrush(fallbackRed, fallbackGreen, fallbackBlue);
        }

        private static System.Windows.Media.Brush GetLoadingBadgeBackgroundBrush() => System.Windows.Media.Brushes.Transparent;
        private static System.Windows.Media.Brush GetLoadingBadgeBorderBrush() => System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.Brush GetLoadingBadgeForegroundBrush() => GetThemeBrush("Theme.Icon.Warning", 0xD9, 0x77, 0x06);
        private static System.Windows.Media.Brush GetRefreshBadgeBackgroundBrush() => System.Windows.Media.Brushes.Transparent;
        private static System.Windows.Media.Brush GetRefreshBadgeBorderBrush() => System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.Brush GetRefreshBadgeForegroundBrush() => GetThemeBrush("Theme.Icon.Accent", 0x25, 0x63, 0xEB);
        private static System.Windows.Media.Brush GetWarningBadgeBackgroundBrush() => System.Windows.Media.Brushes.Transparent;
        private static System.Windows.Media.Brush GetWarningBadgeBorderBrush() => System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.Brush GetWarningBadgeForegroundBrush() => GetThemeBrush("Theme.Icon.Warning", 0xD9, 0x77, 0x06);
        private static System.Windows.Media.Brush GetReadyBadgeBackgroundBrush() => System.Windows.Media.Brushes.Transparent;
        private static System.Windows.Media.Brush GetReadyBadgeBorderBrush() => System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.Brush GetReadyBadgeForegroundBrush() => GetThemeBrush("Theme.Icon.Success", 0x16, 0xA3, 0x4A);
        private static System.Windows.Media.Brush GetFailureBadgeBackgroundBrush() => System.Windows.Media.Brushes.Transparent;
        private static System.Windows.Media.Brush GetFailureBadgeBorderBrush() => System.Windows.Media.Brushes.Transparent;
        private System.Windows.Media.Brush GetFailureBadgeForegroundBrush() => GetThemeBrush("Theme.Icon.Danger", 0xDC, 0x26, 0x26);

        private void UpdateMetadataToast(EditorMetadataWarmupStatus status)
        {
            if (status is null)
            {
                return;
            }

            switch (status.Phase)
            {
                case EditorMetadataWarmupPhase.Scheduled:
                case EditorMetadataWarmupPhase.CoreReady:
                case EditorMetadataWarmupPhase.BuildingCommandCatalog:
                case EditorMetadataWarmupPhase.DiscoveringModules:
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
            var (title, body, phaseText, glyph, showProgress, backgroundResourceKey, borderResourceKey, foregroundResourceKey) = BuildMetadataToastVisual(status);

            MetadataToastTitleTextBlock.Text = title;
            MetadataToastBodyTextBlock.Text = body;
            MetadataToastPhaseTextBlock.Text = phaseText;
            MetadataToastGlyph.Text = glyph;
            MetadataToastCard.SetResourceReference(Border.BackgroundProperty, backgroundResourceKey);
            MetadataToastCard.SetResourceReference(Border.BorderBrushProperty, borderResourceKey);
            MetadataToastTitleTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
            MetadataToastBodyTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
            MetadataToastPhaseTextBlock.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
            MetadataToastGlyph.SetResourceReference(TextBlock.ForegroundProperty, foregroundResourceKey);
            MetadataToastProgressBar.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
            MetadataToastProgressBar.IsIndeterminate = showProgress;
        }

        private static (string Title, string Body, string PhaseText, string Glyph, bool ShowProgress, string BackgroundResourceKey, string BorderResourceKey, string ForegroundResourceKey) BuildMetadataToastVisual(EditorMetadataWarmupStatus status)
        {
            var detailText = string.IsNullOrWhiteSpace(status.DetailText)
                ? "PS7 ScriptDesk is preparing PowerShell IntelliSense metadata in the background."
                : status.DetailText.Trim();

            var progressText = status.HasProgress
                ? $"Processed {status.ProcessedCount:N0} of {status.TotalCount:N0} commands."
                : "You can keep using the editor while this runs.";

            if (status.Phase == EditorMetadataWarmupPhase.Warning)
            {
                return (
                    "IntelliSense: Degraded",
                    "PS7 ScriptDesk could not finish rebuilding PowerShell IntelliSense metadata. The previous cached metadata is still being used. Details were written to the app log.",
                    detailText,
                    "!",
                    false,
                    ThemeStatusWarningBackgroundResourceKey,
                    ThemeStatusWarningBorderResourceKey,
                    ThemeStatusWarningForegroundResourceKey);
            }

            if (status.Phase == EditorMetadataWarmupPhase.Failed)
            {
                return (
                    "IntelliSense: Failed",
                    "PS7 ScriptDesk could not prepare IntelliSense metadata for this PowerShell runtime. Basic editor features may still work, but IntelliSense may be limited. Details were written to the app log.",
                    detailText,
                    "!",
                    false,
                    ThemeStatusErrorBackgroundResourceKey,
                    ThemeStatusErrorBorderResourceKey,
                    ThemeStatusErrorForegroundResourceKey);
            }

            if (status.Phase == EditorMetadataWarmupPhase.Completed)
            {
                return (
                    "IntelliSense: Ready",
                    "PS7 ScriptDesk finished preparing full IntelliSense metadata for this PowerShell runtime. IntelliSense and autofill now have richer command, parameter, syntax, and help details.",
                    detailText,
                    "✓",
                    false,
                    ThemeSurfacePrimaryResourceKey,
                    ThemeBorderStrongResourceKey,
                    ThemeIconSuccessResourceKey);
            }

            if (status.Reason == EditorMetadataWarmupReason.ManualRefresh)
            {
                return (
                    "IntelliSense: Warming up...",
                    "PS7 ScriptDesk is rebuilding command, parameter, syntax, and help metadata for the selected PowerShell runtime.\n\nYou can keep using the editor. The existing metadata cache will remain available until the refresh completes successfully.",
                    $"{detailText} {progressText}".Trim(),
                    "↻",
                    true,
                    ThemeSurfacePrimaryResourceKey,
                    ThemeAccentPrimaryResourceKey,
                    ThemeIconAccentResourceKey);
            }

            var body = status.Reason == EditorMetadataWarmupReason.CacheRebuild
                ? "PS7 ScriptDesk is rebuilding command, parameter, syntax, and help metadata for this PowerShell runtime because the saved metadata cache could not be reused.\n\nYou can keep using the editor while this runs. IntelliSense will improve when loading completes."
                : "PS7 ScriptDesk is loading command, parameter, syntax, and help metadata for this PowerShell runtime.\n\nThis can take a while the first time a PowerShell version is used. You can keep using the editor while this runs. IntelliSense will improve when loading completes.";

            return (
                "IntelliSense: Warming up...",
                body,
                $"{detailText} {progressText}".Trim(),
                "⏳",
                true,
                ThemeSurfacePrimaryResourceKey,
                ThemeAccentPrimaryResourceKey,
                ThemeTextPrimaryResourceKey);
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
            _intelliSenseService.StartCompletionEngineWarmup(runtimeInfo);
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
                ShowIdeMessage("PowerShell Metadata Cache", "No PowerShell runtime is currently selected.");
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

            if (!ShowIdeConfirmation("Delete Current Runtime Metadata Cache", message, "Delete", "Keep"))
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

            if (!ShowIdeConfirmation("Delete All PowerShell Metadata Caches", message, "Delete", "Keep"))
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
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.CoreReady ||
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.BuildingCommandCatalog ||
                         _lastEditorMetadataWarmupPhase == EditorMetadataWarmupPhase.DiscoveringModules ||
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

        private void CloseExplorerPanelButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel is not null)
            {
                ViewModel.IsExplorerVisible = false;
            }
        }

        private void WorkspaceEditorMaximized_Click(object sender, RoutedEventArgs e)
        {
            ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.EditorMaximized, "ViewMenu");
        }

        private void WorkspaceConsoleMaximized_Click(object sender, RoutedEventArgs e)
        {
            ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.ConsoleMaximized, "ViewMenu");
        }

        private void WorkspaceHorizontalSplit_Click(object sender, RoutedEventArgs e)
        {
            ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.HorizontalSplit, "ViewMenu");
        }

        private void WorkspaceSideBySideSplit_Click(object sender, RoutedEventArgs e)
        {
            ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.SideBySideSplit, "ViewMenu");
        }

        private void WorkspaceRestoreDefault_Click(object sender, RoutedEventArgs e)
        {
            _lastKnownConsoleHeight = DefaultConsoleHeight;
            _lastKnownConsoleSideWidth = DefaultConsoleSideWidth;
            ApplyWorkspaceLayoutMode(WorkspaceLayoutMode.Default, "ViewMenu");
        }

        private void ApplyWorkspaceLayoutMode(WorkspaceLayoutMode mode, string source)
        {
            var previousMode = _workspaceLayoutMode;
            CaptureDockedBottomToolWindowHeight();
            CaptureWorkspaceLayoutSizes();
            _workspaceLayoutMode = mode;

            EditorPaneBorder.Visibility = Visibility.Visible;
            ConsolePaneBorder.Visibility = Visibility.Visible;
            EditorPaneBorder.Margin = new Thickness(0, 0, 0, 4);
            ConsolePaneBorder.Margin = new Thickness(0);
            ConsolePaneBorder.BorderThickness = new Thickness(0, 1, 0, 0);
            Grid.SetRow(EditorPaneBorder, 0);
            Grid.SetRowSpan(EditorPaneBorder, 1);
            Grid.SetColumn(EditorPaneBorder, 2);
            Grid.SetColumnSpan(EditorPaneBorder, 1);
            Grid.SetRow(ConsolePaneBorder, 2);
            Grid.SetRowSpan(ConsolePaneBorder, 1);
            Grid.SetColumn(ConsolePaneBorder, 2);
            Grid.SetColumnSpan(ConsolePaneBorder, 1);
            EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star);
            ConsoleSideSplitterColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            ConsoleSideColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            ConsoleSideColumnDefinition.MinWidth = 0;
            EditorConsoleColumnSplitter.Visibility = Visibility.Collapsed;
            EditorConsoleRowSplitter.Visibility = Visibility.Visible;
            EditorConsoleRowSplitterDefinition.Height = new GridLength(6, GridUnitType.Pixel);
            EditorRowDefinition.Height = new GridLength(1, GridUnitType.Star);
            ConsoleRowDefinition.Height = new GridLength(Math.Max(_lastKnownConsoleHeight, MinimumConsoleHeight), GridUnitType.Pixel);

            switch (mode)
            {
                case WorkspaceLayoutMode.EditorMaximized:
                    ConsolePaneBorder.Visibility = Visibility.Collapsed;
                    EditorConsoleRowSplitter.Visibility = Visibility.Collapsed;
                    EditorConsoleRowSplitterDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    ConsoleRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    break;

                case WorkspaceLayoutMode.ConsoleMaximized:
                    EditorPaneBorder.Visibility = Visibility.Collapsed;
                    EditorConsoleRowSplitter.Visibility = Visibility.Collapsed;
                    EditorConsoleRowSplitterDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    EditorRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    ConsoleRowDefinition.Height = new GridLength(1, GridUnitType.Star);
                    Grid.SetRow(ConsolePaneBorder, 0);
                    Grid.SetRowSpan(ConsolePaneBorder, 3);
                    break;

                case WorkspaceLayoutMode.SideBySideSplit:
                    EditorConsoleRowSplitter.Visibility = Visibility.Collapsed;
                    EditorConsoleRowSplitterDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    ConsoleRowDefinition.Height = new GridLength(0, GridUnitType.Pixel);
                    ConsoleSideSplitterColumnDefinition.Width = new GridLength(6, GridUnitType.Pixel);
                    ConsoleSideColumnDefinition.Width = new GridLength(Math.Max(_lastKnownConsoleSideWidth, MinimumConsoleSideWidth), GridUnitType.Pixel);
                    ConsoleSideColumnDefinition.MinWidth = MinimumConsoleSideWidth;
                    EditorConsoleColumnSplitter.Visibility = Visibility.Visible;
                    Grid.SetRow(ConsolePaneBorder, 0);
                    Grid.SetRowSpan(ConsolePaneBorder, 3);
                    Grid.SetColumn(ConsolePaneBorder, 4);
                    ConsolePaneBorder.BorderThickness = new Thickness(1, 0, 0, 0);
                    break;
            }

            ApplyBottomToolWindowPresentationState(source);

            if (ViewModel is not null && !string.Equals(source, "SettingsRestore", StringComparison.Ordinal))
            {
                ViewModel.StatusText = mode switch
                {
                    WorkspaceLayoutMode.EditorMaximized => "Workspace layout: editor maximized",
                    WorkspaceLayoutMode.ConsoleMaximized => "Workspace layout: console maximized",
                    WorkspaceLayoutMode.HorizontalSplit => "Workspace layout: editor and console horizontal split",
                    WorkspaceLayoutMode.SideBySideSplit => "Workspace layout: editor and console side-by-side split",
                    _ => "Workspace layout: default"
                };
            }

            var diagnosticsProperties = new Dictionary<string, object?>
            {
                ["source"] = source,
                ["previousMode"] = previousMode.ToString(),
                ["mode"] = mode.ToString(),
                ["lastKnownConsoleHeight"] = _lastKnownConsoleHeight,
                ["lastKnownConsoleSideWidth"] = _lastKnownConsoleSideWidth,
                ["bottomToolWindowVisible"] = _isBottomToolWindowVisible,
                ["bottomToolWindowFloating"] = _isBottomToolWindowFloating,
                ["lastKnownBottomToolWindowHeight"] = _lastKnownBottomToolWindowHeight
            };

            if (string.Equals(source, "SettingsRestore", StringComparison.Ordinal))
            {
                DeveloperDiagnostics.LogInfo("UI", "Workspace layout restored from settings.", diagnosticsProperties);
            }
            else
            {
                DeveloperDiagnostics.LogUserAction(
                    "UI",
                    "WorkspaceLayoutChanged",
                    "Workspace layout command applied.",
                    diagnosticsProperties);
            }
        }

        private void CaptureWorkspaceLayoutSizes()
        {
            if ((_workspaceLayoutMode is WorkspaceLayoutMode.Default or WorkspaceLayoutMode.HorizontalSplit) &&
                ConsoleRowDefinition.ActualHeight >= MinimumConsoleHeight)
            {
                var consoleHeight = ConsoleRowDefinition.ActualHeight;
                if (BottomToolWindowBorder.Visibility == Visibility.Visible && !_isBottomToolWindowFloating)
                {
                    consoleHeight -= BottomToolWindowSplitterRowDefinition.ActualHeight;
                    consoleHeight -= BottomToolWindowRowDefinition.ActualHeight;
                }

                if (consoleHeight >= MinimumConsoleHeight)
                {
                    _lastKnownConsoleHeight = consoleHeight;
                }
            }

            if (_workspaceLayoutMode == WorkspaceLayoutMode.SideBySideSplit &&
                ConsoleSideColumnDefinition.ActualWidth >= MinimumConsoleSideWidth)
            {
                _lastKnownConsoleSideWidth = ConsoleSideColumnDefinition.ActualWidth;
            }
        }

        private static WorkspaceLayoutMode RestoreWorkspaceLayoutMode(string? persistedMode)
        {
            if (Enum.TryParse<WorkspaceLayoutMode>(persistedMode, ignoreCase: true, out var mode))
            {
                return mode;
            }

            return WorkspaceLayoutMode.HorizontalSplit;
        }

        private void ApplyExplorerVisibilityLayout()
        {
            var isVisible = ViewModel?.IsExplorerVisible ?? true;

            if (isVisible)
            {
                ExplorerColumnDefinition.Width = new GridLength(Math.Max(_lastKnownExplorerWidth, MinimumExplorerWidth), GridUnitType.Pixel);
                ExplorerColumnDefinition.MinWidth = MinimumExplorerWidth;
                ExplorerSplitterColumnDefinition.Width = new GridLength(6, GridUnitType.Pixel);
            }
            else
            {
                if (ExplorerColumnDefinition.ActualWidth >= MinimumExplorerWidth)
                {
                    _lastKnownExplorerWidth = ExplorerColumnDefinition.ActualWidth;
                }

                ExplorerColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
                ExplorerColumnDefinition.MinWidth = 0;
                ExplorerSplitterColumnDefinition.Width = new GridLength(0, GridUnitType.Pixel);
            }

            EditorColumnDefinition.Width = new GridLength(1, GridUnitType.Star);

            DeveloperDiagnostics.LogInfo(
                "UI",
                "Explorer side pane layout applied.",
                new Dictionary<string, object?>
                {
                    ["isVisible"] = isVisible,
                    ["columnWidth"] = ExplorerColumnDefinition.Width.Value,
                    ["columnMinWidth"] = ExplorerColumnDefinition.MinWidth,
                    ["splitterColumnWidth"] = ExplorerSplitterColumnDefinition.Width.Value,
                    ["lastKnownWidth"] = _lastKnownExplorerWidth
                });
        }

        private void SaveApplicationSettings()
        {
            var settings = ViewModel?.CreateApplicationSettingsSnapshot() ?? new ApplicationSettings();
            var restoreBounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            CaptureDebugPaneWindowBounds();
            CaptureBottomToolWindowBounds();
            CaptureWorkspaceLayoutSizes();
            CaptureDockedBottomToolWindowHeight();

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
            settings.UiScalePercent = _uiScaleService.CurrentPercentage;
            settings.IsContextHelpEnabled = IsContextHelpEnabled;
            CopyDeveloperDiagnosticsSettings(_loadedSettings, settings);

            if (ViewModel?.IsExplorerVisible == true && ExplorerColumnDefinition.ActualWidth >= MinimumExplorerWidth)
            {
                _lastKnownExplorerWidth = ExplorerColumnDefinition.ActualWidth;
            }

            settings.ExplorerWidth = _lastKnownExplorerWidth;

            settings.ConsoleHeight = _lastKnownConsoleHeight;
            settings.ConsoleSideWidth = _lastKnownConsoleSideWidth;
            settings.WorkspaceLayoutMode = _workspaceLayoutMode.ToString();
            settings.IsBottomToolWindowVisible = _isBottomToolWindowVisible;
            settings.IsBottomToolWindowFloating = _isBottomToolWindowFloating;
            settings.SelectedBottomToolTab = _selectedBottomToolTab.ToString();
            settings.DockedBottomToolWindowHeight = _lastKnownBottomToolWindowHeight;
            settings.IsDebugPanelVisible = DebugPanelBorder.Visibility == Visibility.Visible;
            settings.DockedDebugPanelWidth = _lastKnownDebugPanelWidth;

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

            if (_lastBottomToolWindowBounds is Rect bottomToolWindowBounds)
            {
                settings.BottomToolWindowWidth = bottomToolWindowBounds.Width;
                settings.BottomToolWindowHeight = bottomToolWindowBounds.Height;
                settings.BottomToolWindowLeft = bottomToolWindowBounds.Left;
                settings.BottomToolWindowTop = bottomToolWindowBounds.Top;

                DeveloperDiagnostics.LogInfo(
                    "UI",
                    "Bottom tool window size and position saved.",
                    new Dictionary<string, object?>
                    {
                        ["left"] = bottomToolWindowBounds.Left,
                        ["top"] = bottomToolWindowBounds.Top,
                        ["width"] = bottomToolWindowBounds.Width,
                        ["height"] = bottomToolWindowBounds.Height
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
            if (!ShowIdeConfirmation("Clear Developer Diagnostics Logs",
                "Delete all files under the Developer Debugging folder? This does not delete normal app logs.",
                "Delete", "Keep"))
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
                    ShowIdeMessage("Support Logs ZIP Created",
                        $"A support logs ZIP was created and its full path was copied to the clipboard. Send this ZIP for support.\n\n{packagePath}");
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

                ShowIdeMessage("Support Logs ZIP Failed", $"PS7 ScriptDesk could not create the support logs ZIP.\n\n{ex.Message}");
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
        /// Queues live syntax diagnostics immediately and schedules the heavier
        /// authoring pass after the editor has been idle. Syntax and authoring now
        /// use separate background paths. The live syntax path parses in process,
        /// so command metadata work and hidden-pwsh transport cannot block the
        /// fast parser feedback loop.
        /// </summary>
        private void ScheduleDiagnostics(TextEditor editorTextEditor)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
            {
                CancelPendingDiagnostics(editorTextEditor);
                DisposeLiveSyntaxPump(editorTextEditor);
                return;
            }

            if (!_tabByEditor.TryGetValue(editorTextEditor, out var currentTab) || !ReferenceEquals(currentTab, tab))
            {
                return;
            }

            var registrationVersion = _editorRegistrationVersions.TryGetValue(editorTextEditor, out var editorRegistrationVersion)
                ? editorRegistrationVersion
                : IncrementEditorRegistrationVersion(editorTextEditor);

            var pwshPath = ViewModel?.EffectiveRuntimeExecutablePath;
            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);

            if (string.IsNullOrWhiteSpace(pwshPath))
            {
                CancelPendingDiagnostics(editorTextEditor);
                DisposeLiveSyntaxPump(editorTextEditor);
                ClearDiagnosticLayers(editorTextEditor);
                var parserTokensChanged = ClearParserTokensForEditor(editorTextEditor);
                _ = tab.SetSyntaxDiagnosticsStatus("No PowerShell runtime is available for syntax checking", clearErrors: true);
                var diagnosticsVisualsChanged = ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);
                if (parserTokensChanged && !diagnosticsVisualsChanged)
                {
                    editorTextEditor.TextArea.TextView.Redraw();
                }

                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab) &&
                    !string.Equals(ViewModel.StatusText, "No PowerShell runtime is available for syntax checking", StringComparison.Ordinal))
                {
                    ViewModel.StatusText = "No PowerShell runtime is available for syntax checking";
                }

                return;
            }

            var scriptSnapshot = editorTextEditor.Text ?? string.Empty;
            var lineCount = editorTextEditor.Document?.LineCount ?? CountLines(scriptSnapshot);

            if (string.Equals(tab.SyntaxDiagnosticsStatusText, "Syntax checking is waiting for a PowerShell runtime", StringComparison.Ordinal))
            {
                tab.SetSyntaxDiagnosticsStatus("Syntax checking…");
            }

            QueueLiveSyntaxDiagnostics(
                editorTextEditor,
                tab,
                pwshPath,
                registrationVersion,
                scriptSnapshot,
                lineCount);

            ScheduleAuthoringDiagnostics(
                editorTextEditor,
                tab,
                errorRenderer,
                pwshPath,
                registrationVersion,
                scriptSnapshot,
                lineCount);
        }

        private void QueueLiveSyntaxDiagnostics(
            TextEditor editorTextEditor,
            EditorTabViewModel tab,
            string pwshPath,
            int registrationVersion,
            string scriptSnapshot,
            int lineCount)
        {
            var requestVersion = IncrementLiveSyntaxRequestVersion(editorTextEditor);
            var state = GetOrCreateLiveSyntaxPumpState(editorTextEditor);
            var workItem = new LiveSyntaxWorkItem(
                scriptSnapshot,
                pwshPath,
                registrationVersion,
                requestVersion,
                lineCount,
                tab.Title,
                tab.FilePath);

            state.Publish(workItem);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Live syntax diagnostics queued latest editor snapshot.",
                    new Dictionary<string, object?>
                    {
                        ["documentTitle"] = tab.Title,
                        ["filePath"] = tab.FilePath,
                        ["requestVersion"] = requestVersion,
                        ["textLength"] = scriptSnapshot.Length,
                        ["lineCount"] = lineCount
                    });
            }
        }

        private LiveSyntaxPumpState GetOrCreateLiveSyntaxPumpState(TextEditor editorTextEditor)
        {
            if (_liveSyntaxPumpStates.TryGetValue(editorTextEditor, out var state) && !state.IsDisposed)
            {
                return state;
            }

            state = new LiveSyntaxPumpState();
            _liveSyntaxPumpStates[editorTextEditor] = state;
            state.WorkerTask = Task.Run(() => RunLiveSyntaxPumpAsync(editorTextEditor, state), state.CancellationToken);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Live syntax diagnostics pump started for editor.",
                    new Dictionary<string, object?>
                    {
                        ["editorHashCode"] = editorTextEditor.GetHashCode()
                    });
            }

            return state;
        }

        private async Task RunLiveSyntaxPumpAsync(TextEditor editorTextEditor, LiveSyntaxPumpState state)
        {
            var token = state.CancellationToken;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    await state.WaitForSignalAsync(token).ConfigureAwait(false);

                    var queuedWorkItem = state.LatestWorkItem;
                    if (queuedWorkItem is null)
                    {
                        continue;
                    }

                    var quietDelay = GetLiveSyntaxQuietDelay(queuedWorkItem);
                    if (quietDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(quietDelay, token).ConfigureAwait(false);
                    }

                    state.DrainSignals();

                    var workItem = state.LatestWorkItem;
                    if (workItem is null)
                    {
                        continue;
                    }

                    var minimumIntervalDelay = GetLiveSyntaxMinimumIntervalDelay(state, workItem);
                    if (minimumIntervalDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(minimumIntervalDelay, token).ConfigureAwait(false);
                        state.DrainSignals();

                        workItem = state.LatestWorkItem;
                        if (workItem is null)
                        {
                            continue;
                        }
                    }

                    state.LastParseStartedUtc = DateTimeOffset.UtcNow;
                    var stopwatch = Stopwatch.StartNew();
                    var syntaxParseResult = await _liveSyntaxDiagnosticsService
                        .ParseAsync(workItem.ScriptSnapshot, workItem.PwshPath, PowerShellDiagnosticsMode.SyntaxOnly, token)
                        .ConfigureAwait(false);
                    stopwatch.Stop();

                    await Dispatcher.InvokeAsync(() =>
                    {
                        ApplyLiveSyntaxDiagnosticsResult(editorTextEditor, state, workItem, syntaxParseResult, stopwatch.ElapsedMilliseconds);
                    }, DispatcherPriority.Background, token);
                }
            }
            catch (OperationCanceledException)
            {
                // The editor was closed or the app is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // The editor was closed or the app is shutting down.
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("Editor", ex, "Live syntax diagnostics pump failed unexpectedly.");
            }
        }

        private void ApplyLiveSyntaxDiagnosticsResult(
            TextEditor editorTextEditor,
            LiveSyntaxPumpState state,
            LiveSyntaxWorkItem workItem,
            DiagnosticsParseResult syntaxParseResult,
            long elapsedMilliseconds)
        {
            if (!IsLiveSyntaxRequestCurrent(editorTextEditor, state, workItem))
            {
                if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                {
                    DeveloperDiagnostics.LogDebug(
                        "Editor",
                        "Discarded stale live syntax diagnostics result.",
                        new Dictionary<string, object?>
                        {
                            ["documentTitle"] = workItem.DocumentTitle,
                            ["filePath"] = workItem.FilePath,
                            ["requestVersion"] = workItem.RequestVersion,
                            ["elapsedMilliseconds"] = elapsedMilliseconds
                        });
                }

                return;
            }

            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
            {
                return;
            }

            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);

            if (!syntaxParseResult.Succeeded)
            {
                ApplyDiagnosticsFailure(
                    editorTextEditor,
                    tab,
                    errorRenderer,
                    syntaxParseResult.FailureMessage ?? "Syntax checking failed.",
                    clearExistingDiagnostics: false);
                return;
            }

            ApplyDiagnosticsResult(
                editorTextEditor,
                workItem.ScriptSnapshot,
                syntaxParseResult,
                includeAuthoringDiagnostics: false,
                successStatusText: "Syntax: OK");

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Live syntax diagnostics applied.",
                    new Dictionary<string, object?>
                    {
                        ["documentTitle"] = workItem.DocumentTitle,
                        ["filePath"] = workItem.FilePath,
                        ["requestVersion"] = workItem.RequestVersion,
                        ["elapsedMilliseconds"] = elapsedMilliseconds,
                        ["textLength"] = workItem.ScriptSnapshot.Length,
                        ["lineCount"] = workItem.LineCount,
                        ["syntaxErrorCount"] = syntaxParseResult.Errors.Count,
                        ["syntaxTokenCount"] = syntaxParseResult.SyntaxTokens.Count
                    });
            }
        }

        private void ScheduleAuthoringDiagnostics(
            TextEditor editorTextEditor,
            EditorTabViewModel tab,
            ErrorMarkerRenderer errorRenderer,
            string pwshPath,
            int registrationVersion,
            string scriptSnapshot,
            int lineCount)
        {
            if (ShouldSkipAuthoringDiagnostics(scriptSnapshot, lineCount))
            {
                if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                {
                    DeveloperDiagnostics.LogDebug(
                        "Editor",
                        "Full authoring diagnostics skipped for very large document; live syntax diagnostics remain active.",
                        new Dictionary<string, object?>
                        {
                            ["documentTitle"] = tab.Title,
                            ["filePath"] = tab.FilePath,
                            ["textLength"] = scriptSnapshot.Length,
                            ["lineCount"] = lineCount
                        });
                }

                return;
            }

            var requestVersion = IncrementDiagnosticsRequestVersion(editorTextEditor);
            var state = GetOrCreateAuthoringDiagnosticsPumpState(editorTextEditor);
            var workItem = new AuthoringDiagnosticsWorkItem(
                scriptSnapshot,
                pwshPath,
                registrationVersion,
                requestVersion,
                lineCount,
                tab.Title,
                tab.FilePath);

            state.Publish(workItem);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Full authoring diagnostics queued latest editor snapshot.",
                    new Dictionary<string, object?>
                    {
                        ["documentTitle"] = tab.Title,
                        ["filePath"] = tab.FilePath,
                        ["requestVersion"] = requestVersion,
                        ["delayMilliseconds"] = GetAuthoringDiagnosticsDelay(scriptSnapshot, lineCount).TotalMilliseconds,
                        ["textLength"] = scriptSnapshot.Length,
                        ["lineCount"] = lineCount
                    });
            }
        }

        private AuthoringDiagnosticsPumpState GetOrCreateAuthoringDiagnosticsPumpState(TextEditor editorTextEditor)
        {
            if (_authoringDiagnosticsPumpStates.TryGetValue(editorTextEditor, out var state) && !state.IsDisposed)
            {
                return state;
            }

            state = new AuthoringDiagnosticsPumpState();
            _authoringDiagnosticsPumpStates[editorTextEditor] = state;
            state.WorkerTask = Task.Run(() => RunAuthoringDiagnosticsPumpAsync(editorTextEditor, state), state.CancellationToken);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    "Full authoring diagnostics pump started for editor.",
                    new Dictionary<string, object?>
                    {
                        ["editorHashCode"] = editorTextEditor.GetHashCode()
                    });
            }

            return state;
        }

        private async Task RunAuthoringDiagnosticsPumpAsync(TextEditor editorTextEditor, AuthoringDiagnosticsPumpState state)
        {
            var pumpToken = state.CancellationToken;

            try
            {
                while (!pumpToken.IsCancellationRequested)
                {
                    await state.WaitForSignalAsync(pumpToken).ConfigureAwait(false);

                    var workItem = state.LatestWorkItem;
                    if (workItem is null)
                    {
                        continue;
                    }

                    using var workCancellationSource = CancellationTokenSource.CreateLinkedTokenSource(pumpToken);
                    state.SetActiveWorkCancellationSource(workCancellationSource);
                    var workToken = workCancellationSource.Token;

                    try
                    {
                        var authoringDelay = GetAuthoringDiagnosticsDelay(workItem.ScriptSnapshot, workItem.LineCount);
                        await Task.Delay(authoringDelay, workToken).ConfigureAwait(false);

                        state.DrainSignals();

                        workItem = state.LatestWorkItem;
                        if (workItem is null)
                        {
                            continue;
                        }

                        var shouldRunFullAuthoringPass = await Dispatcher.InvokeAsync(() =>
                        {
                            return IsAuthoringDiagnosticsRequestCurrent(editorTextEditor, state, workItem) &&
                                editorTextEditor.DataContext is EditorTabViewModel tab &&
                                !tab.SyntaxDiagnosticSpans.Any(static diagnostic => diagnostic.IsError);
                        }, DispatcherPriority.Background, workToken);

                        if (!shouldRunFullAuthoringPass)
                        {
                            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                            {
                                DeveloperDiagnostics.LogDebug(
                                    "Editor",
                                    "Full authoring diagnostics skipped because the request was stale or live syntax currently has parser errors.",
                                    new Dictionary<string, object?>
                                    {
                                        ["documentTitle"] = workItem.DocumentTitle,
                                        ["filePath"] = workItem.FilePath,
                                        ["requestVersion"] = workItem.RequestVersion,
                                        ["delayMilliseconds"] = authoringDelay.TotalMilliseconds,
                                        ["textLength"] = workItem.ScriptSnapshot.Length,
                                        ["lineCount"] = workItem.LineCount
                                    });
                            }

                            continue;
                        }

                        var authoringStopwatch = Stopwatch.StartNew();
                        var fullParseResult = await _diagnosticsService
                            .ParseAsync(workItem.ScriptSnapshot, workItem.PwshPath, PowerShellDiagnosticsMode.FullAuthoring, workToken)
                            .ConfigureAwait(false);
                        authoringStopwatch.Stop();

                        await Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsAuthoringDiagnosticsRequestCurrent(editorTextEditor, state, workItem))
                            {
                                return;
                            }

                            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
                            {
                                return;
                            }

                            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);

                            if (!fullParseResult.Succeeded)
                            {
                                ApplyDiagnosticsFailure(
                                    editorTextEditor,
                                    tab,
                                    errorRenderer,
                                    fullParseResult.FailureMessage ?? "Authoring diagnostics failed.",
                                    clearExistingDiagnostics: false);
                                return;
                            }

                            if (fullParseResult.Errors.Any(static diagnostic => diagnostic.IsError))
                            {
                                // The in-process live syntax pump owns immediate parser feedback.
                                // If the slower pwsh-backed pass sees parser errors, apply those
                                // errors without touching syntax color tokens or adding authoring warnings.
                                ApplyDiagnosticsResult(
                                    editorTextEditor,
                                    workItem.ScriptSnapshot,
                                    fullParseResult,
                                    includeAuthoringDiagnostics: false,
                                    successStatusText: "Syntax issues detected",
                                    applyParserTokens: false);
                            }
                            else
                            {
                                ApplyAuthoringDiagnosticsResult(
                                    editorTextEditor,
                                    workItem.ScriptSnapshot,
                                    fullParseResult,
                                    successStatusText: "Diagnostics: OK");
                            }

                            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
                            {
                                DeveloperDiagnostics.LogDebug(
                                    "Editor",
                                    "Full authoring diagnostics applied.",
                                    new Dictionary<string, object?>
                                    {
                                        ["documentTitle"] = workItem.DocumentTitle,
                                        ["filePath"] = workItem.FilePath,
                                        ["requestVersion"] = workItem.RequestVersion,
                                        ["delayMilliseconds"] = authoringDelay.TotalMilliseconds,
                                        ["elapsedMilliseconds"] = authoringStopwatch.ElapsedMilliseconds,
                                        ["textLength"] = workItem.ScriptSnapshot.Length,
                                        ["lineCount"] = workItem.LineCount,
                                        ["syntaxErrorCount"] = fullParseResult.Errors.Count,
                                        ["syntaxTokenCount"] = fullParseResult.SyntaxTokens.Count,
                                        ["functionFactCount"] = fullParseResult.AuthoringFacts?.Functions.Count ?? 0,
                                        ["commandFactCount"] = fullParseResult.AuthoringFacts?.Commands.Count ?? 0,
                                        ["commandMetadataCount"] = fullParseResult.AuthoringFacts?.CommandMetadata.Count ?? 0,
                                        ["variableFactCount"] = fullParseResult.AuthoringFacts?.Variables.Count ?? 0
                                    });
                            }
                        }, DispatcherPriority.Background, workToken);
                    }
                    catch (OperationCanceledException) when (!pumpToken.IsCancellationRequested)
                    {
                        // Superseded by a newer edit. The latest-work-item storage and signal will drive the next pass.
                    }
                    catch (Exception ex) when (!pumpToken.IsCancellationRequested)
                    {
                        var failureWorkItem = workItem;
                        if (failureWorkItem is null)
                        {
                            return;
                        }

                        _ = Dispatcher.InvokeAsync(() =>
                        {
                            if (!IsAuthoringDiagnosticsRequestCurrent(editorTextEditor, state, failureWorkItem))
                            {
                                return;
                            }

                            if (editorTextEditor.DataContext is not EditorTabViewModel tab)
                            {
                                return;
                            }

                            var errorRenderer = EnsureErrorRendererAttached(editorTextEditor);
                            ApplyDiagnosticsFailure(editorTextEditor, tab, errorRenderer, $"Authoring diagnostics failed: {ex.Message}", clearExistingDiagnostics: false);
                        });
                    }
                    finally
                    {
                        state.ClearActiveWorkCancellationSource(workCancellationSource);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // The editor was closed or the app is shutting down.
            }
            catch (ObjectDisposedException)
            {
                // The editor was closed or the app is shutting down.
            }
            catch (Exception ex)
            {
                DeveloperDiagnostics.LogException("Editor", ex, "Full authoring diagnostics pump failed unexpectedly.");
            }
        }

        private static TimeSpan GetAuthoringDiagnosticsDelay(string scriptSnapshot, int lineCount)
        {
            if (scriptSnapshot.Length >= AuthoringDiagnosticsLargeCharacterThreshold ||
                lineCount >= AuthoringDiagnosticsLargeLineThreshold)
            {
                return TimeSpan.FromMilliseconds(AuthoringDiagnosticsLargeDocumentDelayMilliseconds);
            }

            if (scriptSnapshot.Length >= AuthoringDiagnosticsMediumCharacterThreshold ||
                lineCount >= AuthoringDiagnosticsMediumLineThreshold)
            {
                return TimeSpan.FromMilliseconds(AuthoringDiagnosticsMediumDocumentDelayMilliseconds);
            }

            return TimeSpan.FromMilliseconds(AuthoringDiagnosticsSmallDocumentDelayMilliseconds);
        }

        private static bool ShouldSkipAuthoringDiagnostics(string scriptSnapshot, int lineCount)
        {
            return scriptSnapshot.Length >= AuthoringDiagnosticsVeryLargeCharacterThreshold ||
                lineCount >= AuthoringDiagnosticsVeryLargeLineThreshold;
        }

        private bool IsLiveSyntaxRequestCurrent(
            TextEditor editorTextEditor,
            LiveSyntaxPumpState requestState,
            LiveSyntaxWorkItem workItem)
        {
            return _errorRenderers.ContainsKey(editorTextEditor)
                && _tabByEditor.TryGetValue(editorTextEditor, out var currentTab)
                && editorTextEditor.DataContext is EditorTabViewModel tab
                && ReferenceEquals(currentTab, tab)
                && _liveSyntaxPumpStates.TryGetValue(editorTextEditor, out var currentState)
                && ReferenceEquals(currentState, requestState)
                && _editorRegistrationVersions.TryGetValue(editorTextEditor, out var currentRegistrationVersion)
                && currentRegistrationVersion == workItem.RegistrationVersion
                && _liveSyntaxRequestVersions.TryGetValue(editorTextEditor, out var currentRequestVersion)
                && currentRequestVersion == workItem.RequestVersion
                && string.Equals(editorTextEditor.Text ?? string.Empty, workItem.ScriptSnapshot, StringComparison.Ordinal);
        }

        private static TimeSpan GetLiveSyntaxQuietDelay(LiveSyntaxWorkItem workItem)
        {
            return IsLargeLiveSyntaxDocument(workItem)
                ? TimeSpan.FromMilliseconds(LiveSyntaxDiagnosticsLargeFileQuietDelayMilliseconds)
                : TimeSpan.FromMilliseconds(LiveSyntaxDiagnosticsQuietDelayMilliseconds);
        }

        private static TimeSpan GetLiveSyntaxMinimumIntervalDelay(LiveSyntaxPumpState state, LiveSyntaxWorkItem workItem)
        {
            var minimumInterval = IsLargeLiveSyntaxDocument(workItem)
                ? TimeSpan.FromMilliseconds(LiveSyntaxDiagnosticsLargeFileMinimumIntervalMilliseconds)
                : TimeSpan.FromMilliseconds(LiveSyntaxDiagnosticsMinimumIntervalMilliseconds);

            if (state.LastParseStartedUtc == DateTimeOffset.MinValue)
            {
                return TimeSpan.Zero;
            }

            var elapsedSinceLastParse = DateTimeOffset.UtcNow - state.LastParseStartedUtc;
            return elapsedSinceLastParse >= minimumInterval
                ? TimeSpan.Zero
                : minimumInterval - elapsedSinceLastParse;
        }

        private static bool IsLargeLiveSyntaxDocument(LiveSyntaxWorkItem workItem)
        {
            return workItem.ScriptSnapshot.Length >= LiveSyntaxDiagnosticsLargeFileCharacterThreshold ||
                workItem.LineCount >= LiveSyntaxDiagnosticsLargeFileLineThreshold;
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 1;
            }

            var count = 1;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    count++;
                }
            }

            return count;
        }

        private bool IsAuthoringDiagnosticsRequestCurrent(
            TextEditor editorTextEditor,
            AuthoringDiagnosticsPumpState requestState,
            AuthoringDiagnosticsWorkItem workItem)
        {
            return _errorRenderers.ContainsKey(editorTextEditor)
                && _tabByEditor.TryGetValue(editorTextEditor, out var currentTab)
                && editorTextEditor.DataContext is EditorTabViewModel tab
                && ReferenceEquals(currentTab, tab)
                && _authoringDiagnosticsPumpStates.TryGetValue(editorTextEditor, out var currentState)
                && ReferenceEquals(currentState, requestState)
                && _editorRegistrationVersions.TryGetValue(editorTextEditor, out var currentRegistrationVersion)
                && currentRegistrationVersion == workItem.RegistrationVersion
                && _diagnosticsRequestVersions.TryGetValue(editorTextEditor, out var currentRequestVersion)
                && currentRequestVersion == workItem.RequestVersion
                && string.Equals(editorTextEditor.Text ?? string.Empty, workItem.ScriptSnapshot, StringComparison.Ordinal);
        }

        private void RescheduleDiagnosticsForAllEditors()
        {
            foreach (var editor in _editorByTab.Values.ToList())
            {
                ScheduleDiagnostics(editor);
            }
        }

        private bool ApplyParserTokensToEditor(TextEditor editorTextEditor, IReadOnlyList<SyntaxTokenInfo> syntaxTokens)
        {
            return _syntaxColorizers.TryGetValue(editorTextEditor, out var colorizer) &&
                colorizer.SetParserTokens(syntaxTokens);
        }

        private bool ClearParserTokensForEditor(TextEditor editorTextEditor)
        {
            return _syntaxColorizers.TryGetValue(editorTextEditor, out var colorizer) &&
                colorizer.ClearParserTokens();
        }

        private void ApplyDiagnosticsResult(
            TextEditor editorTextEditor,
            string scriptSnapshot,
            DiagnosticsParseResult parseResult,
            bool includeAuthoringDiagnostics,
            string successStatusText,
            bool applyParserTokens = true)
        {
            var parserTokensChanged = applyParserTokens && ApplyParserTokensToEditor(editorTextEditor, parseResult.SyntaxTokens);
            var parserDiagnostics = parseResult.Errors
                .OrderBy(error => error.StartOffset)
                .ToList();

            _liveSyntaxDiagnosticLayers[editorTextEditor] = new DiagnosticLayerSnapshot(scriptSnapshot, parserDiagnostics);

            if (parserDiagnostics.Any(static diagnostic => diagnostic.IsError))
            {
                // Parser errors are authoritative for the current text.  Do not keep
                // stale authoring warnings beside them because those warnings were
                // calculated against a syntactically valid snapshot.
                _authoringDiagnosticLayers.Remove(editorTextEditor);
            }
            else if (includeAuthoringDiagnostics)
            {
                var authoringDiagnostics = PowerShellAuthoringDiagnostics
                    .Analyze(scriptSnapshot, parseResult)
                    .Select(ParseErrorInfo.AsWarning)
                    .OrderBy(error => error.StartOffset)
                    .ToList();
                _authoringDiagnosticLayers[editorTextEditor] = new DiagnosticLayerSnapshot(scriptSnapshot, authoringDiagnostics);
            }
            else
            {
                RemoveStaleAuthoringDiagnostics(editorTextEditor, scriptSnapshot);
            }

            ScheduleLiveAnalyzer(editorTextEditor, scriptSnapshot, parserDiagnostics.Any(static diagnostic => diagnostic.IsError));

            var diagnosticsChanged = ApplyCombinedDiagnosticsToTab(editorTextEditor, scriptSnapshot, successStatusText);
            if (parserTokensChanged && !diagnosticsChanged)
            {
                editorTextEditor.TextArea.TextView.Redraw();
            }
        }

        private void ApplyAuthoringDiagnosticsResult(
            TextEditor editorTextEditor,
            string scriptSnapshot,
            DiagnosticsParseResult parseResult,
            string successStatusText)
        {
            var authoringDiagnostics = PowerShellAuthoringDiagnostics
                .Analyze(scriptSnapshot, parseResult)
                .Select(ParseErrorInfo.AsWarning)
                .OrderBy(error => error.StartOffset)
                .ToList();

            _authoringDiagnosticLayers[editorTextEditor] = new DiagnosticLayerSnapshot(scriptSnapshot, authoringDiagnostics);
            _ = ApplyCombinedDiagnosticsToTab(editorTextEditor, scriptSnapshot, successStatusText);
        }

        private bool ApplyCombinedDiagnosticsToTab(TextEditor editorTextEditor, string scriptSnapshot, string successStatusText)
        {
            var combinedDiagnostics = new List<ParseErrorInfo>();
            var hasCurrentParserErrors = false;

            if (_liveSyntaxDiagnosticLayers.TryGetValue(editorTextEditor, out var syntaxLayer) &&
                syntaxLayer.IsForSnapshot(scriptSnapshot))
            {
                combinedDiagnostics.AddRange(syntaxLayer.Diagnostics);
                hasCurrentParserErrors = syntaxLayer.Diagnostics.Any(static diagnostic => diagnostic.IsError);
            }

            if (!hasCurrentParserErrors &&
                _authoringDiagnosticLayers.TryGetValue(editorTextEditor, out var authoringLayer) &&
                authoringLayer.IsForSnapshot(scriptSnapshot))
            {
                combinedDiagnostics.AddRange(authoringLayer.Diagnostics);
            }

            if (_analyzerDiagnosticLayers.TryGetValue(editorTextEditor, out var analyzerLayer) &&
                analyzerLayer.IsForSnapshot(scriptSnapshot))
            {
                combinedDiagnostics.AddRange(analyzerLayer.Diagnostics);
            }

            var orderedDiagnostics = combinedDiagnostics
                .OrderBy(error => error.StartOffset)
                .ThenBy(error => error.EndOffset)
                .ThenBy(error => error.Severity, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return UpdateSyntaxDiagnosticsForTab(editorTextEditor, orderedDiagnostics, successStatusText);
        }

        private void RemoveStaleAuthoringDiagnostics(TextEditor editorTextEditor, string scriptSnapshot)
        {
            if (_authoringDiagnosticLayers.TryGetValue(editorTextEditor, out var authoringLayer) &&
                !authoringLayer.IsForSnapshot(scriptSnapshot))
            {
                _authoringDiagnosticLayers.Remove(editorTextEditor);
            }
        }

        private void ClearDiagnosticLayers(TextEditor editorTextEditor)
        {
            _liveSyntaxDiagnosticLayers.Remove(editorTextEditor);
            _authoringDiagnosticLayers.Remove(editorTextEditor);
            _analyzerDiagnosticLayers.Remove(editorTextEditor);
        }

        private void ApplyDiagnosticsFailure(
            TextEditor editorTextEditor,
            EditorTabViewModel tab,
            ErrorMarkerRenderer errorRenderer,
            string failureMessage,
            bool clearExistingDiagnostics = true)
        {
            var parserTokensChanged = false;
            if (clearExistingDiagnostics)
            {
                ClearDiagnosticLayers(editorTextEditor);
                parserTokensChanged = ClearParserTokensForEditor(editorTextEditor);
            }

            if (!string.Equals(failureMessage, "Syntax checking was canceled.", StringComparison.Ordinal))
            {
                _ = tab.SetSyntaxDiagnosticsStatus(failureMessage, clearErrors: clearExistingDiagnostics);
                var diagnosticsVisualsChanged = ApplyPersistedSyntaxDiagnosticsToEditor(errorRenderer, tab, editorTextEditor);
                if (parserTokensChanged && !diagnosticsVisualsChanged)
                {
                    editorTextEditor.TextArea.TextView.Redraw();
                }

                if (ViewModel is not null && ReferenceEquals(ViewModel.SelectedTab, tab))
                {
                    ViewModel.StatusText = failureMessage;
                }
            }
        }

        private bool UpdateSyntaxDiagnosticsForTab(TextEditor editorTextEditor, IReadOnlyList<ParseErrorInfo> errors, string? successStatusText = null)
        {
            if (editorTextEditor.DataContext is not EditorTabViewModel tab || editorTextEditor.Document is null)
            {
                return false;
            }

            var document = editorTextEditor.Document;
            var diagnostics = errors
                .Select(error =>
                {
                    var safeOffset = Math.Clamp(error.StartOffset, 0, document.TextLength);
                    var line = document.GetLineByOffset(safeOffset);
                    var lineNumber = line.LineNumber;
                    var columnNumber = Math.Max(1, safeOffset - line.Offset + 1);

                    return new EditorDiagnosticSpanViewModel(lineNumber, columnNumber, error.Message, error.StartOffset, error.EndOffset, error.Severity, error.SourceId, error.RuleId);
                })
                .ToList();

            var okStatusText = string.IsNullOrWhiteSpace(successStatusText)
                ? "Diagnostics: OK"
                : successStatusText;
            var diagnosticsChanged = tab.SetSyntaxDiagnostics(diagnostics, okStatusText);

            if (DeveloperDiagnostics.IsEnabled && DeveloperDiagnostics.IsVerboseEditorEnabled())
            {
                var errorCount = diagnostics.Count(static diagnostic => diagnostic.IsError);
                var warningCount = diagnostics.Count(static diagnostic => diagnostic.IsWarning);
                DeveloperDiagnostics.LogDebug(
                    "Editor",
                    diagnosticsChanged
                        ? "Editor diagnostics applied to active document tab."
                        : "Editor diagnostics unchanged; skipped redundant tab notifications.",
                    new Dictionary<string, object?>
                    {
                        ["documentTitle"] = tab.Title,
                        ["filePath"] = tab.FilePath,
                        ["diagnosticCount"] = diagnostics.Count,
                        ["errorCount"] = errorCount,
                        ["warningCount"] = warningCount,
                        ["statusText"] = okStatusText,
                        ["diagnosticsChanged"] = diagnosticsChanged
                    });
            }

            if (ViewModel is null || !ReferenceEquals(ViewModel.SelectedTab, tab))
            {
                return diagnosticsChanged;
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
                ViewModel.StatusText = okStatusText;
            }

            return diagnosticsChanged;
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
            _activeEditorToolTip.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "Theme.ToolTip.Background");
            _activeEditorToolTip.SetResourceReference(System.Windows.Controls.Control.ForegroundProperty, "Theme.ToolTip.Foreground");
            _activeEditorToolTip.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "Theme.ToolTip.Foreground");
            _activeEditorToolTip.SetResourceReference(System.Windows.Controls.Control.BorderBrushProperty, "Theme.ToolTip.Border");
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

        private const double MinimumSavedDebugPaneWindowWidth = 240;
        private const double MinimumSavedDebugPaneWindowHeight = 180;

        /// <summary>Shows or hides the right-side debug panel column.</summary>
        private void SetDebugPanelVisible(bool visible)
        {
            CaptureDockedDebugPanelWidth();

            if (visible)
            {
                DebugPanelColumn.Width         = new GridLength(Math.Max(_lastKnownDebugPanelWidth, MinimumDebugPanelWidth), GridUnitType.Pixel);
                DebugPanelColumn.MinWidth      = MinimumDebugPanelWidth;
                DebugPanelSplitterColumn.Width = new GridLength(6, GridUnitType.Pixel);
                DebugPanelSplitter.Visibility  = Visibility.Visible;
                DebugPanelBorder.Visibility    = Visibility.Visible;
            }
            else
            {
                if (DebugPanelColumn.ActualWidth >= MinimumDebugPanelWidth)
                {
                    _lastKnownDebugPanelWidth = DebugPanelColumn.ActualWidth;
                }

                DebugPanelColumn.Width         = new GridLength(0, GridUnitType.Pixel);
                DebugPanelColumn.MinWidth      = 0;
                DebugPanelSplitterColumn.Width = new GridLength(0, GridUnitType.Pixel);
                DebugPanelSplitter.Visibility  = Visibility.Collapsed;
                DebugPanelBorder.Visibility    = Visibility.Collapsed;
            }

            ShowDebugPanelMenuItem.IsChecked = visible;
            ApplyDebugPanePresentationState();

            DeveloperDiagnostics.LogInfo(
                "Debugger",
                "Docked Debug pane layout applied.",
                new Dictionary<string, object?>
                {
                    ["isVisible"] = visible,
                    ["columnWidth"] = DebugPanelColumn.Width.Value,
                    ["columnMinWidth"] = DebugPanelColumn.MinWidth,
                    ["splitterColumnWidth"] = DebugPanelSplitterColumn.Width.Value,
                    ["lastKnownDockedWidth"] = _lastKnownDebugPanelWidth,
                    ["isPoppedOut"] = _debugPaneWindow is not null
                });
        }

        private void ShowDebugPanel_Click(object sender, RoutedEventArgs e)
        {
            SetDebugPanelVisible(ShowDebugPanelMenuItem.IsChecked);
        }

        private void CloseDebugPanelButton_Click(object sender, RoutedEventArgs e)
        {
            SetDebugPanelVisible(false);
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
            SetDebugPanelVisible(true);
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

        private void CaptureDockedDebugPanelWidth()
        {
            if (DebugPanelBorder.Visibility == Visibility.Visible &&
                DebugPanelColumn.ActualWidth >= MinimumDebugPanelWidth)
            {
                _lastKnownDebugPanelWidth = DebugPanelColumn.ActualWidth;
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

                    if (!HasActiveDebugCurrentLocation())
                    {
                        var fallbackFrame = callStack.FirstOrDefault(frame => !string.IsNullOrWhiteSpace(frame.ScriptName) && frame.LineNumber > 0);
                        if (fallbackFrame is not null)
                        {
                            TraceDebugShell("RefreshDebugPanelsAsync", $"Applying fallback source location from call stack; reason={reason}; refreshVersion={refreshVersion}; scriptPresent={!string.IsNullOrWhiteSpace(fallbackFrame.ScriptName)}; lineNumber={fallbackFrame.LineNumber}; {DescribeDebugUiState()}");
                            DeveloperDiagnostics.LogDecision(
                                "Debugger",
                                "RefreshDebugPanelsAsync",
                                "Debug source location was recovered from the call stack because no breakpoint location had been applied yet.",
                                "ApplyCallStackFallbackLocation",
                                new Dictionary<string, object?>
                                {
                                    ["reason"] = reason,
                                    ["refreshVersion"] = refreshVersion,
                                    ["scriptName"] = fallbackFrame.ScriptName,
                                    ["lineNumber"] = fallbackFrame.LineNumber
                                });
                            SetDebugCurrentLocation(fallbackFrame.ScriptName, fallbackFrame.LineNumber);
                        }
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

            // Do not require an editor source-location highlight before refreshing the
            // Variables and Call Stack panes. Some PowerShell hosts/versions can pause
            // successfully without emitting a parseable "At <script>:<line>" line before
            // the first UI refresh. In that case the debugger controls and panes should
            // still become usable, and the call stack refresh below can provide a fallback
            // source location.
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

        private sealed class DiagnosticLayerSnapshot
        {
            public DiagnosticLayerSnapshot(string scriptSnapshot, IReadOnlyList<ParseErrorInfo> diagnostics)
            {
                ScriptSnapshot = scriptSnapshot ?? string.Empty;
                Diagnostics = diagnostics?.ToList() ?? new List<ParseErrorInfo>();
            }

            public string ScriptSnapshot { get; }

            public IReadOnlyList<ParseErrorInfo> Diagnostics { get; }

            public bool IsForSnapshot(string scriptSnapshot)
            {
                return string.Equals(ScriptSnapshot, scriptSnapshot ?? string.Empty, StringComparison.Ordinal);
            }
        }

        private sealed class AuthoringDiagnosticsWorkItem
        {
            public AuthoringDiagnosticsWorkItem(
                string scriptSnapshot,
                string pwshPath,
                int registrationVersion,
                int requestVersion,
                int lineCount,
                string documentTitle,
                string? filePath)
            {
                ScriptSnapshot = scriptSnapshot ?? string.Empty;
                PwshPath = pwshPath;
                RegistrationVersion = registrationVersion;
                RequestVersion = requestVersion;
                LineCount = Math.Max(1, lineCount);
                DocumentTitle = documentTitle;
                FilePath = filePath;
            }

            public string ScriptSnapshot { get; }

            public string PwshPath { get; }

            public int RegistrationVersion { get; }

            public int RequestVersion { get; }

            public int LineCount { get; }

            public string DocumentTitle { get; }

            public string? FilePath { get; }
        }

        private sealed class AuthoringDiagnosticsPumpState : IDisposable
        {
            private readonly object _syncRoot = new();
            private int _signalPending;
            private AuthoringDiagnosticsWorkItem? _latestWorkItem;
            private CancellationTokenSource? _activeWorkCancellationSource;

            public AuthoringDiagnosticsPumpState()
            {
                CancellationTokenSource = new CancellationTokenSource();
                Signal = new SemaphoreSlim(0, 1);
            }

            public CancellationTokenSource CancellationTokenSource { get; }

            public CancellationToken CancellationToken => CancellationTokenSource.Token;

            public SemaphoreSlim Signal { get; }

            public Task? WorkerTask { get; set; }

            public bool IsDisposed { get; private set; }

            public AuthoringDiagnosticsWorkItem? LatestWorkItem
            {
                get
                {
                    lock (_syncRoot)
                    {
                        return _latestWorkItem;
                    }
                }
            }

            public void Publish(AuthoringDiagnosticsWorkItem workItem)
            {
                if (IsDisposed)
                {
                    return;
                }

                lock (_syncRoot)
                {
                    _latestWorkItem = workItem;
                    CancelActiveWork_NoLock();
                }

                if (Interlocked.Exchange(ref _signalPending, 1) == 0)
                {
                    try
                    {
                        Signal.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        // A wake signal is already pending; latest-work-item storage is authoritative.
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            public async Task WaitForSignalAsync(CancellationToken cancellationToken)
            {
                await Signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
            }

            public void DrainSignals()
            {
                while (Signal.Wait(0))
                {
                }

                Interlocked.Exchange(ref _signalPending, 0);
            }

            public void SetActiveWorkCancellationSource(CancellationTokenSource cancellationTokenSource)
            {
                lock (_syncRoot)
                {
                    CancelActiveWork_NoLock();
                    _activeWorkCancellationSource = cancellationTokenSource;
                }
            }

            public void ClearActiveWorkCancellationSource(CancellationTokenSource cancellationTokenSource)
            {
                lock (_syncRoot)
                {
                    if (ReferenceEquals(_activeWorkCancellationSource, cancellationTokenSource))
                    {
                        _activeWorkCancellationSource = null;
                    }
                }
            }

            private void CancelActiveWork_NoLock()
            {
                if (_activeWorkCancellationSource is null)
                {
                    return;
                }

                try
                {
                    _activeWorkCancellationSource.Cancel();
                }
                catch
                {
                }
            }

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }

                IsDisposed = true;

                try
                {
                    CancellationTokenSource.Cancel();
                }
                catch
                {
                }

                lock (_syncRoot)
                {
                    CancelActiveWork_NoLock();
                    _activeWorkCancellationSource = null;
                }

                try
                {
                    Signal.Dispose();
                }
                catch
                {
                }

                CancellationTokenSource.Dispose();
            }
        }

        private sealed class LiveSyntaxWorkItem
        {
            public LiveSyntaxWorkItem(
                string scriptSnapshot,
                string pwshPath,
                int registrationVersion,
                int requestVersion,
                int lineCount,
                string documentTitle,
                string? filePath)
            {
                ScriptSnapshot = scriptSnapshot ?? string.Empty;
                PwshPath = pwshPath;
                RegistrationVersion = registrationVersion;
                RequestVersion = requestVersion;
                LineCount = Math.Max(1, lineCount);
                DocumentTitle = documentTitle;
                FilePath = filePath;
            }

            public string ScriptSnapshot { get; }

            public string PwshPath { get; }

            public int RegistrationVersion { get; }

            public int RequestVersion { get; }

            public int LineCount { get; }

            public string DocumentTitle { get; }

            public string? FilePath { get; }
        }

        private sealed class LiveSyntaxPumpState : IDisposable
        {
            private readonly object _syncRoot = new();
            private int _signalPending;

            public LiveSyntaxPumpState()
            {
                CancellationTokenSource = new CancellationTokenSource();
                Signal = new SemaphoreSlim(0, 1);
            }

            public CancellationTokenSource CancellationTokenSource { get; }

            public CancellationToken CancellationToken => CancellationTokenSource.Token;

            public SemaphoreSlim Signal { get; }

            public Task? WorkerTask { get; set; }

            public DateTimeOffset LastParseStartedUtc { get; set; } = DateTimeOffset.MinValue;

            public bool IsDisposed { get; private set; }

            public LiveSyntaxWorkItem? LatestWorkItem
            {
                get
                {
                    lock (_syncRoot)
                    {
                        return _latestWorkItem;
                    }
                }
            }

            private LiveSyntaxWorkItem? _latestWorkItem;

            public void Publish(LiveSyntaxWorkItem workItem)
            {
                if (IsDisposed)
                {
                    return;
                }

                lock (_syncRoot)
                {
                    _latestWorkItem = workItem;
                }

                if (Interlocked.Exchange(ref _signalPending, 1) == 0)
                {
                    try
                    {
                        Signal.Release();
                    }
                    catch (SemaphoreFullException)
                    {
                        // A wake signal is already pending; latest-work-item storage is authoritative.
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
            }

            public async Task WaitForSignalAsync(CancellationToken cancellationToken)
            {
                await Signal.WaitAsync(cancellationToken).ConfigureAwait(false);
                Interlocked.Exchange(ref _signalPending, 0);
            }

            public void DrainSignals()
            {
                while (Signal.Wait(0))
                {
                }

                Interlocked.Exchange(ref _signalPending, 0);
            }

            public void Dispose()
            {
                if (IsDisposed)
                {
                    return;
                }

                IsDisposed = true;

                try
                {
                    CancellationTokenSource.Cancel();
                }
                catch
                {
                }

                try
                {
                    Signal.Dispose();
                }
                catch
                {
                }

                CancellationTokenSource.Dispose();
            }
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

        private void ShowIdeMessage(string title, string message)
        {
            _ = new IdeMessageDialog(this, title, message).ShowDialog();
        }

        private bool ShowIdeConfirmation(string title, string message, string primaryText, string secondaryText)
        {
            var dialog = new IdeMessageDialog(this, title, message, primaryText, secondaryText);
            return dialog.ShowDialog() == true && dialog.PrimaryAccepted;
        }
    }
}
