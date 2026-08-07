namespace Duplicates.Engine.Analysis;

public sealed record SimilarityGroup
{
    public required string Id { get; init; }

    public required SimilarityItem ReferenceItem { get; init; }

    public required IReadOnlyList<SimilarityItem> Items { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
