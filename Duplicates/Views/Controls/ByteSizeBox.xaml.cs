using Microsoft.UI.Xaml.Controls;

namespace Duplicates.Views.Controls;

public sealed partial class ByteSizeBox : UserControl
{
    public ByteSizeBox()
    {
        InitializeComponent();
    }

    public string AccessibleName { get; set; } = string.Empty;

    public string PlaceholderText { get; set; } = string.Empty;

    public string UnitAccessibleName => $"{AccessibleName} unit";
}
