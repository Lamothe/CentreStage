namespace CentreStage.Core;

/// <summary>
/// Peak signal-to-noise ratio between two RGB rasters.
///
/// This is the arithmetic behind the numbers the neural-rendering demos print and
/// draw, so it lives here where it can be tested without a GL context. Buffers are
/// top-down RGB8 with no row padding; regions are given in top-left pixel
/// coordinates.
/// </summary>
public static class RasterMetrics
{
    /// <summary>PSNR in dB over both rasters in full. Identical rasters give positive infinity.</summary>
    public static double Psnr(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> reference)
    {
        if (candidate.Length != reference.Length || candidate.Length % 3 != 0)
        {
            throw new ArgumentException("PSNR buffers must be equally sized RGB triples");
        }

        var (squared, _) = SumSquaredError(candidate, reference);
        return ToPsnr(squared, candidate.Length);
    }

    /// <summary>PSNR in dB over a rectangular region, in top-down pixel coordinates.</summary>
    public static double PsnrRegion(ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> reference,
        int width, int height, int x, int y, int regionWidth, int regionHeight)
    {
        if (width <= 0 || height <= 0 || candidate.Length != width * height * 3)
        {
            throw new ArgumentException("PSNR buffers must be RGB triples of width*height");
        }
        if (candidate.Length != reference.Length)
        {
            throw new ArgumentException("PSNR buffers must be equally sized");
        }
        if (x < 0 || y < 0 || regionWidth <= 0 || regionHeight <= 0
            || x > width - regionWidth || y > height - regionHeight)
        {
            throw new ArgumentException("PSNR region does not fit the image");
        }

        var squared = 0.0;
        for (var row = y; row < y + regionHeight; row++)
        {
            var start = (row * width + x) * 3;
            var end = start + regionWidth * 3;
            squared += SumSquaredError(candidate[start..end], reference[start..end]).Squared;
        }

        return ToPsnr(squared, regionWidth * regionHeight * 3);
    }

    private static (double Squared, int Count) SumSquaredError(
        ReadOnlySpan<byte> candidate, ReadOnlySpan<byte> reference)
    {
        var squared = 0.0;
        for (var i = 0; i < candidate.Length; i++)
        {
            var difference = candidate[i] - reference[i];
            squared += difference * difference;
        }

        return (squared, candidate.Length);
    }

    private static double ToPsnr(double squared, int samples)
    {
        if (samples <= 0)
        {
            throw new ArgumentException("PSNR needs at least one sample");
        }

        var mse = squared / samples;
        return mse <= 0 ? double.PositiveInfinity : 10.0 * Math.Log10(255.0 * 255.0 / mse);
    }
}