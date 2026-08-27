using System.Collections.ObjectModel;
using Duplicates.Services;

namespace Duplicates.ViewModels;

public sealed class ExactLinkReplacementSnapshot
{
    private readonly ReadOnlyCollection<LinkReplacementGroup> _groups;

    internal ExactLinkReplacementSnapshot(
        ExactResultsSession session,
        LinkReplacementMode mode,
        LinkReplacementGroup[] groups,
        ExactLinkReplacementGroupState[] canonicalStates,
        string confirmationText)
    {
        Session = session;
        Mode = mode;
        _groups = Array.AsReadOnly(groups);
        CanonicalStates = canonicalStates;
        ConfirmationText = confirmationText;
    }

    internal ExactResultsSession Session { get; }

    internal ExactLinkReplacementGroupState[] CanonicalStates { get; }

    public LinkReplacementMode Mode { get; }

    public IReadOnlyList<LinkReplacementGroup> Groups => _groups;

    public string ConfirmationText { get; }
}

internal sealed record ExactLinkReplacementGroupState(
    DuplicateGroupViewModel Group,
    DuplicateFileViewModel Survivor,
    DuplicateFileViewModel[] Duplicates);
