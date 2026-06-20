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

        if (App.Current.Services.SettingsService.Current.ConfirmBeforeDelete)
        {
            string targetText = App.Current.Services.SettingsService.Current.DeletionMode == DeletionMode.RecycleBin
                ? "Files will be sent to the Recycle Bin."
                : "Files will be permanently deleted. This cannot be undone.";

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"Delete {ViewModel.SelectedFileCount:N0} files?",
                Content = $"{ByteFormatter.Format(ViewModel.SelectedBytes)} selected. {targetText}",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return;
            }
        }

        await ViewModel.DeleteSelectedAsync(CancellationToken.None);
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

    private void FileRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DuplicateFileViewModel file)
        {
            ViewModel.SelectedFile = file;
        }
    }
}
