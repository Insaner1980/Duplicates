using System.Runtime.InteropServices;
using System.Security;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

internal static class SimilarityAnalyzerSupport
{
    internal static async Task<(TCandidate[] Candidates, IReadOnlyList<SkippedPath> SkippedPaths)> AnalyzeFilesAsync<TCandidate>(
        FileInventory inventory,
        FileTypeFilter filter,
        int maximumConcurrency,
        Func<InventoryFile, CancellationToken, ValueTask<FileAnalysisOutcome<TCandidate>>> analyzeFile,
        CancellationToken cancellationToken)
        where TCandidate : class
    {
        InventoryFile[] files = inventory.Files
            .Where(file =>
                !file.Attributes.HasFlag(FileAttributes.Directory) &&
                !file.Attributes.HasFlag(FileAttributes.ReparsePoint) &&
                filter.Matches(file.Extension))
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.Ordinal)
            .ToArray();
        var outcomes = new FileAnalysisOutcome<TCandidate>[files.Length];
        await Parallel.ForEachAsync(
            Enumerable.Range(0, files.Length),
            new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = maximumConcurrency,
                TaskScheduler = TaskScheduler.Default,
            },
            async (index, token) =>
            {
                outcomes[index] = await analyzeFile(files[index], token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        TCandidate[] candidates = outcomes
            .Where(static outcome => outcome.Candidate is not null)
            .Select(static outcome => outcome.Candidate!)
            .ToArray();
        SkippedPath[] analyzerSkips = outcomes
            .Where(static outcome => outcome.Skip is not null)
            .Select(static outcome => outcome.Skip!)
            .ToArray();
        return (
            candidates,
            analyzerSkips.Length == 0
                ? inventory.SkippedPaths
                : [.. inventory.SkippedPaths, .. analyzerSkips]);
    }

    internal static bool TryReadSnapshot(string path, out FileSnapshot snapshot)
    {
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists ||
                attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                snapshot = default;
                return false;
            }

            snapshot = new FileSnapshot(file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (IsExpectedProviderFailure(ex))
        {
            snapshot = default;
            return false;
        }
    }

    internal static bool MatchesInventory(FileSnapshot snapshot, InventoryFile file) =>
        snapshot.SizeBytes == file.SizeBytes && snapshot.ModifiedUtc.Ticks == file.ModifiedUtc.Ticks;

    internal static bool IsExpectedProviderFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or ArgumentException or
            NotSupportedException or InvalidDataException or OverflowException or COMException;

    internal static SkippedPath ChangedSkip(string path) => new()
    {
        Path = path,
        Reason = "File changed since scan.",
    };

    internal readonly record struct FileAnalysisOutcome<TCandidate>(TCandidate? Candidate, SkippedPath? Skip)
        where TCandidate : class;

    internal readonly record struct FileSnapshot(long SizeBytes, DateTime ModifiedUtc);

    internal sealed class DisjointSet
    {
        internal DisjointSet(int count)
        {
            _parents = Enumerable.Range(0, count).ToArray();
        }

        private readonly int[] _parents;

        internal int Find(int item)
        {
            while (_parents[item] != item)
            {
                _parents[item] = _parents[_parents[item]];
                item = _parents[item];
            }

            return item;
        }

        internal void Union(int left, int right)
        {
            int leftRoot = Find(left);
            int rightRoot = Find(right);
            if (leftRoot != rightRoot)
            {
                _parents[Math.Max(leftRoot, rightRoot)] = Math.Min(leftRoot, rightRoot);
            }
        }
    }
}
