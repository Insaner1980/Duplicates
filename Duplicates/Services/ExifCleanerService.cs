using Duplicates.Engine.Analysis;
using Duplicates.Engine.Analysis.Analyzers;

namespace Duplicates.Services;

public sealed class ExifCleanerService : IExifCleanerService
{
    private static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
    };

    private static readonly HashSet<string> TiffExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".tif",
        ".tiff",
    };

    private readonly IWicMetadataBackend _metadataBackend;
    private readonly IIdentityFileTransactions _transactions;
    private readonly IRecycleBinService _recycleBinService;

    internal ExifCleanerService(
        IWicMetadataBackend metadataBackend,
        IIdentityFileTransactions transactions,
        IRecycleBinService recycleBinService)
    {
        _metadataBackend = metadataBackend;
        _transactions = transactions;
        _recycleBinService = recycleBinService;
    }

    public async Task<ExifCleanResult> CleanAsync(
        ExifCleanRequest request,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        string sourcePath;
        try
        {
            sourcePath = Path.GetFullPath(request.SourcePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result(ExifCleanOutcome.Failed, request.SourcePath, "The source path is invalid.");
        }

        if (!string.Equals(sourcePath, request.SourcePath, StringComparison.OrdinalIgnoreCase))
        {
            return Result(ExifCleanOutcome.Failed, sourcePath, "The source path must be canonical.");
        }

        IdentityTrackedFile source;
        try
        {
            source = _transactions.Capture(sourcePath);
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return Result(ExifCleanOutcome.Failed, sourcePath, "The source image could not be opened.");
        }

        if (!MatchesRequest(source, request))
        {
            return Result(ExifCleanOutcome.SourceChanged, sourcePath, "The source image changed since it was queued.");
        }

        if (!IsOrdinaryFile(source))
        {
            return Result(ExifCleanOutcome.UnsupportedFormat, sourcePath, "Only ordinary non-link image files are supported.");
        }

        DetectedFileType? detected;
        try
        {
            detected = await FileSignatureDetector.DetectFileAsync(sourcePath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return Result(ExifCleanOutcome.Failed, sourcePath, "The source image could not be read.");
        }

        if (!TryGetContainer(detected, Path.GetExtension(sourcePath), out WicContainerKind container))
        {
            return Result(ExifCleanOutcome.UnsupportedFormat, sourcePath, "Only content-detected JPEG and single-frame TIFF images are supported.");
        }

        WicImageInspection sourceInspection;
        try
        {
            sourceInspection = _metadataBackend.Inspect(sourcePath);
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return Result(ExifCleanOutcome.Failed, sourcePath, "The source image metadata could not be inspected.");
        }

        if (sourceInspection.Container != container || sourceInspection.RenderState.FrameCount != 1)
        {
            return Result(ExifCleanOutcome.UnsupportedFormat, sourcePath, "Only content-detected JPEG and single-frame TIFF images are supported.");
        }

        string finalPath;
        string tempPath;
        try
        {
            finalPath = GetAvailableOutputPath(sourcePath);
            tempPath = GetAvailableOwnedPath(sourcePath, "temp");
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return Result(ExifCleanOutcome.Failed, sourcePath, "A safe sibling output path could not be selected.");
        }

        IdentityTrackedFile? artifact = null;
        try
        {
            var copyProgress = progress is null
                ? null
                : new MappedProgress(progress, 0, 0.5);
            artifact = _transactions.CreateOwnedNew(tempPath);
            artifact = await _transactions.CopyAndFlushAsync(
                source,
                artifact,
                copyProgress,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            IdentityTrackedFile currentSource = _transactions.Capture(sourcePath);
            if (currentSource.Identity != source.Identity || !MatchesRequest(currentSource, request))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.SourceChanged,
                    sourcePath,
                    "The source image changed during cleaning.",
                    artifact);
            }

            IReadOnlyList<string> selectedQueries = ExifMetadataPolicy.SelectedQueries(container, request.Options);
            try
            {
                using IDisposable mutationGuard = _transactions.GuardOwnedPath(artifact);
                _metadataBackend.RemoveMetadata(artifact.Path, container, selectedQueries);
            }
            catch (WicMetadataLayoutException)
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.UnsupportedMetadataLayout,
                    sourcePath,
                    "The requested metadata cannot be removed safely from this image layout.",
                    artifact);
            }
            progress?.Report(0.65);

            WicImageInspection tempInspection;
            try
            {
                tempInspection = _metadataBackend.Inspect(artifact.Path);
            }
            catch (Exception ex) when (IsOrdinaryFailure(ex))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.VerificationFailed,
                    sourcePath,
                    "The cleaned image could not be reopened for verification.",
                    artifact);
            }

            if (!IsVerified(sourceInspection, tempInspection, selectedQueries))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.VerificationFailed,
                    sourcePath,
                    "The cleaned image did not pass verification.",
                    artifact);
            }

            cancellationToken.ThrowIfCancellationRequested();
            currentSource = _transactions.Capture(sourcePath);
            if (currentSource.Identity != source.Identity || !MatchesRequest(currentSource, request))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.SourceChanged,
                    sourcePath,
                    "The source image changed before publication.",
                    artifact);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (request.Options.ReplaceOriginal)
            {
                return await ReplaceOriginalAsync(
                    source,
                    artifact,
                    sourceInspection,
                    selectedQueries,
                    progress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (_transactions.EntryExistsCaseInsensitive(finalPath))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.DestinationCollision,
                    sourcePath,
                    "The selected output path is no longer available.",
                    artifact);
            }

            cancellationToken.ThrowIfCancellationRequested();
            IdentityMoveResult publication;
            try
            {
                using IDisposable sourceGuard = _transactions.GuardSourceSnapshot(source);
                publication = MoveWithResolution(artifact, finalPath);
            }
            catch (IdentitySourceChangedException)
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.SourceChanged,
                    sourcePath,
                    "The source image changed before publication.",
                    artifact);
            }

            if (publication.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                return CleanupOrRecovery(
                    publication.DestinationOccupied
                        ? ExifCleanOutcome.DestinationCollision
                        : ExifCleanOutcome.Failed,
                    sourcePath,
                    publication.DestinationOccupied
                        ? "The selected output path is no longer available."
                        : "The cleaned image could not be published.",
                    artifact);
            }

            if (publication.CommitState == IdentityMoveCommitState.Indeterminate)
            {
                return RecoveryRequired(
                    sourcePath,
                    "The cleaned image publication could not be resolved safely.",
                    [artifact.Path, finalPath]);
            }

            artifact = publication.File;
            WicImageInspection finalInspection;
            IdentityTrackedFile verifiedArtifact;
            try
            {
                using IDisposable verificationGuard = _transactions.GuardOwnedPath(artifact);
                finalInspection = _metadataBackend.Inspect(artifact.Path);
                verifiedArtifact = _transactions.Capture(artifact.Path);
            }
            catch (Exception ex) when (IsOrdinaryFailure(ex))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.VerificationFailed,
                    sourcePath,
                    "The published image could not be reopened for verification.",
                    artifact);
            }

            if (verifiedArtifact.Identity != artifact.Identity)
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.VerificationFailed,
                    sourcePath,
                    "The published image identity changed during verification.",
                    artifact);
            }

            if (!IsVerified(sourceInspection, finalInspection, selectedQueries))
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.VerificationFailed,
                    sourcePath,
                    "The published image did not pass verification.",
                    artifact);
            }

            progress?.Report(1);
            return new ExifCleanResult(
                ExifCleanOutcome.Succeeded,
                sourcePath,
                finalPath,
                "Image cleaned.",
                []);
        }
        catch (IdentityOwnedCreationRecoveryException exception)
        {
            return RecoveryRequired(
                sourcePath,
                "A newly created EXIF artifact could not be removed safely.",
                [exception.RecoveryPath]);
        }
        catch (OperationCanceledException)
        {
            if (artifact is not null)
            {
                ExifCleanResult cleanup = CleanupOrRecovery(
                    ExifCleanOutcome.Failed,
                    sourcePath,
                    "Image cleaning was cancelled.",
                    artifact);
                if (cleanup.Outcome == ExifCleanOutcome.RecoveryRequired)
                {
                    return cleanup;
                }
            }

            throw;
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return artifact is null
                ? Result(ExifCleanOutcome.Failed, sourcePath, "The image could not be cleaned.")
                : CleanupOrRecovery(
                ExifCleanOutcome.Failed,
                sourcePath,
                "The image could not be cleaned.",
                artifact);
        }
    }

    private async Task<ExifCleanResult> ReplaceOriginalAsync(
        IdentityTrackedFile original,
        IdentityTrackedFile cleaned,
        WicImageInspection sourceInspection,
        IReadOnlyList<string> selectedQueries,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        string sourcePath = original.Path;
        string rollbackPath = GetAvailableOwnedPath(sourcePath, "rollback");
        IdentityTrackedFile? rollback = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IdentityMoveResult rollbackMove = MoveWithResolution(
                original,
                rollbackPath,
                validateSourceSnapshot: true);
            if (rollbackMove.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.Failed,
                    sourcePath,
                    "The original image could not be prepared for replacement.",
                    cleaned);
            }

            if (rollbackMove.CommitState == IdentityMoveCommitState.Indeterminate)
            {
                return RecoveryRequired(
                    sourcePath,
                    "The original image rollback rename could not be resolved safely.",
                    [sourcePath, rollbackPath, cleaned.Path]);
            }

            rollback = rollbackMove.File;
            if (rollback.Identity != original.Identity)
            {
                return RecoveryRequired(
                    sourcePath,
                    "The original image identity could not be confirmed after the rollback rename.",
                    [sourcePath, rollbackPath, cleaned.Path]);
            }

            IdentityMoveResult replacementMove = MoveWithResolution(cleaned, sourcePath);
            if (replacementMove.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                return RestoreReplacement(
                    original,
                    rollback,
                    cleaned,
                    ExifCleanOutcome.Failed,
                    "The cleaned image could not be moved to the original path.",
                    cancellationToken);
            }

            if (replacementMove.CommitState == IdentityMoveCommitState.Indeterminate)
            {
                return RecoveryRequired(
                    sourcePath,
                    "The replacement move could not be resolved safely.",
                    [sourcePath, rollbackPath, cleaned.Path]);
            }

            cleaned = replacementMove.File;
            WicImageInspection finalInspection;
            IdentityTrackedFile verifiedCleaned;
            try
            {
                using IDisposable verificationGuard = _transactions.GuardOwnedPath(cleaned);
                finalInspection = _metadataBackend.Inspect(sourcePath);
                verifiedCleaned = _transactions.Capture(sourcePath);
            }
            catch (Exception ex) when (IsOrdinaryFailure(ex))
            {
                return RestoreReplacement(
                    original,
                    rollback,
                    cleaned,
                    ExifCleanOutcome.VerificationFailed,
                    "The replacement image could not be reopened for verification.",
                    cancellationToken);
            }

            if (verifiedCleaned.Identity != cleaned.Identity)
            {
                return RestoreReplacement(
                    original,
                    rollback,
                    cleaned,
                    ExifCleanOutcome.VerificationFailed,
                    "The replacement image identity changed during verification.",
                    cancellationToken);
            }

            if (!IsVerified(sourceInspection, finalInspection, selectedQueries))
            {
                return RestoreReplacement(
                    original,
                    rollback,
                    cleaned,
                    ExifCleanOutcome.VerificationFailed,
                    "The replacement image did not pass verification.",
                    cancellationToken);
            }

            try
            {
                await _recycleBinService.RecycleFileAsync(
                    rollback.Path,
                    original.Identity,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsOrdinaryFailure(ex) || ex is InvalidOperationException)
            {
                return RestoreReplacement(
                    original,
                    rollback,
                    cleaned,
                    ExifCleanOutcome.Failed,
                    "The original image could not be recycled. The original was restored.",
                    cancellationToken);
            }

            progress?.Report(1);
            return new ExifCleanResult(
                ExifCleanOutcome.Succeeded,
                sourcePath,
                sourcePath,
                "Image cleaned and original replaced.",
                []);
        }
        catch (IdentitySourceChangedException) when (rollback is null)
        {
            return CleanupOrRecovery(
                ExifCleanOutcome.SourceChanged,
                sourcePath,
                "The source image changed before replacement.",
                cleaned);
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            if (rollback is null)
            {
                return CleanupOrRecovery(
                    ExifCleanOutcome.Failed,
                    sourcePath,
                    "The original image could not be prepared for replacement.",
                    cleaned);
            }

            return RestoreReplacement(
                original,
                rollback,
                cleaned,
                ExifCleanOutcome.Failed,
                "The replacement transaction failed. The original was restored.",
                cancellationToken);
        }
    }

    private ExifCleanResult RestoreReplacement(
        IdentityTrackedFile original,
        IdentityTrackedFile rollback,
        IdentityTrackedFile cleaned,
        ExifCleanOutcome safeOutcome,
        string detail,
        CancellationToken cancellationToken)
    {
        string sourcePath = original.Path;
        string rollbackPath = rollback.Path;
        string? asidePath = null;
        try
        {
            IdentityPathProbe sourceProbe = _transactions.Probe(sourcePath);
            if (sourceProbe.State == IdentityPathState.Present && sourceProbe.Identity == cleaned.Identity)
            {
                asidePath = GetAvailableOwnedPath(sourcePath, "cleaned-aside");
                IdentityMoveResult asideMove = MoveWithResolution(cleaned, asidePath);
                if (asideMove.CommitState != IdentityMoveCommitState.Committed)
                {
                    return RecoveryRequired(
                        sourcePath,
                        "The cleaned replacement could not be moved aside safely.",
                        [sourcePath, rollbackPath, asidePath]);
                }

                cleaned = asideMove.File;
                sourceProbe = _transactions.Probe(sourcePath);
            }

            if (sourceProbe.State == IdentityPathState.Missing)
            {
                IdentityPathProbe rollbackProbe = _transactions.Probe(rollbackPath);
                if (rollbackProbe.State != IdentityPathState.Present || rollbackProbe.Identity != original.Identity)
                {
                    return RecoveryRequired(
                        sourcePath,
                        "The original image could not be restored safely.",
                        [sourcePath, rollbackPath, cleaned.Path]);
                }

                IdentityMoveResult restoreMove = MoveWithResolution(rollback, sourcePath);
                if (restoreMove.CommitState != IdentityMoveCommitState.Committed)
                {
                    return RecoveryRequired(
                        sourcePath,
                        "The original image could not be restored safely.",
                        [sourcePath, rollbackPath, cleaned.Path]);
                }

                rollback = restoreMove.File;
                sourceProbe = _transactions.Probe(sourcePath);
            }

            if (sourceProbe.State != IdentityPathState.Present || sourceProbe.Identity != original.Identity)
            {
                return RecoveryRequired(
                    sourcePath,
                    "The original image could not be restored safely.",
                    [sourcePath, rollbackPath, cleaned.Path]);
            }

            ExifCleanResult cleanup = CleanupOrRecovery(safeOutcome, sourcePath, detail, cleaned);
            if (cleanup.Outcome == ExifCleanOutcome.RecoveryRequired)
            {
                return RecoveryRequired(
                    sourcePath,
                    cleanup.Detail,
                    [sourcePath, rollbackPath, asidePath ?? cleaned.Path]);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return cleanup;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsOrdinaryFailure(ex))
        {
            return RecoveryRequired(
                sourcePath,
                "The original image could not be restored safely.",
                [sourcePath, rollbackPath, asidePath ?? cleaned.Path]);
        }
    }

    private string GetAvailableOutputPath(string sourcePath)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string extension = Path.GetExtension(sourcePath);
        string stem = Path.GetFileNameWithoutExtension(sourcePath);
        int suffix = 1;
        while (true)
        {
            string name = suffix == 1
                ? $"{stem}.clean{extension}"
                : $"{stem}.clean ({suffix}){extension}";
            string candidate = Path.Combine(directory, name);
            if (!_transactions.EntryExistsCaseInsensitive(candidate))
            {
                return candidate;
            }

            suffix++;
        }
    }

    private string GetAvailableOwnedPath(string sourcePath, string kind)
    {
        string directory = Path.GetDirectoryName(sourcePath)!;
        string extension = Path.GetExtension(sourcePath);
        while (true)
        {
            string candidate = Path.Combine(
                directory,
                $".{Path.GetFileNameWithoutExtension(sourcePath)}.duplicates-exif-{kind}-{Guid.NewGuid():N}{extension}");
            if (!_transactions.EntryExistsCaseInsensitive(candidate))
            {
                return candidate;
            }
        }
    }

    private static bool TryGetContainer(
        DetectedFileType? detected,
        string extension,
        out WicContainerKind container)
    {
        if (detected?.Name == "JPEG" && JpegExtensions.Contains(extension))
        {
            container = WicContainerKind.Jpeg;
            return true;
        }

        if (detected?.Name == "TIFF" && TiffExtensions.Contains(extension))
        {
            container = WicContainerKind.Tiff;
            return true;
        }

        container = default;
        return false;
    }

    private static bool MatchesRequest(IdentityTrackedFile file, ExifCleanRequest request) =>
        file.Length == request.ExpectedLength &&
        file.ModifiedUtc == request.ExpectedModifiedUtc;

    private static bool IsVerified(
        WicImageInspection source,
        WicImageInspection output,
        IReadOnlyList<string> selectedQueries)
    {
        if (source.Container != output.Container || !RenderStateEquals(source.RenderState, output.RenderState))
        {
            return false;
        }

        var selected = selectedQueries.ToHashSet(StringComparer.Ordinal);
        if (selected.Any(output.Metadata.ContainsKey))
        {
            return false;
        }

        foreach (string query in ExifMetadataPolicy.AllQueries(source.Container))
        {
            if (selected.Contains(query))
            {
                continue;
            }

            bool sourceContains = source.Metadata.TryGetValue(query, out string? sourceValue);
            bool outputContains = output.Metadata.TryGetValue(query, out string? outputValue);
            if (sourceContains != outputContains ||
                (sourceContains && !string.Equals(sourceValue, outputValue, StringComparison.Ordinal)))
            {
                return false;
            }
        }

        return true;
    }

    private static bool RenderStateEquals(WicRenderState left, WicRenderState right) =>
        left.FrameCount == right.FrameCount &&
        left.Width == right.Width &&
        left.Height == right.Height &&
        left.DpiX.Equals(right.DpiX) &&
        left.DpiY.Equals(right.DpiY) &&
        left.Orientation == right.Orientation &&
        left.PixelChecksum == right.PixelChecksum &&
        left.ColorContexts.SequenceEqual(right.ColorContexts, StringComparer.Ordinal);

    private IdentityMoveResult MoveWithResolution(
        IdentityTrackedFile source,
        string destinationPath,
        bool validateSourceSnapshot = false)
    {
        try
        {
            IdentityMoveResult result = validateSourceSnapshot
                ? _transactions.MoveSourceNoOverwrite(source, destinationPath)
                : _transactions.MoveNoOverwrite(source, destinationPath);
            return result.File.Identity == source.Identity
                ? result with { File = result.File with { Identity = source.Identity } }
                : new IdentityMoveResult(IdentityMoveCommitState.Indeterminate, source);
        }
        catch (IdentityMoveException exception)
        {
            IdentityPathProbe sourceProbe = _transactions.Probe(source.Path);
            IdentityPathProbe destinationProbe = _transactions.Probe(destinationPath);
            bool sourceOwned = sourceProbe.State == IdentityPathState.Present && sourceProbe.Identity == source.Identity;
            bool destinationOwned = destinationProbe.State == IdentityPathState.Present && destinationProbe.Identity == source.Identity;
            bool destinationOccupied = destinationProbe.State == IdentityPathState.Present && !destinationOwned;
            if (!sourceOwned && destinationOwned)
            {
                return new IdentityMoveResult(
                    IdentityMoveCommitState.Committed,
                    source with { Path = Path.GetFullPath(destinationPath) });
            }

            if (sourceOwned && exception.CommitState == IdentityMoveCommitState.NotCommitted)
            {
                return new IdentityMoveResult(
                    IdentityMoveCommitState.NotCommitted,
                    source,
                    destinationOccupied);
            }

            if (sourceOwned && destinationProbe.State == IdentityPathState.Missing)
            {
                return new IdentityMoveResult(IdentityMoveCommitState.NotCommitted, source);
            }

            return new IdentityMoveResult(IdentityMoveCommitState.Indeterminate, source);
        }
    }

    private static bool IsOrdinaryFile(IdentityTrackedFile file) =>
        (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;

    private ExifCleanResult CleanupOrRecovery(
        ExifCleanOutcome safeOutcome,
        string sourcePath,
        string detail,
        IdentityTrackedFile artifact)
    {
        try
        {
            IdentityPathProbe probe = _transactions.Probe(artifact.Path);
            if (probe.State == IdentityPathState.Missing)
            {
                return Result(safeOutcome, sourcePath, detail);
            }

            if (probe.State != IdentityPathState.Present || probe.Identity != artifact.Identity)
            {
                return RecoveryRequired(
                    sourcePath,
                    "An unverified artifact could not be removed with identity certainty.",
                    [artifact.Path]);
            }

            _transactions.DeleteOwned(artifact);
            IdentityPathProbe afterDelete = _transactions.Probe(artifact.Path);
            return afterDelete.State == IdentityPathState.Missing
                ? Result(safeOutcome, sourcePath, detail)
                : RecoveryRequired(
                    sourcePath,
                    "An unverified artifact could not be removed safely.",
                    [artifact.Path]);
        }
        catch
        {
            return RecoveryRequired(
                sourcePath,
                "An unverified artifact could not be removed safely.",
                [artifact.Path]);
        }
    }

    private ExifCleanResult RecoveryRequired(
        string sourcePath,
        string detail,
        IEnumerable<string> candidates)
    {
        var paths = new List<string>();
        foreach (string path in candidates.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (_transactions.Probe(path).State != IdentityPathState.Missing)
                {
                    paths.Add(path);
                }
            }
            catch
            {
                paths.Add(path);
            }
        }

        return new ExifCleanResult(
            ExifCleanOutcome.RecoveryRequired,
            sourcePath,
            null,
            detail,
            paths);
    }

    private static ExifCleanResult Result(ExifCleanOutcome outcome, string sourcePath, string detail) =>
        new(outcome, sourcePath, null, detail, []);

    private static bool IsOrdinaryFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException or System.Runtime.InteropServices.COMException;

    private sealed class MappedProgress(
        IProgress<double> inner,
        double minimum,
        double maximum) : IProgress<double>
    {
        public void Report(double value) =>
            inner.Report(minimum + (maximum - minimum) * Math.Clamp(value, 0, 1));
    }
}
