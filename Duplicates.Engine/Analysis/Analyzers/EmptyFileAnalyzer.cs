using System.Diagnostics;

namespace Duplicates.Engine.Analysis.Analyzers;

public static class EmptyFileAnalyzer
{
    public static Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();

        foreach (InventoryFile file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.SizeBytes != 0 || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            findings.Add(new PathFinding
            {
                FullPath = file.FullPath,
                Kind = PathFindingKind.File,
                Reason = "File is empty",
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
}
