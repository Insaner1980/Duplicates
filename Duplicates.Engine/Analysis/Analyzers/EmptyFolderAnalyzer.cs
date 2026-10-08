using System.Diagnostics;
using System.Globalization;

namespace Duplicates.Engine.Analysis.Analyzers;

public static class EmptyFolderAnalyzer
{
    public static Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();

        foreach (InventoryDirectory directory in inventory.Directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.PhysicalChildCount != 0 ||
                directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
                inventory.IncludedRootPaths.Contains(directory.FullPath, StringComparer.OrdinalIgnoreCase) ||
                inventory.ReparsePointPaths.Contains(directory.FullPath, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            findings.Add(new PathFinding
            {
                FullPath = directory.FullPath,
                Kind = PathFindingKind.Directory,
                Reason = "Folder is empty",
                SizeBytes = 0,
                Metadata = new Dictionary<string, string>
                {
                    ["Depth"] = directory.Depth.ToString(CultureInfo.InvariantCulture),
                },
            });
        }

        stopwatch.Stop();
        return Task.FromResult(new AnalysisResult
        {
            Findings = findings
                .OrderByDescending(static finding => int.Parse(finding.Metadata["Depth"], CultureInfo.InvariantCulture))
                .ThenBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .ToArray(),
            Groups = [],
            SkippedPaths = inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        });
    }
}
