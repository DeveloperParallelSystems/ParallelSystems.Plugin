using System;
using System.Globalization;
using System.IO;

using ParallelSystemsPlugin.Models.Configs;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

// Keep using the same alias pattern you already use elsewhere
using PdfDoc = MigraDoc.DocumentObjectModel.Document;
using MigraColor = MigraDoc.DocumentObjectModel.Color;

// IMPORTANT: Avoid collision with ParallelSystemsPlugin.Helpers.Unit
using MUnit = MigraDoc.DocumentObjectModel.Unit;

namespace ParallelSystemsPlugin.Helpers.Reports
{
    /// <summary>
    /// Reusable PDF building helpers for all procurement reports (Assembly Register, Cut List, etc.)
    /// Contains: page setup, base styles, header (logos/date/job info/title), and footer page numbering.
    /// </summary>
    public static class PdfReportCommon
    {
        // -----------------------------
        // Page / paper config (reusable)
        // -----------------------------
        public sealed class PageSpec
        {
            public PageFormat PageFormat { get; set; } = PageFormat.A4;
            public Orientation Orientation { get; set; } = Orientation.Portrait;

            public MUnit TopMargin { get; set; } = MUnit.FromCentimeter(1.0);
            public MUnit BottomMargin { get; set; } = MUnit.FromCentimeter(1.0);
            public MUnit LeftMargin { get; set; } = MUnit.FromCentimeter(1.0);
            public MUnit RightMargin { get; set; } = MUnit.FromCentimeter(1.0);

            /// <summary>
            /// Usable content width for header tables.
            /// Portrait A4 with 1cm margins ≈ 19cm usable width.
            /// </summary>
            public double UsableWidthCm { get; set; } = 19.0;
        }

        // -----------------------------
        // Header theme / layout (reusable)
        // -----------------------------
        public sealed class HeaderTheme
        {
            public MigraColor ThemeGreen { get; set; } = MigraColor.FromRgb(60, 130, 60);
            public MigraColor ThemeGreenLight { get; set; } = MigraColor.FromRgb(232, 243, 232);
            public MigraColor ThemeText { get; set; } = MigraColor.FromRgb(20, 60, 85);
            public MigraColor ThemeLine { get; set; } = MigraColor.FromRgb(120, 170, 120);

            public MUnit HeaderHeight { get; set; } = MUnit.FromCentimeter(4.3);

            public double PadCm { get; set; } = 0.6;
            public double LogosColCm { get; set; } = 4.6;
            public double TitleColCm { get; set; } = 8.2;
            public double DividerColCm { get; set; } = 0.2;
            public double InfoColCm { get; set; } = 4.8;

            public MUnit LogoBoxWidth { get; set; } = MUnit.FromCentimeter(3.0);
            public MUnit LogoBoxHeight { get; set; } = MUnit.FromCentimeter(1.25);

            public double TitleBasePt { get; set; } = 30;
            public double TitleMinPt { get; set; } = 20;

            public double InfoDateBasePt { get; set; } = 11;
            public double InfoDateMinPt { get; set; } = 8;

            public double InfoKVBasePt { get; set; } = 12;
            public double InfoKVMinPt { get; set; } = 8;

            public MUnit InfoTextRowHeight { get; set; } = MUnit.FromCentimeter(1.05);
            public MUnit InfoLineRowHeight { get; set; } = MUnit.FromCentimeter(0.20);

            public double DividerWidth { get; set; } = 1.0;
        }

        // -----------------------------
        // Public entry: create doc
        // -----------------------------
        public static PdfDoc CreateDocument(string docTitle, PageSpec pageSpec)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();
            if (pageSpec == null) pageSpec = new PageSpec();

            var doc = new PdfDoc();
            doc.Info.Title = docTitle ?? "";

            DefineBaseStyles(doc);
            return doc;
        }

        public static Section AddSection(PdfDoc doc)
        {
            return AddSection(doc, new PageSpec());
        }

        public static Section AddSection(PdfDoc doc, PageSpec pageSpec)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (pageSpec == null) pageSpec = new PageSpec();

            var section = doc.AddSection();
            ApplyPageSpec(section, pageSpec);
            return section;
        }

        public static void ApplyPageSpec(Section section, PageSpec pageSpec)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            if (pageSpec == null) pageSpec = new PageSpec();

            section.PageSetup.PageFormat = pageSpec.PageFormat;
            section.PageSetup.Orientation = pageSpec.Orientation;
            section.PageSetup.TopMargin = pageSpec.TopMargin;
            section.PageSetup.BottomMargin = pageSpec.BottomMargin;
            section.PageSetup.LeftMargin = pageSpec.LeftMargin;
            section.PageSetup.RightMargin = pageSpec.RightMargin;
        }

        // -----------------------------
        // Footer: page numbering
        // -----------------------------
        public static void AddFooterPageNumbers(Section section)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));

            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Alignment = ParagraphAlignment.Center;
            footer.Format.Font.Size = 9;
            footer.AddText("Page ");
            footer.AddPageField();
            footer.AddText(" of ");
            footer.AddNumPagesField();
        }

        // -----------------------------
        // Header: standard band
        // -----------------------------
        public static void AddStandardHeader(
            Section section,
            ProcurementConfig cfg,
            string reportTitle,
            string dateText,
            PageSpec pageSpec,
            HeaderTheme theme)
        {
            if (section == null) throw new ArgumentNullException(nameof(section));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (pageSpec == null) pageSpec = new PageSpec();
            if (theme == null) theme = new HeaderTheme(); // C# 7.3 (no ??=)

            // Outer band (background + top/bottom green rules)
            var bandOuter = section.AddTable();
            bandOuter.Borders.Visible = false;
            bandOuter.AddColumn(MUnit.FromCentimeter(pageSpec.UsableWidthCm));

            var bandRow = bandOuter.AddRow();
            bandRow.Height = theme.HeaderHeight;
            bandRow.VerticalAlignment = VerticalAlignment.Top;

            var bandCell = bandRow.Cells[0];
            bandCell.Shading.Color = theme.ThemeGreenLight;

            bandCell.Borders.Top.Visible = true;
            bandCell.Borders.Top.Width = 2.0;
            bandCell.Borders.Top.Color = theme.ThemeGreen;

            bandCell.Borders.Bottom.Visible = true;
            bandCell.Borders.Bottom.Width = 2.0;
            bandCell.Borders.Bottom.Color = theme.ThemeGreen;

            // Inner layout: padding | logos | title | divider | right info | padding
            var band = bandCell.Elements.AddTable();
            band.Borders.Visible = false;

            band.AddColumn(MUnit.FromCentimeter(theme.PadCm));
            band.AddColumn(MUnit.FromCentimeter(theme.LogosColCm));
            band.AddColumn(MUnit.FromCentimeter(theme.TitleColCm));
            band.AddColumn(MUnit.FromCentimeter(theme.DividerColCm));
            band.AddColumn(MUnit.FromCentimeter(theme.InfoColCm));
            band.AddColumn(MUnit.FromCentimeter(theme.PadCm));

            var row = band.AddRow();
            row.Height = theme.HeaderHeight;
            row.TopPadding = MUnit.FromCentimeter(0.20);
            row.BottomPadding = MUnit.FromCentimeter(0.15);

            // ----- Logos -----
            var logoCell = row.Cells[1];
            logoCell.VerticalAlignment = VerticalAlignment.Top;

            var logos = logoCell.Elements.AddTable();
            logos.Borders.Visible = false;
            logos.AddColumn(MUnit.FromCentimeter(theme.LogosColCm));

            var lr1 = logos.AddRow();
            lr1.BottomPadding = MUnit.FromCentimeter(0.20);

            var lr2 = logos.AddRow();

            AddLogoFixedBox(lr1.Cells[0], cfg.CompanyLogoPath, theme.LogoBoxWidth, theme.LogoBoxHeight);
            AddLogoFixedBox(lr2.Cells[0], cfg.ClientLogoPath, theme.LogoBoxWidth, theme.LogoBoxHeight);

            // ----- Title -----
            var titleCell = row.Cells[2];
            titleCell.VerticalAlignment = VerticalAlignment.Top;

            string titleText = string.IsNullOrWhiteSpace(reportTitle) ? "" : reportTitle.Trim();
            double titleFont = FitFontSizeToWidth(titleText, theme.TitleColCm, theme.TitleBasePt, theme.TitleMinPt);

            var titleP = titleCell.AddParagraph(titleText);
            titleP.Format.Font.Name = "Arial";
            titleP.Format.Font.Size = titleFont;
            titleP.Format.Font.Bold = true;
            titleP.Format.Font.Color = theme.ThemeText;
            titleP.Format.SpaceBefore = 0;
            titleP.Format.SpaceAfter = MUnit.FromCentimeter(0.15);
            titleP.Format.Alignment = ParagraphAlignment.Left;
            titleP.Format.KeepTogether = true;

            var underline = titleCell.AddParagraph("\u00A0");
            underline.Format.Font.Size = 1;
            underline.Format.SpaceBefore = 0;
            underline.Format.SpaceAfter = MUnit.FromCentimeter(0.25);
            underline.Format.Borders.Bottom.Visible = true;
            underline.Format.Borders.Bottom.Width = 1.5;
            underline.Format.Borders.Bottom.Color = theme.ThemeGreen;

            // ----- Divider -----
            var dividerCell = row.Cells[3];
            dividerCell.VerticalAlignment = VerticalAlignment.Top;
            dividerCell.Borders.Left.Visible = true;
            dividerCell.Borders.Left.Width = theme.DividerWidth;
            dividerCell.Borders.Left.Color = theme.ThemeLine;

            // ----- Right info -----
            var infoCell = row.Cells[4];
            infoCell.VerticalAlignment = VerticalAlignment.Top;

            double infoWidthCm = theme.InfoColCm;

            var infoT = infoCell.Elements.AddTable();
            infoT.Borders.Visible = false;
            infoT.AddColumn(MUnit.FromCentimeter(infoWidthCm));

            // Date row
            var rDate = infoT.AddRow();
            rDate.Height = theme.InfoTextRowHeight;
            rDate.VerticalAlignment = VerticalAlignment.Center;

            double dateFont = FitFontSizeToWidth(dateText ?? "", infoWidthCm, theme.InfoDateBasePt, theme.InfoDateMinPt);

            var dateP = rDate.Cells[0].AddParagraph(dateText ?? "");
            dateP.Format.Alignment = ParagraphAlignment.Left;
            dateP.Format.Font.Name = "Arial";
            dateP.Format.Font.Size = dateFont;
            dateP.Format.Font.Color = Colors.DimGray;
            dateP.Format.SpaceBefore = 0;
            dateP.Format.SpaceAfter = 0;
            dateP.Format.KeepTogether = true;

            AddThinLineRow(infoT, theme.InfoLineRowHeight, theme.ThemeLine);

            // Job Number
            var rJN = infoT.AddRow();
            rJN.Height = theme.InfoTextRowHeight;
            rJN.VerticalAlignment = VerticalAlignment.Center;

            AddSingleLineKeyValue(
                rJN.Cells[0],
                "Job Number",
                cfg.JobNumber ?? "",
                infoWidthCm,
                theme.InfoKVBasePt,
                theme.InfoKVMinPt,
                Colors.DimGray,
                Colors.DimGray,
                true
            );

            AddThinLineRow(infoT, theme.InfoLineRowHeight, theme.ThemeLine);

            // Job Name
            var rName = infoT.AddRow();
            rName.Height = theme.InfoTextRowHeight;
            rName.VerticalAlignment = VerticalAlignment.Center;

            AddSingleLineKeyValue(
                rName.Cells[0],
                "Job Name",
                cfg.JobName ?? "",
                infoWidthCm,
                theme.InfoKVBasePt,
                theme.InfoKVMinPt,
                Colors.DimGray,
                Colors.DimGray,
                true
            );
        }

        public static string BuildDateText(ProcurementConfig cfg, CultureInfo culture)
        {
            if (culture == null) culture = new CultureInfo("en-US");
            DateTime dt = (cfg == null || cfg.Date == default(DateTime)) ? DateTime.Today : cfg.Date;
            return dt.ToString("dddd, dd MMMM yyyy", culture);
        }

        // -----------------------------
        // PDF save
        // -----------------------------
        public static void SavePdf(PdfDoc doc, string outPath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            var renderer = new PdfDocumentRenderer() { Document = doc };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(outPath);
        }

        // -----------------------------
        // Base styles
        // -----------------------------
        private static void DefineBaseStyles(PdfDoc doc)
        {
            var normal = doc.Styles["Normal"];
            normal.Font.Name = "Arial";
            normal.Font.Size = 10;

            // Always safe: just add styles with unique names (avoid "already exists")
            if (doc.Styles["PS_Title"] == null)
            {
                var title = doc.Styles.AddStyle("PS_Title", "Normal");
                title.Font.Size = 18;
                title.Font.Bold = true;
                title.Font.Color = MigraColor.FromRgb(60, 80, 100);
            }

            if (doc.Styles["PS_Table"] == null)
            {
                var table = doc.Styles.AddStyle("PS_Table", "Normal");
                table.Font.Size = 10;
            }
        }

        // -----------------------------
        // Header helper primitives
        // -----------------------------
        private static void AddThinLineRow(Table infoT, MUnit height, MigraColor lineColor)
        {
            var r = infoT.AddRow();
            r.Height = height;
            r.VerticalAlignment = VerticalAlignment.Center;

            var p = r.Cells[0].AddParagraph("\u00A0");
            p.Format.Font.Size = 1;
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;
            p.Format.Borders.Bottom.Visible = true;
            p.Format.Borders.Bottom.Width = 0.75;
            p.Format.Borders.Bottom.Color = lineColor;
        }

        private static void AddLogoFixedBox(Cell cell, string path, MUnit boxWidth, MUnit boxHeight)
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

        // -----------------------------
        // Single-line fit helpers
        // -----------------------------
        private static double CmToPoints(double cm)
        {
            return (cm / 2.54) * 72.0;
        }

        public static double FitFontSizeToWidth(string text, double maxWidthCm, double baseSizePt, double minSizePt)
        {
            if (string.IsNullOrWhiteSpace(text)) return baseSizePt;

            const double avgCharWidthFactor = 0.62;

            double safeWidthCm = Math.Max(0.0, maxWidthCm - 0.25);
            double maxWidthPt = CmToPoints(safeWidthCm);

            int charCount = text.Length;
            if (charCount <= 0) return baseSizePt;

            double required = maxWidthPt / (charCount * avgCharWidthFactor);

            if (required > baseSizePt) return baseSizePt;
            if (required < minSizePt) return minSizePt;
            return required;
        }

        public static void AddSingleLineKeyValue(
            Cell cell,
            string key,
            string value,
            double maxWidthCm,
            double baseSizePt,
            double minSizePt,
            MigraColor keyColor,
            MigraColor valColor,
            bool valBold)
        {
            if (cell == null) return;

            if (key == null) key = "";
            if (value == null) value = "";

            const string NNBSP = "\u202F";
            string full = string.Format("{0}:{1}{2}", key, NNBSP, value);

            double size = FitFontSizeToWidth(full, maxWidthCm, baseSizePt, minSizePt);

            var p = cell.AddParagraph();
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;
            p.Format.Alignment = ParagraphAlignment.Left;
            p.Format.Font.Name = "Arial";
            p.Format.Font.Size = size;
            p.Format.KeepTogether = true;

            var k = p.AddFormattedText(string.Format("{0}:{1}", key, NNBSP));
            k.Font.Color = keyColor;
            k.Font.Bold = false;

            var v = p.AddFormattedText(value);
            v.Font.Color = valColor;
            v.Font.Bold = valBold;
        }
    }
}
