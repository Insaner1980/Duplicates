using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Duplicates.Views.Controls;

public sealed partial class PathScopeEditor : UserControl
{
    public PathScopeEditor()
    {
        InitializeComponent();
    }

    public string FileTypeFilter { get; set; } = "*";

    private PathScopeViewModel? Scope => DataContext as PathScopeViewModel;

    private async void AddIncludedFolder_Click(object sender, RoutedEventArgs e) => await AddFolderAsync(excluded: false);

    private async void AddExcludedFolder_Click(object sender, RoutedEventArgs e) => await AddFolderAsync(excluded: true);

    private async void AddIncludedFile_Click(object sender, RoutedEventArgs e) => await AddFilesAsync(excluded: false);

    private async void AddExcludedFile_Click(object sender, RoutedEventArgs e) => await AddFilesAsync(excluded: true);

    private void RemoveIncludedPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScopePathViewModel path })
        {
            Scope?.RemoveIncludedPath(path);
        }
    }

    private void RemoveExcludedPath_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ScopePathViewModel path })
        {
            Scope?.RemoveExcludedPath(path);
        }
    }

    private async Task AddFolderAsync(bool excluded)
    {
        if (App.Current.MainWindow is null || Scope is null)
        {
            return;
        }

        var picker = new FolderPicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = excluded ? "Exclude Folder" : "Select Folder",
            ViewMode = PickerViewMode.List,
        };

        PickFolderResult? result = await picker.PickSingleFolderAsync();
        if (result is null)
        {
            return;
        }

        bool added = excluded ? Scope.ExcludePath(result.Path) : Scope.AddFolder(result.Path);
        if (added && !excluded)
        {
            IncludedPathsList.Focus(FocusState.Programmatic);
        }
    }

    private async Task AddFilesAsync(bool excluded)
    {
        if (App.Current.MainWindow is null || Scope is null)
        {
            return;
        }

        var picker = new FileOpenPicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = excluded ? "Exclude Files" : "Select Files",
            ViewMode = PickerViewMode.List,
        };
        foreach (string fileType in ParseFileTypeFilter(FileTypeFilter))
        {
            picker.FileTypeFilter.Add(fileType);
        }

        IReadOnlyList<PickFileResult> results = await picker.PickMultipleFilesAsync();
        bool added = false;
        foreach (PickFileResult result in results)
        {
            if (!IsFileTypeAllowed(result.Path, FileTypeFilter))
            {
                continue;
            }

            added |= excluded ? Scope.ExcludePath(result.Path) : Scope.AddFile(result.Path);
        }

        if (added && !excluded)
        {
            IncludedPathsList.Focus(FocusState.Programmatic);
        }
    }

    private void IncludedPathsRegion_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void IncludedPathsRegion_Drop(object sender, DragEventArgs e)
    {
        if (Scope is null || !e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        bool added = false;
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        foreach (StorageFolder folder in items.OfType<StorageFolder>())
        {
            added |= Scope.AddFolder(folder.Path);
        }

        foreach (StorageFile file in items.OfType<StorageFile>())
        {
            if (IsFileTypeAllowed(file.Path, FileTypeFilter))
            {
                added |= Scope.AddFile(file.Path);
            }
        }

        if (added)
        {
            IncludedPathsList.Focus(FocusState.Programmatic);
        }
    }

    internal static string[] ParseFileTypeFilter(string? filter)
    {
        string[] values = (filter ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => value == "*"
                ? value
                : value.StartsWith('.') ? value.ToLowerInvariant() : $".{value.ToLowerInvariant()}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length == 0 || values.Contains("*", StringComparer.Ordinal)
            ? ["*"]
            : values;
    }

    internal static bool IsFileTypeAllowed(string path, string? filter)
    {
        string[] allowedTypes = ParseFileTypeFilter(filter);
        return allowedTypes[0] == "*" ||
            allowedTypes.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }
}
