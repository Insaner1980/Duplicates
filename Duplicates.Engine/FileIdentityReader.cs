using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Duplicates.Engine;

internal static class FileIdentityReader
{
    public static string GetBestEffortIdentity(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (GetFileInformationByHandle(stream.SafeFileHandle, out ByHandleFileInformation info))
            {
                return string.Create(
                    CultureInfo.InvariantCulture,
                    $"{info.VolumeSerialNumber:X8}:{info.FileIndexHigh:X8}:{info.FileIndexLow:X8}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return "path:" + Path.GetFullPath(path).ToUpperInvariant();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle fileHandle, out ByHandleFileInformation fileInformation);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ByHandleFileInformation
    {
        public readonly uint FileAttributes;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public readonly System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public readonly uint VolumeSerialNumber;
        public readonly uint FileSizeHigh;
        public readonly uint FileSizeLow;
        public readonly uint NumberOfLinks;
        public readonly uint FileIndexHigh;
        public readonly uint FileIndexLow;
    }
}
