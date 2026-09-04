using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using ParallelSystemsPlugin.Models.Configs;

// ===== Aliases to remove ambiguity =====
using RvtDoc = Autodesk.Revit.DB.Document;
using PdfDoc = MigraDoc.DocumentObjectModel.Document;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
using System.Reflection;
using ParallelSystemsPlugin.Configs;


namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomCutListReport
    {
        private const double FT_TO_MM = 304.8;

        // Adjust these if your shared params differ
        private const string PARAM_PACKAGE = "Vic_Area_PT";
        private const string PARAM_MATERIAL = "Segment Description"; // material/grade/segment desc
        private const string PARAM_ASSEMBLY_NAME = "Assembly Name";  // change if your assembly name param differs

        // If your end-prep / description param is different, change this:
        private const string PARAM_DESCRIPTION = "End Prep";

        private enum OptimizationMode
        {
            BestFitDecreasing
        }

        public static void Generate(RvtDoc doc, ProcurementConfig p)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (p == null) throw new ArgumentNullException(nameof(p));

            string reportName = $"BOM-CUT LIST REPORT ({AppConfig.CurrentConfig.Procurement.CutListMaximumLength/1000}m)";
            // Output path
            string outPath = Path.Combine(p.TargetFolder, $"{reportName}.pdf");

            // Config inputs
            double maxLen = (p.CutListMaximumLength <= 0) ? 6000 : p.CutListMaximumLength;
            double blade = (p.CutListBladeThickness < 0) ? 0 : p.CutListBladeThickness;
            double negAllow = (p.CutListNegativeAllowance < 0) ? 0 : p.CutListNegativeAllowance;

            // Date
            var culture = new CultureInfo("en-US");
            DateTime dt = (p.Date == default(DateTime)) ? DateTime.Today : p.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            // Collect pipes from active view
            List<PipePiece> pieces = CollectPipePiecesFromActiveView(doc, negAllow);

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);

            var siteMeasureNames = siteMeasureAssemblies
            .Select(x => x.AssemblyName)
            .ToHashSet();

            bool includeSiteMeasureAssemblies = p.IncludeSiteMeasure;

            pieces = pieces.Where(x =>
                includeSiteMeasureAssemblies ||
                !siteMeasureNames.Any(siteName =>
                    string.Equals(x.AssemblyName, siteName, StringComparison.OrdinalIgnoreCase) ||
                    x.AssemblyName.StartsWith(siteName + "_", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            if (pieces.Count == 0)
                throw new InvalidOperationException("No valid pipes found in the active view for BOM-CUT LIST.");

            // Grouping (package + material + size) similar to sample
            var groups = pieces
                .GroupBy(x => new { x.Package, x.Material, x.SizeText, x.SizeSort })
                .OrderBy(g => g.Key.Package)
                .ThenBy(g => g.Key.Material)
                .ThenByDescending(g => g.Key.SizeSort)
                .ToList();

            string debugText = string.Join(
                Environment.NewLine,
                pieces.Select(p =>

                    $"Assembly: {p.AssemblyName}, " +
                    $"Package: {p.Package}, " +
                    $"Material: {p.Material}, " +
                    $"Description: {p.Description}, " +
                    $"Size: {p.SizeText}, " +
                    $"RawLength: {p.RawLengthMm:F2}, " +
                    $"AdjustedLength: {p.AdjustedLengthMm:F2}")
            );


            var summary = pieces
            .GroupBy(x => new { x.Material, x.SizeText, x.SizeSort })
            .OrderBy(g => g.Key.Material)
            .ThenByDescending(g => g.Key.SizeSort)
            .Select(g =>
            {
                var bins = PackPieces(
                    g.ToList(),
                    maxLen,
                    blade,
                    OptimizationMode.BestFitDecreasing);

                return new
                {
                    Material = g.Key.Material,
                    Size = g.Key.SizeText,
                    Quantity = bins.Count,   // <-- Number of stock pipes
                    Items = g.ToList(),
                    Bins = bins
                };
            })
            .ToList();

            string note = p.IncludeSiteMeasure
                ? ""
                : "NOTE: This report does not include site-measured spools and branches";

            List<PackedBin> allPackedBins = groups
                .SelectMany(g => PackPieces(
                    g.ToList(),
                    maxLen,
                    blade,
                    OptimizationMode.BestFitDecreasing))
                .ToList();

            double grandTotalWaste = allPackedBins
                .Sum(bin => Math.Round(Math.Max(0, maxLen - bin.UsedMm)));
            double grandTotalStock = allPackedBins.Count * maxLen;
            double grandWastePct = grandTotalStock > 0
                ? (grandTotalWaste / grandTotalStock) * 100.0
                : 0;
            string totalWasteText =
                $"TOTAL WASTE: {Math.Round(grandTotalWaste)} mm ({grandWastePct:F1}%)";

            if (!p.ExportReportsToExcel)
            {
            // Build PDF
            PdfDoc pdf = new PdfDoc();
            DefineStyles(pdf);

            Section section = pdf.AddSection();
            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(2.0);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.0);

            // Header (match Assembly Register PROFESSIONAL THEME)
            DrawHeader(section, p, "BOM-CUT LIST", dateText);

            // Footer
            ParallelSystemsPlugin.Helpers.PdfLayoutHelpers.AddFooter(
                section,
                note,
                !p.IncludeSiteMeasure
            );

            foreach (var g in groups)
            {
                AddGroupHeader(section, p, g.Key.Package, g.Key.Material, g.Key.SizeText, maxLen, blade, negAllow);

                // pack items into 6000mm bars using Best-Fit Decreasing
                List<PackedBin> bins = PackPieces(g.ToList(), maxLen, blade, OptimizationMode.BestFitDecreasing);

                int pipeId = 0;
                foreach (var bin in bins)
                {
                    AddPipeIdHeader(section, pipeId, maxLen);

                    foreach (var item in bin.Items)
                    {
                        // line format similar to your sample:
                        // AssemblyName Size Material Description Length(mm)
                        string line =
                            $"{item.AssemblyName}  {item.SizeText}  {item.Material}  {item.Description}  {Math.Round(item.AdjustedLengthMm)}";

                        var table = section.AddTable();
                        table.Borders.Width = 0.25;

                        table.AddColumn(Unit.FromCentimeter(4.0));  // QTY
                        table.AddColumn(Unit.FromCentimeter(4.0));  // Size
                        table.AddColumn(Unit.FromCentimeter(7.0)); // Description
                        table.AddColumn(Unit.FromCentimeter(5.0));
                        table.AddColumn(Unit.FromCentimeter(7.0));

                        var r = table.AddRow();
                        r.Cells[0].AddParagraph(item.AssemblyName);
                        r.Cells[1].AddParagraph(item.SizeText);
                        r.Cells[2].AddParagraph(item.Material);
                        r.Cells[3].AddParagraph(item.PipeEndPrep);
                        r.Cells[4].AddParagraph(Math.Round(item.AdjustedLengthMm).ToString());

                        //var para = section.AddParagraph(line);
                        //para.Style = "Body";
                        //para.Format.SpaceAfter = Unit.FromPoint(1.2);
                    }

                    double waste = Math.Max(0, maxLen - bin.UsedMm);
                    double wastePct = (maxLen <= 0) ? 0 : (waste / maxLen) * 100.0;

                    var wasteP = section.AddParagraph($"Waste: {Math.Round(waste)}mm ({Math.Round(wastePct)}%)");
                    wasteP.Style = "BodyBold";
                    wasteP.Format.SpaceAfter = Unit.FromPoint(8);

                    // ✅ SEPARATOR LINE PER PIPE
                    var separator = section.AddParagraph("\u00A0");
                    separator.Format.Font.Size = 1;
                    separator.Format.Borders.Bottom.Width = 1;
                    separator.Format.Borders.Bottom.Color = Colors.Gray; // ✅ gray line
                    separator.Format.SpaceBefore = Unit.FromPoint(5);
                    separator.Format.SpaceAfter = Unit.FromPoint(10);

                    pipeId++;
                }

                section.AddParagraph().Format.SpaceAfter = Unit.FromPoint(10);
            }

            // ===== GRAND TOTAL (after all groups) =====

            var total = section.AddParagraph(
                totalWasteText);

            total.Style = "Heading2";
            total.Format.SpaceBefore = Unit.FromPoint(10);
            total.Format.SpaceAfter = Unit.FromPoint(10);

            var renderer = new PdfDocumentRenderer() { Document = pdf };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(outPath);
            }

            if (p.ExportReportsToExcel)
            {
                var worksheets = new List<ParallelSystemsPlugin.Helpers.ExcelReportExporter.ExcelWorksheet>
                {
                    BuildCutListExcelSheet(
                        p,
                        pieces,
                        maxLen,
                        blade,
                        negAllow,
                        note,
                        totalWasteText,
                        null)
                };

                var packageGroups = pieces
                    .GroupBy(piece => piece.Package ?? "", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(group => string.IsNullOrWhiteSpace(group.Key) ? 1 : 0)
                    .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (packageGroups.Count > 1)
                {
                    foreach (var packageGroup in packageGroups)
                    {
                        List<PipePiece> packagePieces = packageGroup.ToList();
                        worksheets.Add(BuildCutListExcelSheet(
                            p,
                            packagePieces,
                            maxLen,
                            blade,
                            negAllow,
                            note,
                            BuildTotalWasteText(packagePieces, maxLen, blade),
                            ParallelSystemsPlugin.Helpers.ExcelReportExporter.GetPackageWorksheetName(packageGroup.Key)));
                    }
                }

                ParallelSystemsPlugin.Helpers.ExcelReportExporter.SaveWorkbook(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.BuildOutputPath(p, reportName),
                    worksheets);
            }
        }

        private static ParallelSystemsPlugin.Helpers.ExcelReportExporter.ExcelWorksheet BuildCutListExcelSheet(
            ProcurementConfig config,
            IList<PipePiece> pieces,
            double maxLen,
            double blade,
            double negAllow,
            string note,
            string totalWasteText,
            string worksheetName)
        {
            bool isMasterList = string.IsNullOrWhiteSpace(worksheetName) && pieces
                .Select(x => x.Package ?? "")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

            var cutSheet = ParallelSystemsPlugin.Helpers.ExcelReportExporter.CreateReportSheet(
                config,
                "BOM-CUT LIST Details",
                new[] { "Package", "Assembly Name", "Size", "Material", "End Prep", "Cut Length" },
                note);
            if (!string.IsNullOrWhiteSpace(worksheetName))
                cutSheet.Name = worksheetName;

            if (isMasterList)
            {
                bool masterAlt = false;
                foreach (PipePiece item in pieces
                    .OrderBy(x => x.AssemblyName ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenByDescending(x => x.SizeSort)
                    .ThenBy(x => x.Material ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Package ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    cutSheet.Add(
                        masterAlt
                            ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData
                            : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                        item.Package ?? "",
                        item.AssemblyName ?? "",
                        item.SizeText ?? "",
                        item.Material ?? "",
                        item.PipeEndPrep ?? "",
                        Math.Round(item.AdjustedLengthMm));
                    masterAlt = !masterAlt;
                }

                cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Total, totalWasteText);

                if (!string.IsNullOrWhiteSpace(note))
                {
                    cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                    cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.RedNote, note);
                }

                return cutSheet;
            }

            var groups = pieces
                .GroupBy(x => new { x.Package, x.Material, x.SizeText, x.SizeSort })
                .OrderBy(g => g.Key.Package)
                .ThenBy(g => g.Key.Material)
                .ThenByDescending(g => g.Key.SizeSort)
                .ToList();

            var cutHeaderRow = cutSheet.Rows.Last();
            cutSheet.Rows.RemoveAt(cutSheet.Rows.Count - 1);
            bool cutHeaderAdded = false;
            bool cutAlt = false;

            foreach (var g in groups)
            {
                cutSheet.Add(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Group,
                    string.Format(CultureInfo.InvariantCulture, "Package: {0} | Material: {1} | Size: {2} | Stock Length: {3} | Blade: {4} | Negative Allowance: {5}",
                        g.Key.Package ?? "",
                        g.Key.Material ?? "",
                        g.Key.SizeText ?? "",
                        maxLen,
                        blade,
                        negAllow));

                if (!cutHeaderAdded)
                {
                    cutSheet.Rows.Add(cutHeaderRow);
                    cutHeaderAdded = true;
                }

                List<PackedBin> excelBins = PackPieces(g.ToList(), maxLen, blade, OptimizationMode.BestFitDecreasing);
                int pipeId = 0;
                foreach (var bin in excelBins)
                {
                    cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.GroupBlue, $"Pipe Id: {pipeId}");
                    double waste = Math.Max(0, maxLen - bin.UsedMm);
                    double wastePct = (maxLen <= 0) ? 0 : (waste / maxLen) * 100.0;

                    foreach (var item in bin.Items)
                    {
                        cutSheet.Add(
                            cutAlt ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                            g.Key.Package ?? "",
                            item.AssemblyName ?? "",
                            g.Key.SizeText ?? "",
                            g.Key.Material ?? "",
                            item.PipeEndPrep ?? "",
                            Math.Round(item.AdjustedLengthMm));
                        cutAlt = !cutAlt;
                    }

                    cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Total, $"Waste: {Math.Round(waste)}mm ({Math.Round(wastePct)}%)");
                    cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                    pipeId++;
                }
            }

            if (!cutHeaderAdded)
                cutSheet.Rows.Add(cutHeaderRow);

            cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Total, totalWasteText);

            if (!string.IsNullOrWhiteSpace(note))
            {
                cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                cutSheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.RedNote, note);
            }

            return cutSheet;
        }

        private static string BuildTotalWasteText(
            IList<PipePiece> pieces,
            double maxLen,
            double blade)
        {
            List<PackedBin> bins = pieces
                .GroupBy(x => new { x.Package, x.Material, x.SizeText, x.SizeSort })
                .SelectMany(group => PackPieces(
                    group.ToList(),
                    maxLen,
                    blade,
                    OptimizationMode.BestFitDecreasing))
                .ToList();

            double totalWaste = bins.Sum(bin => Math.Round(Math.Max(0, maxLen - bin.UsedMm)));
            double totalStock = bins.Count * maxLen;
            double totalWastePct = totalStock > 0 ? (totalWaste / totalStock) * 100.0 : 0;
            return $"TOTAL WASTE: {Math.Round(totalWaste)} mm ({totalWastePct:F1}%)";
        }

        // =========================
        // Models
        // =========================
        private sealed class PipePiece
        {
            public ElementId ElementId { get; set; }

            public string AssemblyName { get; set; } = "";
            public string Package { get; set; } = "";
            public string Material { get; set; } = "";
            public string Description { get; set; } = "";
            public string SizeText { get; set; } = "";
            public double SizeSort { get; set; }
            public string PipeEndPrep { get; set; }

            public double RawLengthMm { get; set; }
            public double AdjustedLengthMm { get; set; } // raw - negative allowance
        }

        private sealed class PackedBin
        {
            public List<PipePiece> Items { get; } = new List<PipePiece>();
            public double UsedMm { get; set; } // includes blade kerf between cuts
        }

        // =========================
        // Collectors
        // =========================
        private static List<PipePiece> CollectPipePiecesFromActiveView(RvtDoc doc, double negativeAllowance)
        {
            var viewId = doc.ActiveView?.Id;

            var collector = new FilteredElementCollector(doc, viewId)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            IList<Element> elements = collector.ToElements();

            var result = new List<PipePiece>();

            foreach (var e in elements)
            {
                double lenFt = GetDoubleParam(e, BuiltInParameter.CURVE_ELEM_LENGTH);
                double lenMm = lenFt * FT_TO_MM;
                if (lenMm <= 0.01) continue;

                string package = GetStringParam(e, PARAM_PACKAGE);
                string material = GetStringParam(e, PARAM_MATERIAL);
                string assemblyName = GetStringParam(e, PARAM_ASSEMBLY_NAME);
                if (string.IsNullOrWhiteSpace(assemblyName))
                    assemblyName = Helpers.Elements.GetAssemblyName(doc, e);

                string vicMark = GetStringParam(e, "Vic_Mark");
                if (string.IsNullOrWhiteSpace(vicMark))
                    vicMark = GetStringParam(e, "VicMark");

                assemblyName = CombineAssemblyNameAndVicMark(assemblyName, vicMark);
                string pipeEndPrep = GetStringParam(e, AppConfig.CurrentConfig.PipeMapParameters.EndPrep);

                string desc = GetStringParam(e, PARAM_DESCRIPTION);
                if (string.IsNullOrWhiteSpace(desc))
                    desc = GetStringParam(e, "Description");

                string sizeText = GetPipeSizeText(e, out double sizeSort);

                double adjusted = Math.Max(0, lenMm - negativeAllowance);

                result.Add(new PipePiece
                {
                    ElementId = e.Id,
                    Package = package ?? "",
                    Material = material ?? "",
                    AssemblyName = assemblyName ?? "",
                    Description = desc ?? "",
                    SizeText = sizeText ?? "",
                    SizeSort = sizeSort,
                    RawLengthMm = lenMm,
                    AdjustedLengthMm = adjusted,
                    PipeEndPrep = pipeEndPrep ?? ""
                });
            }

            return result;
        }

        private static string CombineAssemblyNameAndVicMark(string assemblyName, string vicMark)
        {
            assemblyName = (assemblyName ?? "").Trim();
            vicMark = (vicMark ?? "").Trim();

            if (string.IsNullOrWhiteSpace(assemblyName)) return vicMark;
            if (string.IsNullOrWhiteSpace(vicMark)) return assemblyName;

            string suffix = "_" + vicMark;
            return assemblyName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? assemblyName
                : assemblyName + suffix;
        }

        private static string GetPipeSizeText(Element e, out double sizeSort)
        {
            sizeSort = 0;

            // Try "Size" first (many pipe families provide this)
            string s = GetStringParam(e, "Size");
            if (!string.IsNullOrWhiteSpace(s))
            {
                TryExtractNumber(s, out sizeSort);
                return s;
            }

            // Try diameter value string
            s = GetStringParam(e, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
            if (!string.IsNullOrWhiteSpace(s))
            {
                TryExtractNumber(s, out sizeSort);
                return s;
            }

            return "";
        }

        private static bool TryExtractNumber(string text, out double number)
        {
            number = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;

            var digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
            return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        }

        private static string GetStringParam(Element e, string name)
        {
            if (e == null || string.IsNullOrWhiteSpace(name)) return "";
            var p = e.LookupParameter(name);
            return p?.AsString() ?? p?.AsValueString() ?? "";
        }

        private static string GetStringParam(Element e, BuiltInParameter bip)
        {
            var p = e?.get_Parameter(bip);
            return p?.AsValueString() ?? p?.AsString() ?? "";
        }

        private static double GetDoubleParam(Element e, BuiltInParameter bip)
        {
            var p = e?.get_Parameter(bip);
            return (p != null && p.StorageType == StorageType.Double) ? p.AsDouble() : 0.0;
        }

        // =========================
        // Optimization (Best-Fit Decreasing)
        // =========================
        private static List<PackedBin> PackPieces(
            List<PipePiece> pieces,
            double maxLen,
            double bladeThickness,
            OptimizationMode mode)
        {
            // sort decreasing by length
            var items = pieces
                .OrderByDescending(x => x.AdjustedLengthMm)
                .ToList();

            var bins = new List<PackedBin>();

            foreach (var piece in items)
            {
                if (piece.AdjustedLengthMm <= 0.01) continue;

                // Oversized piece -> its own bin (will show negative waste conceptually)
                if (piece.AdjustedLengthMm > maxLen)
                {
                    var over = new PackedBin();
                    over.Items.Add(piece);
                    over.UsedMm = piece.AdjustedLengthMm;
                    bins.Add(over);
                    continue;
                }

                int bestIndex = -1;
                double bestRemaining = double.MaxValue;

                // Best-Fit: choose bin leaving least remaining space after adding
                for (int i = 0; i < bins.Count; i++)
                {
                    if (!CanAdd(bins[i], piece, maxLen, bladeThickness))
                        continue;

                    double newUsed = ComputeUsedIfAdded(bins[i], piece, bladeThickness);
                    double remaining = maxLen - newUsed;

                    if (remaining < bestRemaining)
                    {
                        bestRemaining = remaining;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                {
                    var nb = new PackedBin();
                    nb.Items.Add(piece);
                    nb.UsedMm = piece.AdjustedLengthMm;
                    bins.Add(nb);
                }
                else
                {
                    var bin = bins[bestIndex];
                    bin.UsedMm = ComputeUsedIfAdded(bin, piece, bladeThickness);
                    bin.Items.Add(piece);
                }
            }

            return bins;
        }

        private static bool CanAdd(PackedBin bin, PipePiece piece, double maxLen, double bladeThickness)
        {
            double newUsed = ComputeUsedIfAdded(bin, piece, bladeThickness);
            return newUsed <= maxLen + 0.0001;
        }

        private static double ComputeUsedIfAdded(PackedBin bin, PipePiece piece, double bladeThickness)
        {
            if (bin.Items.Count == 0)
                return piece.AdjustedLengthMm;

            // add blade thickness between the last cut and the new cut
            return bin.UsedMm + bladeThickness + piece.AdjustedLengthMm;
        }

        // =========================
        // PDF Layout
        // =========================
        private static void DrawHeader(Section section, ProcurementConfig p, string title, string dateText)
        {
            // Theme colors (match Assembly Register)
            var themeGreen = MigraColor.FromRgb(60, 130, 60);        // primary green
            var themeGreenLight = MigraColor.FromRgb(232, 243, 232); // light band fill
            var themeText = MigraColor.FromRgb(20, 60, 85);          // dark blue/teal for title
            var themeLine = MigraColor.FromRgb(120, 170, 120);       // thin rule lines

            // A4 Landscape usable width:
            // 29.7cm - (1cm left + 1cm right margins) = 27.7cm
            const double USABLE_WIDTH_CM = 27.7;

            // Outer band (background + top/bottom green rules)
            var bandOuter = section.AddTable();
            bandOuter.Borders.Visible = false;
            bandOuter.AddColumn(Unit.FromCentimeter(USABLE_WIDTH_CM));

            var bandRow = bandOuter.AddRow();
            bandRow.Height = Unit.FromCentimeter(4.3);
            bandRow.VerticalAlignment = VerticalAlignment.Top;

            var bandCell = bandRow.Cells[0];
            bandCell.Shading.Color = themeGreenLight;

            // Top & Bottom green rules
            bandCell.Borders.Top.Visible = true;
            bandCell.Borders.Top.Width = 2.0;
            bandCell.Borders.Top.Color = themeGreen;

            bandCell.Borders.Bottom.Visible = true;
            bandCell.Borders.Bottom.Width = 2.0;
            bandCell.Borders.Bottom.Color = themeGreen;

            // Inner layout
            var band = bandCell.Elements.AddTable();
            band.Borders.Visible = false;

            // padding | logos | title | divider | right info | padding
            // Sum = 27.7 cm
            band.AddColumn(Unit.FromCentimeter(0.8));   // padding
            band.AddColumn(Unit.FromCentimeter(5.2));   // logos
            band.AddColumn(Unit.FromCentimeter(13.5));  // title
            band.AddColumn(Unit.FromCentimeter(0.25));  // vertical divider
            band.AddColumn(Unit.FromCentimeter(7.2));   // right info
            band.AddColumn(Unit.FromCentimeter(0.75));  // padding

            var row = band.AddRow();
            row.Height = Unit.FromCentimeter(4.3);
            row.TopPadding = Unit.FromCentimeter(0.20);
            row.BottomPadding = Unit.FromCentimeter(0.15);

            // ----- Logos (left) -----
            var logoCell = row.Cells[1];
            logoCell.VerticalAlignment = VerticalAlignment.Top;

            var logos = logoCell.Elements.AddTable();
            logos.Borders.Visible = false;
            logos.AddColumn(Unit.FromCentimeter(5.2));

            var lr1 = logos.AddRow();
            lr1.BottomPadding = Unit.FromCentimeter(0.20);

            var lr2 = logos.AddRow();

            // same sizing behavior as Assembly Register
            AddLogoFixedBox(lr1.Cells[0], p.CompanyLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));
            AddLogoFixedBox(lr2.Cells[0], p.ClientLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));

            // ----- Title (center-left) -----
            var titleCell = row.Cells[2];
            titleCell.VerticalAlignment = VerticalAlignment.Top;

            string titleText = string.IsNullOrWhiteSpace(title) ? "Cut List" : title;

            // Auto-fit title so it never wraps
            double titleFont = FitFontSizeToWidth(titleText, 13.5, 30, 18);

            var titleP = titleCell.AddParagraph(titleText);
            titleP.Format.Font.Name = "Arial";
            titleP.Format.Font.Size = titleFont;
            titleP.Format.Font.Bold = true;
            titleP.Format.Font.Color = themeText;
            titleP.Format.SpaceBefore = 0;
            titleP.Format.SpaceAfter = Unit.FromCentimeter(0.15);
            titleP.Format.Alignment = ParagraphAlignment.Left;
            titleP.Format.KeepTogether = true;

            // Underline below title (green rule)
            var underline = titleCell.AddParagraph("\u00A0");
            underline.Format.Font.Size = 1;
            underline.Format.SpaceBefore = 0;
            underline.Format.SpaceAfter = Unit.FromCentimeter(0.25);
            underline.Format.Borders.Bottom.Visible = true;
            underline.Format.Borders.Bottom.Width = 1.5;
            underline.Format.Borders.Bottom.Color = themeGreen;

            // ----- Vertical divider -----
            var dividerCell = row.Cells[3];
            dividerCell.VerticalAlignment = VerticalAlignment.Top;
            dividerCell.Borders.Left.Visible = true;
            dividerCell.Borders.Left.Width = 1.0;
            dividerCell.Borders.Left.Color = themeLine;

            // ----- Right info block (UNIFORM HEIGHT ROWS + FORCED SINGLE LINE) -----
            var infoCell = row.Cells[4];
            infoCell.VerticalAlignment = VerticalAlignment.Top;

            const double INFO_WIDTH_CM = 7.2;

            var infoT = infoCell.Elements.AddTable();
            infoT.Borders.Visible = false;
            infoT.AddColumn(Unit.FromCentimeter(INFO_WIDTH_CM));

            var hText = Unit.FromCentimeter(1.05);
            var hLine = Unit.FromCentimeter(0.20);

            // Date row
            var rDate = infoT.AddRow();
            rDate.Height = hText;
            rDate.VerticalAlignment = VerticalAlignment.Center;

            double dateFont = FitFontSizeToWidth(dateText, INFO_WIDTH_CM, 11, 8);
            var dateP = rDate.Cells[0].AddParagraph(dateText);
            dateP.Format.Alignment = ParagraphAlignment.Left;
            dateP.Format.Font.Name = "Arial";
            dateP.Format.Font.Size = dateFont;
            dateP.Format.Font.Color = Colors.DimGray;
            dateP.Format.SpaceBefore = 0;
            dateP.Format.SpaceAfter = 0;
            dateP.Format.KeepTogether = true;

            // line 1
            var rL1 = infoT.AddRow();
            rL1.Height = hLine;
            rL1.VerticalAlignment = VerticalAlignment.Center;
            var l1 = rL1.Cells[0].AddParagraph("\u00A0");
            l1.Format.Font.Size = 1;
            l1.Format.SpaceBefore = 0;
            l1.Format.SpaceAfter = 0;
            l1.Format.Borders.Bottom.Visible = true;
            l1.Format.Borders.Bottom.Width = 0.75;
            l1.Format.Borders.Bottom.Color = themeLine;

            // Job Number
            var rJN = infoT.AddRow();
            rJN.Height = hText;
            rJN.VerticalAlignment = VerticalAlignment.Center;
            AddSingleLineKeyValue(
                rJN.Cells[0],
                "Job Number",
                p.JobNumber ?? "",
                INFO_WIDTH_CM,
                baseSizePt: 12,
                minSizePt: 8,
                keyColor: Colors.DimGray,
                valColor: Colors.DimGray,
                valBold: true
            );

            // line 2
            var rL2 = infoT.AddRow();
            rL2.Height = hLine;
            rL2.VerticalAlignment = VerticalAlignment.Center;
            var l2 = rL2.Cells[0].AddParagraph("\u00A0");
            l2.Format.Font.Size = 1;
            l2.Format.SpaceBefore = 0;
            l2.Format.SpaceAfter = 0;
            l2.Format.Borders.Bottom.Visible = true;
            l2.Format.Borders.Bottom.Width = 0.75;
            l2.Format.Borders.Bottom.Color = themeLine;

            // Job Name
            var rName = infoT.AddRow();
            rName.Height = hText;
            rName.VerticalAlignment = VerticalAlignment.Center;
            AddSingleLineKeyValue(
                rName.Cells[0],
                "Job Name",
                p.JobName ?? "",
                INFO_WIDTH_CM,
                baseSizePt: 12,
                minSizePt: 8,
                keyColor: Colors.DimGray,
                valColor: Colors.DimGray,
                valBold: true
            );

            // Spacing below header (same as Assembly Register)
            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.35);
        }

        private static void AddLogoFixedBox(Cell cell, string path, Unit boxWidth, Unit boxHeight)
        {
            cell.VerticalAlignment = VerticalAlignment.Top;

            var p = cell.AddParagraph();
            p.Format.Alignment = ParagraphAlignment.Left;
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;

            if (!ParallelSystemsPlugin.Classes.PdfRuntime.IsSupportedImagePath(path))
                return;

            var img = p.AddImage(path);
            img.LockAspectRatio = true;
            img.Width = boxWidth;
            img.Height = boxHeight;
        }

        private static double CmToPoints(double cm)
        {
            // 1 inch = 2.54 cm, 1 inch = 72 points
            return (cm / 2.54) * 72.0;
        }

        private static double FitFontSizeToWidth(string text, double maxWidthCm, double baseSizePt, double minSizePt)
        {
            if (string.IsNullOrWhiteSpace(text)) return baseSizePt;

            // conservative shrink to prevent wrap
            const double avgCharWidthFactor = 0.62;

            // account for padding/layout overhead
            double safeWidthCm = Math.Max(0.0, maxWidthCm - 0.25);

            double maxWidthPt = CmToPoints(safeWidthCm);
            int charCount = text.Length;
            if (charCount <= 0) return baseSizePt;

            double required = maxWidthPt / (charCount * avgCharWidthFactor);

            if (required > baseSizePt) return baseSizePt;
            if (required < minSizePt) return minSizePt;
            return required;
        }

        private static void AddSingleLineKeyValue(
            Cell cell,
            string key,
            string value,
            double maxWidthCm,
            double baseSizePt,
            double minSizePt,
            MigraDoc.DocumentObjectModel.Color keyColor,
            MigraDoc.DocumentObjectModel.Color valColor,
            bool valBold = true)
        {
            key = key ?? "";
            value = value ?? "";

            // Narrow no-break space prevents splitting between ":" and value
            const string NNBSP = "\u202F";

            string full = $"{key}:{NNBSP}{value}";
            double size = FitFontSizeToWidth(full, maxWidthCm, baseSizePt, minSizePt);

            var p = cell.AddParagraph();
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;
            p.Format.Alignment = ParagraphAlignment.Left;
            p.Format.Font.Name = "Arial";
            p.Format.Font.Size = size;
            p.Format.KeepTogether = true;

            var k = p.AddFormattedText($"{key}:{NNBSP}");
            k.Font.Color = keyColor;
            k.Font.Bold = false;

            var v = p.AddFormattedText(value);
            v.Font.Color = valColor;
            v.Font.Bold = valBold;
        }

        private static void AddGroupHeader(Section section, ProcurementConfig p, string package, string material, string sizeText,
                                           double maxLen, double blade, double negAllow)
        {
            var hdr = section.AddParagraph($"{package} | {material} | {sizeText}");
            hdr.Style = "BodyBold";
            hdr.Format.SpaceAfter = Unit.FromPoint(2);

            var cfg = section.AddParagraph($"Max Length: {Math.Round(maxLen)}mm    Blade: {Math.Round(blade)}mm    Negative Allowance: {Math.Round(negAllow)}mm");
            cfg.Style = "Body";
            cfg.Format.SpaceAfter = Unit.FromPoint(6);
        }

        private static void AddPipeIdHeader(Section section, int pipeId, double maxLen)
        {
            var p1 = section.AddParagraph($"Pipe Id: {pipeId}");
            p1.Style = "BodyBold";
            p1.Format.SpaceAfter = Unit.FromPoint(1);

            var p2 = section.AddParagraph($"Max Length: {Math.Round(maxLen)}mm");
            p2.Style = "Body";
            p2.Format.SpaceAfter = Unit.FromPoint(3);
        }

        private static void DefineStyles(PdfDoc doc)
        {
            var normal = doc.Styles["Normal"];
            normal.Font.Name = "Arial";
            normal.Font.Size = 10;

            var body = doc.Styles.AddStyle("Body", "Normal");
            body.Font.Size = 10;

            var bold = doc.Styles.AddStyle("BodyBold", "Normal");
            bold.Font.Bold = true;
            bold.Font.Size = 10;
        }
    }
}
