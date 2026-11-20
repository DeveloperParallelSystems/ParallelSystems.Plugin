using System.Reflection;
using ParallelSystemsPlugin.AssemblyDuplication;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class Program
{
    private static readonly string[] ExpectedHeaders =
    {
        "Project Number",
        "Project Name",
        "Project Phase",
        "Spool Number",
        "Mark Item",
        "Material Grade",
        "Pipe Size",
        "Pipe End Prep",
        "Cut Length",
        "QR Code"
    };

    private static int Main(string[] args)
    {
        try
        {
            PdfConflictTests.ExcelCutListEntryDoesNotInitializePdf();
            if (args.Contains("--description-audit"))
            {
                LabelDescriptionTests.Run();
                PipeDescriptionTests.Run();
                AssemblyLoadingDescriptionTests.Run();
                FieldMaterialReportTests.Run();
                FittingReportTests.Run();
                AccessoryReportTests.Run();
                AssemblyRegisterReportTests.RunExcel();
                AssemblyRegisterReportTests.RunPdf();
                return 0;
            }
            LabelDescriptionTests.Run();
            PipeDescriptionTests.Run();
            AssemblyLoadingDescriptionTests.Run();
            FieldMaterialReportTests.Run();
            if (args.Contains("--field-material")) return 0;
            AssemblyRegisterReportTests.RunExcel();
            if (args.Contains("--assembly-register"))
            {
                AssemblyRegisterReportTests.RunPdf();
                return 0;
            }
            FittingReportTests.Run();
            if (args.Contains("--fitting")) return 0;
            AccessoryReportTests.Run();
            if (args.Contains("--accessory")) return 0;
            CutListReportTests.Run();
            ProcurementSiteMeasureSuffixRequiresTerminalMarker();
            ProcurementFittingWeldTogglePreservesOtherExclusions();
            LabelReportExcelSheetStartsWithSimpleHeaderAndRepeatsProjectDetails();
            ModuleStepAllowsPipingWithoutSupports();
            SlipOnSetbackUsesDynamicNominalDiameterDefaults();
            SlipOnSetbackUsesConfiguredBandValues();
            SlipOnSetbackRejectsNonPositiveBandValues();
            SlipOnFitUpSupportsDirectShapedBranchConnections();
            SmoothBranchBRepUsesHighPrecisionDeviationBudget();
            AssemblyIdentityMarkerNamesAreSafeAndUnique();
            PdfConflictTests.PdfExportStillChecksConflicts();
            AssemblyRegisterReportTests.RunPdf();
            Console.WriteLine("PASS: Label report Excel sheet uses the simple row-based layout.");
            Console.WriteLine("PASS: Module STEP allows piping without support components.");
            Console.WriteLine("PASS: Slip-on setback resolves dynamically from nominal diameter.");
            Console.WriteLine("PASS: Slip-on fit-up supports direct shaped-branch outlets.");
            Console.WriteLine("PASS: Shaped-branch BRep uses the high-precision deviation budget.");
            Console.WriteLine("PASS: Assembly identity marker names are safe and unique.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
        }
    }

    private static void ProcurementSiteMeasureSuffixRequiresTerminalMarker()
    {
        MethodInfo method = typeof(Elements).GetMethod(
            "HasSiteMeasureSuffix", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HasSiteMeasureSuffix was not found.");

        foreach (string identity in new[] { "SM", "SPOOL-087SM", "SPOOL-SM", "SPOOL sm  ", "SPOOL_SM" })
            AssertEqual(true, (bool)method.Invoke(null, new object[] { identity }), "site-measure suffix: " + identity);

        foreach (string identity in new[] { null, "", "SPOOL-087", "SM-SPOOL", "SPOOL-SM-01", "PRISM", "CHASM", "MECHANISM" })
            AssertEqual(false, (bool)method.Invoke(null, new object[] { identity }), "ordinary assembly identity: " + identity);
    }

    private static void ProcurementFittingWeldTogglePreservesOtherExclusions()
    {
        MethodInfo method = typeof(BomFittingReport).GetMethod(
            "IsExcludedCategory", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("IsExcludedCategory was not found.");

        foreach (bool includeWeld in new[] { false, true })
        {
            AssertEqual(!includeWeld, (bool)method.Invoke(null, new object[] { "WELD", includeWeld }), "fitting weld toggle");
            AssertEqual(!includeWeld, (bool)method.Invoke(null, new object[] { "weld", includeWeld }), "case-insensitive fitting weld toggle");
            AssertEqual(false, (bool)method.Invoke(null, new object[] { "ELBOW", includeWeld }), "ordinary fitting remains eligible");
            AssertEqual(true, (bool)method.Invoke(null, new object[] { "SHAPED BRANCH", includeWeld }), "shaped-branch exclusion remains active");
        }
    }

    private static void AssemblyIdentityMarkerNamesAreSafeAndUnique()
    {
        Guid first = Guid.Parse("7f3c18a2-0000-0000-0000-000000000000");
        Guid second = Guid.Parse("9a5d2b11-0000-0000-0000-000000000000");

        string a = AssemblyIdentityMarkerName.Create("CHW500", first);
        string b = AssemblyIdentityMarkerName.Create("CHW500", second);
        string unusual = AssemblyIdentityMarkerName.Create("CHW/500:{bad}|name", first);
        string bounded = AssemblyIdentityMarkerName.Create(new string('X', 200), first);

        AssertEqual("PS_ASM_ID_CHW500_7F3C18A2", a, "first identity name");
        AssertEqual(false, string.Equals(a, b, StringComparison.Ordinal), "unique identity names");
        AssertEqual(
            false,
            unusual.IndexOfAny(new[] { '/', ':', '{', '}', '|' }) >= 0,
            "safe identity characters");
        AssertEqual(true, bounded.Length <= 120, "bounded identity name length");
    }

    private static void SlipOnSetbackUsesDynamicNominalDiameterDefaults()
    {
        var configuration = new FabricationConfig();

        AssertEqual(
            6d,
            configuration.ResolveSlipOnPipeFaceSetbackMillimetres(125),
            "DN125 slip-on default setback");
        AssertEqual(
            10d,
            configuration.ResolveSlipOnPipeFaceSetbackMillimetres(150),
            "DN150 slip-on default setback");
    }

    private static void SlipOnSetbackUsesConfiguredBandValues()
    {
        var configuration = new FabricationConfig
        {
            SlipOnPipeFaceSetbackDn125AndBelowMillimetres = 7d,
            SlipOnPipeFaceSetbackDn150AndAboveMillimetres = 11d
        };

        AssertEqual(
            7d,
            configuration.ResolveSlipOnPipeFaceSetbackMillimetres(100),
            "configured DN125-and-below setback");
        AssertEqual(
            11d,
            configuration.ResolveSlipOnPipeFaceSetbackMillimetres(200),
            "configured DN150-and-above setback");
    }

    private static void SlipOnSetbackRejectsNonPositiveBandValues()
    {
        var configuration = new FabricationConfig
        {
            SlipOnPipeFaceSetbackDn125AndBelowMillimetres = 0d
        };

        AssertThrows<InvalidOperationException>(
            () => configuration.ResolveSlipOnPipeFaceSetbackMillimetres(125),
            "non-positive DN-band setback");
    }

    private static void SlipOnFitUpSupportsDirectShapedBranchConnections()
    {
        Type serviceType = typeof(LabelReport).Assembly.GetType(
            "ParallelSystemsPlugin.Fabrication.FabricationStepService")
            ?? throw new InvalidOperationException("FabricationStepService was not found.");

        MethodInfo method = serviceType.GetMethod(
            "IsSupportedSlipOnFitUpConnectedElement",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "IsSupportedSlipOnFitUpConnectedElement was not found.");

        AssertEqual(
            true,
            (bool)method.Invoke(null, new object[] { true, false }),
            "pipe slip-on fit-up support");
        AssertEqual(
            true,
            (bool)method.Invoke(null, new object[] { false, true }),
            "direct shaped-branch slip-on fit-up support");
        AssertEqual(
            false,
            (bool)method.Invoke(null, new object[] { false, false }),
            "unclassified fitting slip-on fit-up support");
    }

    private static void SmoothBranchBRepUsesHighPrecisionDeviationBudget()
    {
        Type serviceType = typeof(LabelReport).Assembly.GetType(
            "ParallelSystemsPlugin.Fabrication.FabricationStepService")
            ?? throw new InvalidOperationException("FabricationStepService was not found.");

        FieldInfo field = serviceType.GetField(
            "SmoothBRepMaximumDeviationMillimetres",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "SmoothBRepMaximumDeviationMillimetres was not found.");

        double deviation = (double)field.GetRawConstantValue();
        AssertEqual(0.01d, deviation, "smooth branch BRep deviation budget");
    }

    private static void ModuleStepAllowsPipingWithoutSupports()
    {
        Type serviceType = typeof(LabelReport).Assembly.GetType(
            "ParallelSystemsPlugin.Fabrication.FabricationStepService")
            ?? throw new InvalidOperationException("FabricationStepService was not found.");

        MethodInfo method = serviceType.GetMethod(
            "IsValidModuleSelectionCounts",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "IsValidModuleSelectionCounts was not found.");

        bool pipingWithoutSupports = (bool)method.Invoke(
            null,
            new object[] { 1, 0 });
        bool supportsWithoutPiping = (bool)method.Invoke(
            null,
            new object[] { 0, 1 });

        AssertEqual(
            true,
            pipingWithoutSupports,
            "module selection with piping and no supports");
        AssertEqual(
            false,
            supportsWithoutPiping,
            "module selection with supports and no piping");
    }

    private static void LabelReportExcelSheetStartsWithSimpleHeaderAndRepeatsProjectDetails()
    {
        var data = new List<LabelReport.LabelData>
        {
            new LabelReport.LabelData
            {
                ProjectNumber = "10889-10890",
                ProjectName = "NEXTDC S4",
                ProjectPhase = "BATCH 628 - CHWBP",
                SpoolNumber = "S4-ROOF-PR-HEX-01-CHWBP-087",
                MarkItem = "297",
                MaterialGrade = "STAINLESS STEEL - SCH10 - 316 L - ERW",
                PipeSize = "125",
                PipeEndPrep = "BE-PE",
                PipeLength = "1060mm"
            }
        };

        MethodInfo method = typeof(LabelReport).GetMethod(
            "BuildLabelExcelSheet",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildLabelExcelSheet was not found.");

        var sheet = (ExcelReportExporter.ExcelWorksheet)method.Invoke(
            null,
            new object[]
            {
                new ProcurementConfig(),
                data,
                "NOTE: This report does not include site-measured spools and branches",
                data[0].ProjectPhase,
                "Package A",
                false
            });

        AssertEqual("Package A", sheet.Name, "worksheet name");
        AssertEqual(3, sheet.Rows.Count, "row count");
        AssertEqual(ExcelReportExporter.RowKind.Note, sheet.Rows[2].Kind, "site-measure note row kind");
        AssertSequence(new[] { "NOTE: This report does not include site-measured spools and branches" },
            sheet.Rows[2].Values, "site-measure note");
        AssertEqual(ExcelReportExporter.RowKind.Header, sheet.Rows[0].Kind, "first row kind");
        AssertSequence(ExpectedHeaders, sheet.Rows[0].Values, "headers");
        AssertSequence(
            new object[]
            {
                "10889-10890",
                "NEXTDC S4",
                "BATCH 628 - CHWBP",
                "S4-ROOF-PR-HEX-01-CHWBP-087",
                "297",
                "STAINLESS STEEL - SCH10 - 316 L - ERW",
                "125",
                "BE-PE",
                "1060mm",
                "https://www.brownmoodie.com.au"
            },
            sheet.Rows[1].Values,
            "data row");
    }

    private static void AssertSequence(IEnumerable<object> expected, IEnumerable<object> actual, string label)
    {
        string[] expectedValues = expected.Select(value => value?.ToString() ?? "").ToArray();
        string[] actualValues = actual.Select(value => value?.ToString() ?? "").ToArray();
        if (!expectedValues.SequenceEqual(actualValues))
        {
            throw new InvalidOperationException(
                $"Unexpected {label}. Expected [{string.Join(" | ", expectedValues)}], " +
                $"actual [{string.Join(" | ", actualValues)}].");
        }
    }

    private static void AssertEqual<T>(T expected, T actual, string label)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Unexpected {label}. Expected '{expected}', actual '{actual}'.");
    }

    private static void AssertThrows<TException>(Action action, string label)
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
            $"Expected {typeof(TException).Name} for {label}.");
    }
}
