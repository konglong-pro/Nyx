using Nyx.Core.Preflight;

namespace Nyx.Core.Tests;

[TestClass]
public sealed class PreflightServiceTests
{
    [TestMethod]
    public async Task UnsupportedFileSystemBlocksTheBatch()
    {
        var path = Path.Combine(Path.GetTempPath(), "unsupported.bin");
        var inventory = new StubTargetInventory(
            new TargetSnapshot
            {
                Path = path,
                Kind = TargetKind.File,
                VolumeId = "volume-1",
                FileId = "file-1",
                FileSystem = FileSystemKind.ReFs,
                Length = 128,
            });
        var service = new PreflightService(
            inventory,
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([path], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == PreflightIssueCode.UnsupportedFileSystem
            && issue.Severity == PreflightSeverity.Blocking));
    }

    [TestMethod]
    public async Task SsdProducesLimitedWarningWithoutBlocking()
    {
        var path = Path.Combine(Path.GetTempPath(), "ssd.bin");
        var inventory = new StubTargetInventory(
            new TargetSnapshot
            {
                Path = path,
                Kind = TargetKind.File,
                VolumeId = "volume-1",
                FileId = "file-2",
                FileSystem = FileSystemKind.Ntfs,
                Medium = StorageMedium.Ssd,
                Length = 128,
            });
        var service = new PreflightService(
            inventory,
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([path], CancellationToken.None);

        Assert.IsTrue(report.CanStart);
        Assert.AreEqual(AssuranceLevel.Limited, report.Targets.Single().Assurance);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == PreflightIssueCode.LimitedMedium
            && issue.Severity == PreflightSeverity.Warning));
    }

    [TestMethod]
    public async Task UnsupportedLayoutsAndCloudPlaceholdersBlockTheBatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-layouts");
        TargetSnapshot[] targets =
        [
            CreateTarget(Path.Combine(root, "hard-link.bin")) with { LinkCount = 2 },
            CreateTarget(Path.Combine(root, "compressed.bin")) with { Attributes = FileAttributes.Compressed },
            CreateTarget(Path.Combine(root, "sparse.bin")) with { Attributes = FileAttributes.SparseFile },
            CreateTarget(Path.Combine(root, "encrypted.bin")) with { Attributes = FileAttributes.Encrypted },
            CreateTarget(Path.Combine(root, "placeholder.bin")) with { IsCloudPlaceholder = true },
        ];
        var service = new PreflightService(
            new StubTargetInventory(targets),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([root], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.HardLink));
        Assert.AreEqual(
            3,
            report.Issues.Count(issue => issue.Code == PreflightIssueCode.UnsupportedLayout));
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.CloudPlaceholder));
    }

    [TestMethod]
    public async Task ReadOnlyHydratedCloudAndLinksAreWarnings()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-warnings");
        TargetSnapshot[] targets =
        [
            CreateTarget(Path.Combine(root, "readonly.bin")) with
            {
                Attributes = FileAttributes.ReadOnly,
            },
            CreateTarget(Path.Combine(root, "hydrated.bin")) with
            {
                IsCloudPlaceholder = true,
                IsFullyHydratedCloudFile = true,
                IsOneDrive = true,
            },
            CreateTarget(Path.Combine(root, "shortcut")) with
            {
                Kind = TargetKind.SymbolicLink,
            },
        ];
        var service = new PreflightService(
            new StubTargetInventory(targets),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([root], CancellationToken.None);

        Assert.IsTrue(report.CanStart);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.ReadOnly));
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.CloudCopies));
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.LinkOnly));
        Assert.IsFalse(report.Issues.Any(issue => issue.Code == PreflightIssueCode.LimitedMedium));
        Assert.AreEqual(
            AssuranceLevel.LinkOnly,
            report.Targets.Single(target => target.Snapshot.Kind == TargetKind.SymbolicLink).Assurance);
    }

    [TestMethod]
    public async Task FullyHydratedCloudObjectDoesNotBypassSparseFileBlocker()
    {
        var path = Path.Combine(Path.GetTempPath(), "nyx-sparse-cloud.bin");
        var target = CreateTarget(path) with
        {
            Attributes = FileAttributes.SparseFile,
            IsCloudPlaceholder = true,
            IsFullyHydratedCloudFile = true,
            IsOneDrive = true,
        };
        var service = new PreflightService(
            new StubTargetInventory(target),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([path], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.IsTrue(report.Issues.Any(issue =>
            issue.Code == PreflightIssueCode.UnsupportedLayout
            && issue.Severity == PreflightSeverity.Blocking));
    }

    [TestMethod]
    public async Task DuplicateIdentitiesAreCollapsedAndFoldersRequireTypedConfirmation()
    {
        var root = Path.Combine(Path.GetTempPath(), "nyx-dedupe");
        var file = CreateTarget(Path.Combine(root, "file.bin")) with
        {
            VolumeId = "volume-1",
            FileId = "same-file",
            Length = 50,
        };
        var duplicate = file with { Path = Path.Combine(root, ".", "file.bin") };
        var directory = CreateTarget(root) with
        {
            Kind = TargetKind.Directory,
            FileId = "directory",
            Length = 0,
        };
        var service = new PreflightService(
            new StubTargetInventory(file, duplicate, directory),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([root], CancellationToken.None);

        Assert.HasCount(2, report.Targets);
        Assert.AreEqual(50, report.TotalBytes);
        Assert.IsTrue(report.RequiresTypedConfirmation);
    }

    [TestMethod]
    public async Task RevalidationDetectsChangedTargetsBeforeMutation()
    {
        var path = Path.Combine(Path.GetTempPath(), "nyx-changing.bin");
        var initial = CreateTarget(path) with
        {
            Length = 64,
            Streams = [new DataStreamSnapshot("::$DATA", 64)],
        };
        var changed = initial with
        {
            Length = 65,
            Streams = [new DataStreamSnapshot("::$DATA", 65)],
        };
        var inventory = new SequenceTargetInventory(initial, changed);
        var service = new PreflightService(
            inventory,
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));
        var initialReport = await service.InspectAsync([path], CancellationToken.None);

        var revalidation = await service.RevalidateAsync(initialReport, CancellationToken.None);

        Assert.IsFalse(revalidation.IsUnchanged);
        Assert.AreEqual(PreflightIssueCode.TargetChanged, revalidation.Issue?.Code);
    }

    [TestMethod]
    public async Task ProtectedChildDiscoveredDuringInventoryBlocksTheBatch()
    {
        var source = Path.Combine(Path.GetTempPath(), "nyx-safe-source");
        var protectedChild = CreateTarget(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "protected.bin"));
        var service = new PreflightService(
            new StubTargetInventory(protectedChild),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([source], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.ProtectedPath));
    }

    [TestMethod]
    public async Task ProtectedSourceIsRejectedWithoutRecursivelyScanningIt()
    {
        var service = new PreflightService(
            new ThrowIfScannedInventory(),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([@"C:\"], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.IsEmpty(report.Targets);
        Assert.IsTrue(report.Issues.Any(issue => issue.Code == PreflightIssueCode.ProtectedPath));
    }

    [TestMethod]
    public async Task InvalidSourceIsReturnedAsABlockerInsteadOfEscapingPreflight()
    {
        var expectedIssue = new PreflightIssue(
            PreflightIssueCode.UnsupportedLayout,
            PreflightSeverity.Blocking,
            "The path is invalid.");
        var service = new PreflightService(
            new IssueOnlyInventory(expectedIssue),
            PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\Nyx.exe"));

        var report = await service.InspectAsync([string.Empty], CancellationToken.None);

        Assert.IsFalse(report.CanStart);
        Assert.Contains(expectedIssue, report.Issues);
    }

    [TestMethod]
    public void ExactlyOneHundredResolvedFilesRequiresTypedConfirmation()
    {
        var targets = Enumerable.Range(0, 100)
            .Select(index => new PreflightTarget(
                CreateTarget(Path.Combine(Path.GetTempPath(), $"nyx-{index}.bin")) with
                {
                    FileId = $"file-{index}",
                },
                AssuranceLevel.BestEffort))
            .ToArray();

        var report = new PreflightReport([], targets, []);

        Assert.IsTrue(report.RequiresTypedConfirmation);
    }

    [TestMethod]
    public void NinetyNineResolvedFilesDoNotRequireTypedConfirmation()
    {
        var targets = Enumerable.Range(0, 99)
            .Select(index => new PreflightTarget(
                CreateTarget(Path.Combine(Path.GetTempPath(), $"nyx-{index}.bin")) with
                {
                    FileId = $"file-{index}",
                },
                AssuranceLevel.BestEffort))
            .ToArray();

        var report = new PreflightReport([], targets, []);

        Assert.IsFalse(report.RequiresTypedConfirmation);
    }

    [TestMethod]
    public void DirectoryLinkRequiresTypedConfirmationEvenThoughItsTargetIsNotTraversed()
    {
        var link = new PreflightTarget(
            CreateTarget(Path.Combine(Path.GetTempPath(), "nyx-directory-link")) with
            {
                Kind = TargetKind.Junction,
                Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint,
            },
            AssuranceLevel.LinkOnly);
        var report = new PreflightReport([], [link], []);

        Assert.IsTrue(report.RequiresTypedConfirmation);
    }

    private static TargetSnapshot CreateTarget(string path)
    {
        return new TargetSnapshot
        {
            Path = path,
            Kind = TargetKind.File,
            VolumeId = "volume-1",
            FileId = path,
            FileSystem = FileSystemKind.Ntfs,
            Medium = StorageMedium.Hdd,
            Length = 128,
        };
    }

    private sealed class StubTargetInventory(params TargetSnapshot[] targets) : ITargetInventory
    {
        public Task<InventoryResult> ScanAsync(
            IReadOnlyList<string> sourcePaths,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new InventoryResult(targets, []));
        }
    }

    private sealed class SequenceTargetInventory(
        TargetSnapshot first,
        TargetSnapshot second) : ITargetInventory
    {
        private int _callCount;

        public Task<InventoryResult> ScanAsync(
            IReadOnlyList<string> sourcePaths,
            CancellationToken cancellationToken)
        {
            var target = Interlocked.Increment(ref _callCount) == 1 ? first : second;
            return Task.FromResult(new InventoryResult([target], []));
        }
    }

    private sealed class ThrowIfScannedInventory : ITargetInventory
    {
        public Task<InventoryResult> ScanAsync(
            IReadOnlyList<string> sourcePaths,
            CancellationToken cancellationToken) =>
            throw new AssertFailedException("Protected source paths must not be inventoried.");
    }

    private sealed class IssueOnlyInventory(PreflightIssue issue) : ITargetInventory
    {
        public Task<InventoryResult> ScanAsync(
            IReadOnlyList<string> sourcePaths,
            CancellationToken cancellationToken) =>
            Task.FromResult(new InventoryResult([], [issue]));
    }
}
