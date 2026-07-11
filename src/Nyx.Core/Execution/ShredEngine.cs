using Nyx.Core.Preflight;

namespace Nyx.Core.Execution;

internal sealed class ShredEngine(IShredTargetProcessor processor)
{
    public async Task<ShredBatchResult> ExecuteAsync(
        PreflightReport report,
        IProgress<ShredProgress>? progress,
        CancellationToken stopAfterCurrentToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!report.CanStart)
        {
            throw new InvalidOperationException("A blocked preflight report cannot be executed.");
        }

        var startedAt = DateTimeOffset.UtcNow;
        var results = new List<ShredItemResult>(report.Targets.Count);
        var shouldStop = stopAfterCurrentToken.IsCancellationRequested;

        var orderedTargets = report.Targets
            .OrderBy(static target => target.Snapshot.Kind == TargetKind.Directory ? 1 : 0)
            .ThenByDescending(static target => GetPathDepth(target.Path))
            .ToArray();

        foreach (var target in orderedTargets)
        {
            if (shouldStop || stopAfterCurrentToken.IsCancellationRequested)
            {
                shouldStop = true;
                results.Add(new ShredItemResult(
                    target.Snapshot,
                    ShredItemStatus.NotStarted,
                    0,
                    null,
                    false));
                continue;
            }

            ShredItemResult itemResult;
            try
            {
                itemResult = await processor.ProcessAsync(target, progress).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is
                IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or System.ComponentModel.Win32Exception)
            {
                int? nativeErrorCode = exception is System.ComponentModel.Win32Exception win32
                    ? win32.NativeErrorCode
                    : null;
                itemResult = new ShredItemResult(
                    target.Snapshot,
                    ShredItemStatus.PartiallyOverwritten,
                    0,
                    nativeErrorCode,
                    true,
                    target.Path,
                    ShredFailureStage.Unexpected);
            }

            results.Add(itemResult);
            shouldStop = itemResult.IsVolumeFatal;
        }

        var status = results.Any(static result => result.Status == ShredItemStatus.NotStarted)
            ? ShredBatchStatus.Stopped
            : results.All(static result => result.Status is
                ShredItemStatus.Shredded
                or ShredItemStatus.DeletedWithoutNameRandomization
                or ShredItemStatus.LinkRemoved)
                ? ShredBatchStatus.Completed
                : ShredBatchStatus.CompletedWithProblems;

        return new ShredBatchResult(
            status,
            startedAt,
            DateTimeOffset.UtcNow,
            results);
    }

    private static int GetPathDepth(string path)
    {
        return path.Count(static character =>
            character == Path.DirectorySeparatorChar
            || character == Path.AltDirectorySeparatorChar);
    }
}
