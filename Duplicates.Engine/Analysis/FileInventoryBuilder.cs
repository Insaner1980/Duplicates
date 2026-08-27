using System.Diagnostics;
using System.Security;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis;

public sealed class FileInventoryBuilder
{
    public FileInventory Build(
        AnalysisScope scope,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        var files = new List<InventoryFile>();
        var directories = new List<InventoryDirectory>();
        var includedRoots = new List<string>();
        var reparsePointPaths = new List<string>();
        var skippedPaths = new List<SkippedPath>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIncludedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenReparsePoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reporter = new ProgressReporter(progress);
        IReadOnlyList<ExcludedPath> excludedPaths = BuildExcludedPaths(scope.ExcludedPaths, skippedPaths);
        long totalBytes = 0;

        reporter.Report(new AnalysisProgress(AnalysisPhase.Enumerating, 0, 0, 0, 0, null), force: true);

        var pendingDirectories = new Stack<DirectoryWorkItem>();
        foreach (string path in scope.IncludedFolders.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetFullPath(path, skippedPaths, out string fullPath) || IsExcluded(fullPath, excludedPaths))
            {
                continue;
            }

            if (!TryGetAttributes(fullPath, skippedPaths, out FileAttributes attributes))
            {
                continue;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                skippedPaths.Add(new SkippedPath { Path = fullPath, Reason = "Path is not a directory." });
                continue;
            }

            if (seenIncludedRoots.Add(fullPath))
            {
                includedRoots.Add(fullPath);
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                AddReparsePoint(fullPath, reparsePointPaths, seenReparsePoints);
                continue;
            }

            if (ShouldIgnore(attributes, scope))
            {
                continue;
            }

            if (seenDirectories.Add(fullPath))
            {
                int directoryIndex = directories.Count;
                directories.Add(CreateDirectory(fullPath, depth: 0, attributes));
                pendingDirectories.Push(new DirectoryWorkItem(fullPath, 0, directoryIndex));
            }
        }

        while (pendingDirectories.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DirectoryWorkItem currentDirectory = pendingDirectories.Pop();

            string[] paths;
            try
            {
                paths = Directory.GetFileSystemEntries(currentDirectory.FullPath);
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                skippedPaths.Add(new SkippedPath { Path = currentDirectory.FullPath, Reason = ex.Message });
                continue;
            }

            directories[currentDirectory.InventoryIndex] = directories[currentDirectory.InventoryIndex] with
            {
                PhysicalChildCount = paths.Length,
            };

            foreach (string path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (IsExcluded(path, excludedPaths) || !TryGetAttributes(path, skippedPaths, out FileAttributes attributes))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    AddReparsePoint(path, reparsePointPaths, seenReparsePoints);
                    ReportEnumerationProgress(reporter, files, totalBytes, path);
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.Directory))
                {
                    if (scope.IncludeSubfolders && !ShouldIgnore(attributes, scope) && seenDirectories.Add(path))
                    {
                        int depth = currentDirectory.Depth + 1;
                        int directoryIndex = directories.Count;
                        directories.Add(CreateDirectory(path, depth, attributes));
                        pendingDirectories.Push(new DirectoryWorkItem(path, depth, directoryIndex));
                    }
                }
                else if (!ShouldIgnore(attributes, scope))
                {
                    TryAddFile(path, attributes, files, seenFiles, skippedPaths, ref totalBytes);
                }

                ReportEnumerationProgress(reporter, files, totalBytes, path);
            }
        }

        foreach (string path in scope.IncludedFiles.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetFullPath(path, skippedPaths, out string fullPath) || IsExcluded(fullPath, excludedPaths))
            {
                continue;
            }

            if (!TryGetAttributes(fullPath, skippedPaths, out FileAttributes attributes))
            {
                continue;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                AddReparsePoint(fullPath, reparsePointPaths, seenReparsePoints);
            }
            else if (!attributes.HasFlag(FileAttributes.Directory) && !ShouldIgnore(attributes, scope))
            {
                TryAddFile(fullPath, attributes, files, seenFiles, skippedPaths, ref totalBytes);
            }

            ReportEnumerationProgress(reporter, files, totalBytes, fullPath);
        }

        reporter.Report(
            new AnalysisProgress(AnalysisPhase.Done, files.Count, files.Count, totalBytes, totalBytes, null),
            force: true);

        return new FileInventory(files, directories, includedRoots, reparsePointPaths, skippedPaths);
    }

    private static IReadOnlyList<ExcludedPath> BuildExcludedPaths(IReadOnlyList<string> paths, List<SkippedPath> skippedPaths)
    {
        var excludedPaths = new List<ExcludedPath>();

        foreach (string path in paths.Where(static path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                string fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
                if (excludedPaths.Any(existing => string.Equals(existing.FullPath, fullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                FileAttributes attributes = File.GetAttributes(fullPath);
                excludedPaths.Add(new ExcludedPath(fullPath, attributes.HasFlag(FileAttributes.Directory)));
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                skippedPaths.Add(new SkippedPath { Path = path, Reason = ex.Message });
            }
        }

        return excludedPaths;
    }

    private static bool TryGetFullPath(string path, List<SkippedPath> skippedPaths, out string fullPath)
    {
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return true;
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            skippedPaths.Add(new SkippedPath { Path = path, Reason = ex.Message });
            fullPath = string.Empty;
            return false;
        }
    }

    private static bool TryGetAttributes(string path, List<SkippedPath> skippedPaths, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            skippedPaths.Add(new SkippedPath { Path = path, Reason = ex.Message });
            attributes = default;
            return false;
        }
    }

    private static bool TryAddFile(
        string path,
        FileAttributes attributes,
        List<InventoryFile> files,
        HashSet<string> seenFiles,
        List<SkippedPath> skippedPaths,
        ref long totalBytes)
    {
        if (!seenFiles.Add(path))
        {
            return false;
        }

        try
        {
            var file = new FileInfo(path);
            long sizeBytes = file.Length;
            files.Add(new InventoryFile(
                path,
                file.Name,
                file.Extension.ToLowerInvariant(),
                file.DirectoryName ?? string.Empty,
                sizeBytes,
                file.CreationTimeUtc,
                file.LastWriteTimeUtc,
                attributes));
            totalBytes += sizeBytes;
            return true;
        }
        catch (Exception ex) when (IsSkippable(ex))
        {
            seenFiles.Remove(path);
            skippedPaths.Add(new SkippedPath { Path = path, Reason = ex.Message });
            return false;
        }
    }

    private static InventoryDirectory CreateDirectory(string fullPath, int depth, FileAttributes attributes)
    {
        var directory = new DirectoryInfo(fullPath);
        return new InventoryDirectory(
            fullPath,
            directory.Name,
            directory.Parent?.FullName ?? string.Empty,
            depth,
            -1,
            attributes);
    }

    private static bool IsExcluded(string path, IReadOnlyList<ExcludedPath> excludedPaths)
    {
        foreach (ExcludedPath excludedPath in excludedPaths)
        {
            if (string.Equals(path, excludedPath.FullPath, StringComparison.OrdinalIgnoreCase) ||
                (excludedPath.IsDirectory && IsDescendantOf(path, excludedPath.FullPath)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDescendantOf(string path, string ancestor)
    {
        if (!path.StartsWith(ancestor, StringComparison.OrdinalIgnoreCase) || path.Length <= ancestor.Length)
        {
            return false;
        }

        if (string.Equals(Path.GetPathRoot(ancestor), ancestor, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        char separator = path[ancestor.Length];
        return separator == Path.DirectorySeparatorChar || separator == Path.AltDirectorySeparatorChar;
    }

    private static bool ShouldIgnore(FileAttributes attributes, AnalysisScope scope)
    {
        return (scope.IgnoreHiddenFiles && attributes.HasFlag(FileAttributes.Hidden)) ||
               (scope.IgnoreSystemFiles && attributes.HasFlag(FileAttributes.System));
    }

    private static void AddReparsePoint(string path, List<string> reparsePointPaths, HashSet<string> seenReparsePoints)
    {
        if (seenReparsePoints.Add(path))
        {
            reparsePointPaths.Add(path);
        }
    }

    private static void ReportEnumerationProgress(ProgressReporter reporter, List<InventoryFile> files, long totalBytes, string path)
    {
        reporter.Report(new AnalysisProgress(AnalysisPhase.Enumerating, files.Count, 0, 0, totalBytes, path));
    }

    private static bool IsSkippable(Exception exception)
    {
        return exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException or SecurityException;
    }

    private sealed record ExcludedPath(string FullPath, bool IsDirectory);

    private sealed record DirectoryWorkItem(string FullPath, int Depth, int InventoryIndex);

    private sealed class ProgressReporter
    {
        private static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(100);

        private readonly IProgress<AnalysisProgress>? _inner;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastReport = TimeSpan.MinValue;

        public ProgressReporter(IProgress<AnalysisProgress>? inner)
        {
            _inner = inner;
        }

        public void Report(AnalysisProgress progress, bool force = false)
        {
            if (_inner is null)
            {
                return;
            }

            TimeSpan elapsed = _stopwatch.Elapsed;
            if (!force && _lastReport != TimeSpan.MinValue && elapsed - _lastReport < MinimumInterval)
            {
                return;
            }

            _lastReport = elapsed;
            _inner.Report(progress);
        }
    }
}
