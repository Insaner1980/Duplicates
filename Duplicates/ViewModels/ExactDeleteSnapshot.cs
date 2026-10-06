using Duplicates.Models;
using Duplicates.Services;

namespace Duplicates.ViewModels;

internal sealed record ExactDeleteSnapshot(
    ExactResultsSession Session,
    IReadOnlyList<DuplicateFileViewModel> Files,
    DeletionMode Mode,
    bool IsBatch);
