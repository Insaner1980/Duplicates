namespace Duplicates.Services;

internal interface IVideoMediaProbe
{
    Task<VideoMediaInfo> ProbeAsync(string path, CancellationToken cancellationToken);
}
