using System.Reflection;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class LabelDescriptionTests
{
    public static void Run()
    {
        MethodInfo normalize = typeof(LabelReport).GetMethod(
            "NormalizeMaterialGrade", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("Label material formatter was not found.");

        // Changing a branch's model grade into a standard stainless specification
        // must fail these cases, including existing SCH10 text with a different end prep.
        foreach (string modelValue in new[]
        {
            "SHAPED BRANCH - Duplex 2205 SCH40 (B-E)",
            "BRANCH-SS SCH10 (B-E)",
            "Carbon steel ASTM A106 Grade B",
            "BRANCH-SS SCH10 (C-E)"
        })
        {
            string grade = (string)normalize.Invoke(null, new object[] { modelValue });
            Check(grade == modelValue, "model material grade was replaced: " + modelValue + " -> " + grade);

            var labels = new List<LabelReport.LabelData>
            {
                new LabelReport.LabelData
                {
                    MarkItem = "MODEL-MARK-17",
                    MaterialGrade = grade,
                    PipeEndPrep = "MODEL-PREP (B-E / P-E)"
                }
            };
            MethodInfo build = typeof(LabelReport).GetMethod(
                "BuildLabelExcelSheet", BindingFlags.NonPublic | BindingFlags.Static);
            var sheet = (ExcelReportExporter.ExcelWorksheet)build.Invoke(null, new object[]
            {
                new ProcurementConfig(), labels, "", "", null, false
            });
            Check(sheet.Rows[1].Values[4]?.ToString() == "MODEL-MARK-17", "model mark in Excel");
            Check(sheet.Rows[1].Values[5]?.ToString() == modelValue, "model material grade in Excel");
            Check(sheet.Rows[1].Values[7]?.ToString() == "MODEL-PREP (B-E / P-E)", "model end prep in Excel");
        }

        Check((string)normalize.Invoke(null, new object[] { null }) == "", "missing grade stays empty");
        Console.WriteLine("PASS: Labels preserve model material grades, marks and end preparations.");
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Label description: " + label);
    }
}
