using Duplicates.Services;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.Views;

public sealed partial class AnalysisPage : Page
{
    public AnalysisPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.AnalysisViewModel;
        DataContext = ViewModel;
        Loaded += AnalysisPage_Loaded;
        Unloaded += AnalysisPage_Unloaded;
    }

    public AnalysisViewModel ViewModel { get; }

    private void AnalysisPage_Loaded(object sender, RoutedEventArgs e)
    {
        ViewModel.AnalysisCompleted -= AnalysisCompleted;
        ViewModel.AnalysisCompleted += AnalysisCompleted;
    }

    private void AnalysisPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.AnalysisCompleted -= AnalysisCompleted;
    }

    private static void AnalysisCompleted(object? sender, AnalysisSession e)
    {
        App.Current.MainWindow?.ShowAnalysisResultsPage();
    }
}
