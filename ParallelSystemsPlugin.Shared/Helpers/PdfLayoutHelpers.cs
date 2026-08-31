using System;
using System.IO;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Models.Configs;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
namespace ParallelSystemsPlugin.Helpers
{
    public static class PdfLayoutHelpers
    {
        // =========================================================
        // STYLES
        // =========================================================
        public static void DefineStyles(Document doc)
        {
            var normal = doc.Styles["Normal"];
            normal.Font.Name = "Arial";
            normal.Font.Size = 10;

            var bold = doc.Styles.AddStyle("BodyBold", "Normal");
            bold.Font.Bold = true;

            var header = doc.Styles.AddStyle("HeaderTitle", "Normal");
            header.Font.Size = 20;
            header.Font.Bold = true;
            header.Font.Color = Colors.DarkSlateGray;
        }


        public static void DrawHeader(Section section, ProcurementConfig cfg, string title, string dateText, string projectPhase = null)
        {
            var themeGreen = MigraColor.FromRgb(60, 130, 60);
            var themeGreenLight = MigraColor.FromRgb(232, 243, 232);
            var themeText = MigraColor.FromRgb(20, 60, 85);
            var themeLine = MigraColor.FromRgb(120, 170, 120);

            const double USABLE_WIDTH_CM = 27.7; // A4 landscape minus 1cm margins each side

            var bandOuter = section.AddTable();
            bandOuter.Borders.Visible = false;
            bandOuter.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(USABLE_WIDTH_CM));

            var bandRow = bandOuter.AddRow();
            bandRow.Height = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(
                string.IsNullOrWhiteSpace(projectPhase) ? 4.3 : 5.4);
            bandRow.VerticalAlignment = VerticalAlignment.Top;

            var bandCell = bandRow.Cells[0];
            bandCell.Shading.Color = themeGreenLight;

            bandCell.Borders.Top.Visible = true;
            bandCell.Borders.Top.Width = 2.0;
            bandCell.Borders.Top.Color = themeGreen;

            bandCell.Borders.Bottom.Visible = true;
            bandCell.Borders.Bottom.Width = 2.0;
            bandCell.Borders.Bottom.Color = themeGreen;

            var band = bandCell.Elements.AddTable();
            band.Borders.Visible = false;

            // padding | logos | title | divider | right info | padding
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.8));
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(5.2));
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(13.5));
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.25));
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(7.2));
            band.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.75));

            var row = band.AddRow();
            row.Height = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(4.3);
            row.TopPadding = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.20);
            row.BottomPadding = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.15);

            // Logos
            var logoCell = row.Cells[1];
            logoCell.VerticalAlignment = VerticalAlignment.Top;

            var logos = logoCell.Elements.AddTable();
            logos.Borders.Visible = false;
            logos.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(5.2));

            var lr1 = logos.AddRow();
            lr1.BottomPadding = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.20);
            var lr2 = logos.AddRow();

            AddLogoFixedBox(lr1.Cells[0], cfg.CompanyLogoPath, MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.0), MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.25));
            AddLogoFixedBox(lr2.Cells[0], cfg.ClientLogoPath, MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.0), MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.25));

            // Title
            var titleCell = row.Cells[2];
            titleCell.VerticalAlignment = VerticalAlignment.Top;

            string titleText = string.IsNullOrWhiteSpace(title) ? "LOADING SCHEDULE" : title;
            double titleFont = FitFontSizeToWidth(titleText, 13.5, 30, 18);

            var titleP = titleCell.AddParagraph(titleText);
            titleP.Format.Font.Name = "Arial";
            titleP.Format.Font.Size = titleFont;
            titleP.Format.Font.Bold = true;
            titleP.Format.Font.Color = themeText;
            titleP.Format.SpaceBefore = 0;
            titleP.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.15);
            titleP.Format.Alignment = ParagraphAlignment.Left;
            titleP.Format.KeepTogether = true;

            var underline = titleCell.AddParagraph("\u00A0");
            underline.Format.Font.Size = 1;
            underline.Format.SpaceBefore = 0;
            underline.Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.25);
            underline.Format.Borders.Bottom.Visible = true;
            underline.Format.Borders.Bottom.Width = 1.5;
            underline.Format.Borders.Bottom.Color = themeGreen;

            // Divider
            var dividerCell = row.Cells[3];
            dividerCell.VerticalAlignment = VerticalAlignment.Top;
            dividerCell.Borders.Left.Visible = true;
            dividerCell.Borders.Left.Width = 1.0;
            dividerCell.Borders.Left.Color = themeLine;

            // Right info
            var infoCell = row.Cells[4];
            infoCell.VerticalAlignment = VerticalAlignment.Top;

            const double INFO_WIDTH_CM = 7.2;
            var infoT = infoCell.Elements.AddTable();
            infoT.Borders.Visible = false;
            infoT.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(INFO_WIDTH_CM));

            var hText = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.05);
            var hLine = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.20);

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

            var rL1 = infoT.AddRow();
            rL1.Height = hLine;
            var l1 = rL1.Cells[0].AddParagraph("\u00A0");
            l1.Format.Font.Size = 1;
            l1.Format.SpaceBefore = 0;
            l1.Format.SpaceAfter = 0;
            l1.Format.Borders.Bottom.Visible = true;
            l1.Format.Borders.Bottom.Width = 0.75;
            l1.Format.Borders.Bottom.Color = themeLine;

            var rJN = infoT.AddRow();
            rJN.Height = hText;
            AddSingleLineKeyValue(
                rJN.Cells[0],
                "Job Number",
                cfg.JobNumber ?? "",
                INFO_WIDTH_CM,
                baseSizePt: 12,
                minSizePt: 8,
                keyColor: Colors.DimGray,
                valColor: Colors.DimGray,
                valBold: true
            );

            var rL2 = infoT.AddRow();
            rL2.Height = hLine;
            var l2 = rL2.Cells[0].AddParagraph("\u00A0");
            l2.Format.Font.Size = 1;
            l2.Format.SpaceBefore = 0;
            l2.Format.SpaceAfter = 0;
            l2.Format.Borders.Bottom.Visible = true;
            l2.Format.Borders.Bottom.Width = 0.75;
            l2.Format.Borders.Bottom.Color = themeLine;

            var rName = infoT.AddRow();
            rName.Height = hText;
            AddSingleLineKeyValue(
                rName.Cells[0],
                "Job Name",
                cfg.JobName ?? "",
                INFO_WIDTH_CM,
                baseSizePt: 12,
                minSizePt: 8,
                keyColor: Colors.DimGray,
                valColor: Colors.DimGray,
                valBold: true
            );

            if (!string.IsNullOrWhiteSpace(projectPhase))
            {
                var phaseRow = infoT.AddRow();
                phaseRow.Height = hText;
                AddSingleLineKeyValue(
                    phaseRow.Cells[0],
                    "Project Phase",
                    projectPhase,
                    INFO_WIDTH_CM,
                    baseSizePt: 10,
                    minSizePt: 7,
                    keyColor: Colors.DimGray,
                    valColor: Colors.DimGray,
                    valBold: true);
            }

            section.AddParagraph().Format.SpaceAfter = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.35);
        }

        private static double CmToPoints(double cm) => (cm / 2.54) * 72.0;

        private static double FitFontSizeToWidth(string text, double maxWidthCm, double baseSizePt, double minSizePt)
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
            if (key == null) key = "";
            if (value == null) value = "";


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

        // =========================================================
        // FOOTER
        // =========================================================
        public static void AddFooter(Section section, string note, bool noteIsRed = false)
        {
            BuildFooter(section.Footers.Primary, note, noteIsRed);
            BuildFooter(section.Footers.EvenPage, note, noteIsRed);
        }
        private static void AddLogoFixedBox(Cell cell, string path, MigraDoc.DocumentObjectModel.Unit boxWidth, MigraDoc.DocumentObjectModel.Unit boxHeight)
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

        private static void BuildFooter(HeaderFooter footer, string note, bool noteIsRed)
        {
            var tbl = footer.AddTable();
            tbl.Borders.Visible = false;

            tbl.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(18));
            tbl.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(9.7));

            var row = tbl.AddRow();

            var notePara = row.Cells[0].AddParagraph(note ?? "");
            notePara.Format.Font.Size = 9;
            notePara.Format.Font.Color = noteIsRed ? Colors.Red : Colors.DimGray;

            var pagePara = row.Cells[1].AddParagraph();
            pagePara.Format.Alignment = ParagraphAlignment.Right;
            pagePara.Format.Font.Size = 9;
            pagePara.Format.Font.Color = Colors.DimGray;//

            pagePara.AddText("Page ");
            pagePara.AddPageField();
            pagePara.AddText(" of ");
            pagePara.AddNumPagesField();
        }


    }
}
