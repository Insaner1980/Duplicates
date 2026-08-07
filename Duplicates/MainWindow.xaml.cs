using Duplicates.Models;
using Duplicates.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Duplicates;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;

    public MainWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1200, 800));

        _services.ThemeService.Apply(this, Root, _services.SettingsService.Current);
        _services.SettingsService.SettingsChanged += SettingsChanged;
        Closed += MainWindow_Closed;

        RootNavigationView.SelectedItem = ScanNavigationItem;
        RootFrame.Navigate(typeof(ScanPage));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _services.SettingsService.SettingsChanged -= SettingsChanged;
    }

    private void SettingsChanged(object? sender, AppSettings settings)
    {
        _services.ThemeService.Apply(this, Root, settings);
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        if (!Enum.TryParse(tag, out ToolKind kind) || !Enum.IsDefined(kind))
        {
            return;
        }

        if (kind == ToolKind.DuplicateFiles)
        {
            NavigateTo(typeof(ScanPage));
            return;
        }

        if (IsReadOnlyAnalysisTool(kind))
        {
            ShowAnalysisPage(kind);
            return;
        }

        _ = ShowToolNotInstalledDialogAsync(ToolDescriptor.For(kind));
    }

    private void NavigateTo(Type pageType)
    {
        if (RootFrame.CurrentSourcePageType != pageType)
        {
            RootFrame.Navigate(pageType);
        }
    }

    public void ShowResultsPage()
    {
        if (RootFrame.CurrentSourcePageType != typeof(ResultsPage))
        {
            RootFrame.Navigate(typeof(ResultsPage));
        }
    }

    public void ShowScanPage()
    {
        RootNavigationView.SelectedItem = ScanNavigationItem;
        if (RootFrame.CurrentSourcePageType != typeof(ScanPage))
        {
            RootFrame.Navigate(typeof(ScanPage));
        }
    }

    public void ShowAnalysisPage(ToolKind tool)
    {
        _services.AnalysisViewModel.SelectTool(tool);
        NavigateTo(typeof(AnalysisPage));
    }

    public void ShowAnalysisResultsPage()
    {
        NavigateTo(typeof(AnalysisResultsPage));
    }

    private static bool IsReadOnlyAnalysisTool(ToolKind tool) => tool is
        ToolKind.SimilarImages or
        ToolKind.SimilarVideos or
        ToolKind.MusicDuplicates or
        ToolKind.EmptyFolders or
        ToolKind.BigFiles or
        ToolKind.EmptyFiles or
        ToolKind.TemporaryFiles or
        ToolKind.InvalidLinks or
        ToolKind.BrokenFiles or
        ToolKind.BadExtensions or
        ToolKind.BadNames;

    private async Task ShowToolNotInstalledDialogAsync(ToolDescriptor descriptor)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = descriptor.Title,
            Content = "This tool is not installed in the current build.",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        await CompleteUnavailableToolSelectionAsync(
            async () =>
            {
                await dialog.ShowAsync();
            },
            () => RootNavigationView.SelectedItem = ScanNavigationItem);
    }

    private static async Task CompleteUnavailableToolSelectionAsync(
        Func<Task> showDialogAsync,
        Action restoreDuplicateFilesSelection)
    {
        await showDialogAsync();
        restoreDuplicateFilesSelection();
    }
}
