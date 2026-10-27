using System.Globalization;

namespace YourLocalLib.Core;

public static class UserRatingScale
{
    public static readonly double[] Steps = { 0.5, 1, 1.5, 2, 2.5, 3, 3.5, 4, 4.5, 5 };

    public static bool TryCanonical(double value, out double canonical)
    {
        foreach (var step in Steps)
        {
            if (Math.Abs(value - step) < 0.001)
            {
                canonical = step;
                return true;
            }
        }
        canonical = 0;
        return false;
    }

    public static string Label(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
