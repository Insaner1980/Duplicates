using Duplicates.Engine.Analysis;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class ExifRemoverViewModelTests
{
    [Fact]
    public void Defaults_EnableEveryPrivacyCategoryKeepReplacementOffAndReuseSharedScope()
    {
        var scope = new PathScopeViewModel();
        ExifRemoverViewModel viewModel = CreateViewModel(pathScope: scope);

        Assert.Same(scope, viewModel.PathScope);
        Assert.True(viewModel.RemoveGps);
        Assert.True(viewModel.RemoveDeviceIdentifiers);
        Assert.True(viewModel.RemoveDates);
        Assert.True(viewModel.RemoveAuthorAndDescription);
        Assert.True(viewModel.RemoveEmbeddedThumbnail);
        Assert.True(viewModel.RemoveXmpAndIptc);
        Assert.False(viewModel.ReplaceOriginal);
    }

    [Fact]
    public async Task ReplaceOriginalWithoutExplicitConfirmationFailsClosedBeforeCoordinatorAndInventory()
    {
        var cleaner = new FakeExifCleanerService();
        var coordinator = new AppOperationCoordinator();
        bool inventoryCalled = false;
        int acquisitions = 0;
        coordinator.ActiveOperationChanged += (_, _) =>
        {
            if (coordinator.ActiveOperation is not null)
            {
                acquisitions++;
            }
        };
        ExifRemoverViewModel viewModel = CreateViewModel(
            cleaner,
            coordinator: coordinator,
            inventoryBuilder: (_, _) =>
            {
                inventoryCalled = true;
                return Inventory();
            });
        viewModel.ReplaceOriginal = true;

        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.False(inventoryCalled);
        Assert.Equal(0, acquisitions);
        Assert.Empty(cleaner.Requests);
        Assert.Null(coordinator.ActiveOperation);
        Assert.Contains("confirmation", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InventoryRunsOffCallerThread()
    {
        using var inventoryStarted = new ManualResetEventSlim();
        using var releaseInventory = new ManualResetEventSlim();
        int callerThread = Environment.CurrentManagedThreadId;
        int inventoryThread = callerThread;
        ExifRemoverViewModel viewModel = CreateViewModel(
            inventoryBuilder: (_, cancellationToken) =>
            {
                inventoryThread = Environment.CurrentManagedThreadId;
                inventoryStarted.Set();
                releaseInventory.Wait(cancellationToken);
                return Inventory();
            });

        Task cleaning = viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.True(inventoryStarted.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(cleaning.IsCompleted);
        Assert.NotEqual(callerThread, inventoryThread);
        releaseInventory.Set();
        await cleaning;
    }

    [Fact]
    public async Task SharedScopeInventoryFiltersSupportedImagesAndDeduplicatesOverlappingInputs()
    {
        string root = CreateTempRoot();
        try
        {
            string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
            string jpg = WriteFile(root, "one.JPG", "one");
            string jpeg = WriteFile(nested, "two.jpeg", "two");
            string tif = WriteFile(root, "three.tif", "three");
            string tiff = WriteFile(root, "four.TIFF", "four");
            _ = WriteFile(root, "not-supported.png", "png");

            var scope = new PathScopeViewModel();
            Assert.True(scope.AddFolder(root));
            Assert.True(scope.AddFolder(nested));
            Assert.True(scope.AddFile(jpg));
            var cleaner = new FakeExifCleanerService();
            var viewModel = new ExifRemoverViewModel(
                cleaner,
                scope,
                new RecordingFileActionService(),
                new AppOperationCoordinator());

            await viewModel.CleanImagesCommand.ExecuteAsync(false);

            string[] expected = [Path.GetFullPath(jpg), Path.GetFullPath(jpeg), Path.GetFullPath(tif), Path.GetFullPath(tiff)];
            Assert.Equal(
                expected.Order(StringComparer.OrdinalIgnoreCase),
                cleaner.Requests.Select(static request => request.SourcePath).Order(StringComparer.OrdinalIgnoreCase));
            Assert.Equal(expected.Length, cleaner.Requests.Count);
            foreach (ExifCleanRequest request in cleaner.Requests)
            {
                var file = new FileInfo(request.SourcePath);
                Assert.Equal(file.Length, request.ExpectedLength);
                Assert.Equal(file.LastWriteTimeUtc, request.ExpectedModifiedUtc);
                Assert.False(request.Options.ReplaceOriginal);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RequestsFreezeInventoryAndOptionSnapshotsBeforeTheFirstFileRuns()
    {
        DateTime firstModified = new(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc);
        DateTime secondModified = firstModified.AddMinutes(1);
        FileInventory inventory = Inventory(
            Image("C:\\Images\\one.jpg", 11, firstModified),
            Image("C:\\Images\\two.tiff", 22, secondModified));
        ExifRemoverViewModel? viewModel = null;
        var cleaner = new FakeExifCleanerService
        {
            Handler = (request, _, _) =>
            {
                if (request.SourcePath.EndsWith("one.jpg", StringComparison.OrdinalIgnoreCase))
                {
                    viewModel!.RemoveGps = false;
                    viewModel.RemoveDeviceIdentifiers = false;
                    viewModel.RemoveDates = false;
                    viewModel.RemoveAuthorAndDescription = false;
                    viewModel.RemoveEmbeddedThumbnail = false;
                    viewModel.RemoveXmpAndIptc = false;
                    viewModel.ReplaceOriginal = true;
                }

                return Task.FromResult(Succeeded(request.SourcePath));
            },
        };
        viewModel = CreateViewModel(cleaner, inventoryBuilder: (_, _) => inventory);

        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.Equal(2, cleaner.Requests.Count);
        Assert.All(cleaner.Requests, request => Assert.Equal(AllPrivacyDefaults, request.Options));
        Assert.Collection(
            cleaner.Requests,
            request =>
            {
                Assert.Equal(11, request.ExpectedLength);
                Assert.Equal(firstModified, request.ExpectedModifiedUtc);
            },
            request =>
            {
                Assert.Equal(22, request.ExpectedLength);
                Assert.Equal(secondModified, request.ExpectedModifiedUtc);
            });
    }

    [Fact]
    public async Task ProcessingIsSequentialAggregatesProgressAndContinuesOrdinaryFailures()
    {
        int activeCalls = 0;
        int maximumActiveCalls = 0;
        int call = 0;
        ExifRemoverViewModel? viewModel = null;
        var cleaner = new FakeExifCleanerService
        {
            Handler = async (request, progress, _) =>
            {
                int active = Interlocked.Increment(ref activeCalls);
                maximumActiveCalls = Math.Max(maximumActiveCalls, active);
                int currentCall = Interlocked.Increment(ref call);
                try
                {
                    progress?.Report(0.5);
                    double expectedAggregate = currentCall == 1 ? 25 : 75;
                    await WaitUntilAsync(() => Math.Abs(viewModel!.ProgressValue - expectedAggregate) < 0.01);
                    return currentCall == 1
                        ? new ExifCleanResult(ExifCleanOutcome.Failed, request.SourcePath, null, "Could not clean image.", [])
                        : Succeeded(request.SourcePath);
                }
                finally
                {
                    Interlocked.Decrement(ref activeCalls);
                }
            },
        };
        viewModel = CreateViewModel(
            cleaner,
            inventoryBuilder: (_, _) => Inventory(
                Image("C:\\Images\\one.jpg", 1),
                Image("C:\\Images\\two.jpeg", 2)));

        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.Equal(1, maximumActiveCalls);
        Assert.Equal(2, cleaner.Requests.Count);
        Assert.Equal([ExifCleanOutcome.Failed, ExifCleanOutcome.Succeeded], viewModel.Results.Select(static row => row.Outcome));
        Assert.Equal(100, viewModel.ProgressValue);
        Assert.Equal(2, viewModel.ProcessedCount);
    }

    [Fact]
    public async Task RecoveryRequiredStopsTheBatchAndPreservesCompletedRows()
    {
        int call = 0;
        var cleaner = new FakeExifCleanerService
        {
            Handler = (request, _, _) => Task.FromResult(++call == 1
                ? Succeeded(request.SourcePath)
                : new ExifCleanResult(
                    ExifCleanOutcome.RecoveryRequired,
                    request.SourcePath,
                    null,
                    "Manual recovery is required.",
                    ["C:\\Images\\rollback.tmp"])),
        };
        ExifRemoverViewModel viewModel = CreateViewModel(
            cleaner,
            inventoryBuilder: (_, _) => Inventory(
                Image("C:\\Images\\one.jpg", 1),
                Image("C:\\Images\\two.jpg", 2),
                Image("C:\\Images\\three.jpg", 3)));

        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.Equal(2, cleaner.Requests.Count);
        Assert.Equal(2, viewModel.Results.Count);
        Assert.Equal(ExifCleanOutcome.RecoveryRequired, viewModel.Results[1].Outcome);
        Assert.Equal(["C:\\Images\\rollback.tmp"], viewModel.Results[1].RecoveryPaths);
        Assert.Contains("recovery", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(viewModel.ProgressValue, 66.66, 66.67);
    }

    [Fact]
    public async Task CancellationPreservesCompletedRowsAndReleasesCoordinatorLease()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int call = 0;
        var cleaner = new FakeExifCleanerService
        {
            Handler = async (request, _, cancellationToken) =>
            {
                if (++call == 1)
                {
                    return Succeeded(request.SourcePath);
                }

                secondStarted.SetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                throw new InvalidOperationException("Unreachable");
            },
        };
        var coordinator = new AppOperationCoordinator();
        ExifRemoverViewModel viewModel = CreateViewModel(
            cleaner,
            coordinator: coordinator,
            inventoryBuilder: (_, _) => Inventory(
                Image("C:\\Images\\one.jpg", 1),
                Image("C:\\Images\\two.jpg", 2),
                Image("C:\\Images\\three.jpg", 3)));

        Task cleaning = viewModel.CleanImagesCommand.ExecuteAsync(false);
        await secondStarted.Task;

        Assert.Equal(AppOperationKind.ExifCleaning, coordinator.ActiveOperation?.Kind);
        Assert.False(viewModel.CleanImagesCommand.CanExecute(false));
        viewModel.CancelCleaningCommand.Execute(null);
        await cleaning;

        Assert.Single(viewModel.Results);
        Assert.Equal(ExifCleanOutcome.Succeeded, viewModel.Results[0].Outcome);
        Assert.Equal(2, cleaner.Requests.Count);
        Assert.Null(coordinator.ActiveOperation);
        Assert.False(viewModel.IsCleaning);
        Assert.Contains("cancelled", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StateNotificationFailureAfterLeaseAcquisitionRestoresIdleState()
    {
        var coordinator = new AppOperationCoordinator();
        ExifRemoverViewModel viewModel = CreateViewModel(
            coordinator: coordinator,
            inventoryBuilder: (_, _) => Inventory());
        bool notificationObserved = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ExifRemoverViewModel.IsCleaning) && viewModel.IsCleaning)
            {
                notificationObserved = true;
                throw new InvalidOperationException("State observer failed.");
            }
        };

        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        Assert.True(notificationObserved);
        Assert.Null(coordinator.ActiveOperation);
        Assert.True(coordinator.WaitForIdleAsync().IsCompletedSuccessfully);
        Assert.False(viewModel.IsCleaning);
        Assert.True(viewModel.CleanImagesCommand.CanExecute(false));
        viewModel.CancelCleaningCommand.Execute(null);
    }

    [Fact]
    public async Task OutputActionsAllowOnlySucceededRowsWithAnOutputPath()
    {
        var actions = new RecordingFileActionService();
        int call = 0;
        var cleaner = new FakeExifCleanerService
        {
            Handler = (request, _, _) => Task.FromResult(++call switch
            {
                1 => Succeeded(request.SourcePath),
                2 => new ExifCleanResult(ExifCleanOutcome.Succeeded, request.SourcePath, null, "Missing output.", []),
                _ => new ExifCleanResult(ExifCleanOutcome.Failed, request.SourcePath, request.SourcePath + ".unsafe", "Failed.", []),
            }),
        };
        ExifRemoverViewModel viewModel = CreateViewModel(
            cleaner,
            actions,
            inventoryBuilder: (_, _) => Inventory(
                Image("C:\\Images\\one.jpg", 1),
                Image("C:\\Images\\two.jpg", 2),
                Image("C:\\Images\\three.jpg", 3)));
        await viewModel.CleanImagesCommand.ExecuteAsync(false);

        ExifCleanResultViewModel valid = viewModel.Results[0];
        ExifCleanResultViewModel missingOutput = viewModel.Results[1];
        ExifCleanResultViewModel failed = viewModel.Results[2];
        Assert.True(viewModel.OpenOutputCommand.CanExecute(valid));
        Assert.True(viewModel.RevealOutputCommand.CanExecute(valid));
        Assert.False(viewModel.OpenOutputCommand.CanExecute(missingOutput));
        Assert.False(viewModel.OpenOutputCommand.CanExecute(failed));
        Assert.False(viewModel.RevealOutputCommand.CanExecute(missingOutput));
        Assert.False(viewModel.RevealOutputCommand.CanExecute(failed));
        Assert.Equal(Visibility.Visible, valid.OutputActionsVisibility);
        Assert.Equal(Visibility.Collapsed, missingOutput.OutputActionsVisibility);
        Assert.Equal(Visibility.Collapsed, failed.OutputActionsVisibility);

        viewModel.OpenOutputCommand.Execute(valid);
        viewModel.RevealOutputCommand.Execute(valid);

        Assert.Equal([valid.OutputPath!], actions.OpenedPaths);
        Assert.Equal([valid.OutputPath!], actions.RevealedPaths);
    }

    private static readonly ExifCleanOptions AllPrivacyDefaults = new(
        RemoveGps: true,
        RemoveDeviceIdentifiers: true,
        RemoveDates: true,
        RemoveAuthorAndDescription: true,
        RemoveEmbeddedThumbnail: true,
        RemoveXmpAndIptc: true,
        ReplaceOriginal: false);

    private static ExifRemoverViewModel CreateViewModel(
        FakeExifCleanerService? cleaner = null,
        RecordingFileActionService? fileActionService = null,
        PathScopeViewModel? pathScope = null,
        AppOperationCoordinator? coordinator = null,
        Func<AnalysisScope, CancellationToken, FileInventory>? inventoryBuilder = null)
    {
        pathScope ??= new PathScopeViewModel();
        if (!pathScope.HasIncludedPaths)
        {
            pathScope.AddFolder("C:\\ExifScope");
        }

        return new ExifRemoverViewModel(
            cleaner ?? new FakeExifCleanerService(),
            pathScope,
            fileActionService ?? new RecordingFileActionService(),
            coordinator ?? new AppOperationCoordinator(),
            inventoryBuilder ?? ((_, _) => Inventory()));
    }

    private static FileInventory Inventory(params InventoryFile[] files) => new(files, [], [], [], []);

    private static InventoryFile Image(string path, long size, DateTime? modifiedUtc = null) => new(
        Path.GetFullPath(path),
        Path.GetFileName(path),
        Path.GetExtension(path).ToLowerInvariant(),
        Path.GetDirectoryName(path)!,
        size,
        new DateTime(2026, 8, 7, 9, 0, 0, DateTimeKind.Utc),
        modifiedUtc ?? new DateTime(2026, 8, 7, 10, 0, 0, DateTimeKind.Utc),
        FileAttributes.Normal);

    private static ExifCleanResult Succeeded(string sourcePath) => new(
        ExifCleanOutcome.Succeeded,
        sourcePath,
        sourcePath + ".clean",
        "Image cleaned.",
        []);

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), $"Duplicates-Exif-VM-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static string WriteFile(string directory, string name, string content)
    {
        string path = Path.Combine(directory, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeExifCleanerService : IExifCleanerService
    {
        public Func<ExifCleanRequest, IProgress<double>?, CancellationToken, Task<ExifCleanResult>> Handler { get; set; } =
            static (request, progress, _) =>
            {
                progress?.Report(1);
                return Task.FromResult(Succeeded(request.SourcePath));
            };

        public List<ExifCleanRequest> Requests { get; } = [];

        public Task<ExifCleanResult> CleanAsync(
            ExifCleanRequest request,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Handler(request, progress, cancellationToken);
        }
    }

    private sealed class RecordingFileActionService : IFileActionService
    {
        public List<string> OpenedPaths { get; } = [];

        public List<string> RevealedPaths { get; } = [];

        public Task<DeleteSummary> DeleteAsync(
            IReadOnlyList<FileActionTarget> targets,
            IProgress<DeleteProgress>? progress,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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
}
