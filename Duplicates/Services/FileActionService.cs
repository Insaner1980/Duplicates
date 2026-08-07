using System.Diagnostics;
using System.Security;
using Duplicates.Models;
using Microsoft.VisualBasic.FileIO;

namespace Duplicates.Services;

public sealed class FileActionService : IFileActionService
{
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
                RecycleOption recycleOption = _settingsService.Current.DeletionMode == DeletionMode.RecycleBin
                    ? RecycleOption.SendToRecycleBin
                    : RecycleOption.DeletePermanently;

                foreach (FileActionTarget target in targets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        RevalidateTarget(target);
                        DeleteTarget(target, recycleOption);
                        deletedCount++;
                        deletedBytes += target.SizeBytes;
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

                return new DeleteSummary(deletedCount, deletedBytes, failures);
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
                    cancellationToken.ThrowIfCancellationRequested();
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
        if (target.Kind is FileActionTargetKind.Directory or FileActionTargetKind.DirectoryLink)
        {
            FileSystem.DeleteDirectory(target.FullPath, UIOption.OnlyErrorDialogs, recycleOption);
        }
        else
        {
            FileSystem.DeleteFile(target.FullPath, UIOption.OnlyErrorDialogs, recycleOption);
        }
    }

    private static void MoveTarget(FileActionTarget target, string destinationPath)
    {
        if (target.Kind is FileActionTargetKind.Directory or FileActionTargetKind.DirectoryLink)
        {
            Directory.Move(target.FullPath, destinationPath);
        }
        else
        {
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

        if (target.Kind == FileActionTargetKind.File && new FileInfo(target.FullPath).Length != target.SizeBytes)
        {
            throw new IOException("The source no longer matches the scan result.");
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

        string stem = Path.GetFileNameWithoutExtension(name);
        return !ReservedNames.Contains(stem);
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
}
