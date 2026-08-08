namespace Duplicates.Engine.Analysis.Media;

public sealed record MusicMetadata(
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    uint TrackNumber,
    uint Year,
    IReadOnlyList<string> Genres,
    uint Bitrate,
    TimeSpan Duration);

public interface IMusicMetadataProvider
{
    Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken);
}
