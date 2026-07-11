using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Nyx.Core.Preflight;

namespace Nyx.Core.Windows;

internal sealed class WindowsTargetInventory : ITargetInventory
{
    private const int InitialStreamBufferSize = 64 * 1024;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const int MaximumReparseDataBufferSize = 16 * 1024;
    private const FileAttributes FileAttributeRecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes FileAttributePinned = (FileAttributes)0x00080000;
    private const FileAttributes FileAttributeUnpinned = (FileAttributes)0x00100000;
    private const FileAttributes FileAttributeRecallOnDataAccess = (FileAttributes)0x00400000;

    public Task<InventoryResult> ScanAsync(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(sourcePaths);
        return Task.Run(() => Scan(sourcePaths, cancellationToken), cancellationToken);
    }

    private static InventoryResult Scan(
        IReadOnlyList<string> sourcePaths,
        CancellationToken cancellationToken)
    {
        var targets = new List<TargetSnapshot>();
        var issues = new List<PreflightIssue>();
        var seenIdentities = new HashSet<FileIdentity>();
        var volumes = new Dictionary<string, VolumeContext>(StringComparer.OrdinalIgnoreCase);

        foreach (var sourcePath in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScanOne(
                sourcePath,
                targets,
                issues,
                seenIdentities,
                volumes,
                inheritedVolume: null,
                cancellationToken);
        }

        return new InventoryResult(targets, issues);
    }

    private static void ScanOne(
        string sourcePath,
        List<TargetSnapshot> targets,
        List<PreflightIssue> issues,
        HashSet<FileIdentity> seenIdentities,
        Dictionary<string, VolumeContext> volumes,
        VolumeContext? inheritedVolume,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeLocalPath(sourcePath, out var path, out var normalizationError))
        {
            issues.Add(CreateBlockingIssue(sourcePath, normalizationError));
            return;
        }

        var pathRoot = Path.GetPathRoot(path)!;
        var rootDriveType = WindowsNativeMethods.GetDriveType(pathRoot);
        if (rootDriveType is not (
            WindowsNativeMethods.DriveFixed
            or WindowsNativeMethods.DriveRemovable))
        {
            issues.Add(new PreflightIssue(
                PreflightIssueCode.UnsupportedFileSystem,
                PreflightSeverity.Blocking,
                $"'{path}' is not on a local fixed or removable drive."));
            return;
        }

        if (inheritedVolume is null
            && !TryValidateDirectDriveMapping(pathRoot, out var mappingIssue))
        {
            issues.Add(mappingIssue!);
            return;
        }

        if (!TryValidatePathAncestors(path, out var ancestorIssue))
        {
            issues.Add(ancestorIssue!);
            return;
        }

        var nativePath = ToExtendedPath(path);
        var rawAttributes = WindowsNativeMethods.GetFileAttributes(nativePath);
        if (rawAttributes == WindowsNativeMethods.InvalidFileAttributes)
        {
            issues.Add(CreateLastErrorIssue(path, "The target could not be inspected"));
            return;
        }

        var attributesFromPath = (FileAttributes)rawAttributes;
        var pathIsReparsePoint = (attributesFromPath & FileAttributes.ReparsePoint) != 0;
        var initialReparseTag = 0U;
        if (pathIsReparsePoint)
        {
            try
            {
                initialReparseTag = ReadPathReparseTag(nativePath);
            }
            catch (Win32Exception exception)
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.UnsupportedLayout,
                    PreflightSeverity.Blocking,
                    $"The reparse point '{path}' could not be inspected (Win32 {exception.NativeErrorCode})."));
                return;
            }

            if (!IsSupportedReparseTag(initialReparseTag))
            {
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.UnsupportedLayout,
                    PreflightSeverity.Blocking,
                    $"Reparse tag 0x{initialReparseTag:X8} for '{path}' is not supported."));
                return;
            }
        }

        var isOneDrivePath = IsOneDrivePath(path);
        var hasCloudAttributes = HasAnyAttribute(
            attributesFromPath,
            FileAttributes.Offline
                | FileAttributeRecallOnOpen
                | FileAttributeRecallOnDataAccess
                | FileAttributePinned
                | FileAttributeUnpinned);
        var hasCloudEvidence = isOneDrivePath
            || IsCloudReparseTag(initialReparseTag)
            || hasCloudAttributes;
        var placeholderState = hasCloudEvidence
            ? GetPlaceholderState(rawAttributes, initialReparseTag)
            : WindowsNativeMethods.CfPlaceholderStateNoStates;
        var isCloudPlaceholder = HasPlaceholderState(
            placeholderState,
            WindowsNativeMethods.CfPlaceholderStatePlaceholder);
        var isCloudFile = hasCloudEvidence || isCloudPlaceholder;
        var hasUnavailableCloudAttributes = HasUnavailableCloudAttributes(
            attributesFromPath);
        var placeholderIsConfirmedLocal = IsConfirmedLocalPlaceholder(
            attributesFromPath,
            placeholderState);
        var ordinaryOneDriveFileIsLocal = isOneDrivePath
            && placeholderState == WindowsNativeMethods.CfPlaceholderStateNoStates
            && !hasUnavailableCloudAttributes;
        var isFullyHydratedCloudFile = isCloudFile
            && placeholderState != WindowsNativeMethods.CfPlaceholderStateInvalid
            && (placeholderIsConfirmedLocal || ordinaryOneDriveFileIsLocal);
        if (isCloudFile && !isFullyHydratedCloudFile)
        {
            issues.Add(new PreflightIssue(
                PreflightIssueCode.CloudPlaceholder,
                PreflightSeverity.Blocking,
                $"Cloud target '{path}' is not confirmed as fully available on this device."));
            return;
        }

        var volumeProbePath = pathIsReparsePoint
            ? Path.GetDirectoryName(path) ?? path
            : path;
        VolumeContext volume;
        if (inheritedVolume is { } parentVolume)
        {
            volume = parentVolume;
        }
        else if (!TryGetVolume(
                     volumeProbePath,
                     volumes,
                     out volume,
                     out var volumeError))
        {
            issues.Add(volumeError!);
            return;
        }

        var desiredAccess = HasAnyAttribute(attributesFromPath, FileAttributes.ReadOnly)
            ? WindowsNativeMethods.GenericRead
                | WindowsNativeMethods.FileWriteAttributes
                | WindowsNativeMethods.Delete
            : WindowsNativeMethods.GenericRead
                | WindowsNativeMethods.GenericWrite
                | WindowsNativeMethods.Delete;
        using var handle = WindowsNativeMethods.CreateFile(
            nativePath,
            desiredAccess,
            WindowsNativeMethods.FileShareRead
                | WindowsNativeMethods.FileShareDelete,
            0,
            WindowsNativeMethods.OpenExisting,
            WindowsNativeMethods.FileFlagBackupSemantics
                | WindowsNativeMethods.FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            issues.Add(CreateLastErrorIssue(path, "The target could not be opened for shredding"));
            return;
        }

        if (!TryValidateCanonicalPath(handle, path, out var canonicalIssue))
        {
            issues.Add(canonicalIssue!);
            return;
        }

        if (!WindowsNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            issues.Add(CreateLastErrorIssue(path, "Handle metadata could not be read"));
            return;
        }

        if (information.VolumeSerialNumber != volume.SerialNumber)
        {
            issues.Add(new PreflightIssue(
                PreflightIssueCode.UnsupportedLayout,
                PreflightSeverity.Blocking,
                $"The volume identity for '{path}' changed during inspection."));
            return;
        }

        if (information.FileIndexHigh == 0 && information.FileIndexLow == 0)
        {
            issues.Add(new PreflightIssue(
                PreflightIssueCode.UnsupportedLayout,
                PreflightSeverity.Blocking,
                $"Windows did not provide a reliable file identity for '{path}'."));
            return;
        }

        var identity = new FileIdentity(
            volume.VolumeId,
            information.FileIndexHigh,
            information.FileIndexLow);
        if (seenIdentities.Contains(identity))
        {
            return;
        }

        var attributes = (FileAttributes)information.FileAttributes;
        var isDirectory = (attributes & FileAttributes.Directory) != 0;
        var isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        var reparseTag = 0U;
        var kind = isDirectory ? TargetKind.Directory : TargetKind.File;
        if (isReparsePoint)
        {
            try
            {
                reparseTag = ReadReparseTag(handle);
                if (reparseTag != initialReparseTag)
                {
                    issues.Add(new PreflightIssue(
                        PreflightIssueCode.UnsupportedLayout,
                        PreflightSeverity.Blocking,
                        $"The reparse identity for '{path}' changed during inspection."));
                    return;
                }

                kind = ClassifyReparsePoint(handle, reparseTag, kind);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidDataException)
            {
                var nativeError = exception is Win32Exception win32Exception
                    ? $" (Win32 {win32Exception.NativeErrorCode})"
                    : string.Empty;
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.UnsupportedLayout,
                    PreflightSeverity.Blocking,
                    $"The reparse point '{path}' could not be classified{nativeError}."));
                return;
            }
        }

        var length = isDirectory
            ? 0
            : CombineUnsigned(information.FileSizeHigh, information.FileSizeLow);
        IReadOnlyList<DataStreamSnapshot> streams;
        try
        {
            streams = kind is (
                TargetKind.SymbolicLink
                or TargetKind.Junction
                or TargetKind.MountPoint)
                ? []
                : volume.FileSystem == FileSystemKind.Ntfs
                ? ReadStreams(handle, cancellationToken)
                : isDirectory
                    ? []
                    : [new DataStreamSnapshot("::$DATA", length)];
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
        {
            var errorDetail = exception is Win32Exception win32Exception
                ? $" (Win32 {win32Exception.NativeErrorCode})"
                : string.Empty;
            issues.Add(new PreflightIssue(
                PreflightIssueCode.UnsupportedLayout,
                PreflightSeverity.Blocking,
                $"Data streams for '{path}' could not be enumerated{errorDetail}."));
            return;
        }

        seenIdentities.Add(identity);
        targets.Add(new TargetSnapshot
        {
            Path = path,
            Kind = kind,
            VolumeId = volume.VolumeId,
            FileId = $"{information.FileIndexHigh:X8}{information.FileIndexLow:X8}",
            FileSystem = volume.FileSystem,
            Medium = volume.Medium,
            Length = length,
            Attributes = attributes,
            ReparseTag = reparseTag,
            LinkCount = information.NumberOfLinks > int.MaxValue
                ? int.MaxValue
                : (int)information.NumberOfLinks,
            LastWriteTimeUtc = FromFileTime(information.LastWriteTime),
            IsOneDrive = isCloudFile,
            IsCloudPlaceholder = isCloudPlaceholder,
            IsFullyHydratedCloudFile = isFullyHydratedCloudFile,
            Streams = streams,
        });

        if (isReparsePoint
            && reparseTag is not (
                WindowsNativeMethods.IoReparseTagSymbolicLink
                or WindowsNativeMethods.IoReparseTagMountPoint)
            && !IsCloudReparseTag(reparseTag))
        {
            issues.Add(new PreflightIssue(
                PreflightIssueCode.UnsupportedLayout,
                PreflightSeverity.Blocking,
                $"Reparse tag 0x{reparseTag:X8} for '{path}' is not supported."));
        }

        if (isDirectory && (!isReparsePoint || IsCloudReparseTag(reparseTag)))
        {
            List<string> childNames;
            try
            {
                childNames = ReadDirectoryNames(handle, cancellationToken);
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidDataException or OverflowException)
            {
                var errorDetail = exception is Win32Exception win32Exception
                    ? $" (Win32 {win32Exception.NativeErrorCode})"
                    : string.Empty;
                issues.Add(new PreflightIssue(
                    PreflightIssueCode.UnsupportedLayout,
                    PreflightSeverity.Blocking,
                    $"Directory '{path}' could not be completely enumerated{errorDetail}."));
                return;
            }

            foreach (var childName in childNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ScanOne(
                    Path.Combine(path, childName),
                    targets,
                    issues,
                    seenIdentities,
                    volumes,
                    volume,
                    cancellationToken);
            }
        }
    }

    private static uint ReadPathReparseTag(string nativePath)
    {
        using var metadataHandle = WindowsNativeMethods.CreateFile(
            nativePath,
            WindowsNativeMethods.FileReadAttributes,
            WindowsNativeMethods.FileShareRead
                | WindowsNativeMethods.FileShareWrite
                | WindowsNativeMethods.FileShareDelete,
            0,
            WindowsNativeMethods.OpenExisting,
            WindowsNativeMethods.FileFlagBackupSemantics
                | WindowsNativeMethods.FileFlagOpenReparsePoint,
            0);
        if (metadataHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return ReadReparseTag(metadataHandle);
    }

    private static bool TryValidateDirectDriveMapping(
        string pathRoot,
        out PreflightIssue? issue)
    {
        issue = null;
        if (pathRoot.Length < 2 || pathRoot[1] != ':')
        {
            issue = CreateBlockingIssue(pathRoot, "Only direct local drive paths are supported.");
            return false;
        }

        var buffer = new char[32_768];
        var length = WindowsNativeMethods.QueryDosDevice(
            pathRoot[..2],
            buffer,
            checked((uint)buffer.Length));
        if (length == 0)
        {
            issue = CreateLastErrorIssue(pathRoot, "The drive mapping could not be verified");
            return false;
        }

        var target = NullTerminatedString(buffer);
        if (target.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase)
            || target.StartsWith(@"\DosDevices\", StringComparison.OrdinalIgnoreCase))
        {
            issue = CreateBlockingIssue(
                pathRoot,
                "SUBST and other redirected DOS device paths are not supported.");
            return false;
        }

        return true;
    }

    private static bool TryValidateCanonicalPath(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        string expectedPath,
        out PreflightIssue? issue)
    {
        issue = null;
        var buffer = new char[32_768];
        var length = WindowsNativeMethods.GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            0);
        if (length == 0 || length >= buffer.Length)
        {
            issue = length == 0
                ? CreateLastErrorIssue(expectedPath, "The canonical target path could not be verified")
                : CreateBlockingIssue(expectedPath, "The canonical target path is too long to verify.");
            return false;
        }

        var canonicalPath = new string(buffer, 0, checked((int)length));
        const string extendedPrefix = @"\\?\";
        if (canonicalPath.StartsWith(extendedPrefix, StringComparison.Ordinal))
        {
            canonicalPath = canonicalPath[extendedPrefix.Length..];
        }

        string normalizedCanonical;
        try
        {
            normalizedCanonical = Path.GetFullPath(canonicalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            issue = CreateBlockingIssue(expectedPath, "The canonical target path is not a supported DOS path.");
            return false;
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(normalizedCanonical),
                Path.TrimEndingDirectorySeparator(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            issue = CreateBlockingIssue(
                expectedPath,
                "Path aliases, short names, and paths resolved through another directory are not supported.");
            return false;
        }

        return true;
    }

    private static bool TryValidatePathAncestors(
        string path,
        out PreflightIssue? issue)
    {
        issue = null;
        var root = Path.GetPathRoot(path)!;
        var relativePath = Path.GetRelativePath(root, path);
        var components = relativePath.Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        var currentPath = root;

        for (var index = 0; index < components.Length - 1; index++)
        {
            currentPath = Path.Combine(currentPath, components[index]);
            var rawAttributes = WindowsNativeMethods.GetFileAttributes(
                ToExtendedPath(currentPath));
            if (rawAttributes == WindowsNativeMethods.InvalidFileAttributes)
            {
                issue = CreateLastErrorIssue(
                    currentPath,
                    "A path ancestor could not be inspected");
                return false;
            }

            var attributes = (FileAttributes)rawAttributes;
            if (!HasAnyAttribute(attributes, FileAttributes.ReparsePoint))
            {
                continue;
            }

            uint reparseTag;
            try
            {
                reparseTag = ReadPathReparseTag(ToExtendedPath(currentPath));
            }
            catch (Win32Exception exception)
            {
                issue = new PreflightIssue(
                    PreflightIssueCode.UnsupportedLayout,
                    PreflightSeverity.Blocking,
                    $"Path ancestor '{currentPath}' could not be classified (Win32 {exception.NativeErrorCode}).");
                return false;
            }

            if (IsCloudReparseTag(reparseTag))
            {
                var placeholderState = GetPlaceholderState(rawAttributes, reparseTag);
                if (IsConfirmedLocalPlaceholder(
                        (FileAttributes)rawAttributes,
                        placeholderState))
                {
                    continue;
                }

                issue = new PreflightIssue(
                    PreflightIssueCode.CloudPlaceholder,
                    PreflightSeverity.Blocking,
                    $"Cloud path ancestor '{currentPath}' could not be safely classified without hydration.");
                return false;
            }

            issue = new PreflightIssue(
                PreflightIssueCode.UnsupportedLayout,
                PreflightSeverity.Blocking,
                $"Target '{path}' is reached through reparse point '{currentPath}'.");
            return false;
        }

        return true;
    }

    private static uint ReadReparseTag(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        var buffer = new byte[8];
        if (!WindowsNativeMethods.GetFileInformationByHandleEx(
                handle,
                WindowsNativeMethods.FileAttributeTagInfo,
                buffer,
                (uint)buffer.Length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(4, 4));
    }

    private static TargetKind ClassifyReparsePoint(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        uint reparseTag,
        TargetKind defaultKind)
    {
        if (reparseTag == WindowsNativeMethods.IoReparseTagSymbolicLink)
        {
            return TargetKind.SymbolicLink;
        }

        if (reparseTag != WindowsNativeMethods.IoReparseTagMountPoint)
        {
            return defaultKind;
        }

        var buffer = new byte[MaximumReparseDataBufferSize];
        if (!WindowsNativeMethods.GetReparsePoint(
                handle,
                WindowsNativeMethods.FsctlGetReparsePoint,
                0,
                0,
                buffer,
                (uint)buffer.Length,
                out var bytesReturned,
                0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        const int mountPointHeaderSize = 16;
        if (bytesReturned < mountPointHeaderSize)
        {
            throw new InvalidDataException("Windows returned malformed reparse data.");
        }

        var substituteNameOffset = BinaryPrimitives.ReadUInt16LittleEndian(
            buffer.AsSpan(8, 2));
        var substituteNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            buffer.AsSpan(10, 2));
        var substituteStart = checked(mountPointHeaderSize + substituteNameOffset);
        if ((substituteNameLength & 1) != 0
            || substituteStart > bytesReturned
            || substituteNameLength > bytesReturned - substituteStart)
        {
            throw new InvalidDataException("Windows returned malformed reparse data.");
        }

        var substituteName = System.Text.Encoding.Unicode.GetString(
            buffer,
            substituteStart,
            substituteNameLength);
        return substituteName.StartsWith(
            @"\??\Volume{",
            StringComparison.OrdinalIgnoreCase)
            ? TargetKind.MountPoint
            : TargetKind.Junction;
    }

    private static bool IsSupportedReparseTag(uint reparseTag) =>
        reparseTag is WindowsNativeMethods.IoReparseTagSymbolicLink
            or WindowsNativeMethods.IoReparseTagMountPoint
        || IsCloudReparseTag(reparseTag);

    private static bool IsCloudReparseTag(uint reparseTag) =>
        (reparseTag & ~0x0000F000U) == 0x9000001AU;

    private static uint GetPlaceholderState(uint fileAttributes, uint reparseTag)
    {
        try
        {
            return WindowsNativeMethods.CfGetPlaceholderStateFromAttributeTag(
                fileAttributes,
                reparseTag);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            return WindowsNativeMethods.CfPlaceholderStateInvalid;
        }
    }

    private static bool HasPlaceholderState(uint state, uint flags) =>
        state != WindowsNativeMethods.CfPlaceholderStateInvalid
        && (state & flags) != 0;

    private static bool IsConfirmedLocalPlaceholder(
        FileAttributes attributes,
        uint placeholderState) =>
        HasPlaceholderState(
            placeholderState,
            WindowsNativeMethods.CfPlaceholderStatePlaceholder)
        && HasPlaceholderState(
            placeholderState,
            WindowsNativeMethods.CfPlaceholderStateInSync)
        && !HasPlaceholderState(
            placeholderState,
            WindowsNativeMethods.CfPlaceholderStatePartial
                | WindowsNativeMethods.CfPlaceholderStatePartiallyOnDisk)
        && HasAnyAttribute(attributes, FileAttributePinned)
        && !HasUnavailableCloudAttributes(attributes);

    private static bool HasUnavailableCloudAttributes(FileAttributes attributes) =>
        HasAnyAttribute(
            attributes,
            FileAttributes.Offline
                | FileAttributeRecallOnOpen
                | FileAttributeRecallOnDataAccess
                | FileAttributeUnpinned);

    private static bool IsOneDrivePath(string path)
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            var root = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrWhiteSpace(root))
            {
                continue;
            }

            try
            {
                var relative = Path.GetRelativePath(Path.GetFullPath(root), path);
                if (relative == "."
                    || (!Path.IsPathRooted(relative)
                        && !string.Equals(relative, "..", StringComparison.OrdinalIgnoreCase)
                        && !relative.StartsWith(
                            $"..{Path.DirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)
                        && !relative.StartsWith(
                            $"..{Path.AltDirectorySeparatorChar}",
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // An invalid environment variable is not evidence that a target is local cloud data.
            }
        }

        return false;
    }

    private static bool HasAnyAttribute(FileAttributes attributes, FileAttributes flags) =>
        (attributes & flags) != 0;

    private static List<DataStreamSnapshot> ReadStreams(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        var bufferSize = InitialStreamBufferSize;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = new byte[bufferSize];
            if (WindowsNativeMethods.GetFileInformationByHandleEx(
                    handle,
                    WindowsNativeMethods.FileStreamInfo,
                    buffer,
                    (uint)buffer.Length))
            {
                return ParseStreams(buffer, cancellationToken);
            }

            var error = Marshal.GetLastWin32Error();
            if (error == ErrorHandleEof)
            {
                return [];
            }

            if (error is not (ErrorMoreData or ErrorInsufficientBuffer))
            {
                throw new Win32Exception(error);
            }

            bufferSize = checked(bufferSize * 2);
        }
    }

    private static List<string> ReadDirectoryNames(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle,
        CancellationToken cancellationToken)
    {
        const int directoryEntryHeaderSize = 104;
        const int fileNameLengthOffset = 60;
        const int fileNameOffset = 104;
        var buffer = new byte[InitialStreamBufferSize];
        var names = new List<string>();
        var informationClass = WindowsNativeMethods.FileIdBothDirectoryRestartInfo;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!WindowsNativeMethods.GetFileInformationByHandleEx(
                    handle,
                    informationClass,
                    buffer,
                    (uint)buffer.Length))
            {
                var error = Marshal.GetLastWin32Error();
                if (error is ErrorNoMoreFiles or ErrorHandleEof)
                {
                    return names;
                }

                throw new Win32Exception(error);
            }

            informationClass = WindowsNativeMethods.FileIdBothDirectoryInfo;
            var offset = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (buffer.Length - offset < directoryEntryHeaderSize)
                {
                    throw new InvalidDataException("Windows returned malformed directory information.");
                }

                var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    buffer.AsSpan(offset, 4));
                var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(
                    buffer.AsSpan(offset + fileNameLengthOffset, 4));
                if ((nameLength & 1) != 0
                    || nameLength > int.MaxValue
                    || buffer.Length - offset - fileNameOffset < nameLength)
                {
                    throw new InvalidDataException("Windows returned malformed directory information.");
                }

                var name = System.Text.Encoding.Unicode.GetString(
                    buffer,
                    offset + fileNameOffset,
                    (int)nameLength);
                if (name is not ("." or ".."))
                {
                    names.Add(name);
                }

                if (nextOffset == 0)
                {
                    break;
                }

                if (nextOffset > int.MaxValue || nextOffset < directoryEntryHeaderSize)
                {
                    throw new InvalidDataException("Windows returned malformed directory information.");
                }

                offset = checked(offset + (int)nextOffset);
            }
        }
    }

    private static List<DataStreamSnapshot> ParseStreams(
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        const int headerSize = 24;
        var streams = new List<DataStreamSnapshot>();
        var offset = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (buffer.Length - offset < headerSize)
            {
                throw new InvalidDataException("Windows returned malformed stream information.");
            }

            var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset, 4));
            var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
            var length = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(offset + 8, 8));
            if ((nameLength & 1) != 0
                || nameLength > int.MaxValue
                || buffer.Length - offset - headerSize < nameLength)
            {
                throw new InvalidDataException("Windows returned malformed stream information.");
            }

            var name = System.Text.Encoding.Unicode.GetString(
                buffer,
                offset + headerSize,
                (int)nameLength);
            streams.Add(new DataStreamSnapshot(name, length));

            if (nextOffset == 0)
            {
                return streams
                    .Where(static stream => stream.Name.EndsWith(
                        ":$DATA",
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(static stream => stream.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static stream => stream.Name, StringComparer.Ordinal)
                    .ToList();
            }

            if (nextOffset > int.MaxValue || nextOffset < headerSize)
            {
                throw new InvalidDataException("Windows returned malformed stream information.");
            }

            offset = checked(offset + (int)nextOffset);
        }
    }

    private static bool TryNormalizeLocalPath(
        string sourcePath,
        out string path,
        out string error)
    {
        path = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            error = "Empty paths are not supported.";
            return false;
        }

        if (sourcePath.StartsWith("\\\\", StringComparison.Ordinal)
            || sourcePath.StartsWith("//", StringComparison.Ordinal))
        {
            error = "UNC, device, and network paths are not supported.";
            return false;
        }

        if (!Path.IsPathFullyQualified(sourcePath))
        {
            error = "Only fully-qualified local drive paths are supported.";
            return false;
        }

        try
        {
            path = Path.GetFullPath(sourcePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = "The path is invalid.";
            return false;
        }

        var root = Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root)
            || root.Length < 3
            || root[1] != ':'
            || root[2] != Path.DirectorySeparatorChar)
        {
            error = "Only fully-qualified local drive paths are supported.";
            return false;
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(path),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            path = Path.TrimEndingDirectorySeparator(path);
        }

        if (path.IndexOf(':', 2) >= 0)
        {
            error = "Alternate data streams cannot be selected as standalone targets.";
            return false;
        }

        return true;
    }

    private static bool TryGetVolume(
        string path,
        Dictionary<string, VolumeContext> volumes,
        out VolumeContext volume,
        out PreflightIssue? error)
    {
        volume = default;
        error = null;
        var volumePathBuffer = new char[32_768];
        if (!WindowsNativeMethods.GetVolumePathName(
                path,
                volumePathBuffer,
                (uint)volumePathBuffer.Length))
        {
            error = CreateLastErrorIssue(path, "The target volume could not be identified");
            return false;
        }

        var volumePath = NullTerminatedString(volumePathBuffer);
        if (volumes.TryGetValue(volumePath, out volume))
        {
            return true;
        }

        var driveType = WindowsNativeMethods.GetDriveType(volumePath);
        if (driveType is not (
            WindowsNativeMethods.DriveFixed
            or WindowsNativeMethods.DriveRemovable))
        {
            error = new PreflightIssue(
                PreflightIssueCode.UnsupportedFileSystem,
                PreflightSeverity.Blocking,
                $"'{path}' is not on a local fixed or removable drive.");
            return false;
        }

        var fileSystemBuffer = new char[32];
        if (!WindowsNativeMethods.GetVolumeInformation(
                volumePath,
                null,
                0,
                out var volumeSerialNumber,
                out _,
                out _,
                fileSystemBuffer,
                (uint)fileSystemBuffer.Length))
        {
            error = CreateLastErrorIssue(path, "The target file system could not be identified");
            return false;
        }

        var fileSystemName = NullTerminatedString(fileSystemBuffer);
        var fileSystem = fileSystemName.ToUpperInvariant() switch
        {
            "NTFS" => FileSystemKind.Ntfs,
            "EXFAT" => FileSystemKind.ExFat,
            "FAT32" => FileSystemKind.Fat32,
            "REFS" => FileSystemKind.ReFs,
            _ => FileSystemKind.Unknown,
        };
        if (fileSystem is not (
            FileSystemKind.Ntfs
            or FileSystemKind.ExFat
            or FileSystemKind.Fat32))
        {
            error = new PreflightIssue(
                PreflightIssueCode.UnsupportedFileSystem,
                PreflightSeverity.Blocking,
                $"The {fileSystemName} file system for '{path}' is not supported.");
            return false;
        }

        var volumeNameBuffer = new char[51];
        if (!WindowsNativeMethods.GetVolumeNameForVolumeMountPoint(
                volumePath,
                volumeNameBuffer,
                (uint)volumeNameBuffer.Length))
        {
            error = CreateLastErrorIssue(path, "The stable volume identity could not be read");
            return false;
        }

        var volumeId = NullTerminatedString(volumeNameBuffer);
        volume = new VolumeContext(
            fileSystem,
            DetectMedium(volumePath, driveType),
            volumeId,
            volumeSerialNumber);
        volumes.Add(volumePath, volume);
        return true;
    }

    private static StorageMedium DetectMedium(string volumePath, uint driveType)
    {
        if (driveType == WindowsNativeMethods.DriveRemovable)
        {
            return StorageMedium.Flash;
        }

        var devicePath = ToVolumeDevicePath(volumePath);
        if (devicePath is null)
        {
            return StorageMedium.Unknown;
        }

        using var volumeHandle = WindowsNativeMethods.CreateFile(
            devicePath,
            0,
            WindowsNativeMethods.FileShareRead | WindowsNativeMethods.FileShareWrite,
            0,
            WindowsNativeMethods.OpenExisting,
            0,
            0);
        if (volumeHandle.IsInvalid)
        {
            return StorageMedium.Unknown;
        }

        if (TryQueryStorageBoolean(
                volumeHandle,
                WindowsNativeMethods.StorageDeviceSeekPenaltyProperty,
                out var incursSeekPenalty))
        {
            return incursSeekPenalty ? StorageMedium.Hdd : StorageMedium.Ssd;
        }

        return TryQueryStorageBoolean(
            volumeHandle,
            WindowsNativeMethods.StorageDeviceTrimProperty,
            out var trimEnabled)
            ? trimEnabled
                ? StorageMedium.Ssd
                : StorageMedium.Hdd
            : StorageMedium.Unknown;
    }

    private static bool TryQueryStorageBoolean(
        Microsoft.Win32.SafeHandles.SafeFileHandle volumeHandle,
        int propertyId,
        out bool value)
    {
        var query = new byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(query.AsSpan(0, 4), propertyId);
        var descriptor = new byte[12];
        if (!WindowsNativeMethods.QueryStorageProperty(
                volumeHandle,
                WindowsNativeMethods.IoctlStorageQueryProperty,
                query,
                (uint)query.Length,
                descriptor,
                (uint)descriptor.Length,
                out var bytesReturned,
                0)
            || bytesReturned < 9)
        {
            value = false;
            return false;
        }

        value = descriptor[8] != 0;
        return true;
    }

    private static string? ToVolumeDevicePath(string volumePath)
    {
        if (volumePath.Length >= 3
            && volumePath[1] == ':'
            && volumePath[2] == Path.DirectorySeparatorChar)
        {
            return $@"\\.\{volumePath[..2]}";
        }

        if (volumePath.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase))
        {
            return Path.TrimEndingDirectorySeparator(volumePath);
        }

        return null;
    }

    private static PreflightIssue CreateLastErrorIssue(string path, string action)
    {
        var error = Marshal.GetLastWin32Error();
        return new PreflightIssue(
            PreflightIssueCode.UnsupportedLayout,
            PreflightSeverity.Blocking,
            $"{action}: '{path}' (Win32 {error}).");
    }

    private static PreflightIssue CreateBlockingIssue(string path, string message)
    {
        return new PreflightIssue(
            PreflightIssueCode.UnsupportedLayout,
            PreflightSeverity.Blocking,
            $"{message} Path: '{path}'.");
    }

    private static string NullTerminatedString(char[] buffer)
    {
        var length = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static string ToExtendedPath(string path) => $"\\\\?\\{path}";

    private static long CombineUnsigned(uint high, uint low) =>
        unchecked((long)(((ulong)high << 32) | low));

    private static DateTime FromFileTime(WindowsNativeMethods.NativeFileTime value)
    {
        var fileTime = CombineUnsigned(value.HighDateTime, value.LowDateTime);
        return DateTime.FromFileTimeUtc(fileTime);
    }

    private readonly record struct VolumeContext(
        FileSystemKind FileSystem,
        StorageMedium Medium,
        string VolumeId,
        uint SerialNumber);

    private readonly record struct FileIdentity(
        string VolumeId,
        uint FileIndexHigh,
        uint FileIndexLow);
}
