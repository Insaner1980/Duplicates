using System.IO.Compression;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;
using Duplicates.Engine.Analysis.Media;
using Duplicates.Services;

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
        await File.WriteAllBytesAsync(path, bytes);
        DetectedFileType? detected = await FileSignatureDetector.DetectFileAsync(path, CancellationToken.None);
        DateTime modified = File.GetLastWriteTimeUtc(path);

        FileProbeResult result = await new WindowsFileFormatProbe().ProbeAsync(
            path,
            detected,
            CancellationToken.None);

        Assert.Equal(FileProbeStatus.Valid, result.Status);
        Assert.Null(result.ErrorType);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
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
            await writer.WriteAsync("content"u8.ToArray());
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
    public async Task ProbeAsync_CorruptZipReturnsStableCentralDirectoryFailure()
    {
        string path = Path.Combine(_root, "corrupt.zip");
        await File.WriteAllBytesAsync(path, [0x50, 0x4B, 0x03, 0x04, 1, 2, 3]);
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
    public async Task ProbeAsync_PropagatesCancellation()
    {
        string path = Path.Combine(_root, "cancel.bin");
        await File.WriteAllBytesAsync(path, [1]);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new WindowsFileFormatProbe().ProbeAsync(path, null, cancellationSource.Token));
    }
}
