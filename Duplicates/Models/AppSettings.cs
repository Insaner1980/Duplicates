namespace Duplicates.Models;

public sealed record AppSettings
{
    public AppThemeMode ThemeMode { get; init; } = AppThemeMode.System;

    public BackdropMode BackdropMode { get; init; } = BackdropMode.MicaAlt;

    public long DefaultMinSizeBytes { get; init; } = 1;

    public bool VerifyByteByByte { get; init; } = true;

    public bool IgnoreHiddenFiles { get; init; } = true;

    public bool IgnoreSystemFiles { get; init; } = true;

    public bool DefaultIncludeSubfolders { get; init; } = true;

    public long DefaultLargeFileMinimumBytes { get; init; } = 1_073_741_824;

    public int DefaultTemporaryFileMinimumAgeDays { get; init; } = 7;

    public SimilarityPreset DefaultImageSimilarity { get; init; } = SimilarityPreset.Balanced;

    public SimilarityPreset DefaultVideoSimilarity { get; init; } = SimilarityPreset.Balanced;

    public int? MaxMediaConcurrency { get; init; }

    public bool UseMediaFingerprintCache { get; init; } = true;

    public int? MaxHashingConcurrency { get; init; }

    public DeletionMode DeletionMode { get; init; } = DeletionMode.RecycleBin;

    public bool ConfirmBeforeDelete { get; init; } = true;
}
