using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.Views;

public sealed partial class ExifRemoverPage : Page
{
    public ExifRemoverPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.ExifRemoverViewModel;
        DataContext = ViewModel;
    }

    public ExifRemoverViewModel ViewModel { get; }

    private async void CleanImages_Click(object sender, RoutedEventArgs e)
    {
        bool replacementConfirmed = !ViewModel.ReplaceOriginal || await ConfirmReplacementAsync();
        await ViewModel.CleanImagesCommand.ExecuteAsync(replacementConfirmed);
    }

    private async Task<bool> ConfirmReplacementAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Replace original images?",
            Content = "Each original will be replaced only after the cleaned image passes verification. The original is then sent to the Recycle Bin.",
            PrimaryButtonText = "Replace originals",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
