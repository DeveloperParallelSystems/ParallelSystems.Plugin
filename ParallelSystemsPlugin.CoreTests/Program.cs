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
            DocumentationSubstitutionReplacesOnlyExactAssemblyToken();
            DocumentationSubstitutionReportsAmbiguity();
            PreflightReturnsAllNameConflicts();
            Console.WriteLine("PASS: Assembly name parser tests.");
            Console.WriteLine("PASS: Assembly naming sequence tests.");
            Console.WriteLine("PASS: Assembly preflight tests.");
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

    private static void DocumentationSubstitutionReplacesOnlyExactAssemblyToken()
    {
        AssertSubstitution("FAB-CHW001", "CHW001", "CHW500", "FAB-CHW500", true);
        AssertSubstitution("CHW001-SHEET", "CHW001", "CHW500", "CHW500-SHEET", true);
        AssertSubstitution("PRD-CHW-50SM", "CHW-50SM", "CHW-60SM", "PRD-CHW-60SM", true);
        AssertSubstitution("fab-chw001", "CHW001", "CHW500", "fab-CHW500", true);
        AssertSubstitution("DETAIL-001", "CHW001", "CHW500", "DETAIL-001", false);
    }

    private static void DocumentationSubstitutionReportsAmbiguity()
    {
        AssemblyNameSubstitutionResult result = AssemblyNameSubstitution.ReplaceExactToken(
            "CHW001-CHW001",
            "CHW001",
            "CHW500");

        AssertEqual(true, result.IsAmbiguous, "repeated source token ambiguity");
        AssertEqual("CHW001-CHW001", result.Value, "ambiguous name remains unchanged");
    }

    private static void PreflightReturnsAllNameConflicts()
    {
        var index = new AssemblyConflictIndex(
            new[] { "CHW500" },
            new[] { "FAB-CHW500" },
            new[] { "CHW500 - 3D" });

        var proposals = new[]
        {
            new ProposedAssemblyName("CHW001", 1, 1, 500, "chw500"),
            new ProposedAssemblyName("CHR002", 2, 2, 501, "CHR501"),
            new ProposedAssemblyName("CHW003", 3, 3, 502, "CHR501")
        };

        IReadOnlyList<AssemblyPreflightIssue> issues = AssemblyPreflightService.ValidateNames(
            proposals,
            index,
            new[]
            {
                new ProposedDocumentationName(
                    "CHW001",
                    DocumentationNameKind.SheetNumber,
                    "FAB-CHW500"),
                new ProposedDocumentationName(
                    "CHW001",
                    DocumentationNameKind.ViewName,
                    "CHW500 - 3D")
            });

        AssertEqual(4, issues.Count, "all preflight conflicts");
        AssertEqual(
            1,
            issues.Count(x => x.Kind == AssemblyPreflightIssueKind.AssemblyNameConflict),
            "assembly-name conflict count");
        AssertEqual(
            1,
            issues.Count(x => x.Kind == AssemblyPreflightIssueKind.DuplicateProposedName),
            "batch duplicate count");
        AssertEqual(
            1,
            issues.Count(x => x.Kind == AssemblyPreflightIssueKind.SheetNumberConflict),
            "sheet conflict count");
        AssertEqual(
            1,
            issues.Count(x => x.Kind == AssemblyPreflightIssueKind.ViewNameConflict),
            "view conflict count");

        IReadOnlyList<AssemblyPreflightIssue> clean = AssemblyPreflightService.ValidateNames(
            new[] { new ProposedAssemblyName("CHW001", 1, 1, 700, "CHW700") },
            index,
            Array.Empty<ProposedDocumentationName>());

        AssertEqual(0, clean.Count, "clean preflight issue count");
    }

    private static void AssertSubstitution(
        string current,
        string source,
        string proposed,
        string expected,
        bool expectedReplacement)
    {
        AssemblyNameSubstitutionResult result = AssemblyNameSubstitution.ReplaceExactToken(
            current,
            source,
            proposed);

        AssertEqual(expected, result.Value, current + " substitution");
        AssertEqual(expectedReplacement, result.WasReplaced, current + " replacement flag");
        AssertEqual(false, result.IsAmbiguous, current + " ambiguity flag");
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
