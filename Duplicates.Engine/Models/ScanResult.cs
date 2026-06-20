namespace Duplicates.Engine.Models;

public sealed record ScanResult
{
    public required IReadOnlyList<DuplicateGroup> Groups { get; init; }

    public required long TotalFilesScanned { get; init; }

    public required long TotalDuplicateFiles { get; init; }

    public required long TotalReclaimableBytes { get; init; }

    public required TimeSpan Elapsed { get; init; }

    public required IReadOnlyList<SkippedPath> SkippedPaths { get; init; }
}
