using ParallelSystemsPlugin.AssemblyDuplication;

internal static class Program
{
    private static int Main()
    {
        try
        {
            ParserUsesFinalNumericBlock();
            ParserPreservesSuffix();
            ParserRejectsInvalidNames();
            Console.WriteLine("PASS: Assembly name parser tests.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static void ParserUsesFinalNumericBlock()
    {
        AssertParts("CHW001", "CHW", "001", 1L, "", 3);
        AssertParts("CHW-L02-007-A", "CHW-L02-", "007", 7L, "-A", 3);
    }

    private static void ParserPreservesSuffix()
    {
        AssertParts("CHW-50SM", "CHW-", "50", 50L, "SM", 2);
    }

    private static void ParserRejectsInvalidNames()
    {
        AssertInvalid("");
        AssertInvalid("CHW-MAIN");
        AssertInvalid("CHW999999999999999999999999");
    }

    private static void AssertParts(
        string source,
        string prefix,
        string numericText,
        long numericValue,
        string suffix,
        int minimumWidth)
    {
        if (!AssemblyNameParser.TryParse(source, out AssemblyNameParts parts, out string error))
            throw new InvalidOperationException($"Expected '{source}' to parse: {error}");

        AssertEqual(source, parts.SourceName, source + " source");
        AssertEqual(prefix, parts.Prefix, source + " prefix");
        AssertEqual(numericText, parts.NumericText, source + " numeric text");
        AssertEqual(numericValue, parts.NumericValue, source + " numeric value");
        AssertEqual(suffix, parts.Suffix, source + " suffix");
        AssertEqual(minimumWidth, parts.MinimumWidth, source + " width");
    }

    private static void AssertInvalid(string source)
    {
        if (AssemblyNameParser.TryParse(source, out _, out string error))
            throw new InvalidOperationException($"Expected '{source}' to be invalid.");

        if (string.IsNullOrWhiteSpace(error))
            throw new InvalidOperationException($"Expected an error for invalid name '{source}'.");
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException(
                $"Unexpected {label}. Expected '{expected}', actual '{actual}'.");
        }
    }
}
