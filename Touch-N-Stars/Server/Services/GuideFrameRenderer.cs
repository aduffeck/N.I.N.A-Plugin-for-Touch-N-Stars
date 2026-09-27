using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;

namespace TouchNStars.Server.Services;

/// <summary>
/// Turns a raw 16-bit guide frame into a small, auto-stretched grayscale JPEG for the
/// native guider page. Pure and allocation-light so it can run on the request thread for
/// every guide frame (1-4 s cadence) on a Raspberry Pi.
///
/// The stretch is the PixInsight-style screen transfer function (STF): the shadows are
/// clipped at median - 2.8 * normalized MAD, and a midtones transfer function moves the
/// background median to <see cref="RenderOptions.TargetBackground"/>. Stars end up bright
/// and the noise floor stays visible, which is what one wants to judge a guide frame.
/// </summary>
public static class GuideFrameRenderer
{
    public const double DefaultTargetBackground = 0.2;
    public const double DefaultShadowsClipSigma = -2.8;
    public const int DefaultQuality = 80;
    public const int DefaultMaxWidth = 1024;

    public sealed class RenderOptions
    {
        /// <summary>Output width limit; the frame is binned by an integer factor to fit. 0 = full size.</summary>
        public int MaxWidth { get; set; } = DefaultMaxWidth;

        /// <summary>Where the background median lands after the stretch (0.02 = dark, 0.5 = very bright).</summary>
        public double TargetBackground { get; set; } = DefaultTargetBackground;

        /// <summary>Shadows clipping point in normalized MAD units relative to the median (negative).</summary>
        public double ShadowsClipSigma { get; set; } = DefaultShadowsClipSigma;

        /// <summary>Optional extra gamma applied after the STF (1 = none).</summary>
        public double Gamma { get; set; } = 1.0;

        /// <summary>JPEG quality 1-100.</summary>
        public int Quality { get; set; } = DefaultQuality;
    }

    /// <summary>Brightness summary of a frame, to explain frames that can't be stretched (saturated, no signal).</summary>
    public sealed class FrameLevels
    {
        public int Min { get; init; }
        public int Median { get; init; }
        public int Max { get; init; }

        /// <summary>Highest possible pixel value (255 for 8-bit data, else 65535).</summary>
        public int FullScale { get; init; }

        /// <summary>Share of pixels at or near full scale, 0..100.</summary>
        public double SaturatedPercent { get; init; }

        /// <summary>True when the frame has (almost) no contrast: saturated sky, lens cap with clipped bias, dead camera.</summary>
        public bool Flat { get; init; }
    }

    internal readonly struct StretchParameters
    {
        public StretchParameters(ushort median, double madNormalized, double shadows, double highlights, double midtones)
        {
            Median = median;
            MadNormalized = madNormalized;
            Shadows = shadows;
            Highlights = highlights;
            Midtones = midtones;
        }

        public ushort Median { get; }
        public double MadNormalized { get; }

        /// <summary>Black point in ADU.</summary>
        public double Shadows { get; }

        /// <summary>White point in ADU.</summary>
        public double Highlights { get; }

        /// <summary>Midtones balance of the transfer function (0..1).</summary>
        public double Midtones { get; }
    }

    /// <summary>Midtones transfer function: maps x in [0,1] so that x = m maps to 0.5.</summary>
    internal static double Mtf(double m, double x)
    {
        if (x <= 0) return 0;
        if (x >= 1) return 1;
        if (m <= 0) return 1;
        if (m >= 1) return 0;
        return (m - 1) * x / ((2 * m - 1) * x - m);
    }

    /// <summary>
    /// Computes the STF parameters from the frame histogram. A uniform frame has nothing to stretch; it is shown at its
    /// absolute level against <paramref name="fullScale"/> (a saturated frame white, an empty one black).
    /// </summary>
    internal static StretchParameters ComputeStretch(ushort[] pixels, double targetBackground = DefaultTargetBackground, double shadowsClipSigma = DefaultShadowsClipSigma, int fullScale = 65535)
    {
        if (pixels == null || pixels.Length == 0)
        {
            return new StretchParameters(0, 0, 0, 1, 0.5);
        }

        var histogram = new int[65536];
        ushort min = ushort.MaxValue;
        ushort max = 0;
        foreach (ushort v in pixels)
        {
            histogram[v]++;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        ushort median = PercentileFromHistogram(histogram, pixels.Length, 0.5);
        if (min == max)
        {
            return new StretchParameters(median, 0, 0, Math.Max(fullScale, (int)max), 0.5);
        }

        // Median absolute deviation: histogram of |v - median| built from the value histogram.
        var deviations = new int[65536];
        for (int v = min; v <= max; v++)
        {
            int count = histogram[v];
            if (count == 0) continue;
            deviations[Math.Abs(v - median)] += count;
        }
        ushort mad = PercentileFromHistogram(deviations, pixels.Length, 0.5);
        double madNormalized = 1.4826 * mad;

        double highlights = Math.Max(max, (double)min + 1);
        double shadows = madNormalized > 0
            ? Math.Max(min, median + shadowsClipSigma * madNormalized)
            : min;
        if (shadows >= highlights) shadows = highlights - 1;

        double normalizedMedian = (median - shadows) / (highlights - shadows);
        double target = Math.Clamp(targetBackground, 0.01, 0.9);
        double midtones = normalizedMedian > 0 && normalizedMedian < 1
            ? Mtf(target, normalizedMedian)
            : 0.5;

        return new StretchParameters(median, madNormalized, shadows, highlights, midtones);
    }

    /// <summary>255 for 8-bit data, else 65535 (12/14-bit cameras deliver their data scaled to 16 bit).</summary>
    internal static int FullScaleFor(int bitDepth, int max) => bitDepth is > 0 and <= 8 && max <= 255 ? 255 : 65535;

    /// <summary>Min/median/max, saturated share and a flatness flag of the frame.</summary>
    public static FrameLevels ComputeLevels(ushort[] pixels, int bitDepth)
    {
        if (pixels == null || pixels.Length == 0)
        {
            return new FrameLevels { FullScale = FullScaleFor(bitDepth, 0), Flat = true };
        }

        var histogram = new int[65536];
        ushort min = ushort.MaxValue;
        ushort max = 0;
        foreach (ushort v in pixels)
        {
            histogram[v]++;
            if (v < min) min = v;
            if (v > max) max = v;
        }

        int fullScale = FullScaleFor(bitDepth, max);

        // 12-bit cameras saturate at 65520, 14-bit ones at 65532: count everything within 2 % of full scale
        int saturationLevel = (int)(fullScale * 0.98);
        long saturated = 0;
        for (int v = Math.Max(saturationLevel, min); v <= max; v++) saturated += histogram[v];

        return new FrameLevels
        {
            Min = min,
            Median = PercentileFromHistogram(histogram, pixels.Length, 0.5),
            Max = max,
            FullScale = fullScale,
            SaturatedPercent = 100.0 * saturated / pixels.Length,
            Flat = max - min < Math.Max(2, fullScale * 0.002)
        };
    }

    /// <summary>Lookup table ADU -> display byte for the given stretch.</summary>
    internal static byte[] BuildLookupTable(StretchParameters p, double gamma = 1.0)
    {
        var lut = new byte[65536];
        double range = Math.Max(1e-9, p.Highlights - p.Shadows);
        double invGamma = gamma > 0 && Math.Abs(gamma - 1.0) > 1e-6 ? 1.0 / gamma : 1.0;
        for (int v = 0; v < lut.Length; v++)
        {
            double x = (v - p.Shadows) / range;
            double y = Mtf(p.Midtones, Math.Clamp(x, 0, 1));
            if (invGamma != 1.0) y = Math.Pow(y, invGamma);
            lut[v] = (byte)Math.Clamp((int)Math.Round(y * 255.0), 0, 255);
        }
        return lut;
    }

    /// <summary>Integer bin factor so that width / factor &lt;= maxWidth.</summary>
    internal static int BinFactorFor(int width, int maxWidth)
    {
        if (maxWidth <= 0 || width <= maxWidth) return 1;
        return (int)Math.Ceiling(width / (double)maxWidth);
    }

    /// <summary>
    /// Bins (mean of factor x factor blocks, partial edge blocks dropped) and maps through the LUT.
    /// Returns the 8-bit gray pixels and the output size.
    /// </summary>
    internal static byte[] BinAndMap(ushort[] pixels, int width, int height, int factor, byte[] lut, out int outWidth, out int outHeight)
    {
        if (factor < 1) factor = 1;
        outWidth = Math.Max(1, width / factor);
        outHeight = Math.Max(1, height / factor);
        var output = new byte[outWidth * outHeight];

        if (factor == 1)
        {
            int n = Math.Min(pixels.Length, output.Length);
            for (int i = 0; i < n; i++) output[i] = lut[pixels[i]];
            return output;
        }

        int area = factor * factor;
        for (int oy = 0; oy < outHeight; oy++)
        {
            int y0 = oy * factor;
            for (int ox = 0; ox < outWidth; ox++)
            {
                int x0 = ox * factor;
                long sum = 0;
                for (int dy = 0; dy < factor; dy++)
                {
                    int row = (y0 + dy) * width + x0;
                    for (int dx = 0; dx < factor; dx++)
                    {
                        sum += pixels[row + dx];
                    }
                }
                output[oy * outWidth + ox] = lut[(int)(sum / area)];
            }
        }
        return output;
    }

    /// <summary>
    /// Renders the frame to a JPEG. <paramref name="bitDepth"/> is the camera's; it decides the full-scale level a
    /// uniform frame is shown against. Throws on inconsistent dimensions.
    /// </summary>
    public static byte[] RenderJpeg(ushort[] pixels, int width, int height, RenderOptions options = null, int bitDepth = 16)
        => RenderJpeg(pixels, width, height, options, bitDepth, out _, out _, out _);

    /// <summary>
    /// Renders the frame to a JPEG and reports the bin factor applied to fit <see cref="RenderOptions.MaxWidth"/>
    /// and the size of the JPEG. Throws on inconsistent dimensions.
    /// </summary>
    public static byte[] RenderJpeg(ushort[] pixels, int width, int height, RenderOptions options, int bitDepth, out int binFactor, out int outWidth, out int outHeight)
    {
        options ??= new RenderOptions();
        if (pixels == null || width <= 0 || height <= 0 || pixels.Length < (long)width * height)
        {
            throw new ArgumentException($"Frame buffer does not match {width}x{height}");
        }

        int max = 0;
        for (int i = 0; i < pixels.Length && max < 256; i++) max = Math.Max(max, pixels[i]);
        var stretch = ComputeStretch(pixels, options.TargetBackground, options.ShadowsClipSigma, FullScaleFor(bitDepth, max));
        byte[] lut = BuildLookupTable(stretch, options.Gamma);
        binFactor = BinFactorFor(width, options.MaxWidth);
        byte[] gray = BinAndMap(pixels, width, height, binFactor, lut, out outWidth, out outHeight);

        using var image = Image.LoadPixelData<L8>(gray, outWidth, outHeight);
        using var stream = new MemoryStream();
        image.SaveAsJpeg(stream, new JpegEncoder
        {
            Quality = Math.Clamp(options.Quality, 1, 100),
            ColorType = JpegEncodingColor.Luminance
        });
        return stream.ToArray();
    }

    /// <summary>
    /// Copies a square crop (clamped to the frame) around (cx, cy) for client-side star profiles.
    /// </summary>
    public static ushort[] Crop(ushort[] pixels, int width, int height, double cx, double cy, int size, out int x0, out int y0, out int cropWidth, out int cropHeight)
    {
        size = Math.Clamp(size, 3, 128);
        int half = size / 2;
        x0 = Math.Clamp((int)Math.Round(cx) - half, 0, Math.Max(0, width - 1));
        y0 = Math.Clamp((int)Math.Round(cy) - half, 0, Math.Max(0, height - 1));
        int x1 = Math.Min(width, x0 + size);
        int y1 = Math.Min(height, y0 + size);
        cropWidth = Math.Max(0, x1 - x0);
        cropHeight = Math.Max(0, y1 - y0);
        var crop = new ushort[cropWidth * cropHeight];
        for (int y = 0; y < cropHeight; y++)
        {
            Array.Copy(pixels, (y0 + y) * width + x0, crop, y * cropWidth, cropWidth);
        }
        return crop;
    }

    private static ushort PercentileFromHistogram(int[] histogram, int total, double fraction)
    {
        long target = (long)Math.Ceiling(total * fraction);
        if (target < 1) target = 1;
        long cumulative = 0;
        for (int i = 0; i < histogram.Length; i++)
        {
            cumulative += histogram[i];
            if (cumulative >= target) return (ushort)i;
        }
        return ushort.MaxValue;
    }
}
