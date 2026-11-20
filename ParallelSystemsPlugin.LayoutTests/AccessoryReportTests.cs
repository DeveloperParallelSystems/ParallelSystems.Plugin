using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class AccessoryReportTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Report = typeof(BomAccessoryReport);

    public static void Run()
    {
        AssertDescription("PS-FM122R1", "System default", "System default", "PS-FM122R1 - System default");
        AssertDescription("Valve", "Valve", "Butterfly valve with gear operator", "Valve | Butterfly valve with gear operator");
        AssertDescription(" Valve ", " Standard ", "High pressure", "Valve - Standard | High pressure");
        AssertDescription("Valve", "STANDARD", "valve - standard", "Valve - STANDARD");
        AssertDescription(null, "Named accessory", null, "Named accessory");
        AssertDescription("Named family", null, "", "Named family");
        AssertDescription(null, null, "Procurement description", "Procurement description");
        AssertDescription(null, null, null, "");
        AssertDescription(null, null, "Duplex - SCH80 - Model spec", "Duplex - SCH80 - Model spec");
        foreach (bool groupByPackage in new[] { false, true })
        foreach (bool packageWorksheets in new[] { false, true })
            AssertWorkbook(groupByPackage, packageWorksheets);
        Console.WriteLine("PASS: Accessory descriptions retain family/type identity and workbook rows group by description, then numeric size.");
    }

    private static string Description(string family, string type, string description)
    {
        MethodInfo build = Report.GetMethod("BuildDescription", PrivateStatic)
            ?? throw new InvalidOperationException("Accessory descriptions do not yet have family/type formatting.");
        return (string)build.Invoke(null, new object[] { family, type, description });
    }

    private static void AssertDescription(string family, string type, string description, string expected)
    {
        string actual = Description(family, type, description);
        Check(actual == expected, $"description expected '{expected}', actual '{actual}'");
    }

    private static void AssertWorkbook(bool groupByPackage, bool packageWorksheets)
    {
        Type rowType = Report.GetNestedType("AccessoryRow", BindingFlags.NonPublic);
        var items = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
        void Add(string package, string size, string family, string type)
        {
            object row = Activator.CreateInstance(rowType, nonPublic: true);
            rowType.GetProperty("PackageName").SetValue(row, package);
            rowType.GetProperty("Size").SetValue(row, size);
            rowType.GetProperty("Description").SetValue(row, Description(family, type, ""));
            items.Add(row);
        }

        // Unsorted input includes equal type names from different families and packages.
        Add("Package B", "300", "PS-FM122R1", "System default");
        Add("Package A", "1000", "Valve", "Large");
        Add("Package A", "250", "Z-VALVE", "Standard");
        Add("Package A", "80", "Valve", "Small");
        Add("Package A", "250", "PS-FM122R1", "System default");
        Add("Package B", "250", "PS-FM122R1", "System default");
        Add("Package A", "250", "PS-OTHER", "System default");
        Add("Package B", "1000", "PS-FM122R1", "System default");
        Add("Package B", "80", "PS-FM122R1", "System default");

        string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems-Accessory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cfg = new ProcurementConfig
            {
                TargetFolder = folder, GroupByPackage = groupByPackage,
                CreatePackageWorksheets = packageWorksheets
            };
            object master = Report.GetMethod("Summarize", PrivateStatic).Invoke(null, new object[] { items, groupByPackage });
            Report.GetMethod("ExportExcel", PrivateStatic).Invoke(null, new[] { cfg, master, items });
            using var zip = ZipFile.OpenRead(Path.Combine(folder, "BOM-ACCESSORY REPORT.xlsx"));
            XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            XDocument Read(string path)
            {
                using var stream = zip.GetEntry(path).Open();
                return XDocument.Load(stream);
            }
            string[] names = Read("xl/workbook.xml").Descendants(ns + "sheet")
                .Select(sheet => (string)sheet.Attribute("name")).ToArray();
            Check(names.SequenceEqual(packageWorksheets
                ? new[] { "ACCESSORY REPORT", "Package A", "Package B" }
                : new[] { "ACCESSORY REPORT" }), "optional package sheets");
            for (int sheetNumber = 1; sheetNumber <= names.Length; sheetNumber++)
            {
                var data = Read($"xl/worksheets/sheet{sheetNumber}.xml").Descendants(ns + "row")
                    .Select(row => row.Elements(ns + "c").ToArray())
                    .Where(cells => cells.Any(cell => cell.Element(ns + "v") != null))
                    .Select(cells => string.Join(" | ", cells.Take(cells.Length - 1).Select(cell => cell.Value))).ToArray();
                string[] packageA =
                {
                    "250 | PS-FM122R1 - System default | 1",
                    "250 | PS-OTHER - System default | 1",
                    "1000 | Valve - Large | 1",
                    "80 | Valve - Small | 1",
                    "250 | Z-VALVE - Standard | 1"
                };
                string[] packageB =
                {
                    "80 | PS-FM122R1 - System default | 1",
                    "250 | PS-FM122R1 - System default | 1",
                    "300 | PS-FM122R1 - System default | 1",
                    "1000 | PS-FM122R1 - System default | 1"
                };
                string[] expected = sheetNumber == 2 ? packageA : sheetNumber == 3 ? packageB
                    : groupByPackage ? packageA.Select(row => "Package A | " + row)
                        .Concat(packageB.Select(row => "Package B | " + row)).ToArray()
                    : new[]
                    {
                        "80 | PS-FM122R1 - System default | 1",
                        "250 | PS-FM122R1 - System default | 2",
                        "300 | PS-FM122R1 - System default | 1",
                        "1000 | PS-FM122R1 - System default | 1",
                        "250 | PS-OTHER - System default | 1",
                        "1000 | Valve - Large | 1",
                        "80 | Valve - Small | 1",
                        "250 | Z-VALVE - Standard | 1"
                    };
                Check(data.SequenceEqual(expected), "description/size ordering and counts in " + names[sheetNumber - 1]
                    + ". Actual: " + string.Join("; ", data));
            }
        }
        finally
        {
            // This uniquely named directory is created and owned by this test.
            Directory.Delete(folder, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Accessory report: " + message);
    }
}
