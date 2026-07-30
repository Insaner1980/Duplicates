namespace Duplicates.ViewModels;

public static class ByteSizeInput
{
    public static double NoMaximum => double.NaN;

    public static double FromBytes(long value)
    {
        return value == long.MaxValue ? NoMaximum : value;
    }

    public static long ToBytes(double value, long fallback, bool noValueMeansMaximum = false)
    {
        if (double.IsNaN(value))
        {
            return noValueMeansMaximum ? long.MaxValue : fallback;
        }

        if (double.IsInfinity(value))
        {
            return fallback;
        }

        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return checked((long)Math.Max(0d, Math.Truncate(value)));
    }
}
