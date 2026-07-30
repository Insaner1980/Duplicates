namespace Duplicates.ViewModels;

public sealed class ScanFolderViewModel
{
    public ScanFolderViewModel(string fullPath)
    {
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        string trimmedPath = Path.TrimEndingDirectorySeparator(FullPath);
        string displayName = Path.GetFileName(trimmedPath);

        DisplayName = string.IsNullOrWhiteSpace(displayName)
            ? FullPath
            : displayName;
        ParentPath = string.IsNullOrWhiteSpace(displayName)
            ? "Drive root"
            : Path.GetDirectoryName(trimmedPath) ?? FullPath;
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public string ParentPath { get; }
}
