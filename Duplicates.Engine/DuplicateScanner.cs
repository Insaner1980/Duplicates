using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using Duplicates.Engine.FileEnumeration;
using Duplicates.Engine.Hashing;
using Duplicates.Engine.Models;

namespace Duplicates.Engine;

public sealed class DuplicateScanner
{
    private const int VerificationBufferSize = 1024 * 1024;
    private readonly FileWalker _fileWalker;
    private readonly IFileHasher _fileHasher;
    private readonly ArrayPool<byte> _verificationPool;

    public DuplicateScanner()
        : this(new FileWalker(), new FileHasher())
    {
    }

    internal DuplicateScanner(FileWalker fileWalker, IFileHasher fileHasher)
        : this(fileWalker, fileHasher, ArrayPool<byte>.Shared)
    {
    }

    internal DuplicateScanner(
        FileWalker fileWalker,
        IFileHasher fileHasher,
        ArrayPool<byte> verificationPool)
    {
        _fileWalker = fileWalker;
        _fileHasher = fileHasher;
        _verificationPool = verificationPool;
    }

    public async Task<ScanResult> ScanAsync(
        ScanOptions options,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        cancellationToken.ThrowIfCancellationRequested();

        var stopwatch = Stopwatch.StartNew();
        var progressReporter = new ScanProgressReporter(progress);
        var skippedPaths = new ConcurrentBag<SkippedPath>();

        FileWalkResult walkResult = _fileWalker.Walk(options, progressReporter, cancellationToken);
        foreach (SkippedPath skippedPath in walkResult.SkippedPaths)
        {
            skippedPaths.Add(skippedPath);
        }

        progressReporter.Report(new ScanProgress
        {
            Phase = ScanPhase.GroupingBySize,
            FilesDiscovered = walkResult.Files.Count,
        }, force: true);

        List<FileEntry> sizeCandidates = walkResult.Files
            .GroupBy(static file => file.SizeBytes)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .ToList();

        HashResult[] partialHashes = await HashFilesAsync(
            sizeCandidates,
            static file => Math.Min(file.SizeBytes, FileHasher.PartialHashBytes),
            ScanPhase.PartialHashing,
            progressReporter,
            skippedPaths,
            options,
            cancellationToken).ConfigureAwait(false);

        List<FileEntry> fullHashCandidates = partialHashes
            .GroupBy(static result => (result.File.SizeBytes, result.Hash))
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group.Select(static result => result.File))
            .ToList();

        List<FileEntry> needsFullHash = fullHashCandidates
            .Where(static file => file.ContentHash is null)
            .ToList();

        HashResult[] fullHashes = await HashFilesAsync(
            needsFullHash,
            static file => file.SizeBytes,
            ScanPhase.FullHashing,
            progressReporter,
            skippedPaths,
            options,
            cancellationToken).ConfigureAwait(false);

        foreach (HashResult fullHash in fullHashes)
        {
            fullHash.File.ContentHash = fullHash.Hash;
        }

        progressReporter.Report(new ScanProgress
        {
            Phase = ScanPhase.Verifying,
            FilesDiscovered = walkResult.Files.Count,
            TotalBytesToProcess = fullHashCandidates.Sum(static file => file.SizeBytes),
        }, force: true);

        List<DuplicateGroup> groups = [];
        foreach (IGrouping<(long SizeBytes, ulong ContentHash), FileEntry> hashGroup in fullHashCandidates
            .Where(static file => file.ContentHash is not null)
            .GroupBy(static file => (file.SizeBytes, file.ContentHash!.Value)))
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<FileEntry> identityDeduped = DeduplicateSamePhysicalFiles(hashGroup);
            if (identityDeduped.Count < 2)
            {
                continue;
            }

            if (options.VerifyByteByByte && hashGroup.Key.SizeBytes > 0)
            {
                IReadOnlyList<IReadOnlyList<FileEntry>> verifiedGroups = await VerifyGroupsAsync(
                    identityDeduped,
                    skippedPaths,
                    cancellationToken).ConfigureAwait(false);

                groups.AddRange(verifiedGroups
                    .Where(static verifiedGroup => verifiedGroup.Count > 1)
                    .Select(verifiedGroup => CreateGroup(hashGroup.Key.ContentHash, hashGroup.Key.SizeBytes, verifiedGroup)));
            }
            else
            {
                groups.Add(CreateGroup(hashGroup.Key.ContentHash, hashGroup.Key.SizeBytes, identityDeduped));
            }
        }

        groups = groups
            .OrderByDescending(static group => group.WastedBytes)
            .ThenBy(static group => group.Files[0].FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        stopwatch.Stop();
        progressReporter.Report(new ScanProgress
        {
            Phase = ScanPhase.Done,
            FilesDiscovered = walkResult.Files.Count,
            FilesProcessed = walkResult.Files.Count,
        }, force: true);

        return new ScanResult
        {
            Groups = groups,
            TotalFilesScanned = walkResult.Files.Count,
            TotalDuplicateFiles = groups.Sum(static group => group.Files.Count - 1),
            TotalReclaimableBytes = groups.Sum(static group => group.WastedBytes),
            Elapsed = stopwatch.Elapsed,
            SkippedPaths = skippedPaths.ToArray(),
        };
    }

    private static void ValidateOptions(ScanOptions options)
    {
        if (options.MinSizeBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options.MinSizeBytes), options.MinSizeBytes, "Minimum size cannot be negative.");
        }

        if (options.MaxSizeBytes < options.MinSizeBytes)
        {
            throw new ArgumentException("Maximum size must be greater than or equal to minimum size.", nameof(options));
        }

        _ = options.GetEffectiveMaxHashingConcurrency();
    }

    private async Task<HashResult[]> HashFilesAsync(
        IReadOnlyList<FileEntry> files,
        Func<FileEntry, long> bytesToRead,
        ScanPhase phase,
        ScanProgressReporter progress,
        ConcurrentBag<SkippedPath> skippedPaths,
        ScanOptions options,
        CancellationToken cancellationToken)
    {
        if (files.Count == 0)
        {
            return [];
        }

        var results = new ConcurrentBag<HashResult>();
        long processedFiles = 0;
        long processedBytes = 0;
        long totalBytes = files.Sum(bytesToRead);

        await Parallel.ForEachAsync(
            files,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = options.GetEffectiveMaxHashingConcurrency(),
                CancellationToken = cancellationToken,
            },
            async (file, ct) =>
            {
                try
                {
                    long maxBytes = bytesToRead(file);
                    ulong hash = await _fileHasher.HashAsync(
                        file.FullPath,
                        file.SizeBytes,
                        maxBytes,
                        bytesRead =>
                        {
                            long currentBytes = Interlocked.Add(ref processedBytes, bytesRead);
                            progress.Report(new ScanProgress
                            {
                                Phase = phase,
                                FilesDiscovered = files.Count,
                                FilesProcessed = Volatile.Read(ref processedFiles),
                                BytesProcessed = currentBytes,
                                TotalBytesToProcess = totalBytes,
                                CurrentFilePath = file.FullPath,
                            });
                        },
                        ct).ConfigureAwait(false);

                    if (file.SizeBytes <= FileHasher.PartialHashBytes)
                    {
                        file.ContentHash = hash;
                    }

                    results.Add(new HashResult(file, hash));
                    Interlocked.Increment(ref processedFiles);
                }
                catch (Exception ex) when (IsSkippable(ex))
                {
                    skippedPaths.Add(new SkippedPath { Path = file.FullPath, Reason = ex.Message });
                    Interlocked.Increment(ref processedFiles);
                }
            }).ConfigureAwait(false);

        return results.ToArray();
    }

    private static List<FileEntry> DeduplicateSamePhysicalFiles(IEnumerable<FileEntry> files)
    {
        return files
            .GroupBy(static file => FileIdentityReader.GetBestEffortIdentity(file.FullPath), StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<IReadOnlyList<IReadOnlyList<FileEntry>>> VerifyGroupsAsync(
        IReadOnlyList<FileEntry> files,
        ConcurrentBag<SkippedPath> skippedPaths,
        CancellationToken cancellationToken)
    {
        byte[] leftBuffer = _verificationPool.Rent(VerificationBufferSize);
        byte[] rightBuffer = _verificationPool.Rent(VerificationBufferSize);
        try
        {
            var remaining = new List<FileEntry>(files);
            var verifiedGroups = new List<IReadOnlyList<FileEntry>>();

            while (remaining.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileEntry reference = remaining[0];
                remaining.RemoveAt(0);

                var group = new List<FileEntry> { reference };
                for (int index = remaining.Count - 1; index >= 0; index--)
                {
                    FileEntry candidate = remaining[index];
                    try
                    {
                        if (await FilesAreEqualAsync(
                            reference,
                            candidate,
                            leftBuffer,
                            rightBuffer,
                            cancellationToken).ConfigureAwait(false))
                        {
                            group.Add(candidate);
                            remaining.RemoveAt(index);
                        }
                    }
                    catch (Exception ex) when (IsSkippable(ex))
                    {
                        skippedPaths.Add(new SkippedPath { Path = candidate.FullPath, Reason = ex.Message });
                        remaining.RemoveAt(index);
                    }
                }

                if (group.Count > 1)
                {
                    verifiedGroups.Add(group);
                }
            }

            return verifiedGroups;
        }
        finally
        {
            _verificationPool.Return(leftBuffer);
            _verificationPool.Return(rightBuffer);
        }
    }

    private static async Task<bool> FilesAreEqualAsync(
        FileEntry left,
        FileEntry right,
        byte[] leftBuffer,
        byte[] rightBuffer,
        CancellationToken cancellationToken)
    {
        using var leftStream = new FileStream(
            left.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var rightStream = new FileStream(
            right.FullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            VerificationBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (leftStream.Length != left.SizeBytes || rightStream.Length != right.SizeBytes || leftStream.Length != rightStream.Length)
        {
            throw new IOException("File changed during scan.");
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int leftRead = await leftStream.ReadAsync(
                leftBuffer.AsMemory(0, VerificationBufferSize),
                cancellationToken).ConfigureAwait(false);
            int rightRead = await rightStream.ReadAsync(
                rightBuffer.AsMemory(0, VerificationBufferSize),
                cancellationToken).ConfigureAwait(false);

            if (leftRead != rightRead)
            {
                return false;
            }

            if (leftRead == 0)
            {
                return true;
            }

            if (!leftBuffer.AsSpan(0, leftRead).SequenceEqual(rightBuffer.AsSpan(0, rightRead)))
            {
                return false;
            }
        }
    }

    private static DuplicateGroup CreateGroup(ulong contentHash, long sizeBytes, IReadOnlyList<FileEntry> files)
    {
        return new DuplicateGroup
        {
            ContentHash = contentHash,
            SizeBytes = sizeBytes,
            Files = files
                .OrderBy(static file => file.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
        };
    }

    private static bool IsSkippable(Exception ex)
    {
        return ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException;
    }

    private sealed record HashResult(FileEntry File, ulong Hash);
}
