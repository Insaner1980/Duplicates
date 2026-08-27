namespace Duplicates.Engine.Analysis;

public enum PathFindingKind
{
    File,
    Directory,
    Link,
}

public sealed record PathFinding
{
    public required string FullPath { get; init; }

    public required PathFindingKind Kind { get; init; }

    public required string Reason { get; init; }

    public string? Suggestion { get; init; }

    public long? SizeBytes { get; init; }

    public DateTime? CreatedUtc { get; init; }

    public DateTime? ModifiedUtc { get; init; }

    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
}
