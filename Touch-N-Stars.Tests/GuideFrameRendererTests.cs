using System;
using System.Linq;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using TouchNStars.Server.Services;
using Xunit;

namespace TouchNStars.Tests;

public class GuideFrameRendererTests
{
    /// <summary>Background ~1000 ADU with Gaussian-ish noise and one bright star.</summary>
    private static ushort[] SyntheticFrame(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var pixels = new ushort[width * height];
        for (int i = 0; i < pixels.Length; i++)
        {
            double noise = (random.NextDouble() + random.NextDouble() + random.NextDouble() - 1.5) * 40;
            pixels[i] = (ushort)Math.Clamp(1000 + noise, 0, 65535);
        }
        int cx = width / 2, cy = height / 2;
        for (int y = cy - 6; y <= cy + 6; y++)
        {
            for (int x = cx - 6; x <= cx + 6; x++)
            {
                double r2 = (x - cx) * (x - cx) + (y - cy) * (y - cy);
                pixels[y * width + x] = (ushort)Math.Clamp(pixels[y * width + x] + 30000 * Math.Exp(-r2 / 4.0), 0, 65535);
            }
        }
        return pixels;
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.8)]
    public void Mtf_MapsMidtonesBalanceToHalf(double m)
    {
        Assert.Equal(0.5, GuideFrameRenderer.Mtf(m, m), 10);
    }

    [Fact]
    public void Mtf_KeepsEndpoints()
    {
        Assert.Equal(0, GuideFrameRenderer.Mtf(0.2, 0));
        Assert.Equal(1, GuideFrameRenderer.Mtf(0.2, 1));
    }

    [Fact]
    public void ComputeStretch_PutsTheBackgroundMedianAtTheTarget()
    {
        ushort[] pixels = SyntheticFrame(200, 150);
        var p = GuideFrameRenderer.ComputeStretch(pixels, targetBackground: 0.25);

        Assert.InRange(p.Median, 990, 1010);
        Assert.True(p.Shadows < p.Median, "black point below the median");
        Assert.True(p.Highlights > 20000, "white point at the star peak");

        double normalizedMedian = (p.Median - p.Shadows) / (p.Highlights - p.Shadows);
        Assert.Equal(0.25, GuideFrameRenderer.Mtf(p.Midtones, normalizedMedian), 6);
    }

    [Fact]
    public void ComputeStretch_HandlesAFlatFrame()
    {
        var pixels = Enumerable.Repeat((ushort)500, 64).ToArray();
        var p = GuideFrameRenderer.ComputeStretch(pixels);
        Assert.True(p.Highlights > p.Shadows);
        byte[] lut = GuideFrameRenderer.BuildLookupTable(p);
        Assert.Equal(256 * 256, lut.Length);
    }

    [Theory]
    [InlineData(65520, 250)] // ASI120 (12 bit) saturated by the twilight sky: white, not black
    [InlineData(65535, 250)]
    [InlineData(0, 0)]
    public void RenderJpeg_ShowsAUniformFrameAtItsAbsoluteLevel(int value, int minGray)
    {
        var pixels = Enumerable.Repeat((ushort)value, 64 * 48).ToArray();
        byte[] jpeg = GuideFrameRenderer.RenderJpeg(pixels, 64, 48);

        using var image = Image.Load<L8>(jpeg);
        byte gray = image[32, 24].PackedValue;
        Assert.InRange(gray, minGray, value == 0 ? 5 : 255);
    }

    [Fact]
    public void ComputeLevels_FlagsASaturatedFlatFrame()
    {
        var pixels = Enumerable.Repeat((ushort)65520, 1000).ToArray();
        var levels = GuideFrameRenderer.ComputeLevels(pixels, 16);

        Assert.Equal(65520, levels.Median);
        Assert.Equal(65535, levels.FullScale);
        Assert.Equal(100, levels.SaturatedPercent, 6);
        Assert.True(levels.Flat);
    }

    [Fact]
    public void ComputeLevels_DescribesANormalFrame()
    {
        var levels = GuideFrameRenderer.ComputeLevels(SyntheticFrame(200, 150), 16);

        Assert.InRange(levels.Median, 990, 1010);
        Assert.True(levels.Max > 20000);
        Assert.True(levels.SaturatedPercent < 0.01);
        Assert.False(levels.Flat);
    }

    [Fact]
    public void ComputeLevels_Uses8BitFullScaleFor8BitData()
    {
        var pixels = Enumerable.Repeat((ushort)255, 100).ToArray();
        var levels = GuideFrameRenderer.ComputeLevels(pixels, 8);

        Assert.Equal(255, levels.FullScale);
        Assert.Equal(100, levels.SaturatedPercent, 6);
    }

    [Fact]
    public void LookupTable_IsMonotonicAndSpansTheRange()
    {
        var p = GuideFrameRenderer.ComputeStretch(SyntheticFrame(100, 80));
        byte[] lut = GuideFrameRenderer.BuildLookupTable(p);
        for (int i = 1; i < lut.Length; i++)
        {
            Assert.True(lut[i] >= lut[i - 1], $"LUT decreases at {i}");
        }
        Assert.Equal(0, lut[0]);
        Assert.Equal(255, lut[65535]);
        // The background lands around the target background (0.2 * 255 ~ 51).
        Assert.InRange(lut[p.Median], 40, 65);
    }

    [Theory]
    [InlineData(1936, 1024, 2)]
    [InlineData(1024, 1024, 1)]
    [InlineData(640, 1024, 1)]
    [InlineData(4000, 1024, 4)]
    [InlineData(1936, 0, 1)]
    public void BinFactor_FitsTheWidth(int width, int maxWidth, int expected)
    {
        Assert.Equal(expected, GuideFrameRenderer.BinFactorFor(width, maxWidth));
    }

    [Fact]
    public void BinAndMap_AveragesBlocksAndDropsPartialEdges()
    {
        // 5x3 frame, factor 2 -> 2x1 output; identity-ish LUT (value/1).
        ushort[] pixels =
        {
            10, 20, 30, 40, 99,
            30, 40, 50, 60, 99,
            99, 99, 99, 99, 99
        };
        var lut = new byte[65536];
        for (int i = 0; i < 256; i++) lut[i] = (byte)i;

        byte[] output = GuideFrameRenderer.BinAndMap(pixels, 5, 3, 2, lut, out int w, out int h);

        Assert.Equal(2, w);
        Assert.Equal(1, h);
        Assert.Equal(new byte[] { 25, 45 }, output);
    }

    [Fact]
    public void RenderJpeg_ProducesADecodableDownscaledJpeg()
    {
        ushort[] pixels = SyntheticFrame(1936 / 4, 1216 / 4);
        byte[] jpeg = GuideFrameRenderer.RenderJpeg(pixels, 1936 / 4, 1216 / 4, new GuideFrameRenderer.RenderOptions { MaxWidth = 200 });

        Assert.Equal(0xFF, jpeg[0]);
        Assert.Equal(0xD8, jpeg[1]);
        using var image = Image.Load<L8>(jpeg);
        Assert.Equal(484 / 3, image.Width);
        Assert.Equal(304 / 3, image.Height);
    }

    [Fact]
    public void RenderJpeg_RejectsAShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => GuideFrameRenderer.RenderJpeg(new ushort[10], 10, 10));
    }

    [Fact]
    public void Crop_ClampsAtTheFrameEdge()
    {
        var pixels = Enumerable.Range(0, 100).Select(i => (ushort)i).ToArray(); // 10x10
        ushort[] crop = GuideFrameRenderer.Crop(pixels, 10, 10, 1, 1, 5, out int x0, out int y0, out int w, out int h);

        Assert.Equal(0, x0);
        Assert.Equal(0, y0);
        Assert.Equal(5, w);
        Assert.Equal(5, h);
        Assert.Equal(0, crop[0]);
        Assert.Equal(14, crop[5 + 4]); // row 1, column 4

        crop = GuideFrameRenderer.Crop(pixels, 10, 10, 9, 9, 5, out x0, out y0, out w, out h);
        Assert.Equal(7, x0);
        Assert.Equal(7, y0);
        Assert.Equal(3, w);
        Assert.Equal(3, h);
        Assert.Equal(77, crop[0]);
        Assert.Equal(9, crop.Length);
    }
}
