using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class CutListReportTests
{
    public static void Run()
    {
        foreach (bool createPackageWorksheets in new[] { false, true })
        {
            foreach (bool groupByPackage in new[] { false, true })
            {
                AssertDetailedSheet(createPackageWorksheets, multiplePackages: true, groupByPackage);
                AssertDetailedSheet(createPackageWorksheets, multiplePackages: false, groupByPackage);
            }
        }

        AssertDetailedSheet(true, multiplePackages: false, groupByPackage: false, worksheetName: "Package A");
        Console.WriteLine("PASS: Main cut list honors GROUP SUMMARY BY PACKAGE independently of optional package worksheets.");
        Console.WriteLine("PASS: Individual package cut lists retain their detailed layout.");
    }

    private static void AssertDetailedSheet(
        bool createPackageWorksheets,
        bool multiplePackages,
        bool groupByPackage,
        string worksheetName = null)
    {
        Type reportType = typeof(BomCutListReport);
        Type pieceType = reportType.GetNestedType("PipePiece", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("PipePiece was not found.");
        var pieces = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(pieceType));

        void AddPiece(string package, string assembly, double length)
        {
            object piece = Activator.CreateInstance(pieceType, nonPublic: true);
            pieceType.GetProperty("Package").SetValue(piece, package);
            pieceType.GetProperty("AssemblyName").SetValue(piece, assembly);
            pieceType.GetProperty("Material").SetValue(piece, "Stainless steel");
            pieceType.GetProperty("SizeText").SetValue(piece, "200");
            pieceType.GetProperty("SizeSort").SetValue(piece, 200d);
            pieceType.GetProperty("PipeEndPrep").SetValue(piece, "PE-PE");
            pieceType.GetProperty("AdjustedLengthMm").SetValue(piece, length);
            pieces.Add(piece);
        }

        // Deliberately unsorted: output must follow stock-pipe packing within each package.
        if (multiplePackages)
        {
            AddPiece("Package B", "B-cut", 900);
            AddPiece("Package B", "B-full", 6000);
        }
        AddPiece("Package A", "A-short", 1000);
        AddPiece("Package A", "Z-long", 4000);

        string totalWaste = multiplePackages
            ? groupByPackage ? "TOTAL WASTE: 6098 mm (33.9%)" : "TOTAL WASTE: 96 mm (0.8%)"
            : "TOTAL WASTE: 998 mm (16.6%)";
        MethodInfo totalMethod = reportType.GetMethod("BuildTotalWasteText", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildTotalWasteText was not found.");
        string actualTotalWaste = (string)totalMethod.Invoke(null, new object[]
        {
            pieces, 6000d, 2d, groupByPackage || worksheetName != null
        });
        if (actualTotalWaste != totalWaste)
            throw new InvalidOperationException($"Cut list total does not match its packing mode. Expected {totalWaste}, actual {actualTotalWaste}.");
        const string note = "NOTE: This report does not include site-measured spools and branches";
        MethodInfo method = reportType.GetMethod("BuildCutListExcelSheet", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("BuildCutListExcelSheet was not found.");
        var sheet = (ExcelReportExporter.ExcelWorksheet)method.Invoke(null, new object[]
        {
            new ProcurementConfig { CreatePackageWorksheets = createPackageWorksheets, GroupByPackage = groupByPackage },
            pieces, 6000d, 2d, 0d, note, actualTotalWaste, worksheetName
        });

        string scenario = $"multiple packages={multiplePackages}, group by package={groupByPackage}, extra worksheets={createPackageWorksheets}, sheet={worksheetName ?? "main"}";
        var expectedRows = new List<string>
        {
            "Package: Package A | Material: Stainless steel | Size: 200 | Stock Length: 6000 | Blade: 2 | Negative Allowance: 0",
            "Package | Assembly Name | Size | Material | End Prep | Cut Length",
            "Pipe Id: 0",
            "Package A | Z-long | 200 | Stainless steel | PE-PE | 4000",
            "Package A | A-short | 200 | Stainless steel | PE-PE | 1000",
            "Waste: 998mm (17%)"
        };
        if (multiplePackages)
        {
            expectedRows.AddRange(new[]
            {
                "Package: Package B | Material: Stainless steel | Size: 200 | Stock Length: 6000 | Blade: 2 | Negative Allowance: 0",
                "Pipe Id: 0",
                "Package B | B-full | 200 | Stainless steel | PE-PE | 6000",
                "Waste: 0mm (0%)",
                "Pipe Id: 1",
                "Package B | B-cut | 200 | Stainless steel | PE-PE | 900",
                "Waste: 5100mm (85%)"
            });
        }
        if (multiplePackages && !groupByPackage)
        {
            expectedRows = new List<string>
            {
                "Package: ALL PACKAGES | Material: Stainless steel | Size: 200 | Stock Length: 6000 | Blade: 2 | Negative Allowance: 0",
                "Package | Assembly Name | Size | Material | End Prep | Cut Length",
                "Pipe Id: 0",
                "Package B | B-full | 200 | Stainless steel | PE-PE | 6000",
                "Waste: 0mm (0%)",
                "Pipe Id: 1",
                "Package A | Z-long | 200 | Stainless steel | PE-PE | 4000",
                "Package A | A-short | 200 | Stainless steel | PE-PE | 1000",
                "Package B | B-cut | 200 | Stainless steel | PE-PE | 900",
                "Waste: 96mm (2%)"
            };
        }
        expectedRows.Add(totalWaste);
        expectedRows.Add(note);

        string[] actualRows = sheet.Rows
            .Where(row => row.Kind != ExcelReportExporter.RowKind.Title
                && row.Kind != ExcelReportExporter.RowKind.Metadata
                && row.Kind != ExcelReportExporter.RowKind.Blank)
            .Select(row => string.Join(" | ", row.Values))
            .ToArray();
        if (!expectedRows.SequenceEqual(actualRows))
            throw new InvalidOperationException($"Cut list lost its detailed cutting layout ({scenario}). Expected:\n{string.Join("\n", expectedRows)}\nActual:\n{string.Join("\n", actualRows)}");

        if (sheet.Name != (worksheetName ?? "BOM-CUT LIST Details"))
            throw new InvalidOperationException($"Unexpected cut list worksheet name ({scenario}): {sheet.Name}");

        if (worksheetName == null)
            AssertExcelFileExportsWithPdfConflict(pieces, createPackageWorksheets, groupByPackage, multiplePackages, totalWaste);
    }

    private static void AssertExcelFileExportsWithPdfConflict(
        IList pieces,
        bool createPackageWorksheets,
        bool groupByPackage,
        bool multiplePackages,
        string expectedTotal)
    {
        string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems-CutList-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var config = new ProcurementConfig
            {
                ExportReportsToExcel = true,
                CreatePackageWorksheets = createPackageWorksheets,
                GroupByPackage = groupByPackage,
                TargetFolder = folder
            };
            var export = typeof(BomCutListReport).GetMethod("ExportReport", BindingFlags.NonPublic | BindingFlags.Static)
                ?? throw new InvalidOperationException("ExportReport was not found.");
            export.Invoke(null, new object[] { config, pieces, 6000d, 2d, 0d, "", "cut-list-regression" });
            PdfConflictTests.AssertPdfUntouched();

            using var zip = ZipFile.OpenRead(Path.Combine(folder, "cut-list-regression.xlsx"));
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            using var workbookStream = zip.GetEntry("xl/workbook.xml").Open();
            var workbook = XDocument.Load(workbookStream);
            string[] actualNames = workbook.Descendants(ns + "sheet").Select(s => (string)s.Attribute("name")).ToArray();
            string[] expectedNames = createPackageWorksheets && multiplePackages
                ? new[] { "BOM-CUT LIST Details", "Package A", "Package B" }
                : new[] { "BOM-CUT LIST Details" };
            if (!expectedNames.SequenceEqual(actualNames))
                throw new InvalidOperationException("Excel export did not honor the optional package worksheet setting.");

            using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml").Open();
            string[] text = XDocument.Load(sheetStream).Descendants(ns + "t").Select(t => t.Value).ToArray();
            int expectedPipes = multiplePackages ? groupByPackage ? 3 : 2 : 1;
            if (text.Count(t => t.StartsWith("Pipe Id:")) != expectedPipes
                || text.Count(t => t.StartsWith("Waste:")) != expectedPipes
                || !text.Contains(expectedTotal))
                throw new InvalidOperationException("Exported Excel file lost its pipe IDs, individual waste or correct total.");
        }
        finally
        {
            // This unique directory is created and owned by this test.
            Directory.Delete(folder, recursive: true);
        }
    }
}
