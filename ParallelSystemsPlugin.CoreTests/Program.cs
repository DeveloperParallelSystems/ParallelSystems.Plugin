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
            NamingOrdersByFinalNumberAndGeneratesSequence();
            NamingUsesDeterministicTieBreakers();
            NamingPreservesSuffixAndPadding();
            NamingRejectsInvalidInputAndOverflow();
            Console.WriteLine("PASS: Assembly name parser tests.");
            Console.WriteLine("PASS: Assembly naming sequence tests.");
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

    private static void NamingOrdersByFinalNumberAndGeneratesSequence()
    {
        IReadOnlyList<ProposedAssemblyName> proposals = AssemblyNamingService.Generate(
            new[]
            {
                new AssemblyNamingCandidate("CHR111", 30),
                new AssemblyNamingCandidate("CHW002", 20),
                new AssemblyNamingCandidate("CHW001", 10)
            },
            500);

        AssertSequence(
            new[] { "CHW500", "CHW501", "CHR502" },
            proposals.Select(x => x.ProposedName),
            "numeric order and generated names");

        IReadOnlyList<ProposedAssemblyName> mixed = AssemblyNamingService.Generate(
            new[]
            {
                new AssemblyNamingCandidate("CHR112", 3),
                new AssemblyNamingCandidate("CHW110", 1),
                new AssemblyNamingCandidate("CHR111", 2)
            },
            500);

        AssertSequence(
            new[] { "CHW500", "CHR501", "CHR502" },
            mixed.Select(x => x.ProposedName),
            "mixed-prefix numeric order");
    }

    private static void NamingUsesDeterministicTieBreakers()
    {
        IReadOnlyList<ProposedAssemblyName> proposals = AssemblyNamingService.Generate(
            new[]
            {
                new AssemblyNamingCandidate("CHW001", 20),
                new AssemblyNamingCandidate("CHR001", 30),
                new AssemblyNamingCandidate("CHW001", 10)
            },
            5);

        AssertSequence(
            new long[] { 30, 10, 20 },
            proposals.Select(x => x.StableElementId),
            "name and element-id tie breakers");
    }

    private static void NamingPreservesSuffixAndPadding()
    {
        AssertGenerated("CHW-50SM", 60, "CHW-60SM");
        AssertGenerated("CHW-L02-007-A", 100, "CHW-L02-100-A");
        AssertGenerated("CHW001", 5, "CHW005");
        AssertGenerated("CHW999", 1000, "CHW1000");
    }

    private static void NamingRejectsInvalidInputAndOverflow()
    {
        AssertThrows<ArgumentException>(() => AssemblyNamingService.Generate(
            new[]
            {
                new AssemblyNamingCandidate("CHW001", 1),
                new AssemblyNamingCandidate("ASSEMBLY-A", 2)
            },
            500));

        AssertThrows<OverflowException>(() => AssemblyNamingService.Generate(
            new[]
            {
                new AssemblyNamingCandidate("CHW001", 1),
                new AssemblyNamingCandidate("CHW002", 2)
            },
            long.MaxValue));
    }

    private static void AssertGenerated(string source, long start, string expected)
    {
        ProposedAssemblyName proposal = AssemblyNamingService.Generate(
            new[] { new AssemblyNamingCandidate(source, 1) },
            start)[0];

        AssertEqual(expected, proposal.ProposedName, source + " generated name");
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

    private static void AssertSequence<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        string label)
    {
        T[] expectedValues = expected.ToArray();
        T[] actualValues = actual.ToArray();
        if (!expectedValues.SequenceEqual(actualValues))
        {
            throw new InvalidOperationException(
                $"Unexpected {label}. Expected [{string.Join(", ", expectedValues)}], " +
                $"actual [{string.Join(", ", actualValues)}].");
        }
    }

    private static void AssertThrows<TException>(Action action)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected exception {typeof(TException).Name}.");
    }
}
