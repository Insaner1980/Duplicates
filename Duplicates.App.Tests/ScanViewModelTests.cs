using Duplicates.Engine;
using Duplicates.Engine.Models;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class ScanViewModelTests
{
    private static readonly string[] ExpectedCustomExtensions = [".mp3", ".png", ".txt"];

    [Theory]
    [InlineData(FileTypeCategory.Images, ".png")]
    [InlineData(FileTypeCategory.Video, ".mp4")]
    [InlineData(FileTypeCategory.Audio, ".wav")]
    [InlineData(FileTypeCategory.Documents, ".txt")]
    [InlineData(FileTypeCategory.Archives, ".zip")]
    [InlineData(FileTypeCategory.Code, ".cs")]
    public async Task StartScan_CategoryFilterIncludesOnlySelectedCategory(FileTypeCategory category, string extension)
    {
        string root = CreateTempRoot();
        try
        {
            foreach (string candidateExtension in new[] { ".png", ".mp4", ".wav", ".txt", ".zip", ".cs", ".bin" })
            {
                File.WriteAllText(Path.Combine(root, "candidate" + candidateExtension), "same");
            }

            string duplicatePath = Path.Combine(root, "duplicate" + extension);
            File.WriteAllText(duplicatePath, "same");
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var store = new ResultsStore();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope)
            {
                SelectedFileFilterIndex = 1,
                ImagesSelected = category == FileTypeCategory.Images,
                VideoSelected = category == FileTypeCategory.Video,
                AudioSelected = category == FileTypeCategory.Audio,
                DocumentsSelected = category == FileTypeCategory.Documents,
                ArchivesSelected = category == FileTypeCategory.Archives,
                CodeSelected = category == FileTypeCategory.Code,
            };

            await viewModel.StartScanCommand.ExecuteAsync(null);

            ScanResult result = Assert.IsType<ScanResult>(store.CurrentResult);
            DuplicateGroup group = Assert.Single(result.Groups);
            Assert.Equal(2, result.TotalFilesScanned);
            Assert.Equal(1, result.TotalDuplicateFiles);
            Assert.Equal(4, result.TotalReclaimableBytes);
            Assert.Equal(2, group.Files.Count);
            Assert.All(group.Files, file => Assert.Equal(extension, file.Extension));
            Assert.Contains(group.Files, file => file.FullPath == duplicatePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_CustomExtensionsAcceptMixedSeparatorsAndCase()
    {
        string root = CreateTempRoot();
        try
        {
            foreach (string extension in new[] { ".txt", ".png", ".mp3", ".bin" })
            {
                File.WriteAllText(Path.Combine(root, "candidate" + extension), "same");
            }

            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var store = new ResultsStore();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope)
            {
                SelectedFileFilterIndex = 2,
                CustomExtensionsText = "*.TXT; .PNG, mp3 ",
            };

            await viewModel.StartScanCommand.ExecuteAsync(null);

            ScanResult result = Assert.IsType<ScanResult>(store.CurrentResult);
            Assert.Equal(3, result.TotalFilesScanned);
            DuplicateGroup group = Assert.Single(result.Groups);
            Assert.Equal(ExpectedCustomExtensions, group.Files.Select(file => file.Extension).Order());
            Assert.Equal(2, result.TotalDuplicateFiles);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_PublishesCanonicalFilesExclusionsAndScopeOptions()
    {
        string root = CreateTempRoot();
        try
        {
            string folder = Directory.CreateDirectory(Path.Combine(root, "included")).FullName;
            string excluded = Path.Combine(folder, "excluded.txt");
            string explicitFile = Path.Combine(root, "explicit.txt");
            File.WriteAllText(excluded, "same");
            File.WriteAllText(Path.Combine(folder, "keep.txt"), "same");
            File.WriteAllText(explicitFile, "same");
            var scope = new PathScopeViewModel();
            scope.AddFolder(folder + Path.DirectorySeparatorChar);
            scope.AddFile(explicitFile);
            scope.ExcludePath(excluded);
            var store = new ResultsStore();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope)
            {
                IncludeSubfolders = false,
                IgnoreHiddenFiles = false,
                IgnoreSystemFiles = false,
            };

            await viewModel.StartScanCommand.ExecuteAsync(null);

            ExactResultsSession session = Assert.IsType<ExactResultsSession>(store.CurrentSession);
            Assert.Equal(new[] { folder }, session.Scope.IncludedFolders);
            Assert.Equal(new[] { explicitFile }, session.Scope.IncludedFiles);
            Assert.Equal(new[] { excluded }, session.Scope.ExcludedPaths);
            Assert.False(session.Scope.IncludeSubfolders);
            Assert.False(session.Scope.IgnoreHiddenFiles);
            Assert.False(session.Scope.IgnoreSystemFiles);
            Assert.Equal(2, session.Result.TotalFilesScanned);
            Assert.DoesNotContain(Assert.Single(session.Result.Groups).Files, file => file.FullPath == excluded);
            Assert.False(viewModel.IncludeSubfolders);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_InvalidSizeRangePreservesPriorResultAndReleasesOperation()
    {
        string root = CreateTempRoot();
        try
        {
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var store = new ResultsStore();
            var prior = new ScanResult
            {
                Groups = [],
                TotalFilesScanned = 7,
                TotalDuplicateFiles = 0,
                TotalReclaimableBytes = 0,
                Elapsed = TimeSpan.Zero,
                SkippedPaths = [],
            };
            store.SetResult(prior);
            var coordinator = new AppOperationCoordinator();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope, coordinator)
            {
                MinSizeValue = 100,
                MaxSizeValue = 10,
            };
            int completed = 0;
            viewModel.ScanCompleted += (_, _) => completed++;

            await viewModel.StartScanCommand.ExecuteAsync(null);

            Assert.Same(prior, store.CurrentResult);
            Assert.Equal(0, completed);
            Assert.Contains("Maximum size", viewModel.StatusMessage);
            Assert.True(viewModel.IsStatusOpen);
            Assert.Null(coordinator.ActiveOperation);
            Assert.False(viewModel.IsScanning);
            Assert.True(viewModel.StartScanCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StartScan_CompetingOperationKeepsExistingLeaseAndReportsBusy()
    {
        var coordinator = new AppOperationCoordinator();
        var descriptor = new AppOperationDescriptor(AppOperationKind.ExactResultsAction);
        Assert.True(coordinator.TryAcquire(descriptor, static () => { }, out IAppOperationLease? lease));
        using (lease!)
        {
            var store = new ResultsStore();
            var viewModel = new ScanViewModel(
                new DuplicateScanner(), new FakeSettingsService(), store, new PathScopeViewModel(), coordinator);

            await viewModel.StartScanCommand.ExecuteAsync(null);

            Assert.Same(descriptor, coordinator.ActiveOperation);
            Assert.Null(store.CurrentResult);
            Assert.Equal("Another operation is already running.", viewModel.StatusMessage);
            Assert.False(viewModel.IsScanning);
        }

        Assert.Null(coordinator.ActiveOperation);
    }

    [Fact]
    public void ScanPresentationTracksRunStateAndStatus()
    {
        var viewModel = new ScanViewModel(
            new DuplicateScanner(), new FakeSettingsService(), new ResultsStore(), new PathScopeViewModel());

        Assert.Equal(Visibility.Visible, viewModel.SetupVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.ProgressVisibility);
        Assert.False(viewModel.IsStatusOpen);

        viewModel.IsScanning = true;
        viewModel.StatusMessage = "Scanning";

        Assert.Equal(Visibility.Collapsed, viewModel.SetupVisibility);
        Assert.Equal(Visibility.Visible, viewModel.ProgressVisibility);
        Assert.True(viewModel.IsStatusOpen);
        viewModel.CancelScanCommand.Execute(null);
        Assert.True(viewModel.IsScanning);
    }

    [Fact]
    public async Task CancelScanCommandBeforeContinuationPreservesResultsAndAllowsNextScan()
    {
        string root = CreateTempRoot();
        try
        {
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var store = new ResultsStore();
            var coordinator = new AppOperationCoordinator();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope, coordinator);
            using var releaseProgress = new ManualResetEventSlim();
            var context = new QueuedScanProgressContext(() => viewModel.CancelScanCommand.Execute(null), releaseProgress);
            SynchronizationContext? previous = SynchronizationContext.Current;
            Task scan;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                scan = viewModel.StartScanCommand.ExecuteAsync(null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                releaseProgress.Set();
            }

            await scan;
            context.DrainProgress();

            Assert.Null(store.CurrentResult);
            Assert.Equal("Scan cancelled.", viewModel.StatusMessage);
            Assert.Null(coordinator.ActiveOperation);
            Assert.True(viewModel.StartScanCommand.CanExecute(null));
            viewModel.CancelScanCommand.Execute(null);
            Assert.Equal("Scan cancelled.", viewModel.StatusMessage);

            await viewModel.StartScanCommand.ExecuteAsync(null);

            Assert.NotNull(store.CurrentResult);
            Assert.Empty(viewModel.StatusMessage);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CompletedScanQueuedProgressCannotOverwriteLaterState()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates.ScanProgress.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), new ResultsStore(), scope);
            var context = new QueuedScanProgressContext();
            SynchronizationContext? previous = SynchronizationContext.Current;
            Task scan;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                scan = viewModel.StartScanCommand.ExecuteAsync(null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            await scan;
            Assert.True(context.HasProgress);
            viewModel.PhaseText = "Next scan";
            viewModel.CurrentFilePath = "new request";

            context.DrainProgress();

            Assert.Equal("Next scan", viewModel.PhaseText);
            Assert.Equal("new request", viewModel.CurrentFilePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CancellationBeforeScanContinuationDoesNotPublishCompletedResult()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates.ScanCancellation.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var scope = new PathScopeViewModel();
            scope.AddFolder(root);
            var store = new ResultsStore();
            var prior = new ScanResult
            {
                Groups = [],
                TotalFilesScanned = 7,
                TotalDuplicateFiles = 0,
                TotalReclaimableBytes = 0,
                Elapsed = TimeSpan.Zero,
                SkippedPaths = [],
            };
            store.SetResult(prior);
            var coordinator = new AppOperationCoordinator();
            var viewModel = new ScanViewModel(new DuplicateScanner(), new FakeSettingsService(), store, scope, coordinator);
            int completed = 0;
            viewModel.ScanCompleted += (_, _) => completed++;
            using var releaseProgress = new ManualResetEventSlim();
            var context = new QueuedScanProgressContext(coordinator.RequestCancellation, releaseProgress);
            SynchronizationContext? previous = SynchronizationContext.Current;
            Task scan;
            SynchronizationContext.SetSynchronizationContext(context);
            try
            {
                scan = viewModel.StartScanCommand.ExecuteAsync(null);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
                releaseProgress.Set();
            }

            await scan;
            context.DrainProgress();

            Assert.Same(prior, store.CurrentResult);
            Assert.Equal(0, completed);
            Assert.Equal("Scan cancelled.", viewModel.StatusMessage);
            Assert.Null(coordinator.ActiveOperation);
            Assert.False(viewModel.IsScanning);
            Assert.True(viewModel.StartScanCommand.CanExecute(null));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AddFolderCreatesReadablePresentationWithoutChangingCanonicalPath()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Duplicates-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "Documents", "ObsidianVault");
        Directory.CreateDirectory(folder);

        try
        {
            var scope = new PathScopeViewModel();
            var viewModel = new ScanViewModel(
                new DuplicateScanner(),
                new FakeSettingsService(),
                new ResultsStore(),
                scope);
            Assert.Same(scope, viewModel.PathScope);

            scope.AddFolder(folder);

            ScopePathViewModel item = Assert.Single(scope.IncludedPaths);
            Assert.Equal(Path.GetFullPath(folder), item.FullPath);
            Assert.Equal("ObsidianVault", item.DisplayName);
            Assert.Equal(Path.GetDirectoryName(Path.GetFullPath(folder)), item.ParentPath);
            Assert.True(scope.HasIncludedPaths);
            Assert.Equal(Visibility.Collapsed, scope.EmptyIncludedPathsVisibility);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void FolderCommandsUseCanonicalFullPathAndKeepOneSelection()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"Duplicates-{Guid.NewGuid():N}");
        string folder = Path.Combine(root, "ObsidianVault");
        Directory.CreateDirectory(folder);

        try
        {
            var scope = new PathScopeViewModel();
            var viewModel = new ScanViewModel(
                new DuplicateScanner(),
                new FakeSettingsService(),
                new ResultsStore(),
                scope);
            Assert.Same(scope, viewModel.PathScope);

            scope.AddFolder(folder);
            scope.AddFolder(folder + Path.DirectorySeparatorChar);

            ScopePathViewModel item = Assert.Single(scope.IncludedPaths);
            scope.RemoveIncludedPathCommand.Execute(item);

            Assert.Empty(scope.IncludedPaths);
            Assert.False(scope.HasIncludedPaths);
            Assert.Equal(Visibility.Visible, scope.EmptyIncludedPathsVisibility);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void SettingsChanged_UpdatesScanDefaultsWhenNotScanning()
    {
        var settings = new FakeSettingsService();
        var viewModel = new ScanViewModel(new DuplicateScanner(), settings, new ResultsStore(), new PathScopeViewModel());

        settings.SetCurrent(new AppSettings
        {
            DefaultMinSizeBytes = 2048,
            IgnoreHiddenFiles = false,
            IgnoreSystemFiles = false,
            VerifyByteByByte = false,
        });

        Assert.Equal(2048d, viewModel.MinSizeValue);
        Assert.False(viewModel.IgnoreHiddenFiles);
        Assert.False(viewModel.IgnoreSystemFiles);
        Assert.False(viewModel.VerifyByteByByte);
    }

    [Fact]
    public void SettingsChanged_DoesNotOverwriteActiveScanOptions()
    {
        var settings = new FakeSettingsService();
        var viewModel = new ScanViewModel(new DuplicateScanner(), settings, new ResultsStore(), new PathScopeViewModel())
        {
            IsScanning = true,
            MinSizeValue = 99d,
        };

        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 2048 });

        Assert.Equal(99d, viewModel.MinSizeValue);
    }

    [Fact]
    public void NewestPendingSettingsWaitForCoordinatorIdleAndScanTerminalState()
    {
        var settings = new FakeSettingsService();
        var coordinator = new AppOperationCoordinator();
        var scope = new PathScopeViewModel();
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            settings,
            new ResultsStore(),
            scope,
            coordinator)
        {
            IsScanning = true,
            MinSizeValue = 99,
        };
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.ExactScan),
            static () => { },
            out IAppOperationLease? lease));
        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 100, DefaultIncludeSubfolders = false });
        settings.SetCurrent(new AppSettings { DefaultMinSizeBytes = 200, DefaultIncludeSubfolders = true });

        lease!.Dispose();

        Assert.Equal(99, viewModel.MinSizeValue);
        Assert.True(scope.IncludeSubfolders);

        viewModel.IsScanning = false;

        Assert.Equal(200, viewModel.MinSizeValue);
        Assert.True(scope.IncludeSubfolders);
    }

    [Fact]
    public void SizePresets_SetNumberBoxValuesAndNoMaximum()
    {
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            new PathScopeViewModel());

        viewModel.UseOneKilobyteMinimumCommand.Execute(null);
        Assert.Equal(1024d, viewModel.MinSizeValue);

        viewModel.UseOneMegabyteMinimumCommand.Execute(null);
        Assert.Equal(1_048_576d, viewModel.MinSizeValue);

        viewModel.UseAnySizeCommand.Execute(null);
        Assert.Equal(0d, viewModel.MinSizeValue);
        Assert.True(double.IsNaN(viewModel.MaxSizeValue));
    }

    [Theory]
    [InlineData(-1d, 0L)]
    [InlineData(123.9d, 123L)]
    [InlineData(double.PositiveInfinity, 7L)]
    public void ByteSizeInput_NormalizesNumberBoxValues(double value, long expected)
    {
        Assert.Equal(expected, ByteSizeInput.ToBytes(value, fallback: 7L));
    }

    [Fact]
    public void ByteSizeInput_TreatsEmptyMaximumAsUnbounded()
    {
        Assert.Equal(
            long.MaxValue,
            ByteSizeInput.ToBytes(
                ByteSizeInput.NoMaximum,
                fallback: 7L,
                noValueMeansMaximum: true));
        Assert.True(double.IsNaN(ByteSizeInput.FromBytes(long.MaxValue)));
    }

    [Fact]
    public void FilterOptionVisibilityTracksSelectedMode()
    {
        var viewModel = new ScanViewModel(
            new DuplicateScanner(),
            new FakeSettingsService(),
            new ResultsStore(),
            new PathScopeViewModel());

        Assert.Equal(Visibility.Collapsed, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CustomExtensionsVisibility);

        viewModel.SelectedFileFilterIndex = 1;

        Assert.Equal(Visibility.Visible, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Collapsed, viewModel.CustomExtensionsVisibility);

        viewModel.SelectedFileFilterIndex = 2;

        Assert.Equal(Visibility.Collapsed, viewModel.CategoryFiltersVisibility);
        Assert.Equal(Visibility.Visible, viewModel.CustomExtensionsVisibility);
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates.ScanOptions.{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class QueuedScanProgressContext : SynchronizationContext
    {
        private readonly Action? _beforeContinuation;
        private readonly ManualResetEventSlim? _releaseProgress;

        public QueuedScanProgressContext(Action? beforeContinuation = null, ManualResetEventSlim? releaseProgress = null)
        {
            _beforeContinuation = beforeContinuation;
            _releaseProgress = releaseProgress;
        }

        private readonly System.Collections.Concurrent.ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _progress = new();

        public bool HasProgress => !_progress.IsEmpty;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            if (state is ScanProgress)
            {
                _progress.Enqueue((callback, state));
                _releaseProgress?.Wait(TestContext.Current.CancellationToken);
            }
            else
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    _beforeContinuation?.Invoke();
                    callback(state);
                });
            }
        }

        public void DrainProgress()
        {
            while (_progress.TryDequeue(out var item))
            {
                item.Callback(item.State);
            }
        }
    }
}
