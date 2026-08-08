namespace Duplicates.Engine.Analysis.Media;

public sealed record ImageSample(
    int Width,
    int Height,
    byte[] Luminance32x32,
    string Format);

public interface IImageSampleProvider
{
    Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken);

    Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken);
}
