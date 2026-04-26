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

        /// <summary>Whether contextual help UI is enabled across the shell. Defaults to true.</summary>
        public bool IsContextHelpEnabled { get; set; } = true;
    }
}
