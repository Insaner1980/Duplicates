namespace Duplicates.Services;

public sealed record ExifCleanOptions(
    bool RemoveGps,
    bool RemoveDeviceIdentifiers,
    bool RemoveDates,
    bool RemoveAuthorAndDescription,
    bool RemoveEmbeddedThumbnail,
    bool RemoveXmpAndIptc,
    bool ReplaceOriginal);

public sealed record ExifCleanRequest(
    string SourcePath,
    long ExpectedLength,
    DateTime ExpectedModifiedUtc,
    ExifCleanOptions Options);

public enum ExifCleanOutcome
{
    Succeeded,
    SourceChanged,
    UnsupportedFormat,
    UnsupportedMetadataLayout,
    DestinationCollision,
    VerificationFailed,
    RecoveryRequired,
    Failed,
}

public sealed record ExifCleanResult(
    ExifCleanOutcome Outcome,
    string SourcePath,
    string? OutputPath,
    string Detail,
    IReadOnlyList<string> RecoveryPaths);

public interface IExifCleanerService
{
    Task<ExifCleanResult> CleanAsync(
        ExifCleanRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken);
}
