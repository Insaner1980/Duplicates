using System.ComponentModel;
using System.Security;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed class FileLinkService : IFileLinkService
{
    private const uint MaximumNtfsLinkCount = 1024;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes Pinned = (FileAttributes)0x00080000;
    private const FileAttributes Unpinned = (FileAttributes)0x00100000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    private const FileAttributes RejectedAttributes =
        FileAttributes.Directory |
        FileAttributes.Device |
        FileAttributes.ReparsePoint |
        FileAttributes.Encrypted |
        FileAttributes.Offline |
        FileAttributes.IntegrityStream |
        RecallOnOpen |
        RecallOnDataAccess |
        Pinned |
        Unpinned;

    private const FileAttributes EqualAttributes =
        FileAttributes.ReadOnly |
        FileAttributes.Hidden |
        FileAttributes.System |
        FileAttributes.Archive |
        FileAttributes.Temporary |
        FileAttributes.SparseFile |
        FileAttributes.Compressed |
        FileAttributes.NotContentIndexed |
        FileAttributes.NoScrubData;

    private const FileAttributes KnownAttributes =
        RejectedAttributes |
        EqualAttributes |
        FileAttributes.Normal;

    private readonly IFileLinkPlatform _platform;
    private readonly IRecycleBinService _recycleBinService;

    public FileLinkService()
        : this(new FileLinkNative(), new RecycleBinService())
    {
    }

    internal FileLinkService(
        IFileLinkPlatform platform,
        IRecycleBinService recycleBinService)
    {
        _platform = platform;
        _recycleBinService = recycleBinService;
    }

    public Task<FileOperationSummary> ReplaceWithLinksAsync(
        IReadOnlyList<LinkReplacementGroup> groups,
        LinkReplacementMode mode,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidatedGroup[] snapshot = CopyAndValidate(groups, mode);
        return Task.Run(
            () => Execute(snapshot, mode, progress, cancellationToken),
            cancellationToken);
    }

    private FileOperationSummary Execute(
        IReadOnlyList<ValidatedGroup> groups,
        LinkReplacementMode mode,
        IProgress<FileOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        RejectPhysicalAliases(groups);
        int totalCount = groups.Sum(static group => group.Duplicates.Length);
        var results = new List<FileOperationResult>(totalCount);
        long succeededBytes = 0;
        int processedCount = 0;

        foreach (ValidatedGroup group in groups)
        {
            foreach (ValidatedFile duplicate in group.Duplicates)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    ThrowCancellation(results, succeededBytes, cancellationToken);
                }

                ItemOutcome outcome;
                try
                {
                    outcome = ReplaceOne(group.Survivor, duplicate, mode, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    ThrowCancellation(results, succeededBytes, cancellationToken);
                    throw;
                }

                results.Add(outcome.Result);
                if (outcome.Result.Succeeded)
                {
                    succeededBytes += duplicate.ExpectedLength;
                }

                processedCount++;
                progress?.Report(new FileOperationProgress(
                    processedCount,
                    totalCount,
                    duplicate.FullPath,
                    succeededBytes));

                if (outcome.IsFatal)
                {
                    return new FileOperationSummary(results.ToArray(), succeededBytes);
                }
            }
        }

        return new FileOperationSummary(results.ToArray(), succeededBytes);
    }

    private ItemOutcome ReplaceOne(
        ValidatedFile survivorRequest,
        ValidatedFile duplicateRequest,
        LinkReplacementMode mode,
        CancellationToken cancellationToken)
    {
        IFileLinkHandle? survivor = null;
        IFileLinkHandle? duplicate = null;
        IFileLinkHandle? createdLink = null;
        FileSystemIdentity duplicateIdentity = default;
        FileSystemIdentity createdIdentity = default;
        string? rollbackPath = null;
        bool renamed = false;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            survivor = _platform.OpenNoFollow(survivorRequest.FullPath, requestDelete: false);
            duplicate = _platform.OpenNoFollow(duplicateRequest.FullPath, requestDelete: true);
            FileLinkFileInfo survivorInfo = _platform.GetInfo(survivor);
            FileLinkFileInfo duplicateInfo = _platform.GetInfo(duplicate);
            duplicateIdentity = duplicateInfo.Identity;
            EnsureEligible(
                (survivorRequest, survivor, survivorInfo),
                (duplicateRequest, duplicate, duplicateInfo),
                mode,
                cancellationToken);

            rollbackPath = BuildRollbackPath(duplicateRequest.FullPath);
            _platform.Rename(duplicate, rollbackPath);
            renamed = true;

            EnsureRollbackOwned(rollbackPath, duplicate, duplicateIdentity);
            if (!_platform.ContentEquals(survivor, duplicate, CancellationToken.None))
            {
                throw new IOException("The rollback file no longer matches the survivor.");
            }

            createdLink = CreateLink(survivor, survivorRequest.FullPath, duplicateRequest.FullPath, mode);

            createdIdentity = _platform.GetInfo(createdLink).Identity;
            VerifyCreatedLink(
                createdLink,
                createdIdentity,
                duplicateRequest.FullPath,
                survivor,
                survivorInfo.Identity,
                survivorRequest.FullPath,
                mode);
            EnsureFinalRollbackEquivalence(
                survivorRequest,
                survivor,
                survivorInfo.Identity,
                duplicateRequest,
                rollbackPath,
                duplicate,
                duplicateIdentity);
            EnsureCreatedLinkOwned(
                duplicateRequest.FullPath,
                createdLink,
                createdIdentity);

            return RecycleRollback(duplicateRequest, rollbackPath, duplicateIdentity);
        }
        catch (OperationCanceledException) when (!renamed)
        {
            throw;
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            string reason = MapFailureReason(exception, mode);
            if (!renamed &&
                duplicate is not null &&
                rollbackPath is not null &&
                !string.Equals(
                    duplicate.Path,
                    duplicateRequest.FullPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                renamed = true;
                rollbackPath = Path.GetFullPath(duplicate.Path);
            }

            if (!renamed || rollbackPath is null || duplicate is null)
            {
                return Failure(duplicateRequest, reason);
            }

            RollbackOutcome rollback = TryRollback(
                duplicateRequest.FullPath,
                rollbackPath,
                duplicate,
                duplicateIdentity,
                (createdLink, createdIdentity),
                (survivor, survivorRequest.FullPath),
                mode);
            if (rollback.Succeeded)
            {
                return Failure(duplicateRequest, reason);
            }

            return FatalFailure(
                duplicateRequest,
                $"{reason} {rollback.Reason}",
                rollbackPath);
        }
        finally
        {
            createdLink?.Dispose();
            duplicate?.Dispose();
            survivor?.Dispose();
        }
    }

    private IFileLinkHandle CreateLink(
        IFileLinkHandle survivor,
        string survivorPath,
        string duplicatePath,
        LinkReplacementMode mode)
    {
        if (mode == LinkReplacementMode.HardLink)
        {
            FileLinkFileInfo currentSurvivor = _platform.GetInfo(survivor);
            if (currentSurvivor.LinkCount >= MaximumNtfsLinkCount)
            {
                throw new IOException("The survivor has reached the NTFS hard-link limit of 1024 links.");
            }

            return _platform.CreateHardLinkAndOpen(
                duplicatePath,
                survivorPath);
        }
        else
        {
            return _platform.CreateSymbolicLinkAndOpen(
                duplicatePath,
                survivorPath);
        }

    }

    private ItemOutcome RecycleRollback(
        ValidatedFile duplicateRequest,
        string rollbackPath,
        FileSystemIdentity duplicateIdentity)
    {
        try
        {
            _recycleBinService.RecycleFileAsync(
                rollbackPath,
                duplicateIdentity,
                CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception recycleFailure) when (IsOperationalFailure(recycleFailure))
        {
            FileLinkPathProbe rollback = _platform.ProbeNoFollow(rollbackPath);
            if (rollback.State == FileLinkPathState.Missing)
            {
                return Success(duplicateRequest);
            }

            if (rollback.State != FileLinkPathState.Present || rollback.Identity != duplicateIdentity)
            {
                return FatalFailure(
                    duplicateRequest,
                    "The Recycle Bin result is indeterminate; the rollback file was left for manual recovery.",
                    rollbackPath);
            }

            throw new RecycleRollbackException(recycleFailure);
        }

        return Success(duplicateRequest);
    }

    private void EnsureEligible(
        (ValidatedFile Request, IFileLinkHandle Handle, FileLinkFileInfo Info) survivor,
        (ValidatedFile Request, IFileLinkHandle Handle, FileLinkFileInfo Info) duplicate,
        LinkReplacementMode mode,
        CancellationToken cancellationToken)
    {
        EnsureSnapshot(survivor.Request, survivor.Info);
        EnsureSnapshot(duplicate.Request, duplicate.Info);
        EnsureAllowedAttributes(survivor.Info.Attributes);
        EnsureAllowedAttributes(duplicate.Info.Attributes);
        if ((survivor.Info.Attributes & EqualAttributes) !=
            (duplicate.Info.Attributes & EqualAttributes))
        {
            throw new IOException("The file attributes no longer match exactly.");
        }

        if (duplicate.Info.LinkCount != 1)
        {
            throw new IOException("The duplicate already has more than one physical hard link.");
        }

        if (mode == LinkReplacementMode.HardLink)
        {
            if (!survivor.Info.IsLocal || !duplicate.Info.IsLocal ||
                !string.Equals(survivor.Info.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(duplicate.Info.FileSystemName, "NTFS", StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Hard-link replacement requires two files on a local NTFS volume.");
            }

            if (survivor.Info.Identity.VolumeSerialNumber != duplicate.Info.Identity.VolumeSerialNumber)
            {
                throw new IOException("Hard-link replacement requires the survivor and duplicate on the same volume.");
            }

            if (survivor.Info.LinkCount >= MaximumNtfsLinkCount)
            {
                throw new IOException("The survivor has reached the NTFS hard-link limit of 1024 links.");
            }
        }

        FileLinkSecurityInfo survivorSecurity = _platform.GetSecurityInfo(survivor.Handle);
        FileLinkSecurityInfo duplicateSecurity = _platform.GetSecurityInfo(duplicate.Handle);
        if (!SecurityEquals(survivorSecurity, duplicateSecurity))
        {
            throw new IOException("The owner, group, DACL, or DACL control state no longer matches.");
        }

        if (!_platform.StreamsEqual(
                survivor.Request.FullPath,
                survivor.Handle,
                duplicate.Request.FullPath,
                duplicate.Handle,
                cancellationToken))
        {
            throw new IOException("The alternate data streams no longer match exactly.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!_platform.ContentEquals(survivor.Handle, duplicate.Handle, cancellationToken))
        {
            throw new IOException("The files are no longer byte-identical.");
        }
    }

    private void VerifyCreatedLink(
        IFileLinkHandle linkHandle,
        FileSystemIdentity linkIdentity,
        string linkPath,
        IFileLinkHandle survivorHandle,
        FileSystemIdentity survivorIdentity,
        string survivorPath,
        LinkReplacementMode mode)
    {
        FileLinkPathProbe before = _platform.ProbeNoFollow(linkPath);
        if (before.State != FileLinkPathState.Present || before.Identity != linkIdentity)
        {
            throw new IOException("The created link entry changed before verification.");
        }

        if (mode == LinkReplacementMode.HardLink)
        {
            if (linkIdentity != survivorIdentity ||
                !_platform.ContentEquals(linkHandle, survivorHandle, CancellationToken.None))
            {
                throw new IOException("The created hard link does not share the survivor's physical identity.");
            }
        }
        else
        {
            string? target = _platform.GetSymbolicLinkTarget(linkPath, linkHandle);
            if (target is null ||
                !string.Equals(Path.GetFullPath(target), survivorPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The created symbolic link does not target the selected survivor.");
            }

            using IFileLinkHandle followed = _platform.OpenFollow(linkPath);
            if (_platform.GetInfo(followed).Identity != survivorIdentity ||
                !_platform.ContentEquals(followed, survivorHandle, CancellationToken.None))
            {
                throw new IOException("The created symbolic link does not resolve to the selected survivor.");
            }
        }

        FileLinkPathProbe after = _platform.ProbeNoFollow(linkPath);
        if (after.State != FileLinkPathState.Present || after.Identity != linkIdentity)
        {
            throw new IOException("The created link entry changed during verification.");
        }
    }

    private void EnsureFinalRollbackEquivalence(
        ValidatedFile survivorRequest,
        IFileLinkHandle survivorHandle,
        FileSystemIdentity survivorIdentity,
        ValidatedFile duplicateRequest,
        string rollbackPath,
        IFileLinkHandle rollbackHandle,
        FileSystemIdentity rollbackIdentity)
    {
        FileLinkFileInfo survivorInfo = _platform.GetInfo(survivorHandle);
        FileLinkFileInfo rollbackInfo = _platform.GetInfo(rollbackHandle);
        if (survivorInfo.Identity != survivorIdentity ||
            rollbackInfo.Identity != rollbackIdentity ||
            _platform.ProbeNoFollow(survivorRequest.FullPath) is not
            { State: FileLinkPathState.Present, Identity: { } currentSurvivor } ||
            currentSurvivor != survivorIdentity ||
            _platform.ProbeNoFollow(rollbackPath) is not
            { State: FileLinkPathState.Present, Identity: { } currentRollback } ||
            currentRollback != rollbackIdentity)
        {
            throw new IOException("A transaction identity changed before the Recycle Bin handoff.");
        }

        EnsureSnapshot(survivorRequest, survivorInfo);
        EnsureSnapshot(duplicateRequest, rollbackInfo);
        EnsureAllowedAttributes(survivorInfo.Attributes);
        EnsureAllowedAttributes(rollbackInfo.Attributes);
        if ((survivorInfo.Attributes & EqualAttributes) !=
            (rollbackInfo.Attributes & EqualAttributes) ||
            !SecurityEquals(
                _platform.GetSecurityInfo(survivorHandle),
                _platform.GetSecurityInfo(rollbackHandle)) ||
            !_platform.StreamsEqual(
                survivorRequest.FullPath,
                survivorHandle,
                rollbackPath,
                rollbackHandle,
                CancellationToken.None) ||
            !_platform.ContentEquals(survivorHandle, rollbackHandle, CancellationToken.None))
        {
            throw new IOException(
                "The rollback file no longer matches the survivor before the Recycle Bin handoff.");
        }
    }

    private void EnsureCreatedLinkOwned(
        string linkPath,
        IFileLinkHandle linkHandle,
        FileSystemIdentity linkIdentity)
    {
        FileLinkPathProbe probe = _platform.ProbeNoFollow(linkPath);
        if (probe.State != FileLinkPathState.Present ||
            probe.Identity != linkIdentity ||
            _platform.GetInfo(linkHandle).Identity != linkIdentity)
        {
            throw new IOException("The created link entry changed before the Recycle Bin handoff.");
        }
    }

    private RollbackOutcome TryRollback(
        string originalPath,
        string rollbackPath,
        IFileLinkHandle rollbackHandle,
        FileSystemIdentity rollbackIdentity,
        (IFileLinkHandle? Handle, FileSystemIdentity Identity) created,
        (IFileLinkHandle? Handle, string Path) survivor,
        LinkReplacementMode mode)
    {
        try
        {
            FileLinkPathProbe rollbackProbe = _platform.ProbeNoFollow(rollbackPath);
            if (rollbackProbe.State != FileLinkPathState.Present ||
                rollbackProbe.Identity != rollbackIdentity ||
                _platform.GetInfo(rollbackHandle).Identity != rollbackIdentity)
            {
                return new RollbackOutcome(false, "The rollback identity is no longer owned by this operation.");
            }

            RollbackOutcome? removalFailure = TryRemoveCreatedLink(originalPath, created, survivor, mode);
            if (removalFailure is not null)
            {
                return removalFailure.Value;
            }

            _platform.Rename(rollbackHandle, originalPath);
            FileLinkPathProbe restored = _platform.ProbeNoFollow(originalPath);
            return restored.State == FileLinkPathState.Present && restored.Identity == rollbackIdentity
                ? new RollbackOutcome(true, string.Empty)
                : new RollbackOutcome(false, "The rollback file could not be restored safely.");
        }
        catch (Exception ex) when (IsOperationalFailure(ex))
        {
            return new RollbackOutcome(false, $"The rollback file could not be restored safely: {ex.Message}");
        }
    }

    private RollbackOutcome? TryRemoveCreatedLink(
        string originalPath,
        (IFileLinkHandle? Handle, FileSystemIdentity Identity) created,
        (IFileLinkHandle? Handle, string Path) survivor,
        LinkReplacementMode mode)
    {
        FileLinkPathProbe originalProbe = _platform.ProbeNoFollow(originalPath);
        if (created.Handle is null)
        {
            return originalProbe.State == FileLinkPathState.Missing
                ? null
                : new RollbackOutcome(false, "Another entry occupies the original path.");
        }

        if (originalProbe.State == FileLinkPathState.Missing)
        {
            created.Handle.Dispose();
            return null;
        }

        if (originalProbe.State != FileLinkPathState.Present || originalProbe.Identity != created.Identity)
        {
            return new RollbackOutcome(false, "Another entry occupies the original path.");
        }

        if (!CanDeleteCreatedLink(originalPath, created.Handle, created.Identity, survivor.Handle, survivor.Path, mode))
        {
            return new RollbackOutcome(false, "The created entry can no longer be proven owned.");
        }

        _platform.DeleteByHandle(created.Handle);
        created.Handle.Dispose();
        originalProbe = _platform.ProbeNoFollow(originalPath);
        return originalProbe.State == FileLinkPathState.Missing
            ? null
            : new RollbackOutcome(false, "The created pathname could not be removed safely.");
    }

    private bool CanDeleteCreatedLink(
        string originalPath,
        IFileLinkHandle createdLink,
        FileSystemIdentity createdIdentity,
        IFileLinkHandle? survivorHandle,
        string survivorPath,
        LinkReplacementMode mode)
    {
        if (_platform.GetInfo(createdLink).Identity != createdIdentity)
        {
            return false;
        }

        if (mode == LinkReplacementMode.HardLink)
        {
            return survivorHandle is not null &&
                _platform.GetInfo(survivorHandle).Identity == createdIdentity;
        }

        string? target = _platform.GetSymbolicLinkTarget(originalPath, createdLink);
        return target is not null &&
            string.Equals(Path.GetFullPath(target), survivorPath, StringComparison.OrdinalIgnoreCase);
    }

    private void RejectPhysicalAliases(IReadOnlyList<ValidatedGroup> groups)
    {
        var identities = new Dictionary<FileSystemIdentity, string>();
        foreach (string path in groups.SelectMany(static group =>
                     group.Duplicates.Prepend(group.Survivor)).Select(static file => file.FullPath))
        {
            try
            {
                using IFileLinkHandle handle = _platform.OpenNoFollow(path, requestDelete: false);
                FileSystemIdentity identity = _platform.GetInfo(handle).Identity;
                if (identities.TryGetValue(identity, out string? existingPath) &&
                    !string.Equals(existingPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        $"The request contains filesystem aliases for '{existingPath}' and '{path}'.",
                        nameof(groups));
                }

                identities[identity] = path;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex) when (IsOperationalFailure(ex))
            {
                // Per-item eligibility reports inaccessible or missing paths without mutating them.
            }
        }
    }

    private static ValidatedGroup[] CopyAndValidate(
        IReadOnlyList<LinkReplacementGroup> groups,
        LinkReplacementMode mode)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        if (groups.Count == 0)
        {
            throw new ArgumentException("At least one replacement group is required.", nameof(groups));
        }

        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var copied = new ValidatedGroup[groups.Count];
        for (int groupIndex = 0; groupIndex < groups.Count; groupIndex++)
        {
            LinkReplacementGroup group = groups[groupIndex] ??
                throw new ArgumentException("Replacement groups cannot contain null elements.", nameof(groups));
            ValidatedFile survivor = ValidateFile(group.Survivor, nameof(groups));
            if (!seenPaths.Add(survivor.FullPath))
            {
                throw new ArgumentException("Replacement paths must be disjoint.", nameof(groups));
            }

            IReadOnlyList<LinkReplacementFile> duplicateSource = group.Duplicates ??
                throw new ArgumentException("A replacement group's duplicate list cannot be null.", nameof(groups));
            if (duplicateSource.Count == 0)
            {
                throw new ArgumentException("Every replacement group requires at least one duplicate.", nameof(groups));
            }

            var duplicates = new ValidatedFile[duplicateSource.Count];
            for (int duplicateIndex = 0; duplicateIndex < duplicateSource.Count; duplicateIndex++)
            {
                ValidatedFile duplicate = ValidateFile(duplicateSource[duplicateIndex], nameof(groups));
                if (!seenPaths.Add(duplicate.FullPath))
                {
                    throw new ArgumentException("Replacement paths must be disjoint.", nameof(groups));
                }

                duplicates[duplicateIndex] = duplicate;
            }

            copied[groupIndex] = new ValidatedGroup(survivor, duplicates);
        }

        return copied;
    }

    private static ValidatedFile ValidateFile(LinkReplacementFile? file, string parameterName)
    {
        if (file is null)
        {
            throw new ArgumentException("Replacement file entries cannot be null.", parameterName);
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(file.FullPath);
        if (!Path.IsPathFullyQualified(file.FullPath))
        {
            throw new ArgumentException("Replacement paths must be fully qualified.", parameterName);
        }

        if (file.ExpectedLength < 0)
        {
            throw new ArgumentException("Expected file lengths cannot be negative.", parameterName);
        }

        DateTime modifiedUtc = file.ExpectedModifiedUtc.Kind == DateTimeKind.Utc
            ? file.ExpectedModifiedUtc
            : file.ExpectedModifiedUtc.ToUniversalTime();
        return new ValidatedFile(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(file.FullPath)),
            file.ExpectedLength,
            modifiedUtc);
    }

    private static void EnsureSnapshot(ValidatedFile request, FileLinkFileInfo info)
    {
        if (info.Length != request.ExpectedLength ||
            info.ModifiedUtc.ToUniversalTime().Ticks != request.ExpectedModifiedUtc.Ticks)
        {
            throw new IOException("The file changed since the duplicate scan.");
        }
    }

    private static void EnsureAllowedAttributes(FileAttributes attributes)
    {
        if ((attributes & RejectedAttributes) != 0 ||
            (attributes & ~KnownAttributes) != 0)
        {
            throw new IOException("The file has unsupported or unsafe attributes.");
        }
    }

    private static bool SecurityEquals(FileLinkSecurityInfo left, FileLinkSecurityInfo right) =>
        left.DaclPresent == right.DaclPresent &&
        left.DaclControl == right.DaclControl &&
        left.OwnerSid.AsSpan().SequenceEqual(right.OwnerSid) &&
        left.GroupSid.AsSpan().SequenceEqual(right.GroupSid) &&
        left.Dacl.AsSpan().SequenceEqual(right.Dacl);

    private void EnsureRollbackOwned(
        string rollbackPath,
        IFileLinkHandle rollbackHandle,
        FileSystemIdentity expectedIdentity)
    {
        FileLinkPathProbe probe = _platform.ProbeNoFollow(rollbackPath);
        if (probe.State != FileLinkPathState.Present ||
            probe.Identity != expectedIdentity ||
            _platform.GetInfo(rollbackHandle).Identity != expectedIdentity)
        {
            throw new IOException("The rollback path identity changed after rename.");
        }
    }

    private static string BuildRollbackPath(string originalPath)
    {
        string directory = Path.GetDirectoryName(originalPath) ??
            throw new IOException("The duplicate path has no parent directory.");
        return Path.Combine(directory, $".duplicates-rollback-{Guid.NewGuid():N}");
    }

    private static string MapFailureReason(Exception exception, LinkReplacementMode mode)
    {
        Exception source = exception is RecycleRollbackException recycle
            ? recycle.InnerException ?? recycle
            : exception;
        if (mode == LinkReplacementMode.SymbolicLink &&
            source is Win32Exception { NativeErrorCode: 1314 })
        {
            return "Windows could not create the symbolic link. Enable Developer Mode or run with existing link privilege; Duplicates never requests elevation.";
        }

        return source.Message;
    }

    private static bool IsOperationalFailure(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        SecurityException or
        Win32Exception or
        InvalidOperationException or
        NotSupportedException;

    private static void ThrowCancellation(
        List<FileOperationResult> results,
        long succeededBytes,
        CancellationToken cancellationToken)
    {
        if (results.Count > 0)
        {
            throw new FileOperationCanceledException(
                new FileOperationSummary(results.ToArray(), succeededBytes),
                cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new OperationCanceledException(cancellationToken);
    }

    private static ItemOutcome Success(ValidatedFile duplicate) => new(
        new FileOperationResult(duplicate.FullPath, duplicate.FullPath, null),
        false);

    private static ItemOutcome Failure(ValidatedFile duplicate, string reason) => new(
        new FileOperationResult(
            duplicate.FullPath,
            null,
            new FileActionFailure(duplicate.FullPath, reason)),
        false);

    private static ItemOutcome FatalFailure(
        ValidatedFile duplicate,
        string reason,
        string recoveryPath) => new(
            new FileOperationResult(
                duplicate.FullPath,
                null,
                new FileActionFailure(duplicate.FullPath, reason, recoveryPath)),
            true);

    private sealed record ValidatedFile(
        string FullPath,
        long ExpectedLength,
        DateTime ExpectedModifiedUtc);

    private sealed record ValidatedGroup(
        ValidatedFile Survivor,
        ValidatedFile[] Duplicates);

    private readonly record struct ItemOutcome(FileOperationResult Result, bool IsFatal);

    private readonly record struct RollbackOutcome(bool Succeeded, string Reason);

    private sealed class RecycleRollbackException
        : IOException
    {
        public RecycleRollbackException(Exception innerException)
            : base("The rollback file could not be recycled.", innerException)
        {
        }
    }
}
