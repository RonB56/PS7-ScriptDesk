using System.Collections.Generic;

namespace PS7ScriptDesk.Domain.Models
{
    public class ApplicationSettings
    {
        public double? WindowWidth { get; set; }

        public double? WindowHeight { get; set; }

        public double? WindowLeft { get; set; }

        public double? WindowTop { get; set; }

        public bool StartMaximized { get; set; }

        public bool IsExplorerVisible { get; set; }

        public double? ExplorerWidth { get; set; }

        public double? ConsoleHeight { get; set; }

        public double? ConsoleSideWidth { get; set; }

        public string? WorkspaceLayoutMode { get; set; } = "HorizontalSplit";

        public double? WorkspaceSectionHeight { get; set; }

        public double? OpenTabsSectionHeight { get; set; }

        public double? DebugPaneWindowWidth { get; set; }

        public double? DebugPaneWindowHeight { get; set; }

        public double? DebugPaneWindowLeft { get; set; }

        public double? DebugPaneWindowTop { get; set; }

        public bool IsDebugPanelVisible { get; set; }

        public double? DockedDebugPanelWidth { get; set; }

        public string? LastWorkspaceFolderPath { get; set; }

        public string? SelectedRuntimeExecutablePath { get; set; }

        public string? SelectedTabFilePath { get; set; }

        public List<string> RecentFilePaths { get; set; } = new();

        public List<string> ReopenFilePaths { get; set; } = new();

        /// <summary>
        /// Rich restore information for the editor tabs that were open during the previous session.
        /// ReopenFilePaths is kept for backward compatibility with settings written by older versions.
        /// </summary>
        public List<OpenDocumentState> ReopenDocuments { get; set; } = new();

        /// <summary>Name of the active UI theme ("Dark", "Light", "IseBlue"). Null = default Dark.</summary>
        public string? Theme { get; set; } = "Dark";

        /// <summary>Editor font-size zoom level in points.  Null = default (13 pt).</summary>
        public double? EditorZoomLevel { get; set; }

        /// <summary>Optional editor text-selection background color as a #RRGGBB value. Null = active theme default.</summary>
        public string? EditorSelectionBackgroundHex { get; set; }

        /// <summary>Optional editor current-line background color as a #RRGGBB value. Null = active theme default.</summary>
        public string? EditorCurrentLineBackgroundHex { get; set; }

        /// <summary>Whether selected editor text should force a high-contrast foreground instead of preserving syntax colors.</summary>
        public bool ForceHighContrastSelectedText { get; set; } = true;

        /// <summary>Whether contextual help UI is enabled across the shell.</summary>
        public bool IsContextHelpEnabled { get; set; }

        public bool IsDeveloperDiagnosticsEnabled { get; set; }

        public bool IsDeveloperDiagnosticsVerboseUiEnabled { get; set; } = true;

        public bool IsDeveloperDiagnosticsVerboseDebuggerEnabled { get; set; } = true;

        public bool IsDeveloperDiagnosticsVerboseTerminalEnabled { get; set; } = true;

        public bool IsDeveloperDiagnosticsVerboseEditorEnabled { get; set; } = true;

        public bool IsDeveloperDiagnosticsVerbosePowerShellExecutionEnabled { get; set; } = true;

        public int DeveloperDiagnosticsPreviewCharacterLimit { get; set; } = 300;

        public int DeveloperDiagnosticsRetentionHours { get; set; } = 72;

        public bool DeveloperDiagnosticsWriteJsonLines { get; set; } = true;

        public bool DeveloperDiagnosticsWriteReadableLog { get; set; } = true;

        /// <summary>Last stable Export as EXE choices. Script text and temporary paths are never stored here.</summary>
        public ExeExportConfiguration? LastExeExportConfiguration { get; set; }
    }
}
