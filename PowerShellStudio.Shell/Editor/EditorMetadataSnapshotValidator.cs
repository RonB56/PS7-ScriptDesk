using System;
using System.Linq;

namespace PowerShellStudio.Shell.Editor
{
    internal sealed class EditorMetadataSnapshotHealth
    {
        public static readonly EditorMetadataSnapshotHealth Empty = new(0, 0, 0, 0, 0, 0, 0);

        public EditorMetadataSnapshotHealth(
            int commandCount,
            int quickInfoCount,
            int parameterizedQuickInfoCount,
            int getChildItemParameterCount,
            int setExecutionPolicyParameterCount,
            int startProcessParameterCount,
            int getProcessParameterCount)
        {
            CommandCount = Math.Max(0, commandCount);
            QuickInfoCount = Math.Max(0, quickInfoCount);
            ParameterizedQuickInfoCount = Math.Max(0, parameterizedQuickInfoCount);
            GetChildItemParameterCount = Math.Max(0, getChildItemParameterCount);
            SetExecutionPolicyParameterCount = Math.Max(0, setExecutionPolicyParameterCount);
            StartProcessParameterCount = Math.Max(0, startProcessParameterCount);
            GetProcessParameterCount = Math.Max(0, getProcessParameterCount);
        }

        public int CommandCount { get; }
        public int QuickInfoCount { get; }
        public int ParameterizedQuickInfoCount { get; }
        public int GetChildItemParameterCount { get; }
        public int SetExecutionPolicyParameterCount { get; }
        public int StartProcessParameterCount { get; }
        public int GetProcessParameterCount { get; }
    }

    internal sealed class EditorMetadataSnapshotValidationResult
    {
        public EditorMetadataSnapshotValidationResult(bool isHealthy, string message, EditorMetadataSnapshotHealth health)
        {
            IsHealthy = isHealthy;
            Message = string.IsNullOrWhiteSpace(message) ? "Metadata snapshot validation completed." : message.Trim();
            Health = health ?? EditorMetadataSnapshotHealth.Empty;
        }

        public bool IsHealthy { get; }
        public string Message { get; }
        public EditorMetadataSnapshotHealth Health { get; }
    }

    internal static class EditorMetadataSnapshotValidator
    {
        public static EditorMetadataSnapshotValidationResult Validate(EditorMetadataCacheSnapshot? snapshot)
        {
            var health = BuildHealth(snapshot);

            if (health.CommandCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain any PowerShell commands.",
                    health);
            }

            if (health.QuickInfoCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain any quick-info records.",
                    health);
            }

            if (health.GetChildItemParameterCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain usable parameter metadata for Get-ChildItem.",
                    health);
            }

            if (health.SetExecutionPolicyParameterCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain usable parameter metadata for Set-ExecutionPolicy.",
                    health);
            }

            if (health.StartProcessParameterCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain usable parameter metadata for Start-Process.",
                    health);
            }

            if (health.GetProcessParameterCount <= 0)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    "The metadata snapshot does not contain usable parameter metadata for Get-Process.",
                    health);
            }

            var minimumParameterizedQuickInfos = ComputeMinimumParameterizedQuickInfoCount(health);
            if (health.ParameterizedQuickInfoCount < minimumParameterizedQuickInfos)
            {
                return new EditorMetadataSnapshotValidationResult(
                    false,
                    $"The metadata snapshot only contains {health.ParameterizedQuickInfoCount:N0} parameterized quick-info records; at least {minimumParameterizedQuickInfos:N0} are required for a healthy full snapshot.",
                    health);
            }

            return new EditorMetadataSnapshotValidationResult(
                true,
                "The metadata snapshot passed full-health validation.",
                health);
        }

        public static EditorMetadataSnapshotHealth BuildHealth(EditorMetadataCacheSnapshot? snapshot)
        {
            if (snapshot is null)
            {
                return EditorMetadataSnapshotHealth.Empty;
            }

            return new EditorMetadataSnapshotHealth(
                snapshot.Catalog.Commands.Count,
                snapshot.QuickInfos.Count,
                snapshot.QuickInfos.Values.Count(quickInfo => quickInfo is not null && quickInfo.Parameters.Count > 0),
                GetParameterCount(snapshot, "Get-ChildItem"),
                GetParameterCount(snapshot, "Set-ExecutionPolicy"),
                GetParameterCount(snapshot, "Start-Process"),
                GetParameterCount(snapshot, "Get-Process"));
        }

        public static string Describe(EditorMetadataSnapshotHealth health)
        {
            health ??= EditorMetadataSnapshotHealth.Empty;
            return $"Catalog={health.CommandCount:N0}, QuickInfo={health.QuickInfoCount:N0}, ParameterizedQuickInfos={health.ParameterizedQuickInfoCount:N0}, Get-ChildItemParameters={health.GetChildItemParameterCount:N0}, Set-ExecutionPolicyParameters={health.SetExecutionPolicyParameterCount:N0}, Start-ProcessParameters={health.StartProcessParameterCount:N0}, Get-ProcessParameters={health.GetProcessParameterCount:N0}";
        }

        private static int ComputeMinimumParameterizedQuickInfoCount(EditorMetadataSnapshotHealth health)
        {
            if (health is null)
            {
                return 0;
            }

            var quickInfoThreshold = health.QuickInfoCount > 0
                ? (int)Math.Ceiling(health.QuickInfoCount / 6d)
                : 0;
            var catalogThreshold = health.CommandCount > 0
                ? (int)Math.Ceiling(health.CommandCount / 8d)
                : 0;

            return Math.Max(64, Math.Min(256, Math.Max(quickInfoThreshold, catalogThreshold)));
        }

        private static int GetParameterCount(EditorMetadataCacheSnapshot snapshot, string commandName)
        {
            return snapshot.QuickInfos.TryGetValue(commandName, out var quickInfo) && quickInfo is not null
                ? quickInfo.Parameters.Count
                : 0;
        }
    }
}
