namespace Duplicates.Models;

public interface IToolOptions
{
}

public sealed record NoToolOptions : IToolOptions;

public sealed record LargeFileToolOptions(long MinimumSizeBytes) : IToolOptions;

public sealed record TemporaryFileToolOptions(TimeSpan MinimumAge, DateTime UtcNow) : IToolOptions;

public sealed record SimilarImageToolOptions(int MaximumHammingDistance) : IToolOptions;

public sealed record SimilarVideoToolOptions(int MaximumMeanFrameDistance) : IToolOptions;

public sealed record MusicDuplicateToolOptions(TimeSpan MaximumDurationDifference) : IToolOptions;
