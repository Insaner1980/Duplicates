using Duplicates.Models;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Duplicates.Views;

public sealed partial class AnalysisResultsPage : Page
{
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
