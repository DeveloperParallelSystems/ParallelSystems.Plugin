using System.Collections;
using System.IO.Compression;
using System.Reflection;
using System.Xml.Linq;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class AssemblyRegisterReportTests
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type Report = typeof(BomAssemblyRegisterReport);

    private static IList Rows(bool framed, bool empty = false)
    {
        Type rowType = Report.GetNestedType("ReportRow", BindingFlags.NonPublic);
        var rows = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(rowType));
        if (empty) return rows;
        foreach (var item in new[] { ("Package A", 2), ("Package A", 3), ("Package B", 4) })
        {
            object row = Activator.CreateInstance(rowType, nonPublic: true);
            rowType.GetProperty("Package").SetValue(row, item.Item1);
            rowType.GetProperty("FrameNumber").SetValue(row, framed ? "Frame 1" : "");
            rowType.GetProperty("AssemblyNumber").SetValue(row, "Drawing " + item.Item2);
            rowType.GetProperty("Qty").SetValue(row, item.Item2);
            rows.Add(row);
        }
        return rows;
    }

    public static void RunExcel()
    {
        foreach (bool grouped in new[] { false, true })
        foreach (bool framed in new[] { false, true })
        foreach (bool packages in new[] { false, true })
        foreach (bool empty in new[] { false, true })
        {
            string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems-Register-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            try
            {
                var cfg = new ProcurementConfig
                {
                    TargetFolder = folder, GroupByPackage = grouped, CreatePackageWorksheets = packages
                };
                Report.GetMethod("ExportExcel", PrivateStatic).Invoke(null, new object[] { cfg, Rows(framed, empty) });
                using var zip = ZipFile.OpenRead(Path.Combine(folder, "BOM-ASSEMBLY REGISTER.xlsx"));
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                var sheets = zip.Entries.Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet")
                    && entry.FullName.EndsWith(".xml")).OrderBy(entry => entry.FullName).ToArray();
                Check(sheets.Length == (packages && !empty ? 3 : 1), "expected package worksheets");
                int[] expected = empty ? new[] { 0 } : packages ? new[] { 9, 5, 4 } : new[] { 9 };
                for (int i = 0; i < sheets.Length; i++)
                {
                    using var stream = sheets[i].Open();
                    var last = XDocument.Load(stream).Descendants(ns + "row").Last();
                    var cells = last.Elements(ns + "c").ToArray();
                    Check(cells[1].Value == "SUM", "final row has SUM under Drawing Number");
                    Check((int)cells[2].Element(ns + "v") == expected[i], "final Qty sums quantities in this worksheet");
                    Check(cells[0].Value == "" && cells[3].Value == "" && cells[4].Value == "", "other total cells are blank");
                }
            }
            finally
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        Console.WriteLine("PASS: Assembly Register Excel totals sum quantities on main and package worksheets, including frame grouping and empty reports.");
    }

    public static void RunPdf()
    {
        foreach (bool grouped in new[] { false, true })
        foreach (bool empty in new[] { false, true })
        {
            var doc = (MigraDoc.DocumentObjectModel.Document)Report.GetMethod("BuildPdf", PrivateStatic)
                .Invoke(null, new object[] { new ProcurementConfig { GroupByPackage = grouped }, Rows(true, empty) });
            var table = doc.Sections[0].Elements.OfType<Table>().Last();
            var last = table.Rows[table.Rows.Count - 1];
            string Text(Cell cell) => string.Concat(cell.Elements.OfType<MigraDoc.DocumentObjectModel.Paragraph>()
                .SelectMany(paragraph => paragraph.Elements.OfType<MigraDoc.DocumentObjectModel.Text>())
                .Select(text => text.Content));
            Check(Text(last.Cells[1]) == "SUM", "PDF final row has SUM");
            Check(Text(last.Cells[2]) == (empty ? "0" : "9"), "PDF final Qty sums quantities");
            Check(last.Format.Font.Bold, "PDF total is bold");
        }
        Console.WriteLine("PASS: Assembly Register PDF table ends with a bold SUM and quantity total.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assembly Register: " + message);
    }
}
