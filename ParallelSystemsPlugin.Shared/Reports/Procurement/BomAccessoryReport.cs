using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Autodesk.Revit.DB;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Classes;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using RvtDoc = Autodesk.Revit.DB.Document;
using MigraUnit = MigraDoc.DocumentObjectModel.Unit;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomAccessoryReport
    {
        private const string NoPackageAssigned = "NO PACKAGE ASSIGNED";

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            PdfRuntime.EnsureInitialized();

            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (doc.ActiveView == null)
                throw new InvalidOperationException("Active view is not available.");
            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            var siteMeasureNames = Helpers.Elements.GetSiteMeasureAssemblies(doc)
                .Select(x => x.AssemblyName ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<AccessoryRow> rows = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_PipeAccessory)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Select(x => new AccessoryRow
                {
                    PackageName = GetPackageName(doc, x),
                    AssemblyName = Helpers.Elements.GetAssemblyName(doc, x),
                    Size = GetSize(x),
                    Description = GetDescription(x),
                    Symbol = x.Symbol,
                    DimensionLabel = GetDisplayedParameter(x, "L")
                })
                .Where(x => cfg.IncludeSiteMeasure || !siteMeasureNames.Contains(x.AssemblyName ?? ""))
                .GroupBy(x => new { x.PackageName, x.Size, x.Description })
                .Select(x => new AccessoryRow
                {
                    PackageName = x.Key.PackageName,
                    Size = x.Key.Size,
                    Description = x.Key.Description,
                    Symbol = x.Select(item => item.Symbol).FirstOrDefault(symbol => symbol != null),
                    DimensionLabel = x.Select(item => item.DimensionLabel)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "",
                    Quantity = x.Count()
                })
                .OrderBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => ParseSize(x.Size))
                .ThenBy(x => x.Description, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (rows.Count == 0)
                throw new InvalidOperationException("No accessories found in the active view.");

            if (cfg.ExportReportsToExcel)
                ExportExcel(doc, cfg, rows);
            else
                ExportPdf(cfg, rows);
        }

        private static void ExportPdf(ProcurementConfig cfg, IList<AccessoryRow> rows)
        {
            var culture = new CultureInfo("en-US");
            DateTime date = cfg.Date == default(DateTime) ? DateTime.Today : cfg.Date;
            string dateText = date.ToString("dddd, dd MMMM yyyy", culture);

            var builder = new PdfReportBuilder();
            var section = builder.Section;
            PdfLayoutHelpers.DefineStyles(builder.Document);

            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = MigraUnit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.FooterDistance = MigraUnit.FromCentimeter(1.0);

            PdfLayoutHelpers.DrawHeader(section, cfg, "ACCESSORY REPORT", dateText);
            PdfLayoutHelpers.AddFooter(section, "", !cfg.IncludeSiteMeasure);

            foreach (var package in rows.GroupBy(x => x.PackageName))
            {
                AddPackageBand(section, package.Key);
                Table table = section.AddTable();
                table.Borders.Width = 0.25;
                table.Borders.Color = Colors.Gray;
                table.AddColumn(MigraUnit.FromCentimeter(4.5));
                table.AddColumn(MigraUnit.FromCentimeter(19.7));
                table.AddColumn(MigraUnit.FromCentimeter(3.5));

                Row header = table.AddRow();
                header.Shading.Color = Colors.WhiteSmoke;
                header.Format.Font.Bold = true;
                header.Cells[0].AddParagraph("Size");
                header.Cells[1].AddParagraph("Description");
                header.Cells[2].AddParagraph("Qty");
                header.Cells[2].Format.Alignment = ParagraphAlignment.Right;

                bool alternate = false;
                foreach (AccessoryRow item in package)
                {
                    Row row = table.AddRow();
                    if (alternate) row.Shading.Color = Colors.WhiteSmoke;
                    row.Cells[0].AddParagraph(item.Size ?? "");
                    row.Cells[1].AddParagraph(item.Description ?? "");
                    row.Cells[2].AddParagraph(item.Quantity.ToString(CultureInfo.InvariantCulture));
                    row.Cells[2].Format.Alignment = ParagraphAlignment.Right;
                    alternate = !alternate;
                }

                section.AddParagraph().Format.SpaceAfter = MigraUnit.FromCentimeter(0.4);
            }

            builder.Save(Path.Combine(cfg.TargetFolder, "BOM-ACCESSORY REPORT.pdf"));
        }

        private static void ExportExcel(RvtDoc doc, ProcurementConfig cfg, IList<AccessoryRow> rows)
        {
            var sheet = ExcelReportExporter.CreateReportSheet(
                cfg,
                "ACCESSORY REPORT",
                new[] { "Size", "Description", "Qty", "Image" },
                "");
            sheet.SetColumnWidth(1, 14);
            sheet.SetColumnWidth(2, 60);
            sheet.SetColumnWidth(3, 10);
            sheet.SetColumnWidth(4, 32);
            sheet.CenterColumns(1, 3);

            string previewFolder = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystems-AccessoryPreviews-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(previewFolder);

                foreach (var package in rows.GroupBy(x => x.PackageName))
                {
                    sheet.Add(ExcelReportExporter.RowKind.Group, "Package: " + package.Key);
                    bool alternate = false;
                    foreach (AccessoryRow item in package)
                    {
                        sheet.Add(
                            alternate ? ExcelReportExporter.RowKind.AlternateData : ExcelReportExporter.RowKind.Data,
                            item.Size ?? "",
                            item.Description ?? "",
                            item.Quantity,
                            "");

                        int rowNumber = sheet.RowCount;
                        string previewPath = CreateAccessoryPreviewImage(
                            item,
                            previewFolder,
                            rowNumber);
                        if (!string.IsNullOrWhiteSpace(previewPath))
                        {
                            sheet.SetRowHeight(rowNumber, 150);
                            sheet.AddImage(previewPath, rowNumber, 4, 210, 190);
                        }

                        alternate = !alternate;
                    }
                }

                ExcelReportExporter.SaveWorkbook(
                    ExcelReportExporter.BuildOutputPath(cfg, "BOM-ACCESSORY REPORT"),
                    new[] { sheet });
            }
            finally
            {
                try
                {
                    if (Directory.Exists(previewFolder))
                        Directory.Delete(previewFolder, true);
                }
                catch
                {
                    // Temporary preview cleanup must not invalidate a completed report.
                }
            }
        }

        private static string CreateAccessoryPreviewImage(
            AccessoryRow item,
            string folder,
            int rowNumber)
        {
            if (item?.Symbol == null || string.IsNullOrWhiteSpace(folder))
                return null;

            try
            {
                using (Bitmap preview = item.Symbol.GetPreviewImage(new Size(320, 220)))
                {
                    if (preview == null)
                        return null;

                    const int width = 420;
                    const int height = 380;
                    using (var output = new Bitmap(width, height, PixelFormat.Format32bppArgb))
                    using (Graphics graphics = Graphics.FromImage(output))
                    using (var labelFont = new System.Drawing.Font("Arial", 38, FontStyle.Regular, GraphicsUnit.Pixel))
                    using (var labelBrush = new SolidBrush(System.Drawing.Color.Black))
                    {
                        graphics.Clear(System.Drawing.Color.White);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.SmoothingMode = SmoothingMode.HighQuality;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

                        string label = item.DimensionLabel ?? "";
                        if (!string.IsNullOrWhiteSpace(label))
                        {
                            SizeF labelSize = graphics.MeasureString(label, labelFont);
                            graphics.DrawString(
                                label,
                                labelFont,
                                labelBrush,
                                width - labelSize.Width - 18,
                                12);
                        }

                        const int previewTop = 72;
                        System.Drawing.Rectangle target = FitImage(
                            preview.Width,
                            preview.Height,
                            new System.Drawing.Rectangle(12, previewTop, width - 24, height - previewTop - 12));
                        graphics.DrawImage(preview, target);

                        string path = Path.Combine(
                            folder,
                            "accessory-" + rowNumber.ToString(CultureInfo.InvariantCulture) + ".png");
                        output.Save(path, ImageFormat.Png);
                        return path;
                    }
                }
            }
            catch
            {
                // Some Revit element types do not expose a usable preview image.
                return null;
            }
        }

        private static System.Drawing.Rectangle FitImage(
            int sourceWidth,
            int sourceHeight,
            System.Drawing.Rectangle bounds)
        {
            if (sourceWidth <= 0 || sourceHeight <= 0)
                return bounds;

            double scale = Math.Min(
                bounds.Width / (double)sourceWidth,
                bounds.Height / (double)sourceHeight);
            int width = Math.Max(1, (int)Math.Round(sourceWidth * scale));
            int height = Math.Max(1, (int)Math.Round(sourceHeight * scale));
            return new System.Drawing.Rectangle(
                bounds.X + (bounds.Width - width) / 2,
                bounds.Y + (bounds.Height - height) / 2,
                width,
                height);
        }

        private static string GetDisplayedParameter(FamilyInstance item, string name)
        {
            foreach (Element source in new Element[] { item, item?.Symbol })
            {
                Parameter parameter = source?.LookupParameter(name);
                if (parameter == null || !parameter.HasValue)
                    continue;

                string value = parameter.AsValueString() ?? parameter.AsString();
                if (!string.IsNullOrWhiteSpace(value))
                    return NormalizeSize(value);
            }

            return "";
        }

        private static void AddPackageBand(Section section, string packageName)
        {
            Table table = section.AddTable();
            table.Borders.Visible = false;
            table.AddColumn(MigraUnit.FromCentimeter(27.7));
            Row row = table.AddRow();
            row.Shading.Color = MigraDoc.DocumentObjectModel.Color.FromRgb(217, 225, 242);
            Paragraph paragraph = row.Cells[0].AddParagraph("Package: " + packageName);
            paragraph.Format.Font.Bold = true;
            paragraph.Format.Alignment = ParagraphAlignment.Center;
            section.AddParagraph().Format.SpaceAfter = MigraUnit.FromCentimeter(0.1);
        }

        private static string GetPackageName(RvtDoc doc, FamilyInstance item)
        {
            if (item.AssemblyInstanceId != ElementId.InvalidElementId)
            {
                AssemblyInstance assembly = doc.GetElement(item.AssemblyInstanceId) as AssemblyInstance;
                string assemblyPackage = Helpers.Elements.GetProcurementPackageNameFromAssembly(assembly);
                if (!string.IsNullOrWhiteSpace(assemblyPackage))
                    return assemblyPackage;
            }

            string building = GetParameter(item, "PS_Building")
                ?? GetParameter(item.Symbol, "PS_Building");
            string level = GetParameter(item, "PS_Level")
                ?? GetParameter(item.Symbol, "PS_Level");
            string zone = GetParameter(item, "PS_Zone")
                ?? GetParameter(item.Symbol, "PS_Zone");
            string area = GetParameter(item, "PS_Area")
                ?? GetParameter(item.Symbol, "PS_Area");

            if (!string.IsNullOrWhiteSpace(building) &&
                !string.IsNullOrWhiteSpace(level) &&
                !string.IsNullOrWhiteSpace(zone) &&
                !string.IsNullOrWhiteSpace(area))
            {
                return string.Concat(
                    building.Trim(),
                    level.Trim(),
                    zone.Trim(),
                    "-",
                    area.Trim());
            }

            return NoPackageAssigned;
        }

        private static string GetDescription(FamilyInstance item)
        {
            string description = GetParameter(item.Symbol, "Description")
                ?? GetParameter(item, "Description")
                ?? GetParameter(item.Symbol, "BOM Description")
                ?? GetParameter(item, "BOM Description")
                ?? GetParameter(item.Symbol, "Procurement Description")
                ?? GetParameter(item, "Procurement Description");

            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            if (!string.IsNullOrWhiteSpace(item.Symbol?.Name) &&
                !string.Equals(item.Symbol.Name, "Standard", StringComparison.OrdinalIgnoreCase))
                return item.Symbol.Name.Trim();
            return item.Symbol?.Family?.Name?.Trim() ?? item.Name ?? "";
        }

        private static string GetSize(FamilyInstance item)
        {
            string[] parameterNames = { "Nominal Diameter", "Diameter", "Size", "DN", "NPS", "Pipe Size" };
            foreach (Element source in new Element[] { item, item.Symbol })
            {
                foreach (string name in parameterNames)
                {
                    Parameter parameter = source?.LookupParameter(name);
                    if (parameter == null || !parameter.HasValue) continue;
                    if (parameter.StorageType == StorageType.Double)
                    {
                        double mm = UnitUtils.ConvertFromInternalUnits(parameter.AsDouble(), UnitTypeId.Millimeters);
                        return Math.Round(mm).ToString(CultureInfo.InvariantCulture);
                    }

                    string value = parameter.AsString() ?? parameter.AsValueString();
                    if (!string.IsNullOrWhiteSpace(value)) return NormalizeSize(value);
                }
            }

            try
            {
                Connector connector = item.MEPModel?.ConnectorManager?.Connectors
                    .Cast<Connector>()
                    .FirstOrDefault(x => x.Domain == Domain.DomainPiping && x.Radius > 0);
                if (connector != null)
                {
                    double mm = UnitUtils.ConvertFromInternalUnits(connector.Radius * 2, UnitTypeId.Millimeters);
                    return Math.Round(mm).ToString(CultureInfo.InvariantCulture);
                }
            }
            catch { }

            return "";
        }

        private static string GetParameter(Element element, string name)
        {
            Parameter parameter = element?.LookupParameter(name);
            if (parameter == null || !parameter.HasValue) return null;
            return parameter.AsString() ?? parameter.AsValueString();
        }

        private static string NormalizeSize(string value)
        {
            string normalized = (value ?? "").Trim()
                .Replace("DN", "").Replace("dn", "")
                .Replace("MM", "").Replace("mm", "")
                .Replace("Ø", "").Trim();
            string number = new string(normalized.SkipWhile(x => !char.IsDigit(x))
                .TakeWhile(x => char.IsDigit(x) || x == '.').ToArray());
            double parsed;
            return double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                ? parsed.ToString(Math.Abs(parsed - Math.Round(parsed)) < 0.0001 ? "0" : "0.##", CultureInfo.InvariantCulture)
                : normalized;
        }

        private static double ParseSize(string size)
        {
            double parsed;
            return double.TryParse(NormalizeSize(size), NumberStyles.Any, CultureInfo.InvariantCulture, out parsed)
                ? parsed
                : double.MaxValue;
        }

        private sealed class AccessoryRow
        {
            public string AssemblyName { get; set; }
            public string PackageName { get; set; }
            public string Size { get; set; }
            public string Description { get; set; }
            public FamilySymbol Symbol { get; set; }
            public string DimensionLabel { get; set; }
            public int Quantity { get; set; }
        }
    }
}
