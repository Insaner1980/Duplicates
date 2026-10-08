using Duplicates.Models;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Duplicates.Services;

public static class ThemeService
{
    public static void Apply(Window window, FrameworkElement root, AppSettings settings)
    {
        root.RequestedTheme = settings.ThemeMode switch
        {
            AppThemeMode.Light => ElementTheme.Light,
            AppThemeMode.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

        window.SystemBackdrop = settings.BackdropMode switch
        {
            BackdropMode.Mica => new MicaBackdrop { Kind = MicaKind.Base },
            BackdropMode.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
            BackdropMode.Acrylic => new DesktopAcrylicBackdrop(),
            _ => null,
        };
    }
}
