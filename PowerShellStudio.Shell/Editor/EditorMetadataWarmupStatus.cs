using System;

namespace PowerShellStudio.Shell.Editor
{
    public enum EditorMetadataWarmupPhase
    {
        Idle = 0,
        Scheduled = 1,
        BuildingCommandCatalog = 2,
        LoadingCommandMetadata = 3,
        RefreshingCachedMetadata = 4,
        Completed = 5,
        Failed = 6,
        Canceled = 7,
        Warning = 8
    }

    public enum EditorMetadataWarmupReason
    {
        None = 0,
        CachedLoad = 1,
        FirstRunBuild = 2,
        CacheRebuild = 3,
        ManualRefresh = 4
    }

    public sealed class EditorMetadataWarmupStatus
    {
        public EditorMetadataWarmupStatus(
            EditorMetadataWarmupPhase phase,
            string message,
            string? runtimePath = null,
            int processedCount = 0,
            int totalCount = 0,
            string? detailText = null,
            int commandCount = 0,
            int quickInfoCount = 0,
            int parameterizedQuickInfoCount = 0,
            int getChildItemParameterCount = 0,
            bool isLoadedFromCache = false,
            EditorMetadataWarmupReason reason = EditorMetadataWarmupReason.None)
        {
            Phase = phase;
            Message = string.IsNullOrWhiteSpace(message) ? "Editor metadata status changed." : message.Trim();
            RuntimePath = runtimePath;
            ProcessedCount = Math.Max(0, processedCount);
            TotalCount = Math.Max(0, totalCount);
            DetailText = detailText?.Trim() ?? string.Empty;
            CommandCount = Math.Max(0, commandCount);
            QuickInfoCount = Math.Max(0, quickInfoCount);
            ParameterizedQuickInfoCount = Math.Max(0, parameterizedQuickInfoCount);
            GetChildItemParameterCount = Math.Max(0, getChildItemParameterCount);
            IsLoadedFromCache = isLoadedFromCache;
            Reason = reason;
        }

        public EditorMetadataWarmupPhase Phase { get; }

        public string Message { get; }

        public string? RuntimePath { get; }

        public int ProcessedCount { get; }

        public int TotalCount { get; }

        public string DetailText { get; }

        public int CommandCount { get; }

        public int QuickInfoCount { get; }

        public int ParameterizedQuickInfoCount { get; }

        public int GetChildItemParameterCount { get; }

        public bool IsLoadedFromCache { get; }

        public EditorMetadataWarmupReason Reason { get; }

        public bool HasProgress => TotalCount > 0;

        public bool IsCompletedSuccessfully => Phase == EditorMetadataWarmupPhase.Completed;

        public bool HasCommandCatalog => CommandCount > 0;

        public bool HasFullParameterMetadata =>
            CommandCount > 0 &&
            QuickInfoCount > 0 &&
            ParameterizedQuickInfoCount > 0 &&
            GetChildItemParameterCount > 0;

        public string ReadinessCaption =>
            IsLoadedFromCache
                ? "Using cached editor metadata"
                : "Editor metadata ready";

        public string WarningCaption =>
            "Metadata refresh failed; cached metadata still in use";

        public bool IsManualRefresh => Reason == EditorMetadataWarmupReason.ManualRefresh;

        public bool HasReadyMetadata =>
            Phase == EditorMetadataWarmupPhase.Completed ||
            Phase == EditorMetadataWarmupPhase.RefreshingCachedMetadata ||
            Phase == EditorMetadataWarmupPhase.Warning;

        public bool IsActive =>
            Phase == EditorMetadataWarmupPhase.Scheduled ||
            Phase == EditorMetadataWarmupPhase.BuildingCommandCatalog ||
            Phase == EditorMetadataWarmupPhase.LoadingCommandMetadata ||
            Phase == EditorMetadataWarmupPhase.RefreshingCachedMetadata;

        public string ProgressText => HasProgress ? $"{ProcessedCount}/{TotalCount}" : string.Empty;
    }

    public sealed class EditorMetadataWarmupStatusChangedEventArgs : EventArgs
    {
        public EditorMetadataWarmupStatusChangedEventArgs(EditorMetadataWarmupStatus status)
        {
            Status = status ?? throw new ArgumentNullException(nameof(status));
        }

        public EditorMetadataWarmupStatus Status { get; }
    }
}
