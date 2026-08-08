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
        StorageFile? file = null;
        StorageItemContentProperties? contentProperties = null;
        MusicProperties? properties = null;
        IList<string>? genres = null;
        try
        {
            file = await WinRtAsync.AwaitAndCloseAsync(
                    StorageFile.GetFileFromPathAsync(path),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            contentProperties = file.Properties;
            cancellationToken.ThrowIfCancellationRequested();
            properties = await WinRtAsync.AwaitAndCloseAsync(
                    contentProperties.GetMusicPropertiesAsync(),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (properties.Duration <= TimeSpan.Zero)
            {
                throw new InvalidDataException("The music duration is invalid.");
            }

            genres = properties.Genre;
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
        finally
        {
            MediaLuminanceConverter.ReleaseNativeObject(genres);
            MediaLuminanceConverter.ReleaseNativeObject(properties);
            MediaLuminanceConverter.ReleaseNativeObject(contentProperties);
            MediaLuminanceConverter.ReleaseNativeObject(file);
        }
    }
}
