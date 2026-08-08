namespace Duplicates.Engine.Analysis;

public abstract record SimilarityEvidence;

public sealed record ImageSimilarityEvidence(
    ulong PerceptualHash,
    int Width,
    int Height,
    string Format) : SimilarityEvidence;

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
    string Codec) : SimilarityEvidence;

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
    TimeSpan Duration) : SimilarityEvidence
{
    public IReadOnlyList<string> Genres { get; init; } = CopyGenres(Genres);

    private static IReadOnlyList<string> CopyGenres(IReadOnlyList<string>? genres) =>
        Array.AsReadOnly(genres?.ToArray() ?? []);
}

public sealed class MissingRequiredMusicMetadataException : Exception
{
}
