using System.Globalization;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: finds only the final numeric sequence in an assembly name.
    public static class AssemblyNameParser
    {
        public static bool TryParse(
            string sourceName,
            out AssemblyNameParts parts,
            out string error)
        {
            parts = null;
            error = null;

            if (string.IsNullOrWhiteSpace(sourceName))
            {
                error = "Assembly name is blank.";
                return false;
            }

            int numberEnd = -1;
            for (int index = sourceName.Length - 1; index >= 0; index--)
            {
                if (char.IsDigit(sourceName[index]))
                {
                    numberEnd = index;
                    break;
                }
            }

            if (numberEnd < 0)
            {
                error = "Unable to detect a numeric sequence in the assembly name.";
                return false;
            }

            int numberStart = numberEnd;
            while (numberStart > 0 && char.IsDigit(sourceName[numberStart - 1]))
                numberStart--;

            string numericText = sourceName.Substring(
                numberStart,
                numberEnd - numberStart + 1);

            if (!long.TryParse(
                    numericText,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out long numericValue))
            {
                error = "The final numeric sequence is too large.";
                return false;
            }

            parts = new AssemblyNameParts(
                sourceName,
                sourceName.Substring(0, numberStart),
                numericText,
                numericValue,
                sourceName.Substring(numberEnd + 1));

            return true;
        }
    }
}
