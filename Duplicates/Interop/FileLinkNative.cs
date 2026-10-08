using System.Buffers;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using Duplicates.Models;
using Microsoft.Win32.SafeHandles;

namespace Duplicates.Services;

internal sealed partial class FileLinkNative : IFileLinkPlatform
{
    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadControl = 0x00020000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint SymbolicLinkFlagAllowUnprivilegedCreate = 0x2;
    private const int FileBasicInfo = 0;
    private const int FileIdInfo = 18;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const ushort MaterialDaclControl = 0x0400 | 0x1000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoMoreFiles = 18;
    private const int ErrorHandleEof = 38;
    private const uint DriveRemote = 4;
    private const uint RecycleFileFlags =
        0x0004 | // FOF_SILENT
        0x0010 | // FOF_NOCONFIRMATION
        0x0400 | // FOF_NOERRORUI
        0x1000 | // FOF_NORECURSION
        0x00100000 | // FOFX_EARLYFAILURE
        0x00080000; // FOFX_RECYCLEONDELETE

    public IFileLinkHandle OpenNoFollow(string path, bool requestDelete) =>
        Open(path, requestDelete, noFollow: true, shareDelete: true);

    public IFileLinkHandle OpenFollow(string path) =>
        Open(path, requestDelete: false, noFollow: false, shareDelete: true);

    public FileLinkFileInfo GetInfo(IFileLinkHandle handle)
    {
        NativeFileLinkHandle native = GetNativeHandle(handle);
        if (!GetFileInformationByHandleEx(
                native.SafeHandle,
                FileIdInfo,
                out FileIdInformation identity,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandleEx(
                native.SafeHandle,
                FileBasicInfo,
                out FileBasicInformation basic,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetFileInformationByHandle(native.SafeHandle, out ByHandleFileInformation byHandle))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!GetFileSizeEx(native.SafeHandle, out long length))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var fileSystemName = new StringBuilder(64);
        if (!GetVolumeInformationByHandleW(
                native.SafeHandle,
                null,
                0,
                out _,
                out _,
                out _,
                fileSystemName,
                fileSystemName.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return new FileLinkFileInfo(
            new FileSystemIdentity(
                identity.VolumeSerialNumber,
                identity.FileIdLow,
                identity.FileIdHigh),
            length,
            DateTime.FromFileTimeUtc(basic.LastWriteTime),
            (FileAttributes)basic.FileAttributes,
            byHandle.NumberOfLinks,
            fileSystemName.ToString(),
            IsLocalHandle(native.SafeHandle));
    }

    public FileLinkSecurityInfo GetSecurityInfo(IFileLinkHandle handle)
    {
        NativeFileLinkHandle native = GetNativeHandle(handle);
        uint result = GetSecurityInfoNative(
            native.SafeHandle,
            SeObjectType.SeFileObject,
            OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
            out nint ownerSid,
            out nint groupSid,
            out _,
            out _,
            out nint securityDescriptor);
        if (result != 0)
        {
            throw new Win32Exception((int)result);
        }

        try
        {
            if (ownerSid == 0 || groupSid == 0 || securityDescriptor == 0)
            {
                throw new IOException("The file security descriptor is incomplete.");
            }

            if (!GetSecurityDescriptorDacl(
                    securityDescriptor,
                    out bool daclPresent,
                    out nint dacl,
                    out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            byte[] daclBytes = [];
            if (daclPresent && dacl != 0)
            {
                if (!GetAclInformation(
                        dacl,
                        out AclSizeInformation size,
                        (uint)Marshal.SizeOf<AclSizeInformation>(),
                        AclInformationClass.AclSizeInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                daclBytes = new byte[checked((int)size.AclBytesInUse)];
                Marshal.Copy(dacl, daclBytes, 0, daclBytes.Length);
            }

            if (!GetSecurityDescriptorControl(securityDescriptor, out ushort control, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            return new FileLinkSecurityInfo(
                CopySid(ownerSid),
                CopySid(groupSid),
                daclPresent,
                daclBytes,
                (ushort)(control & MaterialDaclControl));
        }
        finally
        {
            if (securityDescriptor != 0)
            {
                _ = LocalFree(securityDescriptor);
            }
        }
    }

    public bool StreamsEqual(
        string leftPath,
        IFileLinkHandle leftHandle,
        string rightPath,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken)
    {
        FileSystemIdentity leftIdentity = GetInfo(leftHandle).Identity;
        FileSystemIdentity rightIdentity = GetInfo(rightHandle).Identity;
        Dictionary<string, long> leftStreams = EnumerateStreams(leftPath);
        Dictionary<string, long> rightStreams = EnumerateStreams(rightPath);

        if (leftStreams.Count != rightStreams.Count)
        {
            return false;
        }

        foreach ((string name, long length) in leftStreams)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!rightStreams.TryGetValue(name, out long rightLength) || rightLength != length)
            {
                return false;
            }

            using IFileLinkHandle leftStream = OpenNoFollow(leftPath + name, requestDelete: false);
            using IFileLinkHandle rightStream = OpenNoFollow(rightPath + name, requestDelete: false);
            if (!ContentEquals(leftStream, rightStream, cancellationToken))
            {
                return false;
            }
        }

        return GetInfo(leftHandle).Identity == leftIdentity &&
            GetInfo(rightHandle).Identity == rightIdentity &&
            ProbeNoFollow(leftPath) is { State: FileLinkPathState.Present, Identity: { } currentLeft } &&
            currentLeft == leftIdentity &&
            ProbeNoFollow(rightPath) is { State: FileLinkPathState.Present, Identity: { } currentRight } &&
            currentRight == rightIdentity;
    }

    public bool ContentEquals(
        IFileLinkHandle leftHandle,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken)
    {
        NativeFileLinkHandle left = GetNativeHandle(leftHandle);
        NativeFileLinkHandle right = GetNativeHandle(rightHandle);
        long leftLength = RandomAccess.GetLength(left.SafeHandle);
        if (leftLength != RandomAccess.GetLength(right.SafeHandle))
        {
            return false;
        }

        const int bufferSize = 128 * 1024;
        byte[] leftBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        byte[] rightBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            long offset = 0;
            while (offset < leftLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(bufferSize, leftLength - offset);
                int leftRead = RandomAccess.Read(left.SafeHandle, leftBuffer.AsSpan(0, requested), offset);
                int rightRead = RandomAccess.Read(right.SafeHandle, rightBuffer.AsSpan(0, requested), offset);
                if (leftRead != rightRead || leftRead == 0 ||
                    !leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
                {
                    return false;
                }

                offset += leftRead;
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(leftBuffer);
            ArrayPool<byte>.Shared.Return(rightBuffer);
        }
    }

    public void Rename(IFileLinkHandle handle, string destinationPath)
    {
        NativeFileLinkHandle native = GetNativeHandle(handle);
        string destination = Path.GetFullPath(destinationPath);
        string destinationDirectory = Path.GetDirectoryName(destination) ??
            throw new IOException("The rename destination has no parent directory.");
        string sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(native.Path)) ??
            throw new IOException("The rename source has no parent directory.");
        if (!string.Equals(sourceDirectory, destinationDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Handle-owned replacement renames must remain in the source directory.");
        }

        byte[] nameBytes = Encoding.Unicode.GetBytes(destination);
        int rootDirectoryOffset = IntPtr.Size;
        int fileNameLengthOffset = rootDirectoryOffset + IntPtr.Size;
        int fileNameOffset = fileNameLengthOffset + sizeof(uint);
        const int minimumStructureSize = 24;
        int bufferSize = minimumStructureSize + nameBytes.Length + sizeof(char);
        nint buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteByte(buffer, 0, 0);
            Marshal.WriteIntPtr(buffer, rootDirectoryOffset, 0);
            Marshal.WriteInt32(buffer, fileNameLengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, buffer + fileNameOffset, nameBytes.Length);
            if (!SetFileInformationByHandle(native.SafeHandle, FileRenameInfo, buffer, (uint)bufferSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            native.Path = destination;
            string actualDestination = NormalizeFinalPath(GetFinalPath(native.SafeHandle));
            native.Path = actualDestination;
            if (!string.Equals(actualDestination, destination, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"The handle-owned rename reached '{actualDestination}' instead of '{destination}'.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public IFileLinkHandle CreateHardLinkAndOpen(string linkPath, string existingPath)
    {
        string link = Path.GetFullPath(linkPath);
        string existing = Path.GetFullPath(existingPath);
        if (!CreateHardLinkW(link, existing, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Open(link, requestDelete: true, noFollow: true, shareDelete: false);
    }

    public IFileLinkHandle CreateSymbolicLinkAndOpen(string linkPath, string targetPath)
    {
        string link = Path.GetFullPath(linkPath);
        string target = Path.GetFullPath(targetPath);
        if (!CreateSymbolicLinkW(link, target, SymbolicLinkFlagAllowUnprivilegedCreate))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return Open(link, requestDelete: true, noFollow: true, shareDelete: false);
    }

    public string? GetSymbolicLinkTarget(string linkPath, IFileLinkHandle linkHandle)
    {
        FileSystemIdentity expectedIdentity = GetInfo(linkHandle).Identity;
        string canonicalLink = Path.GetFullPath(linkPath);
        string? target = new FileInfo(canonicalLink).LinkTarget;
        FileLinkPathProbe probe = ProbeNoFollow(canonicalLink);
        if (probe.State != FileLinkPathState.Present || probe.Identity != expectedIdentity)
        {
            throw new IOException("The symbolic-link entry changed during verification.");
        }

        if (target is null)
        {
            return null;
        }

        return Path.IsPathFullyQualified(target)
            ? Path.GetFullPath(target)
            : Path.GetFullPath(target, Path.GetDirectoryName(canonicalLink)!);
    }

    public string GetFinalPath(IFileLinkHandle handle)
    {
        NativeFileLinkHandle native = GetNativeHandle(handle);
        return NormalizeFinalPath(GetFinalPath(native.SafeHandle));
    }

    public FileLinkPathProbe ProbeNoFollow(string path)
    {
        try
        {
            using IFileLinkHandle handle = OpenNoFollow(path, requestDelete: false);
            return new FileLinkPathProbe(FileLinkPathState.Present, GetInfo(handle).Identity);
        }
        catch (Exception ex) when (TryGetNativeError(ex, out int error) &&
            error is ErrorFileNotFound or ErrorPathNotFound)
        {
            return new FileLinkPathProbe(FileLinkPathState.Missing, null);
        }
        catch (FileNotFoundException)
        {
            return new FileLinkPathProbe(FileLinkPathState.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new FileLinkPathProbe(FileLinkPathState.Missing, null);
        }
        catch
        {
            return new FileLinkPathProbe(FileLinkPathState.Indeterminate, null);
        }
    }

    public void DeleteByHandle(IFileLinkHandle handle)
    {
        NativeFileLinkHandle native = GetNativeHandle(handle);
        nint buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            Marshal.WriteInt32(buffer, 1);
            if (!SetFileInformationByHandle(native.SafeHandle, FileDispositionInfo, buffer, sizeof(int)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void SendToRecycleBin(
        string path,
        Action verifyOwnershipAfterShellItemCreation)
    {
        ArgumentNullException.ThrowIfNull(verifyOwnershipAfterShellItemCreation);
        string canonicalPath = Path.GetFullPath(path);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                SendToRecycleBinOnSta(canonicalPath, verifyOwnershipAfterShellItemCreation);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Duplicates identity-safe Recycle Bin operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void SendToRecycleBinOnSta(
        string path,
        Action verifyOwnershipAfterShellItemCreation)
    {
        IShellItem? item = null;
        IFileOperation? operation = null;
        try
        {
            Guid shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                path,
                nint.Zero,
                ref shellItemId,
                out item));
            verifyOwnershipAfterShellItemCreation();

            operation = (IFileOperation)(object)new FileOperationComObject();
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(RecycleFileFlags));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, nint.Zero));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out int wasAborted));
            if (wasAborted != 0)
            {
                throw new IOException("The Recycle Bin operation was aborted.");
            }
        }
        catch (COMException ex)
        {
            throw new IOException("The Recycle Bin operation failed.", ex);
        }
        finally
        {
            if (item is not null)
            {
                Marshal.FinalReleaseComObject(item);
            }

            if (operation is not null)
            {
                Marshal.FinalReleaseComObject(operation);
            }
        }
    }

    private static NativeFileLinkHandle Open(
        string path,
        bool requestDelete,
        bool noFollow,
        bool shareDelete)
    {
        string canonical = Path.GetFullPath(path);
        uint desiredAccess = GenericRead | ReadControl | FileReadAttributes;
        if (requestDelete)
        {
            desiredAccess |= DeleteAccess;
        }

        uint flags = FileFlagBackupSemantics;
        if (noFollow)
        {
            flags |= FileFlagOpenReparsePoint;
        }

        SafeFileHandle safeHandle = CreateFileW(
            canonical,
            desiredAccess,
            ShareRead | (shareDelete ? ShareDelete : 0),
            0,
            OpenExisting,
            flags,
            0);
        if (safeHandle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            safeHandle.Dispose();
            throw new Win32Exception(error);
        }

        return new NativeFileLinkHandle(canonical, safeHandle);
    }

    private static Dictionary<string, long> EnumerateStreams(string path)
    {
        var streams = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        nint find = FindFirstStreamW(Path.GetFullPath(path), 0, out Win32FindStreamData data, 0);
        if (find == -1)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            AddStream(data);
            while (FindNextStreamW(find, out data))
            {
                AddStream(data);
            }

            int error = Marshal.GetLastWin32Error();
            if (error is not ErrorNoMoreFiles and not ErrorHandleEof)
            {
                throw new Win32Exception(error);
            }
        }
        finally
        {
            _ = FindClose(find);
        }

        return streams;

        void AddStream(Win32FindStreamData stream)
        {
            if (!string.Equals(stream.StreamName, "::$DATA", StringComparison.OrdinalIgnoreCase))
            {
                streams.Add(stream.StreamName, stream.StreamSize);
            }
        }
    }

    private static bool IsLocalHandle(SafeFileHandle handle)
    {
        string finalPath = NormalizeFinalPath(GetFinalPath(handle));
        var volumePath = new StringBuilder(512);
        if (!GetVolumePathNameW(finalPath, volumePath, (uint)volumePath.Capacity))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        return GetDriveTypeW(volumePath.ToString()) != DriveRemote;
    }

    private static string NormalizeFinalPath(string finalPath)
    {
        if (finalPath.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            finalPath = "\\\\" + finalPath[8..];
        }
        else if (finalPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            finalPath = finalPath[4..];
        }

        return Path.GetFullPath(finalPath);
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (length >= buffer.Capacity)
        {
            buffer.Capacity = checked((int)length + 1);
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }

        return buffer.ToString();
    }

    private static byte[] CopySid(nint sid)
    {
        int length = GetLengthSid(sid);
        if (length <= 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        var bytes = new byte[length];
        Marshal.Copy(sid, bytes, 0, length);
        return bytes;
    }

    private static bool TryGetNativeError(Exception exception, out int error)
    {
        if (exception is Win32Exception win32)
        {
            error = win32.NativeErrorCode;
            return true;
        }

        error = exception.HResult & 0xFFFF;
        return error != 0;
    }

    private static NativeFileLinkHandle GetNativeHandle(IFileLinkHandle handle) =>
        handle as NativeFileLinkHandle ??
        throw new ArgumentException("The handle was not created by the native link platform.", nameof(handle));

    private sealed partial class NativeFileLinkHandle : IFileLinkHandle
    {
        public NativeFileLinkHandle(string path, SafeFileHandle safeHandle)
        {
            Path = path;
            SafeHandle = safeHandle;
        }

        public string Path { get; set; }

        public SafeFileHandle SafeHandle { get; }

        public void Dispose() => SafeHandle.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AclSizeInformation
    {
        public uint AceCount;
        public uint AclBytesInUse;
        public uint AclBytesFree;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindStreamData
    {
        public long StreamSize;

        private StreamNameBuffer _streamName;

        public string StreamName
        {
            get
            {
                ReadOnlySpan<ushort> codeUnits = _streamName;
                ReadOnlySpan<char> characters = MemoryMarshal.Cast<ushort, char>(codeUnits);
                int terminator = characters.IndexOf('\0');
                return new string(terminator < 0 ? characters : characters[..terminator]);
            }
        }
    }

    [System.Runtime.CompilerServices.InlineArray(296)]
    private struct StreamNameBuffer
    {
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Sonar", "S1144", Justification = "InlineArray requires one element field; the compiler uses it for span conversion and native layout.")]
        public ushort Element0;
    }

    private enum SeObjectType
    {
        SeFileObject = 1,
    }

    private enum AclInformationClass
    {
        AclRevisionInformation = 1,
        AclSizeInformation = 2,
    }

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM wrappers are required by Marshal COM activation and deterministic release APIs.")]
    private interface IShellItem
    {
    }

    [ComImport]
    [Guid("3AD05575-8857-4850-9277-11B85BDB8E09")]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationComObject
    {
    }

    [ComImport]
    [Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "SYSLIB1096", Justification = "Runtime COM wrappers are required by Marshal COM activation and deterministic release APIs.")]
    private interface IFileOperation
    {
        [PreserveSig]
        int Advise(nint progressSink, out uint cookie);

        [PreserveSig]
        int Unadvise(uint cookie);

        [PreserveSig]
        int SetOperationFlags(uint flags);

        [PreserveSig]
        int SetProgressMessage([MarshalAs(UnmanagedType.LPWStr)] string message);

        [PreserveSig]
        int SetProgressDialog(nint progressDialog);

        [PreserveSig]
        int SetProperties(nint propertyChangeArray);

        [PreserveSig]
        int SetOwnerWindow(nint ownerWindow);

        [PreserveSig]
        int ApplyPropertiesToItem(IShellItem item);

        [PreserveSig]
        int ApplyPropertiesToItems(nint items);

        [PreserveSig]
        int RenameItem(
            IShellItem item,
            [MarshalAs(UnmanagedType.LPWStr)] string newName,
            nint progressSink);

        [PreserveSig]
        int RenameItems(nint items, [MarshalAs(UnmanagedType.LPWStr)] string newName);

        [PreserveSig]
        int MoveItem(
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? newName,
            nint progressSink);

        [PreserveSig]
        int MoveItems(nint items, IShellItem destinationFolder);

        [PreserveSig]
        int CopyItem(
            IShellItem item,
            IShellItem destinationFolder,
            [MarshalAs(UnmanagedType.LPWStr)] string? copyName,
            nint progressSink);

        [PreserveSig]
        int CopyItems(nint items, IShellItem destinationFolder);

        [PreserveSig]
        int DeleteItem(IShellItem item, nint progressSink);

        [PreserveSig]
        int DeleteItems(nint items);

        [PreserveSig]
        int NewItem(
            IShellItem destinationFolder,
            uint fileAttributes,
            [MarshalAs(UnmanagedType.LPWStr)] string name,
            [MarshalAs(UnmanagedType.LPWStr)] string? templateName,
            nint progressSink);

        [PreserveSig]
        int PerformOperations();

        [PreserveSig]
        int GetAnyOperationsAborted(out int anyOperationsAborted);
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileIdInformation information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int informationClass,
        out FileBasicInformation information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileSizeEx(SafeFileHandle file, out long fileSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformationByHandleW(
        SafeFileHandle file,
        StringBuilder? volumeName,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemName,
        int fileSystemNameSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileInformationByHandle(
        SafeFileHandle file,
        int informationClass,
        nint information,
        uint bufferSize);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLinkW(string fileName, string existingFileName, nint securityAttributes);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateSymbolicLinkW(string symlinkFileName, string targetFileName, uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint FindFirstStreamW(
        string fileName,
        int infoLevel,
        out Win32FindStreamData findStreamData,
        uint flags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindNextStreamW(nint findStream, out Win32FindStreamData findStreamData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FindClose(nint findFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathSize,
        uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string fileName,
        StringBuilder volumePathName,
        uint bufferLength);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDriveTypeW(string rootPathName);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = true)]
    private static partial uint GetSecurityInfoNative(
        SafeFileHandle handle,
        SeObjectType objectType,
        uint securityInformation,
        out nint owner,
        out nint group,
        out nint dacl,
        out nint sacl,
        out nint securityDescriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorDacl(
        nint securityDescriptor,
        [MarshalAs(UnmanagedType.Bool)] out bool daclPresent,
        out nint dacl,
        [MarshalAs(UnmanagedType.Bool)] out bool daclDefaulted);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSecurityDescriptorControl(
        nint securityDescriptor,
        out ushort control,
        out uint revision);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetAclInformation(
        nint acl,
        out AclSizeInformation aclInformation,
        uint aclInformationLength,
        AclInformationClass aclInformationClass);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
