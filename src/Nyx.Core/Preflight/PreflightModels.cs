namespace Nyx.Core.Preflight;

internal enum TargetKind
{
    File,
    Directory,
    SymbolicLink,
    Junction,
    MountPoint,
}

internal enum FileSystemKind
{
    Ntfs,
    ExFat,
    Fat32,
    ReFs,
    Unknown,
}

internal enum StorageMedium
{
    Hdd,
    Ssd,
    Flash,
    Unknown,
}

internal enum AssuranceLevel
{
    BestEffort,
    Limited,
    LinkOnly,
    Unsupported,
}

internal sealed record DataStreamSnapshot(string Name, long Length);

internal sealed record TargetSnapshot
{
    public required string Path { get; init; }

    public TargetKind Kind { get; init; }

    public required string VolumeId { get; init; }

    public required string FileId { get; init; }

    public FileSystemKind FileSystem { get; init; }

    public StorageMedium Medium { get; init; }

    public long Length { get; init; }

    public FileAttributes Attributes { get; init; } = FileAttributes.Normal;

    public uint ReparseTag { get; init; }

    public int LinkCount { get; init; } = 1;

    public DateTime LastWriteTimeUtc { get; init; }

    public bool IsOneDrive { get; init; }

    public bool IsCloudPlaceholder { get; init; }

    public bool IsFullyHydratedCloudFile { get; init; }

    public IReadOnlyList<DataStreamSnapshot> Streams { get; init; } = [];
}

internal sealed record InventoryResult(
    IReadOnlyList<TargetSnapshot> Targets,
    IReadOnlyList<PreflightIssue> Issues);

internal sealed record PreflightTarget(
    TargetSnapshot Snapshot,
    AssuranceLevel Assurance)
{
    public string Path => Snapshot.Path;
}

internal interface ITargetInventory
{
    Task<InventoryResult> ScanAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken);
}

internal sealed record PreflightReport
{
    public PreflightReport(
        IReadOnlyList<string> sourcePaths,
        IReadOnlyList<PreflightTarget> targets,
        IReadOnlyList<PreflightIssue> issues)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(issues);

        SourcePaths = Array.AsReadOnly(sourcePaths.ToArray());
        Targets = Array.AsReadOnly(targets.Select(static target => target with
        {
            Snapshot = target.Snapshot with
            {
                Streams = Array.AsReadOnly(target.Snapshot.Streams.ToArray()),
            },
        }).ToArray());
        Issues = Array.AsReadOnly(issues.ToArray());
        CanStart = Issues.All(static issue => issue.Severity != PreflightSeverity.Blocking);
        TotalBytes = Targets.Sum(static target =>
            target.Snapshot.Streams.Count > 0
                ? target.Snapshot.Streams.Sum(static stream => stream.Length)
                : target.Snapshot.Length);
        RequiresTypedConfirmation = Targets.Count >= 100
            || Targets.Any(static target =>
                target.Snapshot.Kind == TargetKind.Directory
                || (target.Snapshot.Attributes & FileAttributes.Directory) != 0);
    }

    public IReadOnlyList<string> SourcePaths { get; }

    public IReadOnlyList<PreflightTarget> Targets { get; }

    public IReadOnlyList<PreflightIssue> Issues { get; }

    public bool CanStart { get; }

    public long TotalBytes { get; }

    public bool RequiresTypedConfirmation { get; }
}

internal sealed record RevalidationResult(
    bool IsUnchanged,
    PreflightReport CurrentReport,
    PreflightIssue? Issue);
