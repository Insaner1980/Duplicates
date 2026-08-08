using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Models;
using Microsoft.VisualBasic.FileIO;

namespace Duplicates.Services;

public sealed class FileActionService : IFileActionService
{
    private const uint RecycleEmptyDirectoryFlags =
        0x0004 | // FOF_SILENT
        0x0010 | // FOF_NOCONFIRMATION
        0x0400 | // FOF_NOERRORUI
        0x1000 | // FOF_NORECURSION
        0x00080000; // FOFX_RECYCLEONDELETE

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).Select(static number => $"COM{number}"), .. Enumerable.Range(1, 9).Select(static number => $"LPT{number}")],
        StringComparer.OrdinalIgnoreCase);

    private readonly ISettingsService _settingsService;

    public FileActionService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<FileActionTarget> targets,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                var failures = new List<FileActionFailure>();
                int deletedCount = 0;
                long deletedBytes = 0;
                int processedCount = 0;
                var deletedPaths = new List<string>();
                RecycleOption recycleOption = _settingsService.Current.DeletionMode == DeletionMode.RecycleBin
                    ? RecycleOption.SendToRecycleBin
                    : RecycleOption.DeletePermanently;

                foreach (FileActionTarget target in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (processedCount > 0)
                        {
                            throw new DeleteOperationCanceledException(
                                new DeleteSummary(
                                    deletedCount,
                                    deletedBytes,
                                    failures.ToArray(),
                                    deletedPaths.ToArray()),
                                cancellationToken);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    try
                    {
                        RevalidateTarget(target);
                        DeleteTarget(target, recycleOption);
                        deletedCount++;
                        deletedBytes += target.SizeBytes;
                        deletedPaths.Add(target.FullPath);
                    }
                    catch (Exception ex) when (IsOperationalFailure(ex))
                    {
                        failures.Add(new FileActionFailure(target.FullPath, ex.Message));
                    }
                    finally
                    {
                        processedCount++;
                        progress?.Report(new DeleteProgress(processedCount, targets.Count, target.FullPath, deletedBytes));
                    }
                }

                return new DeleteSummary(deletedCount, deletedBytes, failures, deletedPaths);
            },
            cancellationToken);
    }

    public Task<FileOperationSummary> MoveAsync(
        IReadOnlyList<FileActionTarget> targets,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        string destination = ValidateDestination(destinationFolder);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                HashSet<string> existingNames = GetExistingNames(destination);
                if (collisionBehavior == MoveCollisionBehavior.Cancel && HasCollision(targets, existingNames))
                {
                    throw new OperationCanceledException("A file or folder with the same name already exists in the destination.");
                }

                Directory.CreateDirectory(destination);
                var results = new List<FileOperationResult>(targets.Count);
                long succeededBytes = 0;
                int processedCount = 0;

                foreach (FileActionTarget target in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        if (results.Count > 0)
                        {
                            throw new FileOperationCanceledException(
                                new FileOperationSummary(results.ToArray(), succeededBytes),
                                cancellationToken);
                        }

                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    string? finalPath = null;
                    FileActionFailure? failure = null;
                    try
                    {
                        RevalidateTarget(target);
                        string leafName = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.FullPath));
                        bool collision = existingNames.Contains(leafName);
                        if (collision && collisionBehavior != MoveCollisionBehavior.KeepBoth)
                        {
                            throw new IOException("A file or folder with the same name already exists in the destination.");
                        }

                        finalPath = collision
                            ? GetKeepBothPath(destination, leafName, target.Kind, existingNames)
                            : Path.Combine(destination, leafName);
                        MoveTarget(target, finalPath);
                        existingNames.Add(Path.GetFileName(finalPath));
                        succeededBytes += target.SizeBytes;
                    }
                    catch (Exception ex) when (IsOperationalFailure(ex))
                    {
                        finalPath = null;
                        failure = new FileActionFailure(target.FullPath, ex.Message);
                    }
                    finally
                    {
                        processedCount++;
                        progress?.Report(new FileOperationProgress(
                            processedCount,
                            targets.Count,
                            target.FullPath,
                            succeededBytes));
                    }

                    results.Add(new FileOperationResult(target.FullPath, finalPath, failure));
                }

                return new FileOperationSummary(results, succeededBytes);
            },
            cancellationToken);
    }

    public Task<FileOperationResult> RenameAsync(
        FileActionTarget target,
        string newName,
        CancellationToken cancellationToken)
    {
        ValidateLeafName(newName);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    RevalidateTarget(target);
                    string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(target.FullPath));
                    if (string.IsNullOrWhiteSpace(parent))
                    {
                        throw new IOException("The source path cannot be renamed.");
                    }

                    string destination = Path.Combine(parent, newName);
                    if (GetExistingNames(parent).Contains(newName))
                    {
                        throw new IOException("A file or folder with the same name already exists.");
                    }

                    MoveTarget(target, destination);
                    return new FileOperationResult(target.FullPath, destination, null);
                }
                catch (Exception ex) when (IsOperationalFailure(ex))
                {
                    return new FileOperationResult(
                        target.FullPath,
                        null,
                        new FileActionFailure(target.FullPath, ex.Message));
                }
            },
            cancellationToken);
    }

    public void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }

    private static void DeleteTarget(FileActionTarget target, RecycleOption recycleOption)
    {
        if (target.Kind == FileActionTargetKind.Directory)
        {
            if (recycleOption == RecycleOption.SendToRecycleBin)
            {
                RecycleEmptyDirectory(target.FullPath);
            }
            else
            {
                Directory.Delete(target.FullPath, recursive: false);
            }
        }
        else if (target.Kind == FileActionTargetKind.DirectoryLink)
        {
            if (recycleOption == RecycleOption.SendToRecycleBin)
            {
                RecycleDirectoryEntry(target.FullPath, requireEmpty: false);
            }
            else
            {
                Directory.Delete(target.FullPath, recursive: false);
            }
        }
        else
        {
            FileSystem.DeleteFile(target.FullPath, UIOption.OnlyErrorDialogs, recycleOption);
        }
    }

    private static void MoveTarget(FileActionTarget target, string destinationPath)
    {
        bool sameVolume = string.Equals(
            Path.GetPathRoot(Path.GetFullPath(target.FullPath)),
            Path.GetPathRoot(Path.GetFullPath(destinationPath)),
            StringComparison.OrdinalIgnoreCase);
        if (target.Kind == FileActionTargetKind.Directory)
        {
            if (sameVolume)
            {
                Directory.Move(target.FullPath, destinationPath);
            }
            else
            {
                MoveEmptyDirectoryAcrossVolumes(target.FullPath, destinationPath);
            }
        }
        else if (target.Kind == FileActionTargetKind.DirectoryLink)
        {
            if (!sameVolume)
            {
                throw new IOException("File-system links cannot be moved across volumes safely.");
            }

            Directory.Move(target.FullPath, destinationPath);
        }
        else
        {
            if (target.Kind == FileActionTargetKind.FileLink && !sameVolume)
            {
                throw new IOException("File-system links cannot be moved across volumes safely.");
            }

            File.Move(target.FullPath, destinationPath);
        }
    }

    private static void RevalidateTarget(FileActionTarget target)
    {
        FileAttributes attributes = File.GetAttributes(target.FullPath);
        FileActionTargetKind actualKind = (attributes.HasFlag(FileAttributes.Directory), attributes.HasFlag(FileAttributes.ReparsePoint)) switch
        {
            (false, false) => FileActionTargetKind.File,
            (true, false) => FileActionTargetKind.Directory,
            (false, true) => FileActionTargetKind.FileLink,
            (true, true) => FileActionTargetKind.DirectoryLink,
        };

        if (actualKind != target.Kind)
        {
            throw new IOException("The source no longer matches the scan result.");
        }

        if (target.ExpectedInvalidLinkReason is not null &&
            target.Kind is FileActionTargetKind.FileLink or FileActionTargetKind.DirectoryLink)
        {
            FileSystemInfo source = target.Kind == FileActionTargetKind.DirectoryLink
                ? new DirectoryInfo(target.FullPath)
                : new FileInfo(target.FullPath);
            if (source.LinkTarget is null ||
                !string.Equals(
                    InvalidLinkAnalyzer.GetInvalidReason(source),
                    target.ExpectedInvalidLinkReason,
                    StringComparison.Ordinal))
            {
                throw new IOException("The source no longer matches the scan result.");
            }
        }

        if (target.Kind == FileActionTargetKind.File && new FileInfo(target.FullPath).Length != target.SizeBytes)
        {
            throw new IOException("The source no longer matches the scan result.");
        }

        if (target.Kind == FileActionTargetKind.Directory && Directory.EnumerateFileSystemEntries(target.FullPath).Any())
        {
            throw new IOException("The directory is no longer empty.");
        }
    }

    private static string ValidateDestination(string destinationFolder)
    {
        if (string.IsNullOrWhiteSpace(destinationFolder) || !Path.IsPathFullyQualified(destinationFolder))
        {
            throw new ArgumentException("The destination must be an absolute folder path.", nameof(destinationFolder));
        }

        string destination;
        try
        {
            destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationFolder));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("The destination folder path is invalid.", nameof(destinationFolder), ex);
        }

        string root = Path.GetPathRoot(destination) ?? string.Empty;
        string remainder = destination[root.Length..];
        if (remainder.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(static segment => !IsValidLeafName(segment)))
        {
            throw new ArgumentException("The destination folder path is invalid.", nameof(destinationFolder));
        }

        if (File.Exists(destination))
        {
            throw new ArgumentException("The destination must be a folder.", nameof(destinationFolder));
        }

        if (Directory.Exists(destination) && File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ArgumentException("The destination cannot be a file-system link.", nameof(destinationFolder));
        }

        return destination;
    }

    private static void ValidateLeafName(string newName)
    {
        if (!IsValidLeafName(newName))
        {
            throw new ArgumentException("The new name is not a valid Windows file or folder name.", nameof(newName));
        }
    }

    private static bool IsValidLeafName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            name is "." or ".." ||
            name.EndsWith(' ') ||
            name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            name.Contains(Path.DirectorySeparatorChar) ||
            name.Contains(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        string stem = Path.GetFileNameWithoutExtension(name).TrimEnd(' ', '.');
        return !ReservedNames.Contains(stem);
    }

    private static void RecycleEmptyDirectory(string path) =>
        RecycleDirectoryEntry(path, requireEmpty: true);

    private static void RecycleDirectoryEntry(string path, bool requireEmpty)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                RecycleDirectoryEntryOnSta(path, requireEmpty);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "Duplicates Recycle Bin operation",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    private static void RecycleDirectoryEntryOnSta(string path, bool requireEmpty)
    {
        if (requireEmpty && Directory.EnumerateFileSystemEntries(path).Any())
        {
            throw new IOException("The directory is no longer empty.");
        }

        IFileOperation? operation = null;
        IShellItem? item = null;
        try
        {
            operation = (IFileOperation)(object)new FileOperationComObject();
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(RecycleEmptyDirectoryFlags));
            Guid shellItemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(
                path,
                nint.Zero,
                ref shellItemId,
                out item));
            Marshal.ThrowExceptionForHR(operation.DeleteItem(item, nint.Zero));

            int performResult = operation.PerformOperations();
            int abortedResult = operation.GetAnyOperationsAborted(out int wasAborted);
            Marshal.ThrowExceptionForHR(performResult);
            Marshal.ThrowExceptionForHR(abortedResult);
            if (wasAborted != 0 || PathEntryExists(path))
            {
                throw new IOException("The Recycle Bin operation did not delete the empty directory.");
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

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void MoveEmptyDirectoryAcrossVolumes(string sourcePath, string destinationPath)
    {
        if (Directory.EnumerateFileSystemEntries(sourcePath).Any())
        {
            throw new IOException("The directory is no longer empty.");
        }

        DateTime creationTimeUtc = Directory.GetCreationTimeUtc(sourcePath);
        DateTime lastWriteTimeUtc = Directory.GetLastWriteTimeUtc(sourcePath);
        string destinationParent = Path.GetDirectoryName(destinationPath) ??
            throw new IOException("The destination folder is invalid.");
        string stagingPath = Path.Combine(
            destinationParent,
            $".duplicates-{Guid.NewGuid():N}.tmp");
        bool destinationCreated = false;
        try
        {
            Directory.CreateDirectory(stagingPath);
            Directory.SetCreationTimeUtc(stagingPath, creationTimeUtc);
            Directory.SetLastWriteTimeUtc(stagingPath, lastWriteTimeUtc);
            Directory.Move(stagingPath, destinationPath);
            destinationCreated = true;

            FileAttributes destinationAttributes = File.GetAttributes(destinationPath);
            if (destinationAttributes.HasFlag(FileAttributes.ReparsePoint) ||
                !destinationAttributes.HasFlag(FileAttributes.Directory) ||
                Directory.EnumerateFileSystemEntries(destinationPath).Any() ||
                Directory.GetCreationTimeUtc(destinationPath) != creationTimeUtc ||
                Directory.GetLastWriteTimeUtc(destinationPath) != lastWriteTimeUtc)
            {
                throw new IOException("The destination directory could not be verified.");
            }

            Directory.Delete(sourcePath, recursive: false);
            destinationCreated = false;
        }
        catch
        {
            if (destinationCreated &&
                Directory.Exists(destinationPath) &&
                !File.GetAttributes(destinationPath).HasFlag(FileAttributes.ReparsePoint) &&
                !Directory.EnumerateFileSystemEntries(destinationPath).Any())
            {
                Directory.Delete(destinationPath, recursive: false);
            }

            throw;
        }
        finally
        {
            if (Directory.Exists(stagingPath) &&
                !File.GetAttributes(stagingPath).HasFlag(FileAttributes.ReparsePoint) &&
                !Directory.EnumerateFileSystemEntries(stagingPath).Any())
            {
                Directory.Delete(stagingPath, recursive: false);
            }
        }
    }

    private static HashSet<string> GetExistingNames(string destination)
    {
        if (!Directory.Exists(destination))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateFileSystemEntries(destination)
            .Select(static path => Path.GetFileName(Path.TrimEndingDirectorySeparator(path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static bool HasCollision(IReadOnlyList<FileActionTarget> targets, HashSet<string> existingNames)
    {
        var names = new HashSet<string>(existingNames, StringComparer.OrdinalIgnoreCase);
        foreach (FileActionTarget target in targets)
        {
            string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(target.FullPath));
            if (!names.Add(name))
            {
                return true;
            }
        }

        return false;
    }

    private static string GetKeepBothPath(
        string destination,
        string leafName,
        FileActionTargetKind kind,
        HashSet<string> existingNames)
    {
        bool isDirectory = kind is FileActionTargetKind.Directory or FileActionTargetKind.DirectoryLink;
        string extension = isDirectory ? string.Empty : Path.GetExtension(leafName);
        string stem = isDirectory ? leafName : Path.GetFileNameWithoutExtension(leafName);
        for (int suffix = 2; suffix < int.MaxValue; suffix++)
        {
            string candidate = $"{stem} ({suffix}){extension}";
            if (!existingNames.Contains(candidate))
            {
                return Path.Combine(destination, candidate);
            }
        }

        throw new IOException("A free destination name could not be found.");
    }

    private static bool IsOperationalFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException or NotSupportedException;

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
        int RenameItem(IShellItem item, [MarshalAs(UnmanagedType.LPWStr)] string newName, nint progressSink);

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

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        nint bindContext,
        ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem item);
}
