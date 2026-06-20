namespace Duplicates.Engine.Models;

public sealed record FileEntry
{
    public required string FullPath { get; init; }

    public required string FileName { get; init; }

    public required string Extension { get; init; }

    public required string DirectoryPath { get; init; }

    public required long SizeBytes { get; init; }

    public required DateTime CreatedUtc { get; init; }

    public required DateTime ModifiedUtc { get; init; }

    public ulong? ContentHash { get; set; }
}
