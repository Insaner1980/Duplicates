namespace Duplicates.Engine.Models;

public sealed record DuplicateGroup
{
    public required ulong ContentHash { get; init; }

    public required long SizeBytes { get; init; }

    public required IReadOnlyList<FileEntry> Files { get; init; }

    public long WastedBytes => SizeBytes * (Files.Count - 1);
}
