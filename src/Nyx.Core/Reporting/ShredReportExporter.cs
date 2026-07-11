using System.Text.Json;
using System.Text.Json.Serialization;
using Nyx.Core.Execution;
using Nyx.Core.Preflight;

namespace Nyx.Core.Reporting;

internal sealed class ShredReportExporter(string applicationVersion)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public Task ExportAsync(
        ShredBatchResult batch,
        Stream destination,
        bool includeFullPaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(destination);

        var items = batch.Items
            .Select((item, index) => new ReportItem(
                $"item-{index + 1:D4}",
                includeFullPaths ? item.Target.Path : null,
                includeFullPaths ? item.CurrentPath : null,
                item.Target.Kind.ToString(),
                GetAddressableBytes(item.Target),
                item.Target.FileSystem.ToString(),
                item.Target.Medium.ToString(),
                GetAssurance(item.Target).ToString(),
                item.Status.ToString(),
                item.BytesAcceptedByOperatingSystem,
                item.NativeErrorCode,
                item.FailureStage.ToString()))
            .ToArray();
        var report = new ReportDocument(
            1,
            applicationVersion,
            Guid.NewGuid(),
            batch.StartedAt,
            batch.FinishedAt,
            batch.Status.ToString(),
            batch.Items
                .Select(static item => item.Target.Medium.ToString())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            GetBatchAssurance(batch.Items),
            items);

        return JsonSerializer.SerializeAsync(
            destination,
            report,
            SerializerOptions,
            cancellationToken);
    }

    private static AssuranceLevel GetAssurance(TargetSnapshot target)
    {
        if (target.Kind is TargetKind.SymbolicLink or TargetKind.Junction or TargetKind.MountPoint)
        {
            return AssuranceLevel.LinkOnly;
        }

        return target.IsFullyHydratedCloudFile
            || target.Medium is StorageMedium.Ssd or StorageMedium.Flash or StorageMedium.Unknown
            ? AssuranceLevel.Limited
            : AssuranceLevel.BestEffort;
    }

    private static long GetAddressableBytes(TargetSnapshot target) =>
        target.Streams.Count > 0
            ? target.Streams.Sum(static stream => stream.Length)
            : target.Length;

    private static string GetBatchAssurance(IReadOnlyList<ShredItemResult> items)
    {
        var levels = items
            .Select(static item => GetAssurance(item.Target))
            .Distinct()
            .ToArray();
        if (levels.Contains(AssuranceLevel.Unsupported))
        {
            return AssuranceLevel.Unsupported.ToString();
        }

        if (levels.Contains(AssuranceLevel.Limited))
        {
            return AssuranceLevel.Limited.ToString();
        }

        if (levels.Length == 1)
        {
            return levels[0].ToString();
        }

        return levels.Length == 0 ? AssuranceLevel.BestEffort.ToString() : "Mixed";
    }

    private sealed record ReportDocument(
        int SchemaVersion,
        string AppVersion,
        Guid BatchId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset FinishedAtUtc,
        string OverallStatus,
        IReadOnlyList<string> Media,
        string Assurance,
        IReadOnlyList<ReportItem> Items);

    private sealed record ReportItem(
        string ItemId,
        string? Path,
        string? CurrentPath,
        string Kind,
        long Size,
        string FileSystem,
        string Medium,
        string Assurance,
        string Status,
        long BytesAcceptedByOperatingSystem,
        int? NativeErrorCode,
        string FailureStage);
}
