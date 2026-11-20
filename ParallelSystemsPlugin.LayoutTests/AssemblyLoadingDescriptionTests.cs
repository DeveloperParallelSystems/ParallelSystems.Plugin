using System.Collections;
using System.Reflection;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class AssemblyLoadingDescriptionTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

    public static void Run()
    {
        foreach (bool grouped in new[] { false, true })
        foreach (string material in new[] { "Duplex 2205 - SCH40S - ASTM A790", "Copper nickel - EN12449", "" })
        {
            var cfg = new ProcurementConfig { GroupByPackage = grouped };
            Type register = typeof(BomAssemblyRegisterReport);
            object registerRows = Rows(register, "ReportRow", "Package", "AssemblyNumber", material);
            var registerSheet = (ExcelReportExporter.ExcelWorksheet)register.GetMethod("BuildAssemblyRegisterExcelSheet", PrivateStatic)
                .Invoke(null, new object[] { cfg, registerRows, false, "", null });
            Check(registerSheet, 1, 3, material, "Assembly Register");

            Type loading = typeof(BomLoadingReport);
            object loadingRows = Rows(loading, "Row", "PackageName", "DrawingNumber", material);
            object result = loading.GetMethod("BuildLoadingExcelSheet", PrivateStatic)
                .Invoke(null, new object[] { cfg, loadingRows, null });
            var loadingSheet = (ExcelReportExporter.ExcelWorksheet)result.GetType().GetProperty("Sheet").GetValue(result);
            Check(loadingSheet, 1, 2, material, "Loading Report");
        }
        Console.WriteLine("PASS: Assembly Register and Loading sheets preserve full model material specifications and missing values.");
    }

    private static object Rows(Type report, string rowName, string packageProperty, string drawingProperty, string material)
    {
        Type rowType = report.GetNestedType(rowName, BindingFlags.NonPublic);
        var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
        object row = Activator.CreateInstance(rowType, nonPublic: true);
        rowType.GetProperty(packageProperty).SetValue(row, "Model package");
        rowType.GetProperty(drawingProperty).SetValue(row, "MODEL-DRAWING-17");
        rowType.GetProperty("MaterialGrade").SetValue(row, material);
        rowType.GetProperty("Qty").SetValue(row, 1);
        rows.Add(row);
        return rows;
    }

    private static void Check(ExcelReportExporter.ExcelWorksheet sheet, int drawingIndex, int materialIndex, string expected, string report)
    {
        var row = sheet.Rows.Single(row => row.Values.Count > materialIndex && row.Values[drawingIndex]?.ToString() == "MODEL-DRAWING-17");
        if (row.Values[materialIndex]?.ToString() != expected)
            throw new InvalidOperationException(report + " replaced the model material: " + expected);
    }
}
