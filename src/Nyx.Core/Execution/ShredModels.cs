using Nyx.Core.Preflight;

namespace Nyx.Core.Execution;

internal enum ShredItemStatus
{
    Shredded,
    DeletedWithoutNameRandomization,
    LinkRemoved,
    FailedBeforeMutation,
    PartiallyOverwritten,
    OverwriteFlushFailed,
    OverwrittenNotDeleted,
    FolderRetained,
    NotStarted,
}

internal enum ShredBatchStatus
{
    Completed,
    CompletedWithProblems,
    Stopped,
    Aborted,
}

internal enum ShredFailureStage
{
    None,
    Open,
    IdentityVerification,
    FinalValidation,
    StreamEnumeration,
    Overwrite,
    Flush,
    Truncate,
    NameRandomization,
    Delete,
    DirectoryValidation,
    AttributeRestore,
    Unexpected,
}

internal sealed record ShredProgress(
    string Path,
    int ItemIndex,
    int ItemCount,
    long ItemBytesProcessed,
    long ItemBytesTotal,
    long TotalBytesProcessed,
    long TotalBytes);

internal sealed record ShredItemResult(
    TargetSnapshot Target,
    ShredItemStatus Status,
    long BytesAcceptedByOperatingSystem,
    int? NativeErrorCode,
    bool IsVolumeFatal,
    string? CurrentPath = null,
    ShredFailureStage FailureStage = ShredFailureStage.None);

internal sealed record ShredBatchResult(
    ShredBatchStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<ShredItemResult> Items);

internal interface IShredTargetProcessor
{
    Task<ShredItemResult> ProcessAsync(
        PreflightTarget target,
        IProgress<ShredProgress>? progress);
}
