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

    Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        AnalysisRunOptions runOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken) => RunAsync(
            tool,
            scope,
            toolOptions,
            progress,
            cancellationToken);

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
    private readonly IVideoSampleProvider? _videoSampleProvider;
    private readonly IMusicMetadataProvider? _musicMetadataProvider;

    public AnalysisService()
        : this(null, null, null, null)
    {
    }

    public AnalysisService(IFileFormatProbe? fileFormatProbe)
        : this(fileFormatProbe, null, null, null)
    {
    }

    public AnalysisService(
        IFileFormatProbe? fileFormatProbe,
        IImageSampleProvider? imageSampleProvider)
        : this(fileFormatProbe, imageSampleProvider, null, null)
    {
    }

    public AnalysisService(
        IFileFormatProbe? fileFormatProbe,
        IImageSampleProvider? imageSampleProvider,
        IVideoSampleProvider? videoSampleProvider)
        : this(fileFormatProbe, imageSampleProvider, videoSampleProvider, null)
    {
    }

    public AnalysisService(
        IFileFormatProbe? fileFormatProbe,
        IImageSampleProvider? imageSampleProvider,
        IVideoSampleProvider? videoSampleProvider,
        IMusicMetadataProvider? musicMetadataProvider)
    {
        _fileFormatProbe = fileFormatProbe;
        _imageSampleProvider = imageSampleProvider;
        _videoSampleProvider = videoSampleProvider;
        _musicMetadataProvider = musicMetadataProvider;
    }

    public Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken) => RunAsync(
            tool,
            scope,
            toolOptions,
            new AnalysisRunOptions(1, UseMediaFingerprintCache: true),
            progress,
            cancellationToken);

    public async Task<AnalysisResult> RunAsync(
        ToolKind tool,
        AnalysisScope scope,
        ToolOptions toolOptions,
        AnalysisRunOptions runOptions,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runOptions);
        if (runOptions.MaxMediaConcurrency is not (1 or 2 or 4))
        {
            throw new ArgumentOutOfRangeException(nameof(runOptions));
        }

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

        if (toolOptions is SimilarVideoToolOptions { MaximumMeanFrameDistance: < 0 or > 13 })
        {
            throw new ArgumentOutOfRangeException(nameof(toolOptions));
        }

        if (toolOptions is MusicDuplicateToolOptions musicOptions &&
            musicOptions.MaximumDurationDifference < TimeSpan.Zero)
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

        if (tool == ToolKind.SimilarVideos && _videoSampleProvider is null)
        {
            throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet.");
        }

        if (tool == ToolKind.MusicDuplicates && _musicMetadataProvider is null)
        {
            throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet.");
        }

        var stopwatch = Stopwatch.StartNew();
        IProgress<AnalysisProgress>? inventoryProgress = progress is null
            ? null
            : new InventoryProgress(progress);
        FileInventory inventory = await Task.Run(
            () => new FileInventoryBuilder().Build(scope, inventoryProgress, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        long totalBytes = inventory.Files.Sum(static file => file.SizeBytes);
        progress?.Report(new AnalysisProgress(
            AnalysisPhase.Inspecting,
            inventory.Files.Count,
            0,
            0,
            totalBytes,
            null));
        var mediaProgress = new MediaAttemptProgress(inventory, totalBytes, progress);
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
                new RunImageSampleProvider(
                    _imageSampleProvider!,
                    runOptions.UseMediaFingerprintCache,
                    mediaProgress),
                runOptions.MaxMediaConcurrency).AnalyzeAsync(
                    inventory,
                    new SimilarImageOptions(options.MaximumHammingDistance),
                    cancellationToken).ConfigureAwait(false),
            (ToolKind.SimilarVideos, SimilarVideoToolOptions options) => await new SimilarVideoAnalyzer(
                new RunVideoSampleProvider(
                    _videoSampleProvider!,
                    runOptions.UseMediaFingerprintCache,
                    mediaProgress),
                runOptions.MaxMediaConcurrency).AnalyzeAsync(
                    inventory,
                    new SimilarVideoOptions(options.MaximumMeanFrameDistance),
                    cancellationToken).ConfigureAwait(false),
            (ToolKind.MusicDuplicates, MusicDuplicateToolOptions options) => await new MusicDuplicateAnalyzer(
                new RunMusicMetadataProvider(_musicMetadataProvider!, mediaProgress),
                runOptions.MaxMediaConcurrency).AnalyzeAsync(
                    inventory,
                    new MusicDuplicateOptions(options.MaximumDurationDifference),
                    cancellationToken).ConfigureAwait(false),
            _ => throw new NotSupportedException(
                $"The {ToolDescriptor.For(tool).Title} analyzer is not installed yet."),
        };

        return CompleteSuccessfulRun(
            result,
            stopwatch,
            inventory.Files.Count,
            totalBytes,
            progress,
            cancellationToken);
    }

    internal static AnalysisResult CompleteSuccessfulRun(
        AnalysisResult result,
        Stopwatch stopwatch,
        int itemsDiscovered,
        long totalBytes,
        IProgress<AnalysisProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        stopwatch.Stop();
        progress?.Report(new AnalysisProgress(
            AnalysisPhase.Done,
            itemsDiscovered,
            itemsDiscovered,
            totalBytes,
            totalBytes,
            null));
        return result with { Elapsed = stopwatch.Elapsed };
    }

    public Task<bool> RevalidateSimilarityItemAsync(
        ToolKind tool,
        SimilarityItem item,
        CancellationToken cancellationToken) => (tool, _imageSampleProvider) switch
        {
            (ToolKind.SimilarImages, not null) => new SimilarImageAnalyzer(_imageSampleProvider)
                .RevalidateAsync(item, cancellationToken),
            (ToolKind.SimilarVideos, _) when _videoSampleProvider is not null =>
                new SimilarVideoAnalyzer(_videoSampleProvider).RevalidateAsync(item, cancellationToken),
            (ToolKind.MusicDuplicates, _) when _musicMetadataProvider is not null =>
                new MusicDuplicateAnalyzer(_musicMetadataProvider).RevalidateAsync(item, cancellationToken),
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
            (ToolKind.SimilarVideos, SimilarVideoToolOptions videoOptions, _) when _videoSampleProvider is not null =>
                new SimilarVideoAnalyzer(_videoSampleProvider).Regroup(
                    items,
                    new SimilarVideoOptions(videoOptions.MaximumMeanFrameDistance)),
            (ToolKind.MusicDuplicates, MusicDuplicateToolOptions musicOptions, _) when _musicMetadataProvider is not null =>
                new MusicDuplicateAnalyzer(_musicMetadataProvider).Regroup(
                    items,
                    new MusicDuplicateOptions(musicOptions.MaximumDurationDifference)),
            _ => throw new NotSupportedException($"Regrouping is not available for {tool} with these options."),
        };

    private sealed class RunImageSampleProvider(
        IImageSampleProvider provider,
        bool useCache,
        MediaAttemptProgress progress) : IImageSampleProvider
    {
        public async Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                return useCache
                    ? await provider.GetSampleAsync(path, cancellationToken).ConfigureAwait(false)
                    : await provider.GetFreshSampleAsync(path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                progress.ReportAttempt(path);
            }
        }

        public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            provider.GetFreshSampleAsync(path, cancellationToken);
    }

    private sealed class RunVideoSampleProvider(
        IVideoSampleProvider provider,
        bool useCache,
        MediaAttemptProgress progress) : IVideoSampleProvider
    {
        public async Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                return useCache
                    ? await provider.GetSampleAsync(path, cancellationToken).ConfigureAwait(false)
                    : await provider.GetFreshSampleAsync(path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                progress.ReportAttempt(path);
            }
        }

        public Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken) =>
            provider.GetFreshSampleAsync(path, cancellationToken);
    }

    private sealed class RunMusicMetadataProvider(
        IMusicMetadataProvider provider,
        MediaAttemptProgress progress) : IMusicMetadataProvider
    {
        public async Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
        {
            try
            {
                return await provider.GetMetadataAsync(path, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                progress.ReportAttempt(path);
            }
        }
    }

    private sealed class MediaAttemptProgress
    {
        private readonly Lock _gate = new();
        private readonly IReadOnlyDictionary<string, long> _fileSizes;
        private readonly long _itemsDiscovered;
        private readonly long _totalBytes;
        private readonly IProgress<AnalysisProgress>? _progress;
        private long _itemsProcessed;
        private long _bytesProcessed;

        public MediaAttemptProgress(
            FileInventory inventory,
            long totalBytes,
            IProgress<AnalysisProgress>? progress)
        {
            _fileSizes = inventory.Files.ToDictionary(
                static file => file.FullPath,
                static file => file.SizeBytes,
                StringComparer.OrdinalIgnoreCase);
            _itemsDiscovered = inventory.Files.Count;
            _totalBytes = totalBytes;
            _progress = progress;
        }

        public void ReportAttempt(string path)
        {
            lock (_gate)
            {
                _itemsProcessed++;
                if (_fileSizes.TryGetValue(path, out long sizeBytes))
                {
                    _bytesProcessed += sizeBytes;
                }

                _progress?.Report(new AnalysisProgress(
                    AnalysisPhase.Inspecting,
                    _itemsDiscovered,
                    _itemsProcessed,
                    _bytesProcessed,
                    _totalBytes,
                    path));
            }
        }
    }

    private sealed class InventoryProgress(IProgress<AnalysisProgress> progress) : IProgress<AnalysisProgress>
    {
        public void Report(AnalysisProgress value)
        {
            if (value.Phase != AnalysisPhase.Done)
            {
                progress.Report(value);
            }
        }
    }
}
