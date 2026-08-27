namespace Duplicates.Engine.Analysis.Media;

public sealed record VideoSample(
    int Width,
    int Height,
    double DisplayAspectRatio,
    TimeSpan Duration,
    uint Bitrate,
    double FramesPerSecond,
    string Codec,
    IReadOnlyList<byte[]> LuminanceFrames32x32);

public interface IVideoSampleProvider
{
    Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken);

    Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken);
}
