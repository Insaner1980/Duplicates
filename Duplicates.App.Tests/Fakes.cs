using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;

namespace Duplicates.App.Tests;

internal sealed class FakeSettingsService : ISettingsService
{
    public AppSettings Current { get; private set; } = new();

    public List<AppSettings> SavedSettings { get; } = [];

    public Func<AppSettings, CancellationToken, Task>? SaveHandler { get; set; }

    public event EventHandler<AppSettings>? SettingsChanged;

    public Task LoadAsync()
    {
        SettingsChanged?.Invoke(this, Current);
        return Task.CompletedTask;
    }

    public Task SaveAsync(
        AppSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SavedSettings.Add(settings);
        if (SaveHandler is not null)
        {
            return SaveHandler(settings, cancellationToken);
        }

        SetCurrent(settings);
        return Task.CompletedTask;
    }

    public void SetCurrent(AppSettings settings)
    {
        Current = settings;
        SettingsChanged?.Invoke(this, Current);
    }
}

internal sealed class FakeFileActionService : IFileActionService
{
    public DeletionMode? LastDeletionMode { get; private set; }

    public DeleteSummary NextSummary { get; set; } = new(0, 0, []);

    public DeleteOperationCanceledException? NextDeleteCancellation { get; set; }

    public FileOperationSummary NextMoveSummary { get; set; } = new([], 0);

    public FileOperationCanceledException? NextMoveCancellation { get; set; }

    public FileOperationResult? NextRenameResult { get; set; }

    public int DeleteCallCount { get; private set; }

    public int MoveCallCount { get; private set; }

    public int RenameCallCount { get; private set; }

    public FileActionTarget? LastRenameTarget { get; private set; }

    public string? LastRenameName { get; private set; }

    public Func<FileActionTarget, string, CancellationToken, Task<FileOperationResult>>? RenameHandler { get; set; }

    public Func<IReadOnlyList<FileActionTarget>, CancellationToken, Task<DeleteSummary>>? DeleteHandler { get; set; }

    public Func<IReadOnlyList<FileActionTarget>, string, MoveCollisionBehavior, CancellationToken, Task<FileOperationSummary>>? MoveHandler { get; set; }

    public Action<IReadOnlyList<FileActionTarget>, IProgress<DeleteProgress>?>? OnDelete { get; set; }

    public Action<IReadOnlyList<FileActionTarget>, string, MoveCollisionBehavior, IProgress<FileOperationProgress>?>? OnMove { get; set; }
    public Action<string>? OnOpen { get; set; }
    public Action<string>? OnReveal { get; set; }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<FileActionTarget> targets,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken,
        DeletionMode? deletionMode = null)
    {
        DeleteCallCount++;
        LastDeletionMode = deletionMode;
        OnDelete?.Invoke(targets, progress);
        if (DeleteHandler is not null)
        {
            return DeleteHandler(targets, cancellationToken);
        }

        if (NextDeleteCancellation is not null)
        {
            throw NextDeleteCancellation;
        }

        return Task.FromResult(NextSummary);
    }

    public Task<FileOperationSummary> MoveAsync(
        IReadOnlyList<FileActionTarget> targets,
        string destinationFolder,
        MoveCollisionBehavior collisionBehavior,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        MoveCallCount++;
        OnMove?.Invoke(targets, destinationFolder, collisionBehavior, progress);
        if (MoveHandler is not null)
        {
            return MoveHandler(targets, destinationFolder, collisionBehavior, cancellationToken);
        }

        if (NextMoveCancellation is not null)
        {
            throw NextMoveCancellation;
        }

        return Task.FromResult(NextMoveSummary);
    }

    public Task<FileOperationResult> RenameAsync(
        FileActionTarget target,
        string newName,
        CancellationToken cancellationToken)
    {
        RenameCallCount++;
        LastRenameTarget = target;
        LastRenameName = newName;
        if (RenameHandler is not null)
        {
            return RenameHandler(target, newName, cancellationToken);
        }

        return Task.FromResult(NextRenameResult ?? new FileOperationResult(
            target.FullPath,
            Path.Combine(Path.GetDirectoryName(target.FullPath)!, newName),
            null));
    }

    public void OpenFile(string path)
    {
        OnOpen?.Invoke(path);
    }

    public void RevealInExplorer(string path)
    {
        OnReveal?.Invoke(path);
    }
}

internal sealed class FakeFileFormatProbe : IFileFormatProbe
{
    public Func<string, DetectedFileType?, CancellationToken, Task<FileProbeResult>> Handler { get; set; } =
        (_, _, _) => Task.FromResult(new FileProbeResult(FileProbeStatus.Valid, null, null));

    public List<(string Path, DetectedFileType? DetectedType)> Calls { get; } = [];

    public Task<FileProbeResult> ProbeAsync(
        string path,
        DetectedFileType? detectedType,
        CancellationToken cancellationToken)
    {
        Calls.Add((path, detectedType));
        return Handler(path, detectedType, cancellationToken);
    }
}

internal sealed class FakeImageSampleProvider : IImageSampleProvider
{
    public Func<string, CancellationToken, Task<ImageSample>> CachedHandler { get; set; } =
        (_, _) => Task.FromResult(new ImageSample(100, 100, new byte[1024], "JPEG"));

    public Func<string, CancellationToken, Task<ImageSample>> FreshHandler { get; set; } =
        (_, _) => Task.FromResult(new ImageSample(100, 100, new byte[1024], "JPEG"));

    public List<string> CachedPaths { get; } = [];

    public List<string> FreshPaths { get; } = [];

    public Task<ImageSample> GetSampleAsync(string path, CancellationToken cancellationToken)
    {
        CachedPaths.Add(path);
        return CachedHandler(path, cancellationToken);
    }

    public Task<ImageSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
    {
        FreshPaths.Add(path);
        return FreshHandler(path, cancellationToken);
    }
}

internal sealed class FakeVideoSampleProvider : IVideoSampleProvider
{
    public Func<string, CancellationToken, Task<VideoSample>> CachedHandler { get; set; } =
        (_, _) => Task.FromResult(Video());

    public Func<string, CancellationToken, Task<VideoSample>> FreshHandler { get; set; } =
        (_, _) => Task.FromResult(Video());

    public List<string> CachedPaths { get; } = [];

    public List<string> FreshPaths { get; } = [];

    public Task<VideoSample> GetSampleAsync(string path, CancellationToken cancellationToken)
    {
        CachedPaths.Add(path);
        return CachedHandler(path, cancellationToken);
    }

    public Task<VideoSample> GetFreshSampleAsync(string path, CancellationToken cancellationToken)
    {
        FreshPaths.Add(path);
        return FreshHandler(path, cancellationToken);
    }

    public static VideoSample Video(byte value = 0) => new(
        1920,
        1080,
        16d / 9d,
        TimeSpan.FromSeconds(10),
        1_000_000,
        30,
        "H264",
        Enumerable.Range(0, 5)
            .Select(index => Enumerable.Repeat((byte)(value + index), 1024).ToArray())
            .ToArray());
}

internal sealed class FakeMusicMetadataProvider : IMusicMetadataProvider
{
    public Func<string, CancellationToken, Task<MusicMetadata>> Handler { get; set; } =
        (_, _) => Task.FromResult(Music());

    public List<string> Paths { get; } = [];

    public Task<MusicMetadata> GetMetadataAsync(string path, CancellationToken cancellationToken)
    {
        Paths.Add(path);
        return Handler(path, cancellationToken);
    }

    public static MusicMetadata Music(
        string title = "Song",
        string artist = "Artist",
        string albumArtist = "Album Artist",
        string album = "Album",
        uint trackNumber = 1,
        uint year = 2025,
        IReadOnlyList<string>? genres = null,
        uint bitrate = 192_000,
        TimeSpan? duration = null) => new(
            title,
            artist,
            albumArtist,
            album,
            trackNumber,
            year,
            genres ?? ["Rock"],
            bitrate,
            duration ?? TimeSpan.FromSeconds(100));
}

internal sealed class FakeResultExportService : IResultExportService
{
    public ResultExportSnapshot? Snapshot { get; private set; }

    public ResultExportFormat? Format { get; private set; }

    public string? DestinationPath { get; private set; }

    public bool OverwriteExisting { get; private set; }

    public Task ExportAsync(
        ResultExportSnapshot snapshot,
        ResultExportFormat format,
        string destinationPath,
        CancellationToken cancellationToken,
        bool overwriteExisting = false)
    {
        Snapshot = snapshot;
        Format = format;
        DestinationPath = destinationPath;
        OverwriteExisting = overwriteExisting;
        return Task.CompletedTask;
    }
}
