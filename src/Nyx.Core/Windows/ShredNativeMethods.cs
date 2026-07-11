using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nyx.Core.Windows;

internal static class ShredNativeMethods
{
    internal const uint InvalidFileAttributes = 0xFFFFFFFF;
    internal const uint Delete = 0x00010000;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint FileListDirectory = 0x00000001;
    internal const uint GenericWrite = 0x40000000;
    internal const uint Synchronize = 0x00100000;

    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;

    internal const uint OpenExisting = 3;
    internal const uint FileFlagWriteThrough = 0x80000000;
    internal const uint FileFlagSequentialScan = 0x08000000;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileFlagOpenNoRecall = 0x00100000;

    internal const int FileBasicInfo = 0;
    internal const int FileRenameInfo = 3;
    internal const int FileDispositionInfo = 4;
    internal const int FileStreamInfo = 7;
    internal const int FileAttributeTagInfo = 9;
    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileIdBothDirectoryRestartInfo = 11;
    internal const uint FileBegin = 0;

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", EntryPoint = "GetFileAttributesW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetFileAttributes(string fileName);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathSize,
        uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        [Out] byte[] information,
        uint bufferSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileSizeEx(
        SafeFileHandle file,
        out long fileSize);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathName(
        string fileName,
        [Out] char[] volumePathName,
        uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        [Out] char[] volumeName,
        uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "DeleteVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DeleteVolumeMountPoint(string volumeMountPoint);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeInformationW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeInformation(
        string rootPathName,
        [Out] char[]? volumeName,
        uint volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        [Out] char[] fileSystemName,
        uint fileSystemNameSize);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteFile(
        SafeFileHandle file,
        [In] byte[] buffer,
        uint numberOfBytesToWrite,
        out uint numberOfBytesWritten,
        nint overlapped);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFilePointerEx(
        SafeFileHandle file,
        long distanceToMove,
        out long newFilePointer,
        uint moveMethod);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetEndOfFile(SafeFileHandle file);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandleBytes(
        SafeFileHandle file,
        int fileInformationClass,
        [In] byte[] fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetFileInformationByHandleBasic(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileBasicInformation fileInformation,
        uint bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeFileTime
    {
        internal uint LowDateTime;
        internal uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal NativeFileTime CreationTime;
        internal NativeFileTime LastAccessTime;
        internal NativeFileTime LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileBasicInformation
    {
        internal long CreationTime;
        internal long LastAccessTime;
        internal long LastWriteTime;
        internal long ChangeTime;
        internal uint FileAttributes;
    }
}

internal enum ShredNativeOperation
{
    OpenObject,
    OpenStream,
    Write,
    Flush,
    Truncate,
    Rename,
    Delete,
    Unmount,
    SetAttributes,
}

internal sealed record ShredNativeFault(
    ShredNativeOperation Operation,
    int Occurrence,
    int ErrorCode,
    uint? SimulatedBytesWritten = null,
    Action? BeforeNativeCall = null,
    bool ContinueNative = false,
    bool SimulatedSuccess = false);

internal sealed class ShredNativeFaultPlan(params ShredNativeFault[] faults)
{
    private readonly Dictionary<(ShredNativeOperation Operation, int Occurrence), ShredNativeFault> _faults =
        faults.ToDictionary(static fault => (fault.Operation, fault.Occurrence));
    private readonly Dictionary<ShredNativeOperation, int> _occurrences = [];
    private readonly Lock _lock = new();

    internal bool TryTake(
        ShredNativeOperation operation,
        out ShredNativeFault fault)
    {
        lock (_lock)
        {
            var occurrence = _occurrences.GetValueOrDefault(operation) + 1;
            _occurrences[operation] = occurrence;
            return _faults.TryGetValue((operation, occurrence), out fault!);
        }
    }
}
