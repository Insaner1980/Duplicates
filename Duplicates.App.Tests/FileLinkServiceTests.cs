using System.ComponentModel;
using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class FileLinkServiceTests
{
    private static readonly DateTime SnapshotUtc = new(2026, 8, 8, 9, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ReplaceWithLinksAsync_CopiesCallerListsSynchronouslyAndSupportsMultipleGroups()
    {
        var platform = new FakeFileLinkPlatform();
        string survivorOne = FullPath("one-survivor.bin");
        string duplicateOne = FullPath("one-copy.bin");
        string survivorTwo = FullPath("two-survivor.bin");
        string duplicateTwo = FullPath("two-copy.bin");
        platform.AddOrdinaryFile(survivorOne, 11, [1, 2, 3]);
        platform.AddOrdinaryFile(duplicateOne, 12, [1, 2, 3]);
        platform.AddOrdinaryFile(survivorTwo, 21, [4, 5, 6]);
        platform.AddOrdinaryFile(duplicateTwo, 22, [4, 5, 6]);
        var firstDuplicates = new List<LinkReplacementFile> { Snapshot(duplicateOne, 3) };
        var groups = new List<LinkReplacementGroup>
        {
            new(Snapshot(survivorOne, 3), firstDuplicates),
            new(Snapshot(survivorTwo, 3), [Snapshot(duplicateTwo, 3)]),
        };
        var recycle = new FakeRecycleBinService(platform);
        var service = new FileLinkService(platform, recycle);

        Task<FileOperationSummary> operation = service.ReplaceWithLinksAsync(
            groups,
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);
        groups.Clear();
        firstDuplicates.Clear();
        FileOperationSummary summary = await operation;

        Assert.Equal(2, summary.Results.Count);
        Assert.All(summary.Results, static result => Assert.True(result.Succeeded));
        Assert.Equal(6, summary.SucceededBytes);
        Assert.Equal(2, recycle.Calls.Count);
    }

    [Theory]
    [InlineData(InvalidRequest.RelativePath)]
    [InlineData(InvalidRequest.NegativeLength)]
    [InlineData(InvalidRequest.EmptyDuplicates)]
    [InlineData(InvalidRequest.SurvivorRepeated)]
    [InlineData(InvalidRequest.OverlapAcrossGroups)]
    public async Task ReplaceWithLinksAsync_RejectsStructurallyInvalidRequestsBeforePlatformAccess(
        InvalidRequest invalidRequest)
    {
        var platform = new FakeFileLinkPlatform();
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));
        IReadOnlyList<LinkReplacementGroup> groups = BuildInvalidRequest(invalidRequest);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => service.ReplaceWithLinksAsync(
            groups,
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None));

        Assert.Equal(0, platform.OpenCount);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_RejectsPhysicalAliasAcrossGroupsBeforeMutation()
    {
        var platform = new FakeFileLinkPlatform();
        string survivorOne = FullPath("alias-survivor-one.bin");
        string survivorTwo = FullPath("alias-survivor-two.bin");
        string duplicateOne = FullPath("alias-copy-one.bin");
        string duplicateTwo = FullPath("alias-copy-two.bin");
        platform.AddOrdinaryFile(survivorOne, 91, [1]);
        platform.AddOrdinaryFile(survivorTwo, 91, [1]);
        platform.AddOrdinaryFile(duplicateOne, 92, [1]);
        platform.AddOrdinaryFile(duplicateTwo, 93, [1]);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        await Assert.ThrowsAsync<ArgumentException>(() => service.ReplaceWithLinksAsync(
            [
                new(Snapshot(survivorOne, 1), [Snapshot(duplicateOne, 1)]),
                new(Snapshot(survivorTwo, 1), [Snapshot(duplicateTwo, 1)]),
            ],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None));

        Assert.Empty(platform.Renames);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ContentMismatchFailsBeforeRenameAndContinuesNextItem()
    {
        var platform = new FakeFileLinkPlatform();
        string survivor = FullPath("content-survivor.bin");
        string changed = FullPath("changed-copy.bin");
        string valid = FullPath("valid-copy.bin");
        platform.AddOrdinaryFile(survivor, 1, [1, 2, 3]);
        platform.AddOrdinaryFile(changed, 2, [1, 2, 4]);
        platform.AddOrdinaryFile(valid, 3, [1, 2, 3]);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(changed, 3), Snapshot(valid, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.Equal(2, summary.Results.Count);
        Assert.False(summary.Results[0].Succeeded);
        Assert.True(summary.Results[1].Succeeded);
        Assert.DoesNotContain(platform.Renames, path => PathEquals(path.Source, changed));
    }

    [Theory]
    [InlineData(EligibilityFault.Length)]
    [InlineData(EligibilityFault.Modified)]
    [InlineData(EligibilityFault.Reparse)]
    [InlineData(EligibilityFault.UnknownAttribute)]
    [InlineData(EligibilityFault.AttributesMismatch)]
    [InlineData(EligibilityFault.SecurityMismatch)]
    [InlineData(EligibilityFault.StreamMismatch)]
    [InlineData(EligibilityFault.StreamError)]
    [InlineData(EligibilityFault.DuplicateHasMultipleLinks)]
    public async Task ReplaceWithLinksAsync_EligibilityFaultsFailClosedBeforeRename(EligibilityFault fault)
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.ApplyEligibilityFault(fault, survivor, duplicate);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileOperationResult failure = Assert.Single(summary.Results);
        Assert.False(failure.Succeeded);
        Assert.Null(failure.Failure!.RecoveryPath);
        Assert.Empty(platform.Renames);
    }

    [Theory]
    [InlineData((int)FileAttributes.Directory)]
    [InlineData((int)FileAttributes.Device)]
    [InlineData((int)FileAttributes.ReparsePoint)]
    [InlineData((int)FileAttributes.Encrypted)]
    [InlineData((int)FileAttributes.Offline)]
    [InlineData((int)FileAttributes.IntegrityStream)]
    [InlineData(0x00040000)]
    [InlineData(0x00080000)]
    [InlineData(0x00100000)]
    [InlineData(0x00400000)]
    public async Task ReplaceWithLinksAsync_RejectsEveryUnsafeAttribute(int unsafeAttribute)
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SetAttributes(duplicate, FileAttributes.Archive | (FileAttributes)unsafeAttribute);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.SymbolicLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Empty(platform.Renames);
    }

    [Theory]
    [InlineData((int)FileAttributes.ReadOnly)]
    [InlineData((int)FileAttributes.Hidden)]
    [InlineData((int)FileAttributes.System)]
    [InlineData((int)FileAttributes.Temporary)]
    [InlineData((int)FileAttributes.SparseFile)]
    [InlineData((int)FileAttributes.Compressed)]
    [InlineData((int)FileAttributes.NotContentIndexed)]
    [InlineData((int)FileAttributes.NoScrubData)]
    public async Task ReplaceWithLinksAsync_RequiresEveryMaterialAttributeToMatch(int materialAttribute)
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SetAttributes(duplicate, FileAttributes.Archive | (FileAttributes)materialAttribute);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.SymbolicLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Empty(platform.Renames);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_RequiresOwnerGroupDaclPresenceBytesAndControlToMatch()
    {
        foreach (FileLinkSecurityInfo mismatched in new FileLinkSecurityInfo[]
        {
            new([9], [2], true, [3, 4], 0),
            new([1], [9], true, [3, 4], 0),
            new([1], [2], false, [3, 4], 0),
            new([1], [2], true, [9], 0),
            new([1], [2], true, [3, 4], 0x1000),
        })
        {
            var platform = NewEligiblePair(out string survivor, out string duplicate);
            platform.SetSecurity(duplicate, mismatched);
            var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

            FileOperationSummary summary = await service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
                LinkReplacementMode.HardLink,
                null,
                CancellationToken.None);

            Assert.False(Assert.Single(summary.Results).Succeeded);
            Assert.Empty(platform.Renames);
        }
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ReadControlDenialFailsClosedBeforeRename()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SecurityReadException = new UnauthorizedAccessException("READ_CONTROL denied.");
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Empty(platform.Renames);
    }

    [Theory]
    [InlineData(false, "NTFS")]
    [InlineData(true, "ReFS")]
    public async Task ReplaceWithLinksAsync_HardLinkRequiresLocalNtfs(bool isLocal, string fileSystem)
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SetVolume(survivor, isLocal, fileSystem, 7);
        platform.SetVolume(duplicate, isLocal, fileSystem, 7);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Empty(platform.Renames);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_HardLinkRequiresSameVolume()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SetVolume(duplicate, true, "NTFS", 8);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Empty(platform.Renames);
    }

    [Theory]
    [InlineData(1023u, true)]
    [InlineData(1024u, false)]
    public async Task ReplaceWithLinksAsync_EnforcesNtfsHardLinkLimit(uint survivorLinks, bool succeeds)
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SetLinkCount(survivor, survivorLinks);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.Equal(succeeds, Assert.Single(summary.Results).Succeeded);
        Assert.Equal(succeeds ? 1 : 0, platform.HardLinksCreated);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_CreateFailureRestoresDuplicateThroughOriginalHandle()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.CreateException = new IOException("create failed");
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.DoesNotContain(platform.Paths, path => path.Contains(".duplicates-rollback-", StringComparison.Ordinal));
        Assert.Equal(2, platform.Renames.Count);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ContentVerificationFailureDisposesOwnedLinkAndRestores()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.ContentMismatchOnCall = 3;
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        Assert.False(result.Succeeded);
        Assert.Null(result.Failure!.RecoveryPath);
        Assert.Equal(1, platform.DeleteByHandleCount);
        Assert.Equal(2, platform.Renames.Count);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.DoesNotContain(platform.Paths, path => path.Contains(".duplicates-rollback-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_FinalRollbackContentMismatchDisposesLinkAndRestores()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.ContentMismatchOnCall = 4;
        var recycle = new FakeRecycleBinService(platform);
        var service = new FileLinkService(platform, recycle);

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        Assert.False(result.Succeeded);
        Assert.Empty(recycle.Calls);
        Assert.Equal(1, platform.DeleteByHandleCount);
        Assert.Equal(2, platform.Renames.Count);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.DoesNotContain(platform.Paths, path => path.Contains(".duplicates-rollback-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_CreatedPathSwapDuringFinalComparisonIsNeverCommitted()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SwapCreatedPathOnContentCall = 4;
        var recycle = new FakeRecycleBinService(platform);
        var service = new FileLinkService(platform, recycle);

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        Assert.False(result.Succeeded);
        Assert.Empty(recycle.Calls);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.NotNull(result.Failure!.RecoveryPath);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_RestoreFailureStopsBatchAndReportsExactRecoveryPath()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.CreateException = new IOException("create failed");
        platform.RestoreException = new IOException("restore failed");
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileActionFailure failure = Assert.Single(summary.Results).Failure!;
        Assert.NotNull(failure.RecoveryPath);
        Assert.True(platform.ContainsPath(failure.RecoveryPath!));
        Assert.False(platform.ContainsPath(duplicate));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ForeignCreatedPathIsNeverDisposedAndReportsRollback()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.SwapCreatedIdentityBeforeProbe = true;
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.Equal(0, platform.DeleteByHandleCount);
        Assert.Equal(0, platform.PathDeleteCount);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.NotNull(Assert.Single(summary.Results).Failure!.RecoveryPath);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_RecycleFailureWithOwnedRollbackRestoresDuplicate()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        var recycle = new FakeRecycleBinService(platform) { ThrowBeforeRemoval = true };
        var service = new FileLinkService(platform, recycle);

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.False(Assert.Single(summary.Results).Succeeded);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.Equal(1, platform.DeleteByHandleCount);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_RecycleErrorAfterCommittedMoveCountsSuccess()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        var recycle = new FakeRecycleBinService(platform) { ThrowAfterRemoval = true };
        var service = new FileLinkService(platform, recycle);

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        Assert.True(Assert.Single(summary.Results).Succeeded);
        Assert.True(platform.ContainsPath(duplicate));
        Assert.DoesNotContain(platform.Paths, path => path.Contains(".duplicates-rollback-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ForeignOccupantStopsBatchAndReportsRecoveryPath()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        string second = FullPath("second-copy.bin");
        platform.AddOrdinaryFile(second, 3, [1, 2, 3]);
        platform.ForeignOccupantAfterCreate = true;
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3), Snapshot(second, 3)])],
            LinkReplacementMode.HardLink,
            null,
            CancellationToken.None);

        FileOperationResult result = Assert.Single(summary.Results);
        Assert.False(result.Succeeded);
        Assert.Contains(".duplicates-rollback-", result.Failure!.RecoveryPath, StringComparison.Ordinal);
        Assert.True(platform.ContainsPath(result.Failure.RecoveryPath!));
        Assert.DoesNotContain(platform.Renames, rename => PathEquals(rename.Source, second));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_DefersCancellationUntilTransactionCommits()
    {
        var platform = NewEligiblePair(out string survivor, out string first);
        string second = FullPath("cancel-second.bin");
        platform.AddOrdinaryFile(second, 3, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        platform.AfterFirstRename = cancellation.Cancel;
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationCanceledException exception = await Assert.ThrowsAsync<FileOperationCanceledException>(() =>
            service.ReplaceWithLinksAsync(
                [new(Snapshot(survivor, 3), [Snapshot(first, 3), Snapshot(second, 3)])],
                LinkReplacementMode.HardLink,
                null,
                cancellation.Token));

        Assert.True(Assert.Single(exception.Summary.Results).Succeeded);
        Assert.DoesNotContain(platform.Renames, rename => PathEquals(rename.Source, second));
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_CancellationBeforeExecutionDoesNotOpenOrMutate()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.HardLink,
            null,
            cancellation.Token));

        Assert.Equal(0, platform.OpenCount);
        Assert.Empty(platform.Renames);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_SymbolicLinkVerifiesEntryTargetAndFollowedIdentity()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.SymbolicLink,
            null,
            CancellationToken.None);

        Assert.True(Assert.Single(summary.Results).Succeeded);
        Assert.Equal(Path.GetFullPath(survivor), platform.LastSymbolicTarget);
        Assert.True(platform.FollowOpenCount > 0);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_MapsSymbolicPrivilegeFailureToStableGuidance()
    {
        var platform = NewEligiblePair(out string survivor, out string duplicate);
        platform.CreateException = new Win32Exception(1314);
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(duplicate, 3)])],
            LinkReplacementMode.SymbolicLink,
            null,
            CancellationToken.None);

        FileActionFailure failure = Assert.Single(summary.Results).Failure!;
        Assert.Contains("Developer Mode", failure.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("enable elevation", failure.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceWithLinksAsync_ReportsCommittedProgressOnly()
    {
        var platform = NewEligiblePair(out string survivor, out string first);
        string mismatch = FullPath("progress-mismatch.bin");
        platform.AddOrdinaryFile(mismatch, 3, [9, 9, 9]);
        var reports = new List<FileOperationProgress>();
        var service = new FileLinkService(platform, new FakeRecycleBinService(platform));

        FileOperationSummary summary = await service.ReplaceWithLinksAsync(
            [new(Snapshot(survivor, 3), [Snapshot(mismatch, 3), Snapshot(first, 3)])],
            LinkReplacementMode.HardLink,
            new InlineProgress<FileOperationProgress>(reports.Add),
            CancellationToken.None);

        Assert.Equal(3, summary.SucceededBytes);
        Assert.Equal([0L, 3L], reports.Select(static progress => progress.SucceededBytes));
        Assert.Equal([1, 2], reports.Select(static progress => progress.ProcessedCount));
    }

    private static FakeFileLinkPlatform NewEligiblePair(out string survivor, out string duplicate)
    {
        var platform = new FakeFileLinkPlatform();
        survivor = FullPath("survivor.bin");
        duplicate = FullPath("duplicate.bin");
        platform.AddOrdinaryFile(survivor, 1, [1, 2, 3]);
        platform.AddOrdinaryFile(duplicate, 2, [1, 2, 3]);
        return platform;
    }

    private static LinkReplacementFile Snapshot(string path, long length) => new(path, length, SnapshotUtc);

    private static string FullPath(string name) => Path.Combine("C:\\Task16Tests", name);

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<LinkReplacementGroup> BuildInvalidRequest(InvalidRequest request)
    {
        string survivor = FullPath("invalid-survivor.bin");
        string duplicate = FullPath("invalid-duplicate.bin");
        return request switch
        {
            InvalidRequest.RelativePath => [new(new LinkReplacementFile("relative.bin", 1, SnapshotUtc), [Snapshot(duplicate, 1)])],
            InvalidRequest.NegativeLength => [new(new LinkReplacementFile(survivor, -1, SnapshotUtc), [Snapshot(duplicate, 1)])],
            InvalidRequest.EmptyDuplicates => [new(Snapshot(survivor, 1), [])],
            InvalidRequest.SurvivorRepeated => [new(Snapshot(survivor, 1), [Snapshot(survivor.ToUpperInvariant(), 1)])],
            InvalidRequest.OverlapAcrossGroups =>
            [
                new(Snapshot(survivor, 1), [Snapshot(duplicate, 1)]),
                new(Snapshot(FullPath("other.bin"), 1), [Snapshot(duplicate.ToUpperInvariant(), 1)]),
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public enum InvalidRequest
    {
        RelativePath,
        NegativeLength,
        EmptyDuplicates,
        SurvivorRepeated,
        OverlapAcrossGroups,
    }

    public enum EligibilityFault
    {
        Length,
        Modified,
        Reparse,
        UnknownAttribute,
        AttributesMismatch,
        SecurityMismatch,
        StreamMismatch,
        StreamError,
        DuplicateHasMultipleLinks,
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

internal sealed class FakeRecycleBinService(FakeFileLinkPlatform platform) : IRecycleBinService
{
    public bool ThrowBeforeRemoval { get; set; }

    public bool ThrowAfterRemoval { get; set; }

    public List<(string Path, FileSystemIdentity Identity, CancellationToken CancellationToken)> Calls { get; } = [];

    public Task RecycleFileAsync(
        string path,
        FileSystemIdentity expectedIdentity,
        CancellationToken cancellationToken)
    {
        Calls.Add((path, expectedIdentity, cancellationToken));
        if (ThrowBeforeRemoval)
        {
            throw new IOException("Recycle failed before commit.");
        }

        platform.RemoveForRecycle(path, expectedIdentity);
        if (ThrowAfterRemoval)
        {
            throw new IOException("Recycle reported an error after commit.");
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeFileLinkPlatform : IFileLinkPlatform
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _nextIdentity = 500;
    private bool _firstRenameObserved;

    public Exception? CreateException { get; set; }

    public Exception? RestoreException { get; set; }

    public Exception? SecurityReadException { get; set; }

    public int ContentMismatchOnCall { get; set; }

    public int SwapCreatedPathOnContentCall { get; set; }

    public Exception? RecycleException { get; set; }

    public bool RemoveBeforeRecycleException { get; set; }

    public bool ReplaceWithForeignBeforeRecycleException { get; set; }

    public bool SwapCreatedIdentityBeforeProbe { get; set; }

    public bool ForeignOccupantAfterCreate { get; set; }

    public Action? AfterFirstRename { get; set; }

    private string? LastCreatedPath { get; set; }

    public int OpenCount { get; private set; }

    public int FollowOpenCount { get; private set; }

    public int ContentComparisonCount { get; private set; }

    public int HardLinksCreated { get; private set; }

    public int DeleteByHandleCount { get; private set; }

    public int PathDeleteCount { get; private set; }

    public int RecycleDispatchCount { get; private set; }

    public string? LastSymbolicTarget { get; private set; }

    public List<(string Source, string Destination)> Renames { get; } = [];

    public IReadOnlyCollection<string> Paths => _entries.Keys;

    public void AddOrdinaryFile(string path, ulong identity, byte[] content)
    {
        string canonical = Path.GetFullPath(path);
        _entries[canonical] = new Entry(
            new FileLinkFileInfo(
                Identity(identity, 7),
                content.Length,
                new DateTime(2026, 8, 8, 9, 30, 0, DateTimeKind.Utc),
                FileAttributes.Archive,
                1,
                "NTFS",
                true),
            new FileLinkSecurityInfo([1], [2], true, [3, 4], 0),
            content,
            "streams",
            null);
    }

    public bool ContainsPath(string path) => _entries.ContainsKey(Path.GetFullPath(path));

    public IFileLinkHandle OpenNoFollow(string path, bool requestDelete)
    {
        OpenCount++;
        string canonical = Path.GetFullPath(path);
        if (!_entries.TryGetValue(canonical, out Entry? entry))
        {
            throw new FileNotFoundException("Missing fake file.", canonical);
        }

        return new FakeHandle(canonical, entry, requestDelete);
    }

    public IFileLinkHandle OpenFollow(string path)
    {
        FollowOpenCount++;
        string canonical = Path.GetFullPath(path);
        if (!_entries.TryGetValue(canonical, out Entry? entry))
        {
            throw new FileNotFoundException("Missing fake file.", canonical);
        }

        if (entry.FollowTarget is not null)
        {
            entry = _entries[entry.FollowTarget];
        }

        return new FakeHandle(canonical, entry, false);
    }

    public FileLinkFileInfo GetInfo(IFileLinkHandle handle) => GetHandle(handle).Entry.Info;

    public FileLinkSecurityInfo GetSecurityInfo(IFileLinkHandle handle)
    {
        if (SecurityReadException is not null)
        {
            throw SecurityReadException;
        }

        return GetHandle(handle).Entry.Security;
    }

    public bool StreamsEqual(
        string leftPath,
        IFileLinkHandle leftHandle,
        string rightPath,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Entry left = GetHandle(leftHandle).Entry;
        Entry right = GetHandle(rightHandle).Entry;
        if (left.StreamsError || right.StreamsError)
        {
            throw new IOException("Stream enumeration failed.");
        }

        return string.Equals(left.StreamTag, right.StreamTag, StringComparison.Ordinal);
    }

    public bool ContentEquals(
        IFileLinkHandle leftHandle,
        IFileLinkHandle rightHandle,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContentComparisonCount++;
        if (SwapCreatedPathOnContentCall == ContentComparisonCount &&
            LastCreatedPath is { } createdPath &&
            _entries.TryGetValue(createdPath, out Entry? createdEntry))
        {
            _entries[createdPath] = createdEntry with
            {
                Info = createdEntry.Info with { Identity = Identity(902, 7) },
            };
        }

        if (ContentMismatchOnCall == ContentComparisonCount)
        {
            return false;
        }

        return GetHandle(leftHandle).Entry.Content.AsSpan().SequenceEqual(GetHandle(rightHandle).Entry.Content);
    }

    public void Rename(IFileLinkHandle handle, string destinationPath)
    {
        var fake = GetHandle(handle);
        string source = fake.Path;
        string destination = Path.GetFullPath(destinationPath);
        if (_firstRenameObserved && RestoreException is not null)
        {
            throw RestoreException;
        }

        if (_entries.ContainsKey(destination))
        {
            throw new IOException("Destination already exists.");
        }

        if (!_entries.Remove(source, out Entry? entry))
        {
            throw new IOException("Source entry changed.");
        }

        _entries[destination] = entry;
        fake.Path = destination;
        Renames.Add((source, destination));
        if (!_firstRenameObserved)
        {
            _firstRenameObserved = true;
            AfterFirstRename?.Invoke();
        }
    }

    public IFileLinkHandle CreateHardLinkAndOpen(string linkPath, string existingPath)
    {
        ThrowCreateIfNeeded();
        string link = Path.GetFullPath(linkPath);
        Entry survivor = _entries[Path.GetFullPath(existingPath)];
        IncrementLinkCount(survivor.Info.Identity);
        survivor = survivor with { Info = survivor.Info with { LinkCount = survivor.Info.LinkCount + 1 } };
        _entries[Path.GetFullPath(existingPath)] = survivor;
        var created = survivor with { FollowTarget = null };
        _entries[link] = created;
        LastCreatedPath = link;
        HardLinksCreated++;
        return new FakeHandle(link, created, true);
    }

    public IFileLinkHandle CreateSymbolicLinkAndOpen(string linkPath, string targetPath)
    {
        ThrowCreateIfNeeded();
        string link = Path.GetFullPath(linkPath);
        string target = Path.GetFullPath(targetPath);
        LastSymbolicTarget = target;
        Entry survivor = _entries[target];
        var created = new Entry(
            survivor.Info with
            {
                Identity = Identity((ulong)Interlocked.Increment(ref _nextIdentity), survivor.Info.Identity.VolumeSerialNumber),
                Attributes = FileAttributes.ReparsePoint,
                LinkCount = 1,
            },
            survivor.Security,
            survivor.Content,
            survivor.StreamTag,
            target);
        _entries[link] = created;
        LastCreatedPath = link;
        return new FakeHandle(link, created, true);
    }

    public string? GetSymbolicLinkTarget(string linkPath, IFileLinkHandle linkHandle) =>
        GetHandle(linkHandle).Entry.FollowTarget;

    public string GetFinalPath(IFileLinkHandle handle)
    {
        Entry expected = GetHandle(handle).Entry;
        return _entries
            .Where(pair => ReferenceEquals(pair.Value, expected))
            .Select(static pair => pair.Key)
            .FirstOrDefault() ?? throw new IOException("The anchored file no longer has a pathname.");
    }

    public FileLinkPathProbe ProbeNoFollow(string path)
    {
        string canonical = Path.GetFullPath(path);
        if (!_entries.TryGetValue(canonical, out Entry? entry))
        {
            return new FileLinkPathProbe(FileLinkPathState.Missing, null);
        }

        if (SwapCreatedIdentityBeforeProbe && !canonical.Contains(".duplicates-rollback-", StringComparison.Ordinal))
        {
            SwapCreatedIdentityBeforeProbe = false;
            entry = entry with { Info = entry.Info with { Identity = Identity(900, 7) } };
            _entries[canonical] = entry;
        }

        if (ForeignOccupantAfterCreate && !canonical.Contains(".duplicates-rollback-", StringComparison.Ordinal))
        {
            ForeignOccupantAfterCreate = false;
            entry = entry with { Info = entry.Info with { Identity = Identity(901, 7) } };
            _entries[canonical] = entry;
        }

        return new FileLinkPathProbe(FileLinkPathState.Present, entry.Info.Identity);
    }

    public void DeleteByHandle(IFileLinkHandle handle)
    {
        DeleteByHandleCount++;
        var fake = GetHandle(handle);
        if (_entries.TryGetValue(fake.Path, out Entry? current) && ReferenceEquals(current, fake.Entry))
        {
            _entries.Remove(fake.Path);
        }
    }

    public void SendToRecycleBin(string path, Action verifyOwnershipAfterShellItemCreation)
    {
        string canonical = Path.GetFullPath(path);
        if (ReplaceWithForeignBeforeRecycleException && _entries.TryGetValue(canonical, out Entry? entry))
        {
            _entries[canonical] = entry with { Info = entry.Info with { Identity = Identity(999, 7) } };
        }

        verifyOwnershipAfterShellItemCreation();
        RecycleDispatchCount++;
        if (RemoveBeforeRecycleException)
        {
            _entries.Remove(canonical);
        }

        if (RecycleException is not null)
        {
            throw RecycleException;
        }

        _entries.Remove(canonical);
    }

    public void RemoveForRecycle(string path, FileSystemIdentity expectedIdentity)
    {
        string canonical = Path.GetFullPath(path);
        if (_entries.TryGetValue(canonical, out Entry? entry) && entry.Info.Identity == expectedIdentity)
        {
            _entries.Remove(canonical);
        }
    }

    public void ApplyEligibilityFault(
        FileLinkServiceTests.EligibilityFault fault,
        string survivor,
        string duplicate)
    {
        string left = Path.GetFullPath(survivor);
        string right = Path.GetFullPath(duplicate);
        Entry survivorEntry = _entries[left];
        Entry duplicateEntry = _entries[right];
        switch (fault)
        {
            case FileLinkServiceTests.EligibilityFault.Length:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { Length = 4 } };
                break;
            case FileLinkServiceTests.EligibilityFault.Modified:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { ModifiedUtc = SnapshotUtc.AddTicks(1) } };
                break;
            case FileLinkServiceTests.EligibilityFault.Reparse:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { Attributes = FileAttributes.ReparsePoint } };
                break;
            case FileLinkServiceTests.EligibilityFault.UnknownAttribute:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { Attributes = (FileAttributes)0x20000000 } };
                break;
            case FileLinkServiceTests.EligibilityFault.AttributesMismatch:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { Attributes = FileAttributes.Hidden } };
                break;
            case FileLinkServiceTests.EligibilityFault.SecurityMismatch:
                duplicateEntry = duplicateEntry with { Security = duplicateEntry.Security with { OwnerSid = [9] } };
                break;
            case FileLinkServiceTests.EligibilityFault.StreamMismatch:
                duplicateEntry = duplicateEntry with { StreamTag = "other" };
                break;
            case FileLinkServiceTests.EligibilityFault.StreamError:
                duplicateEntry = duplicateEntry with { StreamsError = true };
                break;
            case FileLinkServiceTests.EligibilityFault.DuplicateHasMultipleLinks:
                duplicateEntry = duplicateEntry with { Info = duplicateEntry.Info with { LinkCount = 2 } };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(fault));
        }

        _entries[left] = survivorEntry;
        _entries[right] = duplicateEntry;
    }

    public void SetVolume(string path, bool isLocal, string fileSystem, ulong serial)
    {
        string canonical = Path.GetFullPath(path);
        Entry entry = _entries[canonical];
        _entries[canonical] = entry with
        {
            Info = entry.Info with
            {
                Identity = entry.Info.Identity with { VolumeSerialNumber = serial },
                IsLocal = isLocal,
                FileSystemName = fileSystem,
            },
        };
    }

    public void SetLinkCount(string path, uint count)
    {
        string canonical = Path.GetFullPath(path);
        Entry entry = _entries[canonical];
        _entries[canonical] = entry with { Info = entry.Info with { LinkCount = count } };
    }

    public void SetAttributes(string path, FileAttributes attributes)
    {
        string canonical = Path.GetFullPath(path);
        Entry entry = _entries[canonical];
        _entries[canonical] = entry with { Info = entry.Info with { Attributes = attributes } };
    }

    public void SetSecurity(string path, FileLinkSecurityInfo security)
    {
        string canonical = Path.GetFullPath(path);
        Entry entry = _entries[canonical];
        _entries[canonical] = entry with { Security = security };
    }

    private static DateTime SnapshotUtc => new(2026, 8, 8, 9, 30, 0, DateTimeKind.Utc);

    private static FileSystemIdentity Identity(ulong low, ulong volume) => new(volume, low, 0);

    private static FakeHandle GetHandle(IFileLinkHandle handle) => Assert.IsType<FakeHandle>(handle);

    private void ThrowCreateIfNeeded()
    {
        if (CreateException is not null)
        {
            throw CreateException;
        }
    }

    private void IncrementLinkCount(FileSystemIdentity identity)
    {
        foreach ((string path, Entry entry) in _entries.ToArray())
        {
            if (entry.Info.Identity == identity)
            {
                _entries[path] = entry with { Info = entry.Info with { LinkCount = entry.Info.LinkCount + 1 } };
            }
        }
    }

    private sealed record Entry(
        FileLinkFileInfo Info,
        FileLinkSecurityInfo Security,
        byte[] Content,
        string StreamTag,
        string? FollowTarget)
    {
        public bool StreamsError { get; init; }
    }

    private sealed class FakeHandle(string path, Entry entry, bool deleteAccess) : IFileLinkHandle
    {
        public string Path { get; set; } = path;

        public Entry Entry { get; } = entry;

        public bool DeleteAccess { get; } = deleteAccess;

        public void Dispose()
        {
        }
    }
}
