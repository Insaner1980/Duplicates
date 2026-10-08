using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

public sealed partial class VideoOptimizerViewModelTests
{
    [Fact]
    public void Defaults_ReuseSharedScopeAndUseBalancedHardwareOnKeepOff()
    {
        var scope = new PathScopeViewModel();
        VideoOptimizerViewModel viewModel = CreateViewModel(pathScope: scope);

        Assert.Same(scope, viewModel.PathScope);
        Assert.Equal(VideoOptimizationPreset.Balanced, viewModel.Preset);
        Assert.True(viewModel.HardwareAccelerationEnabled);
        Assert.False(viewModel.KeepOutputWhenNotSmaller);
    }

    [Fact]
    public async Task RefreshQueueBuildsVisibleSnapshotBeforeRunWithTargetProfile()
    {
        var optimizer = new FakeVideoOptimizerService();
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\review.mp4", 500)));

        await viewModel.RefreshQueueCommand.ExecuteAsync(null);

        VideoOptimizationQueueItemViewModel item = Assert.Single(viewModel.Queue);
        Assert.Empty(optimizer.Requests);
        Assert.Contains("Balanced", item.TargetSummary, StringComparison.Ordinal);
        Assert.Contains("H.264", item.TargetSummary, StringComparison.Ordinal);
        Assert.Contains("1920", item.TargetSummary, StringComparison.Ordinal);
        Assert.Contains("1080", item.TargetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PresetChangeUpdatesTheVisiblePreviewBeforeRun()
    {
        VideoOptimizerViewModel viewModel = CreateViewModel(
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\preset.mp4", 500)));
        await viewModel.RefreshQueueCommand.ExecuteAsync(null);

        viewModel.Preset = VideoOptimizationPreset.HighQuality;

        VideoOptimizationQueueItemViewModel item = Assert.Single(viewModel.Queue);
        Assert.Contains("High quality", item.TargetSummary, StringComparison.Ordinal);
        Assert.Contains("3840", item.TargetSummary, StringComparison.Ordinal);
        Assert.Contains("2160", item.TargetSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshQueueHonorsRealSharedScopeRulesAndDuplicatePaths()
    {
        string root = CreateTempRoot();
        string externalRoot = CreateTempRoot();
        string hidden = string.Empty;
        string system = string.Empty;
        try
        {
            string top = WriteFile(root, "top.mp4", 10);
            string nestedRoot = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            string nested = WriteFile(nestedRoot, "nested.mov", 20);
            string excludedRoot = Directory.CreateDirectory(Path.Combine(root, "excluded")).FullName;
            _ = WriteFile(excludedRoot, "excluded.mkv", 30);
            hidden = WriteFile(root, "hidden.mp4", 40);
            system = WriteFile(root, "system.mp4", 50);
            _ = WriteFile(root, "not-video.txt", 60);
            string explicitVideo = WriteFile(externalRoot, "explicit.webm", 70);
            File.SetAttributes(hidden, FileAttributes.Hidden);
            File.SetAttributes(system, FileAttributes.System);

            var scope = new PathScopeViewModel();
            Assert.True(scope.AddFolder(root));
            Assert.False(scope.AddFolder(root.ToUpperInvariant()));
            Assert.True(scope.AddFile(explicitVideo));
            Assert.True(scope.ExcludePath(excludedRoot));
            VideoOptimizerViewModel viewModel = new(
                new FakeVideoOptimizerService(),
                scope,
                new RecordingFileActionService(),
                new AppOperationCoordinator());

            await viewModel.RefreshQueueCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { explicitVideo, nested, top }.Order(StringComparer.OrdinalIgnoreCase),
                viewModel.Queue.Select(item => item.SourcePath).Order(StringComparer.OrdinalIgnoreCase));

            scope.IncludeSubfolders = false;
            await viewModel.RefreshQueueCommand.ExecuteAsync(null);

            Assert.Equal(
                new[] { explicitVideo, top }.Order(StringComparer.OrdinalIgnoreCase),
                viewModel.Queue.Select(item => item.SourcePath).Order(StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            if (File.Exists(hidden))
            {
                File.SetAttributes(hidden, FileAttributes.Normal);
            }

            if (File.Exists(system))
            {
                File.SetAttributes(system, FileAttributes.Normal);
            }

            Directory.Delete(root, recursive: true);
            Directory.Delete(externalRoot, recursive: true);
        }
    }

    [Fact]
    public async Task SharedScopeInventoryUsesEngineVideoFilterAndReservesSameStemDestinations()
    {
        DateTime modified = SnapshotTime();
        FileInventory inventory = Inventory(
            Video(@"C:\Media\clip.mov", 100, modified),
            Video(@"C:\Media\clip.mkv", 200, modified),
            Video(@"C:\Media\other.MP4", 300, modified),
            Video(@"C:\Media\not-video.txt", 400, modified),
            Video(@"C:\Media\linked.mp4", 500, modified, FileAttributes.ReparsePoint));
        var optimizer = new FakeVideoOptimizerService();
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => inventory);

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Equal(3, optimizer.Requests.Count);
        Assert.Equal(
            [
                "C:\\Media\\clip.optimized.mp4",
                "C:\\Media\\clip.optimized (2).mp4",
                "C:\\Media\\other.optimized.mp4"
            ],
            optimizer.Requests.Select(static request => request.DestinationPath));
        Assert.Equal(optimizer.Requests.Count, viewModel.Queue.Count);
        Assert.All(viewModel.Queue, item =>
        {
            Assert.True(Path.IsPathFullyQualified(item.SourcePath));
            Assert.True(item.SizeBytes > 0);
            Assert.Equal(modified, item.ModifiedUtc);
        });
    }

    [Fact]
    public async Task RunFreezesQueueDestinationAndOptionsBeforeFirstServiceCall()
    {
        DateTime modified = SnapshotTime();
        VideoOptimizerViewModel? viewModel = null;
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = (request, progress, _) =>
            {
                if (request.SourcePath.EndsWith("one.mp4", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel!.Preset = VideoOptimizationPreset.Smaller;
                    viewModel.HardwareAccelerationEnabled = false;
                    viewModel.KeepOutputWhenNotSmaller = true;
                    viewModel.PathScope.IncludedPaths.Add(new ScopePathViewModel("C:\\Media\\late.mp4", Duplicates.Models.ScopePathKind.File));
                }

                progress?.Report(100);
                return Task.FromResult(Succeeded(request));
            },
        };
        viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(
                Video(@"C:\Media\one.mp4", 100, modified),
                Video(@"C:\Media\two.mp4", 200, modified)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Equal(2, optimizer.Requests.Count);
        Assert.All(optimizer.Requests, request => Assert.Equal(
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false),
            request.Options));
        Assert.DoesNotContain(optimizer.Requests, request => request.SourcePath.EndsWith("late.mp4"));
        Assert.Equal(
            ["one.optimized.mp4", "two.optimized.mp4"],
            optimizer.Requests.Select(request => Path.GetFileName(request.DestinationPath)));
    }

    [Fact]
    public async Task ProcessingIsSequentialAndAggregatesPerFileProgress()
    {
        int active = 0;
        int maximumActive = 0;
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = async (request, progress, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref active);
                maximumActive = Math.Max(maximumActive, current);
                progress?.Report(25);
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(100);
                Interlocked.Decrement(ref active);
                return Succeeded(request);
            },
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(
                Video(@"C:\Media\one.mp4", 100),
                Video(@"C:\Media\two.mp4", 200)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Equal(1, maximumActive);
        Assert.Equal(100, viewModel.ProgressValue);
        Assert.Equal(100, viewModel.CurrentFileProgress);
        Assert.Equal(2, viewModel.ProcessedCount);
        Assert.Equal(2, viewModel.TotalCount);
        Assert.All(viewModel.Queue, item => Assert.Equal(100, item.Progress));
    }

    [Fact]
    public async Task OrdinaryFailureContinuesButRecoveryRequiredStopsBatch()
    {
        int call = 0;
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = (request, _, _) =>
            {
                call++;
                return Task.FromResult(call switch
                {
                    1 => Failed(request, VideoOptimizationOutcome.CodecNotFound),
                    2 => Failed(request, VideoOptimizationOutcome.RecoveryRequired, [request.DestinationPath + ".tmp"]),
                    _ => Succeeded(request),
                });
            },
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(
                Video(@"C:\Media\one.mp4", 100),
                Video(@"C:\Media\two.mp4", 200),
                Video(@"C:\Media\three.mp4", 300)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Equal(2, optimizer.Requests.Count);
        Assert.Equal(VideoOptimizationOutcome.CodecNotFound, viewModel.Queue[0].Outcome);
        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, viewModel.Queue[1].Outcome);
        Assert.Null(viewModel.Queue[2].Outcome);
        Assert.Contains("recovery", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationStopsBeforeNextFileAndReleasesExactCoordinatorLease()
    {
        var coordinator = new AppOperationCoordinator();
        AppOperationDescriptor? activeDescriptor = null;
        VideoOptimizerViewModel? viewModel = null;
        int call = 0;
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = (request, _, cancellationToken) =>
            {
                call++;
                activeDescriptor = coordinator.ActiveOperation;
                if (call == 1)
                {
                    viewModel!.CancelOptimizationCommand.Execute(null);
                    cancellationToken.ThrowIfCancellationRequested();
                }

                return Task.FromResult(Succeeded(request));
            },
        };
        viewModel = CreateViewModel(
            optimizer,
            coordinator: coordinator,
            inventoryBuilder: (_, _) => Inventory(
                Video(@"C:\Media\one.mp4", 100),
                Video(@"C:\Media\two.mp4", 200)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Single(optimizer.Requests);
        Assert.Equal(new AppOperationDescriptor(AppOperationKind.VideoOptimization, false), activeDescriptor);
        Assert.Null(coordinator.ActiveOperation);
        Assert.False(viewModel.IsOptimizing);
        Assert.Contains("cancel", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancellationRecoveryRetainsServiceMediaSnapshotsOnTheActiveRow()
    {
        VideoMediaInfo sourceMedia = SourceMedia();
        VideoMediaInfo outputMedia = SourceMedia() with { VideoBitrate = 3_000_000 };
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = (_, _, cancellationToken) => throw new VideoOptimizationCancellationException(
                [@"C:\Media\recovery.tmp.mp4"],
                sourceMedia,
                outputMedia,
                cancellationToken),
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\one.mp4", 100)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        VideoOptimizationQueueItemViewModel item = Assert.Single(viewModel.Queue);
        Assert.Equal(VideoOptimizationOutcome.RecoveryRequired, item.Outcome);
        Assert.Same(sourceMedia, item.SourceMedia);
        Assert.Same(outputMedia, item.OutputMedia);
        Assert.Equal([@"C:\Media\recovery.tmp.mp4"], item.RecoveryPaths);
    }

    [Fact]
    public async Task DelayedProgressFromPreviousRunCannotChangeTheCurrentRun()
    {
        var progressCallbacks = new List<Action<double>>();
        var secondRunEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondRun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int run = 0;
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = async (request, progress, _) =>
            {
                run++;
                if (run == 1)
                {
                    return Succeeded(request);
                }

                progress?.Report(10);
                secondRunEntered.SetResult();
                await releaseSecondRun.Task;
                return Failed(request, VideoOptimizationOutcome.Failed);
            },
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\one.mp4", 100)),
            progressFactory: callback =>
            {
                progressCallbacks.Add(callback);
                return new InlineProgress(callback);
            });

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);
        Task secondRun = viewModel.OptimizeVideosCommand.ExecuteAsync(null);
        await secondRunEntered.Task;
        await WaitUntilAsync(() => viewModel.CurrentFileProgress == 10);

        Assert.Equal(2, progressCallbacks.Count);
        progressCallbacks[0](90);

        Assert.Equal(10, viewModel.CurrentFileProgress);
        Assert.Equal(10, viewModel.ProgressValue);
        Assert.Equal(10, viewModel.Queue[0].Progress);
        releaseSecondRun.SetResult();
        await secondRun;
    }

    [Fact]
    public async Task CoordinatorLeaseIsReleasedBeforeCancellationSourceIsDisposed()
    {
        var coordinator = new CancellationOnDisposeCoordinator();
        VideoOptimizerViewModel viewModel = CreateViewModel(
            coordinator: coordinator,
            inventoryBuilder: (_, _) => Inventory());

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.True(coordinator.LeaseDisposed);
        Assert.Null(coordinator.ActiveOperation);
    }

    [Fact]
    public async Task AcquisitionEventFailureReleasesThePublishedCoordinatorLease()
    {
        var coordinator = new AppOperationCoordinator();
        coordinator.ActiveOperationChanged += (_, _) =>
        {
            if (coordinator.ActiveOperation is not null)
            {
                throw new InvalidOperationException("Subscriber failed after acquisition.");
            }
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            coordinator: coordinator,
            inventoryBuilder: (_, _) => throw new InvalidOperationException("Inventory must not start."));

        Exception? exception = await Record.ExceptionAsync(
            () => viewModel.OptimizeVideosCommand.ExecuteAsync(null));

        Assert.Null(exception);
        Assert.Null(coordinator.ActiveOperation);
        Assert.False(viewModel.IsOptimizing);
        Assert.Equal("Video optimization failed unexpectedly.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task OptimizerExceptionUsesStableStatusMessage()
    {
        var optimizer = new FakeVideoOptimizerService
        {
            Handler = (_, _, _) => throw new InvalidOperationException("sensitive optimizer text"),
        };
        VideoOptimizerViewModel viewModel = CreateViewModel(
            optimizer,
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\one.mp4", 100)));

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        Assert.Equal("Video optimization failed unexpectedly.", viewModel.StatusMessage);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task QueueRefreshExceptionUsesStableStatusMessage()
    {
        VideoOptimizerViewModel viewModel = CreateViewModel(
            inventoryBuilder: (_, _) => throw new InvalidOperationException("sensitive inventory text"));

        await viewModel.RefreshQueueCommand.ExecuteAsync(null);

        Assert.Equal("The video queue could not be refreshed.", viewModel.StatusMessage);
        Assert.DoesNotContain("sensitive", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VmRecheckSkipsChangedLaterSourceBeforeServiceCall()
    {
        string root = CreateTempRoot();
        try
        {
            string first = WriteFile(root, "one.mp4", 100);
            string second = WriteFile(root, "two.mp4", 100);
            FileInventory inventory = Inventory(FromFile(first), FromFile(second));
            var optimizer = new FakeVideoOptimizerService
            {
                Handler = (request, _, _) =>
                {
                    if (request.SourcePath == first)
                    {
                        File.AppendAllText(second, "changed");
                    }

                    return Task.FromResult(Succeeded(request));
                },
            };
            VideoOptimizerViewModel viewModel = CreateViewModel(
                optimizer,
                inventoryBuilder: (_, _) => inventory,
                snapshotRechecker: VideoOptimizerViewModel.IsCurrentOrdinarySnapshot);

            await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

            Assert.Single(optimizer.Requests);
            Assert.Equal(VideoOptimizationOutcome.SourceChanged, viewModel.Queue[1].Outcome);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PublishedOutputActionsAreGatedToCommittedOutcomes()
    {
        var actions = new RecordingFileActionService();
        VideoOptimizerViewModel viewModel = CreateViewModel(fileActionService: actions);
        var succeeded = new VideoOptimizationQueueItemViewModel(
            Video(@"C:\Media\one.mp4", 100),
            @"C:\Media\one.optimized.mp4");
        succeeded.Apply(Succeeded(new VideoOptimizationRequest(
            succeeded.SourcePath,
            succeeded.SizeBytes,
            succeeded.ModifiedUtc,
            succeeded.DestinationPath,
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false))));
        var failed = new VideoOptimizationQueueItemViewModel(
            Video(@"C:\Media\two.mp4", 100),
            @"C:\Media\two.optimized.mp4");
        failed.Apply(Failed(new VideoOptimizationRequest(
            failed.SourcePath,
            failed.SizeBytes,
            failed.ModifiedUtc,
            failed.DestinationPath,
            new VideoOptimizationOptions(VideoOptimizationPreset.Balanced, true, false)), VideoOptimizationOutcome.Failed));

        Assert.True(viewModel.OpenOutputCommand.CanExecute(succeeded));
        Assert.True(viewModel.RevealOutputCommand.CanExecute(succeeded));
        Assert.False(viewModel.OpenOutputCommand.CanExecute(failed));

        viewModel.OpenOutputCommand.Execute(succeeded);
        viewModel.RevealOutputCommand.Execute(succeeded);

        Assert.Equal([succeeded.OutputPath!], actions.OpenedPaths);
        Assert.Equal([succeeded.OutputPath!], actions.RevealedPaths);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task PublishedRowRaisesOutputActionCanExecuteChanged()
    {
        int openNotifications = 0;
        int revealNotifications = 0;
        VideoOptimizerViewModel viewModel = CreateViewModel(
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\one.mp4", 100)));
        viewModel.OpenOutputCommand.CanExecuteChanged += (_, _) => openNotifications++;
        viewModel.RevealOutputCommand.CanExecuteChanged += (_, _) => revealNotifications++;

        await viewModel.OptimizeVideosCommand.ExecuteAsync(null);

        VideoOptimizationQueueItemViewModel item = Assert.Single(viewModel.Queue);
        Assert.True(viewModel.OpenOutputCommand.CanExecute(item));
        Assert.True(viewModel.RevealOutputCommand.CanExecute(item));
        Assert.Equal(1, openNotifications);
        Assert.Equal(1, revealNotifications);
    }

    private static VideoOptimizerViewModel CreateViewModel(
        FakeVideoOptimizerService? optimizer = null,
        RecordingFileActionService? fileActionService = null,
        PathScopeViewModel? pathScope = null,
        IAppOperationCoordinator? coordinator = null,
        Func<AnalysisScope, CancellationToken, FileInventory>? inventoryBuilder = null,
        Func<InventoryFile, bool>? snapshotRechecker = null,
        Func<Action<double>, IProgress<double>>? progressFactory = null)
    {
        pathScope ??= new PathScopeViewModel();
        if (!pathScope.HasIncludedPaths)
        {
            pathScope.IncludedPaths.Add(new ScopePathViewModel("C:\\VideoScope", Duplicates.Models.ScopePathKind.Folder));
        }

        return new VideoOptimizerViewModel(
            optimizer ?? new FakeVideoOptimizerService(),
            pathScope,
            fileActionService ?? new RecordingFileActionService(),
            coordinator ?? new AppOperationCoordinator(),
            inventoryBuilder ?? ((_, _) => Inventory()),
            snapshotRechecker ?? (static _ => true),
            progressFactory);
    }

    private static FileInventory Inventory(params InventoryFile[] files) => new(files, [], [], [], []);

    private static InventoryFile Video(
        string path,
        long size,
        DateTime? modifiedUtc = null,
        FileAttributes attributes = FileAttributes.Normal) => new(
            Path.GetFullPath(path),
            Path.GetFileName(path),
            Path.GetExtension(path).ToLowerInvariant(),
            Path.GetDirectoryName(path)!,
            size,
            SnapshotTime().AddHours(-1),
            modifiedUtc ?? SnapshotTime(),
            attributes);

    private static DateTime SnapshotTime() =>
        new(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc);

    private static VideoOptimizationResult Succeeded(VideoOptimizationRequest request) => new(
        VideoOptimizationOutcome.Succeeded,
        request.SourcePath,
        request.DestinationPath,
        Math.Max(0, request.ExpectedLength - 10),
        SourceMedia(),
        SourceMedia(),
        10,
        "Optimized.",
        []);

    private static VideoOptimizationResult Failed(
        VideoOptimizationRequest request,
        VideoOptimizationOutcome outcome,
        IReadOnlyList<string>? recoveryPaths = null) => new(
            outcome,
            request.SourcePath,
            null,
            null,
            SourceMedia(),
            null,
            0,
            "Failed.",
            recoveryPaths ?? []);

    private static VideoMediaInfo SourceMedia() => new(
        1920, 1080, 1920, 1080, 16d / 9, 1, 1, TimeSpan.FromSeconds(10),
        6_000_000, 5_800_000, 30, 1, "H.264", "MP4", "AAC", 128_000, 1, 1, 0);

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-Video-VM-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteFile(string directory, string name, int size)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[size]);
        return path;
    }

    private static InventoryFile FromFile(string path)
    {
        var file = new FileInfo(path);
        return Video(file.FullName, file.Length, file.LastWriteTimeUtc);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }

    private sealed class FakeVideoOptimizerService : IVideoOptimizerService
    {
        public Func<VideoOptimizationRequest, IProgress<double>?, CancellationToken, Task<VideoOptimizationResult>> Handler { get; set; } =
            static (request, progress, _) =>
            {
                progress?.Report(100);
                return Task.FromResult(Succeeded(request));
            };

        public List<VideoOptimizationRequest> Requests { get; } = [];

        public Task<VideoOptimizationResult> OptimizeAsync(
            VideoOptimizationRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Handler(request, progress, cancellationToken);
        }
    }

    private sealed class InlineProgress : IProgress<double>
    {
        private readonly Action<double> _callback;

        public InlineProgress(Action<double> callback)
        {
            _callback = callback;
        }

        public void Report(double value) => _callback(value);
    }

    private sealed class RecordingFileActionService : IFileActionService
    {
        public List<string> OpenedPaths { get; } = [];

        public List<string> RevealedPaths { get; } = [];

        public Task<DeleteSummary> DeleteAsync(
            IReadOnlyList<FileActionTarget> targets,
            IProgress<DeleteProgress>? progress,
            CancellationToken cancellationToken,
            DeletionMode? deletionMode = null) => throw new NotSupportedException();

        public Task<FileOperationSummary> MoveAsync(
            IReadOnlyList<FileActionTarget> targets,
            string destinationFolder,
            MoveCollisionBehavior collisionBehavior,
            IProgress<FileOperationProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<FileOperationResult> RenameAsync(
            FileActionTarget target,
            string newName,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public void OpenFile(string path) => OpenedPaths.Add(path);

        public void RevealInExplorer(string path) => RevealedPaths.Add(path);
    }

    private sealed partial class CancellationOnDisposeCoordinator : IAppOperationCoordinator
    {
        private Action? _requestCancellation;

        public AppOperationDescriptor? ActiveOperation { get; private set; }

        public bool LeaseDisposed { get; private set; }

        public event EventHandler? ActiveOperationChanged;

        public bool TryAcquire(
            AppOperationDescriptor descriptor,
            Action requestCancellation,
            out IAppOperationLease? lease)
        {
            ActiveOperation = descriptor;
            _requestCancellation = requestCancellation;
            lease = new CallbackLease(this);
            ActiveOperationChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }

        public void RequestCancellation() => _requestCancellation?.Invoke();

        public Task WaitForIdleAsync() => ActiveOperation is null
            ? Task.CompletedTask
            : Task.Delay(Timeout.InfiniteTimeSpan);

        private sealed partial class CallbackLease : IAppOperationLease
        {
            private readonly CancellationOnDisposeCoordinator _owner;

            public CallbackLease(CancellationOnDisposeCoordinator owner)
            {
                _owner = owner;
            }

            public void Dispose()
            {
                _owner.RequestCancellation();
                _owner._requestCancellation = null;
                _owner.ActiveOperation = null;
                _owner.LeaseDisposed = true;
                _owner.ActiveOperationChanged?.Invoke(_owner, EventArgs.Empty);
            }
        }
    }

    [Fact]
    public void PresetIndexMirrorsPresetAndIgnoresInvalidSelections()
    {
        VideoOptimizerViewModel viewModel = CreateViewModel();
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal((int)VideoOptimizationPreset.Balanced, viewModel.PresetIndex);

        viewModel.PresetIndex = (int)VideoOptimizationPreset.HighQuality;
        viewModel.PresetIndex = -1;

        Assert.Equal(VideoOptimizationPreset.HighQuality, viewModel.Preset);
        Assert.Equal((int)VideoOptimizationPreset.HighQuality, viewModel.PresetIndex);
        Assert.Contains(nameof(VideoOptimizerViewModel.PresetIndex), changed);
    }

    [Fact]
    public async Task ProgressAndQueueSectionsAreHiddenUntilTheyHaveContent()
    {
        VideoOptimizerViewModel viewModel = CreateViewModel(
            inventoryBuilder: (_, _) => Inventory(Video(@"C:\Media\visible.mp4", 500)));
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.ProgressVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Collapsed, viewModel.QueueVisibility);

        await viewModel.RefreshQueueCommand.ExecuteAsync(null);
        viewModel.IsOptimizing = true;

        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.QueueVisibility);
        Assert.Equal(Microsoft.UI.Xaml.Visibility.Visible, viewModel.ProgressVisibility);
        Assert.Contains(nameof(VideoOptimizerViewModel.QueueVisibility), changed);
        Assert.Contains(nameof(VideoOptimizerViewModel.ProgressVisibility), changed);
    }
}
