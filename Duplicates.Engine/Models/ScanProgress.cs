namespace Duplicates.Engine.Models;

public sealed record ScanProgress
{
    public required ScanPhase Phase { get; init; }

    public long FilesDiscovered { get; init; }

    public long FilesProcessed { get; init; }

    public long BytesProcessed { get; init; }

    public long TotalBytesToProcess { get; init; }

    public string? CurrentFilePath { get; init; }
}
