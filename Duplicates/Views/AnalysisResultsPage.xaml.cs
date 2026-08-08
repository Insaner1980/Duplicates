using Duplicates.Models;
using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Windows.Storage.Pickers;

namespace Duplicates.Views;

public sealed partial class AnalysisResultsPage : Page
{
    private PathFindingViewModel? _renameFinding;

    public AnalysisResultsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.AnalysisResultsViewModel;
        DataContext = ViewModel;
        Loaded += AnalysisResultsPage_Loaded;
        Unloaded += AnalysisResultsPage_Unloaded;
    }

    public AnalysisResultsViewModel ViewModel { get; }

    private void AnalysisResultsPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.NewAnalysisRequested -= NewAnalysisRequested;
        ViewModel.NewAnalysisRequested += NewAnalysisRequested;
    }

    private void AnalysisResultsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.NewAnalysisRequested -= NewAnalysisRequested;
    }

    private void NewAnalysisRequested(object? sender, ToolKind tool)
    {
        App.Current.MainWindow?.ShowAnalysisPage(tool);
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        args.Handled = true;
    }

    private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.CanActOnSelection || !await ConfirmDeleteAsync())
        {
            return;
        }

        try
        {
            await ViewModel.DeleteSelectedAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            ViewModel.ActionStatusMessage = ex.Message;
        }
    }

    private async void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is null || !ViewModel.CanActOnSelection)
        {
            return;
        }

        var picker = new FolderPicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            CommitButtonText = "Choose destination",
            ViewMode = PickerViewMode.List,
        };
        PickFolderResult? pickedFolder = await picker.PickSingleFolderAsync();
        if (pickedFolder is null)
        {
            return;
        }

        MoveCollisionBehavior? collisionBehavior = await ConfirmMoveAsync(pickedFolder.Path);
        if (collisionBehavior is null)
        {
            return;
        }

        try
        {
            await ViewModel.MoveSelectedAsync(pickedFolder.Path, collisionBehavior.Value, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OperationCanceledException)
        {
            ViewModel.ActionStatusMessage = ex.Message;
        }
    }

    private async void RenameSelected_Click(object sender, RoutedEventArgs e)
    {
        PathFindingViewModel? finding = ViewModel.RenameSelection;
        if (finding is null)
        {
            return;
        }

        _renameFinding = finding;
        RenameDialog.XamlRoot = XamlRoot;
        RenameCurrentPathTextBlock.Text = finding.FullPath;
        RenameReasonsTextBlock.Text = finding.Reason.Replace(
            "; ",
            Environment.NewLine,
            StringComparison.Ordinal);
        RenameNameTextBox.Text = finding.Suggestion;
        UpdateRenameDialogState();

        ContentDialogResult dialogResult = await RenameDialog.ShowAsync();
        string requestedName = RenameNameTextBox.Text;
        _renameFinding = null;
        if (dialogResult != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            FileOperationResult result = await ViewModel.RenameFindingAsync(
                finding,
                requestedName,
                CancellationToken.None);
            ViewModel.ActionStatusMessage = result.Succeeded
                ? "File renamed."
                : result.Failure?.Reason ?? "The file could not be renamed.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException)
        {
            ViewModel.ActionStatusMessage = ex.Message;
        }
    }

    private void RenameNameTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        UpdateRenameDialogState();

    private void UpdateRenameDialogState()
    {
        if (_renameFinding is null)
        {
            RenameDestinationTextBlock.Text = string.Empty;
            RenameValidationTextBlock.Text = string.Empty;
            RenameDialog.IsPrimaryButtonEnabled = false;
            return;
        }

        bool isValid = ViewModel.TryValidateRenameCandidate(
            _renameFinding,
            RenameNameTextBox.Text,
            out string destinationPath,
            out string validationMessage);
        RenameDestinationTextBlock.Text = destinationPath;
        RenameValidationTextBlock.Text = isValid ? "Ready to rename." : validationMessage;
        RenameDialog.IsPrimaryButtonEnabled = isValid;
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.MainWindow is null)
        {
            return;
        }

        var picker = new FileSavePicker(App.Current.MainWindow.AppWindow.Id)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "analysis-results",
            CommitButtonText = "Export",
        };
        picker.FileTypeChoices.Add("CSV file", [".csv"]);
        picker.FileTypeChoices.Add("JSON file", [".json"]);
        PickFileResult? result = await picker.PickSaveFileAsync();
        if (result is null)
        {
            return;
        }

        ResultExportFormat format = string.Equals(Path.GetExtension(result.Path), ".json", StringComparison.OrdinalIgnoreCase)
            ? ResultExportFormat.Json
            : ResultExportFormat.Csv;
        try
        {
            await ViewModel.ExportAsync(format, result.Path, CancellationToken.None);
            ViewModel.ActionStatusMessage = "Results exported.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            ViewModel.ActionStatusMessage = ex.Message;
        }
    }

    private async Task<bool> ConfirmDeleteAsync()
    {
        bool recycle = App.Current.Services.SettingsService.Current.DeletionMode == DeletionMode.RecycleBin;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Delete {ViewModel.SelectedItemCount:N0} selected items?",
            Content = $"{ByteFormatter.Format(ViewModel.SelectedBytes)} selected. " +
                (recycle
                    ? "Items will be sent to the Recycle Bin."
                    : "Items will be permanently deleted. This cannot be undone."),
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task<MoveCollisionBehavior?> ConfirmMoveAsync(string destinationPath)
    {
        var collisionBehavior = new ComboBox
        {
            Header = "If a name already exists",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 0,
            Items =
            {
                new ComboBoxItem { Content = "Skip existing items" },
                new ComboBoxItem { Content = "Keep both" },
                new ComboBoxItem { Content = "Cancel the move" },
            },
        };
        var content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = $"Move {ViewModel.SelectedItemCount:N0} selected items to {destinationPath}?",
                    TextWrapping = TextWrapping.Wrap,
                },
                collisionBehavior,
            },
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Move selected items?",
            Content = content,
            PrimaryButtonText = "Move",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return collisionBehavior.SelectedIndex switch
        {
            1 => MoveCollisionBehavior.KeepBoth,
            2 => MoveCollisionBehavior.Cancel,
            _ => MoveCollisionBehavior.Skip,
        };
    }
}

public sealed class AnalysisResultTemplateSelector : DataTemplateSelector
{
    public DataTemplate? PathFindingTemplate { get; set; }

    public DataTemplate? SimilarityGroupTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item) => item switch
    {
        PathFindingViewModel => PathFindingTemplate!,
        SimilarityGroupViewModel => SimilarityGroupTemplate!,
        _ => base.SelectTemplateCore(item),
    };
}
