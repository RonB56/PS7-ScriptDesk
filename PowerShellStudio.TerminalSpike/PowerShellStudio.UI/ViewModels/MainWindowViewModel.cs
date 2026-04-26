using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
// System.Windows.Threading removed — DispatcherTimer not available in UI project (no WPF ref).
using PowerShellStudio.Application.Utilities;
using PowerShellStudio.Application.Diagnostics;
using PowerShellStudio.Application.Interfaces;
using PowerShellStudio.Domain.Models;
using PowerShellStudio.UI.Commands;

namespace PowerShellStudio.UI.ViewModels
{
    public class MainWindowViewModel : INotifyPropertyChanged
    {
        private readonly IFileDocumentService _fileDocumentService;
        private readonly IRuntimeService _runtimeService;
        private readonly ILiveConsoleService _liveConsoleService;
        private readonly IWorkspaceFolderService _workspaceFolderService;
        private readonly IUserPromptService _userPromptService;
        private readonly IExeExportService _exeExportService;
        private SynchronizationContext? _uiSynchronizationContext;

        private readonly RelayCommand _runCommand;
        private readonly RelayCommand _stopCommand;
        private readonly RelayCommand _refreshRuntimesCommand;
        private readonly RelayCommand _sendConsoleCommand;
        private readonly RelayCommand _restartConsoleCommand;
        private readonly RelayCommand _exportAsExeCommand;
        private readonly RelayCommand _zoomInCommand;
        private readonly RelayCommand _zoomOutCommand;
        private readonly RelayCommand _resetZoomCommand;

        private readonly string _applicationVersionText;

        private string _runtimeText;
        private string _statusText;
        private string _terminalDisplayText;

        // Sinks registered by the Shell layer to route output to the xterm.js control.
        // When set, AppendTerminalTextFragment writes here instead of TerminalDisplayText.
        private Action<string>? _writeTextSink;
        private Action?         _clearTerminalSink;
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
            ApplicationSettings? initialSettings = null)
        {
            _fileDocumentService = fileDocumentService;
            _runtimeService = runtimeService;
            _workspaceFolderService = workspaceFolderService;
            _userPromptService = userPromptService;
            _liveConsoleService = liveConsoleService;
            _exeExportService = exeExportService;
            _uiSynchronizationContext = SynchronizationContext.Current;
            _applicationVersionText = GetApplicationVersionText();

            Title = $"PowerShellStudio {_applicationVersionText}";
            WelcomeMessage = "PowerShellStudio shell is running.";
            _runtimeText = "Runtime: Detecting installed PowerShell runtimes...";
            _workspaceText = workspaceService.GetWorkspaceDisplayText();
            _statusText = $"Ready - {_applicationVersionText}";
            _terminalDisplayText =
                $"PowerShellStudio {_applicationVersionText} terminal pane initialized.{Environment.NewLine}" +
                $"This phase now hosts a ConPTY-backed PowerShell terminal process inside the application.{Environment.NewLine}";

            OpenTabs = new ObservableCollection<EditorTabViewModel>();
            DetectedRuntimes = new ObservableCollection<RuntimeItemViewModel>();

            NewScriptCommand = new RelayCommand(OnNewScript);
            CloseTabCommand = new RelayCommand(OnCloseTab);
            _runCommand = new RelayCommand(async () => await OnRunAsync(), CanRunScript);
            RunCommand = _runCommand;
            _stopCommand = new RelayCommand(async () => await OnStopAsync(), CanStopScript);
            StopCommand = _stopCommand;
            AboutCommand = new RelayCommand(OnAbout);
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

            RestorePersistedState(initialSettings);
            TrySeedPersistedRuntimeSelection();

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
                    _selectedRuntimeExecutablePathToRestore = _selectedRuntimeItem?.ExecutablePath;
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

        public string? EffectiveRuntimeExecutablePath => EffectiveRuntimeItem?.ExecutablePath;

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

        public ICommand RunCommand { get; }

        public ICommand StopCommand { get; }

        public ICommand AboutCommand { get; }

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
            StartupTimingLogger.Log("MainWindowViewModel", "Deferred initialization started.");

            try
            {
                var runtimeDiscoveryTask = RefreshRuntimeDiscoveryAsync(logOperation: true, updateStatusText: false);

                if (!string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath) && Directory.Exists(_currentWorkspaceFolderPath))
                {
                    await ReloadWorkspaceItemsAsync(logOperation: false);
                    StartupTimingLogger.Log("MainWindowViewModel", $"Persisted workspace loaded in {startupStopwatch.ElapsedMilliseconds} ms.");
                }

                await runtimeDiscoveryTask.ConfigureAwait(false);
                StartupTimingLogger.Log("MainWindowViewModel", $"Deferred initialization completed in {startupStopwatch.ElapsedMilliseconds} ms.");
            }
            catch (Exception ex)
            {
                StartupTimingLogger.Log("MainWindowViewModel", $"Deferred initialization failed after {startupStopwatch.ElapsedMilliseconds} ms: {ex}");
                PostToUi(() =>
                {
                    StatusText = "Startup initialization failed";
                    AppendOutputLine($"Startup initialization failed: {ex.Message}");
                    UpdateConsoleSessionPresentation();
                });
            }
        }

        public async Task SubmitConsoleCommandAsync()
        {
            await OnExecuteConsoleCommandAsync().ConfigureAwait(false);
        }

        /// <summary>Sends Ctrl+C (ETX) to the ConPTY terminal to interrupt a running script (4B).</summary>
        public async Task SendInterruptAsync()
        {
            if (!_liveConsoleService.IsSessionRunning)
            {
                return;
            }

            try
            {
                await _liveConsoleService.SendInterruptAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best effort — if the process is already gone, ignore.
            }
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
        /// Registers the xterm.js terminal control's output sinks. Called once by
        /// the Shell layer after the TerminalControl is wired up.
        /// </summary>
        public void SetTerminalSinks(Action<string> writeText, Action clearTerminal)
        {
            _writeTextSink      = writeText;
            _clearTerminalSink  = clearTerminal;
        }

        /// <summary>
        /// Subscribes a handler to raw (ANSI-intact) ConPTY output for forwarding
        /// to xterm.js. The handler is called on the thread-pool.
        /// </summary>
        public void SubscribeRawOutput(Action<string> handler)
        {
            _liveConsoleService.RawOutputReceived += handler;
        }

        /// <summary>Unsubscribes a handler previously added via SubscribeRawOutput.</summary>
        public void UnsubscribeRawOutput(Action<string> handler)
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
            catch
            {
                // Best effort — session may be stopped.
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

            // Fire-and-forget the terminal stop.  Blocking on async work from the UI
            // thread (GetAwaiter().GetResult()) risks a deadlock and freezes the window
            // during close.  The process will be killed by the OS when the app exits
            // if the stop does not complete in time.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _liveConsoleService.StopConsoleAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Best effort shutdown only.
                }
            });

            return true;
        }

        public ApplicationSettings CreateApplicationSettingsSnapshot()
        {
            var reopenFilePaths = new List<string>();

            foreach (var tab in OpenTabs)
            {
                if (!string.IsNullOrWhiteSpace(tab.FilePath) && File.Exists(tab.FilePath))
                {
                    reopenFilePaths.Add(tab.FilePath);
                }
            }

            var settings = new ApplicationSettings
            {
                IsExplorerVisible = IsExplorerVisible,
                LastWorkspaceFolderPath = !string.IsNullOrWhiteSpace(_currentWorkspaceFolderPath) && Directory.Exists(_currentWorkspaceFolderPath)
                    ? _currentWorkspaceFolderPath
                    : null,
                SelectedRuntimeExecutablePath = SelectedRuntimeItem?.ExecutablePath ?? _preferredRuntimeItem?.ExecutablePath,
                SelectedTabFilePath = SelectedTab?.FilePath,
                RecentFilePaths = new List<string>(_recentFilePaths),
                ReopenFilePaths = reopenFilePaths
            };

            TrySetOptionalProperty(settings, "Theme", _currentThemeName);
            TrySetOptionalProperty(settings, "EditorZoomLevel", _editorZoomLevel);

            return settings;
        }

        private void RestorePersistedState(ApplicationSettings? settings)
        {
            if (settings is null)
            {
                return;
            }

            IsExplorerVisible = settings.IsExplorerVisible;
            _selectedRuntimeExecutablePathToRestore = NormalizeStoredPath(settings.SelectedRuntimeExecutablePath);

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

            foreach (var reopenFilePath in settings.ReopenFilePaths)
            {
                _ = TryOpenFileFromPathCore(reopenFilePath, addToRecentFiles: false, logOperation: false, out _);
            }

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

            if (OpenTabs.Count > 0)
            {
                StatusText = "Session restored";
            }
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
            RuntimeText = $"Runtime: Checking PowerShell runtime ({runtime.DisplayName})...";
            StatusText = "Checking PowerShell runtime...";
            StartupTimingLogger.Log("MainWindowViewModel", $"Seeded persisted runtime selection in {stopwatch.ElapsedMilliseconds} ms: {runtime.DisplayName} ({runtime.ExecutablePath})");
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
            failureReason = null;

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

                OpenTabs.Add(tab);
                SelectedTab = tab;

                if (addToRecentFiles)
                {
                    AddRecentFilePath(normalizedFilePath);
                }

                OnPropertyChanged(nameof(OpenTabCountText));
                OnPropertyChanged(nameof(ActiveDocumentText));

                if (logOperation)
                {
                    StatusText = $"{title} opened";
                    AppendOutputLine($"{normalizedFilePath} opened");
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

            const int maximumRecentFiles = 15;
            if (_recentFilePaths.Count > maximumRecentFiles)
            {
                _recentFilePaths.RemoveRange(maximumRecentFiles, _recentFilePaths.Count - maximumRecentFiles);
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

        private bool SaveTabCore(EditorTabViewModel tab)
        {
            var normalizedFilePath = NormalizeStoredPath(tab.FilePath);
            if (normalizedFilePath is null)
            {
                StatusText = "Save failed";
                AppendOutputLine("Save failed: the current file path is invalid.");
                AppLogger.Warning("Save", $"Save rejected for {tab.Title} because the current file path was invalid. OriginalPath='{tab.FilePath ?? "<null>"}'.");
                return false;
            }

            try
            {
                _fileDocumentService.WriteAllText(normalizedFilePath, tab.Content);
                if (!string.Equals(tab.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase))
                {
                    tab.SetFilePath(normalizedFilePath);
                    OnPropertyChanged(nameof(ActiveDocumentText));
                }

                tab.MarkSaved();
                AddRecentFilePath(normalizedFilePath);

                StatusText = $"{tab.Title} saved";
                AppendOutputLine($"{normalizedFilePath} saved");

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                StatusText = "Save failed";
                AppendOutputLine($"Save failed: access to {normalizedFilePath} was denied.");
                AppLogger.Error("Save", $"Save failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
            catch (IOException ex)
            {
                StatusText = "Save failed";
                AppendOutputLine($"Save failed: {normalizedFilePath} is locked or unavailable.");
                AppLogger.Error("Save", $"Save failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
            catch (Exception ex)
            {
                StatusText = "Save failed";
                AppendOutputLine($"Save failed: {ex.Message}");
                AppLogger.Error("Save", $"Save failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
        }

        private bool SaveTabAsCore(EditorTabViewModel tab, string filePath)
        {
            var normalizedFilePath = NormalizeStoredPath(NormalizeScriptSavePath(filePath));
            if (normalizedFilePath is null)
            {
                StatusText = "Save As failed";
                AppendOutputLine("Save As failed: the selected file path was invalid.");
                AppLogger.Warning("Save", $"Save As rejected for {tab.Title} because the selected path was invalid. OriginalPath='{filePath ?? "<null>"}'.");
                return false;
            }

            try
            {
                _fileDocumentService.WriteAllText(normalizedFilePath, tab.Content);
                tab.SetFilePath(normalizedFilePath);
                tab.MarkSaved();
                AddRecentFilePath(normalizedFilePath);

                OnPropertyChanged(nameof(ActiveDocumentText));

                StatusText = $"{tab.Title} saved";
                AppendOutputLine($"{normalizedFilePath} saved");

                return true;
            }
            catch (UnauthorizedAccessException ex)
            {
                StatusText = "Save As failed";
                AppendOutputLine($"Save As failed: access to {normalizedFilePath} was denied.");
                AppLogger.Error("Save", $"Save As failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
            catch (IOException ex)
            {
                StatusText = "Save As failed";
                AppendOutputLine($"Save As failed: {normalizedFilePath} is locked or unavailable.");
                AppLogger.Error("Save", $"Save As failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
            catch (Exception ex)
            {
                StatusText = "Save As failed";
                AppendOutputLine($"Save As failed: {ex.Message}");
                AppLogger.Error("Save", $"Save As failed for {tab.Title}; Path={normalizedFilePath}", ex);
                return false;
            }
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
                "# Welcome to PowerShellStudio\r\n\r\n# This is the future editor surface.");

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
            AppendOutputLine($"{title} opened");
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
                AppendOutputLine($"{closingTitle} closed");
                AppendOutputLine($"{newTitle} opened");
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
                AppendOutputLine($"{closingTitle} closed");
            }

            OnPropertyChanged(nameof(OpenTabCountText));
            OnPropertyChanged(nameof(ActiveDocumentText));
            RefreshCommandStates();
        }

        private async Task OnRunAsync()
        {
            if (SelectedTab is null)
            {
                StatusText = "No script tab selected";
                return;
            }

            // Set BEFORE the first await so we are still on the UI thread and the button
            // disables synchronously (no flicker).  The flag is cleared by the sentinel
            // event when the script finishes, or in the catch block if dispatch fails.
            IsExecutionRunning = true;

            var dispatched = false;
            try
            {
                dispatched = await DispatchScriptToTerminalAsync(SelectedTab.Title, SelectedTab.Content, executeInCurrentScope: false).ConfigureAwait(false);
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

        private async Task<bool> DispatchScriptToTerminalAsync(string dispatchTitle, string scriptContent, bool executeInCurrentScope)
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

                await _liveConsoleService.ExecuteScriptAsync(
                    dispatchTitle,
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
                StatusText = "The PowerShell terminal is not currently running";
                return;
            }

            if (!_liveConsoleService.IsCommandInProgress)
            {
                StatusText = "There is no running script or command to stop";
                return;
            }

            StatusText = "Interrupting the current PowerShell operation...";
            AppLogger.Info("Console", "Interrupt requested; sending Ctrl+C to the terminal session.");
            IsStopInProgress = true;

            try
            {
                await _liveConsoleService.SendInterruptAsync().ConfigureAwait(false);

                PostToUi(() =>
                {
                    StatusText = "Interrupt sent to the PowerShell terminal";
                    UpdateConsoleSessionPresentation();
                });
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    StatusText = "Interrupt failed";
                    AppendOutputLine($"Interrupt failed: {ex.Message}");
                    UpdateConsoleSessionPresentation();
                });
            }
            finally
            {
                PostToUi(() =>
                {
                    IsStopInProgress = false;
                    RefreshCommandStates();
                });
            }
        }

        private async Task OnRestartConsoleAsync()
        {
            if (IsExecutionRunning)
            {
                StatusText = "Wait for the current command to finish before restarting the console";
                return;
            }

            var runtimeToUse = EffectiveRuntimeInfo;
            if (runtimeToUse is null)
            {
                StatusText = "No PowerShell runtime is available to start the ConPTY terminal";
                return;
            }

            StatusText = "Restarting PowerShell terminal...";
            AppLogger.Info("Console", $"Restarting PowerShell terminal using {runtimeToUse.DisplayName}.");

            try
            {
                await EnsureConsoleSessionAsync(runtimeToUse, forceRestart: true, logOperation: true).ConfigureAwait(false);
                PostToUi(() => StatusText = $"ConPTY terminal restarted with {runtimeToUse.DisplayName}");
            }
            catch (Exception ex)
            {
                PostToUi(() =>
                {
                    StatusText = "ConPTY terminal restart failed";
                    AppendOutputLine($"ConPTY terminal restart failed: {ex.Message}");
                });
            }
        }

        private void OnAbout()
        {
            StatusText = $"PowerShellStudio {_applicationVersionText} - ConPTY PowerShell terminal host";
            AppendOutputLine($"About requested - running {_applicationVersionText}");
        }

        private async Task OnClearConsoleAsync()
        {
            // Toolbar/menu Clear Output is an app UI action, not a PowerShell command.
            // Clear xterm.js directly so the terminal is not polluted with an injected
            // Clear-Host command or an internal execution sentinel. If the user types
            // cls/Clear-Host themselves, PowerShell still handles that normally.
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

                StatusText = "Terminal output cleared";
            });

            AppLogger.Info("Console", "Terminal output was cleared by the app UI.");
            await Task.CompletedTask.ConfigureAwait(false);
        }

        private async Task OnRefreshRuntimesAsync()
        {
            await RefreshRuntimeDiscoveryAsync(logOperation: true, updateStatusText: true).ConfigureAwait(false);
        }

        private async Task RefreshRuntimeDiscoveryAsync(bool logOperation, bool updateStatusText)
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
            StartupTimingLogger.Log("MainWindowViewModel", "Runtime discovery started.");

            try
            {
                var discoveryResult = await Task.Run(() => _runtimeService.DiscoverRuntimes()).ConfigureAwait(false);
                StartupTimingLogger.Log("MainWindowViewModel", $"Runtime discovery finished in {discoveryStopwatch.ElapsedMilliseconds} ms with {discoveryResult.DetectedRuntimes.Count} detected runtime(s).");

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

                    SelectedRuntimeItem = runtimeToSelect ?? _preferredRuntimeItem ?? (DetectedRuntimes.Count > 0 ? DetectedRuntimes[0] : null);
                    RuntimeText = discoveryResult.SummaryText;

                    OnPropertyChanged(nameof(RuntimeCountText));
                    OnPropertyChanged(nameof(PreferredRuntimeText));
                    OnPropertyChanged(nameof(RuntimeListHeaderText));

                    if (logOperation)
                    {
                        AppendOutputLine("PowerShell runtime discovery complete.");

                        if (_preferredRuntimeItem is null)
                        {
                            AppendOutputLine("No PowerShell runtime was detected.");
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
                            ? "Runtime discovery refreshed - none detected"
                            : $"Runtime discovery refreshed - {_preferredRuntimeItem.DisplayName} preferred";
                    }
                    else if (_preferredRuntimeItem is null)
                    {
                        StatusText = "Runtime discovery completed - no runtime detected";
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

            var sessionIsCurrent = _liveConsoleService.IsSessionRunning &&
                                   _liveConsoleService.ActiveRuntime is not null &&
                                   string.Equals(_liveConsoleService.ActiveRuntime.ExecutablePath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase);

            if (forceRestart && _liveConsoleService.IsSessionRunning)
            {
                await _liveConsoleService.StopConsoleAsync(AppendExecutionOutput).ConfigureAwait(false);
                sessionIsCurrent = false;
            }

            if (!sessionIsCurrent)
            {
                var startupDirectory = GetConsoleStartupDirectory();
                AppLogger.Info("Console", $"Starting PowerShell terminal using {runtime.DisplayName}; StartupDirectory={startupDirectory}");
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

        private void OnTerminalCommandCompleted()
        {
            // Fired on a thread-pool thread by LiveConsoleService when the sentinel token
            // is detected in terminal output.  Marshal to the UI thread before touching
            // UI-bound state.
            PostToUi(() =>
            {
                IsExecutionRunning = false;
                RefreshCommandStates();
            });
        }

        private void OnSessionTerminated()
        {
            // The pwsh.exe process exited (e.g. the user typed 'exit' inside a script).
            // Ensure the Run button is re-enabled even if the sentinel was never echoed.
            PostToUi(() =>
            {
                if (IsExecutionRunning)
                {
                    IsExecutionRunning = false;
                }

                RefreshCommandStates();
                UpdateConsoleSessionPresentation();
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

                    if (ShouldShowLifecycleMessageInTerminal(record.Text))
                    {
                        AppendOutputLine($"PowerShellStudio: {record.Text}");
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

        private static bool ShouldShowLifecycleMessageInTerminal(string? text)
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
                   message.Contains("stopped unexpectedly", StringComparison.OrdinalIgnoreCase);
        }

        private void AppendOutputLine(string text)
        {
            AppendTerminalTextFragment(text + Environment.NewLine);
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

                AppendOutputLine($"[debug] {line.TrimEnd()}");
            }
        }

        private void AppendTerminalTextFragment(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Route to the xterm.js terminal control sink when wired up (normal runtime).
            if (_writeTextSink is not null)
            {
                _writeTextSink(text);
                return;
            }

            // Fallback: accumulate in TerminalDisplayText (used before the terminal
            // control is ready, or in unit-test / headless scenarios).
            const int maxBufferLength = 500000;
            var next = _terminalDisplayText + text;
            if (next.Length > maxBufferLength)
                next = next[^maxBufferLength..];
            TerminalDisplayText = next;
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
                   !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   !string.IsNullOrWhiteSpace(ConsoleCommandText) &&
                   (SelectedRuntimeItem is not null || _preferredRuntimeItem is not null);
        }

        private bool CanRestartConsole()
        {
            return !IsExecutionRunning &&
                   !IsStopInProgress &&
                   !IsRuntimeDiscoveryInProgress &&
                   (SelectedRuntimeItem is not null || _preferredRuntimeItem is not null);
        }

        private void RefreshCommandStates()
        {
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

            if (!string.IsNullOrWhiteSpace(informationalVersion))
            {
                return $"v{informationalVersion}";
            }

            var version = entryAssembly?.GetName().Version;
            return version is null ? "v0.0.0" : $"v{version}";
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
