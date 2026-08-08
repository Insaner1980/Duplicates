using System.Buffers.Binary;
using System.Text;

namespace Duplicates.Engine.Analysis
{
    public sealed record DetectedFileType(
        string Name,
        IReadOnlySet<string> AllowedExtensions,
        string? RecommendedExtension);
}

namespace Duplicates.Engine.Analysis.Analyzers
{
    public static class FileSignatureDetector
    {
        public const int MaximumHeaderBytes = 64 * 1024;

        private static readonly DetectedFileType Jpeg = Type("JPEG", [".jpg", ".jpeg"], ".jpg");
        private static readonly DetectedFileType Png = Type("PNG", [".png"], ".png");
        private static readonly DetectedFileType Gif = Type("GIF", [".gif"], ".gif");
        private static readonly DetectedFileType Bmp = Type("BMP", [".bmp"], ".bmp");
        private static readonly DetectedFileType Tiff = Type("TIFF", [".tif", ".tiff"], ".tiff");
        private static readonly DetectedFileType WebP = Type("WebP", [".webp"], ".webp");
        private static readonly DetectedFileType Pdf = Type("PDF", [".pdf"], ".pdf");
        private static readonly DetectedFileType Zip = Type(
            "ZIP",
            [
                ".zip", ".docx", ".docm", ".dotx", ".dotm", ".xlsx", ".xlsm", ".xltx", ".xltm", ".xlsb",
                ".xlam", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppam", ".sldx", ".sldm",
                ".odt", ".ott", ".ods", ".ots", ".odp", ".otp", ".odg", ".otg", ".odf",
            ],
            ".zip");
        private static readonly DetectedFileType Rar = Type("RAR", [".rar"], ".rar");
        private static readonly DetectedFileType SevenZip = Type("7z", [".7z"], ".7z");
        private static readonly DetectedFileType Mp3 = Type("MP3", [".mp3"], ".mp3");
        private static readonly DetectedFileType Flac = Type("FLAC", [".flac"], ".flac");
        private static readonly DetectedFileType Wav = Type("WAV", [".wav"], ".wav");
        private static readonly DetectedFileType Ogg = Type("Ogg", [".ogg", ".oga", ".ogv", ".opus"], ".ogg");
        private static readonly DetectedFileType Mp4 = Type("MP4", [".mp4", ".m4a", ".m4v"], ".mp4");
        private static readonly DetectedFileType QuickTime = Type("QuickTime", [".mov"], ".mov");
        private static readonly DetectedFileType IsoBmff = Type(
            "ISO BMFF",
            [".mp4", ".m4a", ".m4v", ".mov"],
            null);
        private static readonly DetectedFileType WebM = Type("WebM", [".webm"], ".webm");
        private static readonly DetectedFileType Matroska = Type(
            "Matroska",
            [".mkv", ".mka", ".mks", ".mk3d"],
            ".mkv");
        private static readonly DetectedFileType Ebml = Type(
            "EBML",
            [".webm", ".mkv", ".mka", ".mks", ".mk3d"],
            null);
        private static readonly DetectedFileType Avi = Type("AVI", [".avi"], ".avi");

        private static readonly HashSet<string> Mp4Brands = new(StringComparer.Ordinal)
        {
            "isom", "iso2", "iso3", "iso4", "iso5", "iso6", "mp41", "mp42", "avc1", "dash",
            "M4A ", "M4B ", "M4P ", "M4V ",
        };

        public static DetectedFileType? Detect(ReadOnlySpan<byte> header, string path)
        {
            _ = path;

            if (HasPrefix(header, [0xFF, 0xD8, 0xFF]))
            {
                return Jpeg;
            }

            if (HasPrefix(header, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            {
                return Png;
            }

            if (header.StartsWith("GIF87a"u8) || header.StartsWith("GIF89a"u8))
            {
                return Gif;
            }

            if (header.StartsWith("BM"u8))
            {
                return Bmp;
            }

            if (HasPrefix(header, [0x49, 0x49, 0x2A, 0x00]) ||
                HasPrefix(header, [0x4D, 0x4D, 0x00, 0x2A]))
            {
                return Tiff;
            }

            if (header.StartsWith("%PDF-"u8))
            {
                return Pdf;
            }

            if (HasPrefix(header, [0x50, 0x4B, 0x03, 0x04]) ||
                HasPrefix(header, [0x50, 0x4B, 0x05, 0x06]) ||
                HasPrefix(header, [0x50, 0x4B, 0x07, 0x08]))
            {
                return Zip;
            }

            if (HasPrefix(header, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00]) ||
                HasPrefix(header, [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]))
            {
                return Rar;
            }

            if (HasPrefix(header, [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            {
                return SevenZip;
            }

            if (header.StartsWith("ID3"u8) || IsMpegAudioFrame(header))
            {
                return Mp3;
            }

            if (header.StartsWith("fLaC"u8))
            {
                return Flac;
            }

            if (header.StartsWith("OggS"u8))
            {
                return Ogg;
            }

            if (header.Length >= 12 && header.StartsWith("RIFF"u8))
            {
                if (header[8..].StartsWith("WEBP"u8))
                {
                    return WebP;
                }

                if (header[8..].StartsWith("WAVE"u8))
                {
                    return Wav;
                }

                if (header[8..].StartsWith("AVI "u8))
                {
                    return Avi;
                }
            }

            DetectedFileType? bmff = DetectIsoBmff(header);
            if (bmff is not null)
            {
                return bmff;
            }

            return DetectEbml(header);
        }

        public static async ValueTask<DetectedFileType?> DetectFileAsync(
            string path,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await DetectStreamAsync(stream, path, cancellationToken).ConfigureAwait(false);
        }

        internal static async ValueTask<DetectedFileType?> DetectStreamAsync(
            Stream stream,
            string path,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(stream);
            cancellationToken.ThrowIfCancellationRequested();

            byte[] header = new byte[MaximumHeaderBytes];
            int totalRead = 0;
            while (totalRead < header.Length)
            {
                int read = await stream.ReadAsync(
                    header.AsMemory(totalRead, header.Length - totalRead),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            return Detect(header.AsSpan(0, totalRead), path);
        }

        private static DetectedFileType? DetectIsoBmff(ReadOnlySpan<byte> header)
        {
            if (header.Length < 16 || !header[4..].StartsWith("ftyp"u8))
            {
                return null;
            }

            uint ordinarySize = BinaryPrimitives.ReadUInt32BigEndian(header);
            int brandsOffset;
            ulong boxSize;
            if (ordinarySize == 1)
            {
                if (header.Length < 24)
                {
                    return null;
                }

                boxSize = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                brandsOffset = 16;
                if (boxSize < 24 || boxSize > (ulong)header.Length || (boxSize - 24) % 4 != 0)
                {
                    return null;
                }
            }
            else
            {
                boxSize = ordinarySize;
                brandsOffset = 8;
                if (boxSize < 16 || boxSize > (ulong)header.Length || (boxSize - 16) % 4 != 0)
                {
                    return null;
                }
            }

            int end = checked((int)boxSize);
            bool hasMp4 = false;
            bool hasQuickTime = false;
            bool hasUnknown = false;
            for (int offset = brandsOffset; offset < end; offset += 4)
            {
                if (offset == brandsOffset + 4)
                {
                    continue;
                }

                string brand = Encoding.ASCII.GetString(header.Slice(offset, 4));
                if (Mp4Brands.Contains(brand))
                {
                    hasMp4 = true;
                }
                else if (brand == "qt  ")
                {
                    hasQuickTime = true;
                }
                else
                {
                    hasUnknown = true;
                }
            }

            if (hasMp4 && !hasQuickTime && !hasUnknown)
            {
                return Mp4;
            }

            if (hasQuickTime && !hasMp4 && !hasUnknown)
            {
                return QuickTime;
            }

            return IsoBmff;
        }

        private static DetectedFileType? DetectEbml(ReadOnlySpan<byte> header)
        {
            if (!HasPrefix(header, [0x1A, 0x45, 0xDF, 0xA3]) ||
                !TryReadVint(header[4..], out int sizeLength, out ulong payloadSize))
            {
                return null;
            }

            int payloadStart = 4 + sizeLength;
            if (payloadSize > (ulong)(header.Length - payloadStart))
            {
                return null;
            }

            int payloadEnd = payloadStart + checked((int)payloadSize);
            int offset = payloadStart;
            while (offset < payloadEnd)
            {
                if (!TryGetVintLength(header[offset..payloadEnd], out int idLength) ||
                    offset + idLength > payloadEnd)
                {
                    return null;
                }

                bool isDocType = idLength == 2 && header[offset] == 0x42 && header[offset + 1] == 0x82;
                offset += idLength;
                if (!TryReadVint(header[offset..payloadEnd], out int elementSizeLength, out ulong elementSize))
                {
                    return null;
                }

                offset += elementSizeLength;
                if (elementSize > (ulong)(payloadEnd - offset))
                {
                    return null;
                }

                int valueLength = checked((int)elementSize);
                if (isDocType)
                {
                    ReadOnlySpan<byte> docType = header.Slice(offset, valueLength);
                    if (docType.SequenceEqual("webm"u8))
                    {
                        return WebM;
                    }

                    if (docType.SequenceEqual("matroska"u8))
                    {
                        return Matroska;
                    }

                    return Ebml;
                }

                offset += valueLength;
            }

            return Ebml;
        }

        private static bool TryReadVint(
            ReadOnlySpan<byte> bytes,
            out int length,
            out ulong value)
        {
            value = 0;
            if (!TryGetVintLength(bytes, out length) || bytes.Length < length)
            {
                return false;
            }

            byte marker = (byte)(0x80 >> (length - 1));
            value = (ulong)(bytes[0] & (marker - 1));
            for (int index = 1; index < length; index++)
            {
                value = (value << 8) | bytes[index];
            }

            ulong unknownValue = (1UL << (length * 7)) - 1;
            return value != unknownValue;
        }

        private static bool TryGetVintLength(ReadOnlySpan<byte> bytes, out int length)
        {
            length = 0;
            if (bytes.IsEmpty || bytes[0] == 0)
            {
                return false;
            }

            byte marker = 0x80;
            while ((bytes[0] & marker) == 0)
            {
                marker >>= 1;
                length++;
            }

            length++;
            return length <= 8 && bytes.Length >= length;
        }

        private static bool IsMpegAudioFrame(ReadOnlySpan<byte> header)
        {
            if (header.Length < 3 || header[0] != 0xFF || (header[1] & 0xE0) != 0xE0)
            {
                return false;
            }

            int version = (header[1] >> 3) & 0x03;
            int layer = (header[1] >> 1) & 0x03;
            int bitrate = (header[2] >> 4) & 0x0F;
            int sampleRate = (header[2] >> 2) & 0x03;
            return version != 1 && layer != 0 && bitrate is not (0 or 15) && sampleRate != 3;
        }

        private static bool HasPrefix(ReadOnlySpan<byte> header, ReadOnlySpan<byte> prefix) =>
            header.StartsWith(prefix);

        private static DetectedFileType Type(
            string name,
            IEnumerable<string> allowedExtensions,
            string? recommendedExtension)
        {
            var allowed = allowedExtensions
                .Select(static extension => extension.StartsWith('.') ? extension : $".{extension}")
                .Select(static extension => extension.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (recommendedExtension is not null && !allowed.Contains(recommendedExtension))
            {
                throw new ArgumentException("The recommendation must be allowed.", nameof(recommendedExtension));
            }

            return new DetectedFileType(name, allowed, recommendedExtension);
        }
    }
}
