using System.Text.Json;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed class SettingsService : ISettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string _settingsPath;
    private readonly Func<long, string, CancellationToken, Task>? _beforeCommit;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _revisionGate = new();
    private long _latestRegisteredRevision;

    public SettingsService()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Duplicates",
            "settings.json"))
    {
    }

    internal SettingsService(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = Path.GetFullPath(settingsPath);
    }

    internal SettingsService(
        string settingsPath,
        Func<long, string, CancellationToken, Task> beforeCommit)
        : this(settingsPath)
    {
        _beforeCommit = beforeCommit ?? throw new ArgumentNullException(nameof(beforeCommit));
    }

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? SettingsChanged;

    public async Task LoadAsync()
    {
        if (!File.Exists(_settingsPath))
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(_settingsPath);
            AppSettings? settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, SerializerOptions);
            Current = Normalize(settings ?? new AppSettings());
            SettingsChanged?.Invoke(this, Current);
        }
        catch (Exception ex) when (IsSettingsFailure(ex))
        {
            Current = Normalize(new AppSettings());
        }
    }

    public Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        AppSettings snapshot = Normalize(settings);
        long revision;
        lock (_revisionGate)
        {
            revision = ++_latestRegisteredRevision;
        }

        return SaveRevisionAsync(snapshot, revision, cancellationToken);
    }

    private async Task SaveRevisionAsync(
        AppSettings snapshot,
        long revision,
        CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = Path.GetDirectoryName(_settingsPath)!;
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                string.Concat(Path.GetFileName(_settingsPath), ".", Environment.ProcessId, ".", Guid.NewGuid().ToString("N"), ".tmp"));
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    snapshot,
                    SerializerOptions,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            if (_beforeCommit is not null)
            {
                await _beforeCommit(revision, temporaryPath, cancellationToken);
            }

            bool committed = false;
            lock (_revisionGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (revision == _latestRegisteredRevision)
                {
                    File.Move(temporaryPath, _settingsPath, overwrite: true);
                    temporaryPath = null;
                    Current = snapshot;
                    committed = true;
                }
            }

            if (committed)
            {
                SettingsChanged?.Invoke(this, snapshot);
            }
        }
        finally
        {
            try
            {
                if (temporaryPath is not null)
                {
                    File.Delete(temporaryPath);
                }
            }
            finally
            {
                _writeGate.Release();
            }
        }
    }

    private static AppSettings Normalize(AppSettings settings) => settings with
    {
        ThemeMode = Enum.IsDefined(settings.ThemeMode) ? settings.ThemeMode : AppThemeMode.System,
        BackdropMode = Enum.IsDefined(settings.BackdropMode) ? settings.BackdropMode : BackdropMode.MicaAlt,
        DefaultMinSizeBytes = Math.Max(0, settings.DefaultMinSizeBytes),
        DefaultLargeFileMinimumBytes = Math.Max(0, settings.DefaultLargeFileMinimumBytes),
        DefaultTemporaryFileMinimumAgeDays = Math.Clamp(settings.DefaultTemporaryFileMinimumAgeDays, 1, 365),
        DefaultImageSimilarity = Enum.IsDefined(settings.DefaultImageSimilarity)
            ? settings.DefaultImageSimilarity
            : SimilarityPreset.Balanced,
        DefaultVideoSimilarity = Enum.IsDefined(settings.DefaultVideoSimilarity)
            ? settings.DefaultVideoSimilarity
            : SimilarityPreset.Balanced,
        MaxMediaConcurrency = settings.MaxMediaConcurrency is 1 or 2 or 4
            ? settings.MaxMediaConcurrency
            : null,
        MaxHashingConcurrency = settings.MaxHashingConcurrency is 1 or 2 or 4 or 8
            ? settings.MaxHashingConcurrency
            : null,
        DeletionMode = Enum.IsDefined(settings.DeletionMode) ? settings.DeletionMode : DeletionMode.RecycleBin
    };

    private static bool IsSettingsFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            JsonException or NotSupportedException;
}
