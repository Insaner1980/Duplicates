using Duplicates.Models;

namespace Duplicates.Services;

public enum LinkReplacementMode
{
    HardLink,
    SymbolicLink,
}

public sealed record LinkReplacementFile(
    string FullPath,
    long ExpectedLength,
    DateTime ExpectedModifiedUtc);

public sealed record LinkReplacementGroup(
    LinkReplacementFile Survivor,
    IReadOnlyList<LinkReplacementFile> Duplicates);

public interface IFileLinkService
{
    Task<FileOperationSummary> ReplaceWithLinksAsync(
        IReadOnlyList<LinkReplacementGroup> groups,
        LinkReplacementMode mode,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken);
}
