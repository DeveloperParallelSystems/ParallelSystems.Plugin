using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Drawing;

using ParallelSystemsPlugin.Models.Configs;

namespace ParallelSystemsPlugin.Helpers
{
    /// <summary>
    /// Small dependency-free XLSX writer used by Procurement reports.
    /// Do not replace this with Excel COM automation: Revit add-ins must not depend on Excel being installed.
    /// </summary>
    public static class ExcelReportExporter
    {
        public enum RowKind
        {
            Logo,
            Blank,
            Title,
            Metadata,
            Header,
            Group,
            Data,
            AlternateData,
            Note,
            RedNote,
            Total,
            GroupBlue,
            GroupOrange,
            GroupPurple,
            FittingCustom,
            FittingElbow,
            FittingEndCap,
            FittingFlange,
            FittingReducer,
            FittingSocket,
            FittingTee,
            FittingWeld,
            FittingShapedBranch,
            FittingOther,
        }

        public sealed class ExcelRow
        {
            public RowKind Kind { get; private set; }
            public List<object> Values { get; private set; }

            public ExcelRow(RowKind kind, params object[] values)
            {
                Kind = kind;
                Values = (values ?? new object[0]).ToList();
            }
        }

        public sealed class ExcelWorksheet
        {
            public string Name { get; set; }
            public string CompanyLogoPath { get; set; }
            public string ClientLogoPath { get; set; }
            public List<ExcelRow> Rows { get; private set; }

            // 1-based Excel column numbers:
            // 1 = A, 2 = B, 3 = C, etc.
            public HashSet<int> CenterAlignedColumns { get; private set; }
            public Dictionary<int, double> ColumnWidths { get; private set; }
            internal List<WorksheetImage> Images { get; private set; }
            internal Dictionary<int, double> RowHeights { get; private set; }

            public ExcelWorksheet(string name)
            {
                Name = string.IsNullOrWhiteSpace(name) ? "Sheet" : name;
                Rows = new List<ExcelRow>();
                CenterAlignedColumns = new HashSet<int>();
                ColumnWidths = new Dictionary<int, double>();
                Images = new List<WorksheetImage>();
                RowHeights = new Dictionary<int, double>();
            }

            public void Add(RowKind kind, params object[] values)
            {
                Rows.Add(new ExcelRow(kind, values));
            }

            public void CenterColumns(params int[] columnNumbers)
            {
                if (columnNumbers == null) return;

                foreach (int columnNumber in columnNumbers)
                {
                    if (columnNumber > 0)
                        CenterAlignedColumns.Add(columnNumber);
                }
            }

            public void SetColumnWidth(int columnNumber, double width)
            {
                if (columnNumber > 0 && width > 0)
                    ColumnWidths[columnNumber] = width;
            }

            public int RowCount => Rows.Count;

            public void SetRowHeight(int rowNumber, double heightPoints)
            {
                if (rowNumber > 0 && heightPoints > 0)
                    RowHeights[rowNumber] = heightPoints;
            }

            public void AddImage(string path, int rowNumber, int columnNumber, int widthPixels, int heightPixels)
            {
                WorksheetImage image;
                if (TryCreateWorksheetImage(path, false, rowNumber, columnNumber, widthPixels, heightPixels, out image))
                    Images.Add(image);
            }
        }

        internal sealed class WorksheetImage
        {
            public string Path { get; set; }
            public string Extension { get; set; }
            public bool IsLogo { get; set; }
            public int RowNumber { get; set; }
            public int ColumnNumber { get; set; }
            public int WidthPixels { get; set; }
            public int HeightPixels { get; set; }
        }

        public static void SaveSingleSheetReport(
            ProcurementConfig cfg,
            string fileNameWithoutExtension,
            string reportTitle,
            IList<string> headers,
            IEnumerable<IList<object>> dataRows,
            string note)
        {
            var sheet = CreateReportSheet(cfg, reportTitle, headers, note);
            bool alternate = false;

            foreach (var row in dataRows ?? Enumerable.Empty<IList<object>>())
            {
                sheet.Add(alternate ? RowKind.AlternateData : RowKind.Data, row == null ? new object[0] : row.ToArray());
                alternate = !alternate;
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(RowKind.Blank);
                sheet.Add(RowKind.Note, note);
            }

            SaveWorkbook(BuildOutputPath(cfg, fileNameWithoutExtension), new[] { sheet });
        }

        public static ExcelWorksheet CreateReportSheet(ProcurementConfig cfg, string reportTitle, IList<string> headers, string note, string projectPhase = null)
        {
            var sheet = new ExcelWorksheet(SafeWorksheetName(reportTitle));
            if (cfg != null)
            {
                sheet.CompanyLogoPath = cfg.CompanyLogoPath;
                sheet.ClientLogoPath = cfg.ClientLogoPath;
            }
            if (cfg != null &&
                (!string.IsNullOrWhiteSpace(cfg.CompanyLogoPath) ||
                 !string.IsNullOrWhiteSpace(cfg.ClientLogoPath)))
            {
                sheet.Add(RowKind.Logo);
            }
            sheet.Add(RowKind.Title, reportTitle ?? "Report");
            sheet.Add(RowKind.Metadata, "Job Number", cfg == null ? "" : cfg.JobNumber ?? "");
            sheet.Add(RowKind.Metadata, "Job Name", cfg == null ? "" : cfg.JobName ?? "");
            if (!string.IsNullOrWhiteSpace(projectPhase))
                sheet.Add(RowKind.Metadata, "Project Phase", projectPhase);
            sheet.Add(RowKind.Metadata, "Date", BuildDateText(cfg));
            sheet.Add(RowKind.Blank);
            sheet.Add(RowKind.Header, (headers ?? new string[0]).Cast<object>().ToArray());
            return sheet;
        }

        public static string BuildOutputPath(ProcurementConfig cfg, string fileNameWithoutExtension)
        {
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));
            if (string.IsNullOrWhiteSpace(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string safeName = SanitizeFileName(string.IsNullOrWhiteSpace(fileNameWithoutExtension) ? "Report" : fileNameWithoutExtension);
            return Path.Combine(cfg.TargetFolder, safeName + ".xlsx");
        }

        public static void SaveWorkbook(string outPath, IEnumerable<ExcelWorksheet> worksheets)
        {
            if (string.IsNullOrWhiteSpace(outPath)) throw new ArgumentNullException(nameof(outPath));

            string folder = Path.GetDirectoryName(outPath);
            if (!string.IsNullOrWhiteSpace(folder) && !Directory.Exists(folder))
                Directory.CreateDirectory(folder);

            var sheets = (worksheets ?? Enumerable.Empty<ExcelWorksheet>()).ToList();
            if (sheets.Count == 0)
                sheets.Add(new ExcelWorksheet("Sheet1"));

            if (File.Exists(outPath))
                File.Delete(outPath);

            using (var fs = new FileStream(outPath, FileMode.CreateNew, FileAccess.ReadWrite))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteEntry(zip, "[Content_Types].xml", BuildContentTypesXml(sheets));
                WriteEntry(zip, "_rels/.rels", RootRelationshipsXml);
                WriteEntry(zip, "docProps/app.xml", AppXml);
                WriteEntry(zip, "docProps/core.xml", BuildCoreXml());
                WriteEntry(zip, "xl/workbook.xml", BuildWorkbookXml(sheets));
                WriteEntry(zip, "xl/_rels/workbook.xml.rels", BuildWorkbookRelationshipsXml(sheets.Count));
                WriteEntry(zip, "xl/styles.xml", StylesXml);

                for (int i = 0; i < sheets.Count; i++)
                {
                    int sheetNumber = i + 1;
                    var images = GetUsableImages(sheets[i]);
                    WriteEntry(zip, "xl/worksheets/sheet" + sheetNumber.ToString(CultureInfo.InvariantCulture) + ".xml", BuildWorksheetXml(sheets[i], images.Count > 0));

                    if (images.Count == 0)
                        continue;

                    WriteEntry(zip, "xl/worksheets/_rels/sheet" + sheetNumber.ToString(CultureInfo.InvariantCulture) + ".xml.rels", BuildWorksheetRelationshipsXml(sheetNumber));
                    WriteEntry(zip, "xl/drawings/drawing" + sheetNumber.ToString(CultureInfo.InvariantCulture) + ".xml", BuildDrawingXml(images));
                    WriteEntry(zip, "xl/drawings/_rels/drawing" + sheetNumber.ToString(CultureInfo.InvariantCulture) + ".xml.rels", BuildDrawingRelationshipsXml(images, sheetNumber));

                    for (int imageIndex = 0; imageIndex < images.Count; imageIndex++)
                    {
                        string mediaName = GetMediaName(sheetNumber, imageIndex, images[imageIndex].Extension);
                        WriteBinaryEntry(zip, "xl/media/" + mediaName, images[imageIndex].Path);
                    }
                }
            }
        }

        private static string BuildWorksheetXml(ExcelWorksheet sheet, bool hasLogos)
        {
            var rows = sheet.Rows ?? new List<ExcelRow>();
            int columnCount = Math.Max(1, rows.Select(r => r.Values == null ? 0 : r.Values.Count).DefaultIfEmpty(1).Max());
            var sb = new StringBuilder();

            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">");
            sb.Append("<cols>");
            for (int c = 1; c <= columnCount; c++)
            {
                double width;
                if (!sheet.ColumnWidths.TryGetValue(c, out width))
                    width = GetColumnWidth(c);

                sb.Append("<col min=\"").Append(c).Append("\" max=\"").Append(c).Append("\" width=\"")
                  .Append(width.ToString(CultureInfo.InvariantCulture)).Append("\" customWidth=\"1\"/>");
            }
            sb.Append("</cols>");
            sb.Append("<sheetData>");

            var mergedTitleRows = new List<int>();
            for (int r = 0; r < rows.Count; r++)
            {
                int rowNumber = r + 1;
                var row = rows[r];
                double customHeight;
                double height = sheet.RowHeights.TryGetValue(rowNumber, out customHeight)
                    ? customHeight
                    : row.Kind == RowKind.Logo
                    ? 95.25
                    : row.Kind == RowKind.Title
                    ? 35.25
                    : row.Kind == RowKind.Group || IsFittingCategory(row.Kind)
                        ? 20
                        : 16;
                sb.Append("<row r=\"").Append(rowNumber).Append("\" ht=\"").Append(height.ToString(CultureInfo.InvariantCulture)).Append("\" customHeight=\"1\">");

                var values = row.Values ?? new List<object>();
                int cellsToWrite = Math.Max(columnCount, values.Count);
                for (int c = 0; c < cellsToWrite; c++)
                {
                    object value = c < values.Count ? values[c] : "";

                    int columnNumber = c + 1;
                    bool centerAlign = ShouldCenterAlign(sheet, row.Kind, columnNumber);
                    int styleIndex = IsFittingCategory(row.Kind) && columnNumber != 2
                        ? GetStyleIndex(RowKind.Data, false)
                        : GetStyleIndex(row.Kind, centerAlign);

                    WriteCell(sb, rowNumber, columnNumber, value, styleIndex);
                }

                sb.Append("</row>");

                if ((row.Kind == RowKind.Logo || row.Kind == RowKind.Title || row.Kind == RowKind.Group || row.Kind == RowKind.Note || row.Kind == RowKind.RedNote) && columnCount > 1)
                    mergedTitleRows.Add(rowNumber);
            }

            sb.Append("</sheetData>");

            if (mergedTitleRows.Count > 0)
            {
                sb.Append("<mergeCells count=\"").Append(mergedTitleRows.Count).Append("\">");
                foreach (int rowNumber in mergedTitleRows)
                {
                    ExcelRowKindMerge(sb, rows[rowNumber - 1].Kind, rowNumber, columnCount);
                }
                sb.Append("</mergeCells>");
            }

            sb.Append("<pageMargins left=\"0.25\" right=\"0.25\" top=\"0.75\" bottom=\"0.75\" header=\"0.3\" footer=\"0.3\"/>");
            if (hasLogos)
                sb.Append("<drawing r:id=\"rId1\"/>");
            sb.Append("</worksheet>");
            return sb.ToString();
        }

        private static List<WorksheetImage> GetUsableImages(ExcelWorksheet sheet)
        {
            var images = new List<WorksheetImage>();
            if (sheet == null) return images;

            AddUsableLogo(images, sheet.CompanyLogoPath);
            AddUsableLogo(images, sheet.ClientLogoPath);
            foreach (WorksheetImage image in sheet.Images ?? new List<WorksheetImage>())
            {
                WorksheetImage usable;
                if (TryCreateWorksheetImage(image.Path, false, image.RowNumber, image.ColumnNumber, image.WidthPixels, image.HeightPixels, out usable))
                    images.Add(usable);
            }
            return images;
        }

        private static void AddUsableLogo(ICollection<WorksheetImage> images, string path)
        {
            WorksheetImage image;
            if (TryCreateWorksheetImage(path, true, 0, 0, 0, 0, out image))
                images.Add(image);
        }

        private static bool TryCreateWorksheetImage(string path, bool isLogo, int rowNumber, int columnNumber, int widthPixels, int heightPixels, out WorksheetImage image)
        {
            image = null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return false;

            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            if (extension == "jpg") extension = "jpeg";
            if (extension != "png" && extension != "jpeg" && extension != "gif" && extension != "bmp") return false;

            try
            {
                using (Image.FromFile(path))
                {
                    image = new WorksheetImage
                    {
                        Path = path,
                        Extension = extension,
                        IsLogo = isLogo,
                        RowNumber = rowNumber,
                        ColumnNumber = columnNumber,
                        WidthPixels = widthPixels,
                        HeightPixels = heightPixels
                    };
                    return true;
                }
            }
            catch
            {
                // A bad image must not prevent the report itself from exporting.
            }
            return false;
        }

        private static string BuildWorksheetRelationshipsXml(int sheetNumber)
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                   "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/drawing\" Target=\"../drawings/drawing" + sheetNumber.ToString(CultureInfo.InvariantCulture) + ".xml\"/>" +
                   "</Relationships>";
        }

        private static string BuildDrawingXml(IList<WorksheetImage> images)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<xdr:wsDr xmlns:xdr=\"http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing\" xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">");
            int logoIndex = 0;
            for (int i = 0; i < images.Count; i++)
            {
                WorksheetImage image = images[i];
                long widthEmu = (image.IsLogo ? 338L * 8L / 10L : Math.Max(1, image.WidthPixels)) * 9525L;
                long heightEmu = (image.IsLogo ? 158L * 8L / 10L : Math.Max(1, image.HeightPixels)) * 9525L;
                if (image.IsLogo)
                {
                    long xOffsetEmu = logoIndex++ * (widthEmu + (10L * 9525L));
                    sb.Append("<xdr:absoluteAnchor><xdr:pos x=\"").Append(xOffsetEmu).Append("\" y=\"0\"/>");
                }
                else
                {
                    sb.Append("<xdr:oneCellAnchor><xdr:from><xdr:col>").Append(Math.Max(0, image.ColumnNumber - 1)).Append("</xdr:col><xdr:colOff>95250</xdr:colOff><xdr:row>").Append(Math.Max(0, image.RowNumber - 1)).Append("</xdr:row><xdr:rowOff>95250</xdr:rowOff></xdr:from>");
                }
                sb.Append("<xdr:ext cx=\"").Append(widthEmu).Append("\" cy=\"").Append(heightEmu).Append("\"/>");
                sb.Append("<xdr:pic><xdr:nvPicPr><xdr:cNvPr id=\"").Append(i + 1).Append("\" name=\"").Append(image.IsLogo ? "Logo " : "Accessory ").Append(i + 1).Append("\"/><xdr:cNvPicPr><a:picLocks noChangeAspect=\"1\"/></xdr:cNvPicPr></xdr:nvPicPr>");
                sb.Append("<xdr:blipFill><a:blip xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" r:embed=\"rId").Append(i + 1).Append("\"/><a:stretch><a:fillRect/></a:stretch></xdr:blipFill>");
                sb.Append("<xdr:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"").Append(widthEmu).Append("\" cy=\"").Append(heightEmu).Append("\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></xdr:spPr></xdr:pic>");
                sb.Append("<xdr:clientData/>").Append(image.IsLogo ? "</xdr:absoluteAnchor>" : "</xdr:oneCellAnchor>");
            }
            sb.Append("</xdr:wsDr>");
            return sb.ToString();
        }

        private static string BuildDrawingRelationshipsXml(IList<WorksheetImage> images, int sheetNumber)
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 0; i < images.Count; i++)
                sb.Append("<Relationship Id=\"rId").Append(i + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"../media/").Append(GetMediaName(sheetNumber, i, images[i].Extension)).Append("\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string GetMediaName(int sheetNumber, int logoIndex, string extension)
        {
            return "sheet" + sheetNumber.ToString(CultureInfo.InvariantCulture) + "Logo" + (logoIndex + 1).ToString(CultureInfo.InvariantCulture) + "." + extension;
        }

        private static void ExcelRowKindMerge(StringBuilder sb, RowKind kind, int rowNumber, int columnCount)
        {
            string lastColumn = GetColumnName(columnCount);
            sb.Append("<mergeCell ref=\"A").Append(rowNumber).Append(":")
              .Append(lastColumn).Append(rowNumber).Append("\"/>");
        }

        private static bool ShouldCenterAlign(ExcelWorksheet sheet, RowKind rowKind, int columnNumber)
        {
            if (sheet == null)
                return false;

            // Only center actual table rows.
            // Do not center title, metadata, group, note, etc.
            if (rowKind != RowKind.Data &&
                rowKind != RowKind.AlternateData &&
                rowKind != RowKind.Total)
            {
                return false;
            }

            return sheet.CenterAlignedColumns != null &&
                   sheet.CenterAlignedColumns.Contains(columnNumber);
        }

        private static bool IsFittingCategory(RowKind kind)
        {
            return kind >= RowKind.FittingCustom && kind <= RowKind.FittingOther;
        }

        private static void WriteCell(StringBuilder sb, int rowNumber, int columnNumber, object value, int styleIndex)
        {
            string cellRef = GetColumnName(columnNumber) + rowNumber.ToString(CultureInfo.InvariantCulture);
            sb.Append("<c r=\"").Append(cellRef).Append("\" s=\"").Append(styleIndex.ToString(CultureInfo.InvariantCulture)).Append("\"");

            if (IsNumeric(value))
            {
                sb.Append("><v>").Append(Convert.ToString(value, CultureInfo.InvariantCulture)).Append("</v></c>");
                return;
            }

            string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
            sb.Append(" t=\"inlineStr\"><is><t");
            if (text.StartsWith(" ", StringComparison.Ordinal) || text.EndsWith(" ", StringComparison.Ordinal) || text.Contains("\n"))
                sb.Append(" xml:space=\"preserve\"");
            sb.Append(">").Append(EscapeXml(text)).Append("</t></is></c>");
        }

        private static bool IsNumeric(object value)
        {
            if (value == null) return false;
            TypeCode code = Type.GetTypeCode(value.GetType());
            switch (code)
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                case TypeCode.Single:
                case TypeCode.Double:
                case TypeCode.Decimal:
                    return true;
                default:
                    return false;
            }
        }

        private static int GetStyleIndex(RowKind kind)
        {
            switch (kind)
            {
                case RowKind.Logo: return 0;
                case RowKind.Title: return 1;
                case RowKind.Metadata: return 2;
                case RowKind.Header: return 3;
                case RowKind.Group: return 6;
                case RowKind.AlternateData: return 5;
                case RowKind.Note: return 7;
                case RowKind.RedNote: return 16;
                case RowKind.Total: return 8;
                case RowKind.GroupBlue: return 9;    
                case RowKind.GroupOrange: return 10; 
                case RowKind.GroupPurple: return 11; 
                case RowKind.FittingCustom: return 17;
                case RowKind.FittingElbow: return 18;
                case RowKind.FittingEndCap: return 19;
                case RowKind.FittingFlange: return 20;
                case RowKind.FittingReducer: return 21;
                case RowKind.FittingSocket: return 22;
                case RowKind.FittingTee: return 23;
                case RowKind.FittingWeld: return 24;
                case RowKind.FittingShapedBranch: return 25;
                case RowKind.FittingOther: return 26;
                case RowKind.Data:
                default: return 4;
            }
        }

        private static int GetStyleIndex(RowKind kind, bool centerAlign)
        {
            switch (kind)
            {
                case RowKind.Logo:
                    return 0;

                case RowKind.Title:
                    return 1;

                case RowKind.Metadata:
                    return 2;

                case RowKind.Header:
                    return centerAlign ? 14 : 3;

                case RowKind.Group:
                    return 6;

                case RowKind.Data:
                    return centerAlign ? 12 : 4;

                case RowKind.AlternateData:
                    return centerAlign ? 13 : 5;

                case RowKind.Note:
                    return 7;

                case RowKind.RedNote:
                    return 16;

                case RowKind.Total:
                    return centerAlign ? 15 : 8;

                case RowKind.GroupBlue:
                    return 9;

                case RowKind.GroupOrange:
                    return 10;

                case RowKind.GroupPurple:
                    return 11;

                case RowKind.FittingCustom: return 17;
                case RowKind.FittingElbow: return 18;
                case RowKind.FittingEndCap: return 19;
                case RowKind.FittingFlange: return 20;
                case RowKind.FittingReducer: return 21;
                case RowKind.FittingSocket: return 22;
                case RowKind.FittingTee: return 23;
                case RowKind.FittingWeld: return 24;
                case RowKind.FittingShapedBranch: return 25;
                case RowKind.FittingOther: return 26;

                default:
                    return 4;
            }
        }

        private static double GetColumnWidth(int columnNumber)
        {
            if (columnNumber == 1) return 18;
            if (columnNumber == 2) return 26;
            return 20;
        }

        private static string GetColumnName(int columnNumber)
        {
            var dividend = columnNumber;
            var columnName = string.Empty;
            while (dividend > 0)
            {
                int modulo = (dividend - 1) % 26;
                columnName = Convert.ToChar(65 + modulo) + columnName;
                dividend = (dividend - modulo) / 26;
            }
            return columnName;
        }

        private static string BuildDateText(ProcurementConfig cfg)
        {
            var culture = new CultureInfo("en-US");
            DateTime dt = (cfg == null || cfg.Date == default(DateTime)) ? DateTime.Today : cfg.Date;
            return dt.ToString("dddd, dd MMMM yyyy", culture);
        }

        private static string SafeWorksheetName(string name)
        {
            string cleaned = new string((name ?? "Sheet").Select(ch => ":\\/?*[]".IndexOf(ch) >= 0 ? '-' : ch).ToArray()).Trim();
            if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "Sheet";
            return cleaned.Length > 31 ? cleaned.Substring(0, 31) : cleaned;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '-');
            return name.Trim();
        }

        private static string EscapeXml(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            return value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;").Replace("'", "&apos;");
        }

        private static void WriteEntry(ZipArchive zip, string name, string content)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var stream = entry.Open())
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(content);
            }
        }

        private static void WriteBinaryEntry(ZipArchive zip, string name, string sourcePath)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var destination = entry.Open())
            {
                source.CopyTo(destination);
            }
        }

        private static string BuildContentTypesXml(IList<ExcelWorksheet> sheets)
        {
            int sheetCount = sheets == null ? 0 : sheets.Count;
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
            sb.Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>");
            sb.Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>");
            sb.Append("<Default Extension=\"png\" ContentType=\"image/png\"/>");
            sb.Append("<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>");
            sb.Append("<Default Extension=\"gif\" ContentType=\"image/gif\"/>");
            sb.Append("<Default Extension=\"bmp\" ContentType=\"image/bmp\"/>");
            sb.Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
            sb.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/>");
            sb.Append("<Override PartName=\"/docProps/app.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.extended-properties+xml\"/>");
            for (int i = 1; i <= sheetCount; i++)
            {
                sb.Append("<Override PartName=\"/xl/worksheets/sheet").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                if (GetUsableImages(sheets[i - 1]).Count > 0)
                    sb.Append("<Override PartName=\"/xl/drawings/drawing").Append(i).Append(".xml\" ContentType=\"application/vnd.openxmlformats-officedocument.drawing+xml\"/>");
            }
            sb.Append("</Types>");
            return sb.ToString();
        }

        private static string BuildWorkbookXml(IList<ExcelWorksheet> sheets)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
            for (int i = 0; i < sheets.Count; i++)
            {
                sb.Append("<sheet name=\"").Append(EscapeXml(SafeWorksheetName(sheets[i].Name))).Append("\" sheetId=\"").Append(i + 1).Append("\" r:id=\"rId").Append(i + 1).Append("\"/>");
            }
            sb.Append("</sheets></workbook>");
            return sb.ToString();
        }

        private static string BuildWorkbookRelationshipsXml(int sheetCount)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            sb.Append("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
            for (int i = 1; i <= sheetCount; i++)
                sb.Append("<Relationship Id=\"rId").Append(i).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet").Append(i).Append(".xml\"/>");
            sb.Append("<Relationship Id=\"rId").Append(sheetCount + 1).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            sb.Append("</Relationships>");
            return sb.ToString();
        }

        private static string BuildCoreXml()
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                   "<cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:dcmitype=\"http://purl.org/dc/dcmitype/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
                   "<dc:creator>Parallel Systems Plugin</dc:creator><cp:lastModifiedBy>Parallel Systems Plugin</cp:lastModifiedBy>" +
                   "<dcterms:created xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:created>" +
                   "<dcterms:modified xsi:type=\"dcterms:W3CDTF\">" + now + "</dcterms:modified></cp:coreProperties>";
        }

        private const string RootRelationshipsXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties\" Target=\"docProps/app.xml\"/></Relationships>";
        private const string AppXml = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Properties xmlns=\"http://schemas.openxmlformats.org/officeDocument/2006/extended-properties\" xmlns:vt=\"http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes\"><Application>Parallel Systems Plugin</Application></Properties>";

        private const string StylesXml = @"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<styleSheet xmlns=""http://schemas.openxmlformats.org/spreadsheetml/2006/main"">
  <fonts count=""5"">
    <font><sz val=""10""/><name val=""Arial""/></font>
    <font><b/><sz val=""16""/><color rgb=""FF143C55""/><name val=""Arial""/></font>
    <font><b/><sz val=""10""/><name val=""Arial""/></font>
    <font><i/><sz val=""9""/><color rgb=""FF666666""/><name val=""Arial""/></font>
    <font><b/><sz val=""11""/><color rgb=""FFB00000""/><name val=""Arial""/></font>
  </fonts>
  <fills count=""19"">
    <fill><patternFill patternType=""none""/></fill>
    <fill><patternFill patternType=""gray125""/></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFE8F3E8""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF5F5F5""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFD9EAD3""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFE5E5""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFD9EAF7""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFCE4D6""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFEADCF8""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF4B183""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFF0000""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFA6A6A6""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFFFFF00""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF47D359""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFF8CBAD""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FF83CCEB""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFD996D3""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFC9B2E8""/><bgColor indexed=""64""/></patternFill></fill>
    <fill><patternFill patternType=""solid""><fgColor rgb=""FFD9EAD3""/><bgColor indexed=""64""/></patternFill></fill>
  </fills>
  <borders count=""2"">
    <border><left/><right/><top/><bottom/><diagonal/></border>
    <border><left style=""thin""><color rgb=""FFD9D9D9""/></left><right style=""thin""><color rgb=""FFD9D9D9""/></right><top style=""thin""><color rgb=""FFD9D9D9""/></top><bottom style=""thin""><color rgb=""FFD9D9D9""/></bottom><diagonal/></border>
  </borders>
  <cellStyleXfs count=""1""><xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0""/></cellStyleXfs>
  <cellXfs count=""27"">
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""0"" xfId=""0""/>
    <xf numFmtId=""0"" fontId=""1"" fillId=""2"" borderId=""0"" xfId=""0"" applyFill=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""left"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""3"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyBorder=""1"" applyAlignment=""1""><alignment vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""3"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyAlignment=""1""><alignment vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""4"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""3"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""4"" fillId=""5"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""6"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""7"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""8"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1""/>
 <xf numFmtId=""0"" fontId=""0"" fillId=""0"" borderId=""1"" xfId=""0"" applyBorder=""1"" applyAlignment=""1"">
      <alignment horizontal=""center"" vertical=""center""/>
    </xf>
    <xf numFmtId=""0"" fontId=""0"" fillId=""3"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyAlignment=""1"">
      <alignment horizontal=""center"" vertical=""center""/>
    </xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""3"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1"">
      <alignment horizontal=""center"" vertical=""center""/>
    </xf>
    <xf numFmtId=""0"" fontId=""4"" fillId=""5"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1"">
      <alignment horizontal=""center"" vertical=""center""/>
    </xf>
    <xf numFmtId=""0"" fontId=""4"" fillId=""0"" borderId=""0"" xfId=""0"" applyFont=""1""/>
    <xf numFmtId=""0"" fontId=""2"" fillId=""9"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""10"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""11"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""12"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""13"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""14"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""15"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""16"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""17"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
    <xf numFmtId=""0"" fontId=""2"" fillId=""18"" borderId=""1"" xfId=""0"" applyFill=""1"" applyBorder=""1"" applyFont=""1"" applyAlignment=""1""><alignment horizontal=""center"" vertical=""center""/></xf>
  </cellXfs>
  <cellStyles count=""1""><cellStyle name=""Normal"" xfId=""0"" builtinId=""0""/></cellStyles>
</styleSheet>";
    }
}
