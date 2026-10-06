using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;

namespace Duplicates.App.Tests;

public sealed class SettingsViewModelTests
{
    [Fact]
    public void NewToolSettingsHaveBackwardCompatibleDefaults()
    {
        var settings = new AppSettings();

        Assert.True(ReadSetting<bool>(settings, "DefaultIncludeSubfolders"));
        Assert.Equal(1_073_741_824L, ReadSetting<long>(settings, "DefaultLargeFileMinimumBytes"));
        Assert.Equal(7, ReadSetting<int>(settings, "DefaultTemporaryFileMinimumAgeDays"));
        Assert.Equal(SimilarityPreset.Balanced, ReadSetting<SimilarityPreset>(settings, "DefaultImageSimilarity"));
        Assert.Equal(SimilarityPreset.Balanced, ReadSetting<SimilarityPreset>(settings, "DefaultVideoSimilarity"));
        Assert.Null(ReadNullableSetting(settings, "MaxMediaConcurrency"));
        Assert.True(ReadSetting<bool>(settings, "UseMediaFingerprintCache"));
        Assert.Equal(0, (int)SimilarityPreset.Strict);
        Assert.Equal(1, (int)SimilarityPreset.Balanced);
        Assert.Equal(2, (int)SimilarityPreset.Broad);
    }

    [Fact]
    public async Task PropertyChangesCaptureCompleteSnapshotsAndObserveSaveFailures()
    {
        var releaseSaves = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var settings = new FakeSettingsService
        {
            SaveHandler = async (_, _) =>
            {
                await releaseSaves.Task;
                throw new IOException("Injected save failure.");
            },
        };
        var viewModel = new SettingsViewModel(settings);

        SetProperty(viewModel, "DefaultIncludeSubfolders", false);
        SetProperty(viewModel, "DefaultLargeFileMinimumValue", 42d);

        Assert.Equal(2, settings.SavedSettings.Count);
        Assert.False(settings.SavedSettings[0].DefaultIncludeSubfolders);
        Assert.Equal(1_073_741_824, settings.SavedSettings[0].DefaultLargeFileMinimumBytes);
        Assert.False(settings.SavedSettings[1].DefaultIncludeSubfolders);
        Assert.Equal(42, settings.SavedSettings[1].DefaultLargeFileMinimumBytes);
        releaseSaves.SetResult();
        await WaitUntilAsync(() => ReadProperty<bool>(viewModel, "IsSettingsInfoOpen"));
        Assert.Equal("Could not save settings.", ReadProperty<string>(viewModel, "SettingsInfoMessage"));
    }

    [Fact]
    public async Task UnexpectedSaveFailureIsObservedAndSurfaced()
    {
        var settings = new FakeSettingsService
        {
            SaveHandler = (_, _) => throw new InvalidOperationException("Injected failure."),
        };
        var viewModel = new SettingsViewModel(settings);

        viewModel.ConfirmBeforeDelete = false;

        await WaitUntilAsync(() => viewModel.IsSettingsInfoOpen);
        Assert.Equal("Could not save settings.", viewModel.SettingsInfoMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LatestSuccessfulSaveClearsPriorErrorAndIgnoresDelayedOlderFailure(
        bool failBeforeLatestRequest)
    {
        var older = new TaskCompletionSource();
        var newer = new TaskCompletionSource();
        int requestCount = 0;
        var settings = new FakeSettingsService
        {
            SaveHandler = (_, _) => ++requestCount == 1 ? older.Task : newer.Task,
        };
        var viewModel = new SettingsViewModel(settings);
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            viewModel.ConfirmBeforeDelete = false;
            if (failBeforeLatestRequest)
            {
                older.SetException(new IOException("Earlier save failure."));
                Assert.True(viewModel.IsSettingsInfoOpen);
            }

            viewModel.IgnoreHiddenFiles = false;
            AppSettings latest = settings.SavedSettings[1];
            settings.SetCurrent(latest);
            newer.SetResult();
            if (!failBeforeLatestRequest)
            {
                older.SetException(new IOException("Delayed earlier save failure."));
            }

            Assert.False(viewModel.IsSettingsInfoOpen);
            Assert.Equal(latest, settings.Current);
            Assert.False(viewModel.ConfirmBeforeDelete);
            Assert.False(viewModel.IgnoreHiddenFiles);
            Assert.Equal(2, requestCount);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public void ConstructorAcceptsSharedCacheControlAndCoordinator()
    {
        System.Reflection.ConstructorInfo? constructor = typeof(SettingsViewModel).GetConstructor(
            [typeof(ISettingsService), typeof(IMediaFingerprintCacheControl), typeof(IAppOperationCoordinator)]);

        Assert.NotNull(constructor);
    }

    [Fact]
    public void CacheControlSurfaceIsObservableFromTheSettingsPage()
    {
        Assert.NotNull(typeof(SettingsViewModel).GetProperty("CacheStatusText"));
        Assert.NotNull(typeof(SettingsViewModel).GetProperty("IsClearingCache"));
        Assert.NotNull(typeof(SettingsViewModel).GetMethod("RefreshCacheStatusAsync"));
        Assert.NotNull(typeof(SettingsViewModel).GetMethod(
            "ClearCacheAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic));
        Assert.NotNull(typeof(SettingsViewModel).GetProperty("ClearCacheCommand"));
    }

    [Fact]
    public async Task CacheRefreshRunsEveryTimeAndClearHonorsTheSharedOperationGate()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "cache.json"));
        var cache = new FakeCacheControl
        {
            Status = new MediaFingerprintCacheStatus(3, 4096, path),
        };
        var coordinator = new AppOperationCoordinator();
        var viewModel = new SettingsViewModel(new FakeSettingsService(), cache, coordinator);

        await viewModel.RefreshCacheStatusAsync();
        await viewModel.RefreshCacheStatusAsync();

        Assert.Equal(2, cache.StatusCallCount);
        Assert.Equal("3 cached items, 4 KB", viewModel.CacheStatusText);
        Assert.Equal(path, viewModel.CachePath);
        Assert.True(coordinator.TryAcquire(
            new AppOperationDescriptor(AppOperationKind.AnalysisRun, UsesMediaFingerprintCache: true),
            static () => { },
            out IAppOperationLease? lease));
        Assert.False(viewModel.ClearCacheCommand.CanExecute(null));
        System.Reflection.MethodInfo clearMethod = typeof(SettingsViewModel).GetMethod(
            "ClearCacheAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(clearMethod.Invoke(viewModel, null));
        Assert.Equal(0, cache.ClearCallCount);
        lease!.Dispose();

        cache.Status = new MediaFingerprintCacheStatus(0, 0, path);
        await viewModel.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal(1, cache.ClearCallCount);
        Assert.Equal(3, cache.StatusCallCount);
        Assert.Equal("0 cached items, 0 B", viewModel.CacheStatusText);
    }

    [Fact]
    public async Task UnexpectedClearFailureIsObservedAndSurfaced()
    {
        var cache = new FakeCacheControl
        {
            ClearHandler = _ => throw new InvalidOperationException("Injected clear failure."),
        };
        var viewModel = new SettingsViewModel(
            new FakeSettingsService(),
            cache,
            new AppOperationCoordinator());

        await viewModel.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal(1, cache.ClearCallCount);
        Assert.Equal("Could not clear media cache.", viewModel.SettingsInfoMessage);
        Assert.False(viewModel.IsClearingCache);
    }

    [Fact]
    public async Task FailedClearRefreshesTheActualCacheStateAndRemainsRetryable()
    {
        string path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "failed-clear-cache.json"));
        var cache = new FakeCacheControl
        {
            Status = new MediaFingerprintCacheStatus(3, 4096, path),
        };
        var viewModel = new SettingsViewModel(
            new FakeSettingsService(), cache, new AppOperationCoordinator());
        await viewModel.RefreshCacheStatusAsync();
        cache.ClearHandler = _ =>
        {
            cache.Status = new MediaFingerprintCacheStatus(0, 4096, path);
            throw new IOException("Injected final document deletion failure.");
        };

        await viewModel.ClearCacheCommand.ExecuteAsync(null);

        Assert.Equal("0 cached items, 4 KB", viewModel.CacheStatusText);
        Assert.Equal(path, viewModel.CachePath);
        Assert.Equal("Could not clear media cache.", viewModel.SettingsInfoMessage);
        Assert.False(viewModel.IsClearingCache);
        Assert.True(viewModel.ClearCacheCommand.CanExecute(null));

        cache.ClearHandler = _ =>
        {
            cache.Status = new MediaFingerprintCacheStatus(0, 0, path);
            return Task.CompletedTask;
        };
        await viewModel.ClearCacheCommand.ExecuteAsync(null);
        Assert.Equal("0 cached items, 0 B", viewModel.CacheStatusText);
        Assert.Equal(2, cache.ClearCallCount);
        Assert.False(viewModel.IsSettingsInfoOpen);
    }

    [Fact]
    public async Task ClearRejectsAConcurrentMethodEntry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new FakeCacheControl
        {
            ClearHandler = async _ =>
            {
                started.SetResult();
                await release.Task;
            },
        };
        var viewModel = new SettingsViewModel(
            new FakeSettingsService(),
            cache,
            new AppOperationCoordinator());

        Task first = viewModel.ClearCacheCommand.ExecuteAsync(null);
        await started.Task;
        Assert.True(viewModel.IsClearingCache);
        Assert.False(viewModel.ClearCacheCommand.CanExecute(null));
        System.Reflection.MethodInfo clearMethod = typeof(SettingsViewModel).GetMethod(
            "ClearCacheAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await Assert.IsAssignableFrom<Task>(clearMethod.Invoke(viewModel, null));
        Assert.Equal(1, cache.ClearCallCount);

        release.SetResult();
        await first;

        Assert.False(viewModel.IsClearingCache);
        Assert.True(viewModel.ClearCacheCommand.CanExecute(null));
    }

    [Fact]
    public void PermanentDeleteWarningVisibilityTracksDeletionMode()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        Assert.Equal(Visibility.Collapsed, viewModel.PermanentDeleteWarningVisibility);

        viewModel.SelectedDeletionMode = DeletionMode.Permanent;

        Assert.Equal(Visibility.Visible, viewModel.PermanentDeleteWarningVisibility);
    }

    [Fact]
    public void MinimumSizePresetCommandsUpdateDefaultMinSizeValue()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        viewModel.UseAnySizeDefaultCommand.Execute(null);
        Assert.Equal(0d, viewModel.DefaultMinSizeValue);

        viewModel.UseOneKilobyteDefaultCommand.Execute(null);
        Assert.Equal(1024d, viewModel.DefaultMinSizeValue);

        viewModel.UseOneMegabyteDefaultCommand.Execute(null);
        Assert.Equal(1_048_576d, viewModel.DefaultMinSizeValue);
    }

    [Theory]
    [InlineData(2048d, 2048L)]
    [InlineData(-1d, 0L)]
    public void DefaultMinimumSizeValue_IsNormalizedBeforeSaving(
        double input,
        long expected)
    {
        var settings = new FakeSettingsService();
        var viewModel = new SettingsViewModel(settings);

        viewModel.DefaultMinSizeValue = input;

        Assert.Equal(expected, settings.Current.DefaultMinSizeBytes);
    }

    [Fact]
    public void AboutTextIncludesAppAssemblyVersion()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());
        string version = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "unknown";

        Assert.Contains(version, viewModel.AboutText);
    }

    [Fact]
    public void AboutTextIncludesCurrentWindowsAppSdkVersion()
    {
        var viewModel = new SettingsViewModel(new FakeSettingsService());

        Assert.Contains("Windows App SDK 2.3", viewModel.AboutText);
    }

    private static T ReadSetting<T>(AppSettings settings, string propertyName)
    {
        object? value = typeof(AppSettings).GetProperty(propertyName)?.GetValue(settings);
        Assert.NotNull(value);
        return Assert.IsType<T>(value);
    }

    private static object? ReadNullableSetting(AppSettings settings, string propertyName)
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettings).GetProperty(propertyName);
        Assert.NotNull(property);
        return property.GetValue(settings);
    }

    private static void SetProperty<T>(SettingsViewModel viewModel, string propertyName, T value)
    {
        System.Reflection.PropertyInfo? property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        property.SetValue(viewModel, value);
    }

    private static T ReadProperty<T>(SettingsViewModel viewModel, string propertyName)
    {
        System.Reflection.PropertyInfo? property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsType<T>(property.GetValue(viewModel));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class FakeCacheControl : IMediaFingerprintCacheControl
    {
        public MediaFingerprintCacheStatus Status { get; set; } = new(0, 0, "C:\\cache.json");

        public int StatusCallCount { get; private set; }

        public int ClearCallCount { get; private set; }

        public Func<CancellationToken, Task>? ClearHandler { get; set; }

        public Task<MediaFingerprintCacheStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StatusCallCount++;
            return Task.FromResult(Status);
        }

        public Task ClearAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearCallCount++;
            return ClearHandler?.Invoke(cancellationToken) ?? Task.CompletedTask;
        }
    }
}
