using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nyx.Core.Execution;
using Nyx.Core.Preflight;
using Nyx.Core.Windows;

namespace Nyx.DestructiveTests;

[TestClass]
[TestCategory("Destructive")]
[DoNotParallelize]
public sealed class WindowsShredTargetProcessorTests
{
    [TestMethod]
    public async Task RegularFileIsShreddedAndRemoved()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("ordinary.bin", CreatePayload(2 * 1024 * 1024 + 17));
        var target = await InspectSingleAsync(path);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(target.Snapshot.Length, result.BytesAcceptedByOperatingSystem);
        Assert.IsNull(result.NativeErrorCode);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReviewedFolderBatchRemovesParentsAfterTheirChildren(bool includeDirectoryStreams)
    {
        using var sandbox = ShredSandbox.Create();
        var root = sandbox.CreateDirectory("folder-batch");
        var nested = sandbox.CreateDirectory(Path.Combine("folder-batch", "nested"));
        _ = sandbox.CreateFile(Path.Combine("folder-batch", "first.bin"), CreatePayload(257));
        _ = sandbox.CreateFile(Path.Combine("folder-batch", "nested", "second.bin"), CreatePayload(513));
        var canary = CreatePayload(97);
        var canaryPath = sandbox.CreateFile("outside-selection.bin", canary);
        if (includeDirectoryStreams)
        {
            _ = sandbox.CreateStream(root, "root-metadata", CreatePayload(113));
            _ = sandbox.CreateStream(nested, "nested-metadata", CreatePayload(179));
        }

        var reviewTime = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.SetLastWriteTimeUtc(root, reviewTime);
        Directory.SetLastWriteTimeUtc(nested, reviewTime);
        var service = new PreflightService(
            new WindowsTargetInventory(),
            PathSafetyPolicy.CreateForCurrentMachine(Path.Combine(AppContext.BaseDirectory, "Nyx.exe")));
        var report = await service.InspectAsync([root], CancellationToken.None);
        Assert.IsTrue(report.CanStart, string.Join(Environment.NewLine, report.Issues.Select(issue => issue.Message)));
        Assert.HasCount(4, report.Targets);
        var revalidation = await service.RevalidateAsync(report, CancellationToken.None);
        Assert.IsTrue(revalidation.IsUnchanged);

        var result = await new ShredEngine(new WindowsShredTargetProcessor()).ExecuteAsync(
            revalidation.CurrentReport,
            progress: null,
            CancellationToken.None);

        Assert.AreEqual(
            ShredBatchStatus.Completed,
            result.Status,
            string.Join(Environment.NewLine, result.Items.Select(item =>
                $"{item.Target.Path}: {item.Status}, {item.FailureStage}, Win32={item.NativeErrorCode}")));
        Assert.IsTrue(result.Items.All(item => item.Status == ShredItemStatus.Shredded));
        Assert.AreEqual(report.TotalBytes, result.Items.Sum(item => item.BytesAcceptedByOperatingSystem));
        Assert.IsFalse(Directory.Exists(root));
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(canaryPath));
    }

    [TestMethod]
    public async Task StreamAddedAfterReviewStopsBeforeMutationAndPreservesContent()
    {
        using var sandbox = ShredSandbox.Create();
        var original = CreatePayload(4097);
        var addedStream = CreatePayload(257);
        var path = sandbox.CreateFile("changed-streams.bin", original);
        var target = await InspectSingleAsync(path);
        var streamPath = sandbox.CreateStream(path, "late", addedStream);
        File.SetLastWriteTimeUtc(path, target.Snapshot.LastWriteTimeUtc);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsTrue(result.IsVolumeFatal);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        CollectionAssert.AreEqual(addedStream, File.ReadAllBytes(streamPath));
    }

    [TestMethod]
    public async Task EveryReviewedNtfsDataStreamIsShreddedBeforeDeletion()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("multiple-streams.bin", CreatePayload(1537));
        _ = sandbox.CreateStream(path, "secret", CreatePayload(733));
        var target = await InspectSingleAsync(path);
        var immediatelyRechecked = await InspectSingleAsync(path);
        Assert.AreEqual(target.Snapshot.Attributes, immediatelyRechecked.Snapshot.Attributes);
        Assert.AreEqual(target.Snapshot.LastWriteTimeUtc, immediatelyRechecked.Snapshot.LastWriteTimeUtc);
        Assert.AreEqual(target.Snapshot.Length, immediatelyRechecked.Snapshot.Length);
        Assert.AreEqual(target.Snapshot.FileId, immediatelyRechecked.Snapshot.FileId);
        CollectionAssert.AreEqual(
            target.Snapshot.Streams.ToArray(),
            immediatelyRechecked.Snapshot.Streams.ToArray());
        var expectedBytes = target.Snapshot.Streams.Sum(stream => stream.Length);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(expectedBytes, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task ReadOnlyFileIsClearedOnlyForTheShredOperation()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("read-only.bin", CreatePayload(8193));
        var target = await InspectSingleAsync(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        target = target with
        {
            Snapshot = target.Snapshot with
            {
                Attributes = File.GetAttributes(path),
            },
        };

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(target.Snapshot.Length, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task SymbolicLinkEntryIsRemovedWithoutTouchingItsTarget()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(2049);
        var targetPath = sandbox.CreateFile("link-target.bin", canary);
        var linkPath = sandbox.CreateFileSymbolicLink("link-entry.bin", targetPath);
        var target = await InspectSingleAsync(linkPath);
        Assert.AreEqual(TargetKind.SymbolicLink, target.Snapshot.Kind);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.LinkRemoved,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(linkPath));
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(targetPath));
    }

    [TestMethod]
    public async Task JunctionEntryIsRemovedWithoutTouchingItsTargetDirectory()
    {
        using var sandbox = ShredSandbox.Create();
        var targetDirectory = sandbox.CreateDirectory("junction-target");
        var canary = CreatePayload(777);
        var canaryPath = sandbox.CreateFile(
            System.IO.Path.Combine("junction-target", "canary.bin"),
            canary);
        var junctionPath = sandbox.CreateDirectoryJunction("junction-entry", targetDirectory);
        var target = await InspectSingleAsync(junctionPath);
        Assert.AreEqual(TargetKind.Junction, target.Snapshot.Kind);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.LinkRemoved,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.IsFalse(Directory.Exists(junctionPath));
        Assert.IsTrue(Directory.Exists(targetDirectory));
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(canaryPath));
    }

    [TestMethod]
    [DataRow(0, "零字节-文件.bin")]
    [DataRow(1024 * 1024, "整块-一兆.bin")]
    public async Task BoundarySizedUnicodeFileIsShredded(int length, string name)
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile(name, CreatePayload(length));
        var target = await InspectSingleAsync(path);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(length, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task LongUnicodePathIsShredded()
    {
        using var sandbox = ShredSandbox.Create();
        var segment = new string('長', 80);
        var relativePath = System.IO.Path.Combine(segment, segment, segment, "資料.bin");
        var path = sandbox.CreateFile(relativePath, CreatePayload(257));
        Assert.IsGreaterThan(260, path.Length);
        var target = await InspectSingleAsync(path);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task EmptyReviewedDirectoryIsRandomizedAndRemoved()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateDirectory("empty-directory");
        var target = await InspectSingleAsync(path);
        Assert.AreEqual(TargetKind.Directory, target.Snapshot.Kind);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}; Attributes={target.Snapshot.Attributes}");
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(Directory.Exists(path));
    }

    [TestMethod]
    public async Task NonEmptyDirectoryIsRetainedWithoutMutatingItsChild()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateDirectory("non-empty-directory");
        var canary = CreatePayload(513);
        var childPath = sandbox.CreateFile(
            System.IO.Path.Combine("non-empty-directory", "child.bin"),
            canary);
        var target = await InspectTargetAsync(path, path);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FolderRetained, result.Status);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.AreEqual(145, result.NativeErrorCode);
        Assert.IsTrue(result.IsVolumeFatal);
        Assert.IsTrue(Directory.Exists(path));
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(childPath));
    }

    [TestMethod]
    public async Task ReviewedDirectoryDataStreamIsShreddedBeforeDirectoryDeletion()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateDirectory("directory-with-stream");
        _ = sandbox.CreateStream(path, "metadata", CreatePayload(1024 * 1024 + 37));
        var target = await InspectSingleAsync(path);
        var expectedBytes = target.Snapshot.Streams.Sum(stream => stream.Length);
        Assert.IsGreaterThan(0L, expectedBytes);

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);

        Assert.AreEqual(
            ShredItemStatus.Shredded,
            result.Status,
            $"Win32={result.NativeErrorCode}; CurrentPath={result.CurrentPath}");
        Assert.AreEqual(expectedBytes, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(Directory.Exists(path));
    }

    [TestMethod]
    public async Task ChildAddedDuringDirectoryStreamOverwriteStopsTheBatchBeforeDeletion()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateDirectory("changed-directory");
        _ = sandbox.CreateStream(path, "metadata", CreatePayload(1024 * 1024 + 37));
        var target = await InspectSingleAsync(path);
        var childCanary = CreatePayload(321);
        var childPath = string.Empty;
        var progress = new CallbackProgress<ShredProgress>(_ =>
        {
            if (childPath.Length == 0)
            {
                childPath = sandbox.CreateFile(
                    System.IO.Path.Combine("changed-directory", "late-child.bin"),
                    childCanary);
            }
        });

        var result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress);

        Assert.AreEqual(ShredItemStatus.FolderRetained, result.Status);
        Assert.AreEqual(target.Snapshot.Streams.Sum(stream => stream.Length), result.BytesAcceptedByOperatingSystem);
        Assert.AreEqual(145, result.NativeErrorCode);
        Assert.IsTrue(result.IsVolumeFatal);
        Assert.IsTrue(Directory.Exists(path));
        CollectionAssert.AreEqual(childCanary, File.ReadAllBytes(childPath));
    }

    [TestMethod]
    public async Task InjectedObjectOpenFailureDoesNotMutateTheFile()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4096);
        var path = sandbox.CreateFile("open-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.OpenObject, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.Open, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task InjectedStreamOpenFailureStopsBeforeMutation()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4096);
        var path = sandbox.CreateFile("stream-open-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.OpenStream, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.StreamEnumeration, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void InjectedMountPointUnbindFailureDoesNotTouchTheDirectory()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateDirectory("mount-point-fault");
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Unmount, 1, 5)));

        var deleted = processor.TryDeleteMountPoint(path, out var error);

        Assert.IsFalse(deleted);
        Assert.AreEqual(5, error);
        Assert.IsTrue(Directory.Exists(path));
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task InjectedZeroByteShortWriteStopsWithoutLooping()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4096);
        var path = sandbox.CreateFile("zero-short-write.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.Write,
                1,
                0,
                SimulatedBytesWritten: 0)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.Overwrite, result.FailureStage);
        Assert.AreEqual(29, result.NativeErrorCode);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsTrue(result.IsVolumeFatal);
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task InjectedWriteFailureAfterOneChunkReportsPartialOverwrite()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(1024 * 1024 + 97);
        var path = sandbox.CreateFile("partial-write.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Write, 2, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.PartiallyOverwritten, result.Status);
        Assert.AreEqual(ShredFailureStage.Overwrite, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(1024L * 1024L, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(canary.SequenceEqual(File.ReadAllBytes(path)));
    }

    [TestMethod]
    [DataRow(1, 0L, (int)ShredItemStatus.FailedBeforeMutation)]
    [DataRow(2, 1048576L, (int)ShredItemStatus.PartiallyOverwritten)]
    public async Task ManagedWriteFailurePreservesConfirmedBytesAndMutationStatus(
        int writeOccurrence,
        long expectedBytes,
        int expectedStatusValue)
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(1024 * 1024 + 97);
        var path = sandbox.CreateFile("managed-write-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.Write,
                writeOccurrence,
                0,
                BeforeNativeCall: static () => throw new CryptographicException("Injected managed write failure."),
                ContinueNative: true)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual((ShredItemStatus)expectedStatusValue, result.Status);
        Assert.AreEqual(expectedBytes, result.BytesAcceptedByOperatingSystem);
        Assert.AreEqual(ShredFailureStage.Unexpected, result.FailureStage);
        Assert.AreEqual(995, result.NativeErrorCode);
        var remaining = File.ReadAllBytes(path);
        CollectionAssert.AreEqual(canary[(int)expectedBytes..], remaining[(int)expectedBytes..]);
    }

    [TestMethod]
    public async Task HandleDiskFullWriteErrorStopsTheRemainingBatch()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("handle-disk-full.bin", CreatePayload(4096));
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Write, 1, 39)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(39, result.NativeErrorCode);
        Assert.IsTrue(result.IsVolumeFatal);
        Assert.AreEqual(ShredFailureStage.Overwrite, result.FailureStage);
    }

    [TestMethod]
    public async Task ManagedFailureAfterTruncateReportsOverwrittenNotDeleted()
    {
        using var sandbox = ShredSandbox.Create();
        var payload = CreatePayload(4096);
        var path = sandbox.CreateFile("managed-after-truncate.bin", payload);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.Rename,
                1,
                0,
                BeforeNativeCall: static () => throw new CryptographicException("Injected rename failure."),
                ContinueNative: true)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.OverwrittenNotDeleted, result.Status);
        Assert.AreEqual(payload.LongLength, result.BytesAcceptedByOperatingSystem);
        Assert.AreEqual(ShredFailureStage.Unexpected, result.FailureStage);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsTrue(File.Exists(path));
        Assert.AreEqual(0L, new FileInfo(path).Length);
    }

    [TestMethod]
    public async Task SimulatedMountUnbindThenManagedFailureReportsRetainedMutation()
    {
        using var sandbox = ShredSandbox.Create();
        var targetDirectory = sandbox.CreateDirectory("mount-phase-target");
        var junctionPath = sandbox.CreateDirectoryJunction("mount-phase-entry", targetDirectory);
        var reviewed = await InspectSingleAsync(junctionPath);
        var mountPoint = reviewed with
        {
            Snapshot = reviewed.Snapshot with { Kind = TargetKind.MountPoint },
        };
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.Unmount,
                1,
                0,
                SimulatedSuccess: true),
            new ShredNativeFault(
                ShredNativeOperation.Rename,
                1,
                0,
                BeforeNativeCall: static () => throw new CryptographicException("Injected after unbind."),
                ContinueNative: true)));

        var result = await processor.ProcessAsync(mountPoint, progress: null);

        Assert.AreEqual(ShredItemStatus.FolderRetained, result.Status);
        Assert.AreEqual(ShredFailureStage.Unexpected, result.FailureStage);
        Assert.AreEqual(995, result.NativeErrorCode);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsTrue(Directory.Exists(junctionPath));
        Assert.IsTrue(Directory.Exists(targetDirectory));
    }

    [TestMethod]
    public async Task DeleteOperationHandleLocksTheReviewedNamespace()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("namespace-lock.bin", CreatePayload(4096));
        var target = await InspectSingleAsync(path);
        var deleteOpenWasBlocked = false;
        var deleteOpenError = 0;
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.OpenStream,
                1,
                0,
                BeforeNativeCall: () =>
                {
                    using var competingHandle = ShredNativeMethods.CreateFile(
                        $@"\\?\{path}",
                        ShredNativeMethods.Delete,
                        ShredNativeMethods.FileShareRead
                            | ShredNativeMethods.FileShareWrite
                            | ShredNativeMethods.FileShareDelete,
                        0,
                        ShredNativeMethods.OpenExisting,
                        0,
                        0);
                    deleteOpenWasBlocked = competingHandle.IsInvalid;
                    deleteOpenError = Marshal.GetLastWin32Error();
                },
                ContinueNative: true)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.IsTrue(deleteOpenWasBlocked);
        Assert.AreEqual(32, deleteOpenError);
        Assert.AreEqual(ShredItemStatus.Shredded, result.Status);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task InjectedFlushFailureHasItsOwnResultClassification()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4097);
        var path = sandbox.CreateFile("flush-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Flush, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.OverwriteFlushFailed, result.Status);
        Assert.AreEqual(ShredFailureStage.Flush, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(canary.LongLength, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(canary.SequenceEqual(File.ReadAllBytes(path)));
    }

    [TestMethod]
    public async Task InjectedTruncateFailureLeavesAnOverwrittenObject()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4097);
        var path = sandbox.CreateFile("truncate-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Truncate, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.OverwrittenNotDeleted, result.Status);
        Assert.AreEqual(ShredFailureStage.Truncate, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(canary.LongLength, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsTrue(File.Exists(path));
        Assert.IsFalse(canary.SequenceEqual(File.ReadAllBytes(path)));
    }

    [TestMethod]
    public async Task InjectedRenameFailureStillDeletesWithAQualifiedSuccessStatus()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4097);
        var path = sandbox.CreateFile("rename-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Rename, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.DeletedWithoutNameRandomization, result.Status);
        Assert.AreEqual(ShredFailureStage.NameRandomization, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(canary.LongLength, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public async Task InjectedDeleteFailureReportsTheRandomizedRemainingPath()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4097);
        var path = sandbox.CreateFile("delete-failure.bin", canary);
        var target = await InspectSingleAsync(path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.Delete, 1, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.OverwrittenNotDeleted, result.Status);
        Assert.AreEqual(ShredFailureStage.Delete, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(canary.LongLength, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.Exists(path));
        Assert.IsNotNull(result.CurrentPath);
        Assert.AreEqual(Path.GetFileName(path).Length, Path.GetFileName(result.CurrentPath).Length);
        Assert.IsTrue(File.Exists(result.CurrentPath));
    }

    [TestMethod]
    public async Task ReadOnlyAttributeIsRestoredWhenOperationOpenFails()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4096);
        var path = sandbox.CreateFile("restore-read-only.bin", canary);
        var target = await InspectSingleAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        target = WithCurrentAttributes(target, path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.OpenObject, 2, 5)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.Open, result.FailureStage);
        Assert.AreEqual(5, result.NativeErrorCode);
        Assert.AreEqual(target.Snapshot.Attributes, File.GetAttributes(path));
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ReadOnlyRestoreFailureOverridesTheFinalDiagnostic()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("restore-failure.bin", CreatePayload(4096));
        var target = await InspectSingleAsync(path);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        target = WithCurrentAttributes(target, path);
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(ShredNativeOperation.OpenObject, 2, 5),
            new ShredNativeFault(ShredNativeOperation.SetAttributes, 2, 32)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.AttributeRestore, result.FailureStage);
        Assert.AreEqual(32, result.NativeErrorCode);
        Assert.IsFalse(result.IsVolumeFatal);
        Assert.IsFalse(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
    }

    [TestMethod]
    public async Task ReadOnlyRestoreFailurePreservesAPrimaryFatalChange()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("fatal-restore-failure.bin", CreatePayload(4096));
        var target = await InspectSingleAsync(path);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        target = WithCurrentAttributes(target, path);
        var replacement = Enumerable.Repeat((byte)0x5A, 4096).ToArray();
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.OpenObject,
                2,
                0,
                BeforeNativeCall: () => File.WriteAllBytes(path, replacement),
                ContinueNative: true),
            new ShredNativeFault(ShredNativeOperation.SetAttributes, 2, 32)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.AttributeRestore, result.FailureStage);
        Assert.AreEqual(32, result.NativeErrorCode);
        Assert.IsTrue(result.IsVolumeFatal);
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task SameLengthChangeBetweenVerifiedHandlesStopsBeforeOverwrite()
    {
        using var sandbox = ShredSandbox.Create();
        var path = sandbox.CreateFile("changed-between-handles.bin", CreatePayload(4096));
        var target = await InspectSingleAsync(path);
        var replacement = Enumerable.Repeat((byte)0xA5, 4096).ToArray();
        var processor = new WindowsShredTargetProcessor(new ShredNativeFaultPlan(
            new ShredNativeFault(
                ShredNativeOperation.OpenObject,
                2,
                0,
                BeforeNativeCall: () => File.WriteAllBytes(path, replacement),
                ContinueNative: true)));

        var result = await processor.ProcessAsync(target, progress: null);

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.FinalValidation, result.FailureStage);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsTrue(result.IsVolumeFatal);
        CollectionAssert.AreEqual(replacement, File.ReadAllBytes(path));
    }

    [TestMethod]
    public void RandomNameValidationRejectsEveryDosDeviceBasename()
    {
        using var sandbox = ShredSandbox.Create();
        foreach (var reservedName in new[]
                 {
                     "CON", "prn", "Aux", "NUL",
                     "COM1", "com9", "LPT1", "lpt9",
                     "CON.txt", "LPT1.bin",
                 })
        {
            Assert.IsFalse(
                WindowsShredTargetProcessor.IsLegalRandomFileName(reservedName),
                reservedName);
        }

        Assert.IsTrue(WindowsShredTargetProcessor.IsLegalRandomFileName("aB39x"));
    }

    [TestMethod]
    public async Task ExistingWriterBlocksBeforeAnyDestructiveWrite()
    {
        using var sandbox = ShredSandbox.Create();
        var canary = CreatePayload(4096);
        var path = sandbox.CreateFile("occupied.bin", canary);
        var target = await InspectSingleAsync(path);
        ShredItemResult result;
        await using (var writer = new FileStream(
                         path,
                         FileMode.Open,
                         FileAccess.Write,
                         FileShare.ReadWrite | FileShare.Delete))
        {
            result = await new WindowsShredTargetProcessor().ProcessAsync(target, progress: null);
        }

        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Status);
        Assert.AreEqual(ShredFailureStage.StreamEnumeration, result.FailureStage);
        Assert.AreEqual(32, result.NativeErrorCode);
        Assert.AreEqual(0L, result.BytesAcceptedByOperatingSystem);
        Assert.IsFalse(result.IsVolumeFatal);
        CollectionAssert.AreEqual(canary, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task FatalRuntimeChangeLeavesEveryLaterEngineTargetNotStarted()
    {
        using var sandbox = ShredSandbox.Create();
        var firstPath = sandbox.CreateFile("changed-first.bin", CreatePayload(4096));
        var secondCanary = CreatePayload(2048);
        var secondPath = sandbox.CreateFile("untouched-second.bin", secondCanary);
        var first = await InspectSingleAsync(firstPath);
        var second = await InspectSingleAsync(secondPath);
        _ = sandbox.CreateStream(firstPath, "late", CreatePayload(33));
        File.SetLastWriteTimeUtc(firstPath, first.Snapshot.LastWriteTimeUtc);
        var report = new PreflightReport(
            [firstPath, secondPath],
            [first, second],
            []);

        var result = await new ShredEngine(new WindowsShredTargetProcessor()).ExecuteAsync(
            report,
            progress: null,
            CancellationToken.None);

        Assert.AreEqual(ShredBatchStatus.Stopped, result.Status);
        Assert.AreEqual(ShredItemStatus.FailedBeforeMutation, result.Items[0].Status);
        Assert.IsTrue(result.Items[0].IsVolumeFatal);
        Assert.AreEqual(ShredItemStatus.NotStarted, result.Items[1].Status);
        CollectionAssert.AreEqual(secondCanary, File.ReadAllBytes(secondPath));
    }

    private static async Task<PreflightTarget> InspectSingleAsync(string path)
    {
        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);
        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        return new PreflightTarget(result.Targets[0], AssuranceLevel.Limited);
    }

    private static async Task<PreflightTarget> InspectTargetAsync(
        string sourcePath,
        string targetPath)
    {
        var result = await new WindowsTargetInventory().ScanAsync(
            [sourcePath],
            CancellationToken.None);
        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        var snapshot = result.Targets.Single(target =>
            string.Equals(target.Path, targetPath, StringComparison.OrdinalIgnoreCase));
        return new PreflightTarget(snapshot, AssuranceLevel.Limited);
    }

    private static PreflightTarget WithCurrentAttributes(
        PreflightTarget target,
        string path) =>
        target with
        {
            Snapshot = target.Snapshot with
            {
                Attributes = File.GetAttributes(path),
            },
        };

    private static byte[] CreatePayload(int length)
    {
        var payload = new byte[length];
        for (var index = 0; index < payload.Length; index++)
        {
            payload[index] = unchecked((byte)(index * 31));
        }

        return payload;
    }

    private sealed class CallbackProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class ShredSandbox : IDisposable
    {
        private const string OptInVariable = "NYX_RUN_DESTRUCTIVE_TESTS";
        private const string SentinelName = ".nyx-destructive-tests-sentinel";
        private readonly string _tempRoot;

        private ShredSandbox(string path, string tempRoot)
        {
            Path = path;
            _tempRoot = tempRoot;
        }

        public string Path { get; }

        public static ShredSandbox Create()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(OptInVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Assert.Inconclusive(
                    $"Destructive tests are disabled. Set {OptInVariable}=1 to opt in explicitly.");
            }

            var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
                .TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                tempRoot,
                $"Nyx.Shred.{Guid.NewGuid():N}"));
            if (!path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            {
                Assert.Inconclusive("The destructive-test sandbox is not inside the OS temp root.");
            }

            Directory.CreateDirectory(path);
            File.WriteAllText(System.IO.Path.Combine(path, SentinelName), "NYX DESTRUCTIVE TEST SANDBOX");
            if (!File.Exists(System.IO.Path.Combine(path, SentinelName)))
            {
                Assert.Inconclusive("The destructive-test sentinel could not be created.");
            }

            return new ShredSandbox(path, tempRoot);
        }

        public string CreateFile(string name, byte[] content)
        {
            RequireSentinel();
            var path = GetChildPath(name);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            return path;
        }

        public string CreateDirectory(string name)
        {
            RequireSentinel();
            var path = GetChildPath(name);
            Directory.CreateDirectory(path);
            return path;
        }

        public string CreateStream(string filePath, string streamName, byte[] content)
        {
            RequireSentinel();
            var fullFilePath = System.IO.Path.GetFullPath(filePath);
            var sandboxPrefix = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (!fullFilePath.StartsWith(sandboxPrefix, StringComparison.OrdinalIgnoreCase)
                || streamName.IndexOfAny([':', '\\', '/']) >= 0)
            {
                throw new InvalidOperationException("An alternate-stream path escaped its sandbox.");
            }

            var streamPath = $"{fullFilePath}:{streamName}:$DATA";
            File.WriteAllBytes(streamPath, content);
            return streamPath;
        }

        public string CreateFileSymbolicLink(string name, string targetPath)
        {
            RequireSentinel();
            var linkPath = GetChildPath(name);
            try
            {
                File.CreateSymbolicLink(linkPath, targetPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Assert.Inconclusive(
                    $"This machine cannot create an unprivileged symbolic link: {exception.Message}");
            }

            return linkPath;
        }

        public string CreateDirectoryJunction(string name, string targetPath)
        {
            RequireSentinel();
            var linkPath = GetChildPath(name);
            var fullTargetPath = System.IO.Path.GetFullPath(targetPath);
            var sandboxPrefix = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (!fullTargetPath.StartsWith(sandboxPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A junction target escaped its test sandbox.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/d");
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add("mklink");
            startInfo.ArgumentList.Add("/J");
            startInfo.ArgumentList.Add(linkPath);
            startInfo.ArgumentList.Add(fullTargetPath);
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                Assert.Inconclusive("The junction helper process could not be started.");
            }

            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(linkPath))
            {
                Assert.Inconclusive(
                    $"This machine could not create a junction: {process.StandardError.ReadToEnd()}");
            }

            return linkPath;
        }

        public void Dispose()
        {
            try
            {
                RequireSentinel();
                RemoveReparseEntriesWithoutFollowing(Path);
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // Cleanup failure must not hide shredder behavior.
            }
            catch (UnauthorizedAccessException)
            {
                // Cleanup failure must not hide shredder behavior.
            }
        }

        private static void RemoveReparseEntriesWithoutFollowing(string directoryPath)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var attributes = File.GetAttributes(entry);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if (isDirectory)
                    {
                        Directory.Delete(entry, recursive: false);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    continue;
                }

                if (isDirectory)
                {
                    RemoveReparseEntriesWithoutFollowing(entry);
                }
            }
        }

        private string GetChildPath(string relativePath)
        {
            var child = System.IO.Path.GetFullPath(System.IO.Path.Combine(Path, relativePath));
            var sandboxPrefix = Path.TrimEnd(System.IO.Path.DirectorySeparatorChar)
                + System.IO.Path.DirectorySeparatorChar;
            if (!child.StartsWith(sandboxPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("A destructive-test path escaped its sandbox.");
            }

            return child;
        }

        private void RequireSentinel()
        {
            var fullPath = System.IO.Path.GetFullPath(Path);
            if (!fullPath.StartsWith(_tempRoot, StringComparison.OrdinalIgnoreCase)
                || !File.Exists(System.IO.Path.Combine(fullPath, SentinelName)))
            {
                throw new InvalidOperationException(
                    "Refusing a destructive test operation outside a sentinel-protected temp sandbox.");
            }
        }
    }
}
