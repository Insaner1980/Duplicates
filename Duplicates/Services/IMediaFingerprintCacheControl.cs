namespace Duplicates.Services;

public sealed record MediaFingerprintCacheStatus(
    int EntryCount,
    long SizeBytes,
    string Path);

public interface IMediaFingerprintCacheControl
{
    Task<MediaFingerprintCacheStatus> GetStatusAsync(CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}
