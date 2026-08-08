using System.Diagnostics;
using System.Security;

namespace Duplicates.Engine.Analysis.Analyzers;

public sealed record LinkFinding(
    string FullPath,
    string? ImmediateTarget,
    string Reason,
    bool IsDirectoryLink);

public sealed class InvalidLinkAnalyzer
{
    public Task<AnalysisResult> AnalyzeAsync(
        FileInventory inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var linkFindings = new List<LinkFinding>();

        foreach (string path in inventory.ReparsePointPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LinkFinding? finding = Inspect(path);
            if (finding is not null)
            {
                linkFindings.Add(finding);
            }
        }

        stopwatch.Stop();
        return Task.FromResult(new AnalysisResult
        {
            Findings = linkFindings
                .OrderBy(static finding => finding.FullPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static finding => finding.FullPath, StringComparer.Ordinal)
                .Select(static finding => new PathFinding
                {
                    FullPath = finding.FullPath,
                    Kind = PathFindingKind.Link,
                    Reason = finding.Reason,
                    SizeBytes = 0,
                    Metadata = new Dictionary<string, string>
                    {
                        ["LinkKind"] = finding.IsDirectoryLink ? "Directory" : "File",
                        ["ImmediateTarget"] = finding.ImmediateTarget!,
                    },
                })
                .ToArray(),
            Groups = [],
            SkippedPaths = inventory.SkippedPaths,
            Elapsed = stopwatch.Elapsed,
        });
    }

    private static LinkFinding? Inspect(string path)
    {
        FileAttributes attributes = File.GetAttributes(path);
        bool isDirectoryLink = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo source = isDirectoryLink ? new DirectoryInfo(path) : new FileInfo(path);
        string? immediateTarget = source.LinkTarget;
        if (immediateTarget is null)
        {
            return null;
        }

        string? reason = GetInvalidReason(source);
        return reason is null
            ? null
            : new LinkFinding(path, immediateTarget, reason, isDirectoryLink);
    }

    private static string? GetInvalidReason(FileSystemInfo source)
    {
        FileSystemInfo? finalTarget;
        try
        {
            finalTarget = source.ResolveLinkTarget(returnFinalTarget: true);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "Link target is missing.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return "Link target is inaccessible.";
        }
        catch (Exception ex) when (IsUnresolvable(ex))
        {
            return "Link target cannot be resolved.";
        }

        if (finalTarget is null)
        {
            return "Link target cannot be resolved.";
        }

        if (finalTarget.Exists)
        {
            return null;
        }

        try
        {
            _ = File.GetAttributes(finalTarget.FullName);
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return "Link target is missing.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
        {
            return "Link target is inaccessible.";
        }
        catch (Exception ex) when (IsUnresolvable(ex))
        {
            return "Link target cannot be resolved.";
        }
    }

    private static bool IsUnresolvable(Exception exception) =>
        exception is IOException or ArgumentException or NotSupportedException;
}
