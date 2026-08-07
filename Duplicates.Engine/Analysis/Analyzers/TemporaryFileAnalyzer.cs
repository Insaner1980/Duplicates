using System.Diagnostics;
using System.Globalization;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record TemporaryFileOptions(TimeSpan MinimumAge, DateTime UtcNow);

public sealed class TemporaryFileAnalyzer
{
    private static readonly string[] ApprovedSuffixes =
    [
        ".tmp",
        ".temp",
        ".partial",
        ".part",
        ".crdownload",
        ".download",
        ".dmp",
        ".chk",
    ];

    public Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        TemporaryFileOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(options);
        if (options.MinimumAge < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();
        DateTime oldestAllowed = options.UtcNow - options.MinimumAge;
        string reason = string.Format(
            CultureInfo.InvariantCulture,
            "Temporary file at least {0:N0} days old",
            options.MinimumAge.TotalDays);

        foreach (InventoryFile file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!MatchesName(file.FileName) ||
                file.ModifiedUtc > oldestAllowed ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            findings.Add(new PathFinding
            {
                FullPath = file.FullPath,
                Kind = PathFindingKind.File,
                Reason = reason,
                SizeBytes = file.SizeBytes,
                ModifiedUtc = file.ModifiedUtc,
                Metadata = new Dictionary<string, string>
                {
                    ["Extension"] = file.Extension,
                },
            });
        }

        stopwatch.Stop();
        return Task.FromResult(new AnalysisResult
        {
            Findings = findings
                .OrderBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .ToArray(),
            Groups = [],
            SkippedPaths = inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        });
    }

    private static bool MatchesName(string fileName) =>
        fileName.StartsWith("~$", StringComparison.OrdinalIgnoreCase) ||
        fileName.EndsWith('~') ||
        ApprovedSuffixes.Any(suffix => fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
}
