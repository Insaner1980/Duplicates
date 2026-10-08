namespace Duplicates.Models;

// Maps native ComboBox.SelectedIndex values to ordered enum members.
public static class EnumSelection
{
    public static bool TryFromIndex<TEnum>(int index, out TEnum value)
        where TEnum : struct, Enum
    {
        TEnum[] values = Enum.GetValues<TEnum>();
        if (index >= 0 && index < values.Length)
        {
            value = values[index];
            return true;
        }

        value = default;
        return false;
    }

    public static int ToIndex<TEnum>(TEnum value)
        where TEnum : struct, Enum => Array.IndexOf(Enum.GetValues<TEnum>(), value);
}
