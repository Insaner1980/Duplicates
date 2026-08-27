using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
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
    private const int MfEUnsupportedBytestreamType = unchecked((int)0xC00D36C4);
    private const int MfETopoCodecNotFound = unchecked((int)0xC00D5212);
    private const int MfEDrmUnsupported = unchecked((int)0xC00D3700);
    private const int MfELicenseRequired = unchecked((int)0xC00D714A);
    private const int MfEInvalidFileFormat = unchecked((int)0xC00D36BE);
    private const int MfEEndOfStream = unchecked((int)0xC00D3E84);
    private const int WinCodecErrUnknownImageFormat = unchecked((int)0x88982F07);
    private const int WinCodecErrComponentNotFound = unchecked((int)0x88982F50);
    private const int WinCodecErrBadImage = unchecked((int)0x88982F60);
    private const int WinCodecErrBadHeader = unchecked((int)0x88982F61);
    private const int WinCodecErrFrameMissing = unchecked((int)0x88982F62);
    private const int WinCodecErrBadMetadataHeader = unchecked((int)0x88982F63);
    private const int WinCodecErrBadStreamData = unchecked((int)0x88982F70);
    private const int WinCodecErrStreamRead = unchecked((int)0x88982F72);
    private const int WinCodecErrUnsupportedPixelFormat = unchecked((int)0x88982F80);
    private const int WinCodecErrUnsupportedOperation = unchecked((int)0x88982F81);
    private const uint ZipCentralDirectoryHeaderSignature = 0x02014B50;
    private const uint ZipEndOfCentralDirectorySignature = 0x06054B50;
    private const uint Zip64EndOfCentralDirectorySignature = 0x06064B50;
    private const uint Zip64EndOfCentralDirectoryLocatorSignature = 0x07064B50;

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

            return HasEncryptedZipEntry(stream, cancellationToken)
                ? Unsupported("PasswordProtected")
                : Valid();
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
                    await source.OpenAsync().AsTask(cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!source.IsOpen || source.State == MediaSourceState.Failed)
                    {
                        return Invalid("MediaOpenFailure");
                    }
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
            WinCodecErrComponentNotFound or WinCodecErrUnsupportedPixelFormat => Unsupported("CodecUnavailable"),
            WinCodecErrUnsupportedOperation => Unsupported("UnsupportedContainer"),
            WinCodecErrUnknownImageFormat or WinCodecErrBadImage or WinCodecErrBadHeader or
                WinCodecErrFrameMissing or WinCodecErrBadMetadataHeader or WinCodecErrBadStreamData or
                WinCodecErrStreamRead or MfEInvalidFileFormat => Invalid("ImageDecodeFailure"),
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
            COMException => Unsupported("UnsupportedContainer"),
            _ => null,
        };
        return result is not null;
    }

    private static bool TryClassifyMedia(Exception exception, out FileProbeResult? result)
    {
        result = exception.HResult switch
        {
            MfETopoCodecNotFound => Unsupported("CodecUnavailable"),
            MfEUnsupportedBytestreamType => Unsupported("UnsupportedContainer"),
            MfEDrmUnsupported or MfELicenseRequired => Unsupported("PasswordProtected"),
            MfEInvalidFileFormat or MfEEndOfStream => Invalid("MediaOpenFailure"),
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
            COMException => Unsupported("UnsupportedContainer"),
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

    private static bool HasEncryptedZipEntry(Stream stream, CancellationToken cancellationToken)
    {
        const int endRecordLength = 22;
        const int maximumCommentLength = ushort.MaxValue;
        int tailLength = checked((int)Math.Min(stream.Length, endRecordLength + maximumCommentLength));
        byte[] tail = new byte[tailLength];
        stream.Position = stream.Length - tailLength;
        stream.ReadExactly(tail);

        int endRecordOffset = -1;
        for (int offset = tail.Length - endRecordLength; offset >= 0; offset--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(offset)) == ZipEndOfCentralDirectorySignature &&
                offset + endRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(offset + 20)) == tail.Length)
            {
                endRecordOffset = offset;
                break;
            }
        }

        if (endRecordOffset < 0)
        {
            throw new InvalidDataException();
        }

        long endRecordPosition = stream.Length - tailLength + endRecordOffset;
        ReadOnlySpan<byte> endRecord = tail.AsSpan(endRecordOffset, endRecordLength);
        ushort diskNumber = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[4..]);
        ushort centralDirectoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[6..]);
        ushort diskEntryCount = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[8..]);
        ushort entryCount16 = BinaryPrimitives.ReadUInt16LittleEndian(endRecord[10..]);
        uint centralDirectorySize32 = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[12..]);
        uint centralDirectoryOffset32 = BinaryPrimitives.ReadUInt32LittleEndian(endRecord[16..]);
        if (diskNumber != 0 ||
            centralDirectoryDisk != 0 ||
            diskEntryCount != entryCount16)
        {
            throw new NotSupportedException();
        }

        ulong entryCount = entryCount16;
        ulong centralDirectorySize = centralDirectorySize32;
        ulong centralDirectoryOffset = centralDirectoryOffset32;
        ulong centralDirectoryLimit = (ulong)endRecordPosition;
        if (entryCount16 == ushort.MaxValue ||
            centralDirectorySize32 == uint.MaxValue ||
            centralDirectoryOffset32 == uint.MaxValue)
        {
            (entryCount, centralDirectorySize, centralDirectoryOffset, centralDirectoryLimit) =
                ReadZip64EndRecord(stream, endRecordPosition);
        }

        if (centralDirectoryOffset > centralDirectoryLimit ||
            centralDirectorySize > centralDirectoryLimit - centralDirectoryOffset)
        {
            throw new InvalidDataException();
        }

        long centralDirectoryEnd = checked((long)(centralDirectoryOffset + centralDirectorySize));
        stream.Position = checked((long)centralDirectoryOffset);
        byte[] header = new byte[46];
        bool hasEncryptedEntry = false;
        for (ulong entryIndex = 0; entryIndex < entryCount; entryIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (centralDirectoryEnd - stream.Position < header.Length)
            {
                throw new InvalidDataException();
            }

            stream.ReadExactly(header);
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != ZipCentralDirectoryHeaderSignature)
            {
                throw new InvalidDataException();
            }

            int variableLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(30)) +
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(32));
            if (centralDirectoryEnd - stream.Position < variableLength)
            {
                throw new InvalidDataException();
            }

            hasEncryptedEntry |= (BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8)) & 1) != 0;
            stream.Seek(variableLength, SeekOrigin.Current);
        }

        if (stream.Position != centralDirectoryEnd)
        {
            throw new InvalidDataException();
        }

        return hasEncryptedEntry;
    }

    private static (
        ulong EntryCount,
        ulong CentralDirectorySize,
        ulong CentralDirectoryOffset,
        ulong EndRecordPosition) ReadZip64EndRecord(
        Stream stream,
        long endRecordPosition)
    {
        const int locatorLength = 20;
        const int minimumEndRecordLength = 56;
        long locatorPosition = endRecordPosition - locatorLength;
        if (locatorPosition < 0)
        {
            throw new InvalidDataException();
        }

        byte[] locator = new byte[locatorLength];
        stream.Position = locatorPosition;
        stream.ReadExactly(locator);
        if (BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64EndOfCentralDirectoryLocatorSignature)
        {
            throw new InvalidDataException();
        }

        uint recordDisk = BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(4));
        ulong recordPosition = BinaryPrimitives.ReadUInt64LittleEndian(locator.AsSpan(8));
        uint diskCount = BinaryPrimitives.ReadUInt32LittleEndian(locator.AsSpan(16));
        if (recordDisk != 0 || diskCount != 1)
        {
            throw new NotSupportedException();
        }

        if (recordPosition > (ulong)locatorPosition ||
            (ulong)locatorPosition - recordPosition < minimumEndRecordLength)
        {
            throw new InvalidDataException();
        }

        byte[] record = new byte[minimumEndRecordLength];
        stream.Position = checked((long)recordPosition);
        stream.ReadExactly(record);
        if (BinaryPrimitives.ReadUInt32LittleEndian(record) != Zip64EndOfCentralDirectorySignature)
        {
            throw new InvalidDataException();
        }

        ulong recordSize = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(4));
        if (recordSize < minimumEndRecordLength - 12 ||
            recordSize != (ulong)locatorPosition - recordPosition - 12)
        {
            throw new InvalidDataException();
        }

        uint diskNumber = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16));
        uint centralDirectoryDisk = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20));
        ulong diskEntryCount = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(24));
        ulong entryCount = BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(32));
        if (diskNumber != 0 || centralDirectoryDisk != 0 || diskEntryCount != entryCount)
        {
            throw new NotSupportedException();
        }

        return (
            entryCount,
            BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(40)),
            BinaryPrimitives.ReadUInt64LittleEndian(record.AsSpan(48)),
            recordPosition);
    }

    private static bool IsFileSystemFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or System.Security.SecurityException or
            ArgumentException or NotSupportedException or PathTooLongException;

    private static FileProbeResult Valid() => new(FileProbeStatus.Valid, null, null);

    private static FileProbeResult Invalid(string errorType) =>
        new(FileProbeStatus.Invalid, errorType, null);

    private static FileProbeResult Unsupported(string errorType) =>
        new(FileProbeStatus.UnsupportedOrProtected, errorType, null);
}
