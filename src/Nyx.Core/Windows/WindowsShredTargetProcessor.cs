using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Nyx.Core.Execution;
using Nyx.Core.Preflight;

namespace Nyx.Core.Windows;

internal sealed class WindowsShredTargetProcessor : IShredTargetProcessor
{
    private readonly ShredNativeFaultPlan? _faultPlan;

    private const int BufferSize = 1024 * 1024;
    private const int ErrorInvalidData = 13;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorWriteFault = 29;
    private const int ErrorHandleEof = 38;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorDeletePending = 303;
    private const int ErrorDirectoryNotEmpty = 145;
    private const int ErrorMoreData = 234;
    private const int ErrorInsufficientBuffer = 122;
    private const int InitialStreamBufferSize = 64 * 1024;
    private const int MaximumStreamBufferSize = 64 * 1024 * 1024;
    private const int RenameAttemptLimit = 64;
    private const string RandomNameAlphabet =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    internal WindowsShredTargetProcessor()
    {
    }

    internal WindowsShredTargetProcessor(ShredNativeFaultPlan faultPlan)
    {
        _faultPlan = faultPlan ?? throw new ArgumentNullException(nameof(faultPlan));
    }

    internal Task<ShredItemResult> ProcessAsync(
        PreflightTarget target,
        IProgress<ShredProgress>? progress)
    {
        ArgumentNullException.ThrowIfNull(target);
        return Task.Run(() =>
        {
            try
            {
                return Process(target, progress);
            }
            catch (AttributeRestoreFailureException exception)
            {
                return exception.Result;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
            {
                var error = GetNativeError(exception);
                return Failure(
                    target.Snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    0,
                    error,
                    IsVolumeFatalError(error),
                    target.Path,
                    ShredFailureStage.Unexpected);
            }
        });
    }

    Task<ShredItemResult> IShredTargetProcessor.ProcessAsync(
        PreflightTarget target,
        IProgress<ShredProgress>? progress) =>
        ProcessAsync(target, progress);

    private ShredItemResult Process(
        PreflightTarget target,
        IProgress<ShredProgress>? progress)
    {
        var snapshot = target.Snapshot;
        if (IsLink(snapshot.Kind))
        {
            return ProcessLink(snapshot);
        }

        if (snapshot.Kind is not (TargetKind.File or TargetKind.Directory))
        {
            return Failure(
                snapshot,
                ShredItemStatus.FailedBeforeMutation,
                0,
                ErrorInvalidData,
                isVolumeFatal: false,
                snapshot.Path,
                ShredFailureStage.FinalValidation);
        }

        SafeFileHandle? guardHandle = null;
        SafeFileHandle? operationHandle = null;
        var readOnlyCleared = false;
        var deleteRequested = false;
        var bytesAccepted = 0L;
        var remainingPath = snapshot.Path;
        var mutationPhase = MutationPhase.None;
        ShredItemResult? primaryFailure = null;
        try
        {
            if (!TryOpenVerifiedObject(
                    snapshot,
                    ShredNativeMethods.FileReadAttributes
                        | ShredNativeMethods.FileWriteAttributes
                        | ShredNativeMethods.Synchronize
                        | (snapshot.Kind == TargetKind.Directory
                            ? ShredNativeMethods.FileListDirectory
                            : 0),
                    verifyMetadata: true,
                    expectReadOnlyCleared: false,
                    out guardHandle,
                    out var openError,
                    out var targetChanged))
            {
                return Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    bytesAccepted,
                    openError,
                    targetChanged || IsVolumeFatalError(openError),
                    snapshot.Path,
                    targetChanged
                        ? ShredFailureStage.IdentityVerification
                        : ShredFailureStage.Open);
            }

            if (snapshot.Kind == TargetKind.Directory)
            {
                if (!TryIsDirectoryEmpty(guardHandle, out var isEmpty, out var directoryError))
                {
                    return Failure(
                        snapshot,
                        ShredItemStatus.FolderRetained,
                        bytesAccepted,
                        directoryError,
                        isVolumeFatal: true,
                        snapshot.Path,
                        ShredFailureStage.DirectoryValidation);
                }

                if (!isEmpty)
                {
                    return Failure(
                        snapshot,
                        ShredItemStatus.FolderRetained,
                        bytesAccepted,
                        ErrorDirectoryNotEmpty,
                        isVolumeFatal: true,
                        snapshot.Path,
                        ShredFailureStage.DirectoryValidation);
                }
            }

            if ((snapshot.Attributes & FileAttributes.ReadOnly) != 0)
            {
                if (!TrySetReadOnly(guardHandle, isReadOnly: false, out var attributeError))
                {
                    return Failure(
                        snapshot,
                        ShredItemStatus.FailedBeforeMutation,
                        bytesAccepted,
                        attributeError,
                        IsVolumeFatalError(attributeError),
                        snapshot.Path,
                        ShredFailureStage.FinalValidation);
                }

                readOnlyCleared = true;
            }

            if (!TryOpenVerifiedObject(
                    snapshot,
                    ShredNativeMethods.Delete
                        | ShredNativeMethods.FileReadAttributes
                        | ShredNativeMethods.FileWriteAttributes
                        | ShredNativeMethods.Synchronize
                        | (snapshot.Kind == TargetKind.Directory
                            ? ShredNativeMethods.FileListDirectory
                            : 0),
                    verifyMetadata: true,
                    expectReadOnlyCleared: readOnlyCleared,
                    out operationHandle,
                    out openError,
                    out targetChanged))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    bytesAccepted,
                    openError,
                    targetChanged || IsVolumeFatalError(openError),
                    snapshot.Path,
                    targetChanged
                        ? ShredFailureStage.FinalValidation
                        : ShredFailureStage.Open);
            }

            guardHandle.Dispose();
            guardHandle = null;

            var streams = GetRequiredStreams(snapshot);
            if (!TryValidateStreams(
                    operationHandle,
                    snapshot,
                    streams,
                    out var validationError,
                    out targetChanged))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    bytesAccepted,
                    validationError,
                    targetChanged || IsVolumeFatalError(validationError),
                    snapshot.Path,
                    targetChanged
                        ? ShredFailureStage.FinalValidation
                        : ShredFailureStage.StreamEnumeration);
            }

            var totalBytes = streams.Sum(static stream => stream.Length);
            var buffer = new byte[BufferSize];
            foreach (var stream in streams)
            {
                mutationPhase = MutationPhase.ContentOverwriteStarted;
                var overwrite = OverwriteStream(
                    snapshot,
                    stream,
                    buffer,
                    ref bytesAccepted,
                    totalBytes,
                    progress);
                bytesAccepted = overwrite.BytesAccepted;
                if (!overwrite.Succeeded)
                {
                    return primaryFailure = Failure(
                        snapshot,
                        overwrite.FlushFailed
                            ? ShredItemStatus.OverwriteFlushFailed
                            : bytesAccepted > 0
                                ? ShredItemStatus.PartiallyOverwritten
                                : ShredItemStatus.FailedBeforeMutation,
                        bytesAccepted,
                        overwrite.ErrorCode,
                        overwrite.TargetChanged || IsVolumeFatalError(overwrite.ErrorCode),
                        snapshot.Path,
                        overwrite.FlushFailed
                            ? ShredFailureStage.Flush
                            : ShredFailureStage.Overwrite);
                }
            }

            mutationPhase = MutationPhase.ContentOverwritten;

            if (!TryMatchCurrentStreams(
                    operationHandle,
                    snapshot,
                    streams,
                    expectTruncated: false,
                    out validationError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.OverwrittenNotDeleted,
                    bytesAccepted,
                    validationError,
                    isVolumeFatal: true,
                    snapshot.Path,
                    ShredFailureStage.FinalValidation);
            }

            foreach (var stream in streams)
            {
                if (!TryTruncateStream(snapshot, stream, out var truncateError, out targetChanged))
                {
                    return primaryFailure = Failure(
                        snapshot,
                        ShredItemStatus.OverwrittenNotDeleted,
                        bytesAccepted,
                        truncateError,
                        targetChanged || IsVolumeFatalError(truncateError),
                        snapshot.Path,
                        ShredFailureStage.Truncate);
                }
            }

            if (!TryMatchCurrentStreams(
                    operationHandle,
                    snapshot,
                    streams,
                    expectTruncated: true,
                    out validationError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.OverwrittenNotDeleted,
                    bytesAccepted,
                    validationError,
                    isVolumeFatal: true,
                    snapshot.Path,
                    ShredFailureStage.FinalValidation);
            }

            mutationPhase = MutationPhase.ContentTruncated;

            if (snapshot.Kind == TargetKind.Directory)
            {
                if (!TryIsDirectoryEmpty(operationHandle, out var isEmpty, out var directoryError))
                {
                    return primaryFailure = Failure(
                        snapshot,
                        ShredItemStatus.FolderRetained,
                        bytesAccepted,
                        directoryError,
                        isVolumeFatal: true,
                        snapshot.Path,
                        ShredFailureStage.DirectoryValidation);
                }

                if (!isEmpty)
                {
                    return primaryFailure = Failure(
                        snapshot,
                        ShredItemStatus.FolderRetained,
                        bytesAccepted,
                        ErrorDirectoryNotEmpty,
                        isVolumeFatal: true,
                        snapshot.Path,
                        ShredFailureStage.DirectoryValidation);
                }
            }

            var renamed = TryRandomizeName(
                operationHandle,
                snapshot.Path,
                out var currentPath,
                out var renameError);
            remainingPath = currentPath;
            if (renamed)
            {
                mutationPhase = MutationPhase.NameRandomized;
            }
            if (!TryRequestNormalDeletion(operationHandle, out var deleteError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    snapshot.Kind == TargetKind.Directory
                        ? ShredItemStatus.FolderRetained
                        : ShredItemStatus.OverwrittenNotDeleted,
                    bytesAccepted,
                    deleteError,
                    IsVolumeFatalError(deleteError),
                    currentPath,
                    ShredFailureStage.Delete);
            }

            deleteRequested = true;
            operationHandle.Dispose();
            operationHandle = null;
            if (!TryConfirmDeletion(currentPath, out deleteError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    snapshot.Kind == TargetKind.Directory
                        ? ShredItemStatus.FolderRetained
                        : ShredItemStatus.OverwrittenNotDeleted,
                    bytesAccepted,
                    deleteError,
                    IsVolumeFatalError(deleteError),
                    currentPath,
                    ShredFailureStage.Delete);
            }

            return new ShredItemResult(
                snapshot,
                renamed
                    ? ShredItemStatus.Shredded
                    : ShredItemStatus.DeletedWithoutNameRandomization,
                bytesAccepted,
                renamed ? null : renameError,
                false,
                CurrentPath: null,
                FailureStage: renamed
                    ? ShredFailureStage.None
                    : ShredFailureStage.NameRandomization);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            var error = GetNativeError(exception);
            return primaryFailure = CreateUnexpectedFailure(
                snapshot,
                mutationPhase,
                bytesAccepted,
                error,
                remainingPath);
        }
        finally
        {
            int? restoreError = null;
            if (readOnlyCleared && !deleteRequested)
            {
                var restoreHandle = operationHandle is { IsInvalid: false, IsClosed: false }
                    ? operationHandle
                    : guardHandle;
                if (restoreHandle is { IsInvalid: false, IsClosed: false })
                {
                    if (!TrySetReadOnly(restoreHandle, isReadOnly: true, out var attributeRestoreError))
                    {
                        restoreError = attributeRestoreError;
                    }
                }
            }

            operationHandle?.Dispose();
            guardHandle?.Dispose();
            if (restoreError is int finalRestoreError)
            {
                var result = primaryFailure ?? new ShredItemResult(
                    snapshot,
                    bytesAccepted > 0
                        ? ShredItemStatus.PartiallyOverwritten
                        : ShredItemStatus.FailedBeforeMutation,
                    bytesAccepted,
                    finalRestoreError,
                    IsVolumeFatalError(finalRestoreError),
                    remainingPath);
                ThrowAttributeRestoreFailure(result with
                {
                    NativeErrorCode = finalRestoreError,
                    IsVolumeFatal = result.IsVolumeFatal || IsVolumeFatalError(finalRestoreError),
                    CurrentPath = remainingPath,
                    FailureStage = ShredFailureStage.AttributeRestore,
                });
            }
        }
    }

    private ShredItemResult ProcessLink(TargetSnapshot snapshot)
    {
        SafeFileHandle? guardHandle = null;
        SafeFileHandle? operationHandle = null;
        var readOnlyCleared = false;
        var deleteRequested = false;
        var remainingPath = snapshot.Path;
        var mutationPhase = MutationPhase.None;
        ShredItemResult? primaryFailure = null;
        try
        {
            if (!TryOpenVerifiedObject(
                    snapshot,
                    ShredNativeMethods.FileReadAttributes
                        | ShredNativeMethods.FileWriteAttributes
                        | ShredNativeMethods.Synchronize,
                    verifyMetadata: true,
                    expectReadOnlyCleared: false,
                    out guardHandle,
                    out var openError,
                    out var targetChanged))
            {
                return Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    0,
                    openError,
                    targetChanged || IsVolumeFatalError(openError),
                    snapshot.Path,
                    targetChanged
                        ? ShredFailureStage.IdentityVerification
                        : ShredFailureStage.Open);
            }

            if ((snapshot.Attributes & FileAttributes.ReadOnly) != 0)
            {
                if (!TrySetReadOnly(guardHandle, isReadOnly: false, out var attributeError))
                {
                    return Failure(
                        snapshot,
                        ShredItemStatus.FailedBeforeMutation,
                        0,
                        attributeError,
                        IsVolumeFatalError(attributeError),
                        snapshot.Path,
                        ShredFailureStage.FinalValidation);
                }

                readOnlyCleared = true;
            }

            if (!TryOpenVerifiedObject(
                    snapshot,
                    ShredNativeMethods.Delete
                        | ShredNativeMethods.FileReadAttributes
                        | ShredNativeMethods.FileWriteAttributes
                        | ShredNativeMethods.Synchronize,
                    verifyMetadata: true,
                    expectReadOnlyCleared: readOnlyCleared,
                    out operationHandle,
                    out openError,
                    out targetChanged))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    0,
                    openError,
                    targetChanged || IsVolumeFatalError(openError),
                    snapshot.Path,
                    targetChanged
                        ? ShredFailureStage.FinalValidation
                        : ShredFailureStage.Open);
            }

            guardHandle.Dispose();
            guardHandle = null;

            if (snapshot.Kind == TargetKind.MountPoint
                && !TryDeleteMountPoint(snapshot.Path, out var unmountError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    0,
                    unmountError,
                    IsVolumeFatalError(unmountError),
                    snapshot.Path,
                    ShredFailureStage.Delete);
            }

            if (snapshot.Kind == TargetKind.MountPoint)
            {
                mutationPhase = MutationPhase.LinkMetadataMutated;
            }

            var renamed = TryRandomizeName(
                operationHandle,
                snapshot.Path,
                out var currentPath,
                out var renameError);
            remainingPath = currentPath;
            if (renamed)
            {
                mutationPhase = MutationPhase.NameRandomized;
            }
            if (!TryRequestNormalDeletion(operationHandle, out var deleteError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    (snapshot.Attributes & FileAttributes.Directory) != 0
                        ? ShredItemStatus.FolderRetained
                        : ShredItemStatus.OverwrittenNotDeleted,
                    0,
                    deleteError,
                    IsVolumeFatalError(deleteError),
                    currentPath,
                    ShredFailureStage.Delete);
            }

            deleteRequested = true;
            operationHandle.Dispose();
            operationHandle = null;
            if (!TryConfirmDeletion(currentPath, out deleteError))
            {
                return primaryFailure = Failure(
                    snapshot,
                    (snapshot.Attributes & FileAttributes.Directory) != 0
                        ? ShredItemStatus.FolderRetained
                        : ShredItemStatus.OverwrittenNotDeleted,
                    0,
                    deleteError,
                    IsVolumeFatalError(deleteError),
                    currentPath,
                    ShredFailureStage.Delete);
            }

            return new ShredItemResult(
                snapshot,
                renamed
                    ? ShredItemStatus.LinkRemoved
                    : ShredItemStatus.DeletedWithoutNameRandomization,
                0,
                renamed ? null : renameError,
                false,
                CurrentPath: null,
                FailureStage: renamed
                    ? ShredFailureStage.None
                    : ShredFailureStage.NameRandomization);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException)
        {
            var error = GetNativeError(exception);
            return primaryFailure = CreateUnexpectedFailure(
                snapshot,
                mutationPhase,
                bytesAccepted: 0,
                error,
                remainingPath);
        }
        finally
        {
            int? restoreError = null;
            if (readOnlyCleared && !deleteRequested)
            {
                var restoreHandle = operationHandle is { IsInvalid: false, IsClosed: false }
                    ? operationHandle
                    : guardHandle;
                if (restoreHandle is { IsInvalid: false, IsClosed: false })
                {
                    if (!TrySetReadOnly(restoreHandle, isReadOnly: true, out var attributeRestoreError))
                    {
                        restoreError = attributeRestoreError;
                    }
                }
            }

            operationHandle?.Dispose();
            guardHandle?.Dispose();
            if (restoreError is int finalRestoreError)
            {
                var result = primaryFailure ?? new ShredItemResult(
                    snapshot,
                    ShredItemStatus.FailedBeforeMutation,
                    0,
                    finalRestoreError,
                    IsVolumeFatalError(finalRestoreError),
                    remainingPath);
                ThrowAttributeRestoreFailure(result with
                {
                    NativeErrorCode = finalRestoreError,
                    IsVolumeFatal = result.IsVolumeFatal || IsVolumeFatalError(finalRestoreError),
                    CurrentPath = remainingPath,
                    FailureStage = ShredFailureStage.AttributeRestore,
                });
            }
        }
    }

    private static DataStreamSnapshot[] GetRequiredStreams(TargetSnapshot snapshot)
    {
        var streams = snapshot.Streams
            .Where(static stream => IsValidDataStreamName(stream.Name))
            .OrderBy(static stream => stream.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static stream => stream.Name, StringComparer.Ordinal)
            .ToArray();
        if (streams.Length > 0 || snapshot.Kind == TargetKind.Directory)
        {
            return streams;
        }

        return [new DataStreamSnapshot("::$DATA", snapshot.Length)];
    }

    private static bool TryIsDirectoryEmpty(
        SafeFileHandle directoryHandle,
        out bool isEmpty,
        out int error)
    {
        const int directoryEntryHeaderSize = 104;
        const int fileNameLengthOffset = 60;
        const int fileNameOffset = 104;
        var buffer = new byte[InitialStreamBufferSize];
        var informationClass = ShredNativeMethods.FileIdBothDirectoryRestartInfo;
        while (true)
        {
            if (!ShredNativeMethods.GetFileInformationByHandleEx(
                    directoryHandle,
                    informationClass,
                    buffer,
                    checked((uint)buffer.Length)))
            {
                error = Marshal.GetLastWin32Error();
                if (error is ErrorNoMoreFiles or ErrorHandleEof)
                {
                    isEmpty = true;
                    error = 0;
                    return true;
                }

                isEmpty = false;
                return false;
            }

            informationClass = ShredNativeMethods.FileIdBothDirectoryInfo;
            var offset = 0;
            while (true)
            {
                if (buffer.Length - offset < directoryEntryHeaderSize)
                {
                    isEmpty = false;
                    error = ErrorInvalidData;
                    return false;
                }

                var nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(
                    buffer.AsSpan(offset, sizeof(uint)));
                var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(
                    buffer.AsSpan(offset + fileNameLengthOffset, sizeof(uint)));
                if ((nameLength & 1) != 0
                    || nameLength > int.MaxValue
                    || buffer.Length - offset - fileNameOffset < nameLength)
                {
                    isEmpty = false;
                    error = ErrorInvalidData;
                    return false;
                }

                var name = Encoding.Unicode.GetString(
                    buffer,
                    offset + fileNameOffset,
                    checked((int)nameLength));
                if (name is not ("." or ".."))
                {
                    isEmpty = false;
                    error = 0;
                    return true;
                }

                if (nextOffset == 0)
                {
                    break;
                }

                if (nextOffset > int.MaxValue || nextOffset < directoryEntryHeaderSize)
                {
                    isEmpty = false;
                    error = ErrorInvalidData;
                    return false;
                }

                offset = checked(offset + (int)nextOffset);
            }
        }
    }

    private bool TryValidateStreams(
        SafeFileHandle objectHandle,
        TargetSnapshot snapshot,
        IReadOnlyList<DataStreamSnapshot> streams,
        out int error,
        out bool targetChanged)
    {
        if (!TryMatchCurrentStreams(
                objectHandle,
                snapshot,
                streams,
                expectTruncated: false,
                out error))
        {
            targetChanged = true;
            return false;
        }

        foreach (var stream in streams)
        {
            if (!IsValidDataStreamName(stream.Name) || stream.Length < 0)
            {
                error = ErrorInvalidData;
                targetChanged = true;
                return false;
            }

            using var handle = OpenStream(snapshot, stream, out var streamOpenError);
            if (handle.IsInvalid)
            {
                error = streamOpenError;
                targetChanged = IsMissingTargetError(error);
                return false;
            }

            if (!TryVerifyIdentity(handle, snapshot, out error)
                || !ShredNativeMethods.GetFileSizeEx(handle, out var length))
            {
                error = error == 0 ? Marshal.GetLastWin32Error() : error;
                targetChanged = error == ErrorInvalidData || IsMissingTargetError(error);
                return false;
            }

            if (length != stream.Length)
            {
                error = ErrorInvalidData;
                targetChanged = true;
                return false;
            }
        }

        error = 0;
        targetChanged = false;
        return true;
    }

    private static bool TryMatchCurrentStreams(
        SafeFileHandle objectHandle,
        TargetSnapshot snapshot,
        IReadOnlyList<DataStreamSnapshot> expectedStreams,
        bool expectTruncated,
        out int error)
    {
        if (snapshot.FileSystem != FileSystemKind.Ntfs)
        {
            error = 0;
            return true;
        }

        if (!TryReadStreams(objectHandle, out var currentStreams, out error))
        {
            return false;
        }

        var expected = expectedStreams
            .Where(static stream => IsValidDataStreamName(stream.Name))
            .Select(stream => expectTruncated
                ? stream with { Length = 0 }
                : stream)
            .OrderBy(static stream => stream.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static stream => stream.Name, StringComparer.Ordinal)
            .ToArray();
        var current = currentStreams
            .Where(static stream => IsValidDataStreamName(stream.Name))
            .OrderBy(static stream => stream.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static stream => stream.Name, StringComparer.Ordinal)
            .ToArray();
        if (!expected.SequenceEqual(current))
        {
            error = ErrorInvalidData;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryReadStreams(
        SafeFileHandle objectHandle,
        out IReadOnlyList<DataStreamSnapshot> streams,
        out int error)
    {
        var bufferSize = InitialStreamBufferSize;
        while (true)
        {
            var buffer = new byte[bufferSize];
            if (ShredNativeMethods.GetFileInformationByHandleEx(
                    objectHandle,
                    ShredNativeMethods.FileStreamInfo,
                    buffer,
                    checked((uint)buffer.Length)))
            {
                try
                {
                    streams = ParseStreams(buffer);
                    error = 0;
                    return true;
                }
                catch (InvalidDataException)
                {
                    streams = [];
                    error = ErrorInvalidData;
                    return false;
                }
            }

            error = Marshal.GetLastWin32Error();
            if (error == ErrorHandleEof)
            {
                streams = [];
                error = 0;
                return true;
            }

            if (error is not (ErrorMoreData or ErrorInsufficientBuffer)
                || bufferSize >= MaximumStreamBufferSize)
            {
                streams = [];
                return false;
            }

            bufferSize = checked(bufferSize * 2);
        }
    }

    private static List<DataStreamSnapshot> ParseStreams(byte[] buffer)
    {
        const int headerSize = 24;
        var streams = new List<DataStreamSnapshot>();
        var offset = 0;
        while (true)
        {
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

            var name = Encoding.Unicode.GetString(
                buffer,
                offset + headerSize,
                checked((int)nameLength));
            streams.Add(new DataStreamSnapshot(name, length));
            if (nextOffset == 0)
            {
                return streams;
            }

            if (nextOffset > int.MaxValue || nextOffset < headerSize)
            {
                throw new InvalidDataException("Windows returned malformed stream information.");
            }

            offset = checked(offset + (int)nextOffset);
        }
    }

    private StreamOperationResult OverwriteStream(
        TargetSnapshot snapshot,
        DataStreamSnapshot stream,
        byte[] buffer,
        ref long bytesAccepted,
        long totalBytes,
        IProgress<ShredProgress>? progress)
    {
        var bytesAlreadyAccepted = bytesAccepted;
        using var handle = OpenStream(snapshot, stream, out var streamOpenError);
        if (handle.IsInvalid)
        {
            return StreamOperationResult.Failure(
                bytesAlreadyAccepted,
                streamOpenError,
                flushFailed: false,
                IsMissingTargetError(streamOpenError));
        }

        if (!TryVerifyIdentity(handle, snapshot, out var identityError))
        {
            return StreamOperationResult.Failure(
                bytesAlreadyAccepted,
                identityError,
                flushFailed: false,
                targetChanged: true);
        }

        if (!ShredNativeMethods.GetFileSizeEx(handle, out var currentLength))
        {
            var error = Marshal.GetLastWin32Error();
            return StreamOperationResult.Failure(
                bytesAlreadyAccepted,
                error,
                flushFailed: false,
                IsMissingTargetError(error));
        }

        if (currentLength != stream.Length)
        {
            return StreamOperationResult.Failure(
                bytesAlreadyAccepted,
                ErrorInvalidData,
                flushFailed: false,
                targetChanged: true);
        }

        var remaining = stream.Length;
        var streamBytesAccepted = 0L;
        while (remaining > 0)
        {
            var chunkLength = (int)Math.Min(buffer.Length, remaining);
            RandomNumberGenerator.Fill(buffer.AsSpan(0, chunkLength));
            var chunkOffset = 0;
            while (chunkOffset < chunkLength)
            {
                var requested = checked((uint)(chunkLength - chunkOffset));
                byte[] writeBuffer;
                if (chunkOffset == 0)
                {
                    writeBuffer = buffer;
                }
                else
                {
                    writeBuffer = buffer.AsSpan(chunkOffset, chunkLength - chunkOffset).ToArray();
                }

                uint written;
                if (TryGetFault(ShredNativeOperation.Write, out var writeFault)
                    && !writeFault.ContinueNative)
                {
                    if (writeFault.SimulatedBytesWritten is not uint simulatedBytesWritten)
                    {
                        return StreamOperationResult.Failure(
                            checked(bytesAlreadyAccepted + streamBytesAccepted),
                            writeFault.ErrorCode,
                            flushFailed: false,
                            targetChanged: false);
                    }

                    written = simulatedBytesWritten;
                }
                else if (!ShredNativeMethods.WriteFile(
                             handle,
                             writeBuffer,
                             requested,
                             out written,
                             0))
                {
                    return StreamOperationResult.Failure(
                        checked(bytesAlreadyAccepted + streamBytesAccepted),
                        Marshal.GetLastWin32Error(),
                        flushFailed: false,
                        targetChanged: false);
                }

                if (written == 0 || written > requested)
                {
                    return StreamOperationResult.Failure(
                        checked(bytesAlreadyAccepted + streamBytesAccepted),
                        ErrorWriteFault,
                        flushFailed: false,
                        targetChanged: false);
                }

                streamBytesAccepted = checked(streamBytesAccepted + written);
                // Preserve confirmed writes even if a later managed operation throws.
                bytesAccepted = checked(bytesAlreadyAccepted + streamBytesAccepted);
                chunkOffset = checked(chunkOffset + (int)written);
                ReportProgress(
                    progress,
                    snapshot.Path,
                    checked(bytesAlreadyAccepted + streamBytesAccepted),
                    totalBytes);
            }

            remaining -= chunkLength;
        }

        if (TryGetFault(ShredNativeOperation.Flush, out var flushFault)
            && !flushFault.ContinueNative)
        {
            return StreamOperationResult.Failure(
                checked(bytesAlreadyAccepted + streamBytesAccepted),
                flushFault.ErrorCode,
                flushFailed: true,
                targetChanged: false);
        }

        if (!ShredNativeMethods.FlushFileBuffers(handle))
        {
            return StreamOperationResult.Failure(
                checked(bytesAlreadyAccepted + streamBytesAccepted),
                Marshal.GetLastWin32Error(),
                flushFailed: true,
                targetChanged: false);
        }

        return StreamOperationResult.Success(
            checked(bytesAlreadyAccepted + streamBytesAccepted));
    }

    private bool TryTruncateStream(
        TargetSnapshot snapshot,
        DataStreamSnapshot stream,
        out int error,
        out bool targetChanged)
    {
        using var handle = OpenStream(snapshot, stream, out var streamOpenError);
        if (handle.IsInvalid)
        {
            error = streamOpenError;
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        if (!TryVerifyIdentity(handle, snapshot, out error))
        {
            targetChanged = true;
            return false;
        }

        if (!ShredNativeMethods.GetFileSizeEx(handle, out var currentLength))
        {
            error = Marshal.GetLastWin32Error();
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        if (currentLength != stream.Length)
        {
            error = ErrorInvalidData;
            targetChanged = true;
            return false;
        }

        if (TryGetFault(ShredNativeOperation.Truncate, out var truncateFault)
            && !truncateFault.ContinueNative)
        {
            error = truncateFault.ErrorCode;
            targetChanged = false;
            return false;
        }

        if (!ShredNativeMethods.SetFilePointerEx(
                handle,
                0,
                out _,
                ShredNativeMethods.FileBegin)
            || !ShredNativeMethods.SetEndOfFile(handle))
        {
            error = Marshal.GetLastWin32Error();
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        if (!ShredNativeMethods.GetFileSizeEx(handle, out currentLength))
        {
            error = Marshal.GetLastWin32Error();
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        if (currentLength != 0)
        {
            error = ErrorInvalidData;
            targetChanged = true;
            return false;
        }

        error = 0;
        targetChanged = false;
        return true;
    }

    private SafeFileHandle OpenStream(
        TargetSnapshot snapshot,
        DataStreamSnapshot stream,
        out int error)
    {
        if (TryGetFault(ShredNativeOperation.OpenStream, out var fault)
            && !fault.ContinueNative)
        {
            error = fault.ErrorCode;
            return new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
        }

        var streamPath = snapshot.FileSystem == FileSystemKind.Ntfs
            ? snapshot.Path + stream.Name
            : snapshot.Path;
        var handle = ShredNativeMethods.CreateFile(
            ToExtendedPath(streamPath),
            ShredNativeMethods.GenericWrite
                | ShredNativeMethods.FileReadAttributes
                | ShredNativeMethods.Synchronize,
            ShredNativeMethods.FileShareRead
                | ShredNativeMethods.FileShareDelete,
            0,
            ShredNativeMethods.OpenExisting,
            ShredNativeMethods.FileFlagWriteThrough
                | ShredNativeMethods.FileFlagSequentialScan
                | (snapshot.IsFullyHydratedCloudFile
                    ? ShredNativeMethods.FileFlagOpenNoRecall
                    : ShredNativeMethods.FileFlagOpenReparsePoint)
                | (snapshot.Kind == TargetKind.Directory
                    ? ShredNativeMethods.FileFlagBackupSemantics
                    : 0),
            0);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return handle;
    }

    private bool TryOpenVerifiedObject(
        TargetSnapshot snapshot,
        uint access,
        bool verifyMetadata,
        bool expectReadOnlyCleared,
        out SafeFileHandle handle,
        out int error,
        out bool targetChanged)
    {
        if (TryGetFault(ShredNativeOperation.OpenObject, out var fault)
            && !fault.ContinueNative)
        {
            handle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);
            error = fault.ErrorCode;
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        var shareMode = ShredNativeMethods.FileShareRead
            | ShredNativeMethods.FileShareWrite;
        if ((access & ShredNativeMethods.Delete) == 0)
        {
            shareMode |= ShredNativeMethods.FileShareDelete;
        }

        handle = ShredNativeMethods.CreateFile(
            ToExtendedPath(snapshot.Path),
            access,
            shareMode,
            0,
            ShredNativeMethods.OpenExisting,
            ShredNativeMethods.FileFlagBackupSemantics
                | ShredNativeMethods.FileFlagOpenReparsePoint,
            0);
        if (handle.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            targetChanged = IsMissingTargetError(error);
            return false;
        }

        if (!ShredNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            error = Marshal.GetLastWin32Error();
            targetChanged = IsMissingTargetError(error);
            handle.Dispose();
            return false;
        }

        if (!TryHasExpectedCanonicalPath(handle, snapshot.Path, out error)
            || !TryHasExpectedIdentity(information, snapshot, out error)
            || !HasExpectedShape(information, snapshot)
            || !HasExpectedReparseTag(
                handle,
                snapshot,
                expectReadOnlyCleared,
                out error)
            || verifyMetadata && !HasExpectedMetadata(
                information,
                snapshot,
                expectReadOnlyCleared))
        {
            error = error == 0 ? ErrorInvalidData : error;
            targetChanged = true;
            handle.Dispose();
            return false;
        }

        error = 0;
        targetChanged = false;
        return true;
    }

    private static bool TryHasExpectedCanonicalPath(
        SafeFileHandle handle,
        string expectedPath,
        out int error)
    {
        var buffer = new char[32_768];
        var length = ShredNativeMethods.GetFinalPathNameByHandle(
            handle,
            buffer,
            checked((uint)buffer.Length),
            0);
        if (length == 0)
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (length >= buffer.Length)
        {
            error = ErrorInsufficientBuffer;
            return false;
        }

        var canonicalPath = new string(buffer, 0, checked((int)length));
        const string extendedPrefix = @"\\?\";
        if (canonicalPath.StartsWith(extendedPrefix, StringComparison.Ordinal))
        {
            canonicalPath = canonicalPath[extendedPrefix.Length..];
        }

        try
        {
            canonicalPath = Path.GetFullPath(canonicalPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException)
        {
            error = ErrorInvalidData;
            return false;
        }

        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(canonicalPath),
                Path.TrimEndingDirectorySeparator(expectedPath),
                StringComparison.OrdinalIgnoreCase))
        {
            error = ErrorInvalidData;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryVerifyIdentity(
        SafeFileHandle handle,
        TargetSnapshot snapshot,
        out int error)
    {
        if (!ShredNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        if (!TryHasExpectedIdentity(information, snapshot, out error))
        {
            error = error == 0 ? ErrorInvalidData : error;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryHasExpectedIdentity(
        ShredNativeMethods.ByHandleFileInformation information,
        TargetSnapshot snapshot,
        out int error)
    {
        var volumeProbePath = IsLink(snapshot.Kind)
            ? Path.GetDirectoryName(snapshot.Path) ?? snapshot.Path
            : snapshot.Path;
        if (!TryReadPathVolumeIdentity(
                volumeProbePath,
                out var volumeId,
                out var volumeSerialNumber,
                out error))
        {
            return false;
        }

        var fileId = $"{information.FileIndexHigh:X8}{information.FileIndexLow:X8}";
        if (!string.Equals(volumeId, snapshot.VolumeId, StringComparison.OrdinalIgnoreCase)
            || volumeSerialNumber != information.VolumeSerialNumber
            || !string.Equals(fileId, snapshot.FileId, StringComparison.OrdinalIgnoreCase))
        {
            error = ErrorInvalidData;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryReadPathVolumeIdentity(
        string path,
        out string volumeId,
        out uint volumeSerialNumber,
        out int error)
    {
        var volumePathBuffer = new char[261];
        if (!ShredNativeMethods.GetVolumePathName(
                path,
                volumePathBuffer,
                checked((uint)volumePathBuffer.Length)))
        {
            volumeId = string.Empty;
            volumeSerialNumber = 0;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var volumePath = NullTerminatedString(volumePathBuffer);
        var fileSystemBuffer = new char[32];
        if (!ShredNativeMethods.GetVolumeInformation(
                volumePath,
                null,
                0,
                out volumeSerialNumber,
                out _,
                out _,
                fileSystemBuffer,
                checked((uint)fileSystemBuffer.Length)))
        {
            volumeId = string.Empty;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var volumeNameBuffer = new char[51];
        if (!ShredNativeMethods.GetVolumeNameForVolumeMountPoint(
                volumePath,
                volumeNameBuffer,
                checked((uint)volumeNameBuffer.Length)))
        {
            volumeId = string.Empty;
            error = Marshal.GetLastWin32Error();
            return false;
        }

        volumeId = NullTerminatedString(volumeNameBuffer);
        error = 0;
        return true;
    }

    private static bool HasExpectedShape(
        ShredNativeMethods.ByHandleFileInformation information,
        TargetSnapshot snapshot)
    {
        var attributes = (FileAttributes)information.FileAttributes;
        var shapeMask = FileAttributes.Directory | FileAttributes.ReparsePoint;
        if ((attributes & shapeMask) != (snapshot.Attributes & shapeMask))
        {
            return false;
        }

        return snapshot.Kind switch
        {
            TargetKind.File => (attributes & FileAttributes.Directory) == 0,
            TargetKind.Directory => (attributes & FileAttributes.Directory) != 0,
            TargetKind.SymbolicLink or TargetKind.Junction or TargetKind.MountPoint =>
                (attributes & FileAttributes.ReparsePoint) != 0,
            _ => false,
        };
    }

    private static bool HasExpectedReparseTag(
        SafeFileHandle handle,
        TargetSnapshot snapshot,
        bool expectReadOnlyCleared,
        out int error)
    {
        if ((snapshot.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            error = snapshot.ReparseTag == 0 ? 0 : ErrorInvalidData;
            return error == 0;
        }

        var information = new byte[8];
        if (!ShredNativeMethods.GetFileInformationByHandleEx(
                handle,
                ShredNativeMethods.FileAttributeTagInfo,
                information,
                checked((uint)information.Length)))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var attributes = BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(0, 4));
        var reparseTag = BinaryPrimitives.ReadUInt32LittleEndian(information.AsSpan(4, 4));
        if (attributes != (uint)GetExpectedAttributes(snapshot, expectReadOnlyCleared)
            || reparseTag != snapshot.ReparseTag)
        {
            error = ErrorInvalidData;
            return false;
        }

        error = 0;
        return true;
    }

    private static bool HasExpectedMetadata(
        ShredNativeMethods.ByHandleFileInformation information,
        TargetSnapshot snapshot,
        bool expectReadOnlyCleared)
    {
        var length = CombineUnsigned(information.FileSizeHigh, information.FileSizeLow);
        var writeTime = DateTime.FromFileTimeUtc(CombineUnsigned(
            information.LastWriteTime.HighDateTime,
            information.LastWriteTime.LowDateTime));
        return information.FileAttributes == (uint)GetExpectedAttributes(
                snapshot,
                expectReadOnlyCleared)
            && length == snapshot.Length
            && information.NumberOfLinks == (uint)snapshot.LinkCount
            // Removing reviewed children changes their parent directory's timestamp.
            // The complete pre-write rescan still compares it; execution separately
            // requires the directory to be empty before processing its streams.
            && (snapshot.Kind == TargetKind.Directory || writeTime == snapshot.LastWriteTimeUtc);
    }

    private static FileAttributes GetExpectedAttributes(
        TargetSnapshot snapshot,
        bool expectReadOnlyCleared)
    {
        var attributes = expectReadOnlyCleared
            ? snapshot.Attributes & ~FileAttributes.ReadOnly
            : snapshot.Attributes;
        return attributes == 0 ? FileAttributes.Normal : attributes;
    }

    private bool TrySetReadOnly(
        SafeFileHandle handle,
        bool isReadOnly,
        out int error)
    {
        if (TryGetFault(ShredNativeOperation.SetAttributes, out var attributeFault)
            && !attributeFault.ContinueNative)
        {
            error = attributeFault.ErrorCode;
            return false;
        }

        if (!ShredNativeMethods.GetFileInformationByHandle(handle, out var information))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        var attributes = (FileAttributes)information.FileAttributes;
        if (isReadOnly)
        {
            attributes &= ~FileAttributes.Normal;
            attributes |= FileAttributes.ReadOnly;
        }
        else
        {
            attributes &= ~FileAttributes.ReadOnly;
        }
        if (attributes == 0)
        {
            attributes = FileAttributes.Normal;
        }

        var basicInformation = new ShredNativeMethods.FileBasicInformation
        {
            FileAttributes = (uint)attributes,
        };
        if (!ShredNativeMethods.SetFileInformationByHandleBasic(
                handle,
                ShredNativeMethods.FileBasicInfo,
                ref basicInformation,
                checked((uint)Marshal.SizeOf<ShredNativeMethods.FileBasicInformation>())))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    private bool TryRandomizeName(
        SafeFileHandle objectHandle,
        string originalPath,
        out string currentPath,
        out int error)
    {
        currentPath = originalPath;
        if (TryGetFault(ShredNativeOperation.Rename, out var renameFault)
            && !renameFault.ContinueNative)
        {
            error = renameFault.ErrorCode;
            return false;
        }

        var originalName = Path.GetFileName(originalPath);
        var parentPath = Path.GetDirectoryName(originalPath);
        if (string.IsNullOrEmpty(originalName) || string.IsNullOrEmpty(parentPath))
        {
            error = ErrorInvalidData;
            return false;
        }

        error = ErrorAlreadyExists;
        for (var attempt = 0; attempt < RenameAttemptLimit; attempt++)
        {
            var randomName = CreateRandomName(originalName.Length);
            if (string.Equals(randomName, originalName, StringComparison.OrdinalIgnoreCase)
                || !IsLegalRandomFileName(randomName))
            {
                continue;
            }

            var destinationPath = Path.Combine(parentPath, randomName);
            var renameInformation = CreateRenameInformation(destinationPath);
            if (ShredNativeMethods.SetFileInformationByHandleBytes(
                    objectHandle,
                    ShredNativeMethods.FileRenameInfo,
                    renameInformation,
                    checked((uint)renameInformation.Length)))
            {
                currentPath = Path.Combine(parentPath, randomName);
                error = 0;
                return true;
            }

            error = Marshal.GetLastWin32Error();
            if (error is not (ErrorAlreadyExists or ErrorFileExists))
            {
                return false;
            }
        }

        return false;
    }

    private static byte[] CreateRenameInformation(string newPath)
    {
        var nameBytes = Encoding.Unicode.GetBytes(newPath);
        var rootDirectoryOffset = IntPtr.Size == 8 ? 8 : 4;
        var fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        var fileNameOffset = fileNameLengthOffset + sizeof(uint);
        var buffer = new byte[checked(fileNameOffset + nameBytes.Length + sizeof(char))];

        BinaryPrimitives.WriteUInt32LittleEndian(
            buffer.AsSpan(fileNameLengthOffset, sizeof(uint)),
            checked((uint)nameBytes.Length));
        nameBytes.CopyTo(buffer, fileNameOffset);
        return buffer;
    }

    private static string CreateRandomName(int length)
    {
        var name = new char[length];
        for (var index = 0; index < name.Length; index++)
        {
            name[index] = RandomNameAlphabet[RandomNumberGenerator.GetInt32(RandomNameAlphabet.Length)];
        }

        return new string(name);
    }

    internal static bool IsLegalRandomFileName(string name)
    {
        if (string.IsNullOrEmpty(name)
            || name is "." or ".."
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        var deviceBaseName = name.Split('.')[0].TrimEnd(' ');
        if (deviceBaseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || deviceBaseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || deviceBaseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || deviceBaseName.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !(deviceBaseName.Length == 4
            && (deviceBaseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || deviceBaseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && deviceBaseName[3] is >= '1' and <= '9');
    }

    private bool TryRequestNormalDeletion(SafeFileHandle handle, out int error)
    {
        if (TryGetFault(ShredNativeOperation.Delete, out var deleteFault)
            && !deleteFault.ContinueNative)
        {
            error = deleteFault.ErrorCode;
            return false;
        }

        byte[] dispositionInformation = [1];
        if (!ShredNativeMethods.SetFileInformationByHandleBytes(
                handle,
                ShredNativeMethods.FileDispositionInfo,
                dispositionInformation,
                checked((uint)dispositionInformation.Length)))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    private static bool TryConfirmDeletion(string path, out int error)
    {
        var attributes = ShredNativeMethods.GetFileAttributes(ToExtendedPath(path));
        if (attributes != ShredNativeMethods.InvalidFileAttributes)
        {
            error = ErrorDeletePending;
            return false;
        }

        error = Marshal.GetLastWin32Error();
        return IsMissingTargetError(error);
    }

    internal bool TryDeleteMountPoint(string path, out int error)
    {
        if (TryGetFault(ShredNativeOperation.Unmount, out var unmountFault))
        {
            if (unmountFault.SimulatedSuccess)
            {
                error = 0;
                return true;
            }

            if (!unmountFault.ContinueNative)
            {
                error = unmountFault.ErrorCode;
                return false;
            }
        }

        var mountPath = Path.TrimEndingDirectorySeparator(path)
            + Path.DirectorySeparatorChar;
        if (!ShredNativeMethods.DeleteVolumeMountPoint(ToExtendedPath(mountPath)))
        {
            error = Marshal.GetLastWin32Error();
            return false;
        }

        error = 0;
        return true;
    }

    private static bool IsValidDataStreamName(string name) =>
        name.StartsWith(':')
        && name.EndsWith(":$DATA", StringComparison.OrdinalIgnoreCase)
        && !name.Contains(Path.DirectorySeparatorChar)
        && !name.Contains(Path.AltDirectorySeparatorChar);

    private static void ReportProgress(
        IProgress<ShredProgress>? progress,
        string path,
        long bytesProcessed,
        long totalBytes)
    {
        if (progress is null)
        {
            return;
        }

        try
        {
            progress.Report(new ShredProgress(
                path,
                1,
                1,
                bytesProcessed,
                totalBytes,
                bytesProcessed,
                totalBytes));
        }
        catch (Exception)
        {
            // Progress observers must never interrupt an in-flight destructive operation.
        }
    }

    private static ShredItemResult Failure(
        TargetSnapshot snapshot,
        ShredItemStatus status,
        long bytesAccepted,
        int error,
        bool isVolumeFatal,
        string currentPath,
        ShredFailureStage failureStage = ShredFailureStage.Unexpected) =>
        new(
            snapshot,
            status,
            bytesAccepted,
            error,
            isVolumeFatal,
            currentPath,
            failureStage);

    private static ShredItemResult CreateUnexpectedFailure(
        TargetSnapshot snapshot,
        MutationPhase mutationPhase,
        long bytesAccepted,
        int error,
        string currentPath)
    {
        var status = mutationPhase switch
        {
            MutationPhase.ContentOverwriteStarted => bytesAccepted > 0
                ? ShredItemStatus.PartiallyOverwritten
                : ShredItemStatus.FailedBeforeMutation,
            MutationPhase.ContentOverwritten
                or MutationPhase.ContentTruncated
                or MutationPhase.LinkMetadataMutated
                or MutationPhase.NameRandomized =>
                (snapshot.Attributes & FileAttributes.Directory) != 0
                    ? ShredItemStatus.FolderRetained
                    : ShredItemStatus.OverwrittenNotDeleted,
            _ => bytesAccepted > 0
                ? ShredItemStatus.PartiallyOverwritten
                : ShredItemStatus.FailedBeforeMutation,
        };
        return Failure(
            snapshot,
            status,
            bytesAccepted,
            error,
            IsVolumeFatalError(error),
            currentPath,
            ShredFailureStage.Unexpected);
    }

    private static bool IsMissingTargetError(int error) => error is 2 or 3;

    private bool TryGetFault(
        ShredNativeOperation operation,
        out ShredNativeFault fault)
    {
        if (_faultPlan is not null && _faultPlan.TryTake(operation, out fault))
        {
            fault.BeforeNativeCall?.Invoke();
            return true;
        }

        fault = null!;
        return false;
    }

    private static bool IsLink(TargetKind kind) => kind is
        TargetKind.SymbolicLink
        or TargetKind.Junction
        or TargetKind.MountPoint;

    private static bool IsVolumeFatalError(int error) => error is
        15 or   // ERROR_INVALID_DRIVE
        21 or   // ERROR_NOT_READY
        22 or   // ERROR_BAD_COMMAND
        23 or   // ERROR_CRC
        26 or   // ERROR_NOT_DOS_DISK
        27 or   // ERROR_SECTOR_NOT_FOUND
        29 or   // ERROR_WRITE_FAULT
        30 or   // ERROR_READ_FAULT
        31 or   // ERROR_GEN_FAILURE
        34 or   // ERROR_WRONG_DISK
        39 or   // ERROR_HANDLE_DISK_FULL
        55 or   // ERROR_DEV_NOT_EXIST
        112 or  // ERROR_DISK_FULL
        483 or  // ERROR_DEVICE_HARDWARE_ERROR
        665 or  // ERROR_FILE_SYSTEM_LIMITATION
        1005 or // ERROR_UNRECOGNIZED_VOLUME
        1110 or // ERROR_MEDIA_CHANGED
        1111 or // ERROR_BUS_RESET
        1112 or // ERROR_NO_MEDIA_IN_DRIVE
        1117 or // ERROR_IO_DEVICE
        1126 or // ERROR_DISK_RECALIBRATE_FAILED
        1127 or // ERROR_DISK_OPERATION_FAILED
        1128 or // ERROR_DISK_RESET_FAILED
        1167 or // ERROR_DEVICE_NOT_CONNECTED
        1392 or // ERROR_FILE_CORRUPT
        1393 or // ERROR_DISK_CORRUPT
        1610 or // ERROR_VOLUME_DISMOUNTED
        1785;   // ERROR_UNRECOGNIZED_MEDIA

    private static int GetNativeError(Exception exception) => exception switch
    {
        Win32Exception win32Exception => win32Exception.NativeErrorCode,
        UnauthorizedAccessException => 5,
        IOException ioException => ioException.HResult & 0xFFFF,
        CryptographicException => 995,
        _ => ErrorWriteFault,
    };

    private static string ToExtendedPath(string path) => $"\\\\?\\{path}";

    private static string NullTerminatedString(char[] buffer)
    {
        var length = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, length < 0 ? buffer.Length : length);
    }

    private static long CombineUnsigned(uint high, uint low) =>
        unchecked((long)(((ulong)high << 32) | low));

    private readonly record struct StreamOperationResult(
        bool Succeeded,
        long BytesAccepted,
        int ErrorCode,
        bool FlushFailed,
        bool TargetChanged)
    {
        internal static StreamOperationResult Success(long bytesAccepted) =>
            new(true, bytesAccepted, 0, false, false);

        internal static StreamOperationResult Failure(
            long bytesAccepted,
            int errorCode,
            bool flushFailed,
            bool targetChanged) =>
            new(false, bytesAccepted, errorCode, flushFailed, targetChanged);
    }

    private enum MutationPhase
    {
        None,
        ContentOverwriteStarted,
        ContentOverwritten,
        ContentTruncated,
        LinkMetadataMutated,
        NameRandomized,
    }

    [DoesNotReturn]
    private static void ThrowAttributeRestoreFailure(ShredItemResult result) =>
        throw new AttributeRestoreFailureException(result);

    private sealed class AttributeRestoreFailureException(ShredItemResult result) : Exception
    {
        internal ShredItemResult Result { get; } = result;
    }
}
