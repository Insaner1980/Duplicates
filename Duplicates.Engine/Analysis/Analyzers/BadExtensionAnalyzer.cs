using System.Diagnostics;
using System.Security;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed class BadExtensionAnalyzer
{
    public async Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();
        List<SkippedPath>? appendedSkippedPaths = null;

        foreach (InventoryFile file in inventory.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.Attributes.HasFlag(FileAttributes.Directory) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            DetectedFileType? detected;
            try
            {
                detected = await FileSignatureDetector.DetectFileAsync(
                    file.FullPath,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                appendedSkippedPaths ??= [.. inventory.SkippedPaths];
                appendedSkippedPaths.Add(new SkippedPath { Path = file.FullPath, Reason = ex.Message });
                continue;
            }

            if (detected?.RecommendedExtension is not string recommendation ||
                detected.AllowedExtensions.Any(extension =>
                    string.Equals(extension, file.Extension, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(file.FileName);
            findings.Add(new PathFinding
            {
                FullPath = file.FullPath,
                Kind = PathFindingKind.File,
                Reason = "Extension does not match detected file type.",
                Suggestion = stem + recommendation,
                SizeBytes = file.SizeBytes,
                CreatedUtc = file.CreatedUtc,
                ModifiedUtc = file.ModifiedUtc,
                Metadata = new Dictionary<string, string>
                {
                    ["CurrentExtension"] = string.IsNullOrEmpty(file.Extension) ? "(none)" : file.Extension,
                    ["ProperExtension"] = recommendation,
                    ["DetectedType"] = detected.Name,
                },
            });
        }

        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = findings
                .OrderBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .ToArray(),
            Groups = [],
            SkippedPaths = appendedSkippedPaths ?? inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static bool IsReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;
}
