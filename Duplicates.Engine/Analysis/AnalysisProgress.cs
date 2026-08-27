namespace Duplicates.Engine.Analysis;

public sealed record AnalysisProgress(
    AnalysisPhase Phase,
    long ItemsDiscovered,
    long ItemsProcessed,
    long BytesProcessed,
    long TotalBytes,
    string? CurrentPath);
