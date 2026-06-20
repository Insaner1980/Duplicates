namespace Duplicates.Engine.Models;

public sealed record ScanOptions
{
    private static readonly HashSet<int> AllowedConcurrencyOverrides = [1, 2, 4, 8];

    public IReadOnlyList<string> Folders { get; init; } = [];

    public bool IncludeSubfolders { get; init; } = true;

    public long MinSizeBytes { get; init; } = 1;

    public long MaxSizeBytes { get; init; } = long.MaxValue;

    public FileTypeFilter TypeFilter { get; init; } = FileTypeFilter.All;

    public bool IgnoreHiddenFiles { get; init; } = true;

    public bool IgnoreSystemFiles { get; init; } = true;

    public bool FollowSymlinks { get; init; }

    public bool VerifyByteByByte { get; init; } = true;

    public int? MaxHashingConcurrency { get; init; }

    public int GetEffectiveMaxHashingConcurrency()
    {
        if (MaxHashingConcurrency is null)
        {
            return Math.Max(1, Environment.ProcessorCount);
        }

        if (!AllowedConcurrencyOverrides.Contains(MaxHashingConcurrency.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(MaxHashingConcurrency), MaxHashingConcurrency, "Allowed values are Auto, 1, 2, 4, or 8.");
        }

        return MaxHashingConcurrency.Value;
    }
}
