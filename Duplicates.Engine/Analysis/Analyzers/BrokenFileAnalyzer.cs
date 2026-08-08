using System.Diagnostics;
using System.Security;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed class BrokenFileAnalyzer
{
    private readonly IFileFormatProbe _probe;

    public BrokenFileAnalyzer(IFileFormatProbe probe)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
    }

    public async Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var findings = new List<PathFinding>();
        var analyzerSkips = new List<SkippedPath>();

        foreach (InventoryFile file in inventory.Files
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static file => file.FullPath, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.SizeBytes == 0 ||
                file.Attributes.HasFlag(FileAttributes.Directory) ||
                file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            if (!TryReadSnapshot(file.FullPath, out FileSnapshot before) ||
                !MatchesInventory(before, file))
            {
                analyzerSkips.Add(ChangedSkip(file.FullPath));
                continue;
            }

            DetectedFileType? detectedType = null;
            FileProbeResult? probeResult = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                detectedType = await FileSignatureDetector.DetectFileAsync(
                    file.FullPath,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception ex) when (IsHeaderReadFailure(ex))
            {
                cancellationToken.ThrowIfCancellationRequested();
                probeResult = new FileProbeResult(
                    FileProbeStatus.Invalid,
                    "HeaderReadFailure",
                    null);
            }

            if (probeResult is null)
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    probeResult = await _probe.ProbeAsync(
                        file.FullPath,
                        detectedType,
                        cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                catch (Exception ex) when (IsProviderFailure(ex))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    analyzerSkips.Add(new SkippedPath
                    {
                        Path = file.FullPath,
                        Reason = "Could not validate file.",
                    });
                    continue;
                }
            }

            if (!TryReadSnapshot(file.FullPath, out FileSnapshot after) ||
                after != before ||
                !MatchesInventory(after, file))
            {
                analyzerSkips.Add(ChangedSkip(file.FullPath));
                continue;
            }

            if (probeResult.Status == FileProbeStatus.Valid)
            {
                continue;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Validator"] = GetValidator(detectedType),
                ["ErrorType"] = probeResult.ErrorType ?? string.Empty,
            };
            if (detectedType is not null)
            {
                metadata["DetectedType"] = detectedType.Name;
            }

            findings.Add(new PathFinding
            {
                FullPath = file.FullPath,
                Kind = PathFindingKind.File,
                Reason = probeResult.Status == FileProbeStatus.Invalid
                    ? "Unreadable or malformed file."
                    : "Unsupported or protected.",
                SizeBytes = file.SizeBytes,
                CreatedUtc = file.CreatedUtc,
                ModifiedUtc = file.ModifiedUtc,
                Metadata = metadata,
            });
        }

        stopwatch.Stop();
        return new AnalysisResult
        {
            Findings = findings,
            Groups = [],
            SkippedPaths = analyzerSkips.Count == 0
                ? inventory.SkippedPaths
                : [.. inventory.SkippedPaths, .. analyzerSkips],
            Elapsed = stopwatch.Elapsed,
        };
    }

    private static string GetValidator(DetectedFileType? detectedType) => detectedType?.Name switch
    {
        "JPEG" or "PNG" or "GIF" or "BMP" or "TIFF" or "WebP" => "Image",
        "MP3" or "FLAC" or "WAV" or "Ogg" or "MP4" or "QuickTime" or "ISO BMFF" or
            "WebM" or "Matroska" or "EBML" or "AVI" => "Media",
        "ZIP" => "Zip",
        _ => "Header",
    };

    private static bool TryReadSnapshot(string path, out FileSnapshot snapshot)
    {
        try
        {
            var file = new FileInfo(path);
            file.Refresh();
            if (!file.Exists)
            {
                snapshot = default;
                return false;
            }

            snapshot = new FileSnapshot(file.Length, file.LastWriteTimeUtc);
            return true;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            snapshot = default;
            return false;
        }
    }

    private static bool MatchesInventory(FileSnapshot snapshot, InventoryFile file) =>
        snapshot.SizeBytes == file.SizeBytes && snapshot.ModifiedUtc == file.ModifiedUtc;

    private static SkippedPath ChangedSkip(string path) => new()
    {
        Path = path,
        Reason = "File changed since scan.",
    };

    private static bool IsHeaderReadFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;

    private static bool IsProviderFailure(Exception exception) => IsFileSystemFailure(exception);

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;

    private readonly record struct FileSnapshot(long SizeBytes, DateTime ModifiedUtc);
}
