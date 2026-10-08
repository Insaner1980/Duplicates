using Duplicates.Engine.Analysis;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.App.Tests;

public sealed class AnalysisViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueuedProgressFromFinishedRunCannotOverwriteReplacementRun(bool cancelFirstRun)
    {
        AnalysisViewModel? viewModel = null;
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, progress, _) =>
            {
                if (++calls == 2)
                {
                    return release.Task;
                }

                progress!.Report(new AnalysisProgress(AnalysisPhase.Done, 5, 5, 10, 10, "old path"));
                if (cancelFirstRun)
                {
                    viewModel!.CancelAnalysisCommand.Execute(null);
                }

                return Task.FromResult(NewResult());
            },
        };
        viewModel = new AnalysisViewModel(service, new AnalysisSessionStore(), NewScope());
        var context = new QueuedAnalysisProgressContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task first;
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            first = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await first;
        Task second = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        viewModel.PhaseText = "Replacement run";
        viewModel.CurrentPath = "new path";
        try
        {
            Assert.True(context.HasProgress);
            context.DrainProgress();
            Assert.Equal("Replacement run", viewModel.PhaseText);
            Assert.Equal("new path", viewModel.CurrentPath);
        }
        finally
        {
            release.TrySetResult(NewResult());
            await second;
        }
    }

    [Fact]
    public void ConstructorAcceptsSettingsForRunSnapshotsAndIdleDefaults()
    {
        System.Reflection.ConstructorInfo? constructor = typeof(AnalysisViewModel).GetConstructor(
            [
                typeof(IAnalysisService),
                typeof(AnalysisSessionStore),
                typeof(PathScopeViewModel),
                typeof(IAppOperationCoordinator),
                typeof(ISettingsService)
            ]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public async Task ActiveMediaRunKeepsItsSettingsSnapshotAndAppliesNewestDefaultsAfterTerminalIdle()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) =>
            {
                started.SetResult();
                return release.Task;
            },
        };
        var settings = new FakeSettingsService();
        settings.SetCurrent(new AppSettings
        {
            MaxMediaConcurrency = 4,
            UseMediaFingerprintCache = true,
            DefaultImageSimilarity = SimilarityPreset.Balanced,
        });
        var coordinator = new AppOperationCoordinator();
        var scope = NewScope();
        var viewModel = new AnalysisViewModel(
            service,
            new AnalysisSessionStore(),
            scope,
            coordinator,
            settings);
        viewModel.SelectTool(ToolKind.SimilarImages);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        await started.Task;

        Assert.Equal(new AnalysisRunOptions(4, true), service.LastRunOptions);
        Assert.Equal(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun, UsesMediaFingerprintCache: true),
            coordinator.ActiveOperation);
        settings.SetCurrent(new AppSettings
        {
            MaxMediaConcurrency = 1,
            UseMediaFingerprintCache = false,
            DefaultImageSimilarity = SimilarityPreset.Broad,
            DefaultIncludeSubfolders = !scope.IncludeSubfolders,
            IgnoreHiddenFiles = !scope.IgnoreHiddenFiles,
            IgnoreSystemFiles = !scope.IgnoreSystemFiles,
        });
        Assert.Equal(SimilarityPreset.Balanced, viewModel.ImageSimilarityPreset);
        bool includeSubfolders = scope.IncludeSubfolders;
        bool ignoreHidden = scope.IgnoreHiddenFiles;
        bool ignoreSystem = scope.IgnoreSystemFiles;

        release.SetResult(NewResult());
        await run;

        Assert.Equal(SimilarityPreset.Broad, viewModel.ImageSimilarityPreset);
        Assert.Equal(includeSubfolders, scope.IncludeSubfolders);
        Assert.Equal(ignoreHidden, scope.IgnoreHiddenFiles);
        Assert.Equal(ignoreSystem, scope.IgnoreSystemFiles);
    }

    [Fact]
    public async Task SuccessfulRunMovesFromSetupThroughProgressToStoredResults()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = async (_, _, _, progress, _) =>
            {
                progress?.Report(new AnalysisProgress(AnalysisPhase.Inspecting, 12, 5, 40, 100, @"C:\scan\current.tmp"));
                return await release.Task;
            },
        };
        var store = new AnalysisSessionStore();
        var scope = NewScope();
        var viewModel = new AnalysisViewModel(service, store, scope);
        viewModel.SelectTool(ToolKind.TemporaryFiles);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);

        try
        {
            Assert.True(viewModel.IsAnalyzing);
            Assert.Equal(Visibility.Collapsed, viewModel.SetupVisibility);
            Assert.Equal(Visibility.Visible, viewModel.ProgressVisibility);
            Assert.True(SpinWait.SpinUntil(() => viewModel.CurrentPath == @"C:\scan\current.tmp", TimeSpan.FromSeconds(1)));
            Assert.False(run.IsCompleted);
            Assert.Null(store.CurrentSession);
        }
        finally
        {
            release.TrySetResult(NewResult([NewFinding(@"C:\scan\old.tmp", 25)]));
            await run;
        }

        AnalysisSession session = Assert.IsType<AnalysisSession>(store.CurrentSession);
        Assert.Equal(ToolKind.TemporaryFiles, session.Tool);
        Assert.Equal(@"C:\scan", Assert.Single(session.Scope.IncludedFolders));
        Assert.Equal(@"C:\scan\single.txt", Assert.Single(session.Scope.IncludedFiles));
        Assert.Equal(@"C:\scan\excluded", Assert.Single(session.Scope.ExcludedPaths));
        Assert.Equal("5", viewModel.ItemsProcessedText);
        Assert.Equal("40 B", viewModel.BytesProcessedText);
        Assert.False(viewModel.IsAnalyzing);
        Assert.Equal(Visibility.Visible, viewModel.SetupVisibility);
    }

    [Fact]
    public async Task CancellationKeepsLastSuccessfulSession()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = async (_, _, _, _, cancellationToken) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return NewResult();
            },
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.EmptyFiles);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        await started.Task;
        viewModel.CancelAnalysisCommand.Execute(null);
        await run;

        Assert.Same(previous, store.CurrentSession);
        Assert.Equal("Analysis cancelled.", viewModel.StatusMessage);
        Assert.Equal(InfoBarSeverity.Informational, viewModel.StatusSeverity);
    }

    [Fact]
    public async Task CancellationBeforeNormalServiceReturnKeepsPreviousSessionAndDoesNotComplete()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var serviceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseService = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) =>
            {
                serviceStarted.SetResult();
                return releaseService.Task;
            },
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.EmptyFiles);
        int completionCount = 0;
        viewModel.AnalysisCompleted += (_, _) => completionCount++;

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        await serviceStarted.Task;
        viewModel.CancelAnalysisCommand.Execute(null);
        releaseService.SetResult(NewResult([NewFinding(@"C:\scan\late.txt", 10)]));
        await run;

        Assert.Same(previous, store.CurrentSession);
        Assert.Equal(0, completionCount);
        Assert.Equal("Analysis cancelled.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task FailedRunShowsErrorAndKeepsLastSuccessfulSession()
    {
        var store = new AnalysisSessionStore();
        AnalysisSession previous = StorePreviousResult(store);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => throw new IOException("The file is locked."),
        };
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.BrokenFiles);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.Same(previous, store.CurrentSession);
        Assert.True(viewModel.IsStatusOpen);
        Assert.Equal("The file is locked.", viewModel.StatusMessage);
        Assert.Equal(InfoBarSeverity.Error, viewModel.StatusSeverity);
    }

    [Fact]
    public async Task BrokenFilesShowsExactCoverageAndRunsWithoutOptions()
    {
        IToolOptions? receivedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                receivedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var viewModel = new AnalysisViewModel(service, new AnalysisSessionStore(), NewScope());

        viewModel.SelectTool(ToolKind.BrokenFiles);

        Assert.Equal(
            "Checks readability and validates Windows-supported images, audio, video, and ZIP containers.",
            viewModel.Subtitle);
        Assert.Equal(Visibility.Collapsed, viewModel.OptionsVisibility);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.IsType<NoToolOptions>(receivedOptions);
    }

    [Fact]
    public async Task OnlyOneAnalysisCanRunAtATime()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => release.Task,
        };
        var viewModel = new AnalysisViewModel(service, new AnalysisSessionStore(), NewScope());
        viewModel.SelectTool(ToolKind.BadNames);

        Task first = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => viewModel.IsAnalyzing, TimeSpan.FromSeconds(1)));
        Task second = viewModel.StartAnalysisCommand.ExecuteAsync(null);

        Assert.Equal(1, service.CallCount);
        Assert.False(viewModel.StartAnalysisCommand.CanExecute(null));

        release.SetResult(NewResult());
        await Task.WhenAll(first, second);
        Assert.Equal(1, service.CallCount);
    }

    [Fact]
    public async Task ActiveRunRejectsToolSwitchAndKeepsVisibleToolConsistent()
    {
        var release = new TaskCompletionSource<AnalysisResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => release.Task,
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.BadNames);

        Task run = viewModel.StartAnalysisCommand.ExecuteAsync(null);
        Assert.True(SpinWait.SpinUntil(() => viewModel.IsAnalyzing, TimeSpan.FromSeconds(1)));

        viewModel.SelectTool(ToolKind.BigFiles);

        Assert.Equal(ToolKind.BadNames, viewModel.Tool);
        Assert.Equal("Bad names", viewModel.Title);
        Assert.False(viewModel.StartAnalysisCommand.CanExecute(null));
        Assert.Equal(1, service.CallCount);

        release.SetResult(NewResult());
        await run;
        Assert.Equal(ToolKind.BadNames, Assert.IsType<AnalysisSession>(store.CurrentSession).Tool);
    }

    [Fact]
    public async Task SuccessfulResultPreservesSkippedPathDetails()
    {
        AnalysisResult result = NewResult(
            skippedPaths: [new SkippedPath { Path = @"C:\scan\locked.txt", Reason = "Access denied" }]);
        var service = new FakeAnalysisService
        {
            Run = (_, _, _, _, _) => Task.FromResult(result),
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.InvalidLinks);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        SkippedPath skipped = Assert.Single(Assert.IsType<AnalysisSession>(store.CurrentSession).Result.SkippedPaths);
        Assert.Equal(@"C:\scan\locked.txt", skipped.Path);
        Assert.Equal("Access denied", skipped.Reason);
    }

    [Fact]
    public async Task BigFiles_NormalizesOptionsOnceAndStoresTheExactRunSnapshot()
    {
        IToolOptions? requestedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                requestedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.BigFiles);

        Assert.Equal(1_073_741_824d, viewModel.LargeFileMinimumSizeValue);
        Assert.Equal("Files at least 1 GB are included.", viewModel.OptionsSummary);
        Assert.Equal(Visibility.Visible, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Visible, viewModel.LargeFileOptionsVisibility);

        viewModel.LargeFileMinimumSizeValue = double.NaN;
        Assert.Equal("Files at least 1 GB are included.", viewModel.OptionsSummary);
        viewModel.LargeFileMinimumSizeValue = 104_857_600.9d;

        Assert.Equal("Files at least 100 MB are included.", viewModel.OptionsSummary);
        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        var options = Assert.IsType<LargeFileToolOptions>(requestedOptions);
        Assert.Equal(104_857_600, options.MinimumSizeBytes);
        Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
    }

    [Fact]
    public async Task TemporaryFiles_NormalizesOptionsOnceAndStoresTheExactRunSnapshot()
    {
        IToolOptions? requestedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                requestedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.TemporaryFiles);

        Assert.Equal(7d, viewModel.TemporaryFileMinimumAgeDays);
        Assert.Equal("Files at least 7 days old are included.", viewModel.OptionsSummary);
        Assert.Equal(Visibility.Visible, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Visible, viewModel.TemporaryFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LargeFileOptionsVisibility);

        viewModel.TemporaryFileMinimumAgeDays = double.NaN;
        Assert.Equal("Files at least 7 days old are included.", viewModel.OptionsSummary);
        viewModel.TemporaryFileMinimumAgeDays = double.MaxValue;
        Assert.Equal("Files at least 7 days old are included.", viewModel.OptionsSummary);
        viewModel.TemporaryFileMinimumAgeDays = 3;
        DateTime beforeRunUtc = DateTime.UtcNow;

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        DateTime afterRunUtc = DateTime.UtcNow;
        var options = Assert.IsType<TemporaryFileToolOptions>(requestedOptions);
        Assert.Equal(TimeSpan.FromDays(3), options.MinimumAge);
        Assert.InRange(options.UtcNow, beforeRunUtc, afterRunUtc);
        Assert.Equal("Files at least 3 days old are included.", viewModel.OptionsSummary);
        Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
    }

    [Fact]
    public async Task TemporaryFiles_LargeValidTimeSpanIsNormalizedAgainstCapturedUtcNow()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "old.tmp");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));

        try
        {
            var scope = new PathScopeViewModel();
            Assert.True(scope.AddFile(path));
            var store = new AnalysisSessionStore();
            var viewModel = new AnalysisViewModel(new AnalysisService(), store, scope);
            viewModel.SelectTool(ToolKind.TemporaryFiles);
            viewModel.TemporaryFileMinimumAgeDays = 1_000_000;

            await viewModel.StartAnalysisCommand.ExecuteAsync(null);

            AnalysisSession session = Assert.IsType<AnalysisSession>(store.CurrentSession);
            var options = Assert.IsType<TemporaryFileToolOptions>(session.ToolOptions);
            Assert.Equal(7d, viewModel.TemporaryFileMinimumAgeDays);
            Assert.Equal("Files at least 7 days old are included.", viewModel.OptionsSummary);
            Assert.Equal(TimeSpan.FromDays(7), options.MinimumAge);
            Assert.Equal(path, Assert.Single(session.Result.Findings).FullPath);
            Assert.Equal(string.Empty, viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SimilarImages_PresetIsNativeBalancedByDefaultAndStoresExactImmutableRunOptions()
    {
        IToolOptions? requestedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                requestedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.SimilarImages);

        Assert.Equal(SimilarityPreset.Balanced, viewModel.ImageSimilarityPreset);
        Assert.Equal("Balanced image matching allows a Hamming distance up to 8.", viewModel.OptionsSummary);
        Assert.Equal(Visibility.Visible, viewModel.SimilarImageOptionsVisibility);

        viewModel.ImageSimilarityPreset = SimilarityPreset.Strict;
        Assert.Equal("Strict image matching allows a Hamming distance up to 4.", viewModel.OptionsSummary);
        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        var options = Assert.IsType<SimilarImageToolOptions>(requestedOptions);
        Assert.Equal(4, options.MaximumHammingDistance);
        Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);

        viewModel.SelectTool(ToolKind.BigFiles);
        Assert.Equal(Visibility.Collapsed, viewModel.SimilarImageOptionsVisibility);
    }

    [Fact]
    public void SimilarImages_LegacySessionDefaultUsesBalancedDistanceEight()
    {
        var store = new AnalysisSessionStore();

        store.SetCompleted(ToolKind.SimilarImages, new AnalysisScope(), NewResult());

        Assert.Equal(8, Assert.IsType<SimilarImageToolOptions>(store.CurrentSession!.ToolOptions).MaximumHammingDistance);
    }

    [Theory]
    [InlineData(SimilarityPreset.Strict, 5)]
    [InlineData(SimilarityPreset.Balanced, 9)]
    [InlineData(SimilarityPreset.Broad, 13)]
    public async Task SimilarVideos_UsesNativePresetAndStoresExactImmutableRunOptions(
        SimilarityPreset preset,
        int expectedDistance)
    {
        IToolOptions? requestedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                requestedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());
        viewModel.SelectTool(ToolKind.SimilarVideos);

        Assert.Equal(SimilarityPreset.Balanced, viewModel.VideoSimilarityPreset);
        viewModel.VideoSimilarityPreset = preset;

        Assert.Equal(
            $"{preset} five-frame Windows media matching allows a mean frame distance up to {expectedDistance}.",
            viewModel.OptionsSummary);
        Assert.Equal(Visibility.Visible, viewModel.SimilarVideoOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.SimilarImageOptionsVisibility);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        var options = Assert.IsType<SimilarVideoToolOptions>(requestedOptions);
        Assert.Equal(expectedDistance, options.MaximumMeanFrameDistance);
        Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
    }

    [Fact]
    public void SimilarVideos_LegacySessionDefaultUsesBalancedDistanceNine()
    {
        var store = new AnalysisSessionStore();

        store.SetCompleted(ToolKind.SimilarVideos, new AnalysisScope(), NewResult());

        Assert.Equal(9, Assert.IsType<SimilarVideoToolOptions>(store.CurrentSession!.ToolOptions).MaximumMeanFrameDistance);
    }

    [Fact]
    public async Task MusicDuplicates_UsesExactDisclosureCollapsedOptionsAndImmutableTwoSecondSnapshot()
    {
        IToolOptions? requestedOptions = null;
        var service = new FakeAnalysisService
        {
            Run = (_, _, options, _, _) =>
            {
                requestedOptions = options;
                return Task.FromResult(NewResult());
            },
        };
        var store = new AnalysisSessionStore();
        var viewModel = new AnalysisViewModel(service, store, NewScope());

        viewModel.SelectTool(ToolKind.MusicDuplicates);

        Assert.Equal(
            "Match tracks using Windows music metadata and duration, not acoustic fingerprinting.",
            viewModel.Subtitle);
        Assert.Equal(Visibility.Collapsed, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LargeFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.TemporaryFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.SimilarImageOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.SimilarVideoOptionsVisibility);

        await viewModel.StartAnalysisCommand.ExecuteAsync(null);

        var options = Assert.IsType<MusicDuplicateToolOptions>(requestedOptions);
        Assert.Equal(TimeSpan.FromSeconds(2), options.MaximumDurationDifference);
        Assert.Same(options, Assert.IsType<AnalysisSession>(store.CurrentSession).ToolOptions);
    }

    [Fact]
    public void MusicDuplicates_LegacySessionDefaultUsesExactTwoSeconds()
    {
        var store = new AnalysisSessionStore();

        store.SetCompleted(ToolKind.MusicDuplicates, new AnalysisScope(), NewResult());

        Assert.Equal(
            TimeSpan.FromSeconds(2),
            Assert.IsType<MusicDuplicateToolOptions>(store.CurrentSession!.ToolOptions).MaximumDurationDifference);
    }

    [Fact]
    public void BigFiles_PresetsUseExactByteValuesAndNoOptionStorageToolsHideOptions()
    {
        var viewModel = new AnalysisViewModel(
            new FakeAnalysisService(),
            new AnalysisSessionStore(),
            NewScope());
        viewModel.SelectTool(ToolKind.BigFiles);

        viewModel.SetLargeFileMinimumSizeToAnyCommand.Execute(null);
        Assert.Equal(0d, viewModel.LargeFileMinimumSizeValue);
        viewModel.SetLargeFileMinimumSizeTo100MbCommand.Execute(null);
        Assert.Equal(104_857_600d, viewModel.LargeFileMinimumSizeValue);
        viewModel.SetLargeFileMinimumSizeTo1GbCommand.Execute(null);
        Assert.Equal(1_073_741_824d, viewModel.LargeFileMinimumSizeValue);
        viewModel.SetLargeFileMinimumSizeTo10GbCommand.Execute(null);
        Assert.Equal(10_737_418_240d, viewModel.LargeFileMinimumSizeValue);

        viewModel.SelectTool(ToolKind.EmptyFiles);

        Assert.Equal(Visibility.Collapsed, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LargeFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.TemporaryFileOptionsVisibility);

        viewModel.SelectTool(ToolKind.BadNames);

        Assert.Equal(Visibility.Collapsed, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LargeFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.TemporaryFileOptionsVisibility);

        viewModel.SelectTool(ToolKind.EmptyFolders);

        Assert.Equal(Visibility.Collapsed, viewModel.OptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.LargeFileOptionsVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.TemporaryFileOptionsVisibility);
    }

    [Fact]
    public async Task AnalysisService_BuildsInventoryOffCallerThreadAndRoutesStorageTools()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string largePath = Path.Combine(root, "large.bin");
        string emptyPath = Path.Combine(root, "empty.txt");
        string emptyFolderPath = Path.Combine(root, "empty-folder");
        string temporaryPath = Path.Combine(root, "download.PART");
        await File.WriteAllBytesAsync(largePath, [1, 2, 3], TestContext.Current.CancellationToken);
        await File.WriteAllBytesAsync(emptyPath, [], TestContext.Current.CancellationToken);
        Directory.CreateDirectory(emptyFolderPath);
        await File.WriteAllBytesAsync(temporaryPath, [1], TestContext.Current.CancellationToken);
        DateTime utcNow = DateTime.UtcNow;
        File.SetLastWriteTimeUtc(temporaryPath, utcNow.AddDays(-7));

        try
        {
            var execution = await Task.Factory.StartNew(
                async () =>
                {
                    var service = new AnalysisService();
                    int callerThread = Environment.CurrentManagedThreadId;
                    var reportThreads = new List<int>();
                    var progress = new CapturingProgress<AnalysisProgress>(
                        _ => reportThreads.Add(Environment.CurrentManagedThreadId));
                    AnalysisScope scope = new()
                    {
                        IncludedFiles = [largePath, emptyPath, temporaryPath],
                    };

                    AnalysisResult largeResult = await service.RunAsync(
                        ToolKind.BigFiles,
                        scope,
                        new LargeFileToolOptions(3),
                        progress,
                        CancellationToken.None);
                    AnalysisResult emptyResult = await service.RunAsync(
                        ToolKind.EmptyFiles,
                        scope,
                        new NoToolOptions(),
                        progress: null,
                        CancellationToken.None);
                    AnalysisResult emptyFolderResult = await service.RunAsync(
                        ToolKind.EmptyFolders,
                        new AnalysisScope { IncludedFolders = [root] },
                        new NoToolOptions(),
                        progress: null,
                        CancellationToken.None);
                    AnalysisResult temporaryResult = await service.RunAsync(
                        ToolKind.TemporaryFiles,
                        scope,
                        new TemporaryFileToolOptions(TimeSpan.FromDays(7), utcNow),
                        progress: null,
                        CancellationToken.None);

                    return (
                        CallerThread: callerThread,
                        ReportThreads: reportThreads,
                        LargeResult: largeResult,
                        EmptyResult: emptyResult,
                        EmptyFolderResult: emptyFolderResult,
                        TemporaryResult: temporaryResult);
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default).Unwrap();

            Assert.Equal(largePath, Assert.Single(execution.LargeResult.Findings).FullPath);
            Assert.Equal(emptyPath, Assert.Single(execution.EmptyResult.Findings).FullPath);
            Assert.Equal(emptyFolderPath, Assert.Single(execution.EmptyFolderResult.Findings).FullPath);
            Assert.Equal(temporaryPath, Assert.Single(execution.TemporaryResult.Findings).FullPath);
            Assert.NotEmpty(execution.ReportThreads);
            Assert.DoesNotContain(execution.CallerThread, execution.ReportThreads);
            Assert.True(execution.LargeResult.Elapsed >= TimeSpan.Zero);
            Assert.True(execution.EmptyResult.Elapsed >= TimeSpan.Zero);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AnalysisService_ValidatesOptionsBeforeInventoryAndKeepsLaterToolsUnavailable()
    {
        var reports = new List<AnalysisProgress>();
        var service = new AnalysisService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.RunAsync(
            ToolKind.BigFiles,
            new AnalysisScope { IncludedFolders = [@"C:\missing"] },
            new NoToolOptions(),
            new CapturingProgress<AnalysisProgress>(reports.Add),
            CancellationToken.None));
        await Assert.ThrowsAsync<NotSupportedException>(() => service.RunAsync(
            ToolKind.SimilarImages,
            new AnalysisScope(),
            new SimilarImageToolOptions(10),
            progress: null,
            CancellationToken.None));

        Assert.Empty(reports);
    }

    private static PathScopeViewModel NewScope()
    {
        var scope = new PathScopeViewModel
        {
            IncludeSubfolders = false,
            IgnoreHiddenFiles = false,
            IgnoreSystemFiles = true,
        };
        scope.IncludedPaths.Add(new ScopePathViewModel(@"C:\scan", ScopePathKind.Folder));
        scope.IncludedPaths.Add(new ScopePathViewModel(@"C:\scan\single.txt", ScopePathKind.File));
        scope.ExcludedPaths.Add(new ScopePathViewModel(@"C:\scan\excluded", ScopePathKind.Folder));
        return scope;
    }

    private static AnalysisSession StorePreviousResult(AnalysisSessionStore store)
    {
        var scope = new AnalysisScope { IncludedFolders = [@"C:\previous"] };
        store.SetCompleted(ToolKind.BigFiles, scope, NewResult([NewFinding(@"C:\previous\large.iso", 900)]));
        return Assert.IsType<AnalysisSession>(store.CurrentSession);
    }

    private static AnalysisResult NewResult(
        IReadOnlyList<PathFinding>? findings = null,
        IReadOnlyList<SkippedPath>? skippedPaths = null) => new()
        {
            Findings = findings ?? [],
            Groups = [],
            SkippedPaths = skippedPaths ?? [],
            Elapsed = TimeSpan.FromSeconds(1),
        };

    private static PathFinding NewFinding(string path, long size) => new()
    {
        FullPath = path,
        Kind = PathFindingKind.File,
        Reason = "Test finding",
        SizeBytes = size,
    };

    [Fact]
    public void LargeFileEditorShowsReadableUnitsForDefaultsAndPresets()
    {
        var analysis = new AnalysisViewModel(new FakeAnalysisService(), new AnalysisSessionStore(), NewScope());

        Assert.Equal((1d, 3), (analysis.LargeFileMinimumSizeEditor.Amount, analysis.LargeFileMinimumSizeEditor.UnitIndex));
        analysis.SetLargeFileMinimumSizeTo100MbCommand.Execute(null);
        Assert.Equal((100d, 2), (analysis.LargeFileMinimumSizeEditor.Amount, analysis.LargeFileMinimumSizeEditor.UnitIndex));
    }

    private sealed class FakeAnalysisService : IAnalysisService
    {
        public Func<ToolKind, AnalysisScope, IToolOptions, IProgress<AnalysisProgress>?, CancellationToken, Task<AnalysisResult>> Run { get; init; } =
            (_, _, _, _, _) => Task.FromResult(NewResult());

        public int CallCount { get; private set; }

        public AnalysisRunOptions? LastRunOptions { get; private set; }

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            IToolOptions toolOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Run(tool, scope, toolOptions, progress, cancellationToken);
        }

        public Task<AnalysisResult> RunAsync(
            ToolKind tool,
            AnalysisScope scope,
            IToolOptions toolOptions,
            AnalysisRunOptions runOptions,
            IProgress<AnalysisProgress>? progress,
            CancellationToken cancellationToken)
        {
            LastRunOptions = runOptions;
            CallCount++;
            return Run(tool, scope, toolOptions, progress, cancellationToken);
        }

        public Task<bool> RevalidateSimilarityItemAsync(
            ToolKind tool,
            SimilarityItem item,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public IReadOnlyList<SimilarityGroup> RegroupSimilarityItems(
            ToolKind tool,
            IToolOptions options,
            IReadOnlyList<SimilarityItem> items) => throw new NotSupportedException();
    }

    private sealed class CapturingProgress<T> : IProgress<T>
    {
        private readonly Action<T> _report;

        public CapturingProgress(Action<T> report)
        {
            _report = report;
        }

        public void Report(T value) => _report(value);
    }

    private sealed class QueuedAnalysisProgressContext : SynchronizationContext
    {
        private readonly Queue<(SendOrPostCallback Callback, object? State)> _progress = new();

        public bool HasProgress => _progress.Count > 0;

        public override void Post(SendOrPostCallback callback, object? state) => _progress.Enqueue((callback, state));

        public void DrainProgress()
        {
            while (_progress.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }

    [Fact]
    public void SimilarityPresetIndexesMirrorEnumsAndIgnoreInvalidSelections()
    {
        var viewModel = new AnalysisViewModel(new FakeAnalysisService(), new AnalysisSessionStore(), NewScope());
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal((int)SimilarityPreset.Balanced, viewModel.ImageSimilarityPresetIndex);
        Assert.Equal((int)SimilarityPreset.Balanced, viewModel.VideoSimilarityPresetIndex);

        viewModel.ImageSimilarityPresetIndex = (int)SimilarityPreset.Broad;
        viewModel.VideoSimilarityPreset = SimilarityPreset.Strict;
        viewModel.ImageSimilarityPresetIndex = -1;
        viewModel.VideoSimilarityPresetIndex = 99;

        Assert.Equal(SimilarityPreset.Broad, viewModel.ImageSimilarityPreset);
        Assert.Equal(SimilarityPreset.Strict, viewModel.VideoSimilarityPreset);
        Assert.Equal((int)SimilarityPreset.Strict, viewModel.VideoSimilarityPresetIndex);
        Assert.Contains(nameof(AnalysisViewModel.ImageSimilarityPresetIndex), changed);
        Assert.Contains(nameof(AnalysisViewModel.VideoSimilarityPresetIndex), changed);
    }

    [Theory]
    [InlineData(ToolKind.BigFiles, true)]
    [InlineData(ToolKind.TemporaryFiles, true)]
    [InlineData(ToolKind.SimilarImages, true)]
    [InlineData(ToolKind.SimilarVideos, true)]
    [InlineData(ToolKind.EmptyFolders, false)]
    [InlineData(ToolKind.EmptyFiles, false)]
    [InlineData(ToolKind.MusicDuplicates, false)]
    [InlineData(ToolKind.InvalidLinks, false)]
    [InlineData(ToolKind.BrokenFiles, false)]
    [InlineData(ToolKind.BadExtensions, false)]
    [InlineData(ToolKind.BadNames, false)]
    public void OptionsSectionIsShownOnlyForToolsWithEditableOptions(ToolKind tool, bool hasOptions)
    {
        var viewModel = new AnalysisViewModel(new FakeAnalysisService(), new AnalysisSessionStore(), NewScope());

        viewModel.SelectTool(tool);

        Assert.Equal(hasOptions ? Visibility.Visible : Visibility.Collapsed, viewModel.OptionsVisibility);
    }
}
