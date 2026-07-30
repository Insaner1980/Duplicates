using Duplicates.Models;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.ApplicationModel.DataTransfer;

namespace Duplicates.Views;

public sealed partial class ResultsPage : Page
{
    public ResultsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.ResultsViewModel;
        DataContext = ViewModel;
    }

    public ResultsViewModel ViewModel { get; }

    private async void KeepPreferredFolder_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is null)
        {
            return;
        }

        var picker = new FolderPicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Keep copies here",
            ViewMode = PickerViewMode.List,
        };

        PickFolderResult? result = await picker.PickSingleFolderAsync();
        if (result is not null)
        {
            ViewModel.AutoSelectKeepPreferredFolder(result.Path);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is null || !ViewModel.CanDelete)
        {
            return;
        }

        if (!await ConfirmDeleteAsync(
                ViewModel.SelectedFileCount,
                ViewModel.SelectedGroupCount,
                ViewModel.SelectedBytes))
        {
            return;
        }

        await ViewModel.DeleteSelectedAsync(CancellationToken.None);
    }

    private async void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DuplicateFileViewModel file || ViewModel.IsDeleting)
        {
            return;
        }

        if (!await ConfirmDeleteAsync(1, 1, file.SizeBytes, file.FileName, file.FullPath))
        {
            return;
        }

        await ViewModel.DeleteFileAsync(file, CancellationToken.None);
    }

    private async Task<bool> ConfirmDeleteAsync(
        int fileCount,
        int groupCount,
        long bytes,
        string? fileName = null,
        string? fullPath = null)
    {
        if (!App.Current.Services.SettingsService.Current.ConfirmBeforeDelete)
        {
            return true;
        }

        bool isSingleFile = fileCount == 1;
        bool usesRecycleBin = App.Current.Services.SettingsService.Current.DeletionMode == DeletionMode.RecycleBin;
        string targetText = (usesRecycleBin, isSingleFile) switch
        {
            (true, true) => "The file will be sent to the Recycle Bin.",
            (true, false) => "Files will be sent to the Recycle Bin.",
            (false, true) => "The file will be permanently deleted. This cannot be undone.",
            (false, false) => "Files will be permanently deleted. This cannot be undone.",
        };
        string title = fileCount == 1 && fileName is not null
            ? $"Delete {fileName}?"
            : $"Delete {fileCount:N0} files from {groupCount:N0} groups?";
        string selectionText = fileCount == 1 && fullPath is not null
            ? $"{fullPath}\n\n{ByteFormatter.Format(bytes)} selected."
            : $"{ByteFormatter.Format(bytes)} selected.";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = $"{selectionText} {targetText}",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private void NewScan_Click(object sender, RoutedEventArgs e)
    {
        App.Current.MainWindow?.ShowScanPage();
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileViewModel file)
        {
            App.Current.Services.FileActionService.OpenFile(file.FullPath);
        }
    }

    private void RevealFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileViewModel file)
        {
            App.Current.Services.FileActionService.RevealInExplorer(file.FullPath);
        }
    }

    private void CopyPath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileViewModel file)
        {
            var package = new DataPackage();
            package.SetText(file.FullPath);
            Clipboard.SetContent(package);
        }
    }

}
