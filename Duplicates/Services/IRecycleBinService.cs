using Duplicates.Models;

namespace Duplicates.Services;

public interface IRecycleBinService
{
    Task RecycleFileAsync(
        string path,
        FileSystemIdentity expectedIdentity,
        CancellationToken cancellationToken);
}
