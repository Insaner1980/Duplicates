namespace Duplicates.Engine.Analysis.Media;

public enum FileProbeStatus
{
    Valid,
    Invalid,
    UnsupportedOrProtected,
}

public sealed record FileProbeResult(
    FileProbeStatus Status,
    string? ErrorType,
    string? Message);

public interface IFileFormatProbe
{
    Task<FileProbeResult> ProbeAsync(
        string path,
        DetectedFileType? detectedType,
        CancellationToken cancellationToken);
}
