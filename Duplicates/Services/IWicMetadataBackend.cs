namespace Duplicates.Services;

internal enum WicContainerKind
{
    Jpeg,
    Tiff,
}

internal sealed record WicRenderState(
    int FrameCount,
    uint Width,
    uint Height,
    double DpiX,
    double DpiY,
    string? Orientation,
    IReadOnlyList<string> ColorContexts,
    string PixelChecksum);

internal sealed record WicImageInspection(
    WicContainerKind Container,
    WicRenderState RenderState,
    IReadOnlyDictionary<string, string> Metadata);

internal sealed class WicMetadataLayoutException : Exception
{
    public WicMetadataLayoutException(string message)
        : base(message)
    {
    }

    public WicMetadataLayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal interface IWicMetadataBackend
{
    WicImageInspection Inspect(string path);

    void RemoveMetadata(
        string path,
        WicContainerKind container,
        IReadOnlyList<string> queries);
}
