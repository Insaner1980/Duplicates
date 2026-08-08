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
