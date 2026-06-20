using Duplicates.Engine.Models;

namespace Duplicates.Engine.FileEnumeration;

internal sealed class FileWalker
{
    public FileWalkResult Walk(ScanOptions options, ScanProgressReporter progress, CancellationToken cancellationToken)
    {
        var files = new List<FileEntry>();
        var skipped = new List<SkippedPath>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string folder in options.Folders.Where(static folder => !string.IsNullOrWhiteSpace(folder)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string root;
            try
            {
                root = Path.GetFullPath(folder);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                skipped.Add(new SkippedPath { Path = folder, Reason = ex.Message });
                continue;
            }

            if (!Directory.Exists(root))
            {
                skipped.Add(new SkippedPath { Path = root, Reason = "Folder does not exist." });
                continue;
            }

            var enumerationOptions = new EnumerationOptions
            {
                RecurseSubdirectories = options.IncludeSubfolders,
                IgnoreInaccessible = true,
                AttributesToSkip = BuildAttributesToSkip(options),
            };

            IEnumerable<string> paths;
            try
            {
                paths = Directory.EnumerateFiles(root, "*", enumerationOptions);
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                skipped.Add(new SkippedPath { Path = root, Reason = ex.Message });
                continue;
            }

            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TryAddFile(path, options, files, skipped, seenPaths);

                progress.Report(new ScanProgress
                {
                    Phase = ScanPhase.Enumerating,
                    FilesDiscovered = files.Count,
                    CurrentFilePath = path,
                });
            }
        }

        return new FileWalkResult(files, skipped);
    }

    private static FileAttributes BuildAttributesToSkip(ScanOptions options)
    {
        FileAttributes attributes = FileAttributes.None;

        if (options.IgnoreHiddenFiles)
        {
            attributes |= FileAttributes.Hidden;
        }

        if (options.IgnoreSystemFiles)
        {
            attributes |= FileAttributes.System;
        }

        if (!options.FollowSymlinks)
        {
            attributes |= FileAttributes.ReparsePoint;
        }

        return attributes;
    }

    private static void TryAddFile(
        string path,
        ScanOptions options,
        List<FileEntry> files,
        List<SkippedPath> skipped,
        HashSet<string> seenPaths)
    {
        try
        {
            string fullPath = Path.GetFullPath(path);
            if (!seenPaths.Add(fullPath))
            {
                return;
            }

            var info = new FileInfo(fullPath);
            FileAttributes attributes = info.Attributes;

            if (!options.FollowSymlinks && attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                return;
            }

            if (options.IgnoreHiddenFiles && attributes.HasFlag(FileAttributes.Hidden))
            {
                return;
            }

            if (options.IgnoreSystemFiles && attributes.HasFlag(FileAttributes.System))
            {
                return;
            }

            if (info.Length < options.MinSizeBytes || info.Length > options.MaxSizeBytes)
            {
                return;
            }

            string extension = info.Extension.ToLowerInvariant();
            if (!options.TypeFilter.Matches(extension))
            {
                return;
            }

            files.Add(new FileEntry
            {
                FullPath = fullPath,
                FileName = info.Name,
                Extension = extension,
                DirectoryPath = info.DirectoryName ?? string.Empty,
                SizeBytes = info.Length,
                CreatedUtc = info.CreationTimeUtc,
                ModifiedUtc = info.LastWriteTimeUtc,
            });
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            skipped.Add(new SkippedPath { Path = path, Reason = ex.Message });
        }
    }

    private static bool IsSkippable(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException;
    }
}
