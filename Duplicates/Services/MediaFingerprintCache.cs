using System.Text.Json;
using Duplicates.Engine.Analysis.Media;

namespace Duplicates.Services;

public sealed class MediaFingerprintCache
{
    private const int DocumentSchemaVersion = 1;
    private const int ImageSampleSchemaVersion = 1;
    private const int VideoSampleSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Func<string, CancellationToken, Task>? _beforeCommit;
    private readonly string _temporaryFilePrefix;
    private Dictionary<string, ImageCacheEntry> _imageEntries = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, VideoCacheEntry> _videoEntries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private long _generation;

    public MediaFingerprintCache(string? cachePath = null)
    {
        CachePath = Path.GetFullPath(cachePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Duplicates",
            "cache",
            "media-fingerprints-v1.json"));
        _temporaryFilePrefix = string.Concat(
            Path.GetFileName(CachePath),
            ".",
            Environment.ProcessId,
            ".",
            Guid.NewGuid().ToString("N"),
            ".");
    }

    internal MediaFingerprintCache(
        string cachePath,
        Func<string, CancellationToken, Task> beforeCommit)
        : this(cachePath)
    {
        _beforeCommit = beforeCommit ?? throw new ArgumentNullException(nameof(beforeCommit));
    }

    public string CachePath { get; }

    public async Task<ImageSample> GetOrCreateImageAsync(
        string path,
        Func<CancellationToken, Task<ImageSample>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(factory);
        string canonicalPath = Path.GetFullPath(path);
        FileSnapshot? before;
        long generation;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            before = TryGetSnapshot(canonicalPath);
            if (before is not null &&
                _imageEntries.TryGetValue(canonicalPath, out ImageCacheEntry? entry) &&
                EntryMatches(entry, before.Value))
            {
                return Clone(entry.Sample);
            }

            _imageEntries.Remove(canonicalPath);
            generation = _generation;
        }
        finally
        {
            _gate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        ImageSample sample = await factory(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(sample))
        {
            throw new InvalidDataException("The image sample is structurally invalid.");
        }

        if (before is null || TryGetSnapshot(canonicalPath) is not FileSnapshot after || after != before.Value)
        {
            return sample;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_generation != generation ||
                TryGetSnapshot(canonicalPath) is not FileSnapshot current ||
                current != before.Value)
            {
                return sample;
            }

            var candidateImages = new Dictionary<string, ImageCacheEntry>(
                _imageEntries,
                StringComparer.OrdinalIgnoreCase)
            {
                [canonicalPath] = new(
                    canonicalPath,
                    current.Length,
                    current.LastWriteUtcTicks,
                    ImageSampleSchemaVersion,
                    Clone(sample)),
            };
            var document = new CacheDocument
            {
                SchemaVersion = DocumentSchemaVersion,
                ImageEntries = candidateImages.Values.ToList(),
                VideoEntries = _videoEntries.Values.ToList(),
            };
            if (await TrySaveAsync(document, cancellationToken).ConfigureAwait(false))
            {
                _imageEntries = candidateImages;
            }

            return sample;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VideoSample> GetOrCreateVideoAsync(
        string path,
        Func<CancellationToken, Task<VideoSample>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(factory);
        string canonicalPath = Path.GetFullPath(path);
        FileSnapshot? before;
        long generation;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            before = TryGetSnapshot(canonicalPath);
            if (before is not null &&
                _videoEntries.TryGetValue(canonicalPath, out VideoCacheEntry? entry) &&
                EntryMatches(entry, before.Value))
            {
                return Clone(entry.Sample);
            }

            _videoEntries.Remove(canonicalPath);
            generation = _generation;
        }
        finally
        {
            _gate.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        VideoSample sample = await factory(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsValid(sample))
        {
            throw new InvalidDataException("The video sample is structurally invalid.");
        }

        if (before is null || TryGetSnapshot(canonicalPath) is not FileSnapshot after || after != before.Value)
        {
            return sample;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_generation != generation ||
                TryGetSnapshot(canonicalPath) is not FileSnapshot current ||
                current != before.Value)
            {
                return sample;
            }

            var candidateVideos = new Dictionary<string, VideoCacheEntry>(
                _videoEntries,
                StringComparer.OrdinalIgnoreCase)
            {
                [canonicalPath] = new(
                    canonicalPath,
                    current.Length,
                    current.LastWriteUtcTicks,
                    VideoSampleSchemaVersion,
                    Clone(sample)),
            };
            var document = new CacheDocument
            {
                SchemaVersion = DocumentSchemaVersion,
                ImageEntries = _imageEntries.Values.ToList(),
                VideoEntries = candidateVideos.Values.ToList(),
            };
            if (await TrySaveAsync(document, cancellationToken).ConfigureAwait(false))
            {
                _videoEntries = candidateVideos;
            }

            return sample;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _generation++;
            _imageEntries = new(StringComparer.OrdinalIgnoreCase);
            _videoEntries = new(StringComparer.OrdinalIgnoreCase);
            _loaded = true;
            try
            {
                File.Delete(CachePath);
            }
            catch (DirectoryNotFoundException)
            {
            }

            DeleteOwnedTemporaryFiles(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        var images = new Dictionary<string, ImageCacheEntry>(StringComparer.OrdinalIgnoreCase);
        var videos = new Dictionary<string, VideoCacheEntry>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(CachePath))
            {
                await using FileStream stream = new(
                    CachePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                CacheDocument? document = await JsonSerializer.DeserializeAsync<CacheDocument>(
                    stream,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                if (document?.SchemaVersion == DocumentSchemaVersion)
                {
                    foreach (ImageCacheEntry entry in document.ImageEntries ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (TryAccept(entry, out string? canonicalPath))
                        {
                            images[canonicalPath] = entry with
                            {
                                Path = canonicalPath,
                                Sample = Clone(entry.Sample),
                            };
                        }
                    }

                    foreach (VideoCacheEntry entry in document.VideoEntries ?? [])
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (TryAccept(entry, out string? canonicalPath))
                        {
                            videos[canonicalPath] = entry with
                            {
                                Path = canonicalPath,
                                Sample = Clone(entry.Sample),
                            };
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            images.Clear();
            videos.Clear();
        }

        cancellationToken.ThrowIfCancellationRequested();
        _imageEntries = images;
        _videoEntries = videos;
        _loaded = true;
    }

    private async Task<bool> TrySaveAsync(CacheDocument document, CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(CachePath);
        if (string.IsNullOrEmpty(directory))
        {
            return false;
        }

        string temporaryPath = Path.Combine(
            directory,
            string.Concat(_temporaryFilePrefix, Guid.NewGuid().ToString("N"), ".tmp"));
        try
        {
            Directory.CreateDirectory(directory);
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
                    document,
                    SerializerOptions,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                stream.Flush(flushToDisk: true);
            }

            if (_beforeCommit is not null)
            {
                await _beforeCommit(temporaryPath, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, CachePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(temporaryPath);
            throw;
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
            TryDelete(temporaryPath);
            return false;
        }
    }

    private void DeleteOwnedTemporaryFiles(CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(CachePath);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory, _temporaryFilePrefix + "*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(path);
        }
    }

    private static bool TryAccept(ImageCacheEntry? entry, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (entry is null ||
            entry.SampleSchemaVersion != ImageSampleSchemaVersion ||
            !IsValid(entry.Sample) ||
            !TryCanonicalize(entry.Path, out canonicalPath) ||
            TryGetSnapshot(canonicalPath) is not FileSnapshot snapshot)
        {
            return false;
        }

        return snapshot.Length == entry.Length && snapshot.LastWriteUtcTicks == entry.LastWriteUtcTicks;
    }

    private static bool TryAccept(VideoCacheEntry? entry, out string canonicalPath)
    {
        canonicalPath = string.Empty;
        if (entry is null ||
            entry.SampleSchemaVersion != VideoSampleSchemaVersion ||
            !IsValid(entry.Sample) ||
            !TryCanonicalize(entry.Path, out canonicalPath) ||
            TryGetSnapshot(canonicalPath) is not FileSnapshot snapshot)
        {
            return false;
        }

        return snapshot.Length == entry.Length && snapshot.LastWriteUtcTicks == entry.LastWriteUtcTicks;
    }

    private static bool EntryMatches(ImageCacheEntry entry, FileSnapshot snapshot) =>
        entry.SampleSchemaVersion == ImageSampleSchemaVersion &&
        entry.Length == snapshot.Length &&
        entry.LastWriteUtcTicks == snapshot.LastWriteUtcTicks &&
        IsValid(entry.Sample);

    private static bool EntryMatches(VideoCacheEntry entry, FileSnapshot snapshot) =>
        entry.SampleSchemaVersion == VideoSampleSchemaVersion &&
        entry.Length == snapshot.Length &&
        entry.LastWriteUtcTicks == snapshot.LastWriteUtcTicks &&
        IsValid(entry.Sample);

    private static bool IsValid(ImageSample? sample) =>
        sample is
        {
            Width: > 0,
            Height: > 0,
            Luminance32x32.Length: 1024,
        } &&
        !string.IsNullOrWhiteSpace(sample.Format);

    private static bool IsValid(VideoSample? sample)
    {
        if (sample is null ||
            sample.Width <= 0 ||
            sample.Height <= 0 ||
            !double.IsFinite(sample.DisplayAspectRatio) ||
            sample.DisplayAspectRatio <= 0 ||
            sample.Duration <= TimeSpan.Zero ||
            !double.IsFinite(sample.FramesPerSecond) ||
            sample.FramesPerSecond <= 0 ||
            string.IsNullOrWhiteSpace(sample.Codec) ||
            sample.LuminanceFrames32x32 is null ||
            sample.LuminanceFrames32x32.Count != 5)
        {
            return false;
        }

        var frames = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (byte[]? frame in sample.LuminanceFrames32x32)
        {
            if (frame?.Length != 1024 || !frames.Add(frame))
            {
                return false;
            }
        }

        return true;
    }

    private static ImageSample Clone(ImageSample sample) => sample with
    {
        Luminance32x32 = sample.Luminance32x32.ToArray(),
    };

    private static VideoSample Clone(VideoSample sample) => sample with
    {
        LuminanceFrames32x32 = sample.LuminanceFrames32x32
            .Select(frame => frame.ToArray())
            .ToArray(),
    };

    private static FileSnapshot? TryGetSnapshot(string path)
    {
        try
        {
            var info = new FileInfo(path);
            info.Refresh();
            return info.Exists
                ? new FileSnapshot(info.Length, info.LastWriteTimeUtc.Ticks)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            System.Security.SecurityException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool TryCanonicalize(string? path, out string canonicalPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                canonicalPath = string.Empty;
                return false;
            }

            canonicalPath = Path.GetFullPath(path);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            canonicalPath = string.Empty;
            return false;
        }
    }

    private static bool IsCacheFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            JsonException or NotSupportedException or ArgumentException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (IsCacheFailure(ex))
        {
        }
    }

    private sealed class CacheDocument
    {
        public int SchemaVersion { get; set; }

        public List<ImageCacheEntry>? ImageEntries { get; set; } = [];

        public List<VideoCacheEntry>? VideoEntries { get; set; } = [];
    }

    private sealed record ImageCacheEntry(
        string Path,
        long Length,
        long LastWriteUtcTicks,
        int SampleSchemaVersion,
        ImageSample Sample);

    private sealed record VideoCacheEntry(
        string Path,
        long Length,
        long LastWriteUtcTicks,
        int SampleSchemaVersion,
        VideoSample Sample);

    private readonly record struct FileSnapshot(long Length, long LastWriteUtcTicks);
}
