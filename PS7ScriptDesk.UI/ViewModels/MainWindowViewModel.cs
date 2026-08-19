using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
// System.Windows.Threading removed — DispatcherTimer not available in UI project (no WPF ref).
using PS7ScriptDesk.Application.Utilities;
using PS7ScriptDesk.Application.Diagnostics;
using PS7ScriptDesk.Application.Interfaces;
using PS7ScriptDesk.Domain.Models;
using PS7ScriptDesk.UI.Commands;

namespace PS7ScriptDesk.UI.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private const int MaximumRecentFiles = 10;
        private readonly IFileDocumentService _fileDocumentService;
        private readonly IRuntimeService _runtimeService;
        private readonly ILiveConsoleService _liveConsoleService;
        private readonly IWorkspaceFolderService _workspaceFolderService;
        private readonly IUserPromptService _userPromptService;
        private readonly IExeExportService _exeExportService;
        private SynchronizationContext? _uiSynchronizationContext;
        private readonly SemaphoreSlim _consoleSessionGate = new(1, 1);
        private readonly SemaphoreSlim _consoleRecoveryGate = new(1, 1);
        private readonly TerminalFocusRestorePolicy _terminalFocusRestorePolicy = new();
        private CancellationTokenSource? _runtimeLaunchVerificationCancellationTokenSource;
        private int _runtimeLaunchVerificationGeneration;
        private bool _runtimeLaunchVerificationWarningShown;
        private bool _runtimeReplacementPromptShown;

        private readonly RelayCommand _runCommand;
        private readonly RelayCommand _stopCommand;
        private readonly RelayCommand _refreshRuntimesCommand;
        private readonly RelayCommand _sendConsoleCommand;
        private readonly RelayCommand _restartConsoleCommand;
        private readonly RelayCommand _exportAsExeCommand;
        private readonly RelayCommand _closeAllTabsCommand;
        private readonly RelayCommand _zoomInCommand;
        private readonly RelayCommand _zoomOutCommand;
        private readonly RelayCommand _resetZoomCommand;

        private readonly string _applicationVersionText;

        private string _runtimeText;
        private string _statusText;
        private string _sessionRestoreNoticeText = string.Empty;
        private string _sessionRestoreNoticeToolTip = string.Empty;
        private bool _hasSessionRestoreNotice;
        private string _terminalDisplayText;
        private string _applicationActivityText = string.Empty;
        private string _debuggerOutputText = string.Empty;
        private Action?         _clearTerminalSink;
        private Action?         _focusTerminalSink;
        private Action?         _normalizeTerminalInteractiveStateSink;
        private Func<int, CancellationToken, Task<TerminalFocusRestoreResult>>? _restoreTerminalFocusSink;
        private Func<bool>?     _isTerminalFocusedSink;
        private Func<TerminalFocusRestoreReadiness>? _terminalFocusRestoreReadinessSink;
        private Action<int>?    _beginTerminalOutputGenerationSink;
        private Action<int>?    _invalidateTerminalOutputGenerationSink;
        private TerminalFocusRestoreIntent _preparedTerminalFocusIntent;
        private int _currentTerminalGeneration;
        private int _resetConsoleInProgress;
        private bool _isDebugSessionActive;
        private string _workspaceText;
        private string _workspaceFilterText = string.Empty;
        private string _consoleCommandText = string.Empty;
        private string _consoleSessionText = "ConPTY terminal: not started";
        private string _consolePromptText = "PS >";
        private string? _currentWorkspaceFolderPath;
        private EditorTabViewModel? _selectedTab;
        private RuntimeItemViewModel? _selectedRuntimeItem;
        private RuntimeItemViewModel? _preferredRuntimeItem;
        private readonly PowerShellRuntimeInfo? _startupRuntimeInfo;
        private bool _startupRuntimeSeeded;
        private WorkspaceTreeItemViewModel? _selectedWorkspaceItem;
        private bool _isExplorerVisible = true;
        private bool _isRuntimeDiscoveryInProgress;
        private bool _isWorkspaceLoading;
        private bool _isExecutionRunning;
        private bool _isStopInProgress;
        private bool _isExeExportInProgress;
        private readonly List<string> _recentFilePaths = new();
        private string? _selectedRuntimeExecutablePathToRestore;
        private string? _selectedTabFilePathToRestore;
        private int _untitledCounter = 1;

        // Command history for the console input box (4A).
        private readonly List<string> _commandHistory = new();
        private int _commandHistoryIndex = -1;

        // Execution progress timer (4C).
        private System.Timers.Timer? _progressTimer;
        private DateTime _executionStartTime;
        private string _executionProgressText = string.Empty;

        // Editor zoom level (2B) — font size in points.
        private double _editorZoomLevel = 13.0;

        // Active theme name (5B).
        private string _currentThemeName = "Dark";

        // Editor highlight/selection color preferences.
        private string? _editorSelectionBackgroundHex;
        private string? _editorCurrentLineBackgroundHex;
        private bool _forceHighContrastSelectedText = true;
        private int _workspaceFileCount;
        private int _workspaceFolderCount;
        private int _workspaceReloadGeneration;
        private int _workspaceFilterGeneration;
        private CancellationTokenSource? _workspaceFilterDelayCancellationTokenSource;
        private CancellationTokenSource? _workspaceReloadCancellationTokenSource;
        private IReadOnlyList<WorkspaceItem> _workspaceAllItems = Array.Empty<WorkspaceItem>();
        private ObservableCollection<WorkspaceTreeItemViewModel> _workspaceItems = new();
        private IReadOnlyList<string> _workspaceWarnings = Array.Empty<string>();

        public MainWindowViewModel(
            IWorkspaceService workspaceService,
            IRuntimeService runtimeService,
            IFileDocumentService fileDocumentService,
            IWorkspaceFolderService workspaceFolderService,
            IUserPromptService userPromptService,
            ILiveConsoleService liveConsoleService,
            IExeExportService exeExportService,
            ApplicationSettings? initialSettings = null,
            PowerShellRuntimeInfo? startupRuntimeInfo = null)
        {
            _fileDocumentService = fileDocumentService;
            _runtimeService = runtimeService;
            _workspaceFolderService = workspaceFolderService;
            _userPromptService = userPromptService;
            _liveConsoleService = liveConsoleService;
            _exeExportService = exeExportService;
            _uiSynchronizationContext = SynchronizationContext.Current;
            _startupRuntimeInfo = startupRuntimeInfo;
            _applicationVersionText = GetApplicationVersionText();

            Title = $"{ApplicationBranding.PublicName} {_applicationVersionText}";
            WelcomeMessage = $"{ApplicationBranding.PublicName} shell is running.";
            _runtimeText = "Runtime: Detecting installed PowerShell runtimes...";
            _workspaceText = workspaceService.GetWorkspaceDisplayText();
            _statusText = $"Ready - {_applicationVersionText}";
            _terminalDisplayText =
                $"{ApplicationBranding.PublicName} {_applicationVersionText} terminal pane initialized.{Environment.NewLine}" +
                $"This phase now hosts a ConPTY-backed PowerShell terminal process inside the application.{Environment.NewLine}";

            OpenTabs = new ObservableCollection<EditorTabViewModel>();
            DetectedRuntimes = new ObservableCollection<RuntimeItemViewModel>();

            NewScriptCommand = new RelayCommand(OnNewScript);
            CloseTabCommand = new RelayCommand(OnCloseTab);
            _closeAllTabsCommand = new RelayCommand(OnCloseAllTabs, CanCloseAllTabs);
            CloseAllTabsCommand = _closeAllTabsCommand;
            _runCommand = new RelayCommand(async () => await OnRunAsync(), CanRunScript);
            RunCommand = _runCommand;
            _stopCommand = new RelayCommand(async () => await OnStopAsync(), CanStopScript);
            StopCommand = _stopCommand;
            ClearConsoleCommand = new RelayCommand(async () => await OnClearConsoleAsync());
            _refreshRuntimesCommand = new RelayCommand(async () => await OnRefreshRuntimesAsync(), CanRefreshRuntimes);
            RefreshRuntimesCommand = _refreshRuntimesCommand;
            RefreshWorkspaceCommand = new RelayCommand(async () => await OnRefreshWorkspaceAsync());
            OpenWorkspaceFolderCommand = new RelayCommand(async () => await OnBrowseWorkspaceFolderAsync());
            ShowWorkspaceFolderInExplorerCommand = new RelayCommand(OnShowWorkspaceFolderInExplorer);
            _sendConsoleCommand = new RelayCommand(async () => await OnExecuteConsoleCommandAsync(), CanExecuteConsoleCommand);
            SendConsoleCommand = _sendConsoleCommand;
            _restartConsoleCommand = new RelayCommand(async () => await OnRestartConsoleAsync(), CanRestartConsole);
            RestartConsoleCommand = _restartConsoleCommand;
            _exportAsExeCommand = new RelayCommand(async () => await OnExportAsExeAsync(), CanExportAsExe);
            ExportAsExeCommand = _exportAsExeCommand;

            _zoomInCommand    = new RelayCommand(() => EditorZoomLevel = Math.Min(EditorZoomLevel + 2, 72));
            ZoomInCommand     = _zoomInCommand;
            _zoomOutCommand   = new RelayCommand(() => EditorZoomLevel = Math.Max(EditorZoomLevel - 2, 6));
            ZoomOutCommand    = _zoomOutCommand;
            _resetZoomCommand = new RelayCommand(() => EditorZoomLevel = 13.0);
            ResetZoomCommand  = _resetZoomCommand;

            // Subscribe to live-console completion events so the Run button re-enables
            // when a script finishes executing (1A) and when the session terminates (e.g.
            // the user called 'exit' before the sentinel was echoed).
            _liveConsoleService.CommandExecutionCompleted += OnTerminalCommandCompleted;
            _liveConsoleService.SessionTerminated       += OnSessionTerminated;
            _liveConsoleService.TerminalSessionStarted   += OnTerminalSessionStarted;
            _liveConsoleService.TerminalSessionStopping  += OnTerminalSessionStopping;

            RestorePersistedState(initialSettings);
            if (_startupRuntimeInfo is not null)
            {
                SeedValidatedStartupRuntime(_startupRuntimeInfo);
            }
            else
            {
                TrySeedPersistedRuntimeSelection();
            }

            if (OpenTabs.Count == 0)
            {
                CreateInitialTab();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Title { get; }

        public string WelcomeMessage { get; }

        public string VersionText => $"Version: {_applicationVersionText}";

        public string RuntimeText
        {
            get => _runtimeText;
            set
            {
                if (_runtimeText != value)
                {
                    _runtimeText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string WorkspaceText
        {
            get => _workspaceText;
            set
            {
                if (_workspaceText != value)
                {
                    _workspaceText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string StatusText
        {
            get => _statusText;
            set
            {
                if (_statusText != value)
                {
                    _statusText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SessionRestoreNoticeText
        {
            get => _sessionRestoreNoticeText;
            private set
            {
                if (_sessionRestoreNoticeText != value)
                {
                    _sessionRestoreNoticeText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string SessionRestoreNoticeToolTip
        {
            get => _sessionRestoreNoticeToolTip;
            private set
            {
                if (_sessionRestoreNoticeToolTip != value)
                {
                    _sessionRestoreNoticeToolTip = value;
                    OnPropertyChanged();
                }
            }
        }

        public bool HasSessionRestoreNotice
        {
            get => _hasSessionRestoreNotice;
            private set
            {
                if (_hasSessionRestoreNotice != value)
                {
                    _hasSessionRestoreNotice = value;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>
        /// True while a debug session is active (running or paused at a breakpoint).
        /// Set by the Shell layer via <c>RefreshDebugCommandAvailability</c>.
        /// Used by <see cref="CanRunScript"/> to disable the Run button during debugging.
        /// </summary>
        public bool IsDebugSessionActive
        {
            get => _isDebugSessionActive;
            set
            {
                if (_isDebugSessionActive != value)
                {
                    _isDebugSessionActive = value;
                    OnPropertyChanged();
                    RefreshCommandStates();
                }
            }
        }

        public string TerminalDisplayText
        {
            get => _terminalDisplayText;
            private set
            {
                if (_terminalDisplayText != value)
                {
                    _terminalDisplayText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ApplicationActivityText
        {
            get => _applicationActivityText;
            private set
            {
                if (_applicationActivityText != value)
                {
                    _applicationActivityText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string DebuggerOutputText
        {
            get => _debuggerOutputText;
            private set
            {
                if (_debuggerOutputText != value)
                {
                    _debuggerOutputText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ConsolePromptText => _consolePromptText;

        public string ConsoleCommandText
        {
            get => _consoleCommandText;
            set
            {
                if (_consoleCommandText != value)
                {
                    _consoleCommandText = value;
                    OnPropertyChanged();
                    RefreshCommandStates();
                }
            }
        }

        public string ConsoleSessionText
        {
            get => _consoleSessionText;
            private set
            {
                if (_consoleSessionText != value)
                {
                    _consoleSessionText = value;
                    OnPropertyChanged();
                }
            }
        }

        public string ConsoleHelpText => "Type commands below and press Enter or Execute. This pane is backed by a real pwsh.exe process through Windows ConPTY.";

        public bool IsExplorerVisible
        {
            get => _isExplorerVisible;
            set
            {
                if (_isExplorerVisible != value)
                {
                    _isExplorerVisible = value;
                    OnPropertyChanged();
                    StatusText = value ? "Explorer shown" : "Explorer hidden";
                }
            }
        }

        public bool IsRuntimeDiscoveryInProgress
        {
            get => _isRuntimeDiscoveryInProgress;
            private set
            {
                if (_isRuntimeDiscoveryInProgress != value)
                {
                    _isRuntimeDiscoveryInProgress = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RefreshRuntimesButtonText));
                    OnPropertyChanged(nameof(RuntimeSelectionStatusText));
                    OnPropertyChanged(nameof(IsRuntimeListEnabled));
                    RefreshCommandStates();
                }
            }
        }

        public bool IsWorkspaceLoading
        {
            get => _isWorkspaceLoading;
            private set
            {
                if (_isWorkspaceLoading != value)
                {
                    _isWorkspaceLoading = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RefreshWorkspaceButtonText));
                    OnPropertyChanged(nameof(WorkspaceGroupHeaderText));
                    OnPropertyChanged(nameof(WorkspaceLoadingText));
                    OnPropertyChanged(nameof(IsWorkspaceCommandsEnabled));
                }
            }
        }

        public bool IsExecutionRunning
        {
            get => _isExecutionRunning;
            private set
            {
                if (_isExecutionRunning != value)
                {
                    _isExecutionRunning = value;
                    OnPropertyChanged();
                    RefreshCommandStates();

                    if (value)
                    {
                        StartProgressTimer();
                    }
                    else
                    {
                        StopProgressTimer();
                    }
                }
            }
        }

        private bool IsStopInProgress
        {
            get => _isStopInProgress;
            set
            {
                if (_isStopInProgress != value)
                {
                    _isStopInProgress = value;
                    RefreshCommandStates();
                }
            }
        }

        /// <summary>
        /// Elapsed-time text shown in the status bar while a script is executing.
        /// Empty when no execution is in progress.
        /// </summary>
        public string ExecutionProgressText
        {
            get => _executionProgressText;
            private set
            {
                if (_executionProgressText != value)
                {
                    _executionProgressText = value;
                    OnPropertyChanged();
                }
            }
        }

        // -------------------------------------------------------------------------
        // Editor zoom (2B)
        // -------------------------------------------------------------------------

        /// <summary>Editor font size in points (6–72). Default = 13.</summary>
        public double EditorZoomLevel
        {
            get => _editorZoomLevel;
            set
            {
                var clamped = Math.Clamp(value, 6.0, 72.0);
                if (Math.Abs(_editorZoomLevel - clamped) > 0.01)
                {
                    _editorZoomLevel = clamped;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ZoomLevelText));
                }
            }
        }

        /// <summary>Status bar display text for the current zoom level.</summary>
        public string ZoomLevelText => $"{(int)Math.Round(_editorZoomLevel)} pt";

        // -------------------------------------------------------------------------
        // Theme (5B)
        // -------------------------------------------------------------------------

        /// <summary>Active theme name — "Dark", "Light", or "IseBlue".</summary>
        public string CurrentThemeName
        {
            get => _currentThemeName;
            set
            {
                if (!string.Equals(_currentThemeName, value, StringComparison.Ordinal))
                {
                    _currentThemeName = value;
                    OnPropertyChanged();
                }
            }
        }

        // -------------------------------------------------------------------------
        // Editor highlight/selection colors
        // -------------------------------------------------------------------------

        /// <summary>Optional editor selection background color as #RRGGBB. Null = active theme default.</summary>
        public string? EditorSelectionBackgroundHex
        {
            get => _editorSelectionBackgroundHex;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (!string.Equals(_editorSelectionBackgroundHex, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _editorSelectionBackgroundHex = normalized;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>Optional editor current-line highlight background color as #RRGGBB. Null = active theme default.</summary>
        public string? EditorCurrentLineBackgroundHex
        {
            get => _editorCurrentLineBackgroundHex;
            set
            {
                var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
                if (!string.Equals(_editorCurrentLineBackgroundHex, normalized, StringComparison.OrdinalIgnoreCase))
                {
                    _editorCurrentLineBackgroundHex = normalized;
                    OnPropertyChanged();
                }
            }
        }

        /// <summary>When true, selected editor text uses an automatic black/white foreground for readability.</summary>
        public bool ForceHighContrastSelectedText
        {
            get => _forceHighContrastSelectedText;
            set
            {
                if (_forceHighContrastSelectedText != value)
                {
                    _forceHighContrastSelectedText = value;
                    OnPropertyChanged();
                }
            }
        }

        // -------------------------------------------------------------------------
        // Command history (4A) — public so MainWindow can read for Up/Down navigation
        // -------------------------------------------------------------------------

        public IReadOnlyList<string> CommandHistory => _commandHistory;

        public int CommandHistoryIndex
        {
            get => _commandHistoryIndex;
            set => _commandHistoryIndex = value;
        }

        public string RefreshRuntimesButtonText => IsRuntimeDiscoveryInProgress ? "Refreshing..." : "Refresh Runtimes";

        public string RefreshWorkspaceButtonText => IsWorkspaceLoading ? "Refreshing..." : "Refresh";

        public string WorkspaceGroupHeaderText => IsWorkspaceLoading ? "Workspace (Loading...)" : "Workspace";

        public string WorkspaceLoadingText => IsWorkspaceLoading
            ? (string.IsNullOrWhiteSpace(_workspaceFilterText)
                ? "Loading workspace... large folders or drives can take a few seconds to appear."
                : $"Applying workspace filter '{_workspaceFilterText}'... please wait.")
            : "Tip: very large folders can take a few seconds to appear after Open Folder or Refresh.";

        public string RuntimeSelectionStatusText => IsRuntimeDiscoveryInProgress
            ? "Detecting runtimes... please wait for refresh to complete before changing the selection."
            : "Select the runtime you want to use for Run and Terminal.";

        public bool IsRuntimeListEnabled => !IsRuntimeDiscoveryInProgress && DetectedRuntimes.Count > 0;

        public bool IsWorkspaceCommandsEnabled => !IsWorkspaceLoading;

        public string WorkspaceFilterText
        {
            get => _workspaceFilterText;
            set
            {
                if (_workspaceFilterText != value)
                {
                    _workspaceFilterText = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(WorkspaceLoadingText));

                    if (HasWorkspaceLoaded)
                    {
                        ScheduleWorkspaceFilterRefresh();
                    }
                }
            }
        }

        public ObservableCollection<EditorTabViewModel> OpenTabs { get; }

        public ObservableCollection<RuntimeItemViewModel> DetectedRuntimes { get; }

        public ObservableCollection<WorkspaceTreeItemViewModel> WorkspaceItems
        {
            get => _workspaceItems;
            private set { _workspaceItems = value; OnPropertyChanged(); }
        }

        public EditorTabViewModel? SelectedTab
        {
            get => _selectedTab;
            set
            {
                if (_selectedTab != value)
                {
                    _selectedTab = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ActiveDocumentText));
                    _selectedTabFilePathToRestore = _selectedTab?.FilePath;
                    RefreshCommandStates();
                }
            }
        }

        public RuntimeItemViewModel? SelectedRuntimeItem
        {
            get => _selectedRuntimeItem;
            set
            {
                if (_selectedRuntimeItem != value)
                {
                    _selectedRuntimeItem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(RuntimeDetailsText));
                    OnPropertyChanged(nameof(RuntimePathText));
                    OnPropertyChanged(nameof(SelectedRuntimeCompactText));
                    OnPropertyChanged(nameof(SelectedRuntimePathOnlyText));
                    OnPropertyChanged(nameof(EffectiveRuntimeItem));
                    OnPropertyChanged(nameof(EffectiveRuntimeInfo));
                    OnPropertyChanged(nameof(EffectiveRuntimeExecutablePath));
                    _selectedRuntimeExecutablePathToRestore = _selectedRuntimeItem?.RuntimeInfo.LaunchExecutablePath;
                    RefreshCommandStates();
                }
            }
        }

        public WorkspaceTreeItemViewModel? SelectedWorkspaceItem
        {
            get => _selectedWorkspaceItem;
            set
            {
                if (_selectedWorkspaceItem != value)
                {
                    _selectedWorkspaceItem = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedWorkspacePathText));
                }
            }
        }

        public bool HasWorkspaceLoaded => !string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath);

        public string OpenTabCountText => $"Open Tabs: {OpenTabs.Count}";

        public string ActiveDocumentText =>
            SelectedTab is null
                ? "Active Document: None"
                : $"Active Document: {SelectedTab.Title}";

        public string RuntimeCountText => $"Detected Runtimes: {DetectedRuntimes.Count}";

        public string PreferredRuntimeText =>
            _preferredRuntimeItem is null
                ? "Preferred runtime: none detected"
                : $"Preferred runtime: {_preferredRuntimeItem.DisplayName}";

        public string SelectedRuntimeCompactText =>
            SelectedRuntimeItem is null
                ? "Selected runtime: none"
                : $"Selected runtime: {SelectedRuntimeItem.DisplayText} ({SelectedRuntimeItem.Edition})";

        public string SelectedRuntimePathOnlyText =>
            SelectedRuntimeItem is null
                ? "No executable selected"
                : SelectedRuntimeItem.ExecutablePath;

        public string RuntimeListHeaderText => $"Available runtimes ({DetectedRuntimes.Count})";

        public string RuntimeDetailsText =>
            SelectedRuntimeItem is null
                ? "Runtime details: none"
                : $"Runtime details: {SelectedRuntimeItem.DetailSummary}";

        public string RuntimePathText =>
            SelectedRuntimeItem is null
                ? "Executable path: none"
                : $"Executable path: {SelectedRuntimeItem.ExecutablePath}";

        public RuntimeItemViewModel? EffectiveRuntimeItem => SelectedRuntimeItem ?? _preferredRuntimeItem;

        public PowerShellRuntimeInfo? EffectiveRuntimeInfo => EffectiveRuntimeItem?.RuntimeInfo;

        public string? EffectiveRuntimeExecutablePath => EffectiveRuntimeItem?.RuntimeInfo.LaunchExecutablePath;

        public bool IsRunAvailable => CanRunScript();

        public string WorkspaceFileCountText => $"Workspace Files: {_workspaceFileCount}";

        public string WorkspaceFolderCountText => $"Workspace Folders: {_workspaceFolderCount}";

        public string CurrentWorkspaceText =>
            string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath)
                ? "Current Workspace: None"
                : $"Current Workspace: {_currentWorkspaceFolderPath}";

        public string SelectedWorkspacePathText =>
            SelectedWorkspaceItem is null
                ? "Selected Item: None"
                : $"Selected Item: {SelectedWorkspaceItem.RelativePath}";

        public ICommand NewScriptCommand { get; }

        public ICommand CloseTabCommand { get; }

        public ICommand CloseAllTabsCommand { get; }

        public ICommand RunCommand { get; }

        public ICommand StopCommand { get; }

        public ICommand ClearConsoleCommand { get; }

        public ICommand RefreshRuntimesCommand { get; }

        public ICommand RefreshWorkspaceCommand { get; }

        public ICommand OpenWorkspaceFolderCommand { get; }

        public ICommand ShowWorkspaceFolderInExplorerCommand { get; }

        public ICommand SendConsoleCommand { get; }

        public ICommand RestartConsoleCommand { get; }

        public ICommand ExportAsExeCommand { get; }

        public ICommand ZoomInCommand    { get; }
        public ICommand ZoomOutCommand   { get; }
        public ICommand ResetZoomCommand { get; }

        public void BindToCurrentSynchronizationContext()
        {
            _uiSynchronizationContext ??= SynchronizationContext.Current;
        }

        public async Task InitializeAsync()
        {
            BindToCurrentSynchronizationContext();
            var startupStopwatch = Stopwatch.StartNew();

            try
            {
                StartupTimingLogger.Log("MainWindowViewModel", "Deferred initialization started.");
                Task? runtimeDiscoveryTask = null;
                if (_startupRuntimeSeeded)
                {
                    StartupTimingLogger.Log(
                        "MainWindowViewModel",
                        "Deferred initialization skipped startup runtime discovery because App.OnStartup already validated and seeded the runtime. Use Refresh Runtimes to scan all installed runtimes.");
                }
                else
                {
                    runtimeDiscoveryTask = RefreshRuntimeDiscoveryAsync(logOperation: true, updateStatusText: false);
                }

                if (!string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath) && Directory.Exists(_currentWorkspaceFolderPath))
                {
                    await ReloadWorkspaceItemsAsync(logOperation: false);
                    StartupTimingLogger.Log("MainWindowViewModel", $"Persisted workspace loaded in {startupStopwatch.ElapsedMilliseconds} ms.");
                }

                if (runtimeDiscoveryTask is not null)
                {
                    await runtimeDiscoveryTask.ConfigureAwait(false);
                }

                ScheduleDeferredRuntimeLaunchVerification("deferred startup verification");

                StartupTimingLogger.Log("MainWindowViewModel", $"Deferred initialization completed in {startupStopwatch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Startup", "Deferred ViewModel initialization failed; UI recovery is being applied.", ex);
                DeveloperDiagnostics.LogException("Startup", ex, "Deferred MainWindowViewModel initialization failed.");
                PostToUi(() =>
                {
                    StatusText = "Startup initialization failed";
                    AppendOutputLine($"Startup initialization failed: {ex.Message}");
                    UpdateConsoleSessionPresentation();
                });
                StartupTimingLogger.Log("MainWindowViewModel", $"Deferred initialization failed after {startupStopwatch.ElapsedMilliseconds} ms: {ex}");
            }
        }

        public async Task SubmitConsoleCommandAsync()
        {
            await OnExecuteConsoleCommandAsync().ConfigureAwait(false);
        }

        /// <summary>Sends Ctrl+C (ETX) to the ConPTY terminal to interrupt a running script (4B).</summary>
        public async Task SendInterruptAsync()
        {
            await OnStopAsync().ConfigureAwait(false);
        }

        public async Task RunSelectionAsync(string selectedScriptText)
        {
            if (SelectedTab is null)
            {
                StatusText = "No script tab selected";
                return;
            }

            if (!CanRunScript())
            {
                StatusText = "Run Selection is not available right now";
                return;
            }

            if (string.IsNullOrWhiteSpace(selectedScriptText))
            {
                StatusText = "Select script text to run";
                AppLogger.Info("Console", "Run Selection requested with no selected text.");
                return;
            }

            // Run Selection intentionally executes inside the shared terminal session so
            // selected code can use the same variables, modules, and working directory as
            // previous commands. Keep this as status/log information instead of writing
            // app-host messages into the visible PowerShell terminal.
            AppLogger.Info("Console", $"Run Selection dispatching '{SelectedTab.Title}' into the shared terminal session.");

            // Same flag-management pattern as OnRunAsync (1A).
            IsExecutionRunning = true;
            var dispatched = false;
            try
            {
                dispatched = await DispatchScriptToTerminalAsync($"{SelectedTab.Title} (selection)", selectedScriptText, executeInCurrentScope: true).ConfigureAwait(false);
            }
            finally
            {
                if (!dispatched)
                {
                    PostToUi(() => { IsExecutionRunning = false; RefreshCommandStates(); });
                }
            }
        }

        public Task InitializeTerminalHostAsync(IntPtr hostHandle, int width, int height)
        {
            var stopwatch = Stopwatch.StartNew();
            _liveConsoleService.AttachHost(hostHandle, width, height);
            PostToUi(UpdateConsoleSessionPresentation);
            RefreshCommandStates();
            StartupTimingLogger.Log("MainWindowViewModel", $"Terminal host attached without starting a session in {stopwatch.ElapsedMilliseconds} ms.");
            return Task.CompletedTask;
        }

        public void ResizeTerminalHost(int width, int height)
        {
            _liveConsoleService.ResizeHost(width, height);
        }

        public async Task LoadWorkspaceFolderAsync(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                StatusText = "No workspace folder selected";
                return;
            }

            _currentWorkspaceFolderPath = folderPath;
            _workspaceFilterText = string.Empty;
            OnPropertyChanged(nameof(WorkspaceFilterText));
            IsExplorerVisible = true;
            await ReloadWorkspaceItemsAsync(logOperation: true);
        }

        public void OpenSelectedWorkspaceItem()
        {
            if (SelectedWorkspaceItem is null || SelectedWorkspaceItem.IsPlaceholder)
            {
                StatusText = "No workspace item selected";
                return;
            }

            if (SelectedWorkspaceItem.IsDirectory)
            {
                StatusText = $"Folder selected: {SelectedWorkspaceItem.DisplayName}";
                return;
            }

            OpenFileFromPath(SelectedWorkspaceItem.FullPath);
        }

        public void OpenFileFromPath(string filePath)
        {
            _ = TryOpenFileFromPathCore(filePath, addToRecentFiles: true, logOperation: true, out _);
        }

        public bool TryOpenFileFromPath(string filePath, out string? failureReason)
        {
            return TryOpenFileFromPathCore(filePath, addToRecentFiles: true, logOperation: true, out failureReason);
        }

        public IReadOnlyList<string> GetRecentFilePathsSnapshot()
        {
            return _recentFilePaths.ToList();
        }

        public bool RemoveRecentFilePath(string filePath)
        {
            var normalizedPath = NormalizeStoredPath(filePath);
            if (normalizedPath is null)
            {
                return false;
            }

            return _recentFilePaths.RemoveAll(existingPath => string.Equals(existingPath, normalizedPath, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        public bool SaveSelectedTab()
        {
            if (SelectedTab is null)
            {
                StatusText = "No script tab selected";
                return false;
            }

            if (string.IsNullOrWhiteSpace(SelectedTab.FilePath))
            {
                StatusText = "Use Save As to choose a file name";
                return false;
            }

            return SaveTabCore(SelectedTab);
        }

        public bool SaveSelectedTabAs(string filePath)
        {
            if (SelectedTab is null)
            {
                StatusText = "No script tab selected";
                return false;
            }

            return SaveTabAsCore(SelectedTab, filePath);
        }

        public string GetSuggestedSaveFileName()
        {
            return GetSuggestedSaveFileName(SelectedTab);
        }

        public async Task ExportSelectedTabAsExeAsync()
        {
            await OnExportAsExeAsync();
        }

        /// <summary>
        /// Called after a debug session ends to ensure the ConPTY terminal is running.
        /// Starts the session if it is not already running; does nothing if it is healthy.
        /// </summary>
        public async Task EnsureConsoleRestoredAsync()
        {
            var runtime = EffectiveRuntimeInfo;
            if (runtime is null)
            {
                PostToUi(UpdateConsoleSessionPresentation);
                return;
            }

            await EnsureConsoleSessionAsync(runtime, forceRestart: false, logOperation: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Registers terminal session controls. Visible session output is wired
        /// separately through SubscribeRawOutput so arbitrary strings cannot be
        /// written to xterm.js through this API.
        /// </summary>
        public void SetTerminalSessionControls(
            Action clearTerminal,
            Action? focusTerminal = null,
            Action<int>? beginTerminalOutputGeneration = null,
            Action<int>? invalidateTerminalOutputGeneration = null,
            Func<bool>? isTerminalFocused = null,
            Func<TerminalFocusRestoreReadiness>? terminalFocusRestoreReadiness = null,
            Func<int, CancellationToken, Task<TerminalFocusRestoreResult>>? restoreTerminalFocus = null,
            Action? normalizeTerminalInteractiveState = null)
        {
            _clearTerminalSink  = clearTerminal;
            _focusTerminalSink  = focusTerminal;
            _normalizeTerminalInteractiveStateSink = normalizeTerminalInteractiveState;
            _beginTerminalOutputGenerationSink = beginTerminalOutputGeneration;
            _invalidateTerminalOutputGenerationSink = invalidateTerminalOutputGeneration;
            _isTerminalFocusedSink = isTerminalFocused;
            _terminalFocusRestoreReadinessSink = terminalFocusRestoreReadiness;
            _restoreTerminalFocusSink = restoreTerminalFocus;
        }

        /// <summary>
        /// Captures terminal focus before a Reset Console button click moves focus to the
        /// button. The request is consumed only by the replacement terminal generation.
        /// </summary>
        public void PrepareTerminalFocusRestoreForReset()
        {
            var terminalHadFocus = _isTerminalFocusedSink?.Invoke() == true;
            _preparedTerminalFocusIntent = _terminalFocusRestorePolicy.Capture(
                terminalHadFocus,
                Volatile.Read(ref _currentTerminalGeneration));
            AppLogger.Info(
                "Terminal",
                $"Reset Console focus intent captured. Requested={_preparedTerminalFocusIntent.IsRequested}, PreviousGeneration={_preparedTerminalFocusIntent.PreviousGeneration}.");
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ResetConsoleFocusIntent",
                "Captured pre-reset terminal focus ownership.",
                terminalHadFocus ? "Created" : "NotTerminalFocused",
                new Dictionary<string, object?>
                {
                    ["previousGeneration"] = _preparedTerminalFocusIntent.PreviousGeneration,
                    ["terminalHadFocus"] = terminalHadFocus
                });
        }

        /// <summary>Called by the shell when focus moves outside the terminal during reset.</summary>
        public void NotifyTerminalFocusOwnershipChanged(bool terminalHasFocus)
        {
            if (terminalHasFocus || !_terminalFocusRestorePolicy.Cancel())
            {
                return;
            }

            _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
            AppLogger.Info("Terminal", "Reset Console focus intent canceled because focus moved outside the terminal.");
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ResetConsoleFocusIntent",
                "Canceled pending terminal focus restoration because the user selected another control.",
                "UserFocusMoved");
        }

        /// <summary>Called after xterm/WebView2 renderer readiness is reported by the shell.</summary>
        public void NotifyTerminalRendererReady()
        {
            RequestTerminalFocusAfterReset(Volatile.Read(ref _currentTerminalGeneration), "RendererReady");
        }

        /// <summary>
        /// Subscribes a handler to raw (ANSI-intact) ConPTY output for forwarding
        /// to xterm.js with its session generation. The handler is called on the thread-pool.
        /// </summary>
        public void SubscribeRawOutput(Action<int, string> handler)
        {
            _liveConsoleService.RawOutputReceived += handler;
        }

        /// <summary>Unsubscribes a handler previously added via SubscribeRawOutput.</summary>
        public void UnsubscribeRawOutput(Action<int, string> handler)
        {
            _liveConsoleService.RawOutputReceived -= handler;
        }

        /// <summary>
        /// Writes raw data directly to the ConPTY input pipe (keystroke forwarding
        /// from xterm.js). No sentinel is appended.
        /// </summary>
        public async Task WriteRawInputAsync(string data)
        {
            if (string.IsNullOrEmpty(data))
            {
                return;
            }

            try
            {
                AppLogger.Debug("Console", $"ViewModel forwarding raw terminal input to LiveConsoleService. Length={data.Length}.");
                await _liveConsoleService.WriteRawInputAsync(data).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLogger.Warning(
                    "Console",
                    $"Raw terminal input could not be forwarded. Length={data.Length}, ExceptionType={ex.GetType().Name}, ContentOmitted=True.");
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Raw terminal input forwarding failed.",
                    new Dictionary<string, object?>
                    {
                        ["inputLength"] = data.Length,
                        ["contentOmitted"] = true
                    });
                throw;
            }
        }

        /// <summary>
        /// Resizes the ConPTY pseudo-console using exact character-grid dimensions
        /// reported by xterm.js (cols/rows), bypassing the pixel estimate.
        /// </summary>
        public void ResizeConsole(int cols, int rows)
        {
            _liveConsoleService.ResizeConsole(cols, rows);
        }

        public bool TryPrepareForApplicationClose()
        {
            foreach (var tab in new List<EditorTabViewModel>(OpenTabs))
            {
                if (!TryHandleUnsavedChanges(tab))
                {
                    StatusText = "Application close canceled";
                    return false;
                }
            }

            try
            {
                _runtimeLaunchVerificationCancellationTokenSource?.Cancel();
            }
            catch
            {
                // Best effort shutdown only.
            }

            return true;
        }

        public async Task<bool> ShutdownTerminalAsync(CancellationToken cancellationToken = default)
        {
            CancelPendingTerminalFocusRestore("ApplicationShutdown");
            var operationId = $"TerminalShutdown-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "Terminal",
                "ApplicationShutdown",
                "Awaiting bounded terminal teardown for application shutdown.",
                operationId: operationId);

            try
            {
                var succeeded = await _liveConsoleService
                    .ShutdownAsync(cancellationToken)
                    .ConfigureAwait(false);
                AppLogger.Info(
                    "Console",
                    $"Application terminal shutdown completed. OperationId={operationId}, Succeeded={succeeded}.");
                return succeeded;
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "Console",
                    $"Application terminal shutdown failed. OperationId={operationId}.",
                    ex);
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Application terminal shutdown failed.",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId
                    });
                return false;
            }
        }

        public ApplicationSettings CreateApplicationSettingsSnapshot()
        {
            var reopenFilePaths = new List<string>();
            var reopenDocuments = new List<OpenDocumentState>();

            foreach (var tab in OpenTabs)
            {
                if (!string.IsNullOrWhiteSpace(tab.FilePath) && File.Exists(tab.FilePath))
                {
                    reopenFilePaths.Add(tab.FilePath);
                    reopenDocuments.Add(CreateOpenDocumentState(tab));
                }
            }

            var settings = new ApplicationSettings
            {
                IsExplorerVisible = IsExplorerVisible,
                LastWorkspaceFolderPath = !string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath) && Directory.Exists(_currentWorkspaceFolderPath)
                    ? _currentWorkspaceFolderPath
                    : null,
                SelectedRuntimeExecutablePath = SelectedRuntimeItem?.RuntimeInfo.LaunchExecutablePath ?? _preferredRuntimeItem?.RuntimeInfo.LaunchExecutablePath,
                SelectedTabFilePath = SelectedTab?.FilePath,
                RecentFilePaths = new List<string>(_recentFilePaths),
                ReopenFilePaths = reopenFilePaths,
                ReopenDocuments = reopenDocuments
            };

            TrySetOptionalProperty(settings, "Theme", _currentThemeName);
            TrySetOptionalProperty(settings, "EditorZoomLevel", _editorZoomLevel);
            TrySetOptionalProperty(settings, "EditorSelectionBackgroundHex", _editorSelectionBackgroundHex);
            TrySetOptionalProperty(settings, "EditorCurrentLineBackgroundHex", _editorCurrentLineBackgroundHex);
            TrySetOptionalProperty(settings, "ForceHighContrastSelectedText", _forceHighContrastSelectedText);

            return settings;
        }

        private void RestorePersistedState(ApplicationSettings? settings)
        {
            if (settings is null)
            {
                return;
            }

            IsExplorerVisible = settings.IsExplorerVisible;
            _selectedRuntimeExecutablePathToRestore = NormalizeStoredRuntimePath(settings.SelectedRuntimeExecutablePath);

            var persistedTheme = TryGetOptionalStringProperty(settings, "Theme");
            if (!string.IsNullOrWhiteSpace(persistedTheme))
            {
                _currentThemeName = persistedTheme;
            }

            var persistedEditorZoomLevel = TryGetOptionalNullableDoubleProperty(settings, "EditorZoomLevel");
            if (persistedEditorZoomLevel.HasValue)
            {
                EditorZoomLevel = persistedEditorZoomLevel.Value;
            }

            EditorSelectionBackgroundHex = TryGetOptionalStringProperty(settings, "EditorSelectionBackgroundHex");
            EditorCurrentLineBackgroundHex = TryGetOptionalStringProperty(settings, "EditorCurrentLineBackgroundHex");
            ForceHighContrastSelectedText = TryGetOptionalBoolProperty(settings, "ForceHighContrastSelectedText") ?? true;
            _selectedTabFilePathToRestore = NormalizeStoredPath(settings.SelectedTabFilePath);

            _recentFilePaths.Clear();
            for (var index = settings.RecentFilePaths.Count - 1; index >= 0; index--)
            {
                var normalizedPath = NormalizeStoredPath(settings.RecentFilePaths[index]);
                if (normalizedPath is not null)
                {
                    AddRecentFilePath(normalizedPath);
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.LastWorkspaceFolderPath) && Directory.Exists(settings.LastWorkspaceFolderPath))
            {
                _currentWorkspaceFolderPath = settings.LastWorkspaceFolderPath;
                WorkspaceText = $"Workspace: {_currentWorkspaceFolderPath}";
                OnPropertyChanged(nameof(CurrentWorkspaceText));
                OnPropertyChanged(nameof(SelectedWorkspacePathText));
            }

            var restoreSummary = RestoreReopenDocuments(settings);

            if (!string.IsNullOrWhiteSpace(_selectedTabFilePathToRestore))
            {
                foreach (var openTab in OpenTabs)
                {
                    if (!string.IsNullOrWhiteSpace(openTab.FilePath) &&
                        string.Equals(openTab.FilePath, _selectedTabFilePathToRestore, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedTab = openTab;
                        break;
                    }
                }
            }

            ApplySessionRestoreNotice(restoreSummary);
        }

        private sealed class FileMetadataSnapshot
        {
            public FileMetadataSnapshot(DateTime lastWriteTimeUtc, long length)
            {
                LastWriteTimeUtc = lastWriteTimeUtc;
                Length = length;
            }

            public DateTime LastWriteTimeUtc { get; }

            public long Length { get; }
        }

        private sealed class SessionRestoreSummary
        {
            public int RestoredCount { get; set; }

            public int ChangedCount { get; set; }

            public int MissingCount { get; set; }

            public int FailedCount { get; set; }

            public int UntrackedCount { get; set; }

            public List<string> Details { get; } = new();

            public int AttemptedCount => RestoredCount + MissingCount + FailedCount;
        }

        private SessionRestoreSummary RestoreReopenDocuments(ApplicationSettings settings)
        {
            var summary = new SessionRestoreSummary();

            foreach (var reopenDocument in BuildReopenDocumentStates(settings))
            {
                RestoreReopenDocument(reopenDocument, summary);
            }

            return summary;
        }

        private void RestoreReopenDocument(OpenDocumentState reopenDocument, SessionRestoreSummary summary)
        {
            var normalizedFilePath = NormalizeStoredPath(reopenDocument.FilePath);
            if (normalizedFilePath is null)
            {
                summary.FailedCount++;
                summary.Details.Add("Skipped a saved editor tab because its file path was empty or invalid.");
                return;
            }

            if (!File.Exists(normalizedFilePath))
            {
                summary.MissingCount++;
                summary.Details.Add($"Could not restore missing file: {normalizedFilePath}");
                AppLogger.Warning("Startup", $"Could not restore missing file: {normalizedFilePath}");
                return;
            }

            var currentMetadata = TryGetFileMetadata(normalizedFilePath);
            var hasComparableMetadata = HasComparableRestoreMetadata(reopenDocument);
            var changedSinceLastSession = hasComparableMetadata &&
                                          currentMetadata is not null &&
                                          DoesRestoreMetadataDiffer(reopenDocument, currentMetadata);

            if (TryOpenFileFromPathCore(normalizedFilePath, addToRecentFiles: false, logOperation: false, out var failureReason, out var restoredTab))
            {
                summary.RestoredCount++;

                if (restoredTab is not null && currentMetadata is not null)
                {
                    restoredTab.SetLastKnownFileMetadata(currentMetadata.LastWriteTimeUtc, currentMetadata.Length);
                }

                if (changedSinceLastSession)
                {
                    summary.ChangedCount++;
                    summary.Details.Add($"Reloaded changed file from disk: {normalizedFilePath}");
                    AppLogger.Info("Startup", $"Restored file changed since the last tracked session and was reloaded from disk: {normalizedFilePath}");
                }
                else if (!hasComparableMetadata)
                {
                    summary.UntrackedCount++;
                    summary.Details.Add($"Restored file from disk; previous metadata was not available yet: {normalizedFilePath}");
                }

                return;
            }

            summary.FailedCount++;
            summary.Details.Add($"Could not restore file: {normalizedFilePath}. {failureReason ?? "The file could not be read."}");
            AppLogger.Warning("Startup", $"Could not restore file: {normalizedFilePath}. {failureReason ?? "The file could not be read."}");
        }

        private static List<OpenDocumentState> BuildReopenDocumentStates(ApplicationSettings settings)
        {
            var result = new List<OpenDocumentState>();
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (settings.ReopenDocuments is not null)
            {
                foreach (var reopenDocument in settings.ReopenDocuments)
                {
                    if (reopenDocument is null)
                    {
                        continue;
                    }

                    var normalizedPath = NormalizeStoredPath(reopenDocument.FilePath);
                    if (normalizedPath is null || !seenPaths.Add(normalizedPath))
                    {
                        continue;
                    }

                    result.Add(new OpenDocumentState
                    {
                        FilePath = normalizedPath,
                        LastKnownWriteTimeUtc = reopenDocument.LastKnownWriteTimeUtc,
                        LastKnownLength = reopenDocument.LastKnownLength
                    });
                }
            }

            if (settings.ReopenFilePaths is not null)
            {
                foreach (var reopenFilePath in settings.ReopenFilePaths)
                {
                    var normalizedPath = NormalizeStoredPath(reopenFilePath);
                    if (normalizedPath is null || !seenPaths.Add(normalizedPath))
                    {
                        continue;
                    }

                    result.Add(new OpenDocumentState { FilePath = normalizedPath });
                }
            }

            return result;
        }

        private static OpenDocumentState CreateOpenDocumentState(EditorTabViewModel tab)
        {
            var state = new OpenDocumentState
            {
                FilePath = tab.FilePath ?? string.Empty,
                LastKnownWriteTimeUtc = tab.LastKnownFileWriteTimeUtc,
                LastKnownLength = tab.LastKnownFileLength
            };

            if (!state.LastKnownWriteTimeUtc.HasValue && !state.LastKnownLength.HasValue && !string.IsNullOrWhiteSpace(tab.FilePath))
            {
                var currentMetadata = TryGetFileMetadata(tab.FilePath);
                if (currentMetadata is not null)
                {
                    state.LastKnownWriteTimeUtc = currentMetadata.LastWriteTimeUtc;
                    state.LastKnownLength = currentMetadata.Length;
                }
            }

            return state;
        }

        private static bool HasComparableRestoreMetadata(OpenDocumentState documentState)
        {
            return documentState.LastKnownWriteTimeUtc.HasValue || documentState.LastKnownLength.HasValue;
        }

        private static bool DoesRestoreMetadataDiffer(OpenDocumentState previousState, FileMetadataSnapshot currentMetadata)
        {
            if (previousState.LastKnownWriteTimeUtc.HasValue &&
                previousState.LastKnownWriteTimeUtc.Value != currentMetadata.LastWriteTimeUtc)
            {
                return true;
            }

            if (previousState.LastKnownLength.HasValue &&
                previousState.LastKnownLength.Value != currentMetadata.Length)
            {
                return true;
            }

            return false;
        }

        private static FileMetadataSnapshot? TryGetFileMetadata(string? filePath)
        {
            var normalizedFilePath = NormalizeStoredPath(filePath);
            if (normalizedFilePath is null)
            {
                return null;
            }

            try
            {
                var fileInfo = new FileInfo(normalizedFilePath);
                if (!fileInfo.Exists)
                {
                    return null;
                }

                return new FileMetadataSnapshot(fileInfo.LastWriteTimeUtc, fileInfo.Length);
            }
            catch
            {
                return null;
            }
        }

        private static void ApplyCurrentFileMetadataToTab(EditorTabViewModel tab, string filePath, string knownContent)
        {
            var currentMetadata = TryGetFileMetadata(filePath);
            if (currentMetadata is null)
            {
                tab.ClearLastKnownFileMetadata();
                return;
            }

            tab.SetLastKnownFileState(
                currentMetadata.LastWriteTimeUtc,
                currentMetadata.Length,
                ComputeContentSha256(knownContent));
        }

        private void ApplySessionRestoreNotice(SessionRestoreSummary summary)
        {
            if (summary.AttemptedCount == 0)
            {
                ClearSessionRestoreNotice();
                return;
            }

            var parts = new List<string>();

            if (summary.RestoredCount > 0)
            {
                parts.Add($"{FormatCount(summary.RestoredCount, "file", "files")} restored from disk");
            }

            if (summary.ChangedCount > 0)
            {
                parts.Add($"{FormatCount(summary.ChangedCount, "file", "files")} changed since last session and reloaded");
            }

            if (summary.MissingCount > 0)
            {
                parts.Add($"{FormatCount(summary.MissingCount, "file", "files")} missing");
            }

            if (summary.FailedCount > 0)
            {
                parts.Add($"{FormatCount(summary.FailedCount, "file", "files")} could not be restored");
            }

            var notice = parts.Count == 0
                ? "Session restored from disk"
                : "Session restored: " + string.Join("; ", parts) + ".";

            if (summary.UntrackedCount > 0 && summary.ChangedCount == 0)
            {
                notice += " Change tracking starts now.";
            }

            var toolTipLines = new List<string>
            {
                "PS7 ScriptDesk loads restored editor tabs from the current files on disk."
            };

            if (summary.ChangedCount > 0)
            {
                toolTipLines.Add("One or more restored files changed since the last tracked session, so the current disk version was loaded.");
            }

            if (summary.UntrackedCount > 0)
            {
                toolTipLines.Add("Some restored files were saved by an older settings format, so previous file metadata was not available for comparison.");
            }

            if (summary.Details.Count > 0)
            {
                toolTipLines.Add(string.Empty);
                toolTipLines.AddRange(summary.Details);
            }

            var toolTip = string.Join(Environment.NewLine, toolTipLines);
            SetSessionRestoreNotice(notice, toolTip);
            StatusText = notice;
            AppLogger.Info("Startup", notice);
        }

        private void SetSessionRestoreNotice(string noticeText, string toolTip)
        {
            SessionRestoreNoticeText = noticeText;
            SessionRestoreNoticeToolTip = string.IsNullOrWhiteSpace(toolTip) ? noticeText : toolTip;
            HasSessionRestoreNotice = !string.IsNullOrWhiteSpace(noticeText);
        }

        private void ClearSessionRestoreNotice()
        {
            SessionRestoreNoticeText = string.Empty;
            SessionRestoreNoticeToolTip = string.Empty;
            HasSessionRestoreNotice = false;
        }

        private static string FormatCount(int count, string singular, string plural)
        {
            return count == 1 ? $"1 {singular}" : $"{count} {plural}";
        }

        private void SeedValidatedStartupRuntime(PowerShellRuntimeInfo runtime)
        {
            if (runtime is null)
            {
                return;
            }

            var runtimeItem = new RuntimeItemViewModel(runtime);
            DetectedRuntimes.Clear();
            DetectedRuntimes.Add(runtimeItem);
            _preferredRuntimeItem = runtimeItem;
            SelectedRuntimeItem = runtimeItem;
            _selectedRuntimeExecutablePathToRestore = runtime.LaunchExecutablePath;
            _startupRuntimeSeeded = true;

            RuntimeText = $"Runtime: {runtime.DisplayName}";
            StartupTimingLogger.Log(
                "MainWindowViewModel",
                $"Startup runtime seeded without a duplicate identity probe. DisplayPath='{runtime.ExecutablePath}', LaunchPath='{runtime.LaunchExecutablePath}', Version={runtime.VersionText}.");

            OnPropertyChanged(nameof(RuntimeCountText));
            OnPropertyChanged(nameof(PreferredRuntimeText));
            OnPropertyChanged(nameof(RuntimeListHeaderText));
            OnPropertyChanged(nameof(RuntimeDetailsText));
            OnPropertyChanged(nameof(RuntimePathText));
            OnPropertyChanged(nameof(SelectedRuntimeCompactText));
            OnPropertyChanged(nameof(SelectedRuntimePathOnlyText));
            OnPropertyChanged(nameof(EffectiveRuntimeItem));
            OnPropertyChanged(nameof(EffectiveRuntimeInfo));
            OnPropertyChanged(nameof(EffectiveRuntimeExecutablePath));
            RefreshCommandStates();
        }

        private void TrySeedPersistedRuntimeSelection()
        {
            if (string.IsNullOrWhiteSpace(_selectedRuntimeExecutablePathToRestore))
            {
                return;
            }

            var stopwatch = Stopwatch.StartNew();
            var runtime = _runtimeService.TryResolveRuntimeIdentity(_selectedRuntimeExecutablePathToRestore);
            stopwatch.Stop();

            if (runtime is null)
            {
                StartupTimingLogger.Log("MainWindowViewModel", $"Persisted runtime identity could not be restored from '{_selectedRuntimeExecutablePathToRestore}'.");
                return;
            }

            var runtimeItem = new RuntimeItemViewModel(runtime);
            DetectedRuntimes.Clear();
            DetectedRuntimes.Add(runtimeItem);
            _preferredRuntimeItem = runtimeItem;
            _selectedRuntimeItem = runtimeItem;
            _selectedRuntimeExecutablePathToRestore = runtime.LaunchExecutablePath;
            RuntimeText = $"Runtime: Checking PowerShell runtime ({runtime.DisplayName})...";
            StatusText = "Checking PowerShell runtime...";
            StartupTimingLogger.Log(
                "MainWindowViewModel",
                $"Seeded persisted runtime selection in {stopwatch.ElapsedMilliseconds} ms: {runtime.DisplayName}. " +
                $"ConfiguredPath='{_selectedRuntimeExecutablePathToRestore}', DisplayPath='{runtime.ExecutablePath}', LaunchPath='{runtime.LaunchExecutablePath}', LaunchPathExists={File.Exists(runtime.LaunchExecutablePath)}");
        }

        private static string? TryGetOptionalStringProperty(object target, string propertyName)
        {
            if (target is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                var propertyInfo = target.GetType().GetProperty(propertyName);
                if (propertyInfo is null || !propertyInfo.CanRead)
                {
                    return null;
                }

                return propertyInfo.GetValue(target) as string;
            }
            catch
            {
                return null;
            }
        }

        private static double? TryGetOptionalNullableDoubleProperty(object target, string propertyName)
        {
            if (target is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                var propertyInfo = target.GetType().GetProperty(propertyName);
                if (propertyInfo is null || !propertyInfo.CanRead)
                {
                    return null;
                }

                var value = propertyInfo.GetValue(target);
                if (value is double doubleValue)
                {
                    return doubleValue;
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        private static bool? TryGetOptionalBoolProperty(object target, string propertyName)
        {
            if (target is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                var propertyInfo = target.GetType().GetProperty(propertyName);
                if (propertyInfo is null || !propertyInfo.CanRead)
                {
                    return null;
                }

                var value = propertyInfo.GetValue(target);
                return value is bool boolValue ? boolValue : null;
            }
            catch
            {
                return null;
            }
        }

        private static void TrySetOptionalProperty(object target, string propertyName, object? value)
        {
            if (target is null || string.IsNullOrWhiteSpace(propertyName))
            {
                return;
            }

            try
            {
                var propertyInfo = target.GetType().GetProperty(propertyName);
                if (propertyInfo is null || !propertyInfo.CanWrite)
                {
                    return;
                }

                propertyInfo.SetValue(target, value);
            }
            catch
            {
                // Ignore optional persistence-property mismatches so older Domain assemblies do not crash startup.
            }
        }

        private bool TryOpenFileFromPathCore(string filePath, bool addToRecentFiles, bool logOperation, out string? failureReason)
        {
            return TryOpenFileFromPathCore(filePath, addToRecentFiles, logOperation, out failureReason, out _);
        }

        private bool TryOpenFileFromPathCore(string filePath, bool addToRecentFiles, bool logOperation, out string? failureReason, out EditorTabViewModel? openedTab)
        {
            failureReason = null;
            openedTab = null;

            var normalizedFilePath = NormalizeStoredPath(filePath);
            if (normalizedFilePath is null)
            {
                failureReason = "The file path was empty or invalid.";
                StatusText = "Open failed";
                if (logOperation)
                {
                    AppendOutputLine($"Open failed: {failureReason}");
                }

                return false;
            }

            try
            {
                foreach (var existingTab in OpenTabs)
                {
                    if (!string.IsNullOrWhiteSpace(existingTab.FilePath) &&
                        string.Equals(existingTab.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase))
                    {
                        SelectedTab = existingTab;
                        openedTab = existingTab;

                        if (addToRecentFiles)
                        {
                            AddRecentFilePath(normalizedFilePath);
                        }

                        if (logOperation)
                        {
                            StatusText = $"{existingTab.Title} already open";
                        }

                        return true;
                    }
                }

                if (!File.Exists(normalizedFilePath))
                {
                    failureReason = "The file was not found.";
                    StatusText = "Open failed";

                    if (logOperation)
                    {
                        AppendOutputLine($"Open failed: {normalizedFilePath} was not found.");
                    }

                    return false;
                }

                var content = _fileDocumentService.ReadAllText(normalizedFilePath);
                var title = Path.GetFileName(normalizedFilePath);

                var tab = new EditorTabViewModel(title, content, normalizedFilePath);
                tab.MarkSaved();
                ApplyCurrentFileMetadataToTab(tab, normalizedFilePath, content);

                OpenTabs.Add(tab);
                SelectedTab = tab;
                openedTab = tab;

                if (addToRecentFiles)
                {
                    AddRecentFilePath(normalizedFilePath);
                }

                OnPropertyChanged(nameof(OpenTabCountText));
                OnPropertyChanged(nameof(ActiveDocumentText));

                if (logOperation)
                {
                    StatusText = $"{title} opened";
                    AppLogger.Info("MainWindow", $"{normalizedFilePath} opened");
                }

                return true;
            }
            catch (UnauthorizedAccessException)
            {
                failureReason = "The file is inaccessible or you do not have permission to read it.";
                StatusText = "Open failed";

                if (logOperation)
                {
                    AppendOutputLine($"Open failed: {failureReason}");
                }

                return false;
            }
            catch (IOException)
            {
                failureReason = "The file is locked or otherwise inaccessible.";
                StatusText = "Open failed";

                if (logOperation)
                {
                    AppendOutputLine($"Open failed: {failureReason}");
                }

                return false;
            }
            catch (Exception ex)
            {
                failureReason = ex.Message;
                StatusText = "Open failed";

                if (logOperation)
                {
                    AppendOutputLine($"Open failed: {ex.Message}");
                }

                return false;
            }
        }

        private void AddRecentFilePath(string? filePath)
        {
            var normalizedPath = NormalizeStoredPath(filePath);
            if (normalizedPath is null)
            {
                return;
            }

            _recentFilePaths.RemoveAll(existingPath => string.Equals(existingPath, normalizedPath, StringComparison.OrdinalIgnoreCase));
            _recentFilePaths.Insert(0, normalizedPath);

            if (_recentFilePaths.Count > MaximumRecentFiles)
            {
                _recentFilePaths.RemoveRange(MaximumRecentFiles, _recentFilePaths.Count - MaximumRecentFiles);
            }
        }

        private static string? NormalizeStoredPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return null;
            }
        }

        private static string? NormalizeStoredRuntimePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var trimmed = path.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                return null;
            }

            if (string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.OrdinalIgnoreCase) &&
                trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        private enum SaveOperationOutcome
        {
            Saved,
            Reloaded,
            Canceled,
            Failed
        }

        private sealed record ExternalChangeCheck(
            bool HasConflict,
            string Reason,
            DocumentFileState? CurrentState);

        private bool SaveTabCore(EditorTabViewModel tab)
        {
            return SaveTabCoreWithOutcome(tab) == SaveOperationOutcome.Saved;
        }

        private SaveOperationOutcome SaveTabCoreWithOutcome(EditorTabViewModel tab)
        {
            var normalizedFilePath = NormalizeStoredPath(tab.FilePath);
            if (normalizedFilePath is null)
            {
                StatusText = "Save failed";
                AppLogger.Warning("Save", $"Save rejected for {tab.Title} because the current file path was invalid. OriginalPath='{tab.FilePath ?? "<null>"}'.");
                DeveloperDiagnostics.LogDecision(
                    "Save",
                    "DocumentSaveValidation",
                    "Document Save was rejected because the current file path was invalid.",
                    "RejectedInvalidPath",
                    new Dictionary<string, object?>
                    {
                        ["sourcePath"] = tab.FilePath,
                        ["result"] = "Failed",
                        ["userNotificationDestination"] = "StatusText",
                        ["terminalNotificationEnabled"] = false
                    });
                return SaveOperationOutcome.Failed;
            }

            var operationId = $"DocumentSave-{Guid.NewGuid():N}";
            var stopwatch = Stopwatch.StartNew();
            DeveloperDiagnostics.LogOperationStart(
                "Save",
                "DocumentSave",
                "Document Save started.",
                operationId,
                BuildSaveDiagnostics(tab, normalizedFilePath, "Save"));

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                ExternalChangeCheck changeCheck;
                try
                {
                    changeCheck = CheckForExternalChange(tab, normalizedFilePath, operationId);
                }
                catch (Exception ex)
                {
                    return CompleteSaveFailure(tab, normalizedFilePath, "Save", operationId, stopwatch, ex);
                }

                if (changeCheck.HasConflict)
                {
                    return ResolveExternalSaveConflict(tab, normalizedFilePath, changeCheck, operationId, stopwatch, attempt);
                }

                try
                {
                    WriteAndFinalizeSave(tab, normalizedFilePath, changeCheck.CurrentState, operationId, isSaveAs: false);
                    CompleteSaveSuccess(tab, normalizedFilePath, "Save", operationId, stopwatch);
                    return SaveOperationOutcome.Saved;
                }
                catch (DocumentFileChangedException ex) when (attempt < 3)
                {
                    DeveloperDiagnostics.LogDecision(
                        "Save",
                        "DocumentSaveRevalidation",
                        "The destination changed after the pre-save check; conflict resolution will run again.",
                        "RecheckConflict",
                        BuildStateDiagnostics(operationId, normalizedFilePath, ex.ExpectedState, ex.CurrentState));
                }
                catch (Exception ex)
                {
                    return CompleteSaveFailure(tab, normalizedFilePath, "Save", operationId, stopwatch, ex);
                }
            }

            return CompleteSaveFailure(
                tab,
                normalizedFilePath,
                "Save",
                operationId,
                stopwatch,
                new IOException("The file kept changing while the save was being prepared. Try again when external edits have stopped."));
        }

        private bool SaveTabAsCore(EditorTabViewModel tab, string filePath)
        {
            return SaveTabAsCoreWithOutcome(tab, filePath) == SaveOperationOutcome.Saved;
        }

        private SaveOperationOutcome SaveTabAsCoreWithOutcome(EditorTabViewModel tab, string filePath)
        {
            var normalizedFilePath = NormalizeStoredPath(NormalizeScriptSavePath(filePath));
            if (normalizedFilePath is null)
            {
                StatusText = "Save As failed";
                AppLogger.Warning("Save", $"Save As rejected for {tab.Title} because the selected path was invalid. OriginalPath='{filePath ?? "<null>"}'.");
                DeveloperDiagnostics.LogDecision(
                    "Save",
                    "DocumentSaveAsValidation",
                    "Document Save As was rejected because the selected file path was invalid.",
                    "RejectedInvalidPath",
                    new Dictionary<string, object?>
                    {
                        ["targetPath"] = filePath,
                        ["result"] = "Failed",
                        ["userNotificationDestination"] = "StatusText",
                        ["terminalNotificationEnabled"] = false
                    });
                return SaveOperationOutcome.Failed;
            }

            if (!string.IsNullOrWhiteSpace(tab.FilePath) &&
                string.Equals(NormalizeStoredPath(tab.FilePath), normalizedFilePath, StringComparison.OrdinalIgnoreCase))
            {
                return SaveTabCoreWithOutcome(tab);
            }

            var operationId = $"DocumentSaveAs-{Guid.NewGuid():N}";
            var stopwatch = Stopwatch.StartNew();
            DeveloperDiagnostics.LogOperationStart(
                "Save",
                "DocumentSaveAs",
                "Document Save As started.",
                operationId,
                BuildSaveDiagnostics(tab, normalizedFilePath, "SaveAs"));

            try
            {
                var expectedDestinationState = _fileDocumentService.GetFileState(normalizedFilePath);
                WriteAndFinalizeSave(tab, normalizedFilePath, expectedDestinationState, operationId, isSaveAs: true);
                CompleteSaveSuccess(tab, normalizedFilePath, "Save As", operationId, stopwatch);
                return SaveOperationOutcome.Saved;
            }
            catch (DocumentFileChangedException ex)
            {
                var message = "The Save As destination changed after overwrite confirmation. Nothing was overwritten. Choose Save As again to review the current destination.";
                StatusText = "Save As canceled - destination changed";
                _userPromptService.ShowWarningMessage("Save As destination changed", message);
                DeveloperDiagnostics.LogDecision(
                    "Save",
                    "DocumentSaveAsRevalidation",
                    message,
                    "DestinationChanged",
                    BuildStateDiagnostics(operationId, normalizedFilePath, ex.ExpectedState, ex.CurrentState));
                DeveloperDiagnostics.LogOperationStop(
                    "Save",
                    "DocumentSaveAs",
                    "Document Save As stopped because the destination changed during the operation.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = normalizedFilePath,
                        ["result"] = "CanceledDestinationChanged",
                        ["userNotificationDestination"] = "StatusTextAndWarningDialog",
                        ["terminalNotificationEnabled"] = false
                    });
                return SaveOperationOutcome.Canceled;
            }
            catch (Exception ex)
            {
                return CompleteSaveFailure(tab, normalizedFilePath, "Save As", operationId, stopwatch, ex);
            }
        }

        private ExternalChangeCheck CheckForExternalChange(EditorTabViewModel tab, string filePath, string operationId)
        {
            DocumentFileState currentState;
            try
            {
                currentState = _fileDocumentService.GetFileState(filePath);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                DeveloperDiagnostics.LogDecision(
                    "Save",
                    "ExternalFileChangeCheck",
                    "The current disk state could not be read safely.",
                    "DiskStateUnavailable",
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = filePath,
                        ["exceptionType"] = ex.GetType().Name
                    });
                return new ExternalChangeCheck(
                    true,
                    "The current disk version could not be read or verified. It may be locked or inaccessible.",
                    null);
            }

            if (!currentState.Exists)
            {
                return new ExternalChangeCheck(
                    true,
                    "The file was deleted or moved after it was opened or last saved.",
                    currentState);
            }

            var metadataChanged =
                !tab.LastKnownFileWriteTimeUtc.HasValue ||
                !tab.LastKnownFileLength.HasValue ||
                string.IsNullOrWhiteSpace(tab.LastKnownFileContentSha256) ||
                tab.LastKnownFileWriteTimeUtc.Value != currentState.LastWriteTimeUtc ||
                tab.LastKnownFileLength.Value != currentState.Length;

            if (!metadataChanged)
            {
                return new ExternalChangeCheck(false, string.Empty, currentState);
            }

            var snapshot = _fileDocumentService.ReadSnapshot(filePath);
            var contentMatchesKnownState =
                !string.IsNullOrWhiteSpace(tab.LastKnownFileContentSha256) &&
                string.Equals(tab.LastKnownFileContentSha256, snapshot.ContentSha256, StringComparison.Ordinal);

            DeveloperDiagnostics.LogDecision(
                "Save",
                "ExternalFileChangeCheck",
                contentMatchesKnownState
                    ? "Disk metadata changed, but disk content still matches the last known editor baseline."
                    : "Disk metadata and content indicate an external change.",
                contentMatchesKnownState ? "MetadataOnlyChange" : "ConfirmedExternalChange",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["knownLastWriteTimeUtc"] = tab.LastKnownFileWriteTimeUtc,
                    ["knownLength"] = tab.LastKnownFileLength,
                    ["currentLastWriteTimeUtc"] = snapshot.State.LastWriteTimeUtc,
                    ["currentLength"] = snapshot.State.Length,
                    ["contentMatchesKnownState"] = contentMatchesKnownState
                });

            if (contentMatchesKnownState)
            {
                return new ExternalChangeCheck(false, string.Empty, snapshot.State);
            }

            return new ExternalChangeCheck(
                true,
                "The file's contents changed after this editor tab loaded or last saved it.",
                snapshot.State);
        }

        private SaveOperationOutcome ResolveExternalSaveConflict(
            EditorTabViewModel tab,
            string filePath,
            ExternalChangeCheck changeCheck,
            string operationId,
            Stopwatch stopwatch,
            int attempt)
        {
            var decision = _userPromptService.ShowExternalFileConflictPrompt(filePath, changeCheck.Reason);
            DeveloperDiagnostics.LogDecision(
                "Save",
                "ExternalFileConflictDecision",
                "The user selected an external-file conflict outcome.",
                decision.ToString(),
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["attempt"] = attempt,
                    ["currentExists"] = changeCheck.CurrentState?.Exists,
                    ["currentLastWriteTimeUtc"] = changeCheck.CurrentState?.LastWriteTimeUtc,
                    ["currentLength"] = changeCheck.CurrentState?.Length
                });

            switch (decision)
            {
                case ExternalFileConflictDecision.ReloadFromDisk:
                    return ReloadTabFromDisk(tab, filePath, operationId, stopwatch);

                case ExternalFileConflictDecision.OverwriteDisk:
                    try
                    {
                        var authorizedState = _fileDocumentService.GetFileState(filePath);
                        WriteAndFinalizeSave(tab, filePath, authorizedState, operationId, isSaveAs: false);
                        CompleteSaveSuccess(tab, filePath, "Save", operationId, stopwatch);
                        return SaveOperationOutcome.Saved;
                    }
                    catch (DocumentFileChangedException)
                    {
                        return CompleteSaveFailure(
                            tab,
                            filePath,
                            "Save",
                            operationId,
                            stopwatch,
                            new IOException("The file changed again after overwrite was confirmed. Nothing was overwritten."));
                    }
                    catch (Exception ex)
                    {
                        return CompleteSaveFailure(tab, filePath, "Save", operationId, stopwatch, ex);
                    }

                case ExternalFileConflictDecision.SaveAs:
                    var saveAsPath = _userPromptService.ShowSaveFileDialog(GetSuggestedSaveFileName(tab));
                    if (string.IsNullOrWhiteSpace(saveAsPath))
                    {
                        StatusText = "Save As canceled";
                        CompleteSaveWithoutWrite(filePath, operationId, stopwatch, "CanceledSaveAsDialog");
                        return SaveOperationOutcome.Canceled;
                    }

                    CompleteSaveWithoutWrite(filePath, operationId, stopwatch, "DelegatedToSaveAs");
                    return SaveTabAsCoreWithOutcome(tab, saveAsPath);

                default:
                    StatusText = "Save canceled";
                    CompleteSaveWithoutWrite(filePath, operationId, stopwatch, "CanceledByUser");
                    return SaveOperationOutcome.Canceled;
            }
        }

        private SaveOperationOutcome ReloadTabFromDisk(
            EditorTabViewModel tab,
            string filePath,
            string operationId,
            Stopwatch stopwatch)
        {
            try
            {
                var snapshot = _fileDocumentService.ReadSnapshot(filePath);
                tab.Content = snapshot.Content;
                tab.SetLastKnownFileState(
                    snapshot.State.LastWriteTimeUtc,
                    snapshot.State.Length,
                    snapshot.ContentSha256);
                tab.MarkSaved();
                StatusText = $"{tab.Title} reloaded from disk";
                DeveloperDiagnostics.LogOperationStop(
                    "Save",
                    "DocumentSave",
                    "Document was reloaded from disk; the original save did not continue.",
                    stopwatch.ElapsedMilliseconds,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["targetPath"] = filePath,
                        ["result"] = "Reloaded",
                        ["diskLength"] = snapshot.State.Length,
                        ["userNotificationDestination"] = "StatusText",
                        ["terminalNotificationEnabled"] = false
                    });
                return SaveOperationOutcome.Reloaded;
            }
            catch (Exception ex)
            {
                return CompleteSaveFailure(tab, filePath, "Reload", operationId, stopwatch, ex);
            }
        }

        private void WriteAndFinalizeSave(
            EditorTabViewModel tab,
            string filePath,
            DocumentFileState? expectedDestinationState,
            string operationId,
            bool isSaveAs)
        {
            var content = tab.Content ?? string.Empty;
            _fileDocumentService.WriteAllText(filePath, content, expectedDestinationState, operationId);
            var savedState = _fileDocumentService.GetFileState(filePath);

            if (isSaveAs || !string.Equals(tab.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
            {
                tab.SetFilePath(filePath);
                OnPropertyChanged(nameof(ActiveDocumentText));
            }

            tab.SetLastKnownFileState(
                savedState.LastWriteTimeUtc,
                savedState.Length,
                ComputeContentSha256(content));
            tab.MarkSaved();
            AddRecentFilePath(filePath);
        }

        private void CompleteSaveSuccess(
            EditorTabViewModel tab,
            string filePath,
            string operationName,
            string operationId,
            Stopwatch stopwatch)
        {
            stopwatch.Stop();
            StatusText = $"{tab.Title} saved";
            DeveloperDiagnostics.LogOperationStop(
                "Save",
                operationName == "Save As" ? "DocumentSaveAs" : "DocumentSave",
                $"Document {operationName} completed successfully.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["result"] = "Saved",
                    ["savedLength"] = tab.LastKnownFileLength,
                    ["userNotificationDestination"] = "StatusText",
                    ["terminalNotificationEnabled"] = false
                });
        }

        private SaveOperationOutcome CompleteSaveFailure(
            EditorTabViewModel tab,
            string filePath,
            string operationName,
            string operationId,
            Stopwatch stopwatch,
            Exception ex)
        {
            stopwatch.Stop();
            var directoryExists = DoesSaveTargetDirectoryExist(filePath);
            var fileExistedBeforeWrite = File.Exists(filePath);
            string message;

            if (ex is DirectoryNotFoundException)
            {
                message = $"The folder for {filePath} does not exist.";
            }
            else if (ex is FileNotFoundException)
            {
                message = BuildSaveFileNotFoundMessage(filePath, directoryExists, fileExistedBeforeWrite);
            }
            else if (ex is UnauthorizedAccessException || ex is SecurityException)
            {
                message = BuildSavePermissionDeniedMessage(filePath);
            }
            else
            {
                message = ex.Message;
            }

            StatusText = $"{operationName} failed";
            _userPromptService.ShowWarningMessage($"{operationName} failed", message);
            AppLogger.Error("Save", $"{operationName} failed for {tab.Title}; Path={filePath}; OperationId={operationId}", ex);
            DeveloperDiagnostics.LogException(
                "Save",
                ex,
                $"Document {operationName} failed.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["result"] = "Failed",
                    ["elapsedMilliseconds"] = stopwatch.ElapsedMilliseconds,
                    ["tabStillDirty"] = tab.IsDirty,
                    ["userNotificationDestination"] = "StatusTextAndWarningDialog",
                    ["terminalNotificationEnabled"] = false
                });
            DeveloperDiagnostics.LogOperationStop(
                "Save",
                operationName == "Save As" ? "DocumentSaveAs" : "DocumentSave",
                $"Document {operationName} ended without saving.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["result"] = "Failed",
                    ["tabStillDirty"] = tab.IsDirty,
                    ["userNotificationDestination"] = "StatusTextAndWarningDialog",
                    ["terminalNotificationEnabled"] = false
                });
            return SaveOperationOutcome.Failed;
        }

        private static void CompleteSaveWithoutWrite(
            string filePath,
            string operationId,
            Stopwatch stopwatch,
            string result)
        {
            stopwatch.Stop();
            DeveloperDiagnostics.LogOperationStop(
                "Save",
                "DocumentSave",
                "Document Save ended without writing the current file.",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["targetPath"] = filePath,
                    ["result"] = result,
                    ["userNotificationDestination"] = "StatusText",
                    ["terminalNotificationEnabled"] = false
                });
        }

        private static Dictionary<string, object?> BuildSaveDiagnostics(
            EditorTabViewModel tab,
            string targetPath,
            string operationName)
        {
            return new Dictionary<string, object?>
            {
                ["operationName"] = operationName,
                ["targetPath"] = targetPath,
                ["sourcePath"] = tab.FilePath,
                ["isDirty"] = tab.IsDirty,
                ["contentLength"] = tab.Content?.Length ?? 0,
                ["knownLastWriteTimeUtc"] = tab.LastKnownFileWriteTimeUtc,
                ["knownLength"] = tab.LastKnownFileLength,
                ["hasKnownContentHash"] = !string.IsNullOrWhiteSpace(tab.LastKnownFileContentSha256),
                ["terminalNotificationEnabled"] = false
            };
        }

        private static Dictionary<string, object?> BuildStateDiagnostics(
            string operationId,
            string targetPath,
            DocumentFileState expectedState,
            DocumentFileState currentState)
        {
            return new Dictionary<string, object?>
            {
                ["operationId"] = operationId,
                ["targetPath"] = targetPath,
                ["expectedExists"] = expectedState.Exists,
                ["expectedLastWriteTimeUtc"] = expectedState.LastWriteTimeUtc,
                ["expectedLength"] = expectedState.Length,
                ["currentExists"] = currentState.Exists,
                ["currentLastWriteTimeUtc"] = currentState.LastWriteTimeUtc,
                ["currentLength"] = currentState.Length
            };
        }

        private static string ComputeContentSha256(string content)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content ?? string.Empty)));
        }

        private static string NormalizeScriptSavePath(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
            {
                return filePath;
            }

            var extension = Path.GetExtension(filePath);
            return string.IsNullOrWhiteSpace(extension)
                ? Path.ChangeExtension(filePath, ".ps1")
                : filePath;
        }

        private static string BuildSavePermissionDeniedMessage(string path)
        {
            return $"{ApplicationBranding.PublicName} does not have permission to save to {path}. Windows Controlled Folder Access or folder permissions may be blocking the save. Try another folder such as C:\\Temp\\PS7ScriptDeskSaveTest, or allow the app executable in Windows Security.";
        }

        private static string BuildSaveFileNotFoundMessage(string path, bool directoryExists, bool fileExistedBeforeWrite)
        {
            if (!directoryExists)
            {
                return $"The selected folder for {path} does not exist.";
            }

            if (!fileExistedBeforeWrite)
            {
                return BuildSavePermissionDeniedMessage(path);
            }

            return $"The target file {path} could not be found during save. Verify the path is still available and try again.";
        }

        private static bool DoesSaveTargetDirectoryExist(string path)
        {
            var directoryPath = Path.GetDirectoryName(path);
            return !string.IsNullOrWhiteSpace(directoryPath) && Directory.Exists(directoryPath);
        }

        private string GetSuggestedSaveFileName(EditorTabViewModel? tab)
        {
            if (tab is null)
            {
                return "Untitled.ps1";
            }

            return tab.Title.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase)
                ? tab.Title
                : $"{tab.Title}.ps1";
        }

        private bool TryHandleUnsavedChanges(EditorTabViewModel tab)
        {
            if (!tab.IsDirty)
            {
                return true;
            }

            var decision = _userPromptService.ShowUnsavedChangesPrompt(tab.Title);

            switch (decision)
            {
                case UnsavedChangesDecision.Save:
                    if (string.IsNullOrWhiteSpace(tab.FilePath))
                    {
                        var filePath = _userPromptService.ShowSaveFileDialog(GetSuggestedSaveFileName(tab));

                        if (string.IsNullOrWhiteSpace(filePath))
                        {
                            StatusText = "Save canceled";
                            return false;
                        }

                        return SaveTabAsCore(tab, filePath);
                    }

                    return SaveTabCore(tab);

                case UnsavedChangesDecision.Discard:
                    return true;

                default:
                    StatusText = "Close canceled";
                    return false;
            }
        }

        private async Task OnExportAsExeAsync()
        {
            if (_isExeExportInProgress)
            {
                return;
            }

            if (SelectedTab is null)
            {
                StatusText = "No script tab selected";
                AppendOutputLine("Export as EXE failed: there is no active editor tab.");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedTab.Content))
            {
                StatusText = "Export as EXE requires script content";
                AppendOutputLine($"Export as EXE failed for {SelectedTab.Title}: the active tab is empty.");
                return;
            }

            var runtimeToUse = EffectiveRuntimeInfo;
            if (runtimeToUse is null)
            {
                StatusText = "Export as EXE failed - no PowerShell runtime selected";
                AppendOutputLine($"Export as EXE failed for {SelectedTab.Title}: no PowerShell runtime is available.");
                return;
            }

            if (!runtimeToUse.IsPowerShell7OrLater)
            {
                StatusText = "Export as EXE requires PowerShell 7";
                AppendOutputLine($"Export as EXE failed for {SelectedTab.Title}: the selected runtime is not PowerShell 7.x. Runtime: {runtimeToUse.DisplayName}");
                return;
            }

            if (string.IsNullOrWhiteSpace(SelectedTab.FilePath))
            {
                var savePath = _userPromptService.ShowSaveFileDialog(GetSuggestedSaveFileName(SelectedTab));
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    StatusText = "Export as EXE canceled";
                    AppendOutputLine($"Export as EXE canceled for {SelectedTab.Title}: the script must be saved first.");
                    return;
                }

                if (!SaveTabAsCore(SelectedTab, savePath))
                {
                    StatusText = "Export as EXE failed";
                    AppendOutputLine($"Export as EXE stopped for {SelectedTab.Title}: saving the active script failed.");
                    return;
                }
            }
            else if (SelectedTab.IsDirty)
            {
                if (!SaveTabCore(SelectedTab))
                {
                    StatusText = "Export as EXE failed";
                    AppendOutputLine($"Export as EXE stopped for {SelectedTab.Title}: saving the active script failed.");
                    return;
                }
            }

            var selectedFilePath = SelectedTab.FilePath;
            if (string.IsNullOrWhiteSpace(selectedFilePath))
            {
                StatusText = "Export as EXE failed";
                AppendOutputLine($"Export as EXE failed for {SelectedTab.Title}: the saved script path is still unavailable.");
                return;
            }

            var suggestedExecutableName = $"{Path.GetFileNameWithoutExtension(selectedFilePath)}.exe";
            var outputExecutablePath = _userPromptService.ShowSaveExecutableDialog(suggestedExecutableName);
            if (string.IsNullOrWhiteSpace(outputExecutablePath))
            {
                StatusText = "Export as EXE canceled";
                AppendOutputLine($"Export as EXE canceled for {SelectedTab.Title}: no output path was chosen.");
                return;
            }

            _isExeExportInProgress = true;
            _exportAsExeCommand.RaiseCanExecuteChanged();

            try
            {
                StatusText = $"Export as EXE started - {SelectedTab.Title}";
                AppendOutputLine(new string('-', 60));
                AppendOutputLine($"Export as EXE started: {SelectedTab.Title}");
                AppendOutputLine($"Source script: {selectedFilePath}");
                AppendOutputLine($"Destination EXE: {outputExecutablePath}");
                AppendOutputLine($"Selected runtime: {runtimeToUse.DisplayName}");
                AppendOutputLine("Approach: local .NET wrapper build that launches PowerShell 7 and runs the embedded script.");

                var request = new ExeExportRequest(
                    selectedFilePath,
                    SelectedTab.Content,
                    outputExecutablePath,
                    runtimeToUse);

                var result = await _exeExportService.ExportScriptAsExeAsync(request);

                PostToUi(() =>
                {
                    if (result.Succeeded)
                    {
                        StatusText = "Export as EXE succeeded";
                        AppendOutputLine($"Export as EXE succeeded: {result.OutputExecutablePath}");
                    }
                    else
                    {
                        StatusText = "Export as EXE failed";
                        AppendOutputLine($"Export as EXE failed: {result.SummaryMessage}");
                    }

                    if (!string.IsNullOrWhiteSpace(result.DetailedLog))
                    {
                        AppendOutputLine(result.DetailedLog);
                    }

                    AppendOutputLine(new string('-', 60));
                });
            }
            catch (Exception ex)
            {
                AppLogger.Error("ExeExport", "Export as EXE failed outside the service-owned failure boundary.", ex);
                DeveloperDiagnostics.LogException(
                    "ExeExport",
                    ex,
                    "Export as EXE failed outside the service-owned failure boundary.",
                    new Dictionary<string, object?>
                    {
                        ["operation"] = "ExportAsExe",
                        ["sourceFileName"] = Path.GetFileName(selectedFilePath),
                        ["destinationFileName"] = Path.GetFileName(outputExecutablePath),
                        ["runtimeDisplayName"] = runtimeToUse.DisplayName,
                        ["exceptionType"] = ex.GetType().FullName
                    });
                StatusText = "Export as EXE failed";
                AppendOutputLine($"Export as EXE failed unexpectedly: {ex.Message}");
            }
            finally
            {
                _isExeExportInProgress = false;
                PostToUi(() => _exportAsExeCommand.RaiseCanExecuteChanged());
            }
        }

        private void CreateInitialTab()
        {
            var tab = new EditorTabViewModel(
                "Untitled1.ps1",
                $"# Welcome to {ApplicationBranding.PublicName}\r\n\r\n# {ApplicationBranding.Tagline}.");

            OpenTabs.Add(tab);
            SelectedTab = tab;
            _untitledCounter = 2;

            OnPropertyChanged(nameof(OpenTabCountText));
            OnPropertyChanged(nameof(ActiveDocumentText));
        }

        private void OnNewScript()
        {
            var title = $"Untitled{_untitledCounter}.ps1";
            var tab = new EditorTabViewModel(
                title,
                string.Empty
            );

            OpenTabs.Add(tab);
            SelectedTab = tab;
            _untitledCounter++;

            OnPropertyChanged(nameof(OpenTabCountText));
            OnPropertyChanged(nameof(ActiveDocumentText));

            StatusText = $"{title} created";
            AppLogger.Info("MainWindow", $"{title} opened");
        }

        private void OnCloseTab(object? parameter)
        {
            var tabToClose = parameter as EditorTabViewModel ?? SelectedTab;

            if (tabToClose is null)
            {
                StatusText = "No script tab selected";
                return;
            }

            if (!TryHandleUnsavedChanges(tabToClose))
            {
                return;
            }

            CloseTabCore(tabToClose);
        }

        private bool CanCloseAllTabs()
        {
            return OpenTabs.Count > 0;
        }

        private void OnCloseAllTabs()
        {
            var operationId = $"CloseAllTabs-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "Editor",
                "CloseAllTabs",
                "Close All requested.",
                operationId: operationId);

            var tabsToProcess = new List<EditorTabViewModel>(OpenTabs);
            AppLogger.Info("Editor", $"Close All started. OpenDocumentCount={tabsToProcess.Count}.");
            DeveloperDiagnostics.LogUserAction(
                "Editor",
                "CloseAllTabsRequested",
                "Close All requested from the shell.",
                new Dictionary<string, object?> { ["openDocumentCount"] = tabsToProcess.Count });

            foreach (var tab in tabsToProcess)
            {
                var documentMarker = DescribeDocumentForCloseAll(tab);
                AppLogger.Info("Editor", $"Close All inspecting document '{documentMarker}'. IsDirty={tab.IsDirty}.");
                DeveloperDiagnostics.LogInfo(
                    "Editor",
                    "Close All inspecting document.",
                    new Dictionary<string, object?>
                    {
                        ["document"] = documentMarker,
                        ["filePath"] = tab.FilePath,
                        ["isDirty"] = tab.IsDirty
                    });

                if (!TryHandleUnsavedChanges(tab))
                {
                    StatusText = "Close All canceled";
                    AppLogger.Warning("Editor", $"Close All canceled while processing document '{documentMarker}'.");
                    DeveloperDiagnostics.LogDecision(
                        "Editor",
                        "CloseAllTabsRequested",
                        "Close All stopped because the unsaved-changes prompt was canceled or save failed.",
                        "Canceled",
                        new Dictionary<string, object?>
                        {
                            ["document"] = documentMarker,
                            ["filePath"] = tab.FilePath,
                            ["isDirty"] = tab.IsDirty
                        });
                    return;
                }

                CloseTabCore(tab);
                AppLogger.Info("Editor", $"Close All closed document '{documentMarker}'.");
                DeveloperDiagnostics.LogInfo(
                    "Editor",
                    "Close All closed document.",
                    new Dictionary<string, object?> { ["document"] = documentMarker });
            }

            StatusText = "Close All completed";
            AppLogger.Info("Editor", "Close All completed successfully.");
            DeveloperDiagnostics.LogDecision(
                "Editor",
                "CloseAllTabsRequested",
                "Close All completed successfully.",
                "Completed",
                new Dictionary<string, object?> { ["remainingDocumentCount"] = OpenTabs.Count });
        }

        private void CloseTabCore(EditorTabViewModel tabToClose)
        {
            var closingTitle = tabToClose.Title;
            var wasSelected = ReferenceEquals(SelectedTab, tabToClose);
            var index = OpenTabs.IndexOf(tabToClose);

            OpenTabs.Remove(tabToClose);

            if (OpenTabs.Count == 0)
            {
                var newTitle = $"Untitled{_untitledCounter}.ps1";
                var replacementTab = new EditorTabViewModel(
                    newTitle,
                    "# New PowerShell script\r\n"
                );

                OpenTabs.Add(replacementTab);
                SelectedTab = replacementTab;
                _untitledCounter++;

                StatusText = $"{closingTitle} closed. {newTitle} created";
                AppLogger.Info("MainWindow", $"{closingTitle} closed");
                AppLogger.Info("MainWindow", $"{newTitle} opened");
            }
            else
            {
                if (wasSelected)
                {
                    if (index >= OpenTabs.Count)
                    {
                        index = OpenTabs.Count - 1;
                    }

                    SelectedTab = OpenTabs[index];
                }

                StatusText = $"{closingTitle} closed";
                AppLogger.Info("MainWindow", $"{closingTitle} closed");
            }

            OnPropertyChanged(nameof(OpenTabCountText));
            OnPropertyChanged(nameof(ActiveDocumentText));
            RefreshCommandStates();
        }

        private static string DescribeDocumentForCloseAll(EditorTabViewModel tab)
        {
            if (tab is null)
            {
                return "(null)";
            }

            return string.IsNullOrWhiteSpace(tab.FilePath)
                ? $"Untitled:{tab.Title}"
                : tab.FilePath;
        }

        private async Task OnRunAsync()
        {
            var selectedTab = SelectedTab;
            if (selectedTab is null)
            {
                StatusText = "No script tab selected";
                return;
            }

            // A restored tab can look clean even when the file changed on disk after the
            // workspace was saved.  Before Run, verify that a clean saved tab still
            // exactly matches the current disk file.  If it does not, mark it dirty and
            // run the visible editor buffer through a temp snapshot instead of silently
            // executing different disk content.
            var sourceFilePath = TryPrepareSavedScriptPathForVisibleRun(selectedTab);

            // Set BEFORE the first await so we are still on the UI thread and the button
            // disables synchronously (no flicker).  The flag is cleared by the sentinel
            // event when the script finishes, or in the catch block if dispatch fails.
            IsExecutionRunning = true;

            var dispatched = false;
            try
            {
                dispatched = await DispatchScriptToTerminalAsync(selectedTab.Title, selectedTab.Content, executeInCurrentScope: false, sourceFilePath: sourceFilePath).ConfigureAwait(false);
            }
            finally
            {
                // If dispatch failed before the sentinel could be queued, reset the flag
                // immediately; otherwise it will be reset by OnTerminalCommandCompleted.
                if (!dispatched)
                {
                    PostToUi(() => { IsExecutionRunning = false; RefreshCommandStates(); });
                }
            }
        }

        private string? TryPrepareSavedScriptPathForVisibleRun(EditorTabViewModel tab)
        {
            if (tab.IsDirty || string.IsNullOrWhiteSpace(tab.FilePath))
            {
                return null;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.GetFullPath(tab.FilePath);
            }
            catch (Exception ex)
            {
                MarkTabStaleForVisibleSnapshotRun(tab, $"its saved path is invalid: {ex.Message}");
                return null;
            }

            if (!string.Equals(Path.GetExtension(normalizedPath), ".ps1", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (!File.Exists(normalizedPath))
            {
                MarkTabStaleForVisibleSnapshotRun(tab, $"the saved file no longer exists at {normalizedPath}");
                return null;
            }

            try
            {
                var diskContent = File.ReadAllText(normalizedPath);
                if (string.Equals(diskContent, tab.Content ?? string.Empty, StringComparison.Ordinal))
                {
                    return normalizedPath;
                }

                MarkTabStaleForVisibleSnapshotRun(tab, $"the visible editor content no longer matches {normalizedPath}");
                return null;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException)
            {
                MarkTabStaleForVisibleSnapshotRun(tab, $"the saved file could not be read before Run: {ex.Message}");
                return null;
            }
        }

        private void MarkTabStaleForVisibleSnapshotRun(EditorTabViewModel tab, string reason)
        {
            tab.MarkExternallyStale();
            StatusText = "Saved file changed; running visible editor content";
            AppLogger.Warning("Console", $"Saved script path was not used for Run because {reason}. Tab='{tab.Title}'. Visible editor content will be executed from a temporary snapshot.");
            RefreshCommandStates();
        }

        private async Task<bool> DispatchScriptToTerminalAsync(string dispatchTitle, string scriptContent, bool executeInCurrentScope, string? sourceFilePath = null)
        {
            var runtimeToUse = EffectiveRuntimeInfo;
            if (runtimeToUse is null)
            {
                StatusText = "Run requested but no runtime was detected";
                AppendOutputLine($"Run requested for {dispatchTitle}, but no PowerShell runtime is available.");
                return false;
            }

            StatusText = $"Sending {dispatchTitle} to the live PowerShell console...";

            try
            {
                await EnsureConsoleSessionAsync(runtimeToUse, forceRestart: false, logOperation: false).ConfigureAwait(false);

                var executionIdentity = !executeInCurrentScope && !string.IsNullOrWhiteSpace(sourceFilePath)
                    ? sourceFilePath
                    : dispatchTitle;

                await _liveConsoleService.ExecuteScriptAsync(
                    executionIdentity,
                    scriptContent,
                    AppendExecutionOutput,
                    executeInCurrentScope).ConfigureAwait(false);

                PostToUi(() =>
                {
                    UpdateConsoleSessionPresentation();
                    StatusText = $"{dispatchTitle} sent to the live PowerShell console";
                    // No terminal output here: the script is now executing inside the ConPTY
                    // session and any plain-text lifecycle messages written to xterm.js here
                    // would interleave with ANSI-formatted ConPTY output, corrupting both.
                });

                return true;
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    StatusText = "Send to console failed";
                    AppendOutputLine($"Send to console failed: {ex.Message}");
                    AppendOutputLine(new string('-', 60));
                    UpdateConsoleSessionPresentation();
                });

                return false;
            }
        }

        private async Task OnExecuteConsoleCommandAsync()
        {
            var commandText = ConsoleCommandText;
            if (string.IsNullOrWhiteSpace(commandText))
            {
                StatusText = "Enter a PowerShell command first";
                return;
            }

            var runtimeToUse = EffectiveRuntimeInfo;
            if (runtimeToUse is null)
            {
                StatusText = "No PowerShell runtime is available for the ConPTY terminal";
                return;
            }

            // Add to history before clearing the input box (4A).
            AddToCommandHistory(commandText);
            ConsoleCommandText = string.Empty;
            StatusText = "Sending command to the live PowerShell console...";

            IsExecutionRunning = true;
            var dispatched = false;

            try
            {
                await EnsureConsoleSessionAsync(runtimeToUse, forceRestart: false, logOperation: false).ConfigureAwait(false);
                await _liveConsoleService.ExecuteConsoleCommandAsync(commandText, AppendExecutionOutput).ConfigureAwait(false);
                dispatched = true;

                PostToUi(() =>
                {
                    UpdateConsoleSessionPresentation();
                    StatusText = "Command sent to the live PowerShell console";
                });
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    StatusText = "Console command failed";
                    AppendOutputLine($"Console command failed: {ex.Message}");
                    UpdateConsoleSessionPresentation();
                });
            }
            finally
            {
                if (!dispatched)
                {
                    PostToUi(() =>
                    {
                        IsExecutionRunning = false;
                        RefreshCommandStates();
                    });
                }
            }
        }

        private async Task OnStopAsync()
        {
            if (IsStopInProgress)
            {
                return;
            }

            if (!_liveConsoleService.IsSessionRunning)
            {
                IsExecutionRunning = false;
                IsStopInProgress = false;
                StatusText = "The PowerShell terminal already exited. Use Reset Console to start a new session.";
                UpdateConsoleSessionPresentation();
                RefreshCommandStates();
                AppLogger.Info("Console", "Interrupt was requested after the PowerShell terminal had already exited. Cleared stale execution state so the console can be restarted.");
                return;
            }

            if (!_liveConsoleService.IsCommandInProgress)
            {
                StatusText = "There is no running script or command to stop";
                return;
            }

            var operationId = $"Interrupt-{Guid.NewGuid():N}";
            using var scope = DeveloperDiagnostics.BeginTimedOperation(
                "Terminal",
                "InterruptRequested",
                "Interrupt requested from the shell.",
                operationId: operationId);

            StatusText = "Interrupting the current PowerShell operation...";
            AppLogger.Info("Console", $"Interrupt requested from the shell. OperationId={operationId}, SessionRunning={_liveConsoleService.IsSessionRunning}, CommandInProgress={_liveConsoleService.IsCommandInProgress}, IsExecutionRunning={IsExecutionRunning}, IsDebugSessionActive={IsDebugSessionActive}.");
            DeveloperDiagnostics.LogUserAction(
                "Terminal",
                "InterruptRequested",
                "Interrupt requested from the shell.",
                new Dictionary<string, object?>
                {
                    ["operationId"] = operationId,
                    ["sessionRunning"] = _liveConsoleService.IsSessionRunning,
                    ["commandInProgress"] = _liveConsoleService.IsCommandInProgress,
                    ["isExecutionRunning"] = IsExecutionRunning,
                    ["isDebugSessionActive"] = IsDebugSessionActive
                });
            IsStopInProgress = true;

            if (!await _consoleRecoveryGate.WaitAsync(0).ConfigureAwait(false))
            {
                PostToUi(() =>
                {
                    IsStopInProgress = false;
                    StatusText = "Another console recovery operation is already in progress";
                    RefreshCommandStates();
                });
                AppLogger.Info("Console", $"Duplicate Interrupt request rejected because another console recovery operation owns the recovery gate. OperationId={operationId}.");
                return;
            }

            try
            {
                var interruptResult = await _liveConsoleService
                    .InterruptOrRestartAsync(AppendExecutionOutput)
                    .ConfigureAwait(false);

                var executionResolved = interruptResult.CompletedGracefully ||
                                        interruptResult.SessionRestarted ||
                                        !_liveConsoleService.IsSessionRunning ||
                                        !_liveConsoleService.IsCommandInProgress;

                PostToUi(() =>
                {
                    if (interruptResult.SessionRestarted)
                    {
                        StatusText = "PowerShell session restarted after interrupt timeout";
                    }
                    else if (interruptResult.CompletedGracefully)
                    {
                        StatusText = "Interrupt completed";
                    }
                    else if (interruptResult.InterruptAttempted)
                    {
                        StatusText = executionResolved
                            ? "Interrupt attempted"
                            : "Interrupt recovery did not complete; use Reset Console";
                    }
                    else
                    {
                        StatusText = "Interrupt was not needed";
                    }

                    IsExecutionRunning = !executionResolved;
                    UpdateConsoleSessionPresentation();
                });

                if (executionResolved)
                {
                    RequestTerminalInteractiveStateNormalization("InterruptCompleted");
                }

                var diagnosticOutcome = interruptResult.SessionRestarted
                    ? "ForcedRestart"
                    : interruptResult.CompletedGracefully
                        ? "GracefulCompletion"
                        : executionResolved
                            ? "ResolvedWithoutRestart"
                            : "RecoveryIncomplete";
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "InterruptRequested",
                    interruptResult.SessionRestarted
                        ? "Interrupt escalated to a forced owned-session restart."
                        : interruptResult.CompletedGracefully
                            ? "Interrupt completed without a forced restart."
                            : executionResolved
                                ? "Interrupt target ended or was replaced without a forced restart."
                                : "Interrupt recovery did not resolve the active managed operation.",
                    diagnosticOutcome,
                    new Dictionary<string, object?>
                    {
                        ["operationId"] = operationId,
                        ["interruptAttempted"] = interruptResult.InterruptAttempted,
                        ["completedGracefully"] = interruptResult.CompletedGracefully,
                        ["escalationRequired"] = interruptResult.EscalationRequired,
                        ["processTerminationSucceeded"] = interruptResult.ProcessTerminationSucceeded,
                        ["sessionRestarted"] = interruptResult.SessionRestarted,
                        ["ownedProcessId"] = interruptResult.OwnedProcessId,
                        ["gracefulTimeoutMs"] = interruptResult.GracefulTimeout.TotalMilliseconds
                    });
            }
            catch (Exception ex)
            {
                AppLogger.Error("Console", $"Interrupt or restart failed. OperationId={operationId}.", ex);
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Interrupt or restart failed.",
                    new Dictionary<string, object?> { ["operationId"] = operationId });
                var executionResolved = !_liveConsoleService.IsSessionRunning ||
                                        !_liveConsoleService.IsCommandInProgress;
                PostToUi(() =>
                {
                    StatusText = executionResolved
                        ? "Interrupt recovery failed after the previous operation ended"
                        : "Interrupt recovery failed; use Reset Console";
                    AppendOutputLine($"Interrupt recovery failed: {ex.Message}");
                    IsExecutionRunning = !executionResolved;
                    UpdateConsoleSessionPresentation();
                });
            }
            finally
            {
                _consoleRecoveryGate.Release();
                PostToUi(() =>
                {
                    IsStopInProgress = false;
                    RefreshCommandStates();
                });
            }
        }

        private async Task OnRestartConsoleAsync()
        {
            if (!await _consoleRecoveryGate.WaitAsync(0).ConfigureAwait(false))
            {
                CancelPendingTerminalFocusRestore("RecoveryGateBusy");
                PostToUi(() => StatusText = "Wait for the current Interrupt or Reset Console operation to finish");
                AppLogger.Info("Console", "Reset Console request rejected because another console recovery operation is still in progress.");
                return;
            }

            try
            {
                Interlocked.Exchange(ref _resetConsoleInProgress, 1);
                PostToUi(() => IsStopInProgress = true);

                if (!_preparedTerminalFocusIntent.IsRequested)
                {
                    PrepareTerminalFocusRestoreForReset();
                }

                if (IsExecutionRunning && !_liveConsoleService.IsSessionRunning)
                {
                    AppLogger.Info("Console", "Reset Console requested while the UI still showed execution running but the PowerShell terminal process was already stopped. Clearing stale execution state before restart.");
                    IsExecutionRunning = false;
                    RefreshCommandStates();
                }

                var runtimeToUse = EffectiveRuntimeInfo;
                if (runtimeToUse is null)
                {
                    StatusText = "No PowerShell runtime is available to start the ConPTY terminal";
                    return;
                }

                StatusText = "Restarting PowerShell terminal...";
                AppLogger.Info("Console", $"Restarting PowerShell terminal using {runtimeToUse.DisplayName}. ActiveExecution={IsExecutionRunning}, CommandInProgress={_liveConsoleService.IsCommandInProgress}.");

                try
                {
                    await EnsureConsoleSessionAsync(runtimeToUse, forceRestart: true, logOperation: true).ConfigureAwait(false);
                    PostToUi(() =>
                    {
                        IsExecutionRunning = false;
                        StatusText = $"ConPTY terminal restarted with {runtimeToUse.DisplayName}";
                        RefreshCommandStates();
                    });
                    RequestTerminalFocusAfterReset(Volatile.Read(ref _currentTerminalGeneration), "ReplacementStarted");
                }
                catch (Exception ex)
                {
                    CancelPendingTerminalFocusRestore("ReplacementFailed");
                    PostToUi(() =>
                    {
                        StatusText = "ConPTY terminal restart failed";
                        AppendOutputLine($"ConPTY terminal restart failed: {ex.Message}");
                        UpdateConsoleSessionPresentation();
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _resetConsoleInProgress, 0);
                _consoleRecoveryGate.Release();
                PostToUi(() =>
                {
                    IsStopInProgress = false;
                    RefreshCommandStates();
                });
            }
        }

        private async Task OnClearConsoleAsync()
        {
            // Match the user-verified behavior of typing "cls" in the live
            // PowerShell console.  Earlier versions tried to combine an xterm.js
            // display clear with Ctrl+L.  That could move the cursor without fully
            // synchronizing PSReadLine/ConsoleHost state and was observed to leave a
            // stray continuation prompt (">>").  Sending the same command the user
            // can type manually keeps PowerShell, PSReadLine, ConPTY, and xterm.js
            // in agreement: PowerShell performs the clear and redraws its own prompt.
            if (_liveConsoleService.IsSessionRunning)
            {
                try
                {
                    PostToUi(() => _focusTerminalSink?.Invoke());

                    await _liveConsoleService.WriteRawInputAsync("cls\r").ConfigureAwait(false);

                    PostToUi(() =>
                    {
                        _focusTerminalSink?.Invoke();
                        StatusText = "Terminal output cleared";
                    });

                    AppLogger.Info("Console", "Terminal output was cleared by sending the PowerShell cls command through the live ConPTY session.");
                    return;
                }
                catch (Exception ex)
                {
                    AppLogger.Warning("Console", $"PowerShell cls clear failed; falling back to xterm display clear. Reason={ex.Message}");
                }
            }

            // Fallback for startup or a missing/terminated session.  There is no
            // live PowerShell process available to redraw a prompt, so this is
            // display-only and should be used only when no session exists.
            PostToUi(() =>
            {
                if (_clearTerminalSink is not null)
                {
                    _clearTerminalSink();
                }
                else
                {
                    TerminalDisplayText = string.Empty;
                }

                _focusTerminalSink?.Invoke();
                StatusText = "Terminal output cleared";
            });

            AppLogger.Info("Console", "Terminal output was cleared by the app UI fallback path because no live session was available.");
        }

        private async Task OnRefreshRuntimesAsync()
        {
            await RefreshRuntimeDiscoveryAsync(logOperation: true, updateStatusText: true, requireLaunchValidation: true).ConfigureAwait(false);
        }

        private async Task RefreshRuntimeDiscoveryAsync(bool logOperation, bool updateStatusText, bool requireLaunchValidation = false)
        {
            if (IsRuntimeDiscoveryInProgress)
            {
                return;
            }

            var discoveryStopwatch = Stopwatch.StartNew();
            IsRuntimeDiscoveryInProgress = true;
            PostToUi(() =>
            {
                if (updateStatusText || EffectiveRuntimeItem is null)
                {
                    StatusText = "Checking PowerShell runtime...";
                }

                RuntimeText = updateStatusText
                    ? "Runtime: Refreshing installed PowerShell runtimes..."
                    : "Runtime: Checking PowerShell runtime...";
            });
            StartupTimingLogger.Log(
                "MainWindowViewModel",
                requireLaunchValidation
                    ? "Runtime discovery started. Mode=LaunchValidation"
                    : "Runtime discovery started. Mode=MetadataFastPath");

            try
            {
                var discoveryResult = await Task.Run(() => _runtimeService.DiscoverRuntimes(requireLaunchValidation)).ConfigureAwait(false);
                StartupTimingLogger.Log(
                    "MainWindowViewModel",
                    $"Runtime discovery finished in {discoveryStopwatch.ElapsedMilliseconds} ms with {discoveryResult.DetectedRuntimes.Count} detected runtime(s). Mode={(requireLaunchValidation ? "LaunchValidation" : "MetadataFastPath")}");

                PostToUi(() =>
                {
                    DetectedRuntimes.Clear();
                    _preferredRuntimeItem = null;

                    foreach (var runtime in discoveryResult.DetectedRuntimes)
                    {
                        var runtimeItem = new RuntimeItemViewModel(runtime);
                        DetectedRuntimes.Add(runtimeItem);

                        if (runtimeItem.IsPreferred)
                        {
                            _preferredRuntimeItem = runtimeItem;
                        }
                    }

                    // Mark discovery complete before setting the selection so IsRuntimeListEnabled
                    // is already true when SelectedRuntimeItem fires its property notifications,
                    // preventing a brief disabled-state flash on the ListBox.
                    IsRuntimeDiscoveryInProgress = false;

                    RuntimeItemViewModel? runtimeToSelect = null;

                    if (!string.IsNullOrWhiteSpace(_selectedRuntimeExecutablePathToRestore))
                    {
                        foreach (var runtimeItem in DetectedRuntimes)
                        {
                            if (string.Equals(runtimeItem.ExecutablePath, _selectedRuntimeExecutablePathToRestore, StringComparison.OrdinalIgnoreCase))
                            {
                                runtimeToSelect = runtimeItem;
                                break;
                            }
                        }
                    }

                    SelectedRuntimeItem = runtimeToSelect ?? _preferredRuntimeItem;
                    RuntimeText = discoveryResult.SummaryText;

                    OnPropertyChanged(nameof(RuntimeCountText));
                    OnPropertyChanged(nameof(PreferredRuntimeText));
                    OnPropertyChanged(nameof(RuntimeListHeaderText));

                    if (logOperation)
                    {
                        AppendOutputLine("PowerShell runtime discovery complete.");

                        if (_preferredRuntimeItem is null)
                        {
                            AppendOutputLine("PowerShell 7 was not found or could not be launched.");
                        }
                        else
                        {
                            AppendOutputLine($"Preferred runtime: {_preferredRuntimeItem.DisplayName}");
                        }

                        foreach (var runtimeItem in DetectedRuntimes)
                        {
                            AppendOutputLine($"Detected runtime: {runtimeItem.DisplayText} -> {runtimeItem.ExecutablePath}");
                        }
                    }

                    if (updateStatusText)
                    {
                        StatusText = _preferredRuntimeItem is null
                            ? "PowerShell 7 was not found or could not be launched"
                            : $"Runtime discovery refreshed - {_preferredRuntimeItem.DisplayName} preferred";
                    }
                    else if (_preferredRuntimeItem is null)
                    {
                        StatusText = "PowerShell 7 was not found or could not be launched";
                    }
                    else
                    {
                        StatusText = $"Runtime discovery completed - {_preferredRuntimeItem.DisplayName} preferred";
                    }

                    UpdateConsoleSessionPresentation();
                });
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("MainWindowViewModel", $"Runtime discovery failed after {discoveryStopwatch.ElapsedMilliseconds} ms: {ex}");
                PostToUi(() =>
                {
                    RuntimeText = "Runtime: Runtime discovery failed";
                    StatusText = "Runtime discovery failed";
                    AppendOutputLine($"Runtime discovery failed: {ex.Message}");
                    UpdateConsoleSessionPresentation();
                });
            }
            finally
            {
                PostToUi(() =>
                {
                    if (IsRuntimeDiscoveryInProgress)
                    {
                        IsRuntimeDiscoveryInProgress = false;
                    }
                });
            }
        }

        private async Task EnsureConsoleSessionAsync(PowerShellRuntimeInfo runtime, bool forceRestart, bool logOperation)
        {
            if (runtime is null)
            {
                return;
            }

            var startupAttempted = false;
            await _consoleSessionGate.WaitAsync().ConfigureAwait(false);
            try
            {
                var sessionIsCurrent = _liveConsoleService.IsSessionRunning &&
                                       _liveConsoleService.ActiveRuntime is not null &&
                                       string.Equals(_liveConsoleService.ActiveRuntime.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase);

                if (forceRestart && _liveConsoleService.IsSessionRunning)
                {
                    var stopped = await _liveConsoleService.StopConsoleAsync(AppendExecutionOutput).ConfigureAwait(false);
                    if (!stopped || _liveConsoleService.IsSessionRunning)
                    {
                        throw new InvalidOperationException("The existing PowerShell terminal session did not reach a clean teardown boundary.");
                    }

                    sessionIsCurrent = false;
                }

                if (!sessionIsCurrent)
                {
                    var startupDirectory = GetConsoleStartupDirectory();
                    AppLogger.Info("Console", $"Starting PowerShell terminal using {runtime.DisplayName}; StartupDirectory={startupDirectory}; ForceRestart={forceRestart}; LogOperation={logOperation}");
                    startupAttempted = true;
                    await _liveConsoleService.StartSessionAsync(runtime, AppendExecutionOutput, startupDirectory).ConfigureAwait(false);

                    PostToUi(() =>
                    {
                        UpdateConsoleSessionPresentation();
                        StatusText = $"PowerShell terminal ready: {runtime.DisplayName}";
                        AppLogger.Info("Console", $"PowerShell terminal ready using {runtime.DisplayName}; CurrentDirectory={_liveConsoleService.CurrentWorkingDirectory ?? startupDirectory}");
                    });
                }
                else
                {
                    PostToUi(UpdateConsoleSessionPresentation);
                }
            }
            catch (Exception ex)
            {
                if (startupAttempted)
                {
                    await HandleConsoleRuntimeLaunchFailureAsync(runtime, ex).ConfigureAwait(false);
                }
                else
                {
                    AppLogger.Error("Console", "PowerShell terminal replacement failed before a new runtime launch was attempted.", ex);
                    DeveloperDiagnostics.LogException(
                        "Terminal",
                        ex,
                        "Console replacement failed before runtime startup.",
                        new Dictionary<string, object?>
                        {
                            ["forceRestart"] = forceRestart,
                            ["sessionRunning"] = _liveConsoleService.IsSessionRunning,
                            ["commandInProgress"] = _liveConsoleService.IsCommandInProgress
                        });
                }
                throw;
            }
            finally
            {
                _consoleSessionGate.Release();
            }
        }

        private void ScheduleDeferredRuntimeLaunchVerification(string source)
        {
            var runtime = EffectiveRuntimeInfo;
            if (runtime is null || !runtime.IsPowerShell7OrLater || string.IsNullOrWhiteSpace(runtime.LaunchExecutablePath))
            {
                return;
            }

            var generation = Interlocked.Increment(ref _runtimeLaunchVerificationGeneration);
            CancellationTokenSource? previousCancellationTokenSource = null;
            var verificationCancellationTokenSource = new CancellationTokenSource();

            previousCancellationTokenSource = Interlocked.Exchange(
                ref _runtimeLaunchVerificationCancellationTokenSource,
                verificationCancellationTokenSource);

            try
            {
                previousCancellationTokenSource?.Cancel();
            }
            catch
            {
                // Best effort cancellation only.
            }
            finally
            {
                previousCancellationTokenSource?.Dispose();
            }

            StartupTimingLogger.Log(
                "MainWindowViewModel",
                $"Scheduled delayed PowerShell runtime launch verification for '{runtime.LaunchExecutablePath}'. Source={source}.");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), verificationCancellationTokenSource.Token).ConfigureAwait(false);

                    if (verificationCancellationTokenSource.Token.IsCancellationRequested || generation != _runtimeLaunchVerificationGeneration)
                    {
                        return;
                    }

                    var validationStopwatch = Stopwatch.StartNew();
                    var validationResult = _runtimeService.ValidateRuntimePath(
                        runtime.LaunchExecutablePath,
                        $"Delayed runtime launch verification ({source})");
                    validationStopwatch.Stop();

                    if (verificationCancellationTokenSource.Token.IsCancellationRequested || generation != _runtimeLaunchVerificationGeneration)
                    {
                        return;
                    }

                    if (validationResult.RuntimeInfo is null || !validationResult.RuntimeInfo.IsPowerShell7OrLater)
                    {
                        StartupTimingLogger.Log(
                            "MainWindowViewModel",
                            $"Delayed PowerShell runtime launch verification failed in {validationStopwatch.ElapsedMilliseconds} ms for '{runtime.LaunchExecutablePath}'. Reason={validationResult.CandidateInfo.FailureReason}");

                        PostToUi(() => ApplyRuntimeLaunchVerificationFailure(
                            runtime,
                            validationResult.CandidateInfo.FailureReason,
                            "Delayed runtime launch verification failed.",
                            showWarning: true));
                        return;
                    }

                    StartupTimingLogger.Log(
                        "MainWindowViewModel",
                        $"Delayed PowerShell runtime launch verification succeeded in {validationStopwatch.ElapsedMilliseconds} ms for '{validationResult.RuntimeInfo.DisplayName}' ({validationResult.RuntimeInfo.LaunchExecutablePath}).");
                    AppLogger.Info(
                        "Runtime",
                        $"Delayed runtime launch verification succeeded for {validationResult.RuntimeInfo.DisplayName}. DisplayPath='{validationResult.RuntimeInfo.ExecutablePath}', LaunchPath='{validationResult.RuntimeInfo.LaunchExecutablePath}'.");
                }
                catch (OperationCanceledException)
                {
                    // Expected during shutdown or when a newer runtime verification supersedes this one.
                }
                catch (Exception ex)
                {
                    StartupTimingLogger.Log(
                        "MainWindowViewModel",
                        $"Delayed PowerShell runtime launch verification failed unexpectedly for '{runtime.LaunchExecutablePath}': {ex.GetType().Name}: {ex.Message}");
                    AppLogger.Error("Runtime", "Delayed PowerShell runtime launch verification failed unexpectedly.", ex);
                }
            });
        }

        private async Task HandleConsoleRuntimeLaunchFailureAsync(PowerShellRuntimeInfo runtime, Exception exception)
        {
            var runtimePath = runtime.LaunchExecutablePath;
            StartupTimingLogger.Log(
                "MainWindowViewModel",
                $"Console runtime launch failed for '{runtimePath}'. Verifying runtime with a real launch probe. Error={exception.GetType().Name}: {exception.Message}");
            AppLogger.Error("Console", $"PowerShell terminal launch failed for runtime '{runtimePath}'.", exception);

            RuntimeValidationResult? validationResult = null;
            try
            {
                validationResult = await Task.Run(() => _runtimeService.ValidateRuntimePath(runtimePath, "Console startup failure verification")).ConfigureAwait(false);
            }
            catch (Exception validationException)
            {
                AppLogger.Error("Runtime", $"Runtime validation after console launch failure also failed for '{runtimePath}'.", validationException);
            }

            var failureReason = validationResult?.RuntimeInfo is null
                ? validationResult?.CandidateInfo.FailureReason ?? exception.Message
                : exception.Message;

            if (validationResult?.RuntimeInfo is not null)
            {
                AppLogger.Warning("Console", $"PowerShell terminal startup failed, but the configured runtime passed an independent launch validation. RuntimePath='{runtimePath}', FailureType={exception.GetType().Name}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "ConsoleStartupFailure",
                    "The runtime remained valid; the failure was classified as a terminal-session startup failure.",
                    "RuntimeValidated",
                    new Dictionary<string, object?>
                    {
                        ["runtimePath"] = runtimePath,
                        ["failureType"] = exception.GetType().Name
                    });
                return;
            }

            PostToUi(() =>
            {
                ApplyRuntimeLaunchVerificationFailure(
                    runtime,
                    failureReason,
                    "PowerShell terminal launch failed.",
                    showWarning: true);

                PromptForReplacementRuntimeAfterConsoleFailure(failureReason);
            });
        }

        private void ApplyRuntimeLaunchVerificationFailure(
            PowerShellRuntimeInfo runtime,
            string failureReason,
            string source,
            bool showWarning)
        {
            var failedPath = NormalizeStoredRuntimePath(runtime.LaunchExecutablePath);
            if (string.IsNullOrWhiteSpace(failedPath))
            {
                failedPath = runtime.LaunchExecutablePath;
            }

            for (var index = DetectedRuntimes.Count - 1; index >= 0; index--)
            {
                var runtimeItemPath = NormalizeStoredRuntimePath(DetectedRuntimes[index].RuntimeInfo.LaunchExecutablePath);
                if (string.Equals(runtimeItemPath, failedPath, StringComparison.OrdinalIgnoreCase))
                {
                    DetectedRuntimes.RemoveAt(index);
                }
            }

            if (SelectedRuntimeItem is not null &&
                string.Equals(NormalizeStoredRuntimePath(SelectedRuntimeItem.RuntimeInfo.LaunchExecutablePath), failedPath, StringComparison.OrdinalIgnoreCase))
            {
                SelectedRuntimeItem = null;
            }

            if (_preferredRuntimeItem is not null &&
                string.Equals(NormalizeStoredRuntimePath(_preferredRuntimeItem.RuntimeInfo.LaunchExecutablePath), failedPath, StringComparison.OrdinalIgnoreCase))
            {
                _preferredRuntimeItem = null;
            }

            if (string.Equals(NormalizeStoredRuntimePath(_selectedRuntimeExecutablePathToRestore), failedPath, StringComparison.OrdinalIgnoreCase))
            {
                _selectedRuntimeExecutablePathToRestore = null;
            }

            RuntimeText = "Runtime: PowerShell 7 runtime could not be launched";
            StatusText = "PowerShell 7 runtime could not be launched. Refresh runtimes or choose a valid pwsh.exe.";
            AppendOutputLine($"{source} The saved PowerShell runtime was marked invalid: {runtime.DisplayName}");
            AppendOutputLine($"Runtime path: {runtime.LaunchExecutablePath}");
            AppendOutputLine($"Reason: {failureReason}");
            AppendOutputLine("Use Refresh Runtimes to perform a full launch validation scan, or restart the app to choose a valid PowerShell 7 pwsh.exe path.");

            StartupTimingLogger.Log(
                "MainWindowViewModel",
                $"Runtime marked invalid after launch verification failure. Path='{runtime.LaunchExecutablePath}', Source='{source}', Reason='{failureReason}'.");

            OnPropertyChanged(nameof(RuntimeCountText));
            OnPropertyChanged(nameof(PreferredRuntimeText));
            OnPropertyChanged(nameof(RuntimeListHeaderText));
            OnPropertyChanged(nameof(RuntimeDetailsText));
            OnPropertyChanged(nameof(RuntimePathText));
            OnPropertyChanged(nameof(SelectedRuntimeCompactText));
            OnPropertyChanged(nameof(SelectedRuntimePathOnlyText));
            OnPropertyChanged(nameof(EffectiveRuntimeItem));
            OnPropertyChanged(nameof(EffectiveRuntimeInfo));
            OnPropertyChanged(nameof(EffectiveRuntimeExecutablePath));
            RefreshCommandStates();
            UpdateConsoleSessionPresentation();

            if (showWarning && !_runtimeLaunchVerificationWarningShown)
            {
                _runtimeLaunchVerificationWarningShown = true;
                _userPromptService.ShowWarningMessage(
                    "PowerShell 7 runtime could not be launched",
                    "PS7 ScriptDesk started quickly using saved pwsh.exe file metadata, but a later launch verification failed." + Environment.NewLine + Environment.NewLine +
                    $"Runtime path:" + Environment.NewLine + runtime.LaunchExecutablePath + Environment.NewLine + Environment.NewLine +
                    $"Reason:" + Environment.NewLine + failureReason + Environment.NewLine + Environment.NewLine +
                    "Use Refresh Runtimes to run a full validation scan. If PowerShell 7 was moved, removed, blocked, or corrupted, restart PS7 ScriptDesk and browse to a valid PowerShell 7 pwsh.exe.");
            }
        }

        private void PromptForReplacementRuntimeAfterConsoleFailure(string failureReason)
        {
            if (_runtimeReplacementPromptShown)
            {
                return;
            }

            _runtimeReplacementPromptShown = true;

            var replacementPath = _userPromptService.ShowOpenPowerShellExecutableDialog();
            if (string.IsNullOrWhiteSpace(replacementPath))
            {
                _runtimeReplacementPromptShown = false;
                StatusText = "PowerShell 7 runtime selection canceled";
                return;
            }

            StatusText = "Validating selected PowerShell 7 runtime...";
            AppendOutputLine($"Validating replacement PowerShell runtime: {replacementPath}");

            _ = Task.Run(() =>
            {
                RuntimeValidationResult validationResult;
                try
                {
                    validationResult = _runtimeService.ValidateRuntimePath(replacementPath, "Replacement runtime selected after console launch failure");
                }
                catch (Exception ex)
                {
                    PostToUi(() =>
                    {
                        StatusText = "Replacement PowerShell runtime validation failed";
                        AppendOutputLine($"Replacement runtime validation failed: {ex.Message}");
                        _userPromptService.ShowWarningMessage(
                            "Replacement PowerShell runtime validation failed",
                            "The selected PowerShell runtime could not be validated." + Environment.NewLine + Environment.NewLine + ex.Message);
                    });
                    return;
                }

                PostToUi(() =>
                {
                    if (validationResult.RuntimeInfo is null || !validationResult.RuntimeInfo.IsPowerShell7OrLater)
                    {
                        StatusText = "Selected PowerShell runtime was not valid";
                        AppendOutputLine($"Selected replacement runtime was rejected: {validationResult.FailureReason}");
                        _userPromptService.ShowWarningMessage(
                            "Selected PowerShell runtime was not valid",
                            "PS7 ScriptDesk requires PowerShell 7.0 or newer." + Environment.NewLine + Environment.NewLine +
                            $"Selected path:" + Environment.NewLine + replacementPath + Environment.NewLine + Environment.NewLine +
                            $"Reason:" + Environment.NewLine + validationResult.FailureReason);
                        _runtimeReplacementPromptShown = false;
                        return;
                    }

                    ApplyValidatedReplacementRuntime(validationResult.RuntimeInfo);
                    AppendOutputLine($"Replacement PowerShell runtime accepted: {validationResult.RuntimeInfo.DisplayName} -> {validationResult.RuntimeInfo.LaunchExecutablePath}");
                    StatusText = $"PowerShell runtime updated - {validationResult.RuntimeInfo.DisplayName}";
                });
            });
        }

        private void ApplyValidatedReplacementRuntime(PowerShellRuntimeInfo runtime)
        {
            var runtimeItem = new RuntimeItemViewModel(runtime);
            var runtimePath = NormalizeStoredRuntimePath(runtime.LaunchExecutablePath);

            for (var index = DetectedRuntimes.Count - 1; index >= 0; index--)
            {
                var existingPath = NormalizeStoredRuntimePath(DetectedRuntimes[index].RuntimeInfo.LaunchExecutablePath);
                if (string.Equals(existingPath, runtimePath, StringComparison.OrdinalIgnoreCase))
                {
                    DetectedRuntimes.RemoveAt(index);
                }
            }

            DetectedRuntimes.Insert(0, runtimeItem);
            _preferredRuntimeItem = runtimeItem;
            SelectedRuntimeItem = runtimeItem;
            _selectedRuntimeExecutablePathToRestore = runtime.LaunchExecutablePath;
            _runtimeLaunchVerificationWarningShown = false;
            _runtimeReplacementPromptShown = false;

            RuntimeText = $"Runtime: {runtime.DisplayName}";
            OnPropertyChanged(nameof(RuntimeCountText));
            OnPropertyChanged(nameof(PreferredRuntimeText));
            OnPropertyChanged(nameof(RuntimeListHeaderText));
            OnPropertyChanged(nameof(RuntimeDetailsText));
            OnPropertyChanged(nameof(RuntimePathText));
            OnPropertyChanged(nameof(SelectedRuntimeCompactText));
            OnPropertyChanged(nameof(SelectedRuntimePathOnlyText));
            OnPropertyChanged(nameof(EffectiveRuntimeItem));
            OnPropertyChanged(nameof(EffectiveRuntimeInfo));
            OnPropertyChanged(nameof(EffectiveRuntimeExecutablePath));
            RefreshCommandStates();
            UpdateConsoleSessionPresentation();
        }

        private string GetConsoleStartupDirectory()
        {
            if (!string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath) && Directory.Exists(_currentWorkspaceFolderPath))
            {
                return _currentWorkspaceFolderPath;
            }

            if (SelectedTab is not null && !string.IsNullOrWhiteSpace(SelectedTab.FilePath))
            {
                var directory = Path.GetDirectoryName(SelectedTab.FilePath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    return directory;
                }
            }

            return Environment.CurrentDirectory;
        }

        private void UpdateConsoleSessionPresentation()
        {
            RefreshCommandStates();

            var runtime = _liveConsoleService.ActiveRuntime;
            var currentDirectory = _liveConsoleService.CurrentWorkingDirectory;

            if (!_liveConsoleService.IsHostAttached)
            {
                ConsoleSessionText = "ConPTY terminal: starting";
                _consolePromptText = "PS >";
                OnPropertyChanged(nameof(ConsolePromptText));
                return;
            }

            if (!_liveConsoleService.IsSessionRunning || runtime is null)
            {
                ConsoleSessionText = "ConPTY terminal: not started";
                _consolePromptText = "PS >";
                OnPropertyChanged(nameof(ConsolePromptText));
                return;
            }

            var directoryText = string.IsNullOrWhiteSpace(currentDirectory) ? "startup directory unavailable" : currentDirectory;
            var activityText = _liveConsoleService.IsCommandInProgress ? "busy" : "idle";
            ConsoleSessionText = $"ConPTY terminal: {runtime.DisplayName} running ({activityText}, {directoryText})";
            _consolePromptText = string.IsNullOrWhiteSpace(currentDirectory) ? "PS >" : $"PS {currentDirectory}>";
            OnPropertyChanged(nameof(ConsolePromptText));
        }

        private async Task OnRefreshWorkspaceAsync()
        {
            if (!HasWorkspaceLoaded)
            {
                StatusText = "No workspace folder open";
                return;
            }

            await ReloadWorkspaceItemsAsync(logOperation: true);
        }

        private async Task OnBrowseWorkspaceFolderAsync()
        {
            var folderPath = _userPromptService.ShowOpenFolderDialog();

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                StatusText = "Workspace folder selection canceled";
                return;
            }

            await LoadWorkspaceFolderAsync(folderPath);
        }

        private void OnShowWorkspaceFolderInExplorer()
        {
            var explorerTarget = ResolveExplorerTarget();

            if (explorerTarget is null)
            {
                StatusText = "No workspace folder available";
                return;
            }

            try
            {
                Process.Start(explorerTarget);

                StatusText = explorerTarget.Arguments.StartsWith("/select,", StringComparison.OrdinalIgnoreCase)
                    ? "Selected item in Windows Explorer"
                    : $"Opened folder: {explorerTarget.FileName}";

                AppendOutputLine(explorerTarget.Arguments.StartsWith("/select,", StringComparison.OrdinalIgnoreCase)
                    ? $"Selected item in Windows Explorer: {explorerTarget.Arguments}"
                    : $"Opened folder: {explorerTarget.FileName}");
            }
            catch (Exception ex)
            {
                StatusText = "Show in Explorer failed";
                AppendOutputLine($"Show in Explorer failed: {ex.Message}");
            }
        }

        private ProcessStartInfo? ResolveExplorerTarget()
        {
            if (SelectedWorkspaceItem is not null)
            {
                if (SelectedWorkspaceItem.IsDirectory)
                {
                    return new ProcessStartInfo
                    {
                        FileName = SelectedWorkspaceItem.FullPath,
                        UseShellExecute = true
                    };
                }

                return new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{SelectedWorkspaceItem.FullPath}\"",
                    UseShellExecute = true
                };
            }

            if (!string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath))
            {
                return new ProcessStartInfo
                {
                    FileName = _currentWorkspaceFolderPath,
                    UseShellExecute = true
                };
            }

            return null;
        }

        private async Task ReloadWorkspaceItemsAsync(bool logOperation)
        {
            var workspacePath = _currentWorkspaceFolderPath;
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                CancelPendingWorkspaceFilter();
                IsWorkspaceLoading = false;
                WorkspaceItems = new ObservableCollection<WorkspaceTreeItemViewModel>();
                SelectedWorkspaceItem = null;
                _workspaceAllItems = Array.Empty<WorkspaceItem>();
                _workspaceWarnings = Array.Empty<string>();
                _workspaceFileCount = 0;
                _workspaceFolderCount = 0;
                RaiseWorkspaceCountsChanged();
                WorkspaceText = "Workspace: none";
                OnPropertyChanged(nameof(CurrentWorkspaceText));
                OnPropertyChanged(nameof(SelectedWorkspacePathText));
                return;
            }

            CancelPendingWorkspaceFilter();

            var previousReloadCts = Interlocked.Exchange(ref _workspaceReloadCancellationTokenSource, null);
            previousReloadCts?.Cancel();
            previousReloadCts?.Dispose();

            var reloadCts = new CancellationTokenSource();
            _workspaceReloadCancellationTokenSource = reloadCts;

            var generation = Interlocked.Increment(ref _workspaceReloadGeneration);
            var workspaceStopwatch = Stopwatch.StartNew();
            var filterText = string.IsNullOrWhiteSpace(_workspaceFilterText) ? null : _workspaceFilterText.Trim();
            var recursive = !string.IsNullOrWhiteSpace(filterText);

            IsWorkspaceLoading = true;

            PostToUi(() =>
            {
                WorkspaceText = $"Workspace: {workspacePath}";
                StatusText = recursive
                    ? $"Searching workspace for '{filterText}'..."
                    : "Loading workspace...";
                OnPropertyChanged(nameof(WorkspaceLoadingText));
            });

            StartupTimingLogger.Log("MainWindowViewModel", $"Workspace load started for '{workspacePath}' with filter '{filterText}' (recursive={recursive}).");

            try
            {
                var loadResult = await Task.Run(
                    () => _workspaceFolderService.GetWorkspaceItems(workspacePath, filterText, recursive, reloadCts.Token),
                    reloadCts.Token).ConfigureAwait(false);

                StartupTimingLogger.Log("MainWindowViewModel", $"Workspace enumeration completed in {workspaceStopwatch.ElapsedMilliseconds} ms for '{workspacePath}'.");

                if (generation != _workspaceReloadGeneration)
                {
                    StartupTimingLogger.Log("MainWindowViewModel", $"Discarded stale workspace results for '{workspacePath}'.");
                    return;
                }

                if (!string.Equals(workspacePath, _currentWorkspaceFolderPath, StringComparison.OrdinalIgnoreCase))
                {
                    StartupTimingLogger.Log("MainWindowViewModel", $"Workspace path changed before results were applied. Skipping '{workspacePath}'.");
                    return;
                }

                _workspaceAllItems = loadResult.Items;
                _workspaceWarnings = loadResult.Warnings;

                await ApplyWorkspaceFilterAsync(filterText, workspacePath, generation, logOperation, initialLoad: true, cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                StartupTimingLogger.Log("MainWindowViewModel", $"Workspace load cancelled after {workspaceStopwatch.ElapsedMilliseconds} ms for '{workspacePath}'.");
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("MainWindowViewModel", $"Workspace load failed after {workspaceStopwatch.ElapsedMilliseconds} ms for '{workspacePath}': {ex}");
                PostToUi(() =>
                {
                    StatusText = "Workspace load failed";
                    AppendOutputLine($"Workspace load failed: {ex.Message}");
                });
            }
            finally
            {
                var original = Interlocked.CompareExchange(ref _workspaceReloadCancellationTokenSource, null, reloadCts);
                if (ReferenceEquals(original, reloadCts))
                {
                    reloadCts.Dispose();
                }

                PostToUi(() =>
                {
                    if (generation == _workspaceReloadGeneration &&
                        string.Equals(workspacePath, _currentWorkspaceFolderPath, StringComparison.OrdinalIgnoreCase))
                    {
                        IsWorkspaceLoading = false;
                        OnPropertyChanged(nameof(WorkspaceLoadingText));
                    }
                });
            }
        }

        public async Task LoadWorkspaceChildrenAsync(WorkspaceTreeItemViewModel parentItem)
        {
            if (parentItem is null || !parentItem.TryBeginChildLoad())
            {
                return;
            }

            var workspacePath = _currentWorkspaceFolderPath;
            if (string.IsNullOrWhiteSpace(workspacePath))
            {
                parentItem.CompleteChildLoadWithoutChanges();
                return;
            }

            try
            {
                var loadResult = await Task.Run(
                    () => _workspaceFolderService.GetWorkspaceChildItems(workspacePath, parentItem.FullPath, null, CancellationToken.None))
                    .ConfigureAwait(false);

                PostToUi(() =>
                {
                    parentItem.SetChildren(loadResult.Items);
                    if (loadResult.HasWarnings)
                    {
                        foreach (var warning in loadResult.Warnings)
                        {
                            AppendOutputLine($"Workspace warning: {warning}");
                        }
                    }

                    UpdateWorkspaceCounts();
                });
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    parentItem.CompleteChildLoadWithoutChanges();
                    AppendOutputLine($"Workspace child load failed: {ex.Message}");
                });
            }
        }

        private void ScheduleWorkspaceFilterRefresh()
        {
            if (!HasWorkspaceLoaded)
            {
                return;
            }

            CancelPendingWorkspaceFilter();

            var cancellationTokenSource = new CancellationTokenSource();
            _workspaceFilterDelayCancellationTokenSource = cancellationTokenSource;
            var filterTextSnapshot = _workspaceFilterText;

            PostToUi(() =>
            {
                IsWorkspaceLoading = true;
                StatusText = string.IsNullOrWhiteSpace(filterTextSnapshot)
                    ? "Restoring full workspace view..."
                    : $"Searching workspace for: {filterTextSnapshot}";
                OnPropertyChanged(nameof(WorkspaceLoadingText));
            });

            _ = ApplyWorkspaceFilterAfterDelayAsync(cancellationTokenSource.Token);
        }

        private async Task ApplyWorkspaceFilterAfterDelayAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource? ownedSource = null;

            try
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);

                ownedSource = Interlocked.Exchange(ref _workspaceFilterDelayCancellationTokenSource, null);
                await ReloadWorkspaceItemsAsync(logOperation: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                ownedSource?.Dispose();

                var currentSource = _workspaceFilterDelayCancellationTokenSource;
                if (currentSource is not null && currentSource.Token == cancellationToken)
                {
                    Interlocked.Exchange(ref _workspaceFilterDelayCancellationTokenSource, null)?.Dispose();
                }
            }
        }

        private async Task ApplyWorkspaceFilterAsync(
            string? filterText,
            string workspacePath,
            int reloadGeneration,
            bool logOperation,
            bool initialLoad,
            CancellationToken cancellationToken)
        {
            var filterGeneration = Interlocked.Increment(ref _workspaceFilterGeneration);
            var normalizedFilter = string.IsNullOrWhiteSpace(filterText) ? null : filterText.Trim();
            _ = initialLoad;

            var filteredItems = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return FilterWorkspaceItems(_workspaceAllItems, normalizedFilter, cancellationToken);
            }, cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            if (reloadGeneration != _workspaceReloadGeneration)
            {
                return;
            }

            if (filterGeneration != _workspaceFilterGeneration)
            {
                return;
            }

            if (!string.Equals(workspacePath, _currentWorkspaceFolderPath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(normalizedFilter ?? string.Empty, (_workspaceFilterText ?? string.Empty).Trim(), StringComparison.Ordinal))
            {
                return;
            }

            PostToUi(() =>
            {
                // Build view-models into a list first, then assign the collection in one shot.
                // This fires a single PropertyChanged("WorkspaceItems") instead of one
                // CollectionChanged per item, eliminating incremental TreeView renders.
                var viewModels = new List<WorkspaceTreeItemViewModel>(filteredItems.Count);
                foreach (var item in filteredItems)
                    viewModels.Add(new WorkspaceTreeItemViewModel(item));
                WorkspaceItems = new ObservableCollection<WorkspaceTreeItemViewModel>(viewModels);

                SelectedWorkspaceItem = null;
                UpdateWorkspaceCounts();

                WorkspaceText = $"Workspace: {workspacePath}";

                var hasFilter = !string.IsNullOrWhiteSpace(normalizedFilter);
                var workspaceName = Path.GetFileName(workspacePath);
                var warningSuffix = _workspaceWarnings.Count > 0
                    ? $" ({_workspaceWarnings.Count} path issue{(_workspaceWarnings.Count == 1 ? string.Empty : "s")} skipped)"
                    : string.Empty;

                if (WorkspaceItems.Count == 0)
                {
                    StatusText = hasFilter
                        ? $"Workspace filter returned no matches: {normalizedFilter}{warningSuffix}"
                        : $"Workspace loaded: {workspaceName} (no visible files or folders){warningSuffix}";
                }
                else if (hasFilter)
                {
                    StatusText = $"Workspace filtered: {normalizedFilter}{warningSuffix}";
                }
                else
                {
                    StatusText = $"Workspace loaded: {workspaceName}{warningSuffix}";
                }

                var shouldLogWarnings = logOperation || _workspaceWarnings.Count > 0;

                if (logOperation)
                {
                    var filterDescription = hasFilter
                        ? $"filter '{normalizedFilter}'"
                        : "no filter";

                    AppendOutputLine($"{workspacePath} loaded as workspace ({filterDescription})");
                }

                if (shouldLogWarnings)
                {
                    foreach (var warning in _workspaceWarnings)
                    {
                        AppendOutputLine($"Workspace warning: {warning}");
                    }
                }

                OnPropertyChanged(nameof(CurrentWorkspaceText));
                OnPropertyChanged(nameof(SelectedWorkspacePathText));
                IsWorkspaceLoading = false;
                OnPropertyChanged(nameof(WorkspaceLoadingText));
            });
        }

        private void CancelPendingWorkspaceFilter()
        {
            var cancellationTokenSource = Interlocked.Exchange(ref _workspaceFilterDelayCancellationTokenSource, null);
            if (cancellationTokenSource is null)
            {
                return;
            }

            try
            {
                cancellationTokenSource.Cancel();
                cancellationTokenSource.Dispose();
            }
            catch
            {
                // Best effort only.
            }
        }

        private static IReadOnlyList<WorkspaceItem> FilterWorkspaceItems(
            IReadOnlyList<WorkspaceItem> sourceItems,
            string? filterText,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(filterText))
            {
                return sourceItems;
            }

            var filteredItems = new List<WorkspaceItem>();

            foreach (var item in sourceItems)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var filteredItem = FilterWorkspaceItem(item, filterText, cancellationToken);
                if (filteredItem is not null)
                {
                    filteredItems.Add(filteredItem);
                }
            }

            return filteredItems;
        }

        private static WorkspaceItem? FilterWorkspaceItem(WorkspaceItem item, string filterText, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!item.IsDirectory)
            {
                return WorkspaceItemMatchesFilter(item, filterText) ? item : null;
            }

            var matchingChildren = new List<WorkspaceItem>();
            foreach (var child in item.Children)
            {
                var filteredChild = FilterWorkspaceItem(child, filterText, cancellationToken);
                if (filteredChild is not null)
                {
                    matchingChildren.Add(filteredChild);
                }
            }

            if (matchingChildren.Count == 0 && !WorkspaceItemMatchesFilter(item, filterText))
            {
                return null;
            }

            return new WorkspaceItem(item.Name, item.FullPath, item.RelativePath, isDirectory: true, children: matchingChildren);
        }

        private static bool WorkspaceItemMatchesFilter(WorkspaceItem item, string filterText)
        {
            return item.Name.Contains(filterText, StringComparison.OrdinalIgnoreCase)
                || item.RelativePath.Contains(filterText, StringComparison.OrdinalIgnoreCase);
        }

        private void UpdateWorkspaceCounts()
        {
            _workspaceFileCount = 0;
            _workspaceFolderCount = 0;

            foreach (var item in WorkspaceItems)
            {
                CountWorkspaceItems(item);
            }

            RaiseWorkspaceCountsChanged();
        }

        private void CountWorkspaceItems(WorkspaceTreeItemViewModel item)
        {
            if (item.IsPlaceholder)
            {
                return;
            }

            if (item.IsDirectory)
            {
                _workspaceFolderCount++;

                foreach (var child in item.Children)
                {
                    CountWorkspaceItems(child);
                }

                return;
            }

            _workspaceFileCount++;
        }

        private void RaiseWorkspaceCountsChanged()
        {
            OnPropertyChanged(nameof(WorkspaceFileCountText));
            OnPropertyChanged(nameof(WorkspaceFolderCountText));
        }

        // -------------------------------------------------------------------------
        // Execution completion event handlers (1A)
        // -------------------------------------------------------------------------

        private void OnTerminalSessionStarted(int generation)
        {
            _beginTerminalOutputGenerationSink?.Invoke(generation);
            var previousGeneration = Volatile.Read(ref _currentTerminalGeneration);
            if (generation < previousGeneration)
            {
                AppLogger.Warning("Terminal", $"Ignored stale terminal-session start for focus restoration. Generation={generation}, CurrentGeneration={previousGeneration}.");
                DeveloperDiagnostics.LogDecision(
                    "Terminal",
                    "ResetConsoleFocusRestore",
                    "Ignored a stale terminal-session start callback.",
                    "StaleGeneration",
                    new Dictionary<string, object?>
                    {
                        ["generation"] = generation,
                        ["currentGeneration"] = previousGeneration
                    });
                return;
            }

            Volatile.Write(ref _currentTerminalGeneration, generation);
            var bound = _terminalFocusRestorePolicy.BindReplacementGeneration(generation);
            if (bound)
            {
                AppLogger.Info("Terminal", $"Reset Console focus intent bound to replacement terminal generation {generation}.");
                DeveloperDiagnostics.LogStateTransition(
                    "Terminal",
                    "ResetConsoleFocusRestore",
                    "FocusIntentPending",
                    "ReplacementGenerationBound",
                    "Replacement terminal session started.",
                    new Dictionary<string, object?> { ["replacementGeneration"] = generation });
            }
        }

        private void OnTerminalSessionStopping(int generation)
        {
            _invalidateTerminalOutputGenerationSink?.Invoke(generation);
        }

        private void OnTerminalCommandCompleted()
        {
            // Fired on a thread-pool thread by LiveConsoleService when the sentinel token
            // is detected in terminal output.  Marshal to the UI thread before touching
            // UI-bound state.
            PostToUi(() =>
            {
                IsExecutionRunning = false;
                RefreshCommandStates();
                UpdateConsoleSessionPresentation();
            });

            RequestTerminalInteractiveStateNormalization("CommandCompleted");

            if (Volatile.Read(ref _resetConsoleInProgress) == 0)
            {
                RequestTerminalFocusAfterExecutionCompletion();
            }
        }

        private void OnSessionTerminated()
        {
            // The pwsh.exe process exited unexpectedly or the user/script called exit.
            // Ensure Run/Reset recover even if the helper sentinel was never echoed.
            PostToUi(() =>
            {
                var hadExecutionState = IsExecutionRunning;
                var hadStopState = IsStopInProgress;

                IsExecutionRunning = false;
                IsStopInProgress = false;

                StatusText = "PowerShell terminal exited. Use Reset Console to start a new session.";
                AppLogger.Info(
                    "Console",
                    $"PowerShell terminal session termination observed by ViewModel. Cleared execution state. HadExecutionState={hadExecutionState}, HadStopState={hadStopState}, SessionRunning={_liveConsoleService.IsSessionRunning}, CommandInProgress={_liveConsoleService.IsCommandInProgress}.");

                RefreshCommandStates();
                UpdateConsoleSessionPresentation();
            });

            if (Volatile.Read(ref _resetConsoleInProgress) == 0)
            {
                RequestTerminalFocusAfterExecutionCompletion();
            }
        }

        private void RequestTerminalFocusAfterReset(int generation, string source)
        {
            if (!_preparedTerminalFocusIntent.IsRequested)
            {
                return;
            }

            var readiness = _terminalFocusRestoreReadinessSink?.Invoke() ??
                new TerminalFocusRestoreReadiness(
                    RendererReady: false,
                    ConsoleVisible: false,
                    ApplicationActive: false,
                    ModalDialogOpen: false);
            var decision = _terminalFocusRestorePolicy.TryBeginFocusAttempt(generation, readiness);
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ResetConsoleFocusRestore",
                "Evaluated one-time focus restoration for the replacement terminal.",
                decision.ToString(),
                new Dictionary<string, object?>
                {
                    ["source"] = source,
                    ["generation"] = generation,
                    ["rendererReady"] = readiness.RendererReady,
                    ["consoleVisible"] = readiness.ConsoleVisible,
                    ["applicationActive"] = readiness.ApplicationActive,
                    ["modalDialogOpen"] = readiness.ModalDialogOpen
                });

            if (decision == TerminalFocusRestoreDecision.Restore)
            {
                AppLogger.Info("Terminal", $"Starting verified terminal focus restoration after Reset Console. Generation={generation}, Source={source}.");
                _ = RestoreTerminalFocusAfterResetAsync(generation, source);
                return;
            }

            if (decision is TerminalFocusRestoreDecision.WaitingForReplacementGeneration or
                TerminalFocusRestoreDecision.RendererNotReady or
                TerminalFocusRestoreDecision.StaleGeneration)
            {
                return;
            }

            _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
            AppLogger.Info("Terminal", $"Skipped Reset Console terminal focus restoration. Decision={decision}, Generation={generation}, Source={source}.");
        }

        private async Task RestoreTerminalFocusAfterResetAsync(int generation, string source)
        {
            var restoreFocus = _restoreTerminalFocusSink;
            if (restoreFocus is null)
            {
                _focusTerminalSink?.Invoke();
                _terminalFocusRestorePolicy.CompleteFocusAttempt(generation, succeeded: true);
                _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
                return;
            }

            try
            {
                var result = await restoreFocus(generation, CancellationToken.None).ConfigureAwait(false);
                var retryAllowed = _terminalFocusRestorePolicy.CompleteFocusAttempt(generation, result.Succeeded);
                LogTerminalFocusRestoreResult(generation, source, attempt: 1, result, retryAllowed);
                if (result.Succeeded)
                {
                    _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
                    return;
                }

                if (!retryAllowed)
                {
                    CancelPendingTerminalFocusRestore("BrowserFocusVerificationFailed");
                    return;
                }

                // The first focus operation already ran at the host input priority. Yielding
                // once lets WebView2 apply that activation before the bounded same-generation retry.
                await Task.Yield();
                var readiness = _terminalFocusRestoreReadinessSink?.Invoke();
                var retryDecision = readiness is null
                    ? TerminalFocusRestoreDecision.NoPendingIntent
                    : _terminalFocusRestorePolicy.TryBeginFocusAttempt(generation, readiness.Value);
                if (retryDecision != TerminalFocusRestoreDecision.Restore)
                {
                    AppLogger.Info("Terminal", $"Skipped retrying Reset Console focus restoration. Decision={retryDecision}, Generation={generation}.");
                    return;
                }

                var retryResult = await restoreFocus(generation, CancellationToken.None).ConfigureAwait(false);
                _terminalFocusRestorePolicy.CompleteFocusAttempt(generation, retryResult.Succeeded);
                LogTerminalFocusRestoreResult(generation, source, attempt: 2, retryResult, retryAllowed: false);
                if (retryResult.Succeeded)
                {
                    _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
                }
                else
                {
                    CancelPendingTerminalFocusRestore("BrowserFocusVerificationFailedAfterRetry");
                }
            }
            catch (Exception ex)
            {
                _terminalFocusRestorePolicy.CompleteFocusAttempt(generation, succeeded: false);
                DeveloperDiagnostics.LogException(
                    "Terminal",
                    ex,
                    "Verified Reset Console terminal focus restoration failed.",
                    new Dictionary<string, object?> { ["generation"] = generation, ["source"] = source });
                CancelPendingTerminalFocusRestore("BrowserFocusException");
            }
        }

        private static void LogTerminalFocusRestoreResult(
            int generation,
            string source,
            int attempt,
            TerminalFocusRestoreResult result,
            bool retryAllowed)
        {
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ResetConsoleFocusRestore",
                "Verified WPF, WebView2, and xterm focus result.",
                result.Succeeded ? "Succeeded" : "Failed",
                new Dictionary<string, object?>
                {
                    ["generation"] = generation,
                    ["source"] = source,
                    ["attempt"] = attempt,
                    ["wpfHostFocused"] = result.WpfHostFocused,
                    ["webViewFocused"] = result.WebViewFocused,
                    ["browserFocusCommandExecuted"] = result.BrowserFocusCommandExecuted,
                    ["xtermInputActive"] = result.XtermInputActive,
                    ["activeElement"] = result.ActiveElement,
                    ["failureReason"] = result.FailureReason,
                    ["retryAllowed"] = retryAllowed
                });
        }

        private void CancelPendingTerminalFocusRestore(string reason)
        {
            if (!_terminalFocusRestorePolicy.Cancel())
            {
                return;
            }

            _preparedTerminalFocusIntent = TerminalFocusRestoreIntent.None;
            AppLogger.Info("Terminal", $"Canceled pending Reset Console terminal focus restoration. Reason={reason}.");
            DeveloperDiagnostics.LogDecision(
                "Terminal",
                "ResetConsoleFocusIntent",
                "Canceled pending terminal focus restoration.",
                reason);
        }

        /// <summary>
        /// Repaints xterm's cursor layer after a completed or interrupted command without
        /// moving keyboard focus away from the editor. The delayed repeats cover the final
        /// ConPTY prompt/output chunk reaching WebView2 after the lifecycle event.
        /// </summary>
        private void RequestTerminalInteractiveStateNormalization(string source)
        {
            var normalizeTerminal = _normalizeTerminalInteractiveStateSink;
            if (normalizeTerminal is null)
            {
                return;
            }

            PostToUi(normalizeTerminal);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(75).ConfigureAwait(false);
                    PostToUi(normalizeTerminal);

                    await Task.Delay(175).ConfigureAwait(false);
                    PostToUi(normalizeTerminal);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("Console", $"Deferred terminal interactive-state normalization failed. Source={source}, Reason={ex.Message}");
                }
            });
        }

        /// <summary>
        /// Restores keyboard focus/caret visibility after an editor-launched command completes.
        /// The ConPTY process can return to a prompt before the WebView/xterm focus layer has
        /// processed the final output chunk, so a single immediate focus request can be lost.
        /// A short staggered refocus sequence keeps the dispatch path unchanged while making
        /// the completed prompt visibly ready for typing.
        /// </summary>
        private void RequestTerminalFocusAfterExecutionCompletion()
        {
            var focusTerminal = _focusTerminalSink;
            if (focusTerminal is null)
            {
                return;
            }

            PostToUi(focusTerminal);

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(75).ConfigureAwait(false);
                    PostToUi(focusTerminal);

                    await Task.Delay(175).ConfigureAwait(false);
                    PostToUi(focusTerminal);
                }
                catch (Exception ex)
                {
                    AppLogger.Debug("Console", $"Deferred terminal focus restoration failed after execution completion. Reason={ex.Message}");
                }
            });
        }

        // -------------------------------------------------------------------------
        // Execution progress timer helpers (4C)
        // -------------------------------------------------------------------------

        private void StartProgressTimer()
        {
            _executionStartTime = DateTime.Now;
            ExecutionProgressText = "Running 0s";

            _progressTimer?.Stop();
            _progressTimer?.Dispose();
            var timer = new System.Timers.Timer(1000) { AutoReset = true };
            timer.Elapsed += (_, _) =>
            {
                var elapsed = DateTime.Now - _executionStartTime;
                PostToUi(() => ExecutionProgressText = $"Running {(int)elapsed.TotalSeconds}s");
            };
            timer.Start();
            _progressTimer = timer;
        }

        private void StopProgressTimer()
        {
            _progressTimer?.Stop();
            _progressTimer?.Dispose();
            _progressTimer = null;
            ExecutionProgressText = string.Empty;
        }

        // -------------------------------------------------------------------------
        // Command history helpers (4A)
        // -------------------------------------------------------------------------

        private void AddToCommandHistory(string command)
        {
            if (string.IsNullOrWhiteSpace(command))
            {
                return;
            }

            // Remove duplicate (case-sensitive) then prepend, capped at 200 entries.
            _commandHistory.RemoveAll(h => string.Equals(h, command, StringComparison.Ordinal));
            _commandHistory.Insert(0, command);
            if (_commandHistory.Count > 200)
            {
                _commandHistory.RemoveAt(_commandHistory.Count - 1);
            }

            _commandHistoryIndex = -1;
        }

        private void AppendExecutionOutput(ExecutionOutputRecord record)
        {
            PostToUi(() =>
            {
                if (record.StreamKind == ExecutionOutputStreamKind.Lifecycle &&
                    string.Equals(record.Text, "__PSSTUDIO_CLEAR_TERMINAL__", StringComparison.Ordinal))
                {
                    // Delegate to the terminal control if wired; otherwise clear the
                    // fallback TerminalDisplayText buffer.
                    if (_clearTerminalSink is not null)
                        _clearTerminalSink();
                    else
                        TerminalDisplayText = string.Empty;
                    return;
                }

                if (record.StreamKind == ExecutionOutputStreamKind.Lifecycle)
                {
                    AppLogger.Info("Console", record.Text);

                    if (ShouldSurfaceLifecycleMessageToUser(record.Text))
                    {
                        StatusText = record.Text;
                        AppendApplicationActivityFragmentCore(
                            $"{ApplicationBranding.PublicName}: {record.Text}{Environment.NewLine}");
                        DeveloperDiagnostics.LogDecision(
                            "Console",
                            "LifecycleMessageRouting",
                            "Non-routine terminal lifecycle information was routed outside xterm.js.",
                            "StatusBarAndActivityPane",
                            new Dictionary<string, object?>
                            {
                                ["messageLength"] = record.Text?.Length ?? 0,
                                ["terminalNotificationEnabled"] = false
                            });
                    }

                    // Refresh session state on lifecycle events only (session start, stop,
                    // exit).  Calling this on every stdout chunk would raise CanExecuteChanged
                    // on five commands for each line of terminal output — far too expensive.
                    UpdateConsoleSessionPresentation();
                }
                // Non-lifecycle stdout is no longer routed here: it now arrives via
                // LiveConsoleService.RawOutputReceived → TerminalControl.WriteRaw (xterm.js).
                // The else branch is kept as a no-op in case of future fallback needs.
            });
        }

        private static bool ShouldSurfaceLifecycleMessageToUser(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            var message = text.Trim();

            // Routine startup/status events are logged but should not appear in the
            // visible terminal. A normal PowerShell console starts with PowerShell's
            // own banner/prompt/output, not app-host lifecycle chatter.
            if (message.StartsWith("ConPTY terminal session started", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Non-routine lifecycle events should remain visible because they directly
            // affect the user's interactive session.
            return message.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("fallback", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("exited", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("stopped unexpectedly", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("restarted", StringComparison.OrdinalIgnoreCase) ||
                   message.StartsWith("Running script", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("still running", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("no console output", StringComparison.OrdinalIgnoreCase);
        }

        private void AppendOutputLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            PostToUi(() => AppendApplicationActivityFragmentCore(text + Environment.NewLine));
        }

        public void AppendDebugOutput(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                                 .Replace("\r", "\n", StringComparison.Ordinal);

            foreach (var line in normalized.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                AppendDebuggerOutputFragment($"[debug] {line.TrimEnd()}{Environment.NewLine}");
            }
        }

        public void ClearDebugOutput()
        {
            PostToUi(() => DebuggerOutputText = string.Empty);
        }

        private void AppendApplicationActivityFragmentCore(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            const int maxBufferLength = 500000;
            var next = _applicationActivityText + text;
            if (next.Length > maxBufferLength)
                next = next[^maxBufferLength..];
            ApplicationActivityText = next;
        }

        private void AppendDebuggerOutputFragment(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            PostToUi(() =>
            {
                const int maxBufferLength = 500000;
                var next = _debuggerOutputText + text;
                if (next.Length > maxBufferLength)
                    next = next[^maxBufferLength..];
                DebuggerOutputText = next;
            });
        }

        private void PostToUi(Action action)
        {
            if (_uiSynchronizationContext is null)
            {
                action();
                return;
            }

            _uiSynchronizationContext.Post(_ => action(), null);
        }

        private bool CanRunScript()
        {
            return SelectedTab is not null &&
                   (SelectedRuntimeItem is not null || _preferredRuntimeItem is not null) &&
                   !IsExecutionRunning &&
                   !_liveConsoleService.IsCommandInProgress &&
                   !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   !IsDebugSessionActive;
        }

        private bool CanExportAsExe()
        {
            return SelectedTab is not null &&
                   !IsExecutionRunning &&
                   !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   !_isExeExportInProgress;
        }

        private bool CanStopScript()
        {
            return _liveConsoleService.IsSessionRunning && _liveConsoleService.IsCommandInProgress && !IsStopInProgress && !IsRuntimeDiscoveryInProgress;
        }

        private bool CanRefreshRuntimes()
        {
            return !IsRuntimeDiscoveryInProgress && !IsExecutionRunning && !IsStopInProgress;
        }

        private bool CanExecuteConsoleCommand()
        {
            return !IsExecutionRunning &&
                   !_liveConsoleService.IsCommandInProgress &&
                   !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   !string.IsNullOrWhiteSpace(ConsoleCommandText) &&
                   (SelectedRuntimeItem is not null || _preferredRuntimeItem is not null);
        }

        private bool CanRestartConsole()
        {
            return !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   (SelectedRuntimeItem is not null || _preferredRuntimeItem is not null);
        }

        public void RefreshCommandStates()
        {
            _closeAllTabsCommand.RaiseCanExecuteChanged();
            _runCommand.RaiseCanExecuteChanged();
            _stopCommand.RaiseCanExecuteChanged();
            _refreshRuntimesCommand.RaiseCanExecuteChanged();
            _sendConsoleCommand.RaiseCanExecuteChanged();
            _restartConsoleCommand.RaiseCanExecuteChanged();
            _exportAsExeCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(IsRunAvailable));
        }

        private static string GetApplicationVersionText()
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            var informationalVersion = entryAssembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            var displayVersion = NormalizeApplicationVersionForDisplay(informationalVersion);
            if (!string.IsNullOrWhiteSpace(displayVersion))
            {
                return $"v{displayVersion}";
            }

            var fileVersion = entryAssembly?
                .GetCustomAttribute<AssemblyFileVersionAttribute>()?
                .Version;

            displayVersion = NormalizeApplicationVersionForDisplay(fileVersion);
            if (!string.IsNullOrWhiteSpace(displayVersion))
            {
                return $"v{displayVersion}";
            }

            var version = entryAssembly?.GetName().Version;
            if (version is null)
            {
                return "v0.0.0";
            }

            var major = Math.Max(version.Major, 0);
            var minor = Math.Max(version.Minor, 0);
            var patch = Math.Max(version.Build, 0);
            return $"v{major}.{minor}.{patch}";
        }

        private static string NormalizeApplicationVersionForDisplay(string? versionText)
        {
            if (string.IsNullOrWhiteSpace(versionText))
            {
                return string.Empty;
            }

            var trimmed = versionText.Trim();
            var metadataStartIndex = trimmed.IndexOfAny(new[] { '+', '-', ' ' });
            if (metadataStartIndex >= 0)
            {
                trimmed = trimmed[..metadataStartIndex];
            }

            if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed[1..];
            }

            var versionParts = trimmed.Split('.');
            var displayParts = new List<string>(capacity: 3);

            foreach (var part in versionParts)
            {
                if (displayParts.Count == 3)
                {
                    break;
                }

                if (!int.TryParse(part.Trim(), out var numericPart))
                {
                    break;
                }

                displayParts.Add(Math.Max(numericPart, 0).ToString());
            }

            if (displayParts.Count == 0)
            {
                return string.Empty;
            }

            while (displayParts.Count < 3)
            {
                displayParts.Add("0");
            }

            return string.Join(".", displayParts);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
