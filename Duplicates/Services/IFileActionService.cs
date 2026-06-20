using Duplicates.Models;
using Duplicates.ViewModels;

namespace Duplicates.Services;

public interface IFileActionService
{
    Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<DuplicateFileViewModel> files,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken);

    void OpenFile(string path);

    void RevealInExplorer(string path);
}
