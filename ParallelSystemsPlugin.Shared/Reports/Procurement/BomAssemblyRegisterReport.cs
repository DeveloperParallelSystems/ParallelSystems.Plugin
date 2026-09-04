using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Compatibility;

// MigraDoc / PdfSharp (1.50 line recommended for Revit)
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

// ===== Aliases to remove ambiguity =====
using RvtDoc = Autodesk.Revit.DB.Document;
using PdfDoc = MigraDoc.DocumentObjectModel.Document;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
using System.Windows.Controls;
using System.Diagnostics;
using ParallelSystemsPlugin.Models;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomAssemblyRegisterReport
    {
        // ==== REQUIRED PARAMS ====
        private const string PARAM_PACKAGE = "Vic_Package";
        private const string PARAM_MATERIAL_GRADE = "Segment Description";
        private const string PARAM_ASSEMBLY_NUMBER = "Assembly Number";
        private const string PARAM_FRAME_AREA = "Vic_Area_PT";
        private const string PARAM_FRAME_NUMBER = "FRAME NO";

        public static void Generate(RvtDoc revitDoc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();
            if (revitDoc == null) throw new ArgumentNullException(nameof(revitDoc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("Target Folder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "BOM-ASSEMBLY REGISTER.pdf");

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(revitDoc);

            var siteMeasureNames = siteMeasureAssemblies
            .Select(x => x.AssemblyName)
            .ToHashSet();

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;

            // Active view scope
            ElementId viewId = revitDoc.ActiveView.Id;

            // Assembly instances visible in Active View
            var assembliesInView = new FilteredElementCollector(revitDoc, viewId)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .ToList();

            var frameNumbers = assembliesInView
                .Where(a => IsFrameAssembly(revitDoc, a))
                .Select(a => GetFrameNumber(revitDoc, a))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(x => x.Length)
                .ToList();

            var raw = assembliesInView
                .Where(a => !IsFrameAssembly(revitDoc, a))
                .Select(a =>
                {
                    string package =
                        Helpers.Elements.GetProcurementPackageNameFromAssembly(a);

                    string assemblyNo = Helpers.Elements.GetStringParam(a, PARAM_ASSEMBLY_NUMBER);
                    if (string.IsNullOrWhiteSpace(assemblyNo))
                        assemblyNo = a.Name ?? "";

                    string materialGrade = GetMaterialGrade(revitDoc, a);

                    return new RawRow
                    {
                        Package = package ?? "",
                        FrameNumber = ResolveAssemblyFrameNumber(
                            revitDoc,
                            a,
                            frameNumbers,
                            assemblyNo,
                            package),
                        AssemblyNumber = assemblyNo ?? "",
                        MaterialGrade = materialGrade ?? ""
                    };
                })
                .Where(r => !string.IsNullOrWhiteSpace(
                    r.AssemblyNumber) 
                    && (includeSiteMeasureAssemblies || !siteMeasureNames.Contains(r.AssemblyNumber))
                )
                .ToList();

            // Qty per (Package + AssemblyNumber + MaterialGrade)
            var grouped = raw
                .GroupBy(r => new GroupKey(r.Package, r.FrameNumber, r.AssemblyNumber, r.MaterialGrade), new GroupKeyComparer())
                .Select(g => new ReportRow
                {
                    Package = g.Key.Package,
                    AssemblyNumber = g.Key.AssemblyNumber,
                    MaterialGrade = g.Key.MaterialGrade,
                    Qty = g.Count(),
                    FrameNumber = g.Key.FrameNumber
                })
                .OrderBy(r => r.Package)
                .ThenBy(r => r.AssemblyNumber)
                .ToList();

            PdfDoc pdf = BuildPdf(cfg, grouped);
            if (!cfg.ExportReportsToExcel)
                SavePdf(pdf, outPath);

            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, grouped);
        }

        private static string GetPackageName(RvtDoc doc, AssemblyInstance a)
        {
            if (doc == null || a == null)
                return "";

            // 1. Assembly instance
            string package = GetResolvedPackageParamValue(doc, a, PARAM_PACKAGE);
            if (!string.IsNullOrWhiteSpace(package))
                return package;

            // 2. Assembly type
            var assemblyType = doc.GetElement(a.GetTypeId());
            if (assemblyType != null)
            {
                package = GetResolvedPackageParamValue(doc, assemblyType, PARAM_PACKAGE);
                if (!string.IsNullOrWhiteSpace(package))
                    return package;
            }

            // 3. Members
            var memberIds = a.GetMemberIds();
            if (memberIds != null && memberIds.Count > 0)
            {
                foreach (ElementId memberId in memberIds)
                {
                    var member = doc.GetElement(memberId);
                    if (member == null) continue;

                    package = GetResolvedPackageParamValue(doc, member, PARAM_PACKAGE);
                    if (!string.IsNullOrWhiteSpace(package))
                        return package;
                }

                // 4. Member types
                foreach (ElementId memberId in memberIds)
                {
                    var member = doc.GetElement(memberId);
                    if (member == null) continue;

                    var memberType = doc.GetElement(member.GetTypeId());
                    if (memberType == null) continue;

                    package = GetResolvedPackageParamValue(doc, memberType, PARAM_PACKAGE);
                    if (!string.IsNullOrWhiteSpace(package))
                        return package;
                }
            }

            return "";
        }

        private static string GetResolvedPackageParamValue(RvtDoc doc, Element e, string paramName)
        {
            if (doc == null || e == null || string.IsNullOrWhiteSpace(paramName))
                return "";

            var p = e.LookupParameter(paramName);
            if (p == null)
                return "";

            string raw = "";

            switch (p.StorageType)
            {
                case StorageType.String:
                    raw = p.AsString() ?? "";
                    break;

                case StorageType.Integer:
                    raw = p.AsInteger().ToString(CultureInfo.InvariantCulture);
                    break;

                case StorageType.Double:
                    raw = p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);
                    break;

                case StorageType.ElementId:
                    var id = p.AsElementId();
                    raw = GetElementIdText(id);
                    break;

                default:
                    raw = "";
                    break;
            }

            raw = (raw ?? "").Trim();

            if (string.IsNullOrWhiteSpace(raw))
                return "";

            // Victaulic package value is showing like: "2141493;"
            // So extract the first numeric id.
            string idText = raw
                .Split(new[] { ';', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

            if (long.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long longId))
            {
                ElementId packageElementId = RevitApiCompatibility.CreateElementId(longId);
                Element packageElement = doc.GetElement(packageElementId);

                if (packageElement != null)
                {
                    // Most likely this is the real package name you want.
                    if (!string.IsNullOrWhiteSpace(packageElement.Name))
                        return packageElement.Name;

                    // Fallbacks
                    string name = GetStringParam(packageElement, "Name");
                    if (!string.IsNullOrWhiteSpace(name))
                        return name;

                    string number = GetStringParam(packageElement, "Number");
                    if (!string.IsNullOrWhiteSpace(number))
                        return number;
                }
            }

            // If it cannot be resolved, return raw value for debugging instead of blank.
            return raw;
        }

        private static void DebugAssemblyParams(RvtDoc doc, AssemblyInstance a)
        {
            if (doc == null || a == null)
                return;

            Debug.WriteLine("");
            Debug.WriteLine("==================================================");
            Debug.WriteLine(" DEBUG ASSEMBLY PARAMETER CHECK");
            Debug.WriteLine("==================================================");

            Debug.WriteLine($"Assembly Id   : {GetElementIdText(a.Id)}");
            Debug.WriteLine($"Assembly Name : {a.Name}");
            Debug.WriteLine($"Assembly Type : {GetElementName(doc, a.GetTypeId())}");

            Debug.WriteLine("");
            Debug.WriteLine("===== ASSEMBLY INSTANCE PARAMS =====");
            DebugElementParams(a);

            var assemblyType = doc.GetElement(a.GetTypeId());
            if (assemblyType != null)
            {
                Debug.WriteLine("");
                Debug.WriteLine("===== ASSEMBLY TYPE PARAMS =====");
                DebugElementParams(assemblyType);
            }

            var memberIds = a.GetMemberIds();

            if (memberIds == null || memberIds.Count == 0)
            {
                Debug.WriteLine("");
                Debug.WriteLine("No assembly members found.");
                return;
            }

            Debug.WriteLine("");
            Debug.WriteLine("===== ASSEMBLY MEMBERS =====");

            foreach (ElementId memberId in memberIds)
            {
                var member = doc.GetElement(memberId);
                if (member == null)
                    continue;

                Debug.WriteLine("");
                Debug.WriteLine("------------------------------------------");
                Debug.WriteLine($"MEMBER INSTANCE");
                Debug.WriteLine($"Id       : {GetElementIdText(member.Id)}");
                Debug.WriteLine($"Name     : {member.Name}");
                Debug.WriteLine($"Category : {member.Category?.Name ?? ""}");
                Debug.WriteLine($"Type     : {GetElementName(doc, member.GetTypeId())}");
                Debug.WriteLine("------------------------------------------");

                DebugElementParams(member);

                var memberType = doc.GetElement(member.GetTypeId());
                if (memberType != null)
                {
                    Debug.WriteLine("");
                    Debug.WriteLine($"MEMBER TYPE PARAMS: {memberType.Name}");
                    Debug.WriteLine("------------------------------------------");

                    DebugElementParams(memberType);
                }
            }

            Debug.WriteLine("");
            Debug.WriteLine("==================================================");
            Debug.WriteLine(" END DEBUG ASSEMBLY PARAMETER CHECK");
            Debug.WriteLine("==================================================");
            Debug.WriteLine("");
        }

        private static void DebugElementParams(Element e)
        {
            if (e == null)
                return;

            var paramLines = new List<string>();

            foreach (Parameter p in e.Parameters)
            {
                if (p?.Definition == null)
                    continue;

                string name = p.Definition.Name ?? "";
                string value = GetParamValueAsString(p);

                paramLines.Add($"{name} = {value}");
            }

            foreach (string line in paramLines.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                Debug.WriteLine(line);
            }
        }

        private static string GetParamValueAsString(Parameter p)
        {
            if (p == null)
                return "";

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString() ?? "";

                    case StorageType.Integer:
                        return p.AsInteger().ToString(CultureInfo.InvariantCulture);

                    case StorageType.Double:
                        return p.AsValueString()
                            ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);

                    case StorageType.ElementId:
                        var id = p.AsElementId();

                        return p.AsValueString()
                            ?? GetElementIdText(id);

                    default:
                        return "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static string GetElementIdText(ElementId id)
        {
            if (id == null)
                return "";

            return RevitApiCompatibility
                .GetElementIdValue(id)
                .ToString(CultureInfo.InvariantCulture);
        }

        private static string GetElementName(RvtDoc doc, ElementId id)
        {
            if (doc == null || id == null || id == ElementId.InvalidElementId)
                return "";

            var e = doc.GetElement(id);
            return e?.Name ?? "";
        }

        private static void ExportExcel(ProcurementConfig cfg, List<ReportRow> rows)
        {
            string note = "";
            bool groupByFrame = rows.Any(r => !string.IsNullOrWhiteSpace(r.FrameNumber));

            var worksheets = new List<ParallelSystemsPlugin.Helpers.ExcelReportExporter.ExcelWorksheet>
            {
                BuildAssemblyRegisterExcelSheet(cfg, rows, groupByFrame, note, null)
            };

            if (!groupByFrame)
            {
                var packageGroups = rows
                    .GroupBy(r => r.Package ?? "", StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => string.IsNullOrWhiteSpace(g.Key) ? 1 : 0)
                    .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (packageGroups.Count > 1)
                {
                    foreach (var packageGroup in packageGroups)
                    {
                        worksheets.Add(BuildAssemblyRegisterExcelSheet(
                            cfg,
                            packageGroup.ToList(),
                            false,
                            note,
                            ParallelSystemsPlugin.Helpers.ExcelReportExporter.GetPackageWorksheetName(packageGroup.Key)));
                    }
                }
            }

            ParallelSystemsPlugin.Helpers.ExcelReportExporter.SaveWorkbook(
                ParallelSystemsPlugin.Helpers.ExcelReportExporter.BuildOutputPath(cfg, "BOM-ASSEMBLY REGISTER"),
                worksheets);
        }

        private static ParallelSystemsPlugin.Helpers.ExcelReportExporter.ExcelWorksheet BuildAssemblyRegisterExcelSheet(
            ProcurementConfig cfg,
            List<ReportRow> rows,
            bool groupByFrame,
            string note,
            string worksheetName)
        {
            bool isMasterList = string.IsNullOrWhiteSpace(worksheetName) &&
                !groupByFrame &&
                rows.Select(r => r.Package ?? "")
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Skip(1)
                    .Any();

            var sheet = ParallelSystemsPlugin.Helpers.ExcelReportExporter.CreateReportSheet(
                cfg,
                "Assembly Register",
                new[]
                {
            groupByFrame ? "Frame-No" : "Package Name",
            "Drawing Number",
            "Qty",
            "Material Grade",
            "Comment/Approval"
                },
                note);

            sheet.CenterColumns(3);
            if (!string.IsNullOrWhiteSpace(worksheetName))
                sheet.Name = worksheetName;

            bool alt = false;

            if (isMasterList)
            {
                foreach (var r in rows
                    .OrderBy(x => x.AssemblyNumber ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Package ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    sheet.Add(
                        alt
                            ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData
                            : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                        GetDisplayGroup(r, false),
                        r.AssemblyNumber ?? "",
                        r.Qty,
                        r.MaterialGrade ?? "",
                        "");

                    alt = !alt;
                }

                if (!string.IsNullOrWhiteSpace(note))
                {
                    sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                    sheet.Add(
                        ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Note,
                        note,
                        "",
                        "",
                        "",
                        "");
                }

                return sheet;
            }

            var groupedByPackage = rows
                .GroupBy(r => GetDisplayGroup(r, groupByFrame))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            foreach (var pkgGroup in groupedByPackage)
            {
                string packageName = pkgGroup.Key ?? "";

                // Match PDF package group row:
                // Package name only in first column, rest blank.
                sheet.Add(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Group,
                    packageName,
                    "",
                    "",
                    "",
                    "");

                foreach (var r in pkgGroup.OrderBy(x => x.AssemblyNumber ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    sheet.Add(
                        alt
                            ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData
                            : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                        "",                         // Package Name blank; package is shown in group row
                        r.AssemblyNumber ?? "",     // Drawing Number
                        r.Qty,                       // Qty
                        r.MaterialGrade ?? "",      // Material Grade
                        ""                          // Comment/Approval
                    );

                    alt = !alt;
                }
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                sheet.Add(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Note,
                    note,
                    "",
                    "",
                    "",
                    "");
            }

            return sheet;
        }

        // ========================= PDF Rendering =========================

        private static PdfDoc BuildPdf(ProcurementConfig cfg, List<ReportRow> rows)
        {
            bool groupByFrame = rows.Any(r => !string.IsNullOrWhiteSpace(r.FrameNumber));
            // Theme colors (company-style green header like your reference)
            var themeGreen = MigraColor.FromRgb(60, 130, 60);        // primary green
            var themeGreenLight = MigraColor.FromRgb(232, 243, 232); // light band fill
            var themeText = MigraColor.FromRgb(20, 60, 85);          // dark blue/teal for title
            var themeLine = MigraColor.FromRgb(120, 170, 120);       // thin rule lines

            var culture = new CultureInfo("en-US");
            DateTime dt = (cfg.Date == default(DateTime)) ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            var doc = new PdfDoc();
            doc.Info.Title = "BOM-ASSEMBLY REGISTER";

            DefineStyles(doc);

            var section = doc.AddSection();
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.Orientation = MigraDoc.DocumentObjectModel.Orientation.Landscape;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.0);

            // Footer page numbering
            var footer = section.Footers.Primary.AddParagraph();
            footer.Format.Alignment = ParagraphAlignment.Center;
            footer.Format.Font.Size = 9;
            footer.AddText("Page ");
            footer.AddPageField();
            footer.AddText(" of ");
            footer.AddNumPagesField();

            // ===================== HEADER (PROFESSIONAL THEME) =====================
            {
                // Outer band (background + top/bottom green rules)
                var bandOuter = section.AddTable();
                bandOuter.Borders.Visible = false;
                bandOuter.AddColumn(Unit.FromCentimeter(28.0)); // usable width

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
                band.AddColumn(Unit.FromCentimeter(0.6));
                band.AddColumn(Unit.FromCentimeter(4.6));   // logos
                band.AddColumn(Unit.FromCentimeter(8.2));   // title (slightly smaller)
                band.AddColumn(Unit.FromCentimeter(0.2));   // vertical divider
                band.AddColumn(Unit.FromCentimeter(4.8));   // right info (more room)
                band.AddColumn(Unit.FromCentimeter(0.6));


                var row = band.AddRow();
                row.Height = Unit.FromCentimeter(4.3);
                row.TopPadding = Unit.FromCentimeter(0.20);
                row.BottomPadding = Unit.FromCentimeter(0.15);

                // ----- Logos (left) -----
                var logoCell = row.Cells[1];
                logoCell.VerticalAlignment = VerticalAlignment.Top;

                var logos = logoCell.Elements.AddTable();
                logos.Borders.Visible = false;
                logos.AddColumn(Unit.FromCentimeter(4.6));

                var lr1 = logos.AddRow();
                lr1.BottomPadding = Unit.FromCentimeter(0.20);

                var lr2 = logos.AddRow();

                // Smaller width (as requested)
                AddLogoFixedBox(lr1.Cells[0], cfg.CompanyLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));
                AddLogoFixedBox(lr2.Cells[0], cfg.ClientLogoPath, Unit.FromCentimeter(3.0), Unit.FromCentimeter(1.25));

                // ----- Title (center-left) -----
                var titleCell = row.Cells[2];
                titleCell.VerticalAlignment = VerticalAlignment.Top;

                string titleText = "Assembly Register";

                // Auto-fit title so it never wraps (title column is 9.2 cm based on your layout)
                double titleFont = FitFontSizeToWidth(titleText, 8.2, 30, 20);

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

                // ----- Right info block (FORCED SINGLE LINE FOR EACH ITEM) -----
                // ----- Right info block (UNIFORM HEIGHT ROWS) -----
                var infoCell = row.Cells[4];
                infoCell.VerticalAlignment = VerticalAlignment.Top;

                const double INFO_WIDTH_CM = 4.8;

                // Build a small 1-column table so each block has the SAME height
                var infoT = infoCell.Elements.AddTable();
                infoT.Borders.Visible = false;
                infoT.AddColumn(Unit.FromCentimeter(INFO_WIDTH_CM));

                // row heights (tweak these if you want even tighter)
                var hText = Unit.FromCentimeter(1.05);
                var hLine = Unit.FromCentimeter(0.20);

                // --- Date row ---
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

                // --- line 1 ---
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

                // --- Job Number row ---
                var rJN = infoT.AddRow();
                rJN.Height = hText;
                rJN.VerticalAlignment = VerticalAlignment.Center;
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

                // --- line 2 ---
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

                // --- Job Name row ---
                var rName = infoT.AddRow();
                rName.Height = hText;
                rName.VerticalAlignment = VerticalAlignment.Center;
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

            }

            // Spacing below header
            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.35);

            // ===================== DATA TABLE (REFERENCE STYLE) =====================
            var table = section.AddTable();
            table.Style = "Table";

            // No outer box
            table.Borders.Visible = false;

            // Columns
            table.AddColumn(Unit.FromCentimeter(6.0));  // Package
            table.AddColumn(Unit.FromCentimeter(6.0));  // Assembly Number*
            table.AddColumn(Unit.FromCentimeter(1.0));  // Qty
            table.AddColumn(Unit.FromCentimeter(7.5));  // Material Grade
            table.AddColumn(Unit.FromCentimeter(7.5));  // Comment/Approval

            // ---------- Column header row: underline only ----------
            var colHead = table.AddRow();
            colHead.HeadingFormat = true;
            colHead.Format.Font.Bold = true;
            colHead.TopPadding = Unit.FromPoint(4);
            colHead.BottomPadding = Unit.FromPoint(6);

            for (int i = 0; i < 5; i++)
            {
                colHead.Cells[i].Borders.Bottom.Visible = true;
                colHead.Cells[i].Borders.Bottom.Width = 0.75;
                colHead.Cells[i].Borders.Bottom.Color = Colors.Gray;

                colHead.Cells[i].Borders.Left.Visible = false;
                colHead.Cells[i].Borders.Right.Visible = false;
                colHead.Cells[i].Borders.Top.Visible = false;
            }

            colHead.Cells[0].AddParagraph(groupByFrame ? "Frame-No" : "Package Name");
            colHead.Cells[1].AddParagraph("Drawing Number");
            colHead.Cells[2].AddParagraph("Qty");
            colHead.Cells[3].AddParagraph("Material Grade");
            colHead.Cells[4].AddParagraph("Comment/Approval");

            colHead.Cells[0].Format.Alignment = ParagraphAlignment.Left;
            colHead.Cells[1].Format.Alignment = ParagraphAlignment.Left;
            colHead.Cells[2].Format.Alignment = ParagraphAlignment.Left;
            colHead.Cells[3].Format.Alignment = ParagraphAlignment.Left;
            colHead.Cells[4].Format.Alignment = ParagraphAlignment.Left;

            // ---------- Rows: group by Package, insert package header row ----------
            var groupedByPackage = rows
                .GroupBy(r => GetDisplayGroup(r, groupByFrame))
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

            bool alt = false;

            foreach (var pkgGroup in groupedByPackage)
            {
                string pkg = pkgGroup.Key ?? "";

                // Package group header row
                var pkgRow = table.AddRow();
                pkgRow.TopPadding = Unit.FromPoint(6);
                pkgRow.BottomPadding = Unit.FromPoint(4);

                // subtle separation line under package header (only first cell)
                pkgRow.Cells[0].Borders.Bottom.Visible = true;
                pkgRow.Cells[0].Borders.Bottom.Width = 0.5;
                pkgRow.Cells[0].Borders.Bottom.Color = Colors.LightGray;

                for (int i = 0; i < 5; i++)
                {
                    pkgRow.Cells[i].Borders.Left.Visible = false;
                    pkgRow.Cells[i].Borders.Right.Visible = false;
                    pkgRow.Cells[i].Borders.Top.Visible = false;

                    if (i != 0)
                        pkgRow.Cells[i].Borders.Bottom.Visible = false;
                }

                var pkgP = pkgRow.Cells[0].AddParagraph(pkg);
                pkgP.Format.Font.Bold = false;
                pkgP.Format.Font.Color = Colors.Black;

                pkgRow.Cells[1].AddParagraph("");
                pkgRow.Cells[2].AddParagraph("");
                pkgRow.Cells[3].AddParagraph("");
                pkgRow.Cells[4].AddParagraph("");

                // data rows for this package
                foreach (var r in pkgGroup.OrderBy(x => x.AssemblyNumber ?? "", StringComparer.OrdinalIgnoreCase))
                {
                    var row = table.AddRow();
                    row.TopPadding = Unit.FromPoint(3);
                    row.BottomPadding = Unit.FromPoint(3);

                    row.Shading.Color = alt ? MigraColor.FromRgb(240, 240, 240) : Colors.White;
                    alt = !alt;

                    // light row underline
                    for (int i = 0; i < 5; i++)
                    {
                        row.Cells[i].Borders.Left.Visible = false;
                        row.Cells[i].Borders.Right.Visible = false;
                        row.Cells[i].Borders.Top.Visible = false;

                        row.Cells[i].Borders.Bottom.Visible = true;
                        row.Cells[i].Borders.Bottom.Width = 0.5;
                        row.Cells[i].Borders.Bottom.Color = Colors.LightGray;
                    }

                    // Package column blank for items (package displayed above)
                    row.Cells[0].AddParagraph("");

                    row.Cells[1].AddParagraph(r.AssemblyNumber ?? "");

                    row.Cells[2].AddParagraph(r.Qty.ToString());
                    row.Cells[2].Format.Alignment = ParagraphAlignment.Center;

                    row.Cells[3].AddParagraph(r.MaterialGrade ?? "");
                    row.Cells[4].AddParagraph("");
                }
            }

            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.5);


            return doc;
        }

        private static void DefineStyles(PdfDoc doc)
        {
            var normal = doc.Styles["Normal"];
            normal.Font.Name = "Arial";
            normal.Font.Size = 10;

            // Keep Title style (header overrides it anyway)
            var title = doc.Styles.AddStyle("Title", "Normal");
            title.Font.Size = 18;
            title.Font.Bold = true;
            title.Font.Color = MigraColor.FromRgb(60, 80, 100);

            var table = doc.Styles.AddStyle("Table", "Normal");
            table.Font.Size = 10;
        }

        // Logos: fixed box sizing (stable, no spacers)
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

        // Save PDF (MigraDoc/PdfSharp 1.50 compatible)
        private static void SavePdf(PdfDoc doc, string outPath)
        {
            var renderer = new PdfDocumentRenderer()
            {
                Document = doc
            };
            renderer.RenderDocument();
            renderer.PdfDocument.Save(outPath);
        }

        // ========================= Param helpers =========================

        private static string GetStringParam(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName)) return "";

            var p = e.LookupParameter(paramName);
            if (p == null) return "";

            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.AsString() ?? "";

                case StorageType.Integer:
                    return p.AsInteger().ToString();

                case StorageType.Double:
                    return p.AsValueString() ?? "";

                case StorageType.ElementId:
                    // Revit 2024: IntegerValue is obsolete; use Value
                    var id = p.AsElementId();
                    return id == null
                        ? ""
                        : RevitApiCompatibility.GetElementIdValue(id).ToString(CultureInfo.InvariantCulture);

                default:
                    return "";
            }
        }

        private static string GetMaterialGrade(RvtDoc doc, AssemblyInstance a)
        {
            // Fix for missing grades: scan ALL members + member types (not just the first member)

            // (1) assembly instance
            var v = GetStringParam(a, PARAM_MATERIAL_GRADE);
            if (!string.IsNullOrWhiteSpace(v)) return v;

            // (2) assembly type
            var at = doc.GetElement(a.GetTypeId());
            if (at != null)
            {
                v = GetStringParam(at, PARAM_MATERIAL_GRADE);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }

            // (3) all members (instance param)
            var members = a.GetMemberIds();
            if (members != null && members.Count > 0)
            {
                foreach (var mid in members)
                {
                    var m = doc.GetElement(mid);
                    if (m == null) continue;

                    v = GetStringParam(m, PARAM_MATERIAL_GRADE);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }

                // (4) all member types
                foreach (var mid in members)
                {
                    var m = doc.GetElement(mid);
                    if (m == null) continue;

                    var mt = doc.GetElement(m.GetTypeId());
                    v = GetStringParam(mt, PARAM_MATERIAL_GRADE);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            return "";
        }

        // ========================= Grouping key =========================

        private readonly struct GroupKey
        {
            public GroupKey(string package, string frameNumber, string assemblyNumber, string materialGrade)
            {
                Package = package ?? "";
                FrameNumber = frameNumber ?? "";
                AssemblyNumber = assemblyNumber ?? "";
                MaterialGrade = materialGrade ?? "";
            }

            public string Package { get; }
            public string FrameNumber { get; }
            public string AssemblyNumber { get; }
            public string MaterialGrade { get; }
        }

        private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
        {
            public bool Equals(GroupKey x, GroupKey y)
            {
                return string.Equals(x.Package, y.Package, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.FrameNumber, y.FrameNumber, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.AssemblyNumber, y.AssemblyNumber, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.MaterialGrade, y.MaterialGrade, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(GroupKey obj)
            {
                unchecked
                {
                    int h = 17;
                    h = h * 23 + (obj.Package?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 23 + (obj.FrameNumber?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 23 + (obj.AssemblyNumber?.ToLowerInvariant().GetHashCode() ?? 0);
                    h = h * 23 + (obj.MaterialGrade?.ToLowerInvariant().GetHashCode() ?? 0);
                    return h;
                }
            }
        }

        private sealed class RawRow
        {
            public string Package { get; set; }
            public string FrameNumber { get; set; }
            public string AssemblyNumber { get; set; }
            public string MaterialGrade { get; set; }
        }

        private sealed class ReportRow
        {
            public string Package { get; set; }
            public string FrameNumber { get; set; }
            public string AssemblyNumber { get; set; }
            public int Qty { get; set; }
            public string MaterialGrade { get; set; }
        }

        private static string GetDisplayGroup(ReportRow row, bool groupByFrame)
        {
            string package = row?.Package ?? "";
            if (!groupByFrame)
                return package;

            if (!string.IsNullOrWhiteSpace(row?.FrameNumber))
                return row.FrameNumber;

            return string.IsNullOrWhiteSpace(package)
                ? "LOOSE"
                : package + "-LOOSE";
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

        private static string GetFrameNumber(RvtDoc doc, AssemblyInstance assembly)
        {
            string explicitFrameNumber = GetFrameParameterValue(doc, assembly);

            if (!string.IsNullOrWhiteSpace(explicitFrameNumber))
                return NormalizeFrameNumber(explicitFrameNumber);

            string assemblyName = (assembly.Name ?? "").Trim();
            string[] parts = assemblyName.Split(
                new[] { '-' },
                StringSplitOptions.RemoveEmptyEntries);

            // ATSYD3-B210-MC6-02 -> B210-MC6-02
            return parts.Length >= 3
                ? string.Join("-", parts.Skip(parts.Length - 3))
                : assemblyName;
        }

        private static string ResolveAssemblyFrameNumber(
            RvtDoc doc,
            AssemblyInstance assembly,
            IList<string> discoveredFrameNumbers,
            string drawingNumber,
            string package)
        {
            string explicitFrameNumber = GetFrameParameterValue(doc, assembly);
            if (!string.IsNullOrWhiteSpace(explicitFrameNumber))
                return NormalizeFrameNumber(explicitFrameNumber);

            return ResolveFrameGroup(
                discoveredFrameNumbers,
                drawingNumber,
                package);
        }

        private static string GetFrameParameterValue(
            RvtDoc doc,
            AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return "";

            string value = GetStringParam(assembly, PARAM_FRAME_NUMBER);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            value = GetStringParam(
                doc.GetElement(assembly.GetTypeId()),
                PARAM_FRAME_NUMBER);
            if (!string.IsNullOrWhiteSpace(value))
                return value;

            ICollection<ElementId> memberIds = assembly.GetMemberIds();
            if (memberIds == null)
                return "";

            foreach (ElementId memberId in memberIds)
            {
                Element member = doc.GetElement(memberId);
                value = GetStringParam(member, PARAM_FRAME_NUMBER);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            foreach (ElementId memberId in memberIds)
            {
                Element member = doc.GetElement(memberId);
                Element memberType = member == null
                    ? null
                    : doc.GetElement(member.GetTypeId());
                value = GetStringParam(memberType, PARAM_FRAME_NUMBER);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static string NormalizeFrameNumber(string value)
        {
            string frameNumber = (value ?? "").Trim();
            string[] parts = frameNumber.Split(
                new[] { '-' },
                StringSplitOptions.RemoveEmptyEntries);

            // ATSYD3-B210-MC6-01 -> B210-MC6-01
            return parts.Length >= 4
                ? string.Join("-", parts.Skip(parts.Length - 3))
                : frameNumber;
        }

        private static string ResolveFrameGroup(
            IList<string> frameNumbers,
            string drawingNumber,
            string package)
        {
            if (frameNumbers == null || frameNumbers.Count == 0)
                return "";

            drawingNumber = (drawingNumber ?? "").Trim();

            foreach (string frameNumber in frameNumbers)
            {
                string marker = "-" + frameNumber + "-";
                if (drawingNumber.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    drawingNumber.EndsWith("-" + frameNumber, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(drawingNumber, frameNumber, StringComparison.OrdinalIgnoreCase))
                {
                    return frameNumber;
                }
            }
            
            foreach (string frameNumber in frameNumbers)
            {
                int lastDash = frameNumber.LastIndexOf('-');
                string framePackage = lastDash > 0
                    ? frameNumber.Substring(0, lastDash)
                    : frameNumber;

                if (drawingNumber.IndexOf(
                    "-" + framePackage + "-",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return framePackage + "-LOOSE";
                }
            }

            package = (package ?? "").Trim();
            return string.IsNullOrWhiteSpace(package)
                ? "LOOSE"
                : package + "-LOOSE";
        }

        // ========================= Single-line fit helpers =========================
        // These prevent wrapping by shrinking font size to fit within known cell width.

        private static double CmToPoints(double cm)
        {
            // 1 inch = 2.54 cm, 1 inch = 72 points
            return (cm / 2.54) * 72.0;
        }

        private static double FitFontSizeToWidth(string text, double maxWidthCm, double baseSizePt, double minSizePt)
        {
            if (string.IsNullOrWhiteSpace(text)) return baseSizePt;

            // Be more conservative so it shrinks earlier (prevents wrap)
            // Arial average width is closer to ~0.58–0.62 * fontSize in many cases.
            const double avgCharWidthFactor = 0.62;

            // account for cell padding / layout overhead
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

            // NNBSP prevents breaking between ":" and value more reliably
            const string NNBSP = "\u202F"; // narrow no-break space

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

    }
}
