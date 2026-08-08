using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.App.Tests;

public sealed class ExifCleanerServiceTests
{
    [Fact]
    public void Contracts_PreserveTheExactRequestSnapshotAndRecoverySurface()
    {
        var options = new ExifCleanOptions(
            RemoveGps: true,
            RemoveDeviceIdentifiers: true,
            RemoveDates: true,
            RemoveAuthorAndDescription: true,
            RemoveEmbeddedThumbnail: true,
            RemoveXmpAndIptc: true,
            ReplaceOriginal: false);
        var request = new ExifCleanRequest(
            "C:\\Images\\photo.jpg",
            1234,
            new DateTime(2026, 8, 8, 9, 10, 11, DateTimeKind.Utc),
            options);
        var result = new ExifCleanResult(
            ExifCleanOutcome.RecoveryRequired,
            request.SourcePath,
            null,
            "Manual recovery is required.",
            ["C:\\Images\\photo.duplicates-exif-rollback"]);

        Assert.Equal("C:\\Images\\photo.jpg", request.SourcePath);
        Assert.Equal(1234, request.ExpectedLength);
        Assert.True(request.Options.RemoveXmpAndIptc);
        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Single(result.RecoveryPaths);
        Assert.NotNull(typeof(IWicMetadataBackend));
        Assert.NotNull(typeof(IExifFileTransactions));
    }

    [Fact]
    public async Task CleanAsync_DefaultJpegCopyUsesExactQueriesAndLeavesSourceBytesUntouched()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("photo.jpg", [0xFF, 0xD8, 0xFF, 1, 2, 3, 0xFF, 0xD9]);
        byte[] sourceBytes = await File.ReadAllBytesAsync(source);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg);
        var transactions = new FakeExifFileTransactions();
        var service = new ExifCleanerService(backend, transactions, new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(source, result.SourcePath);
        Assert.Equal(Path.Combine(fixture.Root, "photo.clean.jpg"), result.OutputPath);
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(source));
        Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(result.OutputPath!));
        Assert.Equal(
            ExifMetadataPolicy.SelectedQueries(WicContainerKind.Jpeg, AllPrivacyOptions()),
            backend.RemovedQueries);
        Assert.Empty(result.RecoveryPaths);
        Assert.Equal(1, transactions.CopyCallCount);
        Assert.Equal(1, transactions.MoveCallCount);
    }

    [Theory]
    [InlineData("image.png", new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]
    [InlineData("image.gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]
    [InlineData("image.jpg", new byte[] { 0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0 })]
    [InlineData("image.bin", new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 })]
    public async Task CleanAsync_UnsupportedOrExtensionMismatchedSignatureFailsClosed(
        string name,
        byte[] bytes)
    {
        using var fixture = new TempFixture();
        string source = fixture.Write(name, bytes);
        var transactions = new FakeExifFileTransactions();
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg);
        var service = new ExifCleanerService(backend, transactions, new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.UnsupportedFormat, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Equal(0, transactions.CopyCallCount);
        Assert.Empty(backend.RemovedQueries);
    }

    [Fact]
    public async Task CleanAsync_DetectedContainerMismatchFailsClosedBeforeCopy()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("mismatch.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions();
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Tiff),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.UnsupportedFormat, result.Outcome);
        Assert.Equal(0, transactions.CopyCallCount);
    }

    [Fact]
    public void SelectedQueries_UsesFormatCorrectPathsWithoutDeletingSharedParentBlocks()
    {
        IReadOnlyList<string> jpeg = ExifMetadataPolicy.SelectedQueries(
            WicContainerKind.Jpeg,
            AllPrivacyOptions());
        IReadOnlyList<string> tiff = ExifMetadataPolicy.SelectedQueries(
            WicContainerKind.Tiff,
            AllPrivacyOptions());

        Assert.Contains("/app1/ifd/gps", jpeg);
        Assert.Contains("/app1/ifd/{ushort=271}", jpeg);
        Assert.Contains("/app1/ifd/exif/{ushort=42037}", jpeg);
        Assert.Contains("/app1/ifd/exif/{ushort=36882}", jpeg);
        Assert.Contains("/app1/ifd/{ushort=40095}", jpeg);
        Assert.Contains("/app1/ifd/exif/{ushort=37510}", jpeg);
        Assert.Contains("/com", jpeg);
        Assert.Contains("/app1/thumb", jpeg);
        Assert.Contains("/xmp", jpeg);
        Assert.Contains("/app13/irb/8bimiptc/iptc", jpeg);
        Assert.DoesNotContain("/app1", jpeg);
        Assert.DoesNotContain("/app13", jpeg);
        Assert.DoesNotContain("/app1/ifd", jpeg);
        Assert.DoesNotContain("/app1/ifd/exif", jpeg);

        Assert.Contains("/ifd/gps", tiff);
        Assert.Contains("/ifd/{ushort=272}", tiff);
        Assert.Contains("/ifd/exif/{ushort=42036}", tiff);
        Assert.Contains("/ifd/thumb", tiff);
        Assert.Contains("/ifd/xmp", tiff);
        Assert.Contains("/ifd/iptc", tiff);
        Assert.Contains("/ifd/irb/8bimiptc/iptc", tiff);
        Assert.DoesNotContain("/ifd", tiff);
        Assert.DoesNotContain("/ifd/exif", tiff);
    }

    [Fact]
    public async Task CleanAsync_SourceIdentitySwapAfterCopyReturnsSourceChangedAndRemovesTemp()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("swap.jpg", [0xFF, 0xD8, 0xFF, 1, 2, 3, 0xFF, 0xD9]);
        DateTime modifiedUtc = File.GetLastWriteTimeUtc(source);
        var transactions = new FakeExifFileTransactions();
        transactions.AfterCopy = (_, _) =>
        {
            string aside = source + ".aside";
            File.Move(source, aside);
            File.WriteAllBytes(source, [0xFF, 0xD8, 0xFF, 9, 8, 7, 0xFF, 0xD9]);
            File.SetLastWriteTimeUtc(source, modifiedUtc);
            File.Delete(aside);
            transactions.ForgetIdentity(source);
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.True(
            result.Outcome == ExifCleanOutcome.SourceChanged,
            $"{result.Outcome}: {result.Detail} [{string.Join(", ", result.RecoveryPaths)}]");
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_FastWriterCapabilityFailureMapsToUnsupportedMetadataLayout()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("layout.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            RemoveFailure = new WicMetadataLayoutException("The metadata layout is not writable."),
        };
        var service = new ExifCleanerService(
            backend,
            new FakeExifFileTransactions(),
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.UnsupportedMetadataLayout, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_PublishedVerificationFailureDeletesOnlyTheOwnedOutput()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("verify.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            BreakRenderOnInspection = 3,
        };
        var service = new ExifCleanerService(
            backend,
            new FakeExifFileTransactions(),
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.VerificationFailed, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_UnverifiedPublishedOutputCleanupFailureRequiresRecovery()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("recover.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            BreakRenderOnInspection = 3,
        };
        var transactions = new FakeExifFileTransactions
        {
            DeleteFailure = new IOException("Delete fault"),
        };
        var service = new ExifCleanerService(backend, transactions, new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal([Path.Combine(fixture.Root, "recover.clean.jpg")], result.RecoveryPaths);
        Assert.True(File.Exists(result.RecoveryPaths[0]));
    }

    [Fact]
    public async Task CleanAsync_ReplaceOriginalRecyclesRollbackOnlyAfterVerifiedCommit()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("replace.jpg", [0xFF, 0xD8, 0xFF, 1, 0xFF, 0xD9]);
        byte[] originalBytes = await File.ReadAllBytesAsync(source);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            MutateEditedFile = true,
        };
        var recycle = new RecordingRecycleBinService();
        var service = new ExifCleanerService(backend, new FakeExifFileTransactions(), recycle);
        ExifCleanOptions options = AllPrivacyOptions() with { ReplaceOriginal = true };

        ExifCleanResult result = await service.CleanAsync(
            Request(source, options),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(source, result.OutputPath);
        Assert.Single(recycle.Calls);
        Assert.Contains("duplicates-exif-rollback", recycle.Calls[0].Path, StringComparison.Ordinal);
        Assert.NotEqual(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_ReplaceRecycleFailureRestoresOriginalWithoutResidue()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("restore.jpg", [0xFF, 0xD8, 0xFF, 1, 0xFF, 0xD9]);
        byte[] originalBytes = await File.ReadAllBytesAsync(source);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            MutateEditedFile = true,
        };
        var recycle = new RecordingRecycleBinService
        {
            Failure = new IOException("Recycle fault"),
        };
        var service = new ExifCleanerService(backend, new FakeExifFileTransactions(), recycle);

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions() with { ReplaceOriginal = true }),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Failed, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(source));
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_CopyFaultAfterOwnedCreationCleansTheKnownTempIdentity()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("partial.jpg", [0xFF, 0xD8, 0xFF, 1, 2, 3, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            CopyFailure = new IOException("Copy fault"),
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Failed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_CopyFaultCleanupFailureReturnsRecoveryRequiredWithTrackedTemp()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("partial-recovery.jpg", [0xFF, 0xD8, 0xFF, 1, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            CopyFailure = new IOException("Copy fault"),
            DeleteFailure = new IOException("Delete fault"),
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.Single(result.RecoveryPaths);
        Assert.Contains("duplicates-exif-temp", result.RecoveryPaths[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CleanAsync_OwnedCreationCleanupFailureReturnsExactRecoveryPath()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("create-recovery.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            FailOwnedCreationCleanup = true,
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.NotNull(transactions.LastCreatedPath);
        Assert.Equal([transactions.LastCreatedPath!], result.RecoveryPaths);
        Assert.True(File.Exists(transactions.LastCreatedPath!));
    }

    [Fact]
    public async Task CleanAsync_MoveThrowsAfterCommitProbesFinalAndCompletesVerification()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("commit.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            MoveThrowState = ExifMoveCommitState.Committed,
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(Path.Combine(fixture.Root, "commit.clean.jpg"), result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
    }

    [Fact]
    public async Task CleanAsync_PostEditInspectionErrorIsVerificationFailedAndCleansArtifact()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("decode.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            ThrowOnInspection = 2,
        };
        var service = new ExifCleanerService(
            backend,
            new FakeExifFileTransactions(),
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.VerificationFailed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Theory]
    [InlineData(true, "/xmp", "disabled-xmp.jpg")]
    [InlineData(false, "/ifd/xmp", "disabled-xmp.tiff")]
    [InlineData(false, "/ifd/thumb", "disabled-thumb.tiff")]
    public async Task CleanAsync_DisabledMetadataSubtreeMutationIsVerificationFailed(
        bool jpeg,
        string subtree,
        string name)
    {
        using var fixture = new TempFixture();
        WicContainerKind container = jpeg ? WicContainerKind.Jpeg : WicContainerKind.Tiff;
        byte[] bytes = jpeg
            ? [0xFF, 0xD8, 0xFF, 0xD9]
            : [0x49, 0x49, 0x2A, 0x00, 8, 0, 0, 0];
        string source = fixture.Write(name, bytes);
        var backend = new FakeWicMetadataBackend(container)
        {
            MutateDisabledSubtree = subtree,
        };
        var service = new ExifCleanerService(
            backend,
            new FakeExifFileTransactions(),
            new NoOpRecycleBinService());
        var options = new ExifCleanOptions(
            RemoveGps: true,
            RemoveDeviceIdentifiers: false,
            RemoveDates: false,
            RemoveAuthorAndDescription: false,
            RemoveEmbeddedThumbnail: false,
            RemoveXmpAndIptc: false,
            ReplaceOriginal: false);

        ExifCleanResult result = await service.CleanAsync(
            Request(source, options),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.VerificationFailed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_DisabledMetadataQueryAddedAfterEditIsVerificationFailed()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("added-disabled.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        const string disabledQuery = "/xmp";
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            AddDisabledSubtree = disabledQuery,
        };
        Assert.True(((Dictionary<string, string>)backend.Inspection.Metadata).Remove(disabledQuery));
        var service = new ExifCleanerService(
            backend,
            new FakeExifFileTransactions(),
            new NoOpRecycleBinService());
        var options = new ExifCleanOptions(
            RemoveGps: true,
            RemoveDeviceIdentifiers: false,
            RemoveDates: false,
            RemoveAuthorAndDescription: false,
            RemoveEmbeddedThumbnail: false,
            RemoveXmpAndIptc: false,
            ReplaceOriginal: false);

        ExifCleanResult result = await service.CleanAsync(
            Request(source, options),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.VerificationFailed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_CancellationAfterRollbackRenameIsDeferredThroughSuccessfulReplacement()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("deferred.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        using var cancellation = new CancellationTokenSource();
        var transactions = new FakeExifFileTransactions
        {
            AfterMove = call =>
            {
                if (call == 1)
                {
                    cancellation.Cancel();
                }
            },
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new RecordingRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions() with { ReplaceOriginal = true }),
            null,
            cancellation.Token);

        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
        Assert.Equal(source, result.OutputPath);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_HoldsOwnedPathGuardAcrossMetadataMutation()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("guard.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg);
        bool swapBlocked = false;
        bool swapSucceeded = false;
        backend.BeforeRemove = path =>
        {
            string aside = path + ".swapped";
            try
            {
                File.Move(path, aside);
                swapSucceeded = true;
                File.WriteAllBytes(path, [9, 8, 7, 6]);
            }
            catch (IOException)
            {
                swapBlocked = true;
            }
        };
        var service = new ExifCleanerService(
            backend,
            new ExifFileTransactions(),
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.True(swapBlocked);
        Assert.False(swapSucceeded);
        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task CleanAsync_HoldsOwnedPathGuardAcrossPublishedOutputInspection()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("publish-guard.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg);
        bool swapBlocked = false;
        backend.BeforeInspect = (count, path) =>
        {
            if (count == 3)
            {
                swapBlocked = !TrySwapPath(path);
            }
        };
        var service = new ExifCleanerService(
            backend,
            new ExifFileTransactions(),
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.True(swapBlocked);
        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task CleanAsync_HoldsOwnedPathGuardAcrossReplacementInspection()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("replace-guard.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg);
        bool swapBlocked = false;
        backend.BeforeInspect = (count, path) =>
        {
            if (count == 3)
            {
                swapBlocked = !TrySwapPath(path);
            }
        };
        var service = new ExifCleanerService(
            backend,
            new ExifFileTransactions(),
            new RecordingRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions() with { ReplaceOriginal = true }),
            null,
            CancellationToken.None);

        Assert.True(swapBlocked);
        Assert.Equal(ExifCleanOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task CleanAsync_PublishedOutputIdentitySwapDuringInspectionRequiresRecovery()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("publish-identity.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions();
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            AfterInspect = (count, path) =>
            {
                if (count == 3)
                {
                    Assert.True(TrySwapPath(path));
                    transactions.ForgetIdentity(path);
                }
            },
        };
        var service = new ExifCleanerService(
            backend,
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.Contains(Path.Combine(fixture.Root, "publish-identity.clean.jpg"), result.RecoveryPaths);
    }

    [Fact]
    public async Task CleanAsync_ReplacementIdentitySwapDuringInspectionRequiresRecovery()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("replace-identity.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions();
        var backend = new FakeWicMetadataBackend(WicContainerKind.Jpeg)
        {
            AfterInspect = (count, path) =>
            {
                if (count == 3)
                {
                    Assert.True(TrySwapPath(path));
                    transactions.ForgetIdentity(path);
                }
            },
        };
        var service = new ExifCleanerService(
            backend,
            transactions,
            new RecordingRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions() with { ReplaceOriginal = true }),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.RecoveryRequired, result.Outcome);
        Assert.Contains(source, result.RecoveryPaths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanAsync_TransactionValidationFailuresCleanTheKnownArtifact(bool win32Failure)
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("transaction-fault.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            CopyFailure = win32Failure
                ? new System.ComponentModel.Win32Exception(5)
                : new InvalidOperationException("Identity validation fault"),
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Failed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_NotCommittedMoveWithDestinationOccupantReturnsCollision()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("collision-race.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            MoveThrowState = ExifMoveCommitState.NotCommitted,
            BeforeMove = (_, destination) => File.WriteAllBytes(destination, [9, 9, 9]),
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.DestinationCollision, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Equal([9, 9, 9], File.ReadAllBytes(Path.Combine(fixture.Root, "collision-race.clean.jpg")));
    }

    [Fact]
    public async Task CleanAsync_NotCommittedMoveFailureWithoutDestinationReturnsFailed()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("move-io.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            MoveThrowState = ExifMoveCommitState.NotCommitted,
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.Failed, result.Outcome);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_CancellationAfterFinalVacancyCheckStopsBeforePublication()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("vacancy-cancel.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        using var cancellation = new CancellationTokenSource();
        var transactions = new FakeExifFileTransactions
        {
            AfterEntryExists = call =>
            {
                if (call == 3)
                {
                    cancellation.Cancel();
                }
            },
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            cancellation.Token));

        Assert.Equal(0, transactions.MoveCallCount);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_SourceMutationAfterFinalVacancyCheckReturnsSourceChanged()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("late-source-change.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        var transactions = new FakeExifFileTransactions
        {
            AfterEntryExists = call =>
            {
                if (call == 3)
                {
                    File.AppendAllText(source, "changed");
                }
            },
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new NoOpRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions()),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.SourceChanged, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    [Fact]
    public async Task CleanAsync_SourceMutationImmediatelyBeforeRollbackRenameReturnsSourceChanged()
    {
        using var fixture = new TempFixture();
        string source = fixture.Write("rollback-source-change.jpg", [0xFF, 0xD8, 0xFF, 0xD9]);
        bool firstMove = true;
        var transactions = new FakeExifFileTransactions
        {
            BeforeMove = (tracked, _) =>
            {
                if (firstMove && string.Equals(tracked.Path, source, StringComparison.OrdinalIgnoreCase))
                {
                    firstMove = false;
                    File.AppendAllText(source, "changed");
                }
            },
        };
        var service = new ExifCleanerService(
            new FakeWicMetadataBackend(WicContainerKind.Jpeg),
            transactions,
            new RecordingRecycleBinService());

        ExifCleanResult result = await service.CleanAsync(
            Request(source, AllPrivacyOptions() with { ReplaceOriginal = true }),
            null,
            CancellationToken.None);

        Assert.Equal(ExifCleanOutcome.SourceChanged, result.Outcome);
        Assert.Null(result.OutputPath);
        Assert.Empty(result.RecoveryPaths);
        Assert.Single(Directory.EnumerateFiles(fixture.Root));
    }

    private static ExifCleanOptions AllPrivacyOptions() => new(
        true,
        true,
        true,
        true,
        true,
        true,
        false);

    private static bool TrySwapPath(string path)
    {
        try
        {
            File.Move(path, path + ".swapped");
            File.WriteAllBytes(path, [9, 8, 7, 6]);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static ExifCleanRequest Request(string path, ExifCleanOptions options)
    {
        var file = new FileInfo(path);
        return new ExifCleanRequest(path, file.Length, file.LastWriteTimeUtc, options);
    }

    private sealed class FakeWicMetadataBackend : IWicMetadataBackend
    {
        private bool _edited;
        private int _inspectionCount;

        public FakeWicMetadataBackend(WicContainerKind container)
        {
            Inspection = new WicImageInspection(
                container,
                new WicRenderState(1, 32, 24, 96, 96, "1", ["icc"], "pixels"),
                ExifMetadataPolicy.AllQueries(container).ToDictionary(
                    static query => query,
                    static query => $"value:{query}",
                    StringComparer.Ordinal));
        }

        public WicImageInspection Inspection { get; }

        public List<string> RemovedQueries { get; } = [];

        public Exception? RemoveFailure { get; set; }

        public int BreakRenderOnInspection { get; set; }

        public int ThrowOnInspection { get; set; }

        public bool MutateEditedFile { get; set; }

        public string? MutateDisabledSubtree { get; set; }

        public string? AddDisabledSubtree { get; set; }

        public Action<string>? BeforeRemove { get; set; }

        public Action<int, string>? BeforeInspect { get; set; }

        public Action<int, string>? AfterInspect { get; set; }

        public WicImageInspection Inspect(string path)
        {
            _inspectionCount++;
            BeforeInspect?.Invoke(_inspectionCount, path);
            if (_inspectionCount == ThrowOnInspection)
            {
                throw new IOException("Verification decode fault");
            }

            WicImageInspection inspection = Inspection;
            if (_inspectionCount == BreakRenderOnInspection)
            {
                inspection = inspection with
                {
                    RenderState = inspection.RenderState with { PixelChecksum = "changed-pixels" },
                };
            }

            if (!_edited)
            {
                AfterInspect?.Invoke(_inspectionCount, path);
                return inspection;
            }

            Dictionary<string, string> metadata = inspection.Metadata.ToDictionary(StringComparer.Ordinal);
            foreach (string query in RemovedQueries)
            {
                metadata.Remove(query);
            }

            if (MutateDisabledSubtree is not null)
            {
                metadata[MutateDisabledSubtree] = $"mutated:{MutateDisabledSubtree}";
            }

            if (AddDisabledSubtree is not null)
            {
                metadata[AddDisabledSubtree] = $"added:{AddDisabledSubtree}";
            }

            WicImageInspection result = inspection with { Metadata = metadata };
            AfterInspect?.Invoke(_inspectionCount, path);
            return result;
        }

        public void RemoveMetadata(
            string path,
            WicContainerKind container,
            IReadOnlyList<string> queries)
        {
            BeforeRemove?.Invoke(path);
            if (RemoveFailure is not null)
            {
                throw RemoveFailure;
            }

            Assert.Equal(Inspection.Container, container);
            RemovedQueries.AddRange(queries);
            _edited = true;
            if (MutateEditedFile)
            {
                File.AppendAllText(path, "cleaned");
            }
        }
    }

    private sealed class FakeExifFileTransactions : IExifFileTransactions
    {
        private readonly Dictionary<string, FileSystemIdentity> _identities =
            new(StringComparer.OrdinalIgnoreCase);
        private ulong _nextIdentity = 1;

        public int CopyCallCount { get; private set; }

        public int MoveCallCount { get; private set; }

        public Action<ExifTrackedFile, ExifTrackedFile>? AfterCopy { get; set; }

        public Exception? DeleteFailure { get; set; }

        public Exception? CopyFailure { get; set; }

        public bool FailOwnedCreationCleanup { get; set; }

        public string? LastCreatedPath { get; private set; }

        public ExifMoveCommitState? MoveThrowState { get; set; }

        public Action<int>? AfterMove { get; set; }

        public Action<ExifTrackedFile, string>? BeforeMove { get; set; }

        public Action<int>? AfterEntryExists { get; set; }

        public int EntryExistsCallCount { get; private set; }

        public void ForgetIdentity(string path) => _identities.Remove(Path.GetFullPath(path));

        public ExifTrackedFile Capture(string path)
        {
            string canonical = Path.GetFullPath(path);
            var info = new FileInfo(canonical);
            if (!info.Exists)
            {
                throw new FileNotFoundException(null, canonical);
            }

            if (!_identities.TryGetValue(canonical, out FileSystemIdentity identity))
            {
                identity = new FileSystemIdentity(1, _nextIdentity++, 0);
                _identities.Add(canonical, identity);
            }

            return new ExifTrackedFile(
                canonical,
                identity,
                info.Length,
                info.LastWriteTimeUtc,
                info.Attributes);
        }

        public ExifTrackedFile CreateOwnedNew(string destinationPath)
        {
            using (new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read))
            {
            }

            ExifTrackedFile created = Capture(destinationPath);
            LastCreatedPath = created.Path;
            if (FailOwnedCreationCleanup)
            {
                throw new ExifOwnedCreationRecoveryException(
                    created.Path,
                    new IOException("Injected identity validation failure"),
                    new IOException("Injected owned cleanup failure"));
            }

            return created;
        }

        public async Task<ExifTrackedFile> CopyAndFlushAsync(
            ExifTrackedFile source,
            ExifTrackedFile destination,
            IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            CopyCallCount++;
            await using (FileStream input = File.OpenRead(source.Path))
            await using (var output = new FileStream(destination.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            progress?.Report(1);
            if (CopyFailure is not null)
            {
                throw CopyFailure is System.ComponentModel.Win32Exception or InvalidOperationException
                    ? new ExifTransactionException("Injected transaction failure", CopyFailure)
                    : CopyFailure;
            }

            ExifTrackedFile copied = Capture(destination.Path);
            AfterCopy?.Invoke(source, copied);
            return copied;
        }

        public bool EntryExistsCaseInsensitive(string path)
        {
            string directory = Path.GetDirectoryName(path)!;
            string name = Path.GetFileName(path);
            bool exists = Directory.EnumerateFileSystemEntries(directory)
                .Any(entry => string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase));
            EntryExistsCallCount++;
            AfterEntryExists?.Invoke(EntryExistsCallCount);
            return exists;
        }

        public IDisposable GuardOwnedPath(ExifTrackedFile file) => new NoOpGuard();

        public IDisposable GuardSourceSnapshot(ExifTrackedFile source)
        {
            ExifTrackedFile current = Capture(source.Path);
            if (!MatchesSnapshot(current, source))
            {
                throw new ExifSourceChangedException();
            }

            return new NoOpGuard();
        }

        public ExifMoveResult MoveNoOverwrite(ExifTrackedFile source, string destinationPath) =>
            MoveNoOverwriteCore(source, destinationPath, validateSnapshot: false);

        public ExifMoveResult MoveSourceNoOverwrite(ExifTrackedFile source, string destinationPath) =>
            MoveNoOverwriteCore(source, destinationPath, validateSnapshot: true);

        private ExifMoveResult MoveNoOverwriteCore(
            ExifTrackedFile source,
            string destinationPath,
            bool validateSnapshot)
        {
            MoveCallCount++;
            BeforeMove?.Invoke(source, destinationPath);
            if (validateSnapshot && !MatchesSnapshot(Capture(source.Path), source))
            {
                throw new ExifSourceChangedException();
            }

            ExifMoveCommitState state = MoveThrowState ?? ExifMoveCommitState.Committed;
            if (state == ExifMoveCommitState.Committed)
            {
                File.Move(source.Path, destinationPath, overwrite: false);
                _identities.Remove(source.Path);
                _identities[destinationPath] = source.Identity;
                AfterMove?.Invoke(MoveCallCount);
            }

            if (MoveThrowState is not null)
            {
                MoveThrowState = null;
                throw new ExifMoveException(
                    "Injected move fault",
                    state,
                    source.Path,
                    destinationPath,
                    source.Identity);
            }

            return new ExifMoveResult(
                state,
                source with { Path = destinationPath });
        }

        private static bool MatchesSnapshot(ExifTrackedFile current, ExifTrackedFile expected) =>
            current.Identity == expected.Identity &&
            current.Length == expected.Length &&
            current.ModifiedUtc == expected.ModifiedUtc;

        public void DeleteOwned(ExifTrackedFile file)
        {
            if (DeleteFailure is not null)
            {
                throw DeleteFailure;
            }

            File.Delete(file.Path);
            _identities.Remove(file.Path);
        }

        public ExifPathProbe Probe(string path)
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return new ExifPathProbe(ExifPathState.Missing, null);
            }

            return new ExifPathProbe(ExifPathState.Present, Capture(path).Identity);
        }

        private sealed class NoOpGuard : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class NoOpRecycleBinService : IRecycleBinService
    {
        public Task RecycleFileAsync(
            string path,
            FileSystemIdentity expectedIdentity,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecordingRecycleBinService : IRecycleBinService
    {
        public Exception? Failure { get; set; }

        public List<(string Path, FileSystemIdentity Identity)> Calls { get; } = [];

        public Task RecycleFileAsync(
            string path,
            FileSystemIdentity expectedIdentity,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((path, expectedIdentity));
            if (Failure is not null)
            {
                throw Failure;
            }

            File.Delete(path);
            return Task.CompletedTask;
        }
    }

    private sealed class TempFixture : IDisposable
    {
        public TempFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "Duplicates-Task17-Red", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string Write(string name, byte[] bytes)
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
