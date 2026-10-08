namespace Duplicates.Engine.Analysis;

public sealed record SimilarityItem
{
    public required string FullPath { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime ModifiedUtc { get; init; }

    public required double SimilarityPercent { get; init; }

    public required ISimilarityEvidence Evidence { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
