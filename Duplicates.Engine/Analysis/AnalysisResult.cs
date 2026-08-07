using Duplicates.Engine.Models;

namespace Duplicates.Engine.Analysis;

public sealed record AnalysisResult
{
    public required IReadOnlyList<PathFinding> Findings { get; init; }

    public required IReadOnlyList<SimilarityGroup> Groups { get; init; }

    public required IReadOnlyList<SkippedPath> SkippedPaths { get; init; }

    public required TimeSpan Elapsed { get; init; }
}
