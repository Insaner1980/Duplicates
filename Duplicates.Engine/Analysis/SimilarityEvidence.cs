namespace Duplicates.Engine.Analysis;

public interface ISimilarityEvidence
{
}

public sealed record ImageSimilarityEvidence(
    ulong PerceptualHash,
    int Width,
    int Height,
    string Format) : ISimilarityEvidence;

public sealed record VideoSimilarityEvidence(
    ulong FrameHash10,
    ulong FrameHash30,
    ulong FrameHash50,
    ulong FrameHash70,
    ulong FrameHash90,
    int Width,
    int Height,
    double DisplayAspectRatio,
    TimeSpan Duration,
    uint Bitrate,
    double FramesPerSecond,
    string Codec) : ISimilarityEvidence;

public sealed record MusicSimilarityEvidence(
    string NormalizedTitle,
    string NormalizedArtist,
    string Title,
    string Artist,
    string AlbumArtist,
    string Album,
    uint TrackNumber,
    uint Year,
    IReadOnlyList<string> Genres,
    uint Bitrate,
    TimeSpan Duration) : ISimilarityEvidence
{
    public IReadOnlyList<string> Genres { get; init; } = CopyGenres(Genres);

    private static System.Collections.ObjectModel.ReadOnlyCollection<string> CopyGenres(IReadOnlyList<string>? genres) =>
        Array.AsReadOnly(genres?.ToArray() ?? []);
}

public sealed class MissingRequiredMusicMetadataException : Exception
{
}
