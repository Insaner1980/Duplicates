using Duplicates.Engine.Models;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace Duplicates.Views;

public sealed partial class ScanPage : Page
{
    public ScanPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.ScanViewModel;
        DataContext = ViewModel;
        Loaded += ScanPage_Loaded;
        Unloaded += ScanPage_Unloaded;
    }

    public ScanViewModel ViewModel { get; }

    private void ScanPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ScanCompleted -= ScanCompleted;
        ViewModel.ScanCompleted += ScanCompleted;
    }

    private void ScanPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ScanCompleted -= ScanCompleted;
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is null)
        {
            return;
        }

        var picker = new FolderPicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Select Folder",
            ViewMode = PickerViewMode.List,
        };

        PickFolderResult? result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            int folderCount = ViewModel.Folders.Count;
            ViewModel.AddFolder(result.Path);
            if (ViewModel.Folders.Count > folderCount)
            {
                FoldersList.Focus(FocusState.Programmatic);
            }
        }
    }

    private void FoldersRegion_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
        }
    }

    private async void FoldersRegion_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            return;
        }

        int folderCount = ViewModel.Folders.Count;
        IReadOnlyList<IStorageItem> items = await e.DataView.GetStorageItemsAsync();
        foreach (StorageFolder folder in items.OfType<StorageFolder>())
        {
            ViewModel.AddFolder(folder.Path);
        }

        if (ViewModel.Folders.Count > folderCount)
        {
            FoldersList.Focus(FocusState.Programmatic);
        }
    }

    private void ScanCompleted(object? sender, ScanResult e)
    {
        App.Current.MainWindow?.ShowResultsPage();
    }
}
