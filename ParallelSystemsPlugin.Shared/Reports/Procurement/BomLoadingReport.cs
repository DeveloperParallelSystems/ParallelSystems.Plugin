using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

using Autodesk.Revit.DB;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using DocumentFormat.OpenXml.Packaging;

using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Compatibility;

using RvtDoc = Autodesk.Revit.DB.Document;
using PdfDoc = MigraDoc.DocumentObjectModel.Document;
using RvtParameter = Autodesk.Revit.DB.Parameter;
using PdfTable = MigraDoc.DocumentObjectModel.Tables.Table;
using PdfCell = MigraDoc.DocumentObjectModel.Tables.Cell;
using PdfHeaderFooter = MigraDoc.DocumentObjectModel.HeaderFooter;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
using OpenXmlWorksheet = DocumentFormat.OpenXml.Spreadsheet.Worksheet;
using OpenXmlLegacyDrawing = DocumentFormat.OpenXml.Spreadsheet.LegacyDrawing;
using OpenXmlColumns = DocumentFormat.OpenXml.Spreadsheet.Columns;
using OpenXmlColumn = DocumentFormat.OpenXml.Spreadsheet.Column;
using OpenXmlSheetData = DocumentFormat.OpenXml.Spreadsheet.SheetData;

using System.Diagnostics;
using static ParallelSystemsPlugin.Helpers.Elements;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomLoadingReport
    {
        private const double FT_TO_MM = 304.8;
        private const double CUBIC_FT_TO_CUBIC_M = 0.028316846592;
        private const double LOADING_WEIGHT_SAFETY_FACTOR = 1.10;
        private const string NO_PACKAGE_ASSIGNED = "NO PACKAGE ASSIGNED";
        private const string LOADING_WEIGHT_NOTE =
            "* The assembly weight is approximate and includes a 10% safety factor.";

        private const string PARAM_MATERIAL_GRADE_PRIMARY = "Segment Description";
        private const string PARAM_MATERIAL_GRADE_FALLBACK = "Material";
        private const string PARAM_ASSEMBLY_NUMBER = "Assembly Number";
        private const string PARAM_FRAME_AREA = "Vic_Area_PT";

        private static readonly string[] WEIGHT_PARAMS =
        {
            "Vic_weight",
            "Vic_Weight",
            "Total Weight",
            "TOTAL WEIGHT"
        };

        private static readonly string[] LENGTH_PARAMS =
        {
            "Length",
            "Total Length",
            "Vic_Length"
        };

        private sealed class Row
        {
            public string PackageName { get; set; } = "";
            public string DrawingNumber { get; set; } = "";
            public string MaterialGrade { get; set; } = "";
            public int Qty { get; set; } = 1;
            public double WeightKg { get; set; } = 0.0;
            public double LengthMm { get; set; } = 0.0;
        }

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();

            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (doc.ActiveView == null)
                throw new InvalidOperationException("Active view is not available.");

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "BOM-LOADING REPORT.pdf");

            var culture = new CultureInfo("en-US");
            DateTime dt = cfg.Date == default(DateTime) ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            List<Row> rows = CollectAssemblyRowsFromActiveView(doc);

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);

            var siteMeasureNames = siteMeasureAssemblies
                .Select(x => x.AssemblyName ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;

            rows = rows
                .Where(x => includeSiteMeasureAssemblies || !siteMeasureNames.Contains(x.DrawingNumber ?? ""))
                .ToList();

            if (rows.Count == 0)
                throw new InvalidOperationException("No assemblies found in the active view for BOM-LOADING REPORT.");

            rows = rows
                .OrderBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.LengthMm)
                .ThenBy(r => r.DrawingNumber, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.MaterialGrade)
                .ToList();

            int totalQty = rows.Sum(r => r.Qty);
            double totalWeight = rows.Sum(r => r.WeightKg);
            double totalLen = rows.Sum(r => r.LengthMm);

            PdfDoc pdf = new PdfDoc();
            DefineStyles(pdf);

            Section section = pdf.AddSection();
            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.FooterDistance = Unit.FromCentimeter(1.0);

            AddFooterWithNoteAndPagination(section, cfg);
            DrawHeader(section, cfg, "LOADING SCHEDULE", dateText);

            PdfTable table = BuildMainTable(section);
            AddMainHeaderRow(table);

            bool shade = false;

            foreach (var package in rows.GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                var packageRow = table.AddRow();
                packageRow.VerticalAlignment = VerticalAlignment.Center;
                packageRow.Cells[0].AddParagraph(package.Key ?? NO_PACKAGE_ASSIGNED);

                foreach (var r in package)
                {
                    var tr = table.AddRow();
                    tr.VerticalAlignment = VerticalAlignment.Center;

                    if (shade)
                        tr.Shading.Color = Colors.WhiteSmoke;

                    shade = !shade;

                    tr.Cells[1].AddParagraph(r.DrawingNumber ?? "");
                    tr.Cells[2].AddParagraph(r.MaterialGrade ?? "");

                    tr.Cells[3].AddParagraph(r.Qty.ToString(CultureInfo.InvariantCulture));
                    tr.Cells[3].Format.Alignment = ParagraphAlignment.Center;

                    tr.Cells[4].AddParagraph(FormatWeight(r.WeightKg));
                    tr.Cells[4].Format.Alignment = ParagraphAlignment.Center;

                    tr.Cells[5].AddParagraph(FormatLengthMm(r.LengthMm));
                    tr.Cells[5].Format.Alignment = ParagraphAlignment.Center;

                    AddCheckboxCell(tr.Cells[6]);
                    AddCheckboxCell(tr.Cells[7]);
                    AddCheckboxCell(tr.Cells[8]);
                    AddCheckboxCell(tr.Cells[9]);
                    AddCheckboxCell(tr.Cells[10]);
                }
            }

            section.AddParagraph().Format.SpaceBefore = Unit.FromCentimeter(0.6);

            AddTotalBlock(section, totalQty, totalWeight, totalLen);

            var renderer = new PdfDocumentRenderer { Document = pdf };
            renderer.RenderDocument();
            if (!cfg.ExportReportsToExcel)
                renderer.PdfDocument.Save(outPath);

            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, rows, totalQty, totalWeight, totalLen);
        }

        private static void ExportExcel(
            ProcurementConfig cfg,
            List<Row> rows,
            int totalQty,
            double totalWeight,
            double totalLen)
        {
            string note = LOADING_WEIGHT_NOTE;

            var sheet = ParallelSystemsPlugin.Helpers.ExcelReportExporter.CreateReportSheet(
                cfg,
                "LOADING SCHEDULE",
                new[]
                {
                    "Package",
                    "Drawing Number",
                    "Material Grade",
                    "Qty",
                    "Weight (kg)",
                    "Length (mm)",
                    "CUT",
                    "FAB",
                    "WELD",
                    "QA",
                    "LOAD"
                },
                note);

            bool alt = false;
            var dataRowOffsets = new List<int>();
            int nextRowOffset = 0;

            foreach (var package in rows.GroupBy(r => r.PackageName, StringComparer.OrdinalIgnoreCase))
            {
                sheet.Add(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                    package.Key ?? NO_PACKAGE_ASSIGNED,
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "",
                    "");
                nextRowOffset++;

                foreach (var r in package)
                {
                    dataRowOffsets.Add(nextRowOffset);
                    sheet.Add(
                        alt
                            ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData
                            : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                        "",
                        r.DrawingNumber ?? "",
                        r.MaterialGrade ?? "",
                        r.Qty,
                        r.WeightKg,
                        r.LengthMm,
                        "",
                        "",
                        "",
                        "",
                        "");

                    alt = !alt;
                    nextRowOffset++;
                }
            }

            sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);

            sheet.Add(
                ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Total,
                "",
                "TOTAL",
                "",
                totalQty,
                Math.Round(totalWeight, 1),
                Math.Round(totalLen, 0),
                "",
                "",
                "",
                "",
                "");

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Note, note);
            }

            string excelPath = ParallelSystemsPlugin.Helpers.ExcelReportExporter.BuildOutputPath(
                cfg,
                "BOM-LOADING REPORT");

            ParallelSystemsPlugin.Helpers.ExcelReportExporter.SaveWorkbook(
                excelPath,
                new[] { sheet });

            // Fix excessive column spacing first.
            NormalizeLoadingScheduleExcelLayout(excelPath);

            int firstDataRow = FindFirstLoadingDataRow(excelPath);

            AddLoadingScheduleCheckboxes(
                excelPath,
                firstDataRow: firstDataRow,
                dataRowOffsets: dataRowOffsets);
        }

        private static void NormalizeLoadingScheduleExcelLayout(string xlsxPath)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException("Excel file was not found.", xlsxPath);

            using (SpreadsheetDocument document = SpreadsheetDocument.Open(xlsxPath, true))
            {
                WorkbookPart workbookPart = document.WorkbookPart;

                if (workbookPart == null)
                    throw new InvalidOperationException("WorkbookPart is missing.");

                WorksheetPart worksheetPart = workbookPart.WorksheetParts.FirstOrDefault();

                if (worksheetPart == null)
                    throw new InvalidOperationException("WorksheetPart is missing.");

                OpenXmlWorksheet worksheet = worksheetPart.Worksheet;

                foreach (OpenXmlColumns oldColumns in worksheet.Elements<OpenXmlColumns>().ToList())
                {
                    oldColumns.Remove();
                }

                var columns = new OpenXmlColumns();

                AddColumnWidth(columns, 1, 1, 18.0); // A - Package
                AddColumnWidth(columns, 2, 2, 24.0); // B - Drawing Number
                AddColumnWidth(columns, 3, 3, 22.0); // C - Material Grade
                AddColumnWidth(columns, 4, 4, 8.0);  // D - Qty
                AddColumnWidth(columns, 5, 5, 10.0); // E - Weight
                AddColumnWidth(columns, 6, 6, 12.0); // F - Length

                // G:K - CUT, FAB, WELD, QA, LOAD
                AddColumnWidth(columns, 7, 11, 7.0);

                OpenXmlSheetData sheetData = worksheet.Elements<OpenXmlSheetData>().FirstOrDefault();

                if (sheetData != null)
                {
                    worksheet.InsertBefore(columns, sheetData);
                }
                else
                {
                    worksheet.PrependChild(columns);
                }

                worksheet.Save();
            }
        }

        private static void AddColumnWidth(
            OpenXmlColumns columns,
            uint min,
            uint max,
            double width)
        {
            columns.Append(new OpenXmlColumn
            {
                Min = min,
                Max = max,
                Width = width,
                CustomWidth = true
            });
        }

        private static void AddLoadingScheduleCheckboxes(
            string xlsxPath,
            int firstDataRow,
            IList<int> dataRowOffsets)
        {
            if (string.IsNullOrWhiteSpace(xlsxPath) || !File.Exists(xlsxPath))
                throw new FileNotFoundException("Excel file was not found.", xlsxPath);

            if (dataRowOffsets == null || dataRowOffsets.Count == 0)
                return;

            using (SpreadsheetDocument document = SpreadsheetDocument.Open(xlsxPath, true))
            {
                WorkbookPart workbookPart = document.WorkbookPart;

                if (workbookPart == null)
                    throw new InvalidOperationException("WorkbookPart is missing.");

                WorksheetPart worksheetPart = workbookPart.WorksheetParts.FirstOrDefault();

                if (worksheetPart == null)
                    throw new InvalidOperationException("WorksheetPart is missing.");

                foreach (VmlDrawingPart oldVmlPart in worksheetPart.VmlDrawingParts.ToList())
                {
                    worksheetPart.DeletePart(oldVmlPart);
                }

                OpenXmlWorksheet worksheet = worksheetPart.Worksheet;

                foreach (OpenXmlLegacyDrawing existingLegacyDrawing in worksheet.Elements<OpenXmlLegacyDrawing>().ToList())
                {
                    existingLegacyDrawing.Remove();
                }

                VmlDrawingPart vmlPart = worksheetPart.AddNewPart<VmlDrawingPart>();
                string relationshipId = worksheetPart.GetIdOfPart(vmlPart);

                string vml = BuildLoadingCheckboxVml(
                    firstDataRow,
                    dataRowOffsets,
                    firstColumnZeroBased: 6,
                    lastColumnZeroBased: 10);

                using (var writer = new StreamWriter(
                    vmlPart.GetStream(FileMode.Create, FileAccess.Write),
                    Encoding.UTF8))
                {
                    writer.Write(vml);
                }

                worksheet.Append(new OpenXmlLegacyDrawing
                {
                    Id = relationshipId
                });

                worksheet.Save();
            }
        }

        private static int FindFirstLoadingDataRow(string xlsxPath)
        {
            using (SpreadsheetDocument document = SpreadsheetDocument.Open(xlsxPath, false))
            {
                WorkbookPart workbookPart = document.WorkbookPart;

                if (workbookPart == null)
                    throw new InvalidOperationException("WorkbookPart is missing.");

                WorksheetPart worksheetPart = workbookPart.WorksheetParts.FirstOrDefault();

                if (worksheetPart == null)
                    throw new InvalidOperationException("WorksheetPart is missing.");

                OpenXmlWorksheet worksheet = worksheetPart.Worksheet;

                var sheetData = worksheet.GetFirstChild<DocumentFormat.OpenXml.Spreadsheet.SheetData>();

                if (sheetData == null)
                    throw new InvalidOperationException("SheetData is missing.");

                foreach (var row in sheetData.Elements<DocumentFormat.OpenXml.Spreadsheet.Row>())
                {
                    foreach (var cell in row.Elements<DocumentFormat.OpenXml.Spreadsheet.Cell>())
                    {
                        string value = GetOpenXmlCellText(document, cell);

                        if (string.Equals(value, "Drawing Number", StringComparison.OrdinalIgnoreCase))
                        {
                            return checked((int)row.RowIndex.Value + 1);
                        }
                    }
                }
            }

            throw new InvalidOperationException("Could not find the Loading Schedule header row.");
        }

        private static string GetOpenXmlCellText(
    SpreadsheetDocument document,
    DocumentFormat.OpenXml.Spreadsheet.Cell cell)
        {
            if (cell == null)
                return "";

            string value = cell.InnerText ?? "";

            if (cell.DataType == null)
                return value;

            if (cell.DataType.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.SharedString)
            {
                SharedStringTablePart sharedStringPart = document.WorkbookPart.SharedStringTablePart;

                if (sharedStringPart == null)
                    return value;

                if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sharedStringIndex))
                {
                    return sharedStringPart.SharedStringTable
                        .ElementAt(sharedStringIndex)
                        .InnerText ?? "";
                }
            }

            if (cell.DataType.Value == DocumentFormat.OpenXml.Spreadsheet.CellValues.InlineString)
            {
                return cell.InnerText ?? "";
            }

            return value;
        }

        private static string BuildLoadingCheckboxVml(
            int firstDataRow,
            IList<int> dataRowOffsets,
            int firstColumnZeroBased,
            int lastColumnZeroBased)
        {
            XNamespace v = "urn:schemas-microsoft-com:vml";
            XNamespace o = "urn:schemas-microsoft-com:office:office";
            XNamespace x = "urn:schemas-microsoft-com:office:excel";

            var root = new XElement("xml",
                new XAttribute(XNamespace.Xmlns + "v", v),
                new XAttribute(XNamespace.Xmlns + "o", o),
                new XAttribute(XNamespace.Xmlns + "x", x),

                new XElement(o + "shapelayout",
                    new XAttribute(v + "ext", "edit"),
                    new XElement(o + "idmap",
                        new XAttribute(v + "ext", "edit"),
                        new XAttribute("data", "1"))),

                new XElement(v + "shapetype",
                    new XAttribute("id", "_x0000_t201"),
                    new XAttribute("coordsize", "21600,21600"),
                    new XAttribute(o + "spt", "201"),
                    new XAttribute("path", "m,l,21600r21600,l21600,xe"),
                    new XElement(v + "stroke",
                        new XAttribute("joinstyle", "miter")),
                    new XElement(v + "path",
                        new XAttribute("shadowok", "f"),
                        new XAttribute(o + "extrusionok", "f"),
                        new XAttribute("strokeok", "f"),
                        new XAttribute("fillok", "f"),
                        new XAttribute(o + "connecttype", "rect")),
                    new XElement(o + "lock",
                        new XAttribute(v + "ext", "edit"),
                        new XAttribute("shapetype", "t")))
            );

            int shapeId = 1025;
            int zIndex = 1;

            foreach (int rowOffset in dataRowOffsets)
            {
                int excelRow = firstDataRow + rowOffset;
                int rowZeroBased = excelRow - 1;

                for (int col = firstColumnZeroBased; col <= lastColumnZeroBased; col++)
                {
                    root.Add(CreateCheckboxShape(
                        v,
                        o,
                        x,
                        shapeId,
                        zIndex,
                        col,
                        rowZeroBased));

                    shapeId++;
                    zIndex++;
                }
            }

            return root.ToString(System.Xml.Linq.SaveOptions.DisableFormatting);
        }

        private static XElement CreateCheckboxShape(
            XNamespace v,
            XNamespace o,
            XNamespace x,
            int shapeId,
            int zIndex,
            int columnZeroBased,
            int rowZeroBased)
        {
            string shapeName = "_x0000_s" + shapeId.ToString(CultureInfo.InvariantCulture);

            return new XElement(v + "shape",
                new XAttribute("id", shapeName),
                new XAttribute("type", "#_x0000_t201"),
                new XAttribute("style",
                    "position:absolute;" +
                    "margin-left:0pt;" +
                    "margin-top:0pt;" +
                    "width:14pt;" +
                    "height:14pt;" +
                    "z-index:" + zIndex.ToString(CultureInfo.InvariantCulture) + ";" +
                    "mso-wrap-style:tight"),
                new XAttribute("filled", "f"),
                new XAttribute("fillcolor", "window [65]"),
                new XAttribute("stroked", "f"),
                new XAttribute("strokecolor", "windowText [64]"),
                new XAttribute(o + "insetmode", "auto"),

                new XElement(v + "path",
                    new XAttribute("shadowok", "t"),
                    new XAttribute("strokeok", "t"),
                    new XAttribute("fillok", "t")),

                new XElement(o + "lock",
                    new XAttribute(v + "ext", "edit"),
                    new XAttribute("rotation", "t")),

                new XElement(v + "textbox",
                    new XAttribute("style", "mso-direction-alt:auto"),
                    new XAttribute(o + "singleclick", "f"),
                    new XElement("div",
                        new XAttribute("style", "text-align:left"))),

                new XElement(x + "ClientData",
                    new XAttribute("ObjectType", "Checkbox"),
                    new XElement(x + "SizeWithCells"),

                    new XElement(x + "Anchor",
                        string.Format(
                            CultureInfo.InvariantCulture,
                            "{0}, 20, {1}, 4, {0}, 60, {1}, 18",
                            columnZeroBased,
                            rowZeroBased)),

                    new XElement(x + "AutoFill", "False"),
                    new XElement(x + "AutoLine", "False"),
                    new XElement(x + "TextVAlign", "Center"))
            );
        }

        private static List<Row> CollectAssemblyRowsFromActiveView(RvtDoc doc)
        {
            var res = new List<Row>();

            if (doc.ActiveView == null)
                return res;

            var assemblies = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .Where(a => !IsFrameAssembly(doc, a))
                .ToList();

            foreach (var a in assemblies.Take(1))
            {
                DebugPossibleWeightParams(doc, a);
            }

            foreach (var a in assemblies)
            {
                string asmNo = Helpers.Elements.GetStringParam(a, PARAM_ASSEMBLY_NUMBER);

                if (string.IsNullOrEmpty(asmNo))
                    asmNo = a.Name;

                string mat = Helpers.Elements.GetMaterialGrade(doc, a);

                if (string.IsNullOrWhiteSpace(mat))
                    mat = GetStringParamInstanceOrType(a, PARAM_MATERIAL_GRADE_FALLBACK);

                double weightKg = GetAssemblyWeightKg(doc, a);

                double lenMm = 0.0;

                foreach (var lName in LENGTH_PARAMS)
                {
                    if (TryGetLengthMm(a, lName, out lenMm))
                        break;
                }

                res.Add(new Row
                {
                    PackageName = GetLoadingPackageName(a),
                    DrawingNumber = asmNo,
                    MaterialGrade = mat ?? "",
                    Qty = 1,
                    WeightKg = Math.Round(weightKg, 1),
                    LengthMm = Math.Round(lenMm, 0)
                });
            }

            return res;
        }

        private static string GetLoadingPackageName(AssemblyInstance assembly)
        {
            string assemblyName = (assembly?.Name ?? "").Trim();
            string[] parts = assemblyName
                .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToArray();

            // Legacy Loading Report convention:
            // ATSYD3-B210-MC6-01-CHWF-001 -> B210-MC6-01
            // ATSYD3-B210-MC6-01-CHWR-002 -> B210-MC6-01
            // ATSYD3-B210-MC6-CHWF-015    -> B210-MC6
            if (parts.Length >= 5 && IsLoadingServiceToken(parts[parts.Length - 2]))
            {
                int firstPackagePart = parts[0].StartsWith("AT", StringComparison.OrdinalIgnoreCase)
                    ? 1
                    : 0;
                int packagePartCount = parts.Length - firstPackagePart - 2;
                if (packagePartCount > 0)
                    return string.Join("-", parts.Skip(firstPackagePart).Take(packagePartCount));
            }

            string packageName = Helpers.Elements.GetProcurementPackageNameFromAssembly(assembly);
            if (!string.IsNullOrWhiteSpace(packageName))
                return packageName.Trim();

            string building = GetStringParamInstanceOrType(assembly, "PS_Building");
            string level = GetStringParamInstanceOrType(assembly, "PS_Level");
            string zone = GetStringParamInstanceOrType(assembly, "PS_Zone");
            string area = GetStringParamInstanceOrType(assembly, "PS_Area");

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

            return NO_PACKAGE_ASSIGNED;
        }

        private static bool IsLoadingServiceToken(string value)
        {
            return string.Equals(value, "CHWF", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(value, "CHWR", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFrameAssembly(RvtDoc doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return false;

            string area = GetStringParam(assembly, PARAM_FRAME_AREA);
            if (string.IsNullOrWhiteSpace(area))
                area = GetStringParam(doc.GetElement(assembly.GetTypeId()), PARAM_FRAME_AREA);

            return !string.IsNullOrWhiteSpace(area) &&
                area.IndexOf("FRAME", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static void DebugPossibleWeightParams(RvtDoc doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return;

            Debug.WriteLine("");
            Debug.WriteLine("========== POSSIBLE WEIGHT PARAM DEBUG ==========");
            Debug.WriteLine($"Assembly: {assembly.Name}");

            DebugPossibleWeightParamsForElement("Assembly Instance", assembly);

            var assemblyType = doc.GetElement(assembly.GetTypeId());

            if (assemblyType != null)
                DebugPossibleWeightParamsForElement("Assembly Type", assemblyType);

            foreach (ElementId memberId in assembly.GetMemberIds())
            {
                var member = doc.GetElement(memberId);

                if (member == null)
                    continue;

                DebugPossibleWeightParamsForElement(
                    $"Member Instance: {member.Category?.Name} / {member.Name}",
                    member);

                var memberType = doc.GetElement(member.GetTypeId());

                if (memberType != null)
                {
                    DebugPossibleWeightParamsForElement(
                        $"Member Type: {memberType.Name}",
                        memberType);
                }
            }

            Debug.WriteLine("========== END POSSIBLE WEIGHT PARAM DEBUG ==========");
            Debug.WriteLine("");
        }

        private static void DebugPossibleWeightParamsForElement(string label, Element e)
        {
            if (e == null)
                return;

            Debug.WriteLine("");
            Debug.WriteLine("-- " + label + " --");

            foreach (RvtParameter p in e.Parameters)
            {
                if (p?.Definition == null)
                    continue;

                string name = p.Definition.Name ?? "";

                bool possibleWeight =
                    name.IndexOf("weight", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("mass", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("kg", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("lbs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("lb", StringComparison.OrdinalIgnoreCase) >= 0;

                if (!possibleWeight)
                    continue;

                Debug.WriteLine($"{name} = {GetParamValueForDebug(p)}");
            }
        }

        private static void DebugWeightParams(RvtDoc doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return;

            Debug.WriteLine("");
            Debug.WriteLine("========== WEIGHT DEBUG ==========");
            Debug.WriteLine($"Assembly: {assembly.Name}");

            Debug.WriteLine("-- Assembly Instance --");
            DebugWeightParamsForElement(assembly);

            var assemblyType = doc.GetElement(assembly.GetTypeId());

            if (assemblyType != null)
            {
                Debug.WriteLine("-- Assembly Type --");
                DebugWeightParamsForElement(assemblyType);
            }

            foreach (ElementId memberId in assembly.GetMemberIds())
            {
                var member = doc.GetElement(memberId);

                if (member == null)
                    continue;

                Debug.WriteLine($"-- Member: {member.Category?.Name} / {member.Name} --");
                DebugWeightParamsForElement(member);

                var memberType = doc.GetElement(member.GetTypeId());

                if (memberType != null)
                {
                    Debug.WriteLine($"-- Member Type: {memberType.Name} --");
                    DebugWeightParamsForElement(memberType);
                }
            }

            Debug.WriteLine("========== END WEIGHT DEBUG ==========");
            Debug.WriteLine("");
        }

        private static void DebugWeightParamsForElement(Element e)
        {
            foreach (var name in WEIGHT_PARAMS)
            {
                RvtParameter p = e.LookupParameter(name);

                if (p == null)
                {
                    Debug.WriteLine($"{name} = <missing>");
                    continue;
                }

                Debug.WriteLine($"{name} = {GetParamValueForDebug(p)}");
            }
        }

        private static string GetParamValueForDebug(RvtParameter p)
        {
            if (p == null)
                return "";

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString() ?? p.AsValueString() ?? "";

                    case StorageType.Integer:
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);

                    case StorageType.Double:
                        return $"Raw:{p.AsDouble().ToString(CultureInfo.InvariantCulture)} / Display:{p.AsValueString()}";

                    case StorageType.ElementId:
                        return p.AsValueString() ?? "";

                    default:
                        return p.AsValueString() ?? "";
                }
            }
            catch
            {
                return "<error>";
            }
        }

        private static double GetAssemblyWeightKg(RvtDoc doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return 0.0;

            IList<string> weightParameterNames = GetWeightParameterNames();

            foreach (var wName in weightParameterNames)
            {
                if (TryGetDoubleAsKg(assembly, wName, out double assemblyWeightKg) && assemblyWeightKg > 0)
                    return ApplyLoadingSafetyFactor(assemblyWeightKg);
            }

            var assemblyType = doc.GetElement(assembly.GetTypeId());

            if (assemblyType != null)
            {
                foreach (var wName in weightParameterNames)
                {
                    if (TryGetDoubleAsKg(assemblyType, wName, out double assemblyTypeWeightKg) && assemblyTypeWeightKg > 0)
                        return ApplyLoadingSafetyFactor(assemblyTypeWeightKg);
                }
            }

            double totalKg = 0.0;

            var memberIds = assembly.GetMemberIds();

            if (memberIds == null || memberIds.Count == 0)
                return 0.0;

            foreach (ElementId memberId in memberIds)
            {
                var member = doc.GetElement(memberId);

                if (member == null)
                    continue;

                double memberWeightKg = 0.0;

                foreach (var wName in weightParameterNames)
                {
                    if (TryGetDoubleAsKg(member, wName, out memberWeightKg) && memberWeightKg > 0)
                        break;
                }

                if (memberWeightKg <= 0)
                {
                    var memberType = doc.GetElement(member.GetTypeId());

                    if (memberType != null)
                    {
                        foreach (var wName in weightParameterNames)
                        {
                            if (TryGetDoubleAsKg(memberType, wName, out memberWeightKg) && memberWeightKg > 0)
                                break;
                        }
                    }
                }

                if (memberWeightKg <= 0.0)
                    memberWeightKg = CalculateMemberWeightKg(doc, assembly, member);

                totalKg += memberWeightKg;
            }

            return ApplyLoadingSafetyFactor(totalKg);
        }

        private static double ApplyLoadingSafetyFactor(double baseWeightKg)
        {
            return baseWeightKg > 0.0
                ? baseWeightKg * LOADING_WEIGHT_SAFETY_FACTOR
                : 0.0;
        }

        private static double CalculateMemberWeightKg(
            RvtDoc doc,
            AssemblyInstance assembly,
            Element member)
        {
            double configuredPipeWeightKg = CalculateConfiguredDryWeightKg(doc, member);
            if (configuredPipeWeightKg > 0.0)
                return configuredPipeWeightKg;

            string assemblyMaterial = Helpers.Elements.GetMaterialGrade(doc, assembly);
            return CalculateMaterialVolumeWeightKg(doc, member, assemblyMaterial);
        }

        private static double CalculateConfiguredDryWeightKg(
            RvtDoc doc,
            Element member)
        {
            var configuredWeights = Configs.AppConfig.CurrentConfig?.ElementsWeight;
            if (doc == null || member == null || configuredWeights == null || configuredWeights.Count == 0)
                return 0.0;

            double lengthM = GetElementLengthM(member);
            int? sizeMm = GetElementSizeMm(doc, member);
            if (lengthM <= 0.0 || !sizeMm.HasValue)
                return 0.0;

            string typeText = GetElementWeightTypeText(doc, member);
            var match = configuredWeights
                .Where(item => item != null && item.Size == sizeMm.Value)
                .Where(item => WeightTypeMatches(typeText, item.PipeType))
                .OrderByDescending(item => (item.PipeType ?? "").Length)
                .FirstOrDefault();

            return match != null && match.DryWeight > 0
                ? (double)match.DryWeight * lengthM
                : 0.0;
        }

        private static double CalculateMaterialVolumeWeightKg(
            RvtDoc doc,
            Element element,
            string fallbackMaterialName)
        {
            if (doc == null || element == null || !IsWeightedComponentCategory(element))
                return 0.0;

            double totalKg = 0.0;
            try
            {
                ICollection<ElementId> materialIds = element.GetMaterialIds(false);
                foreach (ElementId materialId in materialIds ?? new List<ElementId>())
                {
                    double volumeCubicFt = element.GetMaterialVolume(materialId);
                    if (volumeCubicFt <= 0.0)
                        continue;

                    string materialName = (doc.GetElement(materialId) as Material)?.Name;
                    double densityKgPerCubicM = GetMaterialDensityKgPerCubicM(
                        materialName,
                        fallbackMaterialName);
                    totalKg += volumeCubicFt * CUBIC_FT_TO_CUBIC_M * densityKgPerCubicM;
                }
            }
            catch
            {
                // Some element classes do not expose material-volume data.
            }

            if (totalKg > 0.0)
                return totalKg;

            RvtParameter volume = element.LookupParameter("Volume");
            if (volume != null && volume.HasValue && volume.StorageType == StorageType.Double)
            {
                double density = GetMaterialDensityKgPerCubicM(
                    GetElementWeightTypeText(doc, element),
                    fallbackMaterialName);
                return volume.AsDouble() * CUBIC_FT_TO_CUBIC_M * density;
            }

            return 0.0;
        }

        private static bool IsWeightedComponentCategory(Element element)
        {
            if (element?.Category == null)
                return false;

            long categoryId = RevitApiCompatibility.GetElementIdValue(element.Category.Id);
            return categoryId == (long)BuiltInCategory.OST_PipeFitting ||
                   categoryId == (long)BuiltInCategory.OST_PipeAccessory;
        }

        private static double GetMaterialDensityKgPerCubicM(
            string materialName,
            string fallbackMaterialName)
        {
            string value = ((materialName ?? "") + " " + (fallbackMaterialName ?? ""))
                .ToUpperInvariant();

            if (value.Contains("STAINLESS")) return 8000.0;
            if (value.Contains("COPPER")) return 8960.0;
            if (value.Contains("DUCTILE") && value.Contains("IRON")) return 7100.0;
            if (value.Contains("CAST") && value.Contains("IRON")) return 7200.0;
            if (value.Contains("ALUMIN")) return 2700.0;
            if (value.Contains("STEEL") || value.Contains("CARBON")) return 7850.0;

            // Loading assemblies in this workflow are predominantly steel.
            return 7850.0;
        }

        private static double GetElementLengthM(Element element)
        {
            if (element == null)
                return 0.0;

            var locationCurve = element.Location as LocationCurve;
            if (locationCurve?.Curve != null)
                return locationCurve.Curve.Length * 0.3048;

            RvtParameter length = element.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
            return length != null && length.HasValue
                ? length.AsDouble() * 0.3048
                : 0.0;
        }

        private static int? GetElementSizeMm(RvtDoc doc, Element element)
        {
            if (doc == null || element == null)
                return null;

            RvtParameter diameter =
                element.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM) ??
                element.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);

            if (diameter != null && diameter.HasValue && diameter.StorageType == StorageType.Double)
                return (int)Math.Round(diameter.AsDouble() * FT_TO_MM);

            foreach (Element host in new[] { element, doc.GetElement(element.GetTypeId()) })
            {
                RvtParameter size = host?.LookupParameter("Size");
                if (size == null || !size.HasValue)
                    continue;

                if (size.StorageType == StorageType.Double)
                    return (int)Math.Round(size.AsDouble() * FT_TO_MM);

                string text = size.AsString() ?? size.AsValueString() ?? "";
                string number = new string(text
                    .SkipWhile(ch => !char.IsDigit(ch))
                    .TakeWhile(ch => char.IsDigit(ch) || ch == '.')
                    .ToArray());
                if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                    return (int)Math.Round(parsed);
            }

            return null;
        }

        private static string GetElementWeightTypeText(RvtDoc doc, Element element)
        {
            Element type = doc?.GetElement(element?.GetTypeId());
            var parts = new[]
            {
                GetStringParam(type, "Description"),
                GetStringParam(type, "Segment Description"),
                type?.Name,
                GetStringParam(element, "Description"),
                GetStringParam(element, "Segment Description"),
                element?.Name
            };
            return string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        }

        private static bool WeightTypeMatches(string elementTypeText, string configuredPipeType)
        {
            string elementText = NormalizeWeightType(elementTypeText);
            string configuredText = NormalizeWeightType(configuredPipeType);
            if (string.IsNullOrWhiteSpace(elementText) || string.IsNullOrWhiteSpace(configuredText))
                return false;

            string[] tokens = configuredText.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return tokens.All(token => elementText.Split(' ').Contains(token));
        }

        private static string NormalizeWeightType(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var chars = value.ToLowerInvariant()
                .Select(ch => char.IsLetterOrDigit(ch) ? ch : ' ')
                .ToArray();
            return string.Join(" ", new string(chars)
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static IList<string> GetWeightParameterNames()
        {
            var names = new List<string>();
            string configuredTotalWeight =
                Configs.AppConfig.CurrentConfig?.PipeWeightMapParameters?.TotalWeight;

            if (!string.IsNullOrWhiteSpace(configuredTotalWeight))
                names.Add(configuredTotalWeight.Trim());

            names.AddRange(WEIGHT_PARAMS);
            names.Add("Weight");
            names.Add("Assembly Weight");
            names.Add("Total Assembly Weight");

            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static PdfTable BuildMainTable(Section section)
        {
            var table = section.AddTable();
            table.Borders.Width = 0.25;
            table.Borders.Color = Colors.Gray;

            table.AddColumn(Unit.FromCentimeter(2.3));  // Package
            table.AddColumn(Unit.FromCentimeter(4.9));  // Drawing Number
            table.AddColumn(Unit.FromCentimeter(6.2));  // Material Grade
            table.AddColumn(Unit.FromCentimeter(1.2));  // Qty
            table.AddColumn(Unit.FromCentimeter(2.8));  // Weight
            table.AddColumn(Unit.FromCentimeter(2.6));  // Length

            table.AddColumn(Unit.FromCentimeter(1.6));  // CUT
            table.AddColumn(Unit.FromCentimeter(1.6));  // FAB
            table.AddColumn(Unit.FromCentimeter(1.6));  // WELD
            table.AddColumn(Unit.FromCentimeter(1.6));  // QA
            table.AddColumn(Unit.FromCentimeter(1.6));  // LOAD

            return table;
        }

        private static void AddMainHeaderRow(PdfTable table)
        {
            var hr = table.AddRow();
            hr.HeadingFormat = true;
            hr.Shading.Color = Colors.WhiteSmoke;
            hr.Format.Font.Bold = true;
            hr.VerticalAlignment = VerticalAlignment.Center;

            hr.Cells[0].AddParagraph("Package");
            hr.Cells[1].AddParagraph("Drawing Number");
            hr.Cells[2].AddParagraph("Material Grade");
            hr.Cells[3].AddParagraph("Qty");
            hr.Cells[4].AddParagraph("Weight (kg)*");
            hr.Cells[5].AddParagraph("Length (mm)");

            hr.Cells[3].Format.Alignment = ParagraphAlignment.Center;
            hr.Cells[4].Format.Alignment = ParagraphAlignment.Center;
            hr.Cells[5].Format.Alignment = ParagraphAlignment.Center;

            hr.Cells[6].AddParagraph("CUT");
            hr.Cells[7].AddParagraph("FAB");
            hr.Cells[8].AddParagraph("WELD");
            hr.Cells[9].AddParagraph("QA");
            hr.Cells[10].AddParagraph("LOAD");

            for (int i = 6; i <= 10; i++)
                hr.Cells[i].Format.Alignment = ParagraphAlignment.Center;
        }

        private static void AddCheckboxCell(PdfCell cell)
        {
            var p = cell.AddParagraph("□");
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;
            p.Format.Font.Name = "Arial";
        }

        private static void AddTotalBlock(
            Section section,
            int totalQty,
            double totalWeightKg,
            double totalLenMm)
        {
            var t = section.AddTable();
            t.Borders.Visible = false;

            t.AddColumn(Unit.FromCentimeter(2.8));
            t.AddColumn(Unit.FromCentimeter(1.4));
            t.AddColumn(Unit.FromCentimeter(3.0));
            t.AddColumn(Unit.FromCentimeter(2.8));

            var r = t.AddRow();
            r.VerticalAlignment = VerticalAlignment.Center;

            var c0 = r.Cells[0];
            c0.AddParagraph("TOTAL:");
            c0.Format.Font.Bold = true;
            c0.Format.Font.Color = Colors.Red;
            c0.Format.Alignment = ParagraphAlignment.Right;

            AddTotalBox(r.Cells[1], totalQty.ToString(CultureInfo.InvariantCulture));
            AddTotalBox(r.Cells[2], FormatWeight(totalWeightKg));
            AddTotalBox(r.Cells[3], FormatLengthMm(totalLenMm));
        }

        private static void AddTotalBox(PdfCell cell, string text)
        {
            cell.Borders.Visible = true;
            cell.Borders.Width = 0.75;
            cell.Borders.Color = Colors.Gray;

            cell.Format.Font.Bold = true;
            cell.Format.Font.Color = Colors.Red;
            cell.Format.Alignment = ParagraphAlignment.Center;

            var p = cell.AddParagraph(text);
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;
        }

        private static string FormatWeight(double kg)
        {
            return Math.Round(kg, 1)
                .ToString("0.0", CultureInfo.InvariantCulture)
                .TrimEnd('0')
                .TrimEnd('.');
        }

        private static string FormatLengthMm(double mm)
        {
            return Math.Round(mm, 0).ToString("0", CultureInfo.InvariantCulture);
        }

        private static void AddFooterWithNoteAndPagination(Section section, ProcurementConfig config)
        {
            var note = "* The assembly weight is approximate and includes a 10% safety factor.";

            BuildFooter(section.Footers.Primary, note);
            BuildFooter(section.Footers.EvenPage, note);
        }

        private static void BuildFooter(PdfHeaderFooter footer, string note)
        {
            var tbl = footer.AddTable();
            tbl.Borders.Visible = false;

            tbl.AddColumn(Unit.FromCentimeter(18.0));
            tbl.AddColumn(Unit.FromCentimeter(9.7));

            var r = tbl.AddRow();

            var n = r.Cells[0].AddParagraph(note);
            n.Format.Font.Name = "Arial";
            n.Format.Font.Size = 9;
            n.Format.Font.Color = Colors.DimGray;
            n.Format.Alignment = ParagraphAlignment.Left;

            var p = r.Cells[1].AddParagraph();
            p.Format.Font.Name = "Arial";
            p.Format.Font.Size = 9;
            p.Format.Font.Color = Colors.DimGray;
            p.Format.Alignment = ParagraphAlignment.Right;

            p.AddText("Page ");
            p.AddPageField();
            p.AddText(" of ");
            p.AddNumPagesField();
        }

        private static void DrawHeader(Section section, ProcurementConfig cfg, string title, string dateText)
        {
            var themeGreen = MigraColor.FromRgb(60, 130, 60);
            var themeGreenLight = MigraColor.FromRgb(232, 243, 232);
            var themeText = MigraColor.FromRgb(20, 60, 85);
            var themeLine = MigraColor.FromRgb(120, 170, 120);

            const double USABLE_WIDTH_CM = 27.7;

            var bandOuter = section.AddTable();
            bandOuter.Borders.Visible = false;
            bandOuter.AddColumn(Unit.FromCentimeter(USABLE_WIDTH_CM));

            var bandRow = bandOuter.AddRow();
            bandRow.Height = Unit.FromCentimeter(4.3);
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

            band.AddColumn(Unit.FromCentimeter(0.8));
            band.AddColumn(Unit.FromCentimeter(5.2));
            band.AddColumn(Unit.FromCentimeter(13.5));
            band.AddColumn(Unit.FromCentimeter(0.25));
            band.AddColumn(Unit.FromCentimeter(7.2));
            band.AddColumn(Unit.FromCentimeter(0.75));

            var row = band.AddRow();
            row.Height = Unit.FromCentimeter(4.3);
            row.TopPadding = Unit.FromCentimeter(0.20);
            row.BottomPadding = Unit.FromCentimeter(0.15);

            var logoCell = row.Cells[1];
            logoCell.VerticalAlignment = VerticalAlignment.Top;

            var logos = logoCell.Elements.AddTable();
            logos.Borders.Visible = false;
            logos.AddColumn(Unit.FromCentimeter(5.2));

            var lr1 = logos.AddRow();
            lr1.BottomPadding = Unit.FromCentimeter(0.20);
            var lr2 = logos.AddRow();

            AddLogoFixedBox(lr1.Cells[0], cfg.CompanyLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));
            AddLogoFixedBox(lr2.Cells[0], cfg.ClientLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));

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
            titleP.Format.SpaceAfter = Unit.FromCentimeter(0.15);
            titleP.Format.Alignment = ParagraphAlignment.Left;
            titleP.Format.KeepTogether = true;

            var underline = titleCell.AddParagraph("\u00A0");
            underline.Format.Font.Size = 1;
            underline.Format.SpaceBefore = 0;
            underline.Format.SpaceAfter = Unit.FromCentimeter(0.25);
            underline.Format.Borders.Bottom.Visible = true;
            underline.Format.Borders.Bottom.Width = 1.5;
            underline.Format.Borders.Bottom.Color = themeGreen;

            var dividerCell = row.Cells[3];
            dividerCell.VerticalAlignment = VerticalAlignment.Top;
            dividerCell.Borders.Left.Visible = true;
            dividerCell.Borders.Left.Width = 1.0;
            dividerCell.Borders.Left.Color = themeLine;

            var infoCell = row.Cells[4];
            infoCell.VerticalAlignment = VerticalAlignment.Top;

            const double INFO_WIDTH_CM = 7.2;

            var infoT = infoCell.Elements.AddTable();
            infoT.Borders.Visible = false;
            infoT.AddColumn(Unit.FromCentimeter(INFO_WIDTH_CM));

            var hText = Unit.FromCentimeter(1.05);
            var hLine = Unit.FromCentimeter(0.20);

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
                valBold: true);

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
                valBold: true);

            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.35);
        }

        private static void AddLogoFixedBox(PdfCell cell, string path, Unit boxWidth, Unit boxHeight)
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
            return cm / 2.54 * 72.0;
        }

        private static double FitFontSizeToWidth(
            string text,
            double maxWidthCm,
            double baseSizePt,
            double minSizePt)
        {
            if (string.IsNullOrWhiteSpace(text))
                return baseSizePt;

            const double avgCharWidthFactor = 0.62;
            double safeWidthCm = Math.Max(0.0, maxWidthCm - 0.25);
            double maxWidthPt = CmToPoints(safeWidthCm);

            int charCount = text.Length;

            if (charCount <= 0)
                return baseSizePt;

            double required = maxWidthPt / (charCount * avgCharWidthFactor);

            if (required > baseSizePt)
                return baseSizePt;

            if (required < minSizePt)
                return minSizePt;

            return required;
        }

        private static void AddSingleLineKeyValue(
            PdfCell cell,
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

        private static string GetStringParamInstanceOrType(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return "";

            RvtParameter pInst = e.LookupParameter(paramName);
            string vInst = pInst?.AsString() ?? pInst?.AsValueString();

            if (!string.IsNullOrWhiteSpace(vInst))
                return vInst;

            var typeId = e.GetTypeId();

            if (!RevitApiCompatibility.IsInvalidElementId(typeId))
            {
                var typeElem = e.Document.GetElement(typeId);
                RvtParameter pType = typeElem?.LookupParameter(paramName);
                string vType = pType?.AsString() ?? pType?.AsValueString();

                if (!string.IsNullOrWhiteSpace(vType))
                    return vType;
            }

            return "";
        }

        private static string GetStringParam(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return "";

            RvtParameter p = e.LookupParameter(paramName);

            if (p == null)
                return "";

            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? "";

                case StorageType.Integer:
                    return p.AsInteger().ToString(CultureInfo.InvariantCulture);

                case StorageType.Double:
                    return p.AsValueString() ?? "";

                case StorageType.ElementId:
                    var id = p.AsElementId();

                    return id == null
                        ? ""
                        : RevitApiCompatibility.GetElementIdValue(id).ToString(CultureInfo.InvariantCulture);

                default:
                    return "";
            }
        }

        private static bool TryGetDoubleAsKg(Element e, string paramName, out double kg)
        {
            kg = 0.0;

            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return false;

            if (TryGetDoubleFromParam(e.LookupParameter(paramName), out double v1))
            {
                kg = v1;
                return true;
            }

            var typeId = e.GetTypeId();

            if (!RevitApiCompatibility.IsInvalidElementId(typeId))
            {
                var typeElem = e.Document.GetElement(typeId);

                if (TryGetDoubleFromParam(typeElem?.LookupParameter(paramName), out double v2))
                {
                    kg = v2;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetLengthMm(Element e, string paramName, out double mm)
        {
            mm = 0.0;

            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return false;

            if (TryGetLengthFromParam(e.LookupParameter(paramName), out double mm1))
            {
                mm = mm1;
                return true;
            }

            var typeId = e.GetTypeId();

            if (!RevitApiCompatibility.IsInvalidElementId(typeId))
            {
                var typeElem = e.Document.GetElement(typeId);

                if (TryGetLengthFromParam(typeElem?.LookupParameter(paramName), out double mm2))
                {
                    mm = mm2;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetDoubleFromParam(RvtParameter p, out double val)
        {
            val = 0.0;

            if (p == null)
                return false;

            if (p.StorageType == StorageType.Double)
            {
                val = p.AsDouble();
                return true;
            }

            string s = p.AsString() ?? p.AsValueString();

            if (double.TryParse(KeepDigitsDotMinus(s), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                val = n;
                return true;
            }

            return false;
        }

        private static bool TryGetLengthFromParam(RvtParameter p, out double mm)
        {
            mm = 0.0;

            if (p == null)
                return false;

            if (p.StorageType == StorageType.Double)
            {
                mm = p.AsDouble() * FT_TO_MM;
                return true;
            }

            string s = p.AsString() ?? p.AsValueString();

            if (double.TryParse(KeepDigitsDotMinus(s), NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
            {
                mm = n;
                return true;
            }

            return false;
        }

        private static string KeepDigitsDotMinus(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return "";

            return new string(s.Where(ch => char.IsDigit(ch) || ch == '.' || ch == '-').ToArray());
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

            var h2 = doc.Styles.AddStyle("H2", "Normal");
            h2.Font.Size = 12;
            h2.Font.Bold = true;
            h2.Font.Color = Colors.Black;
        }
    }
}
