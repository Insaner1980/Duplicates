using Duplicates.Engine.Analysis;
using Duplicates.Models;

namespace Duplicates.Services;

public sealed record MediaPreviewData(int Width, int Height, byte[] Bgra8);

public interface IMediaPreviewLoader
{
    Task<MediaPreviewData> LoadAsync(
        ToolKind tool,
        SimilarityItem item,
        CancellationToken cancellationToken);
}
