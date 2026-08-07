using Duplicates.Engine.Models;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

    private void ScanCompleted(object? sender, ScanResult e)
    {
        App.Current.MainWindow?.ShowResultsPage();
    }
}
