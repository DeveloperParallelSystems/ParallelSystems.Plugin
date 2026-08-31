using System;
using System.Collections.Generic;
using System.Globalization;

namespace ParallelSystemsPlugin.Helpers
{
    public static class String
    {
        public static double ToDouble(this string str)
        {
            if (string.IsNullOrWhiteSpace(str))
                return 0d;

            if (double.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var invariant))
                return invariant;

            return double.TryParse(str, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out var current)
                ? current
                : 0d;
        }

        public static List<string> GeneratePairs(List<string> list)
        {
            var result = new List<string>();

            for (int i = 0; i < list.Count; i++)
            {
                for (int j = i; j < list.Count; j++)
                {
                    result.Add($"{list[i]}-{list[j]}");
                }
            }

            return result;
        }
    }
}
