using System.Collections;
using System.Reflection;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class PipeDescriptionTests
{
    public static void Run()
    {
        foreach (bool groupByPackage in new[] { false, true })
        {
            AssertPipeSummaryPreservesModelText(groupByPackage);
            AssertCutListPreservesModelText(groupByPackage);
        }
        Console.WriteLine("PASS: Pipe and cut list outputs preserve custom model material/specification and end-prep text.");
    }

    private static readonly (string Material, string EndPrep)[] ModelValues =
    {
        ("CARBON STEEL - SCH80 - API 5L X65 - SEAMLESS", "BE-THREADED - MODEL PREP"),
        ("DUPLEX 2205 - SCH40S - ASTM A790", "GROOVED-PE - CUSTOM DEPTH"),
        ("CuNi 90/10 - EN 12449 - Model Grade Q", "SOCKET-BE / MODEL DETAIL"),
        ("MODEL PIPE - NO END PREP SPECIFIED", "")
    };

    // Replacing model material/specification with a default description must fail at the exported row.
    private static void AssertPipeSummaryPreservesModelText(bool groupByPackage)
    {
        Type report = typeof(BomPipeReport);
        IList pieces = CreatePieces(report);
        object summaries = Method(report, "BuildPipeSummaries").Invoke(null, new object[]
        {
            pieces, 6000d, 2d, 1000d, groupByPackage
        });
        var sheet = (ExcelReportExporter.ExcelWorksheet)Method(report, "BuildPipeExcelSheet").Invoke(null, new object[]
        {
            new ProcurementConfig { GroupByPackage = groupByPackage }, summaries,
            "", "6m", null, groupByPackage
        });
        var rows = DataRows(sheet).ToArray();
        Check(rows.Length == ModelValues.Length, "Pipe summary must keep distinct model specifications separate.");
        int descriptionColumn = groupByPackage ? 2 : 1;
        foreach (var value in ModelValues)
            Check(rows.Count(row => Equals(row.Values[descriptionColumn], value.Material)) == 1,
                "Pipe summary must retain model description: " + value.Material);
    }

    // Replacing a material or an unusual end prep must fail in the detailed cut-list row.
    private static void AssertCutListPreservesModelText(bool groupByPackage)
    {
        Type report = typeof(BomCutListReport);
        IList pieces = CreatePieces(report);
        var sheet = (ExcelReportExporter.ExcelWorksheet)Method(report, "BuildCutListExcelSheet").Invoke(null, new object[]
        {
            new ProcurementConfig { GroupByPackage = groupByPackage }, pieces,
            6000d, 2d, 0d, "", "TOTAL WASTE: 0 mm (0.0%)", null
        });
        var rows = DataRows(sheet).ToArray();
        Check(rows.Length == ModelValues.Length, "Cut list must retain every custom model specification.");
        foreach (var value in ModelValues)
            Check(rows.Count(row => Equals(row.Values[3], value.Material)
                && Equals(row.Values[4], value.EndPrep)) == 1,
                "Cut list must retain model material and end prep: " + value.Material + " / " + value.EndPrep);
    }

    private static IList CreatePieces(Type report)
    {
        Type pieceType = report.GetNestedType("PipePiece", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PipePiece was not found.");
        var pieces = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pieceType));
        for (int i = 0; i < ModelValues.Length; i++)
        {
            object piece = Activator.CreateInstance(pieceType, nonPublic: true);
            void Set(string name, object value) => pieceType.GetProperty(name).SetValue(piece, value);
            Set("Package", "Package " + i);
            Set("AssemblyName", "Assembly " + i);
            Set("Material", ModelValues[i].Material);
            Set("PipeEndPrep", ModelValues[i].EndPrep);
            Set("SizeText", "150");
            Set("SizeSort", 150d);
            Set("RawLengthMm", 6000d);
            Set("AdjustedLengthMm", 6000d);
            pieces.Add(piece);
        }
        return pieces;
    }

    private static IEnumerable<ExcelReportExporter.ExcelRow> DataRows(ExcelReportExporter.ExcelWorksheet sheet)
        => sheet.Rows.Where(row => row.Kind == ExcelReportExporter.RowKind.Data
            || row.Kind == ExcelReportExporter.RowKind.AlternateData);

    private static MethodInfo Method(Type report, string name)
        => report.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException(name + " was not found.");

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
