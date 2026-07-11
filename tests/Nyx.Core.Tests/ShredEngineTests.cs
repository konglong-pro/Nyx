using Nyx.Core.Execution;
using Nyx.Core.Preflight;

namespace Nyx.Core.Tests;

[TestClass]
public sealed class ShredEngineTests
{
    [TestMethod]
    public async Task ItemFailureIsReportedAndIndependentItemsContinue()
    {
        var first = CreateTarget("first.bin", "file-1");
        var second = CreateTarget("second.bin", "file-2");
        var report = new PreflightReport(
            [first.Path, second.Path],
            [first, second],
            []);
        var processor = new RecordingProcessor(
            new ShredItemResult(first.Snapshot, ShredItemStatus.PartiallyOverwritten, 32, 5, false),
            new ShredItemResult(second.Snapshot, ShredItemStatus.Shredded, 64, null, false));
        var engine = new ShredEngine(processor);

        var result = await engine.ExecuteAsync(report, null, CancellationToken.None);

        Assert.AreEqual(ShredBatchStatus.CompletedWithProblems, result.Status);
        Assert.HasCount(2, result.Items);
        Assert.HasCount(2, processor.ProcessedPaths);
    }

    [TestMethod]
    public async Task VolumeFatalErrorStopsEveryRemainingTarget()
    {
        var first = CreateTarget("fatal.bin", "file-1");
        var second = CreateTarget("not-started.bin", "file-2");
        var report = new PreflightReport(
            [first.Path, second.Path],
            [first, second],
            []);
        var processor = new RecordingProcessor(
            new ShredItemResult(
                first.Snapshot,
                ShredItemStatus.PartiallyOverwritten,
                16,
                1117,
                true));
        var engine = new ShredEngine(processor);

        var result = await engine.ExecuteAsync(report, null, CancellationToken.None);

        Assert.AreEqual(ShredBatchStatus.Stopped, result.Status);
        Assert.HasCount(1, processor.ProcessedPaths);
        Assert.AreEqual(ShredItemStatus.NotStarted, result.Items[1].Status);
    }

    [TestMethod]
    public async Task FilesAreProcessedBeforeTheirParentDirectories()
    {
        var directory = CreateTarget("folder", "directory") with
        {
            Snapshot = CreateTarget("folder", "directory").Snapshot with
            {
                Kind = TargetKind.Directory,
            },
        };
        var file = CreateTarget(Path.Combine("folder", "child.bin"), "file");
        var report = new PreflightReport(
            [directory.Path],
            [directory, file],
            []);
        var processor = new RecordingProcessor(
            new ShredItemResult(file.Snapshot, ShredItemStatus.Shredded, 64, null, false),
            new ShredItemResult(directory.Snapshot, ShredItemStatus.Shredded, 0, null, false));
        var engine = new ShredEngine(processor);

        await engine.ExecuteAsync(report, null, CancellationToken.None);

        Assert.AreEqual(file.Path, processor.ProcessedPaths[0]);
        Assert.AreEqual(directory.Path, processor.ProcessedPaths[1]);
    }

    [TestMethod]
    public async Task StopRequestDuringAFileTakesEffectBeforeTheNextItem()
    {
        var first = CreateTarget("first-large.bin", "file-1");
        var second = CreateTarget("second.bin", "file-2");
        var report = new PreflightReport(
            [first.Path, second.Path],
            [first, second],
            []);
        using var stop = new CancellationTokenSource();
        var processor = new CancelAfterFirstProcessor(stop);
        var engine = new ShredEngine(processor);

        var result = await engine.ExecuteAsync(report, null, stop.Token);

        Assert.AreEqual(ShredBatchStatus.Stopped, result.Status);
        Assert.HasCount(1, processor.ProcessedPaths);
        Assert.AreEqual(ShredItemStatus.NotStarted, result.Items[1].Status);
    }

    [TestMethod]
    public async Task UnexpectedProcessorExceptionMarksCurrentItemConservativelyAndStops()
    {
        var first = CreateTarget("uncertain.bin", "file-1");
        var second = CreateTarget("untouched.bin", "file-2");
        var report = new PreflightReport(
            [first.Path, second.Path],
            [first, second],
            []);
        var engine = new ShredEngine(new ThrowingProcessor());

        var result = await engine.ExecuteAsync(report, null, CancellationToken.None);

        Assert.AreEqual(ShredBatchStatus.Stopped, result.Status);
        Assert.AreEqual(ShredItemStatus.PartiallyOverwritten, result.Items[0].Status);
        Assert.AreEqual(ShredFailureStage.Unexpected, result.Items[0].FailureStage);
        Assert.IsTrue(result.Items[0].IsVolumeFatal);
        Assert.AreEqual(ShredItemStatus.NotStarted, result.Items[1].Status);
    }

    private static PreflightTarget CreateTarget(string fileName, string fileId)
    {
        var snapshot = new TargetSnapshot
        {
            Path = Path.Combine(Path.GetTempPath(), fileName),
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = fileId,
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 64,
        };
        return new PreflightTarget(snapshot, AssuranceLevel.BestEffort);
    }

    private sealed class RecordingProcessor(params ShredItemResult[] results) : IShredTargetProcessor
    {
        private readonly Queue<ShredItemResult> _results = new(results);

        public List<string> ProcessedPaths { get; } = [];

        public Task<ShredItemResult> ProcessAsync(
            PreflightTarget target,
            IProgress<ShredProgress>? progress)
        {
            ProcessedPaths.Add(target.Path);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CancelAfterFirstProcessor(CancellationTokenSource stop) : IShredTargetProcessor
    {
        public List<string> ProcessedPaths { get; } = [];

        public Task<ShredItemResult> ProcessAsync(
            PreflightTarget target,
            IProgress<ShredProgress>? progress)
        {
            ProcessedPaths.Add(target.Path);
            stop.Cancel();
            return Task.FromResult(new ShredItemResult(
                target.Snapshot,
                ShredItemStatus.Shredded,
                target.Snapshot.Length,
                null,
                false));
        }
    }

    private sealed class ThrowingProcessor : IShredTargetProcessor
    {
        public Task<ShredItemResult> ProcessAsync(
            PreflightTarget target,
            IProgress<ShredProgress>? progress) =>
            throw new IOException("Injected unexpected failure.");
    }
}
