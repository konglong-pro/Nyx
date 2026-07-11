namespace Nyx.Core.Preflight;

internal enum PreflightSeverity
{
    Warning,
    Blocking,
}

internal enum PreflightIssueCode
{
    ProtectedPath,
    UnsupportedFileSystem,
    LimitedMedium,
    HardLink,
    UnsupportedLayout,
    CloudPlaceholder,
    ReadOnly,
    CloudCopies,
    LinkOnly,
    TargetChanged,
}

internal sealed record PreflightIssue(
    PreflightIssueCode Code,
    PreflightSeverity Severity,
    string Message);

internal sealed class PathSafetyPolicy
{
    private static readonly string[] PerVolumeProtectedTreeNames =
    [
        "Boot",
        "Recovery",
        "System Volume Information",
        "$Recycle.Bin",
    ];

    private readonly StringComparison _pathComparison;
    private readonly string _applicationExecutablePath;
    private readonly string[] _protectedExactPaths;
    private readonly string[] _protectedTrees;

    private PathSafetyPolicy(
        string applicationExecutablePath,
        IEnumerable<string> protectedExactPaths,
        IEnumerable<string> protectedTrees)
    {
        _pathComparison = StringComparison.OrdinalIgnoreCase;
        _applicationExecutablePath = Path.GetFullPath(applicationExecutablePath);
        _protectedExactPaths = protectedExactPaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
        _protectedTrees = protectedTrees
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();
    }

    public static PathSafetyPolicy CreateForCurrentMachine(string applicationExecutablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationExecutablePath);
        var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var systemDrive = Path.GetPathRoot(windowsDirectory)!;

        return new PathSafetyPolicy(
        applicationExecutablePath,
        [
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.Combine(systemDrive, "Users"),
        ],
        [
            windowsDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(systemDrive, "Boot"),
            Path.Combine(systemDrive, "Recovery"),
            Path.Combine(systemDrive, "System Volume Information"),
            Path.Combine(systemDrive, "$Recycle.Bin"),
        ]);
    }

    public PreflightIssue? Evaluate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);

        if (string.Equals(
            Path.TrimEndingDirectorySeparator(fullPath),
            Path.TrimEndingDirectorySeparator(root!),
            _pathComparison))
        {
            return new PreflightIssue(
                PreflightIssueCode.ProtectedPath,
                PreflightSeverity.Blocking,
                $"'{fullPath}': volume roots cannot be shredded.");
        }

        var isPerVolumeProtectedTree = PerVolumeProtectedTreeNames.Any(name =>
            IsSameOrDescendant(fullPath, Path.Combine(root!, name)));
        var isPerVolumeUsersRoot = string.Equals(
            Path.TrimEndingDirectorySeparator(fullPath),
            Path.TrimEndingDirectorySeparator(Path.Combine(root!, "Users")),
            _pathComparison);

        return IsSameOrDescendant(_applicationExecutablePath, fullPath)
               || _protectedExactPaths.Any(candidate =>
                   string.Equals(
                       Path.TrimEndingDirectorySeparator(candidate),
                       Path.TrimEndingDirectorySeparator(fullPath),
                       _pathComparison))
               || _protectedTrees.Any(tree => IsSameOrDescendant(fullPath, tree))
               || isPerVolumeProtectedTree
               || isPerVolumeUsersRoot
            ? new PreflightIssue(
                PreflightIssueCode.ProtectedPath,
                PreflightSeverity.Blocking,
                $"'{fullPath}': protected Windows and Nyx paths cannot be shredded.")
            : null;
    }

    private bool IsSameOrDescendant(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative == "."
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", _pathComparison)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    _pathComparison)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    _pathComparison));
    }
}
