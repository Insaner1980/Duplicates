using System.Collections.Specialized;
using System.ComponentModel;
using Duplicates.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.Views;

public sealed partial class VideoOptimizerPage : Page
{
    private bool _scopeEventsAttached;

    public VideoOptimizerPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.VideoOptimizerViewModel;
        DataContext = ViewModel;
    }

    public VideoOptimizerViewModel ViewModel { get; }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (!_scopeEventsAttached)
        {
            ViewModel.PathScope.IncludedPaths.CollectionChanged += ScopeCollectionChanged;
            ViewModel.PathScope.ExcludedPaths.CollectionChanged += ScopeCollectionChanged;
            ViewModel.PathScope.PropertyChanged += ScopePropertyChanged;
            _scopeEventsAttached = true;
        }

        await ViewModel.RefreshQueueCommand.ExecuteAsync(null);
    }

    private void Page_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_scopeEventsAttached)
        {
            return;
        }

        ViewModel.PathScope.IncludedPaths.CollectionChanged -= ScopeCollectionChanged;
        ViewModel.PathScope.ExcludedPaths.CollectionChanged -= ScopeCollectionChanged;
        ViewModel.PathScope.PropertyChanged -= ScopePropertyChanged;
        _scopeEventsAttached = false;
    }

    private async void ScopeCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        await ViewModel.RefreshQueueCommand.ExecuteAsync(null);

    private async void ScopePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PathScopeViewModel.IncludeSubfolders) or
            nameof(PathScopeViewModel.IgnoreHiddenFiles) or
            nameof(PathScopeViewModel.IgnoreSystemFiles))
        {
            await ViewModel.RefreshQueueCommand.ExecuteAsync(null);
        }
    }
}
