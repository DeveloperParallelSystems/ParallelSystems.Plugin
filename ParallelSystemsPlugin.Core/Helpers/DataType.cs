using System.Globalization;

namespace ParallelSystemsPlugin.Helpers
{
    public static class DataType
    {
        public static double StringToDouble(this string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return 0d;

            if (double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var invariant))
                return invariant;

            return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var current)
                ? current
                : 0d;
        }
    }
}
