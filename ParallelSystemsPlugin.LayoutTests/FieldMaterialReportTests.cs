using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class FieldMaterialReportTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Report = typeof(BomFieldMaterialReport);

    private static string Description(string family, string type, string detail)
    {
        var method = Report.GetMethod("BuildDescription", PrivateStatic)
            ?? throw new InvalidOperationException("Field Material description formatter was not found.");
        return (string)method.Invoke(null, new object[] { family, type, detail });
    }

    public static void Run()
    {
        string flex = Description("Vic-Coupling Flex 77-W77 (200 Above)", "Standard", "VICTAULIC GROOVED COUPLING");
        string rigid = Description("Vic-Coupling Rigid 07-W07 (200 Above)", "Standard", "VICTAULIC GROOVED COUPLING");
        Check(flex == "VIC FLEXIBLE COUPLING", "short Flex description");
        Check(rigid == "VIC RIGID COUPLING", "short Rigid description");
        Check(Description(null, null, "VIC QUICKVIC COUPLING STYLE 107V (RIGID)") == rigid, "Rigid model description");
        Check(Description(null, null, "VIC COUPLING STYLE 77 (FLEX)") == flex, "Flex model description");
        Check(Description("Valve family", "Standard", "VIC-300 MASTERSEAL BUTTERFLY VALVE w/GO") == "VIC-300 MASTERSEAL BUTTERFLY VALVE w/GO", "valve uses Description without family/type prefix");
        Check(Description("PS-Flow Meter", "7ME6580 DN300", "SITRANS FMS500 - SIEMENS") == "SITRANS FMS500 - SIEMENS", "flow meter uses Description without family/type prefix");
        Check(Description("Valve", "Valve", "valve") == "valve", "preserve Description casing");
        Check(Description(null, "Named type", "") == "", "missing Description stays blank");
        Check(Description("Vic-Coupling Flex", "Standard", "") == "", "do not invent a description when Description is blank");
        Check(Description("Other brand coupling", "Rigid", "RIGID GROOVED COUPLING") == "RIGID GROOVED COUPLING", "other brands retain Description");
        Check(Description("Vic-Coupling", "Standard", "VICTAULIC GROOVED COUPLING") == "VICTAULIC GROOVED COUPLING", "unknown coupling subtype retains Description");
        Check(Description("Vic-Coupling Rigid", "Flex", "VICTAULIC GROOVED COUPLING") == "VICTAULIC GROOVED COUPLING", "conflicting subtype markers retain Description");
        Check(Description(null, null, "Description") == "Description", "description fallback");
        Check(Description(null, null, null) == "", "missing model values stay blank");
        Check(Description(null, null, "Siemens Model FMS500 - Actual spec") == "Siemens Model FMS500 - Actual spec", "preserve model description text");

        foreach (bool grouped in new[] { false, true })
        foreach (bool packages in new[] { false, true })
        {
            Type rowType = Report.GetNestedType("Data", BindingFlags.NonPublic);
            var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
            void Add(string package, string size, string description, int quantity)
            {
                object row = Activator.CreateInstance(rowType, nonPublic: true);
                foreach (var value in new[] { ("PackageName", package), ("Category", "COUPLING"), ("Size", size), ("Description", description) })
                    rowType.GetProperty(value.Item1).SetValue(row, value.Item2);
                rowType.GetProperty("Quantity").SetValue(row, quantity);
                rows.Add(row);
            }
            Add("Package A", "250", rigid, 4);
            Add("Package A", "300", flex, 6);
            Add("Package A", "300", rigid, 3);
            Add("Package B", "300", flex, 2);
            string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems-FieldMaterial-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var cfg = new ProcurementConfig { TargetFolder = folder, GroupByPackage = grouped, CreatePackageWorksheets = packages };
                object summary = Report.GetMethod("Summarize", PrivateStatic).Invoke(null, new object[] { rows, grouped });
                Report.GetMethod("ExportExcel", PrivateStatic).Invoke(null, new[] { cfg, summary, rows, "" });
                using var zip = ZipFile.OpenRead(Path.Combine(folder, "BOM-FIELD MATERIAL.xlsx"));
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheets = zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet") && entry.FullName.EndsWith(".xml")).ToArray();
                Check(sheets.Length == (packages ? 3 : 1), "optional package worksheets");
                foreach (var entry in sheets)
                {
                    using var stream = entry.Open();
                    var data = XDocument.Load(stream).Descendants(ns + "row")
                        .Select(row => row.Elements(ns + "c").ToArray())
                        .Where(cells => cells.Any(cell => cell.Element(ns + "v") != null))
                        .Select(cells => (Description: cells[^2].Value, Quantity: (int)cells[^1].Element(ns + "v"))).ToArray();
                    Check(data.All(row => row.Description == flex || row.Description == rigid), "workbook uses short coupling descriptions");
                    Check(data.Where(row => row.Description == rigid).Sum(row => row.Quantity) == (entry.FullName.EndsWith("sheet3.xml") ? 0 : 7), "Rigid quantity stays separate");
                    Check(data.Where(row => row.Description == flex).Sum(row => row.Quantity) == (entry.FullName.EndsWith("sheet2.xml") ? 6 : entry.FullName.EndsWith("sheet3.xml") ? 2 : 8), "Flex quantity stays separate");
                }
            }
            finally { Directory.Delete(folder, recursive: true); }
        }
        Console.WriteLine("PASS: Field Material uses short Victaulic coupling names and model Description for other items, keeping Flex/Rigid quantities separate on all worksheets.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Field Material: " + message);
    }
}
