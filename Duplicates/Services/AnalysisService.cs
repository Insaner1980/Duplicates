using System.Diagnostics;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
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
    public async Task<AnalysisResult> RunAsync(
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

        var stopwatch = Stopwatch.StartNew();
        FileInventory inventory = await Task.Run(
            () => new FileInventoryBuilder().Build(scope, progress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        AnalysisResult result = (tool, toolOptions) switch
        {
            (ToolKind.BigFiles, LargeFileToolOptions options) => await new LargeFileAnalyzer().AnalyzeAsync(
                inventory,
                options.MinimumSizeBytes,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.EmptyFiles, NoToolOptions) => await new EmptyFileAnalyzer().AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.EmptyFolders, NoToolOptions) => await new EmptyFolderAnalyzer().AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.InvalidLinks, NoToolOptions) => await new InvalidLinkAnalyzer().AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.TemporaryFiles, TemporaryFileToolOptions options) => await new TemporaryFileAnalyzer().AnalyzeAsync(
                inventory,
                new TemporaryFileOptions(options.MinimumAge, options.UtcNow),
                cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet."),
        };

        stopwatch.Stop();
        return result with { Elapsed = stopwatch.Elapsed };
    }
}
