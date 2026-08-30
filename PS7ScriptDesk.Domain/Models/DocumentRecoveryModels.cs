using System;

namespace PS7ScriptDesk.Domain.Models
{
    public enum DocumentRecoveryFileStatus
    {
        Unknown,
        OriginalUnchanged,
        OriginalModified,
        OriginalMissing
    }

    public enum DocumentRecoveryAction
    {
        Restore,
        Discard,
        SaveAs,
        KeepForLater
    }

    public sealed record DocumentRecoverySnapshot(
        string RecoveryId,
        string? OriginalFilePath,
        string DisplayName,
        string Content,
        DateTime SnapshotCreatedUtc,
        DateTime? OriginalLastWriteTimeUtc,
        long? OriginalLength,
        string? OriginalContentSha256,
        bool IsUntitled);

    public sealed record DocumentRecoveryCandidate(
        string RecoveryId,
        string? OriginalFilePath,
        string DisplayName,
        string Content,
        DateTime SnapshotCreatedUtc,
        DateTime LastRecoveryWriteUtc,
        DateTime? OriginalLastWriteTimeUtc,
        long? OriginalLength,
        string? OriginalContentSha256,
        bool IsUntitled,
        DocumentRecoveryFileStatus OriginalFileStatus,
        string StatusDescription);
}
