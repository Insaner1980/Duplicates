namespace Duplicates.Engine.Analysis;

public abstract record SimilarityEvidence;

public sealed record ImageSimilarityEvidence(
    ulong PerceptualHash,
    int Width,
    int Height,
    string Format) : SimilarityEvidence;
