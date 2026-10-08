using System.Diagnostics;
using System.Security;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis;

public static class FileInventoryBuilder
{
    public static FileInventory Build(
        AnalysisScope scope,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        cancellationToken.ThrowIfCancellationRequested();

        return new InventoryBuild(scope, progress, cancellationToken).Collect();
    }

    private sealed class InventoryBuild
    {
        private readonly AnalysisScope _scope;
        private readonly CancellationToken _cancellationToken;
        private readonly ProgressReporter _reporter;
        private readonly List<ExcludedPath> _excludedPaths;
        private readonly List<InventoryFile> _files = [];
        private readonly List<InventoryDirectory> _directories = [];
        private readonly List<string> _includedRoots = [];
        private readonly List<string> _reparsePointPaths = [];
        private readonly List<SkippedPath> _skippedPaths = [];
        private readonly HashSet<string> _seenFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenDirectories = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenIncludedRoots = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _seenReparsePoints = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<DirectoryWorkItem> _pendingDirectories = new();
        private long _totalBytes;

        public InventoryBuild(AnalysisScope scope, IProgress<AnalysisProgress>? progress, CancellationToken cancellationToken)
        {
            _scope = scope;
            _cancellationToken = cancellationToken;
            _reporter = new ProgressReporter(progress);
            _excludedPaths = BuildExcludedPaths(scope.ExcludedPaths, _skippedPaths);
        }

        public FileInventory Collect()
        {
            _reporter.Report(new AnalysisProgress(AnalysisPhase.Enumerating, 0, 0, 0, 0, null), force: true);
            AddIncludedFolders();
            WalkDirectories();
            AddIncludedFiles();
            _reporter.Report(
                new AnalysisProgress(AnalysisPhase.Done, _files.Count, _files.Count, _totalBytes, _totalBytes, null),
                force: true);

            return new FileInventory(_files, _directories, _includedRoots, _reparsePointPaths, _skippedPaths);
        }

        private void AddIncludedFolders()
        {
            foreach (string path in _scope.IncludedFolders.Where(static path => !string.IsNullOrWhiteSpace(path)))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetIncludedFolder(path, out string fullPath, out FileAttributes attributes))
                {
                    continue;
                }

                if (_seenIncludedRoots.Add(fullPath))
                {
                    _includedRoots.Add(fullPath);
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    AddReparsePoint(fullPath, _reparsePointPaths, _seenReparsePoints);
                    continue;
                }

                if (ShouldIgnore(attributes, _scope))
                {
                    continue;
                }

                if (_seenDirectories.Add(fullPath))
                {
                    int directoryIndex = _directories.Count;
                    _directories.Add(CreateDirectory(fullPath, depth: 0, attributes));
                    _pendingDirectories.Push(new DirectoryWorkItem(fullPath, 0, directoryIndex));
                }
            }
        }

        private bool TryGetIncludedFolder(string path, out string fullPath, out FileAttributes attributes)
        {
            attributes = default;
            if (!TryGetFullPath(path, _skippedPaths, out fullPath) || IsExcluded(fullPath, _excludedPaths))
            {
                return false;
            }

            if (!TryGetAttributes(fullPath, _skippedPaths, out attributes))
            {
                return false;
            }

            if (!attributes.HasFlag(FileAttributes.Directory))
            {
                _skippedPaths.Add(new SkippedPath { Path = fullPath, Reason = "Path is not a directory." });
                return false;
            }

            return true;
        }

        private void WalkDirectories()
        {
            while (_pendingDirectories.Count > 0)
            {
                _cancellationToken.ThrowIfCancellationRequested();
                DirectoryWorkItem currentDirectory = _pendingDirectories.Pop();

                string[] paths;
                try
                {
                    paths = Directory.GetFileSystemEntries(currentDirectory.FullPath);
                }
                catch (Exception ex) when (IsSkippable(ex))
                {
                    _skippedPaths.Add(new SkippedPath { Path = currentDirectory.FullPath, Reason = ex.Message });
                    continue;
                }

                _directories[currentDirectory.InventoryIndex] = _directories[currentDirectory.InventoryIndex] with
                {
                    PhysicalChildCount = paths.Length
                };

                foreach (string path in paths)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    AddDirectoryEntry(path, currentDirectory);
                }
            }
        }

        private void AddDirectoryEntry(string path, DirectoryWorkItem currentDirectory)
        {
            if (IsExcluded(path, _excludedPaths) || !TryGetAttributes(path, _skippedPaths, out FileAttributes attributes))
            {
                return;
            }

            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                AddReparsePoint(path, _reparsePointPaths, _seenReparsePoints);
                ReportEnumerationProgress(_reporter, _files, _totalBytes, path);
                return;
            }

            if (attributes.HasFlag(FileAttributes.Directory))
            {
                if (_scope.IncludeSubfolders && !ShouldIgnore(attributes, _scope) && _seenDirectories.Add(path))
                {
                    int depth = currentDirectory.Depth + 1;
                    int directoryIndex = _directories.Count;
                    _directories.Add(CreateDirectory(path, depth, attributes));
                    _pendingDirectories.Push(new DirectoryWorkItem(path, depth, directoryIndex));
                }
            }
            else if (!ShouldIgnore(attributes, _scope))
            {
                TryAddFile(path, attributes, _files, _seenFiles, _skippedPaths, ref _totalBytes);
            }

            ReportEnumerationProgress(_reporter, _files, _totalBytes, path);
        }

        private void AddIncludedFiles()
        {
            foreach (string path in _scope.IncludedFiles.Where(static path => !string.IsNullOrWhiteSpace(path)))
            {
                _cancellationToken.ThrowIfCancellationRequested();

                if (!TryGetFullPath(path, _skippedPaths, out string fullPath) || IsExcluded(fullPath, _excludedPaths))
                {
                    continue;
                }

                if (!TryGetAttributes(fullPath, _skippedPaths, out FileAttributes attributes))
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    AddReparsePoint(fullPath, _reparsePointPaths, _seenReparsePoints);
                }
                else if (!attributes.HasFlag(FileAttributes.Directory) && !ShouldIgnore(attributes, _scope))
                {
                    TryAddFile(fullPath, attributes, _files, _seenFiles, _skippedPaths, ref _totalBytes);
                }

                ReportEnumerationProgress(_reporter, _files, _totalBytes, fullPath);
            }
        }

        private static List<ExcludedPath> BuildExcludedPaths(IReadOnlyList<string> paths, List<SkippedPath> skippedPaths)
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

        private static void TryAddFile(
            string path,
            FileAttributes attributes,
            List<InventoryFile> files,
            HashSet<string> seenFiles,
            List<SkippedPath> skippedPaths,
            ref long totalBytes)
        {
            if (!seenFiles.Add(path))
            {
                return;
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
            }
            catch (Exception ex) when (IsSkippable(ex))
            {
                seenFiles.Remove(path);
                skippedPaths.Add(new SkippedPath { Path = path, Reason = ex.Message });
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
}
