using System.Buffers;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Duplicates.Models;
using Microsoft.Win32.SafeHandles;

namespace Duplicates.Services;

internal sealed class IdentityFileTransactions : IIdentityFileTransactions
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ShareRead = 0x00000001;
    private const uint ShareWrite = 0x00000002;
    private const uint ShareDelete = 0x00000004;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileFlagWriteThrough = 0x80000000;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const int FileBasicInfo = 0;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorAccessDenied = 5;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;

    public IdentityTrackedFile Capture(string path)
    {
        string canonicalPath = Canonicalize(path);
        using SafeFileHandle handle = Open(
            canonicalPath,
            GenericRead | FileReadAttributes,
            ShareRead | ShareDelete,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        return CaptureOrdinary(handle, canonicalPath);
    }

    public IdentityTrackedFile CreateOwnedNew(string destinationPath)
    {
        string destination = Canonicalize(destinationPath);
        using SafeFileHandle handle = Open(
            destination,
            GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
            ShareRead,
            CreateNew,
            FileFlagSequentialScan | FileFlagWriteThrough | FileFlagOpenReparsePoint);
        try
        {
            return CaptureOrdinary(handle, destination);
        }
        catch (Exception creationFailure)
        {
            try
            {
                DeleteByHandle(handle);
            }
            catch (Exception cleanupFailure)
            {
                throw new IdentityOwnedCreationRecoveryException(
                    destination,
                    creationFailure,
                    cleanupFailure);
            }

            throw;
        }
    }

    public async Task<IdentityTrackedFile> CopyAndFlushAsync(
        IdentityTrackedFile source,
        IdentityTrackedFile destination,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        string sourcePath = Canonicalize(source.Path);
        string destinationPath = Canonicalize(destination.Path);
        EnsureSibling(sourcePath, destinationPath);
        cancellationToken.ThrowIfCancellationRequested();

        SafeFileHandle sourceHandle = Open(
            sourcePath,
            GenericRead | FileReadAttributes,
            ShareRead,
            OpenExisting,
            FileFlagSequentialScan | FileFlagOpenReparsePoint);
        SafeFileHandle? destinationHandle = null;
        try
        {
            ValidateExpected(sourceHandle, source);
            destinationHandle = Open(
                destinationPath,
                GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
                ShareRead,
                OpenExisting,
                FileFlagSequentialScan | FileFlagWriteThrough | FileFlagOpenReparsePoint);
            ValidateExpected(destinationHandle, destination);
        }
        catch
        {
            destinationHandle?.Dispose();
            sourceHandle.Dispose();
            throw;
        }

        using var input = new FileStream(sourceHandle, FileAccess.Read, 128 * 1024, isAsync: false);
        using var output = new FileStream(destinationHandle!, FileAccess.ReadWrite, 128 * 1024, isAsync: false);
        output.SetLength(0);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = await input.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                copied += read;
                progress?.Report(source.Length == 0 ? 1 : Math.Clamp(copied / (double)source.Length, 0, 1));
            }

            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            output.Flush(flushToDisk: true);
            cancellationToken.ThrowIfCancellationRequested();
            IdentityTrackedFile completed = CaptureOrdinary(output.SafeFileHandle, destinationPath);
            if (completed.Identity != destination.Identity || completed.Length != source.Length)
            {
                throw new IOException("The owned temporary copy changed during creation.");
            }

            progress?.Report(1);
            return completed with { Identity = destination.Identity };
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public bool EntryExistsCaseInsensitive(string path)
    {
        string canonicalPath = Canonicalize(path);
        string directory = Path.GetDirectoryName(canonicalPath) ??
            throw new IOException("The identity-owned artifact path has no parent directory.");
        string name = Path.GetFileName(canonicalPath);
        return Directory.EnumerateFileSystemEntries(directory)
            .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
    }

    public IDisposable GuardOwnedPath(IdentityTrackedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string path = Canonicalize(file.Path);
        SafeFileHandle handle = Open(
            path,
            GenericRead | FileReadAttributes,
            ShareRead | ShareWrite,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        try
        {
            ValidateExpected(handle, file);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IDisposable GuardSourceSnapshot(IdentityTrackedFile source)
    {
        ArgumentNullException.ThrowIfNull(source);
        string path = Canonicalize(source.Path);
        SafeFileHandle handle = Open(
            path,
            GenericRead | FileReadAttributes,
            ShareRead,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        try
        {
            ValidateSnapshot(handle, source);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public IdentityMoveResult MoveNoOverwrite(IdentityTrackedFile source, string destinationPath) =>
        MoveNoOverwriteCore(source, destinationPath, validateSnapshot: false);

    public IdentityMoveResult MoveSourceNoOverwrite(IdentityTrackedFile source, string destinationPath) =>
        MoveNoOverwriteCore(source, destinationPath, validateSnapshot: true);

    private IdentityMoveResult MoveNoOverwriteCore(
        IdentityTrackedFile source,
        string destinationPath,
        bool validateSnapshot)
    {
        ArgumentNullException.ThrowIfNull(source);
        string sourcePath = Canonicalize(source.Path);
        string destination = Canonicalize(destinationPath);
        EnsureSibling(sourcePath, destination);
        if (EntryExistsCaseInsensitive(destination))
        {
            throw new IdentityMoveException(
                "The identity transaction destination already exists.",
                IdentityMoveCommitState.NotCommitted,
                sourcePath,
                destination,
                source.Identity);
        }

        using SafeFileHandle handle = Open(
            sourcePath,
            GenericRead | DeleteAccess | FileReadAttributes,
            ShareRead,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        if (validateSnapshot)
        {
            ValidateSnapshot(handle, source);
        }
        else
        {
            ValidateExpected(handle, source);
        }
        try
        {
            RenameByHandle(handle, destination);
            IdentityTrackedFile moved = CaptureOrdinary(handle, destination);
            if (moved.Identity != source.Identity)
            {
                throw new IOException("The moved artifact identity changed.");
            }

            return new IdentityMoveResult(
                IdentityMoveCommitState.Committed,
                moved with { Identity = source.Identity });
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            IdentityMoveCommitState commitState = DetermineMoveState(handle, sourcePath, destination);
            throw new IdentityMoveException(
                "The handle-owned move did not reach a verified terminal state.",
                commitState,
                sourcePath,
                destination,
                source.Identity,
                ex);
        }
    }

    public void DeleteOwned(IdentityTrackedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string path = Canonicalize(file.Path);
        SafeFileHandle handle = Open(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            ShareRead,
            OpenExisting,
            FileFlagBackupSemantics | FileFlagOpenReparsePoint);
        try
        {
            ValidateExpected(handle, file);
            DeleteByHandle(handle);
        }
        finally
        {
            handle.Dispose();
        }

        if (Probe(path).State != IdentityPathState.Missing)
        {
            throw new IOException("The owned artifact still exists after deletion.");
        }
    }

    public IdentityPathProbe Probe(string path)
    {
        string canonicalPath;
        try
        {
            canonicalPath = Canonicalize(path);
        }
        catch
        {
            return new IdentityPathProbe(IdentityPathState.Indeterminate, null);
        }

        try
        {
            using SafeFileHandle handle = Open(
                canonicalPath,
                FileReadAttributes,
                ShareRead | ShareWrite | ShareDelete,
                OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint);
            return new IdentityPathProbe(IdentityPathState.Present, GetIdentity(handle));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound)
        {
            return new IdentityPathProbe(IdentityPathState.Missing, null);
        }
        catch (FileNotFoundException)
        {
            return new IdentityPathProbe(IdentityPathState.Missing, null);
        }
        catch (DirectoryNotFoundException)
        {
            return new IdentityPathProbe(IdentityPathState.Missing, null);
        }
        catch
        {
            return new IdentityPathProbe(IdentityPathState.Indeterminate, null);
        }
    }

    private static IdentityTrackedFile CaptureOrdinary(SafeFileHandle handle, string expectedPath)
    {
        FileBasicInformation basic = GetBasicInfo(handle);
        FileAttributes attributes = (FileAttributes)basic.FileAttributes;
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new IdentityTransactionException("Only an ordinary non-link file can be used by the identity transaction.");
        }

        string finalPath = GetFinalPath(handle);
        if (!string.Equals(finalPath, expectedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new IdentityTransactionException("The identity transaction path changed while its handle was open.");
        }

        if (!GetFileSizeEx(handle, out long length))
        {
            throw CreateTransactionException();
        }

        return new IdentityTrackedFile(
            finalPath,
            GetIdentity(handle),
            length,
            DateTime.FromFileTimeUtc(basic.LastWriteTime),
            attributes);
    }

    private static void ValidateExpected(SafeFileHandle handle, IdentityTrackedFile expected)
    {
        IdentityTrackedFile current = CaptureOrdinary(handle, Canonicalize(expected.Path));
        if (current.Identity != expected.Identity)
        {
            throw new IdentityTransactionException("The identity transaction path is occupied by a different file identity.");
        }
    }

    private static void ValidateSnapshot(SafeFileHandle handle, IdentityTrackedFile expected)
    {
        IdentityTrackedFile current = CaptureOrdinary(handle, Canonicalize(expected.Path));
        if (current.Identity != expected.Identity ||
            current.Length != expected.Length ||
            current.ModifiedUtc != expected.ModifiedUtc)
        {
            throw new IdentitySourceChangedException();
        }
    }

    private static FileSystemIdentity GetIdentity(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out FileIdInformation info,
                (uint)Marshal.SizeOf<FileIdInformation>()))
        {
            throw CreateTransactionException();
        }

        return new FileSystemIdentity(info.VolumeSerialNumber, info.FileIdLow, info.FileIdHigh);
    }

    private static FileBasicInformation GetBasicInfo(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandleEx(
                handle,
                FileBasicInfo,
                out FileBasicInformation info,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw CreateTransactionException();
        }

        return info;
    }

    private static void RenameByHandle(SafeFileHandle handle, string destinationPath)
    {
        byte[] nameBytes = Encoding.Unicode.GetBytes(destinationPath);
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
            if (!SetFileInformationByHandle(handle, FileRenameInfo, buffer, (uint)bufferSize))
            {
                throw CreateTransactionException();
            }

            string actualPath = GetFinalPath(handle);
            if (!string.Equals(actualPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The handle-owned rename reached an unexpected path.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static void DeleteByHandle(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInformation { DeleteFile = true };
        if (!SetFileInformationByHandle(
                handle,
                FileDispositionInfo,
                ref disposition,
                (uint)Marshal.SizeOf<FileDispositionInformation>()))
        {
            throw CreateTransactionException();
        }
    }

    private static IdentityMoveCommitState DetermineMoveState(
        SafeFileHandle handle,
        string sourcePath,
        string destinationPath)
    {
        try
        {
            string currentPath = GetFinalPath(handle);
            if (string.Equals(currentPath, destinationPath, StringComparison.OrdinalIgnoreCase))
            {
                return IdentityMoveCommitState.Committed;
            }

            if (string.Equals(currentPath, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                return IdentityMoveCommitState.NotCommitted;
            }
        }
        catch
        {
        }

        return IdentityMoveCommitState.Indeterminate;
    }

    private static SafeFileHandle Open(
        string path,
        uint desiredAccess,
        uint shareMode,
        uint creationDisposition,
        uint flags)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            desiredAccess,
            shareMode,
            0,
            creationDisposition,
            flags,
            0);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileNotFound or ErrorPathNotFound)
            {
                throw new FileNotFoundException(null, path, new Win32Exception(error));
            }

            if (error == ErrorAccessDenied)
            {
                throw new UnauthorizedAccessException(new Win32Exception(error).Message);
            }

            string detail = error is ErrorFileExists or ErrorAlreadyExists
                ? "The identity transaction destination already exists."
                : new Win32Exception(error).Message;
            throw new IOException(detail, new Win32Exception(error));
        }

        return handle;
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0)
        {
            throw CreateTransactionException();
        }

        if (length >= buffer.Capacity)
        {
            buffer = new StringBuilder(checked((int)length + 1));
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity)
            {
                throw CreateTransactionException();
            }
        }

        string path = buffer.ToString();
        if (path.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase))
        {
            path = "\\\\" + path[8..];
        }
        else if (path.StartsWith("\\\\?\\", StringComparison.OrdinalIgnoreCase))
        {
            path = path[4..];
        }

        return Canonicalize(path);
    }

    private static void EnsureSibling(string sourcePath, string destinationPath)
    {
        if (!string.Equals(
                Path.GetDirectoryName(sourcePath),
                Path.GetDirectoryName(destinationPath),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException("Identity transaction files must stay in the source directory.");
        }
    }

    private static IdentityTransactionException CreateTransactionException()
    {
        var failure = new Win32Exception(Marshal.GetLastWin32Error());
        return new IdentityTransactionException(failure.Message, failure);
    }

    private static string Canonicalize(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The identity transaction path must be fully qualified.", nameof(path));
        }

        return Path.GetFullPath(path);
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
    private struct FileDispositionInformation
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool DeleteFile;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileIdInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle file, out long fileSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(
        SafeFileHandle file,
        StringBuilder filePath,
        uint filePathLength,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        nint fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        ref FileDispositionInformation fileInformation,
        uint bufferSize);
}
