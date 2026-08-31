using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Classes;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using ParallelSystemPlugin.UI;
// ===== Aliases to remove ambiguity =====
using RvtDoc = Autodesk.Revit.DB.Document;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomPipeReport
    {
        private const double FT_TO_MM = 304.8;

        private const string PARAM_PACKAGE = "Vic_Area_PT";
        private const string PARAM_MATERIAL = "Segment Description";
        private const string PARAM_ASSEMBLY_NAME = "Assembly Name";
        private const string PARAM_DESCRIPTION = "End Prep";

        private enum OptimizationMode
        {
            BestFitDecreasing
        }

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();

            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string reportName = $"BOM-PIPE REPORT ({AppConfig.CurrentConfig.Procurement.CutListMaximumLength/1000}m)";
            string outPath = Path.Combine(cfg.TargetFolder, reportName + ".pdf");

            // ---------------- DATE ----------------
            var culture = new CultureInfo("en-US");
            DateTime dt = (cfg.Date == default) ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            // ---------------- CUT LIST CONFIG ----------------
            // This is important.
            // Pipe report quantity should match Cut List stock pipe quantity.
            double maxLen = (cfg.CutListMaximumLength <= 0) ? 6000 : cfg.CutListMaximumLength;
            double blade = (cfg.CutListBladeThickness < 0) ? 0 : cfg.CutListBladeThickness;
            double negAllow = (cfg.CutListNegativeAllowance < 0) ? 0 : cfg.CutListNegativeAllowance;

            // ---------------- COLLECT PIPE PIECES ----------------
            List<PipePiece> pieces = CollectPipePiecesFromActiveView(doc, negAllow);

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);

            var siteMeasureNames = siteMeasureAssemblies
                .Select(x => x.AssemblyName)
                .ToHashSet();

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;

            pieces = pieces
                .Where(x => includeSiteMeasureAssemblies || !siteMeasureNames.Contains(x.AssemblyName))
                .ToList();

            if (pieces.Count == 0)
                throw new InvalidOperationException("No valid pipes found in the active view for BOM-PIPE REPORT.");

            // ---------------- SUMMARY ----------------
            // This is the critical fix.
            // Do NOT use g.Count().
            // Start from the packed stock-pipe count. When a stock pipe leaves
            // a reusable remainder, report its consumed length in OFFCUT and
            // remove that partial pipe from the full-pipe quantity.
            var pipeSummaries = pieces
                .GroupBy(x => new
                {
                    x.Material,
                    x.SizeText,
                    x.SizeSort
                })
                .OrderBy(g => g.Key.Material)
                .ThenByDescending(g => g.Key.SizeSort)
                .Select(g =>
                {
                    var bins = PackPieces(
                        g.ToList(),
                        maxLen,
                        blade,
                        OptimizationMode.BestFitDecreasing);

                    var reusableOffcuts = bins
                        .Select(bin => new
                        {
                            RemainingMm = Math.Max(0, maxLen - bin.UsedMm),
                            UsedMm = Math.Min(maxLen, bin.UsedMm)
                        })
                        .Where(x =>
                            x.RemainingMm > 0 &&
                            x.RemainingMm >= Math.Max(0, cfg.OffcutThreshold))
                        .ToList();

                    return new PipeSummary
                    {
                        Size = g.Key.SizeText,
                        Name = g.Key.Material,
                        Length = Math.Round(maxLen).ToString(CultureInfo.InvariantCulture),
                        Count = Math.Max(0, bins.Count - reusableOffcuts.Count),
                        Offcuts = string.Join(", ", reusableOffcuts
                            .Select(x => Math.Round(x.UsedMm).ToString(CultureInfo.InvariantCulture) + " mm")),
                        Note = ""
                    };
                })
                .ToList();

            // Optional debug. Remove once confirmed.
            /*
            AppDialog.Info(
                "Pipe Debug",
                "Raw pipe pieces: " + pieces.Count + Environment.NewLine +
                "Grouped rows: " + pipeSummaries.Count + Environment.NewLine +
                "Stock pipe quantity: " + pipeSummaries.Sum(x => x.Count)
            );
            */

            string stockLengthLabel = FormatStockLength(maxLen);
            string note = cfg.IncludeSiteMeasure
                ? ""
                : "NOTE: This report does not include site-measured spools and branches";

            if (!cfg.ExportReportsToExcel)
            {
            // ==============================
            // PDF BUILDER
            // ==============================
            var builder = new PdfReportBuilder();
            var pdf = builder.Document;
            var section = builder.Section;

            PdfLayoutHelpers.DefineStyles(pdf);

            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.FooterDistance = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);

            PdfLayoutHelpers.DrawHeader(section, cfg, "PIPE REPORT", dateText);

            PdfLayoutHelpers.AddFooter(section, note, !cfg.IncludeSiteMeasure);

            // ==============================
            // TABLE
            // ==============================
            var table = BuildMainTable(section);
            AddMainHeaderRow(table, stockLengthLabel);

            bool shade = false;

            foreach (var r in pipeSummaries)
            {
                var tr = table.AddRow();
                tr.VerticalAlignment = VerticalAlignment.Center;

                if (shade)
                    tr.Shading.Color = Colors.WhiteSmoke;

                shade = !shade;

                tr.Cells[0].AddParagraph(r.Size ?? "");
                tr.Cells[1].AddParagraph(r.Name ?? "");
                tr.Cells[2].AddParagraph(r.Length ?? "");
                tr.Cells[2].Format.Alignment = ParagraphAlignment.Center;

                tr.Cells[3].AddParagraph(r.Count.ToString(CultureInfo.InvariantCulture));
                tr.Cells[3].Format.Alignment = ParagraphAlignment.Center;

                tr.Cells[4].AddParagraph(r.Offcuts ?? "");
                tr.Cells[5].AddParagraph(r.Note ?? "");
            }

            section.AddParagraph().Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.6);

            // ==============================
            // SAVE PDF
            // ==============================
            builder.Save(outPath);
            }

            // ==============================
            // EXPORT EXCEL
            // ==============================
            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, pipeSummaries, note, reportName, stockLengthLabel);
        }

        // =========================
        // TABLE
        // =========================
        private static Table BuildMainTable(Section section)
        {
            var table = section.AddTable();
            table.Borders.Width = 0.25;
            table.Borders.Color = Colors.LightGray;

            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.5));   // Size
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(14.0));  // Description
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.0));   // Length
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.0));   // Quantity
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.5));   // Offcut
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.5));   // Note

            return table;
        }

        private static void AddMainHeaderRow(Table table, string stockLengthLabel)
        {
            var hr = table.AddRow();
            hr.Shading.Color = Colors.WhiteSmoke;
            hr.Format.Font.Bold = true;
            hr.VerticalAlignment = VerticalAlignment.Center;

            hr.Cells[0].AddParagraph("Size");
            hr.Cells[1].AddParagraph("Description");
            hr.Cells[2].AddParagraph("Length");
            hr.Cells[2].Format.Alignment = ParagraphAlignment.Center;
            hr.Cells[3].AddParagraph("Pipe Required (" + stockLengthLabel + ")");
            hr.Cells[3].Format.Alignment = ParagraphAlignment.Center;
            hr.Cells[4].AddParagraph("OFFCUT");
            hr.Cells[5].AddParagraph("Note");
        }

        // =========================
        // EXCEL EXPORT
        // =========================
        private static void ExportExcel(
            ProcurementConfig cfg,
            List<PipeSummary> pipeSummaries,
            string note,
            string reportName,
            string stockLengthLabel)
        {
            var sheet = ParallelSystemsPlugin.Helpers.ExcelReportExporter.CreateReportSheet(
                cfg,
                "BOM-PIPE REPORT",
                new[] { "Size", "Description", "Length", "Pipe Required (" + stockLengthLabel + ")", "OFFCUT", "Note" },
                note);

            bool alt = false;

            foreach (var r in pipeSummaries)
            {
                sheet.Add(
                    alt
                        ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData
                        : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                    r.Size ?? "",
                    r.Name ?? "",
                    r.Length ?? "",
                    r.Count,
                    r.Offcuts ?? "",
                    r.Note ?? "");

                alt = !alt;
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.RedNote, note);
            }

            ParallelSystemsPlugin.Helpers.ExcelReportExporter.SaveWorkbook(
                ParallelSystemsPlugin.Helpers.ExcelReportExporter.BuildOutputPath(cfg, reportName),
                new[] { sheet });
        }

        // =========================
        // COLLECTORS
        // =========================
        private static List<PipePiece> CollectPipePiecesFromActiveView(RvtDoc doc, double negativeAllowance)
        {
            var result = new List<PipePiece>();

            if (doc.ActiveView == null)
                return result;

            var collector = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            IList<Element> elements = collector.ToElements();

            foreach (var e in elements)
            {
                double lenFt = GetDoubleParam(e, BuiltInParameter.CURVE_ELEM_LENGTH);
                double lenMm = lenFt * FT_TO_MM;

                if (lenMm <= 0.01)
                    continue;

                string package = GetStringParam(e, PARAM_PACKAGE);
                string material = GetStringParam(e, PARAM_MATERIAL);
                string assemblyName = GetStringParam(e, PARAM_ASSEMBLY_NAME);
                string pipeEndPrep = GetStringParam(e, AppConfig.CurrentConfig.PipeMapParameters.EndPrep);

                string desc = GetStringParam(e, "Description");

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

        // =========================
        // PARAMETER HELPERS
        // =========================
        private static string GetPipeSizeText(Element e, out double sizeSort)
        {
            sizeSort = 0;

            string s = GetStringParam(e, "Size");

            if (!string.IsNullOrWhiteSpace(s))
            {
                TryExtractNumber(s, out sizeSort);
                return s;
            }

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

            if (string.IsNullOrWhiteSpace(text))
                return false;

            var digits = new string(
                text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());

            return double.TryParse(
                digits,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out number);
        }

        private static string GetStringParam(Element e, string name)
        {
            if (e == null || string.IsNullOrWhiteSpace(name))
                return "";

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

            return p != null && p.StorageType == StorageType.Double
                ? p.AsDouble()
                : 0.0;
        }

        // =========================
        // OPTIMIZATION
        // Same logic as Cut List.
        // =========================
        private static List<PackedBin> PackPieces(
            List<PipePiece> pieces,
            double maxLen,
            double bladeThickness,
            OptimizationMode mode)
        {
            var items = pieces
                .OrderByDescending(x => x.AdjustedLengthMm)
                .ToList();

            var bins = new List<PackedBin>();

            foreach (var piece in items)
            {
                if (piece.AdjustedLengthMm <= 0.01)
                    continue;

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

        private static bool CanAdd(
            PackedBin bin,
            PipePiece piece,
            double maxLen,
            double bladeThickness)
        {
            double newUsed = ComputeUsedIfAdded(bin, piece, bladeThickness);

            return newUsed <= maxLen + 0.0001;
        }

        private static double ComputeUsedIfAdded(
            PackedBin bin,
            PipePiece piece,
            double bladeThickness)
        {
            if (bin.Items.Count == 0)
                return piece.AdjustedLengthMm;

            return bin.UsedMm + bladeThickness + piece.AdjustedLengthMm;
        }

        // =========================
        // MODELS
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
            public string PipeEndPrep { get; set; } = "";

            public double RawLengthMm { get; set; }
            public double AdjustedLengthMm { get; set; }
        }

        private sealed class PackedBin
        {
            public List<PipePiece> Items { get; } = new List<PipePiece>();

            public double UsedMm { get; set; }
        }

        private sealed class PipeSummary
        {
            public string Size { get; set; } = "";
            public string Name { get; set; } = "";
            public string Length { get; set; } = "";
            public int Count { get; set; }
            public string Offcuts { get; set; } = "";
            public string Note { get; set; } = "";
        }

        private static string FormatStockLength(double lengthMm)
        {
            double metres = lengthMm / 1000.0;
            return metres.ToString(metres % 1 == 0 ? "0" : "0.###", CultureInfo.InvariantCulture) + "m";
        }
    }
}
