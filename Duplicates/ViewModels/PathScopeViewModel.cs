using System.Collections.ObjectModel;
using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Duplicates.Models;
using Microsoft.UI.Xaml;

namespace Duplicates.ViewModels;

public sealed partial class PathScopeViewModel : ObservableObject
{
    public PathScopeViewModel()
    {
        IncludedPaths.CollectionChanged += IncludedPathsChanged;
        AddFolderCommand = new RelayCommand<string?>(path => AddFolder(path));
        AddFileCommand = new RelayCommand<string?>(path => AddFile(path));
        ExcludePathCommand = new RelayCommand<string?>(path => ExcludePath(path));
    }

    public ObservableCollection<ScopePathViewModel> IncludedPaths { get; } = [];

    public ObservableCollection<ScopePathViewModel> ExcludedPaths { get; } = [];

    public IRelayCommand<string?> AddFolderCommand { get; }

    public IRelayCommand<string?> AddFileCommand { get; }

    public IRelayCommand<string?> ExcludePathCommand { get; }

    [ObservableProperty]
    public partial bool IncludeSubfolders { get; set; } = true;

    [ObservableProperty]
    public partial bool IgnoreHiddenFiles { get; set; } = true;

    [ObservableProperty]
    public partial bool IgnoreSystemFiles { get; set; } = true;

    public bool HasIncludedPaths => IncludedPaths.Count > 0;

    public Visibility EmptyIncludedPathsVisibility =>
        HasIncludedPaths ? Visibility.Collapsed : Visibility.Visible;

    public bool AddFolder(string? path) => AddIncludedPath(path, ScopePathKind.Folder);

    public bool AddFile(string? path) => AddIncludedPath(path, ScopePathKind.File);

    public bool ExcludePath(string? path)
    {
        if (!TryCreatePath(path, Directory.Exists(path ?? string.Empty) ? ScopePathKind.Folder : ScopePathKind.File, out ScopePathViewModel? item) || item is null)
        {
            return false;
        }

        RemoveEquivalent(IncludedPaths, item.FullPath);
        if (ContainsPath(ExcludedPaths, item.FullPath))
        {
            return false;
        }

        ExcludedPaths.Add(item);
        return true;
    }

    [RelayCommand]
    public void RemoveIncludedPath(ScopePathViewModel? path)
    {
        if (path is not null)
        {
            IncludedPaths.Remove(path);
        }
    }

    [RelayCommand]
    public void RemoveExcludedPath(ScopePathViewModel? path)
    {
        if (path is not null)
        {
            ExcludedPaths.Remove(path);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPreferFolder))]
    public void PreferFolder(ScopePathViewModel? folder)
    {
        if (!CanPreferFolder(folder))
        {
            return;
        }

        foreach (ScopePathViewModel path in IncludedPaths)
        {
            path.IsPreferred = ReferenceEquals(path, folder);
        }
    }

    private bool AddIncludedPath(string? path, ScopePathKind kind)
    {
        if (!TryCreatePath(path, kind, out ScopePathViewModel? item) || item is null || ContainsPath(IncludedPaths, item.FullPath))
        {
            return false;
        }

        RemoveEquivalent(ExcludedPaths, item.FullPath);
        IncludedPaths.Add(item);
        return true;
    }

    private static bool TryCreatePath(string? path, ScopePathKind kind, out ScopePathViewModel? item)
    {
        item = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var candidate = new ScopePathViewModel(path, kind);
            if (kind == ScopePathKind.Folder ? !Directory.Exists(candidate.FullPath) : !File.Exists(candidate.FullPath))
            {
                return false;
            }

            item = candidate;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool CanPreferFolder(ScopePathViewModel? path) =>
        path is { Kind: ScopePathKind.Folder };

    private static bool ContainsPath(IEnumerable<ScopePathViewModel> paths, string fullPath) =>
        paths.Any(path => string.Equals(path.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));

    private static void RemoveEquivalent(ObservableCollection<ScopePathViewModel> paths, string fullPath)
    {
        ScopePathViewModel? existing = paths.FirstOrDefault(
            path => string.Equals(path.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            paths.Remove(existing);
        }
    }

    private void IncludedPathsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasIncludedPaths));
        OnPropertyChanged(nameof(EmptyIncludedPathsVisibility));
    }
}
