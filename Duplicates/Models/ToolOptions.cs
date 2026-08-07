namespace Duplicates.Models;

public abstract record ToolOptions;

public sealed record NoToolOptions : ToolOptions;

public sealed record LargeFileToolOptions(long MinimumSizeBytes) : ToolOptions;

public sealed record TemporaryFileToolOptions(TimeSpan MinimumAge, DateTime UtcNow) : ToolOptions;

public sealed record SimilarImageToolOptions(int MaximumHammingDistance) : ToolOptions;

public sealed record SimilarVideoToolOptions(int MaximumMeanFrameDistance) : ToolOptions;

public sealed record MusicDuplicateToolOptions(TimeSpan MaximumDurationDifference) : ToolOptions;
