using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Nyx.Core.Windows;

internal static class WindowsNativeMethods
{
    internal const uint InvalidFileAttributes = 0xFFFFFFFF;
    internal const uint GenericRead = 0x80000000;
    internal const uint GenericWrite = 0x40000000;
    internal const uint Delete = 0x00010000;
    internal const uint FileReadAttributes = 0x00000080;
    internal const uint FileWriteAttributes = 0x00000100;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;

    internal const int FileStreamInfo = 7;
    internal const int FileAttributeTagInfo = 9;
    internal const int FileIdBothDirectoryInfo = 10;
    internal const int FileIdBothDirectoryRestartInfo = 11;

    internal const uint IoReparseTagMountPoint = 0xA0000003;
    internal const uint IoReparseTagSymbolicLink = 0xA000000C;
    internal const uint FsctlGetReparsePoint = 0x000900A8;
    internal const uint IoctlStorageQueryProperty = 0x002D1400;
    internal const int StorageDeviceSeekPenaltyProperty = 7;
    internal const int StorageDeviceTrimProperty = 8;

    internal const uint CfPlaceholderStateNoStates = 0x00000000;
    internal const uint CfPlaceholderStatePlaceholder = 0x00000001;
    internal const uint CfPlaceholderStateInSync = 0x00000008;
    internal const uint CfPlaceholderStatePartial = 0x00000010;
    internal const uint CfPlaceholderStatePartiallyOnDisk = 0x00000020;
    internal const uint CfPlaceholderStateInvalid = 0xFFFFFFFF;

    internal const uint DriveRemovable = 2;
    internal const uint DriveFixed = 3;

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

    [DllImport("kernel32.dll", EntryPoint = "GetFullPathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetFullPathName(
        string fileName,
        uint bufferLength,
        [Out] char[] fullPath,
        nint filePart);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] filePath,
        uint filePathSize,
        uint flags);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        [Out] byte[] information,
        uint bufferSize);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetReparsePoint(
        SafeFileHandle device,
        uint controlCode,
        nint inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryStorageProperty(
        SafeFileHandle device,
        uint controlCode,
        [In] byte[] inputBuffer,
        uint inputBufferSize,
        [Out] byte[] outputBuffer,
        uint outputBufferSize,
        out uint bytesReturned,
        nint overlapped);

    [DllImport("kernel32.dll", EntryPoint = "GetVolumePathNameW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumePathName(
        string fileName,
        [Out] char[] volumePathName,
        uint bufferLength);

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

    [DllImport("kernel32.dll", EntryPoint = "GetVolumeNameForVolumeMountPointW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint,
        [Out] char[] volumeName,
        uint bufferLength);

    [DllImport("kernel32.dll", EntryPoint = "GetDriveTypeW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint GetDriveType(string rootPathName);

    [DllImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern uint QueryDosDevice(
        string deviceName,
        [Out] char[] targetPath,
        uint maximumLength);

    [DllImport("cldapi.dll", ExactSpelling = true)]
    internal static extern uint CfGetPlaceholderStateFromAttributeTag(
        uint fileAttributes,
        uint reparseTag);

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
}
