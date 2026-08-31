
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Classes;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using RvtDoc = Autodesk.Revit.DB.Document;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class LabelReport
    {
        private const string QrCodeUrl = "https://www.brownmoodie.com.au";

        public sealed class LabelData
        {
            public string Package { get; set; }
            public string ProjectNumber { get; set; }
            public string ProjectName { get; set; }
            public string ProjectPhase { get; set; }
            public string SpoolNumber { get; set; }
            public string MarkItem { get; set; }
            public string PipeEndPrep { get; set; }
            public string MaterialGrade { get; set; }
            public string PipeSize { get; set; }
            public string PipeLength { get; set; }
        }

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();

            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "LABEL REPORT.pdf");

            var data = GenerateLabelData(doc, cfg)
                .OrderBy(x => x.Package)
                .ThenBy(x => TryParseInt(x.MarkItem))
                .ThenBy(x => x.SpoolNumber)
                .ToList();

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);

            var siteMeasureNames = siteMeasureAssemblies
                .Select(x => x.AssemblyName)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet();

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;

            data = data
                .Where(x => includeSiteMeasureAssemblies || !siteMeasureNames.Contains(x.SpoolNumber))
                .ToList();

            string projectPhases = string.Join(", ", data
                .Select(x => x.ProjectPhase)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase));

            string note = cfg.IncludeSiteMeasure
                ? ""
                : "NOTE: This report does not include site-measured spools and branches";

            // ---------------- DATE ----------------
            var culture = new CultureInfo("en-US");
            DateTime dt = (cfg.Date == default) ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            if (!cfg.ExportReportsToExcel)
            {
            // ==============================
            // PDF BUILDER
            // ==============================
            var builder = new PdfReportBuilder();
            var pdf = builder.Document;
            var section = builder.Section;

            // Styles
            PdfLayoutHelpers.DefineStyles(pdf);

            // Page Setup
            section.PageSetup.Orientation = MigraDoc.DocumentObjectModel.Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.0);
            section.PageSetup.LeftMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);
            section.PageSetup.FooterDistance = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(1.0);

            PdfLayoutHelpers.DrawHeader(section, cfg, "LABEL REPORT", dateText, projectPhases);

            // Footer
            PdfLayoutHelpers.AddFooter(section, note, !cfg.IncludeSiteMeasure);

            // ==============================
            // TABLE
            // ==============================
            var table = BuildMainTable(section);
            AddMainHeaderRow(table);

            bool shade = false;

            foreach (var r in data)
            {
                var tr = table.AddRow();
                tr.VerticalAlignment = VerticalAlignment.Center;

                if (shade)
                    tr.Shading.Color = Colors.WhiteSmoke;

                shade = !shade;

                tr.Cells[0].AddParagraph(r.SpoolNumber ?? "");
                tr.Cells[1].AddParagraph(r.MarkItem ?? "");
                tr.Cells[2].AddParagraph(r.MaterialGrade ?? "");
                tr.Cells[3].AddParagraph(r.PipeSize ?? "");
                tr.Cells[4].AddParagraph(r.PipeEndPrep ?? "");
                tr.Cells[5].AddParagraph(r.PipeLength ?? "");
                tr.Cells[6].AddParagraph(QrCodeUrl);
            }

            section.AddParagraph().Format.SpaceBefore = MigraDoc.DocumentObjectModel.Unit.FromCentimeter(0.6);

            // ==============================
            // SAVE
            // ==============================
            builder.Save(outPath);
            }

            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, data, note, projectPhases);
        }

        private static void ExportExcel(ProcurementConfig cfg, List<LabelData> data, string note, string projectPhases)
        {
            var headers = new[]
            {
        "Spool Number",
        "Mark Item",
        "Material Grade",
        "Pipe Size",
        "Pipe End Prep",
        "Cut Length",
        "QR Code"
    };

            var sheet = ExcelReportExporter.CreateReportSheet(
                cfg,
                "LABEL REPORT",
                headers,
                note,
                projectPhases);

            bool alternate = false;

            foreach (var r in data)
            {
                sheet.Add(
                    alternate
                        ? ExcelReportExporter.RowKind.AlternateData
                        : ExcelReportExporter.RowKind.Data,
                    r.SpoolNumber ?? "",
                    r.MarkItem ?? "",
                    r.MaterialGrade ?? "",
                    r.PipeSize ?? "",
                    r.PipeEndPrep ?? "",
                    r.PipeLength ?? "",
                    QrCodeUrl);

                alternate = !alternate;
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ExcelReportExporter.RowKind.Blank);
                sheet.Add(ExcelReportExporter.RowKind.RedNote, note);
            }

            ExcelReportExporter.SaveWorkbook(
                ExcelReportExporter.BuildOutputPath(cfg, "LABEL REPORT"),
                new[] { sheet });
        }

        private static void AddMainHeaderRow(Table table)
        {
            var hr = table.AddRow();
            hr.Shading.Color = Colors.WhiteSmoke;
            hr.Format.Font.Bold = true;
            hr.VerticalAlignment = VerticalAlignment.Center;

            hr.Cells[0].AddParagraph("Spool Number");
            hr.Cells[1].AddParagraph("Mark Item");
            hr.Cells[2].AddParagraph("Material Grade");
            hr.Cells[3].AddParagraph("Pipe Size");
            hr.Cells[4].AddParagraph("Pipe End Prep");
            hr.Cells[5].AddParagraph("Cut Length");
            hr.Cells[6].AddParagraph("QR Code");
        }

        private static Table BuildMainTable(Section section)
        {
            var table = section.AddTable();
            table.Borders.Width = 0.25;
            table.Borders.Color = Colors.Gray;

            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(3.45)); // Spool Number
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.00)); // Mark Item
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(4.20)); // Material Grade
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.20)); // Pipe Size
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.60)); // Pipe End Prep
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(2.80)); // Cut Length
            table.AddColumn(MigraDoc.DocumentObjectModel.Unit.FromCentimeter(10.05)); // QR Code

            return table;
        }

        private static List<LabelData> GenerateLabelData(RvtDoc doc, ProcurementConfig cfg)
        {
            var labels = new List<LabelData>();

            if (doc.ActiveView == null)
                return labels;

            var assemblies = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .ToList();

            foreach (var assembly in assemblies)
            {
                string spoolNumber = CleanText(
                    Helpers.Elements.GetStringParam(
                        assembly,
                        "Assembly Number"));

                if (string.IsNullOrWhiteSpace(spoolNumber))
                    spoolNumber = CleanText(assembly.Name);

                string package = GetPackageFromSpoolNumber(spoolNumber);
                string assemblyProjectPhase = GetAssemblyProjectPhase(doc, assembly);

                foreach (var id in assembly.GetMemberIds())
                {
                    Element element = doc.GetElement(id);

                    if (element == null)
                        continue;

                    if (IsShapedBranch(doc, element))
                        continue;

                    string markItem = GetMarkItem(element);

                    if (string.IsNullOrWhiteSpace(markItem))
                        continue;

                    string projectPhase = GetProjectPhase(element);

                    if (string.IsNullOrWhiteSpace(projectPhase))
                        projectPhase = assemblyProjectPhase;

                    string materialGrade = GetMaterialGradeForLabel(doc, assembly, element);

                    if (string.IsNullOrWhiteSpace(materialGrade))
                        continue;

                    string pipeSize = GetPipeSizeForLabel(doc, element);
                    string pipeEndPrep = GetPipeEndPrepForLabel(doc, element);
                    string pipeLength = GetCutLengthForLabel(doc, element);

                    labels.Add(new LabelData
                    {
                        Package = package,
                        ProjectNumber = cfg.JobNumber,
                        ProjectName = cfg.JobName,
                        ProjectPhase = projectPhase,
                        SpoolNumber = spoolNumber,
                        MarkItem = markItem,
                        PipeEndPrep = pipeEndPrep,
                        MaterialGrade = materialGrade,
                        PipeSize = pipeSize,
                        PipeLength = pipeLength
                    });
                }
            }

            return labels;
        }

        private static bool IsShapedBranch(RvtDoc doc, Element element)
        {
            if (doc == null || element == null)
                return false;

            Element type = doc.GetElement(element.GetTypeId());
            FamilyInstance familyInstance = element as FamilyInstance;

            string identity = string.Join(
                " ",
                element.Name ?? "",
                familyInstance?.Symbol?.Name ?? "",
                familyInstance?.Symbol?.FamilyName ?? "",
                GetParamString(element, "Description", "BOM Description", "Type"),
                GetParamString(type, "Description", "BOM Description", "Type"))
                .ToUpperInvariant();

            return identity.Contains("SHAPED BRANCH") ||
                identity.Contains("WELDOLET") ||
                identity.Contains("OLET");
        }

        private static string GetMaterialGradeForLabel(RvtDoc doc, AssemblyInstance assembly, Element element)
        {
            if (doc == null || assembly == null || element == null)
                return "";

            // Keep original working pipe material logic.
            if (element is Pipe pipe)
            {
                string materialGrade = Elements.GetMaterialGrade(doc, assembly)
                    ?? pipe.LookupParameter("Material Grade")?.AsString()
                    ?? pipe.LookupParameter("Material")?.AsValueString()
                    ?? pipe.PipeType?.LookupParameter("Material")?.AsValueString();

                return CleanText(materialGrade);
            }

            // Fitting / branch material grade.
            // Do not use Description first because it gives the long SHAPED BRANCH value.
            if (element is FamilyInstance familyInstance)
            {
                FamilySymbol symbol = familyInstance.Symbol;

                string value = GetParamString(
                    element,
                    "Material Grade",
                    "Vic_Material Grade",
                    "Vic_MaterialGrade",
                    "Label Material Grade",
                    "Report Material Grade");

                if (!string.IsNullOrWhiteSpace(value))
                    return NormalizeMaterialGrade(value);

                if (symbol != null)
                {
                    value = GetParamString(
                        symbol,
                        "Material Grade",
                        "Vic_Material Grade",
                        "Vic_MaterialGrade",
                        "Label Material Grade",
                        "Report Material Grade");

                    if (!string.IsNullOrWhiteSpace(value))
                        return NormalizeMaterialGrade(value);

                    if (!string.IsNullOrWhiteSpace(symbol.FamilyName))
                        return NormalizeMaterialGrade(symbol.FamilyName);

                    if (!string.IsNullOrWhiteSpace(symbol.Name))
                        return NormalizeMaterialGrade(symbol.Name);
                }
            }

            Element type = doc.GetElement(element.GetTypeId());

            string fallback = GetParamString(
                element,
                "Material Grade",
                "Vic_Material Grade",
                "Vic_MaterialGrade");

            if (!string.IsNullOrWhiteSpace(fallback))
                return NormalizeMaterialGrade(fallback);

            fallback = GetParamString(
                type,
                "Material Grade",
                "Vic_Material Grade",
                "Vic_MaterialGrade");

            if (!string.IsNullOrWhiteSpace(fallback))
                return NormalizeMaterialGrade(fallback);

            return "";
        }

        private static string GetPipeSizeForLabel(RvtDoc doc, Element element)
        {
            if (element == null)
                return "";

            // Pipe rows keep unit/prefix: DN 125
            if (element is Pipe pipe)
            {
                double diameterFeet = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM)?.AsDouble() ?? 0;

                if (diameterFeet > 0)
                {
                    double mm = UnitUtils.ConvertFromInternalUnits(
                        diameterFeet,
                        UnitTypeId.Millimeters);

                    return $"{Math.Round(mm)}";
                }

                return "";
            }

            // Fitting / branch rows should match Excel style.
            string value = GetParamString(
                element,
                "Pipe Size",
                "Vic_Pipe Size",
                "Vic_PipeSize",
                "Nominal Diameter",
                "Nominal Size",
                "Size",
                "Diameter",
                "Branch Size");

            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeFittingPipeSize(value);

            Element type = doc.GetElement(element.GetTypeId());

            value = GetParamString(
                type,
                "Pipe Size",
                "Vic_Pipe Size",
                "Vic_PipeSize",
                "Nominal Diameter",
                "Nominal Size",
                "Size",
                "Diameter",
                "Branch Size");

            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeFittingPipeSize(value);

            return GetSmallestConnectorSizeForFitting(element);
        }

        private static string GetPipeEndPrepForLabel(RvtDoc doc, Element element)
        {
            if (element == null)
                return "";

            string value = GetParamString(
                element,
                "Pipe End Prep",
                "Vic_Pipe End Prep",
                "Vic_PipeEndPrep",
                "End Prep",
                "End Preparation");

            if (!string.IsNullOrWhiteSpace(value))
                return value;

            Element type = doc.GetElement(element.GetTypeId());

            value = GetParamString(
                type,
                "Pipe End Prep",
                "Vic_Pipe End Prep",
                "Vic_PipeEndPrep",
                "End Prep",
                "End Preparation");

            return value;
        }

        private static string GetCutLengthForLabel(RvtDoc doc, Element element)
        {
            if (element == null)
                return "";

            // Pipe rows keep unit: mm
            if (element is Pipe pipe)
            {
                double lengthFeet = pipe.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0;

                if (lengthFeet > 0)
                {
                    double mm = UnitUtils.ConvertFromInternalUnits(
                        lengthFeet,
                        UnitTypeId.Millimeters);

                    return $"{Math.Round(mm)} mm";
                }

                return "";
            }

            // Fitting / branch rows keep unit: mm
            string value = GetParamString(
                element,
                "Cut Length",
                "Vic_Cut Length",
                "Vic_CutLength",
                "Pipe Length",
                "Vic_Pipe Length",
                "Vic_PipeLength",
                "Length");

            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeLength(value);

            Element type = doc.GetElement(element.GetTypeId());

            value = GetParamString(
                type,
                "Cut Length",
                "Vic_Cut Length",
                "Vic_CutLength",
                "Pipe Length",
                "Vic_Pipe Length",
                "Vic_PipeLength",
                "Length");

            if (!string.IsNullOrWhiteSpace(value))
                return NormalizeLength(value);

            return "";
        }

        private static string GetAssemblyProjectPhase(RvtDoc doc, AssemblyInstance assembly)
        {
            if (doc == null || assembly == null)
                return "";

            foreach (var id in assembly.GetMemberIds())
            {
                Element element = doc.GetElement(id);

                string phase = GetProjectPhase(element);

                if (!string.IsNullOrWhiteSpace(phase))
                    return phase;
            }

            return "";
        }

        private static string GetProjectPhase(Element element)
        {
            if (element == null)
                return "";

            string value = element.LookupParameter("Vic_Zone")?.AsString();

            if (!string.IsNullOrWhiteSpace(value))
                return CleanText(value);

            value = GetParamString(
                element,
                "Project Phase",
                "Vic_Project Phase",
                "Vic_ProjectPhase",
                "Phase",
                "Vic_Phase",
                "Zone");

            return CleanText(value);
        }

        private static string GetMarkItem(Element element)
        {
            if (element == null)
                return "";

            string value = GetParamString(
                element,
                "Vic_Mark",
                "Mark Item",
                "Mark",
                "Item",
                "Item Number");

            return CleanText(value);
        }

        private static string GetPackageFromSpoolNumber(string spoolNumber)
        {
            spoolNumber = CleanText(spoolNumber);

            if (string.IsNullOrWhiteSpace(spoolNumber))
                return "";

            int lastDash = spoolNumber.LastIndexOf('-');

            if (lastDash <= 0)
                return spoolNumber;

            return spoolNumber.Substring(0, lastDash);
        }

        private static string GetParamString(Element element, params string[] names)
        {
            if (element == null || names == null)
                return "";

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                Parameter p = element.LookupParameter(name);

                if (p == null)
                    continue;

                string value = "";

                try
                {
                    value = p.AsString();

                    if (string.IsNullOrWhiteSpace(value))
                        value = p.AsValueString();

                    if (string.IsNullOrWhiteSpace(value) && p.StorageType == StorageType.Integer)
                        value = p.AsInteger().ToString(CultureInfo.InvariantCulture);

                    if (string.IsNullOrWhiteSpace(value) && p.StorageType == StorageType.Double)
                        value = p.AsDouble().ToString(CultureInfo.InvariantCulture);
                }
                catch
                {
                    value = "";
                }

                if (!string.IsNullOrWhiteSpace(value))
                    return CleanText(value);
            }

            return "";
        }

        private static string NormalizeMaterialGrade(string value)
        {
            value = CleanText(value);

            if (string.IsNullOrWhiteSpace(value))
                return "";

            if (value.IndexOf("SHAPED BRANCH", StringComparison.OrdinalIgnoreCase) >= 0)
                return "BRANCH-SS SCH10 (C-E)";

            if (value.IndexOf("BRANCH-SS SCH10", StringComparison.OrdinalIgnoreCase) >= 0)
                return "BRANCH-SS SCH10 (C-E)";

            return value;
        }

        private static string NormalizeFittingPipeSize(string value)
        {
            value = CleanText(value);

            if (string.IsNullOrWhiteSpace(value))
                return "";

            value = Regex.Replace(value, "DN", "", RegexOptions.IgnoreCase);
            value = value.Replace("Ø", "");
            value = Regex.Replace(value, "mm", "", RegexOptions.IgnoreCase);
            value = value.Trim();

            // Example: DN 50-50 becomes 50 to match the Excel-style branch size.
            if (value.Contains("-"))
            {
                var parts = value
                    .Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (parts.Count > 0)
                {
                    var numericParts = new List<double>();

                    foreach (string part in parts)
                    {
                        if (double.TryParse(part, NumberStyles.Any, CultureInfo.InvariantCulture, out double n))
                            numericParts.Add(n);
                    }

                    if (numericParts.Any())
                        return Math.Round(numericParts.Min()).ToString(CultureInfo.InvariantCulture);

                    return parts[0];
                }
            }

            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double numeric))
                return Math.Round(numeric).ToString(CultureInfo.InvariantCulture);

            return value;
        }

        private static string GetSmallestConnectorSizeForFitting(Element element)
        {
            try
            {
                ConnectorSet connectors = null;

                if (element is FamilyInstance familyInstance)
                    connectors = familyInstance.MEPModel?.ConnectorManager?.Connectors;
                else if (element is MEPCurve mepCurve)
                    connectors = mepCurve.ConnectorManager?.Connectors;

                if (connectors == null || connectors.Size == 0)
                    return "";

                var sizes = new List<double>();

                foreach (Connector connector in connectors)
                {
                    if (connector == null || connector.Radius <= 0)
                        continue;

                    double diameterFeet = connector.Radius * 2.0;
                    double diameterMm = UnitUtils.ConvertFromInternalUnits(
                        diameterFeet,
                        UnitTypeId.Millimeters);

                    if (diameterMm > 0)
                        sizes.Add(diameterMm);
                }

                if (!sizes.Any())
                    return "";

                return Math.Round(sizes.Min()).ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return "";
            }
        }

        private static string NormalizeLength(string value)
        {
            value = CleanText(value);

            if (string.IsNullOrWhiteSpace(value))
                return "";

            value = value.Replace("MM", "mm")
                         .Replace("Mm", "mm")
                         .Trim();

            if (value.EndsWith("mm", StringComparison.OrdinalIgnoreCase))
            {
                string numberPart = value.Substring(0, value.Length - 2).Trim();

                if (double.TryParse(numberPart, NumberStyles.Any, CultureInfo.InvariantCulture, out double mm))
                    return $"{Math.Round(mm)} mm";

                return value;
            }

            if (double.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out double numeric))
                return $"{Math.Round(numeric)} mm";

            return value;
        }

        private static int TryParseInt(string value)
        {
            if (int.TryParse(CleanText(value), out int result))
                return result;

            return int.MaxValue;
        }

        private static string CleanText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            while (value.Contains("  "))
                value = value.Replace("  ", " ");

            return value
                .Replace("\r", " ")
                .Replace("\n", " ")
                .Replace("\t", " ")
                .Replace("￾", "")
                .Trim();
        }
    }
}
