using Microsoft.UI.Xaml;

namespace Duplicates;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;

    public AppServices Services { get; } = new();

    public MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        await Services.SettingsService.LoadAsync();
        MainWindow = new MainWindow(Services);
        MainWindow.Activate();
    }
}
