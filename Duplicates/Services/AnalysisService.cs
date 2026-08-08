using System.Diagnostics;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
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

    Task<bool> RevalidateSimilarityItemAsync(
        ToolKind tool,
        SimilarityItem item,
        CancellationToken cancellationToken);

    IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
        ToolKind tool,
        ToolOptions options,
        IReadOnlyList<SimilarityItem> items);
}

public sealed class AnalysisService : IAnalysisService
{
    private readonly IFileFormatProbe? _fileFormatProbe;
    private readonly IImageSampleProvider? _imageSampleProvider;

    public AnalysisService()
        : this(null, null)
    {
    }

    public AnalysisService(IFileFormatProbe? fileFormatProbe)
        : this(fileFormatProbe, null)
    {
    }

    public AnalysisService(
        IFileFormatProbe? fileFormatProbe,
        IImageSampleProvider? imageSampleProvider)
    {
        _fileFormatProbe = fileFormatProbe;
        _imageSampleProvider = imageSampleProvider;
    }

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

        if (toolOptions is SimilarImageToolOptions { MaximumHammingDistance: < 0 or > 12 })
        {
            throw new ArgumentOutOfRangeException(nameof(toolOptions));
        }

        if (tool == ToolKind.BrokenFiles && _fileFormatProbe is null)
        {
            throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet.");
        }

        if (tool == ToolKind.SimilarImages && _imageSampleProvider is null)
        {
            throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet.");
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
            (ToolKind.BrokenFiles, NoToolOptions) => await new BrokenFileAnalyzer(_fileFormatProbe!).AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.BadExtensions, NoToolOptions) => await new BadExtensionAnalyzer().AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.BadNames, NoToolOptions) => await new BadNameAnalyzer().AnalyzeAsync(
                inventory,
                cancellationToken).ConfigureAwait(false),
            (ToolKind.TemporaryFiles, TemporaryFileToolOptions options) => await new TemporaryFileAnalyzer().AnalyzeAsync(
                inventory,
                new TemporaryFileOptions(options.MinimumAge, options.UtcNow),
                cancellationToken).ConfigureAwait(false),
            (ToolKind.SimilarImages, SimilarImageToolOptions options) => await new SimilarImageAnalyzer(
                _imageSampleProvider!).AnalyzeAsync(
                    inventory,
                    new SimilarImageOptions(options.MaximumHammingDistance),
                    cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet."),
        };

        stopwatch.Stop();
        return result with { Elapsed = stopwatch.Elapsed };
    }

    public Task<bool> RevalidateSimilarityItemAsync(
        ToolKind tool,
        SimilarityItem item,
        CancellationToken cancellationToken) => (tool, _imageSampleProvider) switch
        {
            (ToolKind.SimilarImages, not null) => new SimilarImageAnalyzer(_imageSampleProvider)
                .RevalidateAsync(item, cancellationToken),
            _ => throw new NotSupportedException($"Revalidation is not available for {tool}."),
        };

    public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
        ToolKind tool,
        ToolOptions options,
        IReadOnlyList<SimilarityItem> items) => (tool, options, _imageSampleProvider) switch
        {
            (ToolKind.SimilarImages, SimilarImageToolOptions imageOptions, not null) =>
                new SimilarImageAnalyzer(_imageSampleProvider).Regroup(
                    items,
                    new SimilarImageOptions(imageOptions.MaximumHammingDistance)),
            _ => throw new NotSupportedException($"Regrouping is not available for {tool} with these options."),
        };
}
