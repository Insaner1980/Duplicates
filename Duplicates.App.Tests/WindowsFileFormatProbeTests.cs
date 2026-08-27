using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Services;
using Windows.Media.Editing;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

namespace Duplicates.App.Tests;

public sealed class WindowsFileFormatProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "Duplicates.App.Tests",
        Guid.NewGuid().ToString("N"));

    public WindowsFileFormatProbeTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    public static TheoryData<string, byte[]> GenericReadableFixtures => new()
    {
        { "unknown.bin", [1, 2, 3] },
        { "document.pdf", "%PDF-1.7"u8.ToArray() },
        { "archive.rar", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00] },
        { "archive.7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C] },
    };

    [Theory]
    [MemberData(nameof(GenericReadableFixtures))]
    public async Task ProbeAsync_GenericReadableTypesAreValidAndRemainUnchanged(string fileName, byte[] bytes)
    {
        string path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        DetectedFileType? detected = await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None);
        DateTime modified = File.GetLastWriteTimeUtc(path);

        FileProbeResult result = await new WindowsFileFormatProbe().ProbeAsync(
            path,
            detected,
            CancellationToken.None);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.Null(result.ErrorType);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task ProbeAsync_ValidZipEnumeratesCentralDirectoryWithoutExtracting()
    {
        string path = Path.Combine(_root, "valid.zip");
        await using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            ZipArchiveEntry entry = archive.CreateEntry("folder/item.txt");
            await using Stream writer = entry.Open();
            await writer.WriteAsync("content"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        DetectedFileType detected = Assert.IsType<DetectedFileType>(
            await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None));

        FileProbeResult result = await new WindowsFileFormatProbe().ProbeAsync(
            path,
            detected,
            CancellationToken.None);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.False(Directory.Exists(Path.Combine(_root, "folder")));
    }

    [Fact]
    public async Task ProbeAsync_ValidZip64CentralDirectoryIsValid()
    {
        string path = await WriteFixtureAsync("valid-zip64.zip", CreateValidZip64());
        await using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            Assert.Single(archive.Entries);
        }

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.Null(result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_CorruptZipReturnsStableCentralDirectoryFailure()
    {
        string path = Path.Combine(_root, "corrupt.zip");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3], TestContext.Current.CancellationToken);
        DetectedFileType detected = Assert.IsType<DetectedFileType>(
            await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None));

        FileProbeResult result = await new WindowsFileFormatProbe().ProbeAsync(
            path,
            detected,
            CancellationToken.None);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ZipCentralDirectoryFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_EncryptedZipFlagReturnsPasswordProtectedWithoutExtracting()
    {
        string path = Path.Combine(_root, "encrypted-flag.zip");
        await using (FileStream stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
        {
            ZipArchiveEntry entry = archive.CreateEntry("secret.txt");
            await using Stream writer = entry.Open();
            await writer.WriteAsync("secret"u8.ToArray(), TestContext.Current.CancellationToken);
        }

        byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        SetZipEncryptionFlags(bytes);
        await File.WriteAllBytesAsync(path, bytes, TestContext.Current.CancellationToken);
        DetectedFileType detected = Assert.IsType<DetectedFileType>(
            await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None));

        FileProbeResult result = await new WindowsFileFormatProbe().ProbeAsync(
            path,
            detected,
            CancellationToken.None);

        Assert.Equal(FileProbeStatus.UnsupportedOrProtected, result.Status);
        Assert.Equal("PasswordProtected", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_EncryptedFlagAfterMalformedCentralDirectoryBoundsIsInvalid()
    {
        byte[] bytes = CreateZipWithSingleEntry();
        int endRecordOffset = FindEndOfCentralDirectory(bytes);
        int centralDirectoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(endRecordOffset + 16)));
        bytes[centralDirectoryOffset + 8] |= 1;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(endRecordOffset + 12), 45);
        string path = await WriteFixtureAsync("malformed-central-directory.zip", bytes);

        await using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            Assert.Single(archive.Entries);
        }

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ZipCentralDirectoryFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_CentralDirectoryBeyondArchiveIsInvalid()
    {
        byte[] bytes = CreateZipWithSingleEntry();
        int endRecordOffset = FindEndOfCentralDirectory(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(endRecordOffset + 12), (uint)bytes.Length);
        string path = await WriteFixtureAsync("out-of-bounds-central-directory.zip", bytes);

        await using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            Assert.Single(archive.Entries);
        }

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ZipCentralDirectoryFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_OrdinaryCentralDirectoryOverlappingEndRecordIsInvalid()
    {
        byte[] bytes = CreateZipWithSingleEntry();
        int endRecordOffset = FindEndOfCentralDirectory(bytes);
        int centralDirectoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(endRecordOffset + 16)));
        uint centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(endRecordOffset + 12));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(centralDirectoryOffset + 32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(endRecordOffset + 12), centralDirectorySize + 1);
        string path = await WriteFixtureAsync("overlapping-end-record.zip", bytes);

        await using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            Assert.Single(archive.Entries);
        }

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ZipCentralDirectoryFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_Zip64CentralDirectoryOverlappingZip64EndRecordIsInvalid()
    {
        byte[] bytes = CreateValidZip64();
        int endRecordOffset = FindEndOfCentralDirectory(bytes);
        int locatorOffset = endRecordOffset - 20;
        int zip64EndRecordOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(locatorOffset + 8)));
        int centralDirectoryOffset = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(zip64EndRecordOffset + 48)));
        ulong centralDirectorySize = BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(zip64EndRecordOffset + 40));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(centralDirectoryOffset + 32), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(
            bytes.AsSpan(zip64EndRecordOffset + 40),
            centralDirectorySize + 1);
        string path = await WriteFixtureAsync("overlapping-zip64-end-record.zip", bytes);

        await using (FileStream stream = File.OpenRead(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false))
        {
            Assert.Single(archive.Entries);
        }

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ZipCentralDirectoryFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_ValidPngWindowsSmokeIsValid()
    {
        string path = await WriteFixtureAsync(
            "valid.png",
            Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.Null(result.ErrorType);
    }

    public static TheoryData<string, byte[]> MalformedImageFixtures => new()
    {
        { "malformed.png", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1] },
        { "malformed.gif", "GIF89a"u8.ToArray() },
        { "malformed.bmp", "BM"u8.ToArray() },
        { "malformed.webp", [.. "RIFF"u8.ToArray(), 4, 0, 0, 0, .. "WEBP"u8.ToArray()] },
    };

    [Theory]
    [MemberData(nameof(MalformedImageFixtures))]
    public async Task ProbeAsync_MalformedRecognizedImageIsInvalid(string fileName, byte[] bytes)
    {
        string path = await WriteFixtureAsync(fileName, bytes);

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("ImageDecodeFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_ValidWavWindowsSmokeIsValid()
    {
        string path = await WriteFixtureAsync("valid.wav", CreateValidWav());

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.Null(result.ErrorType);
    }

    public static TheoryData<string, byte[]> MalformedAudioFixtures => new()
    {
        { "malformed.mp3", [.. "ID3"u8.ToArray(), 4, 0, 0, 0, 0, 0, 0] },
        { "malformed.flac", "fLaC"u8.ToArray() },
        { "malformed.wav", [.. "RIFF"u8.ToArray(), 4, 0, 0, 0, .. "WAVE"u8.ToArray()] },
        { "malformed.ogg", "OggS"u8.ToArray() },
    };

    [Theory]
    [MemberData(nameof(MalformedAudioFixtures))]
    public async Task ProbeAsync_MalformedRecognizedAudioIsInvalid(string fileName, byte[] bytes)
    {
        string path = await WriteFixtureAsync(fileName, bytes);

        FileProbeResult result = await ProbeDetectedAsync(path);

        Assert.Equal(FileProbeStatus.Invalid, result.Status);
        Assert.Equal("MediaOpenFailure", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_GeneratedMp4AndMalformedMp4WindowsSmoke()
    {
        StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(_root);
        StorageFile validFile = await folder.CreateFileAsync("valid.mp4", CreationCollisionOption.ReplaceExisting);
        var composition = new MediaComposition();
        composition.Clips.Add(MediaClip.CreateFromColor(
            new Color { A = 255, R = 255 },
            TimeSpan.FromMilliseconds(250)));
        TranscodeFailureReason renderResult = await composition.RenderToFileAsync(validFile);
        Assert.Equal(TranscodeFailureReason.None, renderResult);
        string malformedPath = await WriteFixtureAsync(
            "malformed.mp4",
            [0, 0, 0, 16, .. "ftyp"u8.ToArray(), .. "isom"u8.ToArray(), 0, 0, 0, 0]);

        FileProbeResult valid = await ProbeDetectedAsync(validFile.Path);
        FileProbeResult malformed = await ProbeDetectedAsync(malformedPath);

        Assert.Equal(FileProbeStatus.Valid, valid.Status);
        Assert.Equal(FileProbeStatus.Invalid, malformed.Status);
        Assert.Equal("MediaOpenFailure", malformed.ErrorType);
    }

    [Fact]
    public void TryClassifyMedia_UnknownComFailureIsUnsupportedContainer()
    {
        MethodInfo classifier = typeof(WindowsFileFormatProbe).GetMethod(
            "TryClassifyMedia",
            BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new InvalidOperationException("TryClassifyMedia was not found.");
        object?[] arguments = [new COMException("Unknown media failure", unchecked((int)0x80004005)), null];

        bool classified = Assert.IsType<bool>(classifier.Invoke(null, arguments));
        FileProbeResult result = Assert.IsType<FileProbeResult>(arguments[1]);

        Assert.True(classified);
        Assert.Equal(FileProbeStatus.UnsupportedOrProtected, result.Status);
        Assert.Equal("UnsupportedContainer", result.ErrorType);
    }

    [Fact]
    public async Task ProbeAsync_PropagatesCancellation()
    {
        string path = Path.Combine(_root, "cancel.bin");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileFormatProbe().ProbeAsync(path, null, cancellationSource.Token));
    }

    private async Task<FileProbeResult> ProbeDetectedAsync(string path)
    {
        DetectedFileType detected = Assert.IsType<DetectedFileType>(
            await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None));
        return await new WindowsFileFormatProbe().ProbeAsync(path, detected, CancellationToken.None);
    }

    private async Task<string> WriteFixtureAsync(string fileName, byte[] bytes)
    {
        string path = Path.Combine(_root, fileName);
        await File.WriteAllBytesAsync(path, bytes);
        return path;
    }

    private static byte[] CreateValidWav()
    {
        byte[] bytes = new byte[45];
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 37);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(24), 8000);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), 8000);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(34), 8);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), 1);
        bytes[44] = 128;
        return bytes;
    }

    private static byte[] CreateValidZip64()
    {
        byte[] standardZip = CreateZipWithSingleEntry();
        int endRecordOffset = FindEndOfCentralDirectory(standardZip);
        ushort entryCount = BinaryPrimitives.ReadUInt16LittleEndian(standardZip.AsSpan(endRecordOffset + 10));
        uint centralDirectorySize = BinaryPrimitives.ReadUInt32LittleEndian(standardZip.AsSpan(endRecordOffset + 12));
        uint centralDirectoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(standardZip.AsSpan(endRecordOffset + 16));

        const int zip64EndRecordLength = 56;
        const int zip64LocatorLength = 20;
        const int endRecordLength = 22;
        byte[] zip64 = new byte[endRecordOffset + zip64EndRecordLength + zip64LocatorLength + endRecordLength];
        standardZip.AsSpan(0, endRecordOffset).CopyTo(zip64);

        int zip64EndRecordOffset = endRecordOffset;
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zip64EndRecordOffset), 0x06064B50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 4), 44);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 12), 45);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 14), 45);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 24), entryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 32), entryCount);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 40), centralDirectorySize);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64EndRecordOffset + 48), centralDirectoryOffset);

        int zip64LocatorOffset = zip64EndRecordOffset + zip64EndRecordLength;
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zip64LocatorOffset), 0x07064B50);
        BinaryPrimitives.WriteUInt64LittleEndian(zip64.AsSpan(zip64LocatorOffset + 8), (ulong)zip64EndRecordOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zip64LocatorOffset + 16), 1);

        int zipEndRecordOffset = zip64LocatorOffset + zip64LocatorLength;
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zipEndRecordOffset), 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zipEndRecordOffset + 8), ushort.MaxValue);
        BinaryPrimitives.WriteUInt16LittleEndian(zip64.AsSpan(zipEndRecordOffset + 10), ushort.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zipEndRecordOffset + 12), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(zip64.AsSpan(zipEndRecordOffset + 16), uint.MaxValue);
        return zip64;
    }

    private static byte[] CreateZipWithSingleEntry()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("item.txt");
            using Stream writer = entry.Open();
            writer.Write("content"u8);
        }

        return stream.ToArray();
    }

    private static int FindEndOfCentralDirectory(byte[] bytes)
    {
        const int endRecordLength = 22;
        for (int offset = bytes.Length - endRecordLength; offset >= 0; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset)) == 0x06054B50 &&
                offset + endRecordLength + BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 20)) ==
                    bytes.Length)
            {
                return offset;
            }
        }

        throw new InvalidDataException("ZIP end record is missing.");
    }

    private static void SetZipEncryptionFlags(byte[] bytes)
    {
        bool localHeaderFound = false;
        bool centralHeaderFound = false;
        for (int offset = 0; offset <= bytes.Length - sizeof(uint); offset++)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset));
            if (signature == 0x04034B50 && offset + 8 <= bytes.Length)
            {
                bytes[offset + 6] |= 1;
                localHeaderFound = true;
            }
            else if (signature == 0x02014B50 && offset + 10 <= bytes.Length)
            {
                bytes[offset + 8] |= 1;
                centralHeaderFound = true;
            }
        }

        Assert.True(localHeaderFound);
        Assert.True(centralHeaderFound);
    }
}
