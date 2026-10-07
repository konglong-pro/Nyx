using System.Diagnostics;
using System.Runtime.InteropServices;
using Nyx.Core.Preflight;
using Nyx.Core.Windows;

namespace Nyx.DestructiveTests;

[TestClass]
[TestCategory("Inventory")]
public sealed class WindowsTargetInventoryTests
{
    private static readonly string[] ExpectedOrderedDataStreams =
        ["::$DATA", ":Alpha:$DATA", ":private:$DATA"];

    [TestMethod]
    public async Task RegularFileReturnsHandleMetadataWithoutChangingContent()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("ordinary.txt", "inventory canary");
        var originalBytes = File.ReadAllBytes(path);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        var target = result.Targets[0];
        Assert.AreEqual(Path.GetFullPath(path), target.Path);
        Assert.AreEqual(TargetKind.File, target.Kind);
        Assert.AreEqual(FileSystemKind.Ntfs, target.FileSystem);
        Assert.AreEqual(originalBytes.LongLength, target.Length);
        Assert.AreEqual(1, target.LinkCount);
        Assert.IsFalse(string.IsNullOrWhiteSpace(target.VolumeId));
        Assert.IsFalse(string.IsNullOrWhiteSpace(target.FileId));
        Assert.IsGreaterThan(DateTime.UnixEpoch, target.LastWriteTimeUtc);
        Assert.IsTrue(target.Streams.Any(stream =>
            stream.Name == "::$DATA" && stream.Length == originalBytes.LongLength));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task CooperativelySharedWriterStillBlocksPreflight()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("shared-writer.bin", "shared writer canary");
        await using var writer = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking));
        writer.Position = 0;
        using var reader = new StreamReader(writer, leaveOpen: true);
        Assert.AreEqual("shared writer canary", await reader.ReadToEndAsync());
    }

    [TestMethod]
    public async Task DirectoryScanRecursivelyInventoriesEveryEntryWithoutChangingFiles()
    {
        using var sandbox = InventorySandbox.Create();
        var nestedDirectory = sandbox.CreateDirectory("nested");
        var firstFile = sandbox.CreateFile("first.bin", "first canary");
        var secondFile = sandbox.CreateFile(
            System.IO.Path.Combine("nested", "second.bin"),
            "second canary");
        var firstBytes = File.ReadAllBytes(firstFile);
        var secondBytes = File.ReadAllBytes(secondFile);

        var result = await new WindowsTargetInventory().ScanAsync(
            [sandbox.Path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        CollectionAssert.AreEquivalent(
            new[]
            {
                sandbox.Path,
                nestedDirectory,
                firstFile,
                secondFile,
            },
            result.Targets.Select(target => target.Path).ToArray());
        Assert.AreEqual(
            TargetKind.Directory,
            result.Targets.Single(target => target.Path == nestedDirectory).Kind);
        CollectionAssert.AreEqual(firstBytes, File.ReadAllBytes(firstFile));
        CollectionAssert.AreEqual(secondBytes, File.ReadAllBytes(secondFile));
    }

    [TestMethod]
    public async Task NtfsNamedDataStreamsAreInventoriedWithoutChangingTheirContent()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("streams.bin", "default stream");
        var namedStreamPath = $"{path}:private:$DATA";
        var alphaStreamPath = $"{path}:Alpha:$DATA";
        var namedBytes = "named stream"u8.ToArray();
        var alphaBytes = "alpha stream"u8.ToArray();
        File.WriteAllBytes(namedStreamPath, namedBytes);
        File.WriteAllBytes(alphaStreamPath, alphaBytes);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        CollectionAssert.AreEqual(
            ExpectedOrderedDataStreams,
            result.Targets[0].Streams.Select(stream => stream.Name).ToArray());
        Assert.AreEqual(
            namedBytes.LongLength,
            result.Targets[0].Streams.Single(stream => stream.Name == ":private:$DATA").Length);
        CollectionAssert.AreEqual(alphaBytes, File.ReadAllBytes(alphaStreamPath));
        CollectionAssert.AreEqual(namedBytes, File.ReadAllBytes(namedStreamPath));
    }

    [TestMethod]
    public async Task NtfsDirectoryDataStreamsAreIncludedInTheDirectorySnapshot()
    {
        using var sandbox = InventorySandbox.Create();
        var streamPath = $"{sandbox.Path}:directory-metadata:$DATA";
        var streamBytes = "directory stream"u8.ToArray();
        File.WriteAllBytes(streamPath, streamBytes);

        var result = await new WindowsTargetInventory().ScanAsync(
            [sandbox.Path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        var directory = result.Targets.Single(target => target.Path == sandbox.Path);
        Assert.IsTrue(directory.Streams.Any(stream =>
            stream.Name == ":directory-metadata:$DATA"
            && stream.Length == streamBytes.LongLength));
        CollectionAssert.AreEqual(streamBytes, File.ReadAllBytes(streamPath));
    }

    [TestMethod]
    public async Task AlternateDataStreamCannotBeSelectedAsAStandaloneSource()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("source-stream.bin", "default canary");
        var streamPath = $"{path}:private:$DATA";
        File.WriteAllText(streamPath, "stream canary");

        var result = await new WindowsTargetInventory().ScanAsync(
            [streamPath],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking));
        Assert.AreEqual("default canary", File.ReadAllText(path));
        Assert.AreEqual("stream canary", File.ReadAllText(streamPath));
    }

    [TestMethod]
    public async Task LockedNamedDataStreamProducesABlockingIssue()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("locked-stream.bin", "default stream");
        var namedStreamPath = $"{path}:locked:$DATA";
        File.WriteAllText(namedStreamPath, "locked stream");
        await using var lockStream = new FileStream(
            namedStreamPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking
            && issue.Code == PreflightIssueCode.UnsupportedLayout));
        Assert.IsEmpty(result.Targets);
    }

    [TestMethod]
    public async Task DirectorySymbolicLinkIsInventoriedWithoutFollowingItsTarget()
    {
        using var targetSandbox = InventorySandbox.Create();
        using var sourceSandbox = InventorySandbox.Create();
        var canary = targetSandbox.CreateFile("must-not-be-inventoried.txt", "link target");
        var linkPath = System.IO.Path.Combine(sourceSandbox.Path, "linked-directory");
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetSandbox.Path);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
        {
            Assert.Inconclusive($"Directory symbolic links are unavailable: {exception.Message}");
            return;
        }

        var result = await new WindowsTargetInventory().ScanAsync(
            [sourceSandbox.Path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(2, result.Targets);
        var link = result.Targets.Single(target => target.Path == linkPath);
        Assert.AreEqual(TargetKind.SymbolicLink, link.Kind);
        Assert.AreNotEqual(0U, link.ReparseTag);
        Assert.IsFalse(result.Targets.Any(target => target.Path == canary));
        Assert.IsTrue(File.Exists(canary));
    }

    [TestMethod]
    public async Task JunctionIsInventoriedWithoutFollowingItsTarget()
    {
        using var targetSandbox = InventorySandbox.Create();
        using var sourceSandbox = InventorySandbox.Create();
        var canary = targetSandbox.CreateFile("must-not-be-inventoried.txt", "junction target");
        var junctionPath = System.IO.Path.Combine(sourceSandbox.Path, "junction");
        CreateJunction(junctionPath, targetSandbox.Path);

        var result = await new WindowsTargetInventory().ScanAsync(
            [sourceSandbox.Path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(2, result.Targets);
        var junction = result.Targets.Single(target => target.Path == junctionPath);
        Assert.AreEqual(TargetKind.Junction, junction.Kind);
        Assert.AreNotEqual(0U, junction.ReparseTag);
        Assert.IsFalse(result.Targets.Any(target => target.Path == canary));
        Assert.IsTrue(File.Exists(canary));
    }

    [TestMethod]
    public async Task SourcePathReachedThroughJunctionIsBlocked()
    {
        using var targetSandbox = InventorySandbox.Create();
        using var sourceSandbox = InventorySandbox.Create();
        var canary = targetSandbox.CreateFile("junction-child.bin", "junction canary");
        var junctionPath = System.IO.Path.Combine(sourceSandbox.Path, "junction");
        CreateJunction(junctionPath, targetSandbox.Path);
        var pathThroughJunction = System.IO.Path.Combine(junctionPath, "junction-child.bin");

        var result = await new WindowsTargetInventory().ScanAsync(
            [pathThroughJunction],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking
            && issue.Code == PreflightIssueCode.UnsupportedLayout));
        Assert.AreEqual("junction canary", File.ReadAllText(canary));
    }

    [TestMethod]
    public async Task OverlappingSourcesAreCollapsedByHandleIdentity()
    {
        using var sandbox = InventorySandbox.Create();
        var file = sandbox.CreateFile("duplicate.bin", "one physical target");

        var result = await new WindowsTargetInventory().ScanAsync(
            [sandbox.Path, file],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(2, result.Targets);
        Assert.HasCount(1, result.Targets.Where(target => target.Path == file));
    }

    [TestMethod]
    public async Task UncPathIsBlockedWithoutInventoryingAnyTarget()
    {
        var result = await new WindowsTargetInventory().ScanAsync(
            [@"\\localhost\C$\Nyx\must-not-be-contacted.bin"],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking));
    }

    [TestMethod]
    public async Task LockedFileIsBlockedWithoutChangingItsContent()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("locked.bin", "locked canary");
        var originalBytes = File.ReadAllBytes(path);
        await using var lockStream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking
            && issue.Code == PreflightIssueCode.UnsupportedLayout));
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(path));
    }

    [TestMethod]
    public async Task ReadOnlyFileIsInventoriedForTheWarningPath()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("read-only.bin", "read-only canary");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        Assert.AreNotEqual(
            (FileAttributes)0,
            result.Targets[0].Attributes & FileAttributes.ReadOnly);
        Assert.AreEqual("read-only canary", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task OfflineCloudEvidenceIsBlockedWithoutOpeningFileData()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("offline.bin", "offline canary");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Code == PreflightIssueCode.CloudPlaceholder
            && issue.Severity == PreflightSeverity.Blocking));
        Assert.AreEqual("offline canary", File.ReadAllText(path));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task OrdinaryLocalFileInOneDriveRootUsesCloudApiNoStatesResult()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("local-cloud-copy.bin", "local cloud canary");
        var previousOneDrive = Environment.GetEnvironmentVariable("OneDrive");
        try
        {
            Environment.SetEnvironmentVariable("OneDrive", sandbox.Path);

            var result = await new WindowsTargetInventory().ScanAsync(
                [path],
                CancellationToken.None);

            Assert.IsEmpty(
                result.Issues,
                string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
            Assert.HasCount(1, result.Targets);
            Assert.IsTrue(result.Targets[0].IsOneDrive);
            Assert.IsFalse(result.Targets[0].IsCloudPlaceholder);
            Assert.IsTrue(result.Targets[0].IsFullyHydratedCloudFile);
            Assert.AreEqual("local cloud canary", File.ReadAllText(path));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDrive", previousOneDrive);
        }
    }

    [TestMethod]
    public async Task HardLinkCountComesFromTheOpenedFileHandle()
    {
        using var sandbox = InventorySandbox.Create();
        var originalPath = sandbox.CreateFile("original.bin", "hard-link canary");
        var hardLinkPath = System.IO.Path.Combine(sandbox.Path, "hard-link.bin");
        CreateHardLink(hardLinkPath, originalPath);

        var result = await new WindowsTargetInventory().ScanAsync(
            [originalPath],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        Assert.AreEqual(2, result.Targets[0].LinkCount);
        Assert.AreEqual(File.ReadAllText(originalPath), File.ReadAllText(hardLinkPath));
    }

    [TestMethod]
    public async Task LongFilePathIsInventoriedThroughTheExtendedPathApi()
    {
        using var sandbox = InventorySandbox.Create();
        var segment = new string('n', 80);
        var relativeDirectory = System.IO.Path.Combine(segment, segment, segment);
        sandbox.CreateDirectory(relativeDirectory);
        var path = sandbox.CreateFile(
            System.IO.Path.Combine(relativeDirectory, "long-path-canary.bin"),
            "long path canary");
        Assert.IsGreaterThan(260, path.Length);

        var result = await new WindowsTargetInventory().ScanAsync(
            [path],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.HasCount(1, result.Targets);
        Assert.AreEqual(path, result.Targets[0].Path);
        Assert.AreEqual("long path canary", File.ReadAllText(path));
    }

    [TestMethod]
    public async Task NonRootDirectoryTrailingSeparatorIsRemovedFromTheSnapshotPath()
    {
        using var sandbox = InventorySandbox.Create();
        var pathWithSeparator = sandbox.Path + Path.DirectorySeparatorChar;

        var result = await new WindowsTargetInventory().ScanAsync(
            [pathWithSeparator],
            CancellationToken.None);

        Assert.IsEmpty(
            result.Issues,
            string.Join(Environment.NewLine, result.Issues.Select(issue => issue.Message)));
        Assert.AreEqual(sandbox.Path, result.Targets.Single(target =>
            target.Kind == TargetKind.Directory).Path);
    }

    [TestMethod]
    public async Task SubstStyleDosDeviceAliasIsBlocked()
    {
        using var sandbox = InventorySandbox.Create();
        var realPath = sandbox.CreateFile("subst-canary.bin", "subst canary");
        var usedRoots = DriveInfo.GetDrives()
            .Select(static drive => drive.Name[..2])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deviceName = Enumerable.Range('D', 'Z' - 'D' + 1)
            .Select(static value => $"{(char)('Z' - (value - 'D'))}:")
            .First(name => !usedRoots.Contains(name));
        var rawTarget = $@"\??\{sandbox.Path}";
        const uint defineFlags = 0x00000001 | 0x00000008;
        const uint removeFlags = defineFlags | 0x00000002 | 0x00000004;
        if (!DefineDosDevice(defineFlags, deviceName, rawTarget))
        {
            Assert.Inconclusive(
                $"A local DOS device alias could not be created (Win32 {Marshal.GetLastWin32Error()}).");
        }

        try
        {
            var aliasPath = $@"{deviceName}\{Path.GetFileName(realPath)}";

            var result = await new WindowsTargetInventory().ScanAsync(
                [aliasPath],
                CancellationToken.None);

            Assert.IsEmpty(result.Targets);
            Assert.IsTrue(result.Issues.Any(issue =>
                issue.Severity == PreflightSeverity.Blocking));
            Assert.AreEqual("subst canary", File.ReadAllText(realPath));
        }
        finally
        {
            _ = DefineDosDevice(removeFlags, deviceName, rawTarget);
        }
    }

    [TestMethod]
    public async Task EightDotThreeAliasIsBlockedInsteadOfBypassingProtectedPathChecks()
    {
        using var sandbox = InventorySandbox.Create();
        var realPath = sandbox.CreateFile(
            "a-very-long-sensitive-filename-for-nyx.bin",
            "short-name canary");
        var buffer = new char[32_768];
        var length = GetShortPathName(realPath, buffer, checked((uint)buffer.Length));
        if (length == 0 || length >= buffer.Length)
        {
            Assert.Inconclusive(
                $"An 8.3 alias is unavailable (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var shortPath = new string(buffer, 0, checked((int)length));
        if (string.Equals(shortPath, realPath, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("8.3 name creation is disabled on this test volume.");
        }

        var result = await new WindowsTargetInventory().ScanAsync(
            [shortPath],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets, $"Alias '{shortPath}' must not be accepted as '{realPath}'.");
        Assert.IsTrue(result.Issues.Any(issue =>
            issue.Severity == PreflightSeverity.Blocking));
        Assert.AreEqual("short-name canary", File.ReadAllText(realPath));
    }

    [TestMethod]
    public async Task EightDotThreeDirectoryAliasIsBlockedBeforeTraversingItsChildren()
    {
        using var sandbox = InventorySandbox.Create();
        var directory = sandbox.CreateDirectory("a-long-sensitive-folder-name-for-nyx");
        var canaryPath = Path.Combine(directory, "child.txt");
        File.WriteAllText(canaryPath, "short-directory-name canary");
        var buffer = new char[32_768];
        var length = GetShortPathName(directory, buffer, checked((uint)buffer.Length));
        if (length == 0 || length >= buffer.Length)
        {
            Assert.Inconclusive(
                $"An 8.3 directory alias is unavailable (Win32 {Marshal.GetLastWin32Error()}).");
        }

        var shortPath = new string(buffer, 0, checked((int)length));
        if (string.Equals(shortPath, directory, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Inconclusive("8.3 name creation is disabled on this test volume.");
        }

        var result = await new WindowsTargetInventory().ScanAsync(
            [shortPath],
            CancellationToken.None);

        Assert.IsEmpty(result.Targets);
        Assert.IsTrue(result.Issues.Any(issue => issue.Severity == PreflightSeverity.Blocking));
        Assert.AreEqual("short-directory-name canary", File.ReadAllText(canaryPath));
    }

    [TestMethod]
    public async Task ActualLongFileNameContainingTildeIsInventoriedWithoutChangingContent()
    {
        using var sandbox = InventorySandbox.Create();
        var path = sandbox.CreateFile("a-long-name~with-a-literal-tilde.txt", "literal-tilde canary");

        var result = await new WindowsTargetInventory().ScanAsync([path], CancellationToken.None);

        Assert.IsEmpty(result.Issues);
        Assert.HasCount(1, result.Targets);
        Assert.AreEqual(path, result.Targets[0].Path);
        Assert.AreEqual("literal-tilde canary", File.ReadAllText(path));
    }

    private static void CreateJunction(string junctionPath, string targetPath)
    {
        CreateLink("/J", junctionPath, targetPath);
    }

    [DllImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DefineDosDevice(
        uint flags,
        string deviceName,
        string targetPath);

    [DllImport("kernel32.dll", EntryPoint = "GetShortPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetShortPathName(
        string longPath,
        [Out] char[] shortPath,
        uint bufferLength);

    private static void CreateHardLink(string hardLinkPath, string targetPath)
    {
        CreateLink("/H", hardLinkPath, targetPath);
    }

    private static void CreateLink(string linkType, string linkPath, string targetPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add(linkType);
        startInfo.ArgumentList.Add(linkPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("cmd.exe could not be started.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(
            0,
            process.ExitCode,
            $"mklink failed. {standardOutput} {standardError}");
    }

    private sealed class InventorySandbox : IDisposable
    {
        private InventorySandbox(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static InventorySandbox Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"Nyx.Inventory.{Guid.NewGuid():N}");
            Directory.CreateDirectory(path);
            return new InventorySandbox(path);
        }

        public string CreateFile(string name, string content)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, content);
            return path;
        }

        public string CreateDirectory(string name)
        {
            var path = System.IO.Path.Combine(Path, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try
            {
                DeleteTreeWithoutFollowingReparsePoints(Path);
            }
            catch (IOException)
            {
                // A failed cleanup must not hide inventory behavior.
            }
            catch (UnauthorizedAccessException)
            {
                // A failed cleanup must not hide inventory behavior.
            }
        }

        private static void DeleteTreeWithoutFollowingReparsePoints(string directory)
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        Directory.Delete(entry);
                    }
                    else
                    {
                        File.Delete(entry);
                    }

                    continue;
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    DeleteTreeWithoutFollowingReparsePoints(entry);
                }
                else
                {
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                    }

                    File.Delete(entry);
                }
            }

            Directory.Delete(directory);
        }
    }
}
