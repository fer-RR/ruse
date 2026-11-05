namespace Ruse.App.Audio;

/// <summary>
/// Keeps recursive DSP state out of the subnormal range. Flushing at block
/// boundaries is cheap and inaudible, and avoids the large CPU penalty some
/// CPUs charge for denormal arithmetic after long silent tails.
/// </summary>
internal static class DenormalMath
{
    private const double Threshold = 1e-15;

    public static void Flush(double[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            if (Math.Abs(values[i]) < Threshold)
            {
                values[i] = 0.0;
            }
        }
    }

    public static void Flush(double[,] values)
    {
        for (int row = 0; row < values.GetLength(0); row++)
        {
            for (int column = 0; column < values.GetLength(1); column++)
            {
                if (Math.Abs(values[row, column]) < Threshold)
                {
                    values[row, column] = 0.0;
                }
            }
        }
    }

    public static double Flush(double value) =>
        Math.Abs(value) < Threshold ? 0.0 : value;
}
