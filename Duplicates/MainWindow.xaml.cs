using Duplicates.Models;
using Duplicates.Services;
using Duplicates.Views;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace Duplicates;

public sealed partial class MainWindow : Window
{
    private readonly AppServices _services;
    private readonly MainWindowOperationGuard _operationGuard;
    private bool _isRestoringAnalysisSelection;
    private bool _isRestoringNavigationSelection;
    private bool _isCloseReentry;
    private object? _lastNavigationItem;

    public MainWindow(AppServices services)
    {
        _services = services;
        _operationGuard = new MainWindowOperationGuard(_services.OperationCoordinator);
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.Resize(new SizeInt32(1200, 800));

        ThemeService.Apply(this, Root, _services.SettingsService.Current);
        _services.SettingsService.SettingsChanged += SettingsChanged;
        AppWindow.Closing += AppWindow_Closing;
        Closed += MainWindow_Closed;

        RootNavigationView.SelectedItem = ScanNavigationItem;
        _lastNavigationItem = ScanNavigationItem;
        RootFrame.Navigate(typeof(ScanPage));
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _services.SettingsService.SettingsChanged -= SettingsChanged;
        AppWindow.Closing -= AppWindow_Closing;
        _operationGuard.Dispose();
    }

    private void SettingsChanged(object? sender, AppSettings settings)
    {
        ThemeService.Apply(this, Root, settings);
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private async void RootNavigationView_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (_isRestoringAnalysisSelection || _isRestoringNavigationSelection)
        {
            return;
        }

        object? requestedItem = args.SelectedItem;
        bool isSettingsSelected = args.IsSettingsSelected;
        string? requestedTag = args.SelectedItemContainer?.Tag as string;
        AppOperationDescriptor? active = _services.OperationCoordinator.ActiveOperation;
        if (active is not null && IsOwningPage(active.Kind, RootFrame.CurrentSourcePageType))
        {
            bool accepted = await _operationGuard.ConfirmDepartureAsync(ShowDepartureConfirmationAsync);
            if (!accepted)
            {
                RestoreNavigationSelection(_lastNavigationItem);
                return;
            }

            RestoreNavigationSelection(requestedItem);
        }

        if (isSettingsSelected)
        {
            NavigateTo(typeof(SettingsPage));
            _lastNavigationItem = requestedItem;
            return;
        }

        if (requestedTag is null ||
            !Enum.TryParse(requestedTag, out ToolKind kind) ||
            !Enum.IsDefined(kind))
        {
            RestoreNavigationSelection(_lastNavigationItem);
            return;
        }

        _lastNavigationItem = requestedItem;
        if (kind == ToolKind.DuplicateFiles)
        {
            NavigateTo(ResolveExactDestination(_services.ResultsStore.CurrentSession));
            return;
        }

        Type? directPage = ResolveDirectToolPage(kind);
        if (directPage is not null)
        {
            NavigateTo(directPage);
            return;
        }

        if (IsReadOnlyAnalysisTool(kind))
        {
            ShowAnalysisPage(kind);
            return;
        }

        _ = ShowToolNotInstalledDialogAsync(ToolDescriptor.For(kind));
    }

    private async void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isCloseReentry || _services.OperationCoordinator.ActiveOperation is null)
        {
            return;
        }

        args.Cancel = true;
        if (!await _operationGuard.ConfirmCloseAsync(ShowCloseConfirmationAsync))
        {
            return;
        }

        _isCloseReentry = true;
        Close();
    }

    private Task<bool> ShowDepartureConfirmationAsync() => ShowOperationConfirmationAsync(
        "Leave while the operation continues?",
        "The current operation will keep running. You can return to its page to view progress.",
        "Leave page");

    private Task<bool> ShowCloseConfirmationAsync() => ShowOperationConfirmationAsync(
        "Cancel the operation and close?",
        "Duplicates will request cancellation and wait for any safe file transaction to finish before closing.",
        "Cancel and close");

    private async Task<bool> ShowOperationConfirmationAsync(
        string title,
        string content,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Stay",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static bool IsOwningPage(AppOperationKind kind, Type? pageType) => kind switch
    {
        AppOperationKind.ExactScan => pageType == typeof(ScanPage),
        AppOperationKind.AnalysisRun => pageType == typeof(AnalysisPage),
        AppOperationKind.ExactResultsAction => pageType == typeof(ResultsPage),
        AppOperationKind.AnalysisResultsAction => pageType == typeof(AnalysisResultsPage),
        AppOperationKind.ExifCleaning => pageType == typeof(ExifRemoverPage),
        AppOperationKind.VideoOptimization => pageType == typeof(VideoOptimizerPage),
        _ => false,
    };

    private static Type? ResolveDirectToolPage(ToolKind tool) => tool switch
    {
        ToolKind.ExifRemover => typeof(ExifRemoverPage),
        ToolKind.VideoOptimizer => typeof(VideoOptimizerPage),
        _ => null,
    };

    private static Type ResolveExactDestination(ExactResultsSession? session) =>
        session is null ? typeof(ScanPage) : typeof(ResultsPage);

    private void RestoreNavigationSelection(object? item)
    {
        _isRestoringNavigationSelection = true;
        try
        {
            RootNavigationView.SelectedItem = item;
        }
        finally
        {
            _isRestoringNavigationSelection = false;
        }
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

    public async Task RequestNewScanAsync()
    {
        ExactResultsSession? session = _services.ResultsStore.CurrentSession;
        if (session is null)
        {
            ShowScanPage();
            return;
        }

        bool reset = await _operationGuard.ConfirmResetAsync(
            () => ShowResultResetConfirmationAsync(
                "Start a new scan?",
                "The current duplicate results, selection, search, sort, and preview will be cleared.",
                "Start new scan"),
            () => _services.ResultsViewModel.TryResetSession(session));
        if (reset)
        {
            ShowScanPage();
        }
    }

    public async Task RequestNewAnalysisAsync(ToolKind tool)
    {
        AnalysisSession? session = _services.AnalysisSessionStore.CurrentSession;
        if (session is null)
        {
            ShowAnalysisPage(tool);
            return;
        }

        bool reset = await _operationGuard.ConfirmResetAsync(
            () => ShowResultResetConfirmationAsync(
                "Start a new analysis?",
                "The current analysis results, selection, search, sort, and preview will be cleared.",
                "Start new analysis"),
            () => _services.AnalysisResultsViewModel.TryResetSession(session));
        if (reset)
        {
            ShowAnalysisPage(tool);
        }
    }

    private async Task<bool> ShowResultResetConfirmationAsync(
        string title,
        string content,
        string primaryButtonText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Root.XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    public void ShowAnalysisPage(ToolKind tool)
    {
        _services.AnalysisViewModel.SelectTool(tool);
        if (_services.AnalysisViewModel.Tool != tool)
        {
            RestoreAnalysisSelection(_services.AnalysisViewModel.Tool);
            NavigateTo(typeof(AnalysisPage));
            return;
        }

        Type destination = _services.AnalysisViewModel.IsAnalyzing
            ? typeof(AnalysisPage)
            : ResolveAnalysisDestination(tool, _services.AnalysisSessionStore.CurrentSession);
        NavigateTo(destination);
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

    private static Type ResolveAnalysisDestination(ToolKind tool, AnalysisSession? session) =>
        session?.Tool == tool ? typeof(AnalysisResultsPage) : typeof(AnalysisPage);

    private void RestoreAnalysisSelection(ToolKind tool)
    {
        NavigationViewItem? item = RootNavigationView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tool.ToString(), StringComparison.Ordinal));
        if (item is null)
        {
            return;
        }

        _isRestoringAnalysisSelection = true;
        RootNavigationView.SelectedItem = item;
        _isRestoringAnalysisSelection = false;
    }

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
