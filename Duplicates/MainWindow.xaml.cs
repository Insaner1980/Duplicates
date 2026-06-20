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
        _services.ResultsStore.ResultChanged += ResultsChanged;

        RootNavigationView.SelectedItem = ScanNavigationItem;
        RootFrame.Navigate(typeof(ScanPage));
    }

    private void SettingsChanged(object? sender, Models.AppSettings settings)
    {
        _services.ThemeService.Apply(this, Root, settings);
    }

    private void ResultsChanged(object? sender, Engine.Models.ScanResult? result)
    {
        ResultsNavigationItem.IsEnabled = result is not null;
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        RootNavigationView.IsPaneOpen = !RootNavigationView.IsPaneOpen;
    }

    private void RootNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        Type pageType = tag switch
        {
            "Results" => typeof(ResultsPage),
            "Settings" => typeof(SettingsPage),
            _ => typeof(ScanPage),
        };

        if (RootFrame.CurrentSourcePageType != pageType)
        {
            RootFrame.Navigate(pageType);
        }
    }

    public void ShowResultsPage()
    {
        ResultsNavigationItem.IsEnabled = true;
        RootNavigationView.SelectedItem = ResultsNavigationItem;
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
}
