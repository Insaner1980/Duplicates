namespace Duplicates.Engine.Models;

public sealed record SkippedPath
{
    public required string Path { get; init; }

    public required string Reason { get; init; }
}
