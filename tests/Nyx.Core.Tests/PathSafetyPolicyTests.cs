using Nyx.Core.Preflight;

namespace Nyx.Core.Tests;

[TestClass]
public sealed class PathSafetyPolicyTests
{
    [TestMethod]
    public void VolumeRootIsABlockingTarget()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");

        var issue = policy.Evaluate(@"C:\");

        Assert.IsNotNull(issue);
        Assert.AreEqual(PreflightSeverity.Blocking, issue.Severity);
        Assert.AreEqual(PreflightIssueCode.ProtectedPath, issue.Code);
    }

    [TestMethod]
    public void WindowsDirectoryTreeIsBlocking()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");
        var systemFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "kernel32.dll");

        var issue = policy.Evaluate(systemFile);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PreflightIssueCode.ProtectedPath, issue.Code);
    }

    [TestMethod]
    public void UserProfileRootIsBlocking()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var issue = policy.Evaluate(profile);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PreflightIssueCode.ProtectedPath, issue.Code);
    }

    [TestMethod]
    public void OtherSystemTreesAreBlocking()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");
        var systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;
        string[] protectedCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Example", "app.dll"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Example", "data.bin"),
            Path.Combine(systemDrive, "Recovery", "state.bin"),
            Path.Combine(systemDrive, "System Volume Information", "tracking.log"),
            Path.Combine(systemDrive, "$Recycle.Bin", "deleted.bin"),
        ];

        foreach (var candidate in protectedCandidates)
        {
            var issue = policy.Evaluate(candidate);
            Assert.IsNotNull(issue, $"Expected protected path: {candidate}");
        }
    }

    [TestMethod]
    public void PerVolumeMetadataTreesAreBlockingOnNonSystemDrives()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");
        string[] protectedCandidates =
        [
            @"Z:\$Recycle.Bin\deleted.bin",
            @"Z:\System Volume Information\tracking.log",
            @"Z:\Boot\BCD",
            @"Z:\Recovery\state.bin",
            @"Z:\Users",
        ];

        foreach (var candidate in protectedCandidates)
        {
            Assert.IsNotNull(
                policy.Evaluate(candidate),
                $"Expected protected path on a non-system drive: {candidate}");
        }
    }

    [TestMethod]
    public void DirectoryContainingNyxExecutableIsBlocking()
    {
        var applicationDirectory = Path.Combine(Path.GetTempPath(), "Nyx-policy-test", "app");
        var applicationExecutable = Path.Combine(applicationDirectory, "Nyx.exe");
        var policy = PathSafetyPolicy.CreateForCurrentMachine(applicationExecutable);

        var issue = policy.Evaluate(applicationDirectory);

        Assert.IsNotNull(issue);
        Assert.AreEqual(PreflightIssueCode.ProtectedPath, issue.Code);
    }

    [TestMethod]
    public void OrdinaryUserProfileChildIsAllowed()
    {
        var policy = PathSafetyPolicy.CreateForCurrentMachine(@"E:\Nyx\src\Nyx.App\Nyx.exe");
        var ordinaryFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "ordinary.txt");

        var issue = policy.Evaluate(ordinaryFile);

        Assert.IsNull(issue);
    }
}
