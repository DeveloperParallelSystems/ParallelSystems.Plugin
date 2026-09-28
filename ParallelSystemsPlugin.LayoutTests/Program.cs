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

    private static int Main()
    {
        try
        {
            LabelReportExcelSheetStartsWithSimpleHeaderAndRepeatsProjectDetails();
            ModuleStepAllowsPipingWithoutSupports();
            SlipOnSetbackUsesDynamicNominalDiameterDefaults();
            SlipOnSetbackUsesConfiguredBandValues();
            AssemblyIdentityMarkerNamesAreSafeAndUnique();
            Console.WriteLine("PASS: Label report Excel sheet uses the simple row-based layout.");
            Console.WriteLine("PASS: Module STEP allows piping without support components.");
            Console.WriteLine("PASS: Slip-on setback resolves dynamically from nominal diameter.");
            Console.WriteLine("PASS: Assembly identity marker names are safe and unique.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("FAIL: " + exception.Message);
            return 1;
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
                "NOTE: should not be printed in the simple layout",
                data[0].ProjectPhase,
                "Package A",
                true
            });

        AssertEqual("Package A", sheet.Name, "worksheet name");
        AssertEqual(2, sheet.Rows.Count, "row count");
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
}
