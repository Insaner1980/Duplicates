using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public interface IAnalysisService
{
    Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed class AnalysisService : IAnalysisService
{
    public Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        bool isValidPair = (tool, toolOptions) switch
        {
            (ToolKind.BigFiles, LargeFileToolOptions) => true,
            (ToolKind.TemporaryFiles, TemporaryFileToolOptions) => true,
            (ToolKind.SimilarImages, SimilarImageToolOptions) => true,
            (ToolKind.SimilarVideos, SimilarVideoToolOptions) => true,
            (ToolKind.MusicDuplicates, MusicDuplicateToolOptions) => true,
            (ToolKind.EmptyFolders or ToolKind.EmptyFiles or ToolKind.InvalidLinks or
                ToolKind.BrokenFiles or ToolKind.BadExtensions or ToolKind.BadNames,
                NoToolOptions) => true,
            _ => false,
        };

        if (!isValidPair)
        {
            throw new ArgumentException($"Options do not match {tool}.", nameof(toolOptions));
        }

        return Task.FromException<AnalysisResult>(
            new NotSupportedException($"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet."));
    }
}
