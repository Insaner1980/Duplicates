using System.Text;
using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;

namespace Duplicates.Engine.Tests;

public sealed class FileSignatureDetectorTests
{
    public static IEnumerable<TheoryDataRow<string, byte[], byte[], string[], string?>> SignatureCases()
    {
        yield return Case("JPEG", [0xFF, 0xD8, 0xFF], [0xFF, 0xD8, 0xFE], [".jpg", ".jpeg"], ".jpg");
        yield return Case("PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0B], [".png"], ".png");
        yield return Case("GIF", Bytes("GIF87a"), Bytes("GIF87b"), [".gif"], ".gif");
        yield return Case("GIF", Bytes("GIF89a"), Bytes("GIF89b"), [".gif"], ".gif");
        yield return Case("BMP", Bytes("BM"), Bytes("BN"), [".bmp"], ".bmp");
        yield return Case("TIFF", [0x49, 0x49, 0x2A, 0x00], [0x49, 0x49, 0x2A, 0x01], [".tif", ".tiff"], ".tiff");
        yield return Case("TIFF", [0x4D, 0x4D, 0x00, 0x2A], [0x4D, 0x4D, 0x00, 0x2B], [".tif", ".tiff"], ".tiff");
        yield return Case("WebP", Riff("WEBP"), Riff("WEBQ"), [".webp"], ".webp");
        yield return Case("PDF", Bytes("%PDF-"), Bytes("%PDF+"), [".pdf"], ".pdf");
        yield return Case("ZIP", [0x50, 0x4B, 0x03, 0x04], [0x50, 0x4B, 0x03, 0x05], ZipExtensions, ".zip");
        yield return Case("ZIP", [0x50, 0x4B, 0x05, 0x06], [0x50, 0x4B, 0x05, 0x07], ZipExtensions, ".zip");
        yield return Case("ZIP", [0x50, 0x4B, 0x07, 0x08], [0x50, 0x4B, 0x07, 0x09], ZipExtensions, ".zip");
        yield return Case("RAR", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00], [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x02], [".rar"], ".rar");
        yield return Case("RAR", [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00], [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x02, 0x00], [".rar"], ".rar");
        yield return Case("7z", [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C], [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1D], [".7z"], ".7z");
        yield return Case("MP3", Bytes("ID3"), Bytes("ID4"), [".mp3"], ".mp3");
        yield return Case("MP3", [0xFF, 0xFB, 0x90, 0x00], [0xFF, 0xF9, 0x90, 0x00], [".mp3"], ".mp3");
        yield return Case("FLAC", Bytes("fLaC"), Bytes("fLaD"), [".flac"], ".flac");
        yield return Case("WAV", Riff("WAVE"), Riff("WAVF"), [".wav"], ".wav");
        yield return Case("Ogg", Bytes("OggS"), Bytes("OggT"), [".ogg", ".oga", ".ogv", ".opus"], ".ogg");
        yield return Case("MP4", Ftyp("isom", "mp42"), NotFtyp("isom", "mp42"), [".mp4", ".m4a", ".m4v"], ".mp4");
        yield return Case("QuickTime", Ftyp("qt  "), NotFtyp("qt  "), [".mov"], ".mov");
        yield return Case("WebM", Ebml("webm"), CorruptEbml("webm"), [".webm"], ".webm");
        yield return Case("Matroska", Ebml("matroska"), CorruptEbml("matroska"), [".mkv", ".mka", ".mks", ".mk3d"], ".mkv");
        yield return Case("AVI", Riff("AVI "), Riff("AVI!"), [".avi"], ".avi");
    }

    public static IEnumerable<TheoryDataRow<byte[]>> MinimumHeaders() => SignatureCases()
        .Select(static item => new TheoryDataRow<byte[]>(item.Data.Item2));

    [Theory]
    [MemberData(nameof(SignatureCases))]
    public void Detect_MapsExactSignaturesAndRejectsRequiredByteChanges(
        string expectedName,
        byte[] positive,
        byte[] negative,
        string[] expectedExtensions,
        string? expectedRecommendation)
    {
        DetectedFileType detected = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(positive, "misleading.extension"));

        Assert.Equal(expectedName, detected.Name);
        Assert.Equal(expectedRecommendation, detected.RecommendedExtension);
        Assert.Equal(expectedExtensions.Length, detected.AllowedExtensions.Count);
        Assert.All(expectedExtensions, extension => Assert.Contains(extension, detected.AllowedExtensions));
        Assert.All(detected.AllowedExtensions, static extension =>
        {
            Assert.StartsWith(".", extension);
            Assert.Equal(extension.ToLowerInvariant(), extension);
        });
        if (detected.RecommendedExtension is not null)
        {
            Assert.Contains(detected.RecommendedExtension, detected.AllowedExtensions);
        }

        Assert.Null(FileSignatureDetector.Detect(negative, "matching.png"));
    }

    [Theory]
    [MemberData(nameof(MinimumHeaders))]
    public void Detect_AllPrefixTruncationsAreSafe(byte[] minimumHeader)
    {
        for (int length = 0; length < minimumHeader.Length; length++)
        {
            Assert.Null(FileSignatureDetector.Detect(minimumHeader.AsSpan(0, length), "anything.bin"));
        }
    }

    [Fact]
    public void Detect_RejectsRandomBytesAndInvalidMpegFields()
    {
        Assert.Null(FileSignatureDetector.Detect([0x10, 0x20, 0x30, 0x40, 0x50], "photo.jpg"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xEB, 0x90], "reserved-version.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xF9, 0x90], "reserved-layer.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xFB, 0x00], "free-bitrate.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xFB, 0xF0], "bad-bitrate.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xFB, 0x9C], "bad-sample-rate.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xFB, 0x90], "truncated-frame.mp3"));
        Assert.Null(FileSignatureDetector.Detect([0xFF, 0xFB, 0x90, 0x02], "reserved-emphasis.mp3"));
    }

    [Fact]
    public void Detect_ZipOfficeAndOpenDocumentExtensionsRemainAllowedContainers()
    {
        DetectedFileType type = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect([0x50, 0x4B, 0x03, 0x04], "report.docx"));

        Assert.Equal(".zip", type.RecommendedExtension);
        Assert.Contains(".docx", type.AllowedExtensions);
        Assert.Contains(".xlsb", type.AllowedExtensions);
        Assert.Contains(".odt", type.AllowedExtensions);
        Assert.Contains(".odf", type.AllowedExtensions);
    }

    [Fact]
    public void Detect_BmffUsesMajorAndCompatibleBrandsAndValidatesBoxBounds()
    {
        DetectedFileType compatibleMp4 = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(Ftyp("isom", "mp42"), "clip.bin"));
        DetectedFileType mixed = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(Ftyp("isom", "qt  "), "clip.bin"));
        DetectedFileType unknown = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(Ftyp("free"), "clip.bin"));
        DetectedFileType largeSize = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(LargeFtyp("qt  "), "clip.bin"));

        Assert.Equal(".mp4", compatibleMp4.RecommendedExtension);
        Assert.Null(mixed.RecommendedExtension);
        Assert.Null(unknown.RecommendedExtension);
        Assert.Equal([".m4a", ".m4v", ".mov", ".mp4"], mixed.AllowedExtensions.Order(StringComparer.Ordinal));
        Assert.Equal(mixed.AllowedExtensions.Order(StringComparer.Ordinal), unknown.AllowedExtensions.Order(StringComparer.Ordinal));
        Assert.Equal(".mov", largeSize.RecommendedExtension);
        Assert.Null(FileSignatureDetector.Detect(FtypWithDeclaredSize(12, "isom"), "clip.bin"));
        Assert.Null(FileSignatureDetector.Detect(FtypWithDeclaredSize(128, "isom"), "clip.bin"));
        Assert.Null(FileSignatureDetector.Detect(LargeFtyp("isom", declaredSize: 20), "clip.bin"));
    }

    [Fact]
    public void Detect_EbmlParsesVintBoundsAndNeverBlindSearchesPayloadText()
    {
        DetectedFileType missing = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(EbmlWithoutDocType(Bytes("webm")), "video.bin"));
        DetectedFileType unknown = Assert.IsType<DetectedFileType>(
            FileSignatureDetector.Detect(Ebml("other"), "video.bin"));

        Assert.Null(missing.RecommendedExtension);
        Assert.Null(unknown.RecommendedExtension);
        Assert.Equal([".mk3d", ".mka", ".mks", ".mkv", ".webm"], missing.AllowedExtensions.Order(StringComparer.Ordinal));
        Assert.Null(FileSignatureDetector.Detect([0x1A, 0x45, 0xDF, 0xA3, 0x8A, 0x42, 0x82, 0x84, 0x77], "truncated.bin"));
    }

    [Fact]
    public async Task DetectStreamAsync_NeverReadsByte65537()
    {
        await using var stream = new CountingStream(FileSignatureDetector.MaximumHeaderBytes + 1);

        DetectedFileType? result = await FileSignatureDetector.DetectStreamAsync(
            stream,
            "random.bin",
            CancellationToken.None);

        Assert.Null(result);
        Assert.Equal(FileSignatureDetector.MaximumHeaderBytes, stream.BytesRead);
        Assert.Equal(FileSignatureDetector.MaximumHeaderBytes, stream.LargestRequestedEnd);
    }

    [Fact]
    public async Task DetectStreamAsync_PropagatesCancellation()
    {
        await using var stream = new CountingStream(100);
        using var cancellationSource = new CancellationTokenSource();
        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await FileSignatureDetector.DetectStreamAsync(
                stream,
                "random.bin",
                cancellationSource.Token));
        Assert.Equal(0, stream.BytesRead);
    }

    private static readonly string[] ZipExtensions =
    [
        ".zip", ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb",
        ".xlam", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam", ".sldx", ".sldm",
        ".odt", ".ott", ".ods", ".ots", ".odp", ".otp", ".odg", ".otg", ".odf"
    ];

    private static TheoryDataRow<string, byte[], byte[], string[], string?> Case(
        string name,
        byte[] positive,
        byte[] negative,
        string[] allowedExtensions,
        string? recommendedExtension) =>
        new(name, positive, negative, allowedExtensions, recommendedExtension);

    private static byte[] Bytes(string text) => Encoding.ASCII.GetBytes(text);

    private static byte[] Riff(string formType) =>
        [.. Bytes("RIFF"), 0x04, 0x00, 0x00, 0x00, .. Bytes(formType)];

    private static byte[] Ftyp(string majorBrand, params string[] compatibleBrands)
    {
        int size = 16 + (compatibleBrands.Length * 4);
        return
        [
            .. UInt32BigEndian((uint)size), .. Bytes("ftyp"), .. Bytes(majorBrand), 0, 0, 0, 0,
            .. compatibleBrands.SelectMany(Bytes)
        ];
    }

    private static byte[] NotFtyp(string majorBrand, params string[] compatibleBrands)
    {
        byte[] bytes = Ftyp(majorBrand, compatibleBrands);
        bytes[7] = (byte)'q';
        return bytes;
    }

    private static byte[] FtypWithDeclaredSize(uint size, string majorBrand)
    {
        byte[] bytes = Ftyp(majorBrand);
        UInt32BigEndian(size).CopyTo(bytes, 0);
        return bytes;
    }

    private static byte[] LargeFtyp(string majorBrand, ulong? declaredSize = null)
    {
        const int actualSize = 24;
        return
        [
            0, 0, 0, 1, .. Bytes("ftyp"), .. UInt64BigEndian(declaredSize ?? actualSize),
            .. Bytes(majorBrand), 0, 0, 0, 0
        ];
    }

    private static byte[] Ebml(string docType)
    {
        byte[] docTypeBytes = Bytes(docType);
        int payloadLength = 3 + docTypeBytes.Length;
        return [0x1A, 0x45, 0xDF, 0xA3, (byte)(0x80 | payloadLength), 0x42, 0x82, (byte)(0x80 | docTypeBytes.Length), .. docTypeBytes];
    }

    private static byte[] CorruptEbml(string docType)
    {
        byte[] bytes = Ebml(docType);
        bytes[3] = 0xA4;
        return bytes;
    }

    private static byte[] EbmlWithoutDocType(byte[] payload) =>
        [0x1A, 0x45, 0xDF, 0xA3, (byte)(0x80 | (2 + payload.Length)), 0xEC, (byte)(0x80 | payload.Length), .. payload];

    private static byte[] UInt32BigEndian(uint value) =>
        [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] UInt64BigEndian(ulong value) =>
    [
        (byte)(value >> 56), (byte)(value >> 48), (byte)(value >> 40), (byte)(value >> 32),
        (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
    ];

    private sealed class CountingStream : Stream
    {
        private readonly long _length;

        public CountingStream(long length)
        {
            _length = length;
        }

        private long _position;

        public long BytesRead { get; private set; }

        public long LargestRequestedEnd { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LargestRequestedEnd = Math.Max(LargestRequestedEnd, _position + buffer.Length);
            int read = (int)Math.Min(buffer.Length, _length - _position);
            buffer.Span[..read].Fill(0x11);
            _position += read;
            BytesRead += read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
