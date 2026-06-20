using Duplicates.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace Duplicates.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        ViewModel = App.Current.Services.SettingsViewModel;
        DataContext = ViewModel;
    }

    public SettingsViewModel ViewModel { get; }
}
