using Duplicates.Engine.Analysis.Media;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Duplicates.Services;

public sealed class WindowsMusicMetadataProvider : IMusicMetadataProvider
{
    public async Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();
        StorageFile file = await WinRtAsync.AwaitAndCloseAsync(
                StorageFile.GetFileFromPathAsync(path),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        StorageItemContentProperties contentProperties = file.Properties;
        cancellationToken.ThrowIfCancellationRequested();
        MusicProperties properties = await WinRtAsync.AwaitAndCloseAsync(
                contentProperties.GetMusicPropertiesAsync(),
                cancellationToken)
            .ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (properties.Duration <= TimeSpan.Zero)
        {
            throw new InvalidDataException("The music duration is invalid.");
        }

        IList<string> genres = properties.Genre;
        return new MusicMetadata(
            properties.Title,
            properties.Artist,
            properties.AlbumArtist,
            properties.Album,
            properties.TrackNumber,
            properties.Year,
            genres.ToArray(),
            properties.Bitrate,
            properties.Duration);
    }
}
