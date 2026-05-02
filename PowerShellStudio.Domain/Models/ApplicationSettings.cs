using System.Collections.Generic;

namespace PowerShellStudio.Domain.Models
{
    public class ApplicationSettings
    {
        public double? WindowWidth { get; set; }

        public double? WindowHeight { get; set; }

        public double? WindowLeft { get; set; }

        public double? WindowTop { get; set; }

        public bool StartMaximized { get; set; }

        public bool IsExplorerVisible { get; set; } = true;

        public double? ExplorerWidth { get; set; }

        public double? ConsoleHeight { get; set; }

        public double? WorkspaceSectionHeight { get; set; }

        public double? OpenTabsSectionHeight { get; set; }

        public string? LastWorkspaceFolderPath { get; set; }

        public string? SelectedRuntimeExecutablePath { get; set; }

        public string? SelectedTabFilePath { get; set; }

        public List<string> RecentFilePaths { get; set; } = new();

        public List<string> ReopenFilePaths { get; set; } = new();

        /// <summary>Name of the active UI theme ("Dark", "Light", "IseBlue"). Null = default Dark.</summary>
        public string? Theme { get; set; }

        /// <summary>Editor font-size zoom level in points.  Null = default (13 pt).</summary>
        public double? EditorZoomLevel { get; set; }

        /// <summary>Optional editor text-selection background color as a #RRGGBB value. Null = active theme default.</summary>
        public string? EditorSelectionBackgroundHex { get; set; }

        /// <summary>Optional editor current-line background color as a #RRGGBB value. Null = active theme default.</summary>
        public string? EditorCurrentLineBackgroundHex { get; set; }

        /// <summary>Whether selected editor text should force a high-contrast foreground instead of preserving syntax colors.</summary>
        public bool ForceHighContrastSelectedText { get; set; } = true;

        /// <summary>Whether contextual help UI is enabled across the shell. Defaults to true.</summary>
        public bool IsContextHelpEnabled { get; set; } = true;

        /// <summary>Overall SDK editor integration mode. Defaults to Disabled to preserve existing behavior.</summary>
        public EditorSdkMode EditorSdkMode { get; set; } = EditorSdkMode.Disabled;

        /// <summary>Metadata engine selection mode. Defaults to HelperProcessOnly to preserve existing behavior.</summary>
        public MetadataEngineMode MetadataEngineMode { get; set; } = MetadataEngineMode.HelperProcessOnly;

        /// <summary>Completion engine selection mode. Defaults to ExistingOnly to preserve existing behavior.</summary>
        public CompletionEngineMode CompletionEngineMode { get; set; } = CompletionEngineMode.ExistingOnly;

        /// <summary>Whether SDK-backed behavior should retain a safe fallback path. Defaults to true.</summary>
        public bool PowerShellSdkFallbackEnabled { get; set; } = true;
    }
}
