using System.Diagnostics;
using Duplicates.Models;
using Duplicates.ViewModels;
using Microsoft.VisualBasic.FileIO;

namespace Duplicates.Services;

public sealed class FileActionService : IFileActionService
{
    private readonly ISettingsService _settingsService;

    public FileActionService(ISettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public Task<DeleteSummary> DeleteAsync(
        IReadOnlyList<DuplicateFileViewModel> files,
        IProgress<DeleteProgress>? progress,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () =>
            {
                var failures = new List<FileActionFailure>();
                int deletedCount = 0;
                long deletedBytes = 0;
                int processedCount = 0;
                RecycleOption recycleOption = _settingsService.Current.DeletionMode == DeletionMode.RecycleBin
                    ? RecycleOption.SendToRecycleBin
                    : RecycleOption.DeletePermanently;

                foreach (DuplicateFileViewModel file in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        FileSystem.DeleteFile(file.FullPath, UIOption.OnlyErrorDialogs, recycleOption);
                        deletedCount++;
                        deletedBytes += file.SizeBytes;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileNotFoundException or OperationCanceledException)
                    {
                        failures.Add(new FileActionFailure(file.FullPath, ex.Message));
                    }
                    finally
                    {
                        processedCount++;
                        progress?.Report(new DeleteProgress(processedCount, files.Count, file.FullPath, deletedBytes));
                    }
                }

                return new DeleteSummary(deletedCount, deletedBytes, failures);
            },
            cancellationToken);
    }

    public void OpenFile(string path)
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    public void RevealInExplorer(string path)
    {
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
    }
}
