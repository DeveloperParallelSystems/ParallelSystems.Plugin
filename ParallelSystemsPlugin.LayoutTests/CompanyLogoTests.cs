using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Helpers;
using System.IO.Compression;

internal static class CompanyLogoTests
{
    public static void Run()
    {
        string logo = new ProcurementConfig().CompanyLogoPath;
        if (!File.Exists(logo)) throw new InvalidOperationException("Default company logo must be available without choosing a file.");
        byte[] bytes = File.ReadAllBytes(logo);
        if (!bytes.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new InvalidOperationException("Default company logo must be a usable PNG.");
        foreach (string missing in new[] { "", null, Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png") })
            if (new ProcurementConfig { CompanyLogoPath = missing }.CompanyLogoPath != logo)
                throw new InvalidOperationException("Missing company logo must fall back to Parallel Systems.");
        string custom = Path.Combine(Path.GetTempPath(), "ParallelSystems-Logo-" + Guid.NewGuid() + ".png");
        try
        {
            File.Copy(logo, custom);
            if (new ProcurementConfig { CompanyLogoPath = custom }.CompanyLogoPath != custom)
                throw new InvalidOperationException("Existing chosen company logo must be retained.");
        }
        finally { if (File.Exists(custom)) File.Delete(custom); }
        string workbook = Path.Combine(Path.GetTempPath(), "ParallelSystems-Logo-" + Guid.NewGuid() + ".xlsx");
        try
        {
            ExcelReportExporter.SaveWorkbook(workbook, new List<ExcelReportExporter.ExcelWorksheet>
            {
                ExcelReportExporter.CreateReportSheet(new ProcurementConfig(), "Logo check", new[] { "Description" }, "")
            });
            using var zip = ZipFile.OpenRead(workbook);
            if (!zip.Entries.Any(entry => entry.FullName.StartsWith("xl/media/")))
                throw new InvalidOperationException("Default company logo must be embedded in exported workbooks.");
        }
        finally { if (File.Exists(workbook)) File.Delete(workbook); }
        Console.WriteLine("PASS: Company Logo defaults to the bundled Parallel Systems PNG and repairs missing paths.");
    }
}
