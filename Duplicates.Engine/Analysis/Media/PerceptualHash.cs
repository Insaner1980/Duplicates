using System.Numerics;

namespace Duplicates.Engine.Analysis.Media;

public static class PerceptualHash
{
    private const int SampleSide = 32;
    private const int LowFrequencySide = 8;
    private static readonly double[,] Cosine = BuildCosineTable();

    public static ulong Compute(ReadOnlySpan<byte> luminance32x32)
    {
        if (luminance32x32.Length != SampleSide * SampleSide)
        {
            throw new ArgumentException("A perceptual hash requires exactly 1,024 luminance bytes.", nameof(luminance32x32));
        }

        var horizontal = new double[SampleSide, LowFrequencySide];
        for (int y = 0; y < SampleSide; y++)
        {
            for (int u = 0; u < LowFrequencySide; u++)
            {
                double sum = 0;
                for (int x = 0; x < SampleSide; x++)
                {
                    sum += luminance32x32[(y * SampleSide) + x] * Cosine[x, u];
                }

                horizontal[y, u] = sum;
            }
        }

        var coefficients = new double[LowFrequencySide * LowFrequencySide];
        for (int v = 0; v < LowFrequencySide; v++)
        {
            for (int u = 0; u < LowFrequencySide; u++)
            {
                double sum = 0;
                for (int y = 0; y < SampleSide; y++)
                {
                    sum += horizontal[y, u] * Cosine[y, v];
                }

                coefficients[(v * LowFrequencySide) + u] = Math.Round(
                    Alpha(v) * Alpha(u) * sum,
                    9,
                    MidpointRounding.ToEven);
            }
        }

        double[] nonDc = coefficients[1..];
        Array.Sort(nonDc);
        double median = nonDc[31];
        ulong hash = 0;
        for (int bitIndex = 0; bitIndex < coefficients.Length; bitIndex++)
        {
            if (coefficients[bitIndex] > median)
            {
                hash |= 1UL << bitIndex;
            }
        }

        return hash;
    }

    public static int Distance(ulong left, ulong right) => BitOperations.PopCount(left ^ right);

    private static double Alpha(int frequency) => frequency == 0
        ? 1d / Math.Sqrt(SampleSide)
        : Math.Sqrt(2d / SampleSide);

    private static double[,] BuildCosineTable()
    {
        var values = new double[SampleSide, LowFrequencySide];
        for (int position = 0; position < SampleSide; position++)
        {
            for (int frequency = 0; frequency < LowFrequencySide; frequency++)
            {
                values[position, frequency] = Math.Cos(
                    Math.PI * ((2 * position) + 1) * frequency / (2 * SampleSide));
            }
        }

        return values;
    }
}
