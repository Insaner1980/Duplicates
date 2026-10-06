using System.Reflection;
using System.Collections.Concurrent;
using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class SettingsServiceTests
{
    [Fact]
    public void ConstructorAcceptsAnInjectedSettingsPath()
    {
        ConstructorInfo? constructor = typeof(SettingsService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string)],
            modifiers: null);

        Assert.NotNull(constructor);
    }

    [Fact]
    public void ServiceExposesCancelableSaveAndTestCommitHook()
    {
        MethodInfo? save = typeof(SettingsService).GetMethod(
            nameof(SettingsService.SaveAsync),
            [typeof(AppSettings), typeof(CancellationToken)]);
        ConstructorInfo? hookConstructor = typeof(SettingsService).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(string), typeof(Func<long, string, CancellationToken, Task>)],
            modifiers: null);

        Assert.NotNull(save);
        Assert.True(save.GetParameters()[1].HasDefaultValue);
        Assert.NotNull(hookConstructor);
    }

    [Fact]
    public async Task LoadNormalizesEveryPersistedEnumAndScalar()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(
                path,
                """
                {
                  "themeMode": 999,
                  "backdropMode": 999,
                  "deletionMode": 999,
                  "defaultImageSimilarity": 999,
                  "defaultVideoSimilarity": 999,
                  "defaultMinSizeBytes": -1,
                  "defaultLargeFileMinimumBytes": -1,
                  "defaultTemporaryFileMinimumAgeDays": 999,
                  "maxMediaConcurrency": 3,
                  "maxHashingConcurrency": 3
                }
                """, TestContext.Current.CancellationToken);
            var service = new SettingsService(path);

            await service.LoadAsync();

            Assert.Equal(AppThemeMode.System, service.Current.ThemeMode);
            Assert.Equal(BackdropMode.MicaAlt, service.Current.BackdropMode);
            Assert.Equal(DeletionMode.RecycleBin, service.Current.DeletionMode);
            Assert.Equal(SimilarityPreset.Balanced, service.Current.DefaultImageSimilarity);
            Assert.Equal(SimilarityPreset.Balanced, service.Current.DefaultVideoSimilarity);
            Assert.Equal(0, service.Current.DefaultMinSizeBytes);
            Assert.Equal(0, service.Current.DefaultLargeFileMinimumBytes);
            Assert.Equal(365, service.Current.DefaultTemporaryFileMinimumAgeDays);
            Assert.Null(service.Current.MaxMediaConcurrency);
            Assert.Null(service.Current.MaxHashingConcurrency);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("{\"themeMode\":")]
    [InlineData("not-json")]
    public async Task MalformedSettingsUseDefaultsWithoutOverwritingTheBadFile(string content)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            await File.WriteAllTextAsync(path, content, TestContext.Current.CancellationToken);
            var service = new SettingsService(path);

            await service.LoadAsync();

            Assert.Equal(new AppSettings(), service.Current);
            Assert.Equal(content, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewestFailedOrCancelledSaveKeepsPriorStateAndStalesPausedOlderSave(
        bool cancelNewest)
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            AppSettings initial = new() { DefaultMinSizeBytes = 10 };
            await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(initial), TestContext.Current.CancellationToken);
            var olderAtCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOlder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new SettingsService(
                path,
                async (revision, temporaryPath, cancellationToken) =>
                {
                    using (File.Open(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                    }

                    if (revision == 1)
                    {
                        olderAtCommit.SetResult();
                        await releaseOlder.Task.WaitAsync(cancellationToken);
                    }
                    else if (!cancelNewest)
                    {
                        throw new IOException("Injected newest save failure.");
                    }
                });
            await service.LoadAsync();
            int eventCount = 0;
            service.SettingsChanged += (_, _) => eventCount++;

            Task older = service.SaveAsync(initial with { DefaultMinSizeBytes = 20 }, TestContext.Current.CancellationToken);
            await olderAtCommit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            using var newestCancellation = new CancellationTokenSource();
            Task newest = service.SaveAsync(
                initial with { DefaultMinSizeBytes = 30 },
                newestCancellation.Token);
            if (cancelNewest)
            {
                newestCancellation.Cancel();
            }

            releaseOlder.SetResult();
            if (cancelNewest)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => newest);
            }
            else
            {
                await Assert.ThrowsAsync<IOException>(() => newest);
            }

            await older;

            Assert.Equal(initial, service.Current);
            Assert.Equal(0, eventCount);
            var reloaded = new SettingsService(path);
            await reloaded.LoadAsync();
            Assert.Equal(initial, reloaded.Current);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), file => file != path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void SaveRaisesTheCommittedEventOnTheCapturedSynchronizationContext()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var service = new SettingsService(
                Path.Combine(directory, "settings.json"),
                async (_, _, _) => await Task.Delay(10).ConfigureAwait(false));
            var context = new PumpSynchronizationContext();
            SynchronizationContext? eventContext = null;
            service.SettingsChanged += (_, _) => eventContext = SynchronizationContext.Current;

            context.Run(() => service.SaveAsync(new AppSettings { DefaultMinSizeBytes = 17 }));

            Assert.Same(context, eventContext);
            Assert.Equal(17, service.Current.DefaultMinSizeBytes);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RapidSuccessfulWritesCommitOnlyTheLatestRegisteredSnapshot()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var firstAtCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int eventCount = 0;
            SettingsService? service = null;
            service = new SettingsService(
                path,
                async (revision, temporaryPath, cancellationToken) =>
                {
                    using (File.Open(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.None))
                    {
                    }

                    if (revision == 1)
                    {
                        Assert.Equal(new AppSettings(), service!.Current);
                        Assert.Equal(0, eventCount);
                        firstAtCommit.SetResult();
                        await releaseFirst.Task.WaitAsync(cancellationToken);
                    }
                });
            service.SettingsChanged += (_, _) => eventCount++;
            Task first = service.SaveAsync(new AppSettings { DefaultMinSizeBytes = 1 }, TestContext.Current.CancellationToken);
            await firstAtCommit.Task;
            Task[] later = Enumerable.Range(2, 19)
                .Select(value => service.SaveAsync(new AppSettings
                {
                    DefaultMinSizeBytes = value,
                    DefaultTemporaryFileMinimumAgeDays = 0,
                }))
                .ToArray();

            releaseFirst.SetResult();
            await Task.WhenAll([first, .. later]);

            Assert.Equal(20, service.Current.DefaultMinSizeBytes);
            Assert.Equal(1, service.Current.DefaultTemporaryFileMinimumAgeDays);
            Assert.Equal(1, eventCount);
            var reloaded = new SettingsService(path);
            await reloaded.LoadAsync();
            Assert.Equal(service.Current, reloaded.Current);
            Assert.DoesNotContain(Directory.EnumerateFiles(directory), file => file != path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TemporaryCleanupFailureIsObservedAndReleasesWriteGate()
    {
        string directory = CreateTemporaryDirectory();
        FileStream? lockedTemporary = null;
        try
        {
            string path = Path.Combine(directory, "settings.json");
            var initial = new AppSettings { DefaultMinSizeBytes = 10 };
            string original = System.Text.Json.JsonSerializer.Serialize(initial);
            await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
            var atCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var service = new SettingsService(path, async (revision, temporaryPath, _) =>
            {
                if (revision == 1)
                {
                    lockedTemporary = File.Open(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                    atCommit.SetResult();
                    await release.Task;
                }
            });
            await service.LoadAsync();
            int eventCount = 0;
            service.SettingsChanged += (_, _) => eventCount++;

            Task older = service.SaveAsync(
                initial with { DefaultMinSizeBytes = 20 }, TestContext.Current.CancellationToken);
            await atCommit.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(
                initial with { DefaultMinSizeBytes = 30 }, cancellation.Token));
            release.SetResult();

            await Assert.ThrowsAsync<IOException>(() => older);
            Assert.Equal(original, await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(initial, service.Current);
            Assert.Equal(0, eventCount);

            lockedTemporary!.Dispose();
            lockedTemporary = null;
            await service.SaveAsync(
                    initial with { DefaultMinSizeBytes = 40 }, TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Equal(40, service.Current.DefaultMinSizeBytes);
            Assert.Equal(1, eventCount);
        }
        finally
        {
            lockedTemporary?.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Duplicates.SettingsTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class PumpSynchronizationContext : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = [];

        public override void Post(SendOrPostCallback d, object? state) => _work.Add((d, state));

        public void Run(Func<Task> operation)
        {
            SynchronizationContext? previous = Current;
            SetSynchronizationContext(this);
            try
            {
                Task task = operation();
                while (!task.IsCompleted)
                {
                    if (_work.TryTake(out var item, millisecondsTimeout: 100))
                    {
                        item.Callback(item.State);
                    }
                }

                task.GetAwaiter().GetResult();
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
