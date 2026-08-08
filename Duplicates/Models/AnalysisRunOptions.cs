namespace Duplicates.Models;

public sealed record AnalysisRunOptions(
    int MaxMediaConcurrency,
    bool UseMediaFingerprintCache);
