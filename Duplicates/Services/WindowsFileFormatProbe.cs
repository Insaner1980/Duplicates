using System.IO.Compression;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Media;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Editing;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Duplicates.Services;

public sealed class WindowsFileFormatProbe : IFileFormatProbe
{
    private const int MfUnsupportedBytestreamType = unchecked((int)0xC00D36C4);
    private const int MfCodecNotFound = unchecked((int)0xC00D5212);
    private const int MfDrmUnsupported = unchecked((int)0xC00D3700);
    private const int MfLicenseRequired = unchecked((int)0xC00D714A);
    private const int MfInvalidFileFormat = unchecked((int)0xC00D36BE);
    private const int WicUnknownImageFormat = unchecked((int)0x88982F07);
    private const int WicComponentNotFound = unchecked((int)0x88982F50);
    private const int WicBadImage = unchecked((int)0x88982F60);
    private const int WicBadHeader = unchecked((int)0x88982F61);
    private const int WicFrameMissing = unchecked((int)0x88982F62);
    private const int WicUnsupportedOperation = unchecked((int)0x88982F81);

    public async Task<FileProbeResult> ProbeAsync(
        string path,
        DetectedFileType? detectedType,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] buffer = new byte[1];
            _ = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsFileSystemFailure(ex))
        {
            return Invalid("HeaderReadFailure");
        }

        cancellationToken.ThrowIfCancellationRequested();
        return detectedType?.Name switch
        {
            "JPEG" or "PNG" or "GIF" or "BMP" or "TIFF" or "WebP" =>
                await ProbeImageAsync(path, cancellationToken),
            "ZIP" => await ProbeZipAsync(path, cancellationToken).ConfigureAwait(false),
            "MP3" or "FLAC" or "WAV" or "Ogg" or "MP4" or "QuickTime" or "ISO BMFF" or
                "WebM" or "Matroska" or "EBML" or "AVI" =>
                await ProbeMediaAsync(path, detectedType.Name, cancellationToken),
            _ => Valid(),
        };
    }

    private static async Task<FileProbeResult> ProbeImageAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read);
            cancellationToken.ThrowIfCancellationRequested();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            cancellationToken.ThrowIfCancellationRequested();
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (TryClassifyImage(ex, out FileProbeResult? result))
        {
            return result!;
        }
    }

    private static async Task<FileProbeResult> ProbeZipAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.RandomAccess);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = entry.FullName;
            }

            return Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException)
        {
            return Invalid("ZipCentralDirectoryFailure");
        }
        catch (NotSupportedException)
        {
            return Unsupported("UnsupportedContainer");
        }
    }

    private static async Task<FileProbeResult> ProbeMediaAsync(
        string path,
        string detectedType,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            cancellationToken.ThrowIfCancellationRequested();
            if (IsAudio(path, detectedType))
            {
                MusicProperties properties = await file.Properties.GetMusicPropertiesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (properties.Duration == TimeSpan.Zero &&
                    properties.Bitrate == 0 &&
                    string.IsNullOrEmpty(properties.Title))
                {
                    using MediaSource source = MediaSource.CreateFromStorageFile(file);
                }
            }
            else
            {
                VideoProperties properties = await file.Properties.GetVideoPropertiesAsync();
                cancellationToken.ThrowIfCancellationRequested();
                if (properties.Duration == TimeSpan.Zero &&
                    properties.Bitrate == 0 &&
                    properties.Width == 0 &&
                    properties.Height == 0 &&
                    string.IsNullOrEmpty(properties.Title))
                {
                    _ = await MediaClip.CreateFromFileAsync(file);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Valid();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (TryClassifyMedia(ex, out FileProbeResult? result))
        {
            return result!;
        }
    }

    private static bool TryClassifyImage(Exception exception, out FileProbeResult? result)
    {
        result = exception.HResult switch
        {
            WicComponentNotFound => Unsupported("CodecUnavailable"),
            WicUnknownImageFormat or WicUnsupportedOperation => Unsupported("UnsupportedContainer"),
            WicBadImage or WicBadHeader or WicFrameMissing => Invalid("ImageDecodeFailure"),
            _ => null,
        };
        if (result is not null)
        {
            return true;
        }

        result = exception switch
        {
            InvalidDataException or ArgumentException => Invalid("ImageDecodeFailure"),
            NotSupportedException => Unsupported("UnsupportedContainer"),
            _ => null,
        };
        return result is not null;
    }

    private static bool TryClassifyMedia(Exception exception, out FileProbeResult? result)
    {
        result = exception.HResult switch
        {
            MfCodecNotFound => Unsupported("CodecUnavailable"),
            MfUnsupportedBytestreamType => Unsupported("UnsupportedContainer"),
            MfDrmUnsupported or MfLicenseRequired => Unsupported("PasswordProtected"),
            MfInvalidFileFormat => Invalid("MediaOpenFailure"),
            _ => null,
        };
        if (result is not null)
        {
            return true;
        }

        result = exception switch
        {
            InvalidDataException or ArgumentException => Invalid("MediaOpenFailure"),
            NotSupportedException => Unsupported("UnsupportedContainer"),
            _ => null,
        };
        return result is not null;
    }

    private static bool IsAudio(string path, string detectedType) => detectedType switch
    {
        "MP3" or "FLAC" or "WAV" => true,
        "Ogg" => !string.Equals(Path.GetExtension(path), ".ogv", StringComparison.OrdinalIgnoreCase),
        "MP4" or "ISO BMFF" => string.Equals(Path.GetExtension(path), ".m4a", StringComparison.OrdinalIgnoreCase),
        "Matroska" or "EBML" => string.Equals(Path.GetExtension(path), ".mka", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;

    private static FileProbeResult Valid() => new(FileProbeStatus.Valid, null, null);

    private static FileProbeResult Invalid(string errorType) =>
        new(FileProbeStatus.Invalid, errorType, null);

    private static FileProbeResult Unsupported(string errorType) =>
        new(FileProbeStatus.UnsupportedOrProtected, errorType, null);
}
