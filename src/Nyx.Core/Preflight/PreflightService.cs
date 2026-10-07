namespace Nyx.Core.Preflight;

internal sealed class PreflightService(
    ITargetInventory inventory,
    PathSafetyPolicy pathSafetyPolicy)
{
    public async Task<PreflightReport> InspectAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        cancellationToken.ThrowIfCancellationRequested();
        var selectedPaths = Array.AsReadOnly(sourcePaths.ToArray());

        var issues = new List<PreflightIssue>();
        foreach (var sourcePath in selectedPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PreflightIssue? pathIssue;
            try
            {
                pathIssue = pathSafetyPolicy.Evaluate(sourcePath);
            }
            catch (Exception exception) when (exception is
                ArgumentException
                or NotSupportedException
                or IOException)
            {
                // Inventory owns invalid-path diagnostics. The policy only short-circuits
                // paths that can be normalized and are already known to be protected.
                continue;
            }

            if (pathIssue is not null)
            {
                issues.Add(pathIssue);
            }
        }

        if (issues.Any(static issue => issue.Severity == PreflightSeverity.Blocking))
        {
            return new PreflightReport(selectedPaths, [], issues);
        }

        var scan = await inventory.ScanAsync(selectedPaths, cancellationToken)
            .ConfigureAwait(false);
        issues.AddRange(scan.Issues);
        var distinctTargets = scan.Targets
            .DistinctBy(static target => (target.VolumeId, target.FileId))
            .ToArray();
        var assessedTargets = new List<PreflightTarget>(distinctTargets.Length);

        foreach (var target in distinctTargets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPathIssue = pathSafetyPolicy.Evaluate(target.Path);
            if (targetPathIssue is not null)
            {
                issues.Add(targetPathIssue);
            }

            if (target.FileSystem is not (
                FileSystemKind.Ntfs
                or FileSystemKind.ExFat
                or FileSystemKind.Fat32))
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.UnsupportedFileSystem,
                    PreflightSeverity.Blocking,
                    $"'{target.Path}': {target.FileSystem} is not a supported file system."));
            }

            if (target.LinkCount > 1)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.HardLink,
                    PreflightSeverity.Blocking,
                    $"'{target.Path}': files with multiple hard links cannot be shredded safely."));
            }

            FileAttributes unsupportedLayouts =
                FileAttributes.Compressed
                | FileAttributes.SparseFile
                | FileAttributes.Encrypted;
            foreach (var attribute in new[]
                     {
                         FileAttributes.Compressed,
                         FileAttributes.SparseFile,
                         FileAttributes.Encrypted,
                     })
            {
                if ((target.Attributes & unsupportedLayouts & attribute) != 0)
                {
                    issues.Add(new PreflightIssue(
                        PreflightIssueCode.UnsupportedLayout,
                        PreflightSeverity.Blocking,
                        $"'{target.Path}': {attribute} files are not supported."));
                }
            }

            if (target.IsCloudPlaceholder && !target.IsFullyHydratedCloudFile)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.CloudPlaceholder,
                    PreflightSeverity.Blocking,
                    $"'{target.Path}': cloud placeholders must be fully available on this device."));
            }

            if ((target.Attributes & FileAttributes.ReadOnly) != 0)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.ReadOnly,
                    PreflightSeverity.Warning,
                    $"'{target.Path}': the read-only attribute will be cleared after confirmation."));
            }

            if (target.IsOneDrive && target.IsFullyHydratedCloudFile)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.CloudCopies,
                    PreflightSeverity.Warning,
                    $"'{target.Path}': cloud versions, recycle bins, and synced copies may remain."));
            }

            var isLink = target.Kind is
                TargetKind.SymbolicLink
                or TargetKind.Junction
                or TargetKind.MountPoint;
            if (isLink)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.LinkOnly,
                    PreflightSeverity.Warning,
                    $"'{target.Path}': only the link entry will be removed; its target will not be touched."));
            }

            var assurance = isLink
                ? AssuranceLevel.LinkOnly
                : target.IsFullyHydratedCloudFile
                    || target.Medium is StorageMedium.Ssd or StorageMedium.Flash or StorageMedium.Unknown
                    ? AssuranceLevel.Limited
                    : AssuranceLevel.BestEffort;
            if (target.Medium is StorageMedium.Ssd or StorageMedium.Flash or StorageMedium.Unknown)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.LimitedMedium,
                    PreflightSeverity.Warning,
                    $"'{target.Path}': SSD, flash, and unknown media provide limited assurance."));
            }

            assessedTargets.Add(new PreflightTarget(target, assurance));
        }

        return new PreflightReport(selectedPaths, assessedTargets, issues);
    }

    public async Task<RevalidationResult> RevalidateAsync(
        PreflightReport originalReport,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(originalReport);

        var currentReport = await InspectAsync(
            originalReport.SourcePaths,
            cancellationToken).ConfigureAwait(false);
        var unchanged = currentReport.CanStart
            && HaveEquivalentTargets(originalReport.Targets, currentReport.Targets);

        return unchanged
            ? new RevalidationResult(true, currentReport, null)
            : new RevalidationResult(
                false,
                currentReport,
                new PreflightIssue(
                    PreflightIssueCode.TargetChanged,
                    PreflightSeverity.Blocking,
                    "The selected targets changed after confirmation. Run the check again."));
    }

    private static bool HaveEquivalentTargets(
        IReadOnlyList<PreflightTarget> expected,
        IReadOnlyList<PreflightTarget> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }

        var actualByIdentity = actual.ToDictionary(
            static target => (target.Snapshot.VolumeId, target.Snapshot.FileId));
        foreach (var expectedTarget in expected)
        {
            if (!actualByIdentity.TryGetValue(
                    (expectedTarget.Snapshot.VolumeId, expectedTarget.Snapshot.FileId),
                    out var actualTarget)
                || !AreEquivalent(expectedTarget.Snapshot, actualTarget.Snapshot))
            {
                return false;
            }
        }

        return true;
    }

    private static bool AreEquivalent(TargetSnapshot expected, TargetSnapshot actual)
    {
        return string.Equals(expected.Path, actual.Path, StringComparison.OrdinalIgnoreCase)
            && expected.Kind == actual.Kind
            && expected.FileSystem == actual.FileSystem
            && expected.Medium == actual.Medium
            && expected.Length == actual.Length
            && expected.Attributes == actual.Attributes
            && expected.ReparseTag == actual.ReparseTag
            && expected.LinkCount == actual.LinkCount
            && expected.LastWriteTimeUtc == actual.LastWriteTimeUtc
            && expected.IsOneDrive == actual.IsOneDrive
            && expected.IsCloudPlaceholder == actual.IsCloudPlaceholder
            && expected.IsFullyHydratedCloudFile == actual.IsFullyHydratedCloudFile
            && expected.Streams.SequenceEqual(actual.Streams);
    }
}
