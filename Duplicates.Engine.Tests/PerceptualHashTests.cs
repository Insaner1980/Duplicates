using Duplicates.Engine.Analysis.Media;

namespace Duplicates.Engine.Tests;

public sealed class PerceptualHashTests
{
    [Fact]
    public void Compute_UsesStrictMedianPackingForConstantAndZeroInputs()
    {
        Assert.Equal(0UL, PerceptualHash.Compute(new byte[1024]));
        Assert.Equal(0x0000000000000001UL, PerceptualHash.Compute(Filled(128)));
    }

    [Fact]
    public void Compute_ProducesExactBitsForGeneratedFixtures()
    {
        Assert.Equal(0x0100000001000001UL, PerceptualHash.Compute(HorizontalEdge()));
        Assert.Equal(0x0000000000000089UL, PerceptualHash.Compute(VerticalEdge()));
        Assert.Equal(0x7ED48AD4A2D7AC05UL, PerceptualHash.Compute(Gradient(10)));
    }

    [Fact]
    public void Compute_BrightnessOffsetPreservesTheHashWithoutClipping()
    {
        ulong original = PerceptualHash.Compute(Gradient(10));
        ulong brighter = PerceptualHash.Compute(Gradient(30));

        Assert.Equal(original, brighter);
        Assert.Equal(0, PerceptualHash.Distance(original, brighter));
    }

    [Fact]
    public void Compute_DistinguishesHorizontalAndVerticalEdgesAndIsRepeatable()
    {
        byte[] horizontal = HorizontalEdge();
        ulong expected = PerceptualHash.Compute(horizontal);

        Assert.NotEqual(expected, PerceptualHash.Compute(VerticalEdge()));
        Assert.Equal(4, PerceptualHash.Distance(expected, PerceptualHash.Compute(VerticalEdge())));
        Assert.All(Enumerable.Range(0, 8), _ => Assert.Equal(expected, PerceptualHash.Compute(horizontal)));
    }

    [Fact]
    public void Compute_RequiresExactlyThirtyTwoSquaredLuminanceBytes()
    {
        Assert.Throws<ArgumentException>(() => PerceptualHash.Compute(new byte[1023]));
        Assert.Throws<ArgumentException>(() => PerceptualHash.Compute(new byte[1025]));
    }

    [Fact]
    public void Distance_CountsChangedBits()
    {
        Assert.Equal(0, PerceptualHash.Distance(0xA5UL, 0xA5UL));
        Assert.Equal(1, PerceptualHash.Distance(0UL, 1UL << 47));
        Assert.Equal(64, PerceptualHash.Distance(0UL, ulong.MaxValue));
    }

    private static byte[] Filled(byte value)
    {
        var bytes = new byte[1024];
        Array.Fill(bytes, value);
        return bytes;
    }

    private static byte[] Gradient(int offset)
    {
        var bytes = new byte[1024];
        for (int y = 0; y < 32; y++)
        {
            for (int x = 0; x < 32; x++)
            {
                bytes[(y * 32) + x] = (byte)(offset + (((x + y) * 180) / 62));
            }
        }

        return bytes;
    }

    private static byte[] HorizontalEdge()
    {
        var bytes = new byte[1024];
        for (int y = 16; y < 32; y++)
        {
            Array.Fill(bytes, (byte)255, y * 32, 32);
        }

        return bytes;
    }

    private static byte[] VerticalEdge()
    {
        var bytes = new byte[1024];
        for (int y = 0; y < 32; y++)
        {
            Array.Fill(bytes, (byte)255, (y * 32) + 16, 16);
        }

        return bytes;
    }
}
