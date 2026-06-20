using System.Buffers;
using System.IO.Hashing;

namespace Duplicates.Engine.Hashing;

internal interface IFileHasher
{
    Task<ulong> HashAsync(
        string path,
        long expectedSizeBytes,
        long maxBytesToRead,
        Action<long>? bytesRead,
        CancellationToken cancellationToken);
}

internal sealed class FileHasher : IFileHasher
{
    public const int PartialHashBytes = 64 * 1024;
    private const int BufferSize = 1024 * 1024;

    public async Task<ulong> HashAsync(
        string path,
        long expectedSizeBytes,
        long maxBytesToRead,
        Action<long>? bytesRead,
        CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                BufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (stream.Length != expectedSizeBytes)
            {
                throw new IOException("File changed during scan.");
            }

            var hash = new XxHash3();
            long remaining = Math.Min(maxBytesToRead, expectedSizeBytes);

            while (remaining > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, remaining);
                int read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.Append(buffer.AsSpan(0, read));
                remaining -= read;
                bytesRead?.Invoke(read);
            }

            if (stream.Length != expectedSizeBytes)
            {
                throw new IOException("File changed during scan.");
            }

            return hash.GetCurrentHashAsUInt64();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
