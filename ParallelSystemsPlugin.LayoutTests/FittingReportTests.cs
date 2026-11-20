using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class FittingReportTests
{
    private static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly (string Category, string Colour)[] Categories =
    {
        ("CUSTOM FITTING", "FFF4B183"),
        ("ELBOW", "FFFF0000"), ("END CAP", "FFADADAD"), ("FLANGE", "FFFFFF00"),
        ("REDUCER", "FF45D456"), ("SOCKET", "FFF7C7AC"), ("TEE", "FF95DBF8"),
        ("WELD", "FFE49FDD"), ("OTHER", "FFD9EAD3")
    };

    public static void Run()
    {
        AssertElbowDescriptions();
        AssertAllDescriptionsUseModelValues();
        AssertPlaceholderFiltering();
        var material = typeof(BomFittingReport).GetMethod("NormalizeMaterialGrade", BindingFlags.NonPublic | BindingFlags.Static);
        Check((string)material.Invoke(null, new object[] { "DUPLEX - SCH80 - Model grade" }) == "DUPLEX - SCH80 - Model grade", "retain full model material specification");
        foreach (bool groupByPackage in new[] { false, true })
        foreach (bool includeWeld in new[] { false, true })
        {
            AssertWorkbook(groupByPackage, includeWeld);
        }
        Console.WriteLine("PASS: Fitting workbooks use four columns, category colours and the weld toggle on all worksheets.");
    }

    private static void AssertElbowDescriptions()
    {
        var build = typeof(BomFittingReport).GetMethod("BuildReportDescription", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var item in new[]
        {
            ("LR ELBOW-BW-SCH10-316/L-SS", "LR Elbow-SS - SCH10 - STAINLESS-316-LR ELBOW-BW-SCH10", "LR ELBOW-BW-SCH10-316/L-SS"),
            ("LR ELBOW - BW - 45DEG - SCH40 - CS", "LR Elbow-CS", "LR ELBOW - BW - 45DEG - SCH40 - CS"),
            ("VICTAULIC SS ELBOW", "Victaulic Elbow", "VICTAULIC SS ELBOW"),
            ("", "LR Elbow-SS - SCH10", "LR Elbow-SS - SCH10"),
            ("", "LR Elbow-CS - SCH40", "LR Elbow-CS - SCH40")
        })
        {
            string actual = (string)build.Invoke(null, new object[] { item.Item1, item.Item2 });
            Check(actual == item.Item3, "elbow must retain model description/material. Expected: " + item.Item3 + "; actual: " + actual);
        }
        Console.WriteLine("PASS: Elbow descriptions preserve SS/CS material and schedule from the model without synthesized replacements.");
    }

    private static void AssertAllDescriptionsUseModelValues()
    {
        var build = typeof(BomFittingReport).GetMethod("BuildReportDescription", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (string description in new[]
        {
            "Actual titanium fitting - SCH80 - Model XYZ",
            "FLANGE FILLET WELD EXTERNAL - MODEL SPECIFICATION",
            "FLANGE FILLET WELD INTERNAL - MODEL SPECIFICATION",
            "STAINLESS-0MM-WELD GAP - ACTUAL CLASS",
            "BSP THREADED COUPLING150LB-SS",
            "DN50 FERRULES - SCH10 - SS",
            "050DN"
        })
        {
            string actual = (string)build.Invoke(null, new object[] { description, "Family - Type" });
            Check(actual == description, "must preserve actual description: " + description + "; actual: " + actual);
        }
        foreach (string missing in new string[] { null, "", "  " })
            Check((string)build.Invoke(null, new object[] { missing, "Model Family - Model Type" }) == "Model Family - Model Type", "missing descriptions use model identity");
        Check((string)build.Invoke(null, new object[] { null, null }) == "", "no fabricated fallback description");
        Console.WriteLine("PASS: All fitting categories retain actual model descriptions, including custom fittings, tees and welds.");
    }

    private static void AssertPlaceholderFiltering()
    {
        var skip = typeof(BomFittingReport).GetMethod("ShouldSkipFitting", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var item in new[]
        {
            ("Model socket", "Standard", "SOCKET", "", true),
            ("Model fitting", "Actual type", "OTHER", "", false),
            ("Model custom", "Standard", "CUSTOM FITTING", "", false),
            ("Model elbow", "Standard", "ELBOW", "", false),
            ("VICTAULIC Elbow", "Standard", "ELBOW", "", true),
            ("Flange external weld", "Standard", "WELD", "", false),
            ("Model weld", "Standard", "WELD", "", true),
            ("Model tee", "Standard", "TEE", "Actual equal tee BOM description", false),
            ("Model tee", "Actual type", "TEE", "Standard", true),
            ("Tri-Clamp model", "Actual type", "CUSTOM FITTING", "", true),
            ("Model ferrule placeholder", "050DN", "CUSTOM FITTING", "", true)
        })
        {
            bool actual = (bool)skip.Invoke(null, new object[] { item.Item1, item.Item2, item.Item3, item.Item4 });
            Check(actual == item.Item5, "existing placeholder filtering: " + item.Item1);
        }
        Console.WriteLine("PASS: Fitting placeholder filtering uses its established classification values independently of display descriptions.");
    }

    private static void AssertWorkbook(bool groupByPackage, bool includeWeld)
    {
        Type report = typeof(BomFittingReport);
        var mapToBucket = report.GetMethod("MapToBucket", BindingFlags.NonPublic | BindingFlags.Static);
        string unidentifiedCategory = (string)mapToBucket.Invoke(null, new object[] { "UNIDENTIFIED FITTING XYZ" });
        Check(unidentifiedCategory == "OTHER", "unidentified fittings use the OTHER category");
        Type rowType = report.GetNestedType("FittingRow", BindingFlags.NonPublic);
        var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
        foreach (string package in new[] { "Package A", "Package B" })
        foreach (var category in Categories)
        {
            // Duplicate rows must aggregate into one quantity, without repeating the category label.
            for (int i = 0; i < 2; i++)
            {
                object row = Activator.CreateInstance(rowType, nonPublic: true);
                rowType.GetProperty("PackageName").SetValue(row, package);
                rowType.GetProperty("TypeBucket").SetValue(row,
                    category.Category == "OTHER" ? unidentifiedCategory : category.Category);
                rowType.GetProperty("SizeText").SetValue(row, "152-152-152");
                rowType.GetProperty("SizeSort").SetValue(row, 152d);
                string description = (string)report.GetMethod("BuildReportDescription", BindingFlags.NonPublic | BindingFlags.Static)
                    .Invoke(null, new object[] { category.Category + " DESCRIPTION", "Family - Type" });
                rowType.GetProperty("Description").SetValue(row, description);
                rows.Add(row);
            }
        }

        var packages = rows.Cast<object>()
            .GroupBy(row => (string)rowType.GetProperty("PackageName").GetValue(row))
            .Select(group => ToTypedGroup(group.Key, group, rowType)).ToArray();
        Type groupType = typeof(IGrouping<,>).MakeGenericType(typeof(string), rowType);
        var typedPackages = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(groupType));
        foreach (object package in packages) typedPackages.Add(package);

        string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems-Fitting-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var cfg = new ProcurementConfig
            {
                TargetFolder = folder, GroupByPackage = groupByPackage,
                CreatePackageWorksheets = true, IncludeWeld = includeWeld,
                JobNumber = "GBR02590", JobName = "OS1", Date = new DateTime(2026, 11, 12)
            };
            object sections = report.GetMethod("PrepareSections", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { typedPackages, groupByPackage });
            report.GetMethod("ExportExcel", BindingFlags.NonPublic | BindingFlags.Static)
                .Invoke(null, new object[] { cfg, typedPackages, sections });

            using var zip = ZipFile.OpenRead(Path.Combine(folder, "BOM-FITTING REPORT.xlsx"));
            XDocument Read(string path)
            {
                using var stream = zip.GetEntry(path).Open();
                return XDocument.Load(stream);
            }
            string[] names = Read("xl/workbook.xml").Descendants(Ns + "sheet")
                .Select(sheet => (string)sheet.Attribute("name")).ToArray();
            Check(names.SequenceEqual(new[] { "BOM Fitting Report", "Package A", "Package B" }), "package worksheets");
            XElement styles = Read("xl/styles.xml").Root;
            for (int sheetNumber = 1; sheetNumber <= 3; sheetNumber++)
            {
                XDocument sheet = Read($"xl/worksheets/sheet{sheetNumber}.xml");
                XElement[] sheetRows = sheet.Descendants(Ns + "row").ToArray();
                XElement header = sheetRows.Single(row => row.Descendants(Ns + "t").Any(t => t.Value == "Category"));
                Check(header.Descendants(Ns + "t").Select(t => t.Value)
                    .SequenceEqual(new[] { "Category", "Qty", "Size", "Description" }), "four-column fitting header");
                Check(sheet.Descendants(Ns + "col").Count() == 4, "no extra package column");
                foreach (var category in Categories)
                {
                    XElement[] bands = sheetRows.Where(row => row.Descendants(Ns + "t")
                        .Any(t => t.Value == category.Category)).ToArray();
                    int expectedBands = category.Category == "WELD" && !includeWeld ? 0
                        : sheetNumber == 1 && groupByPackage ? 2 : 1;
                    Check(bands.Length == expectedBands, category.Category + " band count");
                    foreach (XElement band in bands)
                    {
                        XElement cell = band.Elements(Ns + "c").First();
                        Check(cell.Descendants(Ns + "t").Single().Value == category.Category, "category is in column A");
                        int styleIndex = (int)cell.Attribute("s");
                        XElement style = styles.Element(Ns + "cellXfs").Elements().ElementAt(styleIndex);
                        int fillId = (int)style.Attribute("fillId");
                        string colour = (string)styles.Element(Ns + "fills").Elements().ElementAt(fillId)
                            .Descendants(Ns + "fgColor").Single().Attribute("rgb");
                        Check(colour == category.Colour, category.Category + " fill colour");
                        foreach (XElement other in band.Elements(Ns + "c").Skip(1))
                        {
                            XElement otherStyle = styles.Element(Ns + "cellXfs").Elements().ElementAt((int)other.Attribute("s"));
                            Check((int)otherStyle.Attribute("fillId") == 0, "category colour is limited to column A");
                        }
                    }
                }
                XElement[] data = sheetRows.Where(row => row.Descendants(Ns + "t")
                    .Any(t => t.Value.EndsWith(" DESCRIPTION", StringComparison.Ordinal))).ToArray();
                int categoryCount = includeWeld ? 9 : 8;
                Check(data.Length == categoryCount * (sheetNumber == 1 && groupByPackage ? 2 : 1), "fitting data count");
                foreach (XElement row in data)
                {
                    XElement[] cells = row.Elements(Ns + "c").ToArray();
                    Check(cells.Length == 4, "four data cells");
                    Check(cells[0].Value == "", "category label is not repeated on detail rows");
                    Check(cells[1].Element(Ns + "v")?.Value == (sheetNumber == 1 && !groupByPackage ? "4" : "2"), "numeric quantity in column B");
                    Check(cells[2].Descendants(Ns + "t").Single().Value == "152-152-152", "size text in column C");
                    if (!includeWeld) Check(!cells[3].Value.Contains("WELD"), "no weld detail when disabled");
                }
                bool expectPackages = groupByPackage || sheetNumber > 1;
                Check(sheet.Descendants(Ns + "t").Any(t => t.Value == "Package A") == (expectPackages && sheetNumber != 3), "package grouping is retained");
            }
        }
        finally
        {
            // This uniquely named test directory is created and owned by this test.
            Directory.Delete(folder, recursive: true);
        }
    }

    private static object ToTypedGroup(string package, IEnumerable<object> rows, Type rowType)
    {
        return typeof(FittingReportTests).GetMethod(nameof(GroupRows), BindingFlags.NonPublic | BindingFlags.Static)
            .MakeGenericMethod(rowType).Invoke(null, new object[] { package, rows });
    }

    private static IGrouping<string, T> GroupRows<T>(string package, IEnumerable<object> rows)
    {
        return rows.Cast<T>().GroupBy(_ => package).Single();
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException("Fitting workbook: " + label);
    }
}
