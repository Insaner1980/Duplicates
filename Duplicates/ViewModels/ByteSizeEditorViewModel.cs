using CommunityToolkit.Mvvm.ComponentModel;

namespace Duplicates.ViewModels;

// Edits a byte-backed size as an amount and a binary unit (B, KB, MB, GB).
public sealed partial class ByteSizeEditorViewModel : ObservableObject
{
    private static readonly string[] UnitNames = ["B", "KB", "MB", "GB"];
    private readonly Func<double> _getBytes;
    private readonly Action<double> _setBytes;
    private bool _isSyncing;
    private int _unitIndex;

    public ByteSizeEditorViewModel(Func<double> getBytes, Action<double> setBytes)
    {
        _getBytes = getBytes;
        _setBytes = setBytes;
        Refresh();
    }

    public IReadOnlyList<string> Units { get; } = UnitNames;

    [ObservableProperty]
    public partial double Amount { get; set; }

    public int UnitIndex
    {
        get => _unitIndex;
        set
        {
            if (value < 0 || value >= UnitNames.Length)
            {
                OnPropertyChanged();
                return;
            }

            if (SetProperty(ref _unitIndex, value))
            {
                WriteBytes();
            }
        }
    }

    public void Refresh()
    {
        if (_isSyncing)
        {
            return;
        }

        double bytes = _getBytes();
        int unitIndex = UnitIndex;
        if (!double.IsNaN(bytes) && bytes > 0)
        {
            unitIndex = 0;
            while (unitIndex < UnitNames.Length - 1 && bytes % Multiplier(unitIndex + 1) == 0)
            {
                unitIndex++;
            }
        }

        _isSyncing = true;
        try
        {
            UnitIndex = unitIndex;
            Amount = double.IsNaN(bytes) ? double.NaN : bytes / Multiplier(unitIndex);
        }
        finally
        {
            _isSyncing = false;
        }
    }

    partial void OnAmountChanged(double value) => WriteBytes();

    private void WriteBytes()
    {
        if (_isSyncing)
        {
            return;
        }

        _isSyncing = true;
        try
        {
            _setBytes(double.IsNaN(Amount) ? double.NaN : Amount * Multiplier(UnitIndex));
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private static double Multiplier(int unitIndex) => Math.Pow(1024d, unitIndex);
}
