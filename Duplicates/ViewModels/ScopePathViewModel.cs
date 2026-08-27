using CommunityToolkit.Mvvm.ComponentModel;
using Duplicates.Models;

namespace Duplicates.ViewModels;

public sealed partial class ScopePathViewModel : ObservableObject
{
    public ScopePathViewModel(string fullPath, ScopePathKind kind)
    {
        FullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(fullPath));
        Kind = kind;

        string displayName = Path.GetFileName(FullPath);
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? FullPath : displayName;
        ParentPath = string.IsNullOrWhiteSpace(displayName)
            ? "Drive root"
            : Path.GetDirectoryName(FullPath) ?? FullPath;
    }

    public string FullPath { get; }

    public string DisplayName { get; }

    public string ParentPath { get; }

    public ScopePathKind Kind { get; }

    [ObservableProperty]
    public partial bool IsPreferred { get; set; }
}
