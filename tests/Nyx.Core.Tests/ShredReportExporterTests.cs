using System.Text.Json;
using Nyx.Core.Execution;
using Nyx.Core.Preflight;
using Nyx.Core.Reporting;

namespace Nyx.Core.Tests;

[TestClass]
public sealed class ShredReportExporterTests
{
    [TestMethod]
    public async Task FileExportReplacesAnExistingReportWithCompleteRedactedJson()
    {
        using var directory = new ReportDirectory();
        var destinationPath = Path.Combine(directory.Path, "report.json");
        await File.WriteAllTextAsync(destinationPath, "previous report");

        await new ShredReportExporter("0.1.0-beta.1").ExportToFileAsync(
            CreateBatch(),
            destinationPath,
            includeFullPaths: false,
            CancellationToken.None);

        await using var output = File.OpenRead(destinationPath);
        using var document = await JsonDocument.ParseAsync(output);
        Assert.AreEqual(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("Shredded", document.RootElement.GetProperty("items")[0].GetProperty("status").GetString());
        Assert.IsFalse(document.RootElement.GetProperty("items")[0].TryGetProperty("path", out _));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public async Task CanceledFileExportPreservesThePreviousReportWithoutTemporaryFiles()
    {
        using var directory = new ReportDirectory();
        var destinationPath = Path.Combine(directory.Path, "report.json");
        const string previousReport = "previous report";
        await File.WriteAllTextAsync(destinationPath, previousReport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            new ShredReportExporter("0.1.0-beta.1").ExportToFileAsync(
                CreateBatch(),
                destinationPath,
                includeFullPaths: false,
                cancellation.Token));

        Assert.AreEqual(previousReport, await File.ReadAllTextAsync(destinationPath));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public async Task FailedSerializationPreservesThePreviousReportAndRemovesTheTemporaryFile()
    {
        using var directory = new ReportDirectory();
        var destinationPath = Path.Combine(directory.Path, "report.json");
        const string previousReport = "previous report";
        await File.WriteAllTextAsync(destinationPath, previousReport);
        var batch = CreateBatch();
        batch = batch with
        {
            Items =
            [
                batch.Items[0] with
                {
                    Target = batch.Items[0].Target with
                    {
                        Streams =
                        [
                            new DataStreamSnapshot("::$DATA", long.MaxValue),
                            new DataStreamSnapshot(":extra:$DATA", 1),
                        ],
                    },
                },
            ],
        };

        await Assert.ThrowsExactlyAsync<OverflowException>(() =>
            new ShredReportExporter("0.1.0-beta.1").ExportToFileAsync(
                batch,
                destinationPath,
                includeFullPaths: false,
                CancellationToken.None));

        Assert.AreEqual(previousReport, await File.ReadAllTextAsync(destinationPath));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public async Task FailedReportReplacementPreservesThePreviousFileAndRemovesTheTemporaryFile()
    {
        using var directory = new ReportDirectory();
        var destinationPath = Path.Combine(directory.Path, "report.json");
        const string previousReport = "previous report";
        await File.WriteAllTextAsync(destinationPath, previousReport);

        await using (var lockedDestination = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            var exception = await Assert.ThrowsAsync<Exception>(() =>
                new ShredReportExporter("0.1.0-beta.1").ExportToFileAsync(
                    CreateBatch(),
                    destinationPath,
                    includeFullPaths: false,
                    CancellationToken.None));
            Assert.IsTrue(exception is IOException or UnauthorizedAccessException,
                $"Unexpected replacement failure: {exception.GetType().Name}.");
        }

        Assert.AreEqual(previousReport, await File.ReadAllTextAsync(destinationPath));
        Assert.HasCount(1, Directory.GetFiles(directory.Path));
    }

    [TestMethod]
    public async Task RedactedReportOmitsPathsAndIncludesStableSchema()
    {
        var target = new TargetSnapshot
        {
            Path = Path.Combine(Path.GetTempPath(), "sensitive-name.bin"),
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = "file-1",
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Ssd,
            Length = 128,
        };
        var batch = new ShredBatchResult(
            ShredBatchStatus.Completed,
            DateTimeOffset.Parse("2026-07-10T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-07-10T00:00:01Z", System.Globalization.CultureInfo.InvariantCulture),
            [new ShredItemResult(target, ShredItemStatus.Shredded, 128, null, false)]);
        var exporter = new ShredReportExporter("0.1.0-beta.1");
        await using var output = new MemoryStream();

        await exporter.ExportAsync(batch, output, includeFullPaths: false, CancellationToken.None);

        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        var root = document.RootElement;
        AssertPropertyNames(
            root,
            "schemaVersion",
            "appVersion",
            "batchId",
            "startedAtUtc",
            "finishedAtUtc",
            "overallStatus",
            "media",
            "assurance",
            "items");
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("0.1.0-beta.1", root.GetProperty("appVersion").GetString());
        Assert.AreNotEqual(Guid.Empty, root.GetProperty("batchId").GetGuid());
        Assert.AreEqual(batch.StartedAt, root.GetProperty("startedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(batch.FinishedAt, root.GetProperty("finishedAtUtc").GetDateTimeOffset());
        Assert.AreEqual("Completed", root.GetProperty("overallStatus").GetString());
        Assert.AreEqual("Ssd", root.GetProperty("media")[0].GetString());
        Assert.AreEqual("Limited", root.GetProperty("assurance").GetString());

        var item = root.GetProperty("items")[0];
        AssertPropertyNames(
            item,
            "itemId",
            "kind",
            "size",
            "fileSystem",
            "medium",
            "assurance",
            "status",
            "bytesAcceptedByOperatingSystem",
            "failureStage");
        Assert.AreEqual("item-0001", item.GetProperty("itemId").GetString());
        Assert.AreEqual("File", item.GetProperty("kind").GetString());
        Assert.AreEqual(128, item.GetProperty("size").GetInt64());
        Assert.AreEqual("Ntfs", item.GetProperty("fileSystem").GetString());
        Assert.AreEqual("Ssd", item.GetProperty("medium").GetString());
        Assert.AreEqual("Limited", item.GetProperty("assurance").GetString());
        Assert.AreEqual("Shredded", item.GetProperty("status").GetString());
        Assert.AreEqual(128, item.GetProperty("bytesAcceptedByOperatingSystem").GetInt64());
        Assert.AreEqual("None", item.GetProperty("failureStage").GetString());
        Assert.IsFalse(item.TryGetProperty("path", out _));
        Assert.IsFalse(item.TryGetProperty("currentPath", out _));
        Assert.IsFalse(item.TryGetProperty("nativeErrorCode", out _));
        Assert.IsFalse(item.TryGetProperty("contentHash", out _));
    }

    [TestMethod]
    public async Task ExplicitFullPathExportIncludesTheOriginalPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "explicit-path.bin");
        var currentPath = Path.Combine(Path.GetTempPath(), "randomized-name.bin");
        var target = new TargetSnapshot
        {
            Path = path,
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = "file-1",
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 1,
        };
        var now = DateTimeOffset.UtcNow;
        var batch = new ShredBatchResult(
            ShredBatchStatus.Completed,
            now,
            now,
            [new ShredItemResult(target, ShredItemStatus.Shredded, 1, null, false, currentPath)]);
        var exporter = new ShredReportExporter("0.1.0-beta.1");
        await using var output = new MemoryStream();

        await exporter.ExportAsync(batch, output, includeFullPaths: true, CancellationToken.None);

        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        Assert.AreEqual(
            path,
            document.RootElement.GetProperty("items")[0].GetProperty("path").GetString());
        Assert.AreEqual(
            currentPath,
            document.RootElement.GetProperty("items")[0].GetProperty("currentPath").GetString());
    }

    [TestMethod]
    public async Task FailureReportIncludesTheFailureStageAndNativeErrorCode()
    {
        var target = new TargetSnapshot
        {
            Path = Path.Combine(Path.GetTempPath(), "failed.bin"),
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = "file-1",
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 64,
        };
        var now = DateTimeOffset.UtcNow;
        var batch = new ShredBatchResult(
            ShredBatchStatus.CompletedWithProblems,
            now,
            now,
            [new ShredItemResult(
                target,
                ShredItemStatus.PartiallyOverwritten,
                32,
                5,
                false,
                target.Path,
                ShredFailureStage.Overwrite)]);
        var exporter = new ShredReportExporter("0.1.0-beta.1");
        await using var output = new MemoryStream();

        await exporter.ExportAsync(batch, output, includeFullPaths: false, CancellationToken.None);

        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        var item = document.RootElement.GetProperty("items")[0];
        AssertPropertyNames(
            item,
            "itemId",
            "kind",
            "size",
            "fileSystem",
            "medium",
            "assurance",
            "status",
            "bytesAcceptedByOperatingSystem",
            "nativeErrorCode",
            "failureStage");
        Assert.AreEqual("Overwrite", item.GetProperty("failureStage").GetString());
        Assert.AreEqual(5, item.GetProperty("nativeErrorCode").GetInt32());
    }

    [TestMethod]
    public async Task ReportSummarizesBatchMediaAssuranceAndAllAddressableStreamBytes()
    {
        var target = new TargetSnapshot
        {
            Path = Path.Combine(Path.GetTempPath(), "streams.bin"),
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = "file-1",
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 10,
            Streams =
            [
                new DataStreamSnapshot("::$DATA", 10),
                new DataStreamSnapshot(":secret:$DATA", 20),
            ],
        };
        var now = DateTimeOffset.UtcNow;
        var batch = new ShredBatchResult(
            ShredBatchStatus.Completed,
            now,
            now,
            [new ShredItemResult(target, ShredItemStatus.Shredded, 30, null, false)]);
        await using var output = new MemoryStream();

        await new ShredReportExporter("0.1.0-beta.1").ExportAsync(
            batch,
            output,
            includeFullPaths: false,
            CancellationToken.None);

        output.Position = 0;
        using var document = await JsonDocument.ParseAsync(output);
        Assert.AreEqual("BestEffort", document.RootElement.GetProperty("assurance").GetString());
        Assert.AreEqual("Hdd", document.RootElement.GetProperty("media")[0].GetString());
        Assert.AreEqual(30, document.RootElement.GetProperty("items")[0].GetProperty("size").GetInt64());
    }

    private static void AssertPropertyNames(JsonElement element, params string[] expected)
    {
        var actual = element
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sortedExpected = expected
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(sortedExpected, actual);
    }

    private static ShredBatchResult CreateBatch()
    {
        var target = new TargetSnapshot
        {
            Path = Path.Combine(Path.GetTempPath(), "sensitive-name.bin"),
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = "file-1",
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 128,
        };
        var now = DateTimeOffset.UtcNow;
        return new ShredBatchResult(
            ShredBatchStatus.Completed,
            now,
            now,
            [new ShredItemResult(target, ShredItemStatus.Shredded, 128, null, false)]);
    }

    private sealed class ReportDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateTempSubdirectory("Nyx.Report.").FullName;

        public void Dispose()
        {
            foreach (var file in Directory.GetFiles(Path))
            {
                File.Delete(file);
            }

            Directory.Delete(Path);
        }
    }
}
