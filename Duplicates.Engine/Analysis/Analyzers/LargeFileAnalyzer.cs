using System.Diagnostics;
using System.Globalization;

namespace Duplicates.Engine.Analysis.Analyzers;

public static class LargeFileAnalyzer
{
    public static Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        long minimumSizeBytes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentOutOfRangeException.ThrowIfNegative(minimumSizeBytes);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();
        string reason = string.Format(
            CultureInfo.InvariantCulture,
            "At least {0:N0} bytes",
            minimumSizeBytes);

        foreach (InventoryFile file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.SizeBytes < minimumSizeBytes || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
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
                .OrderByDescending(static finding => finding.SizeBytes)
                .ThenBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .ToArray(),
            Groups = [],
            SkippedPaths = inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        });
    }
}
