using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;

using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using MigraDoc.Rendering;

using ParallelSystemsPlugin.Models.Configs;

// ===== Aliases to remove ambiguity =====
using RvtDoc = Autodesk.Revit.DB.Document;
using PdfDoc = MigraDoc.DocumentObjectModel.Document;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
using ParallelSystemsPlugin.Compatibility;


namespace ParallelSystemsPlugin.Reports.Procurement
{
    public static class BomFittingReport
    {
        // Material grade comes from your shared parameter "Material"
        private const string PARAM_MATERIAL_GRADE = "Material";
        private const bool DEBUG_MODE = false;
        private const string PARAM_IS_CUSTOM = "IsCustom";
        private const string NO_PACKAGE_ASSIGNED = "NO PACKAGE ASSIGNED";

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "BOM-FITTING REPORT.pdf");

            var culture = new CultureInfo("en-US");
            DateTime dt = (cfg.Date == default(DateTime)) ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            // Collect fittings in active view
            List<FittingRow> rows = CollectFittingsFromActiveView(doc);

            if(DEBUG_MODE)
                WriteFittingDebugCsv(doc, cfg, rows, "01_AFTER_COLLECT");

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);

            var siteMeasureNames = siteMeasureAssemblies
            .Select(x => x.AssemblyName)
            .ToHashSet();

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;
            rows = rows.Where(x => includeSiteMeasureAssemblies || !siteMeasureNames.Contains(x.AssemblyName)).ToList();

            rows = rows
                .Where(x => !IsExcludedCategory(x.TypeBucket))
                .ToList();


            if (rows.Count == 0)
                throw new InvalidOperationException("No fittings found in the active view.");

            if (DEBUG_MODE)
                WriteFittingDebugCsv(doc, cfg, rows, "02_AFTER_SITE_MEASURE_FILTER");

            // Group by Material Grade (Material param)
            var byMat = rows
                .GroupBy(r => (r.MaterialGrade ?? "").Trim())
                .OrderBy(g => g.Key)
                .ToList();

            if (DEBUG_MODE)
                WriteFittingSummaryDebugCsv(cfg, byMat, "03_GROUPED_SUMMARY");

            PdfDoc pdf = new PdfDoc();
            DefineStyles(pdf);

            Section section = pdf.AddSection();
            section.PageSetup.Orientation = Orientation.Landscape;
            section.PageSetup.PageFormat = PageFormat.A4;
            section.PageSetup.TopMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = Unit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = Unit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = Unit.FromCentimeter(1.0);

            // Pagination footer: "Page 1 of 1"
            //AddPageFooter(section);
            var note = "";

            // Footerusing ParallelSystemsPlugin.Helpers;
            ParallelSystemsPlugin.Helpers.PdfLayoutHelpers.AddFooter(
                 section,
                 note
            );

            // Header
            DrawHeader(section, cfg, "BOM Fitting Report", dateText);

            foreach (var matGroup in byMat)
            {
                AddMaterialGradeHeader(section, matGroup.Key);

                var byType = matGroup
                    .GroupBy(x => x.TypeBucket)
                    .OrderBy(g => TypeSortOrder(g.Key))
                    .ThenBy(g => g.Key)
                    .ToList();

                foreach (var typeGroup in byType)
                {
                    string typeBucket = typeGroup.Key;

                    // TYPE colored band
                    AddTypeBand(section, typeBucket);

                    // Table: QTY | Size | Description
                    var table = section.AddTable();
                    table.Borders.Width = 0.25;
                    table.Borders.Color = Colors.LightGray;

                    table.AddColumn(Unit.FromCentimeter(2.2));  // QTY
                    table.AddColumn(Unit.FromCentimeter(6.0));  // Size
                    table.AddColumn(Unit.FromCentimeter(19.5)); // Description

                    var hr = table.AddRow();
                    hr.Shading.Color = Colors.WhiteSmoke;
                    hr.Format.Font.Bold = true;
                    hr.Cells[0].AddParagraph("Qty");
                    hr.Cells[1].AddParagraph("Size");
                    hr.Cells[2].AddParagraph("Description");

                    // Aggregate: same type + same size + same description
                    var aggregated = typeGroup
                        .GroupBy(x => new { x.SizeText, x.Description })
                        .Select(g => new
                        {
                            Qty = g.Count(),
                            SizeText = g.Key.SizeText ?? "",
                            Desc = g.Key.Description ?? "",
                            SizeSort = g.Max(z => z.SizeSort)
                        })
                        .OrderByDescending(x => x.SizeSort)
                        .ThenBy(x => x.Desc)
                        .ToList();

                    foreach (var item in aggregated)
                    {
                        var r = table.AddRow();
                        r.Cells[0].AddParagraph(item.Qty.ToString());
                        r.Cells[1].AddParagraph(item.SizeText);
                        r.Cells[2].AddParagraph(item.Desc);
                    }

                    section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.35);
                }

                section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.25);
            }

            var renderer = new PdfDocumentRenderer() { Document = pdf };
            renderer.RenderDocument();
            if (!cfg.ExportReportsToExcel)
                renderer.PdfDocument.Save(outPath);

            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, byMat);
        }



        private static bool GetYesNoParamInstanceOrType(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return false;

            // 1) Instance parameter first
            Parameter pInst = e.LookupParameter(paramName);
            if (TryReadYesNoParameter(pInst, out bool instValue))
                return instValue;

            // 2) Type parameter fallback
            ElementId typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                Element typeElem = e.Document.GetElement(typeId);
                Parameter pType = typeElem?.LookupParameter(paramName);

                if (TryReadYesNoParameter(pType, out bool typeValue))
                    return typeValue;
            }

            return false;
        }

        private static bool TryReadYesNoParameter(Parameter p, out bool value)
        {
            value = false;

            if (p == null)
                return false;

            if (p.StorageType == StorageType.Integer)
            {
                value = p.AsInteger() == 1;
                return true;
            }

            // Defensive fallback only.
            // Some shared params may appear as string/value string depending on setup.
            string s = p.AsString() ?? p.AsValueString() ?? "";

            if (string.IsNullOrWhiteSpace(s))
                return false;

            s = s.Trim();

            if (s.Equals("Yes", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("True", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                value = true;
                return true;
            }

            if (s.Equals("No", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("False", StringComparison.OrdinalIgnoreCase) ||
                s.Equals("0", StringComparison.OrdinalIgnoreCase))
            {
                value = false;
                return true;
            }

            return false;
        }

        private static void ExportExcel(ProcurementConfig cfg, List<IGrouping<string, FittingRow>> byMat)
        {
            string note = "";
            var sheet = ParallelSystemsPlugin.Helpers.ExcelReportExporter.CreateReportSheet(
                cfg,
                "BOM Fitting Report",
                new[] { "Material Grade", "Category", "Qty", "Size", "Description" },
                note);
            sheet.SetColumnWidth(1, 28);
            sheet.SetColumnWidth(2, 24);
            sheet.SetColumnWidth(3, 10);
            sheet.SetColumnWidth(4, 14);
            sheet.SetColumnWidth(5, 45);
            sheet.CenterColumns(3);

            bool alt = false;
            foreach (var materialGroup in byMat)
            {
                sheet.Add(
                    ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                    materialGroup.Key ?? "",
                    "",
                    "",
                    "",
                    "");

                var byType = materialGroup
                    .GroupBy(x => x.TypeBucket)
                    .OrderBy(g => TypeSortOrder(g.Key))
                    .ThenBy(g => g.Key)
                    .ToList();

                foreach (var typeGroup in byType)
                {
                    sheet.Add(
                        GetExcelCategoryRowKind(typeGroup.Key),
                        "",
                        typeGroup.Key ?? "",
                        "",
                        "",
                        "");

                    var aggregated = typeGroup
                        .GroupBy(x => new { x.SizeText, x.Description })
                        .Select(g => new
                        {
                            Qty = g.Count(),
                            SizeText = g.Key.SizeText ?? "",
                            Desc = g.Key.Description ?? "",
                            SizeSort = g.Max(z => z.SizeSort)
                        })
                        .OrderByDescending(x => x.SizeSort)
                        .ThenBy(x => x.Desc)
                        .ToList();

                    foreach (var item in aggregated)
                    {
                        sheet.Add(
                            alt ? ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.AlternateData : ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Data,
                            "",
                            "",
                            item.Qty,
                            item.SizeText,
                            item.Desc);
                        alt = !alt;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Blank);
                sheet.Add(ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.Note, note);
            }

            ParallelSystemsPlugin.Helpers.ExcelReportExporter.SaveWorkbook(
                ParallelSystemsPlugin.Helpers.ExcelReportExporter.BuildOutputPath(cfg, "BOM-FITTING REPORT"),
                new[] { sheet });
        }

        private static bool IsExcludedCategory(string category)
        {
            return string.Equals(category, "WELD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(category, "SHAPED BRANCH", StringComparison.OrdinalIgnoreCase);
        }

        private static ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind
            GetExcelCategoryRowKind(string category)
        {
            switch ((category ?? "").ToUpperInvariant())
            {
                case "CUSTOM FITTING": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingCustom;
                case "ELBOW": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingElbow;
                case "END CAP": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingEndCap;
                case "FLANGE": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingFlange;
                case "REDUCER": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingReducer;
                case "SOCKET": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingSocket;
                case "TEE": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingTee;
                case "WELD": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingWeld;
                case "SHAPED BRANCH": return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingShapedBranch;
                default: return ParallelSystemsPlugin.Helpers.ExcelReportExporter.RowKind.FittingOther;
            }
        }

        // -----------------------------
        // Footer: Page X of Y
        // -----------------------------
        private static void AddPageFooter(Section section)
        {
            // Add to both Primary and EvenPage (just in case)
            var fp = section.Footers.Primary.AddParagraph();
            fp.Format.Alignment = ParagraphAlignment.Center;
            fp.Format.Font.Name = "Arial";
            fp.Format.Font.Size = 9;
            fp.Format.Font.Color = Colors.DimGray;

            fp.AddText("Page ");
            fp.AddPageField();
            fp.AddText(" of ");
            fp.AddNumPagesField();

            var fe = section.Footers.EvenPage.AddParagraph();
            fe.Format.Alignment = ParagraphAlignment.Center;
            fe.Format.Font.Name = "Arial";
            fe.Format.Font.Size = 9;
            fe.Format.Font.Color = Colors.DimGray;

            fe.AddText("Page ");
            fe.AddPageField();
            fe.AddText(" of ");
            fe.AddNumPagesField();
        }

        // -----------------------------
        // Data model
        // -----------------------------
        private sealed class FittingRow
        {
            public ElementId ElementId { get; set; }

            public string MaterialGrade { get; set; } = "";
            public string TypeBucket { get; set; } = "";
            public string SizeText { get; set; } = "";
            public double SizeSort { get; set; }
            public string Description { get; set; } = "";
            public string AssemblyName { get; set; } = "";
            public string PackageName { get; set; } = "";
        }

        // -----------------------------
        // Collect fittings from active view
        // -----------------------------
        private static List<FittingRow> CollectFittingsFromActiveView(RvtDoc doc)
        {
            if (doc.ActiveView == null) return new List<FittingRow>();

            var results = new List<FittingRow>();

            var fittings = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var e in fittings)
            {
                string assemblyName = "";
                if (e.AssemblyInstanceId != ElementId.InvalidElementId)
                {
                    AssemblyInstance assembly = doc.GetElement(e.AssemblyInstanceId) as AssemblyInstance;
                    if (assembly != null)
                        assemblyName = assembly.Name;
                }

                string mat = NormalizeMaterialGrade(
                    GetFittingMaterialGrade(doc, e));

                string typeIdentity = GetElementTypeIdentity(e);
                string rawDesc = GetBestDescription(e);

                bool isCustom = GetYesNoParamInstanceOrType(e, PARAM_IS_CUSTOM);

                string bucket = isCustom
                                ? "CUSTOM FITTING"
                                : MapToBucket(typeIdentity + " " + rawDesc);


                string sizeText = GetStringParam(e, BuiltInParameter.RBS_CALCULATED_SIZE);
               
                if (string.IsNullOrWhiteSpace(sizeText))
                    sizeText = FirstNonEmptyParam(e, "Size", "Nominal Diameter", "Diameter", "DN");

                if (string.IsNullOrWhiteSpace(sizeText))
                    sizeText = ExtractLikelySizeFromText(typeIdentity);

                sizeText = NormalizeSizeText(sizeText);

                double sizeSort = ExtractFirstNumber(sizeText);

                string finalDesc = BuildReportDescription(e, bucket, rawDesc, typeIdentity, sizeText);

                // Manual report does not show these "Standard" non-connectors.
                // They are being picked up by OST_PipeFitting but are not BOM fitting items.
                if (string.Equals(finalDesc, "Standard", StringComparison.OrdinalIgnoreCase))
                    continue;

                // The Tri-Clamp 050DN and Ferrule rows are two Revit elements for the same physical custom fitting.
                // Keep only the Ferrule description row, skip the Tri-Clamp placeholder.
                if (typeIdentity.IndexOf("Tri-Clamp", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    rawDesc.Equals("050DN", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string packageName = GetPackageNameFromAssemblyName(assemblyName);
                if (string.IsNullOrWhiteSpace(packageName))
                    packageName = Helpers.Elements.GetProcurementPackageName(doc, e);
                if (string.IsNullOrWhiteSpace(packageName))
                    packageName = ResolvePackageFromConnectedPipe(doc, e);

                results.Add(new FittingRow
                {
                    ElementId = e.Id,
                    MaterialGrade = mat ?? "",
                    TypeBucket = bucket,
                    SizeText = sizeText ?? "",
                    SizeSort = sizeSort,
                    Description = finalDesc ?? "",
                    AssemblyName = assemblyName,
                    PackageName = packageName ?? ""
                });
            }

            return results;
        }

        private static string GetFittingMaterialGrade(RvtDoc doc, Element fitting)
        {
            if (doc == null || fitting == null)
                return "";

            string materialGrade = GetStringParamInstanceOrType(
                fitting,
                PARAM_MATERIAL_GRADE);
            if (!string.IsNullOrWhiteSpace(materialGrade))
                return materialGrade;

            materialGrade = GetStringParamInstanceOrType(
                fitting,
                "Segment Description");
            if (!string.IsNullOrWhiteSpace(materialGrade))
                return materialGrade;

            if (fitting.AssemblyInstanceId != ElementId.InvalidElementId)
            {
                AssemblyInstance assembly =
                    doc.GetElement(fitting.AssemblyInstanceId) as AssemblyInstance;
                materialGrade = Helpers.Elements.GetMaterialGrade(doc, assembly);
                if (!string.IsNullOrWhiteSpace(materialGrade))
                    return materialGrade;
            }

            foreach (Pipe pipe in GetConnectedPipesThroughVictaulicCouplings(fitting)
                .OrderBy(x => RevitApiCompatibility.GetElementIdValue(x.Id)))
            {
                materialGrade = GetStringParamInstanceOrType(
                    pipe,
                    "Segment Description");
                if (string.IsNullOrWhiteSpace(materialGrade))
                {
                    materialGrade = GetStringParamInstanceOrType(
                        pipe,
                        PARAM_MATERIAL_GRADE);
                }

                if (!string.IsNullOrWhiteSpace(materialGrade))
                    return materialGrade;
            }

            return "";
        }

        private static string NormalizeMaterialGrade(string materialGrade)
        {
            string value = (materialGrade ?? "").Trim();
            int specificationSeparator = value.IndexOf(
                " - ",
                StringComparison.Ordinal);

            return specificationSeparator > 0
                ? value.Substring(0, specificationSeparator).Trim()
                : value;
        }

        private static string GetPackageNameFromAssemblyName(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
                return "";

            int lastDash = assemblyName.LastIndexOf('-');
            return lastDash > 0
                ? assemblyName.Substring(0, lastDash).Trim()
                : assemblyName.Trim();
        }

        private static string ResolvePackageFromConnectedPipe(
            RvtDoc doc,
            Element fitting)
        {
            if (doc == null || fitting == null)
                return "";

            foreach (Pipe pipe in GetConnectedPipesThroughVictaulicCouplings(fitting)
                .OrderBy(x => RevitApiCompatibility.GetElementIdValue(x.Id)))
            {
                if (pipe.AssemblyInstanceId == ElementId.InvalidElementId)
                    continue;

                AssemblyInstance assembly =
                    doc.GetElement(pipe.AssemblyInstanceId) as AssemblyInstance;
                string packageName = Helpers.Elements
                    .GetProcurementPackageNameFromAssembly(assembly);

                if (!string.IsNullOrWhiteSpace(packageName))
                    return packageName;
            }

            return "";
        }

        private static List<Pipe> GetConnectedPipesThroughVictaulicCouplings(
            Element fitting)
        {
            const int maximumCouplingHops = 8;
            var pipes = new List<Pipe>();
            var pipeIds = new HashSet<long>();
            var visited = new HashSet<long>();
            var queue = new Queue<ConnectionTraversalItem>();

            if (fitting == null)
                return pipes;

            visited.Add(RevitApiCompatibility.GetElementIdValue(fitting.Id));

            foreach (Element connected in GetDirectlyConnectedElements(fitting))
            {
                Pipe pipe = connected as Pipe;
                if (pipe != null)
                {
                    AddConnectedPipe(pipes, pipeIds, pipe);
                }
                else if (IsTransparentVictaulicCoupling(connected))
                {
                    queue.Enqueue(new ConnectionTraversalItem
                    {
                        Element = connected,
                        CouplingHops = 1
                    });
                }
            }

            while (queue.Count > 0)
            {
                ConnectionTraversalItem item = queue.Dequeue();
                Element current = item.Element;
                if (current == null || item.CouplingHops > maximumCouplingHops)
                    continue;

                long currentId = RevitApiCompatibility.GetElementIdValue(current.Id);
                if (!visited.Add(currentId))
                    continue;

                foreach (Element connected in GetDirectlyConnectedElements(current))
                {
                    long connectedId =
                        RevitApiCompatibility.GetElementIdValue(connected.Id);
                    if (visited.Contains(connectedId))
                        continue;

                    Pipe pipe = connected as Pipe;
                    if (pipe != null)
                    {
                        AddConnectedPipe(pipes, pipeIds, pipe);
                    }
                    else if (item.CouplingHops < maximumCouplingHops &&
                        IsTransparentVictaulicCoupling(connected))
                    {
                        queue.Enqueue(new ConnectionTraversalItem
                        {
                            Element = connected,
                            CouplingHops = item.CouplingHops + 1
                        });
                    }
                }
            }

            return pipes;
        }

        private static void AddConnectedPipe(
            List<Pipe> pipes,
            HashSet<long> pipeIds,
            Pipe pipe)
        {
            if (pipe == null)
                return;

            long pipeId = RevitApiCompatibility.GetElementIdValue(pipe.Id);
            if (pipeIds.Add(pipeId))
                pipes.Add(pipe);
        }

        private static bool IsTransparentVictaulicCoupling(Element element)
        {
            FamilyInstance familyInstance = element as FamilyInstance;
            if (familyInstance == null ||
                familyInstance.Category?.Id == null ||
                familyInstance.Category.Id !=
                    new ElementId(BuiltInCategory.OST_PipeFitting))
            {
                return false;
            }

            string identity = string.Join(
                    " ",
                    familyInstance.Name ?? "",
                    familyInstance.Symbol?.Name ?? "",
                    familyInstance.Symbol?.Family?.Name ?? "")
                .ToUpperInvariant();

            bool isVictaulic =
                identity.Contains("VICTAULIC") || identity.Contains("VIC-");
            bool isCoupling = identity.Contains("COUPLING");
            bool isRigidOrFlex =
                identity.Contains("RIGID") ||
                identity.Contains("FLEX") ||
                identity.Contains("07-W07") ||
                identity.Contains("77-W77");

            return isVictaulic && isCoupling && isRigidOrFlex;
        }

        private static List<Element> GetDirectlyConnectedElements(Element source)
        {
            var connected = new List<Element>();
            var seen = new HashSet<long>();
            FamilyInstance familyInstance = source as FamilyInstance;
            MEPCurve mepCurve = source as MEPCurve;
            ConnectorManager manager =
                familyInstance?.MEPModel?.ConnectorManager ??
                mepCurve?.ConnectorManager;

            if (manager == null)
                return connected;

            try
            {
                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null)
                        continue;

                    foreach (Connector reference in connector.AllRefs)
                    {
                        Element owner = reference?.Owner;
                        if (owner == null || owner.Id == source.Id)
                            continue;

                        long ownerId =
                            RevitApiCompatibility.GetElementIdValue(owner.Id);
                        if (seen.Add(ownerId))
                            connected.Add(owner);
                    }
                }
            }
            catch
            {
                // Keep the fitting in the unassigned group if its connector
                // graph cannot be read; report generation must still finish.
            }

            return connected;
        }

        private sealed class ConnectionTraversalItem
        {
            public Element Element { get; set; }
            public int CouplingHops { get; set; }
        }

        private static string GetStringParamInstanceOrType(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName)) return "";

            // 1) Instance
            var pInst = e.LookupParameter(paramName);
            string vInst = pInst?.AsString() ?? pInst?.AsValueString();
            if (!string.IsNullOrWhiteSpace(vInst)) return vInst;

            // 2) Type
            var typeId = e.GetTypeId();
            if (typeId != ElementId.InvalidElementId)
            {
                var typeElem = e.Document.GetElement(typeId);
                var pType = typeElem?.LookupParameter(paramName);
                string vType = pType?.AsString() ?? pType?.AsValueString();
                if (!string.IsNullOrWhiteSpace(vType)) return vType;
            }

            return "";
        }


        private static string GetBestDescription(Element e)
        {
            //string typeId = GetElementTypeIdentity(e);
            //if (!string.IsNullOrWhiteSpace(typeId))
            //    return typeId;

            Helpers.Elements.AddToDebugLog(e);

            return FirstNonEmptyParam(e, "Type", "Description", "Family and Type", "Type Name") ?? "";
        }

        private static string GetElementTypeIdentity(Element e)
        {
            if (e is FamilyInstance fi)
            {
                var sym = fi.Symbol;
                if (sym != null)
                {
                    string fam = sym.FamilyName ?? "";
                    string typ = sym.Name ?? "";
                    string combo = $"{fam} {typ}".Trim();
                    if (!string.IsNullOrWhiteSpace(combo)) return combo;
                }
            }

            string typeName = GetStringParam(e, BuiltInParameter.ALL_MODEL_TYPE_NAME);
            if (!string.IsNullOrWhiteSpace(typeName)) return typeName;

            return e.Name ?? "";
        }

        // -----------------------------
        // TYPE bucket mapping (added WELD + SHAPED BRANCH)
        // -----------------------------
        private static string MapToBucket(string text)
        {
            string t = (text ?? "").ToUpperInvariant();

            //if (t.Contains("FERRULE") || t.Contains("HYDROFLOW") || t.Contains("TRI-CLAMP"))
            //    return "CUSTOM FITTING";

            if (t.Contains("SHAPED BRANCH") || t.Contains("WELDOLET") || t.Contains("OLET"))
                return "SHAPED BRANCH";

            if (t.Contains("FILLET WELD") || t.Contains("BUTT WELD") || t.Contains("SOCKET WELD") || t.Contains("WELD"))
                return "WELD";

            if (t.Contains("ELBOW")) return "ELBOW";
            if (t.Contains("END CAP") || t.Contains("ENDCAP") || t.Contains("CAP")) return "END CAP";
            if (t.Contains("FLANGE")) return "FLANGE";
            if (t.Contains("REDUCER") || t.Contains("REDUCTION")) return "REDUCER";
            if (t.Contains("SOCKET") || t.Contains("COUPLING")) return "SOCKET";
            if (t.Contains("TEE") || t.Contains("T-E")) return "TEE";
            if (t.Contains("BRANCH")) return "SHAPED BRANCH";

            return "OTHER";
        }

        private static int TypeSortOrder(string bucket)
        {
            switch ((bucket ?? "").ToUpperInvariant())
            {
                case "CUSTOM FITTING": return 0;
                case "ELBOW": return 1;
                case "END CAP": return 2;
                case "FLANGE": return 3;
                case "REDUCER": return 4;
                case "SOCKET": return 5;
                case "TEE": return 6;
                case "WELD": return 7;
                case "SHAPED BRANCH": return 8;
                default: return 99;
            }
        }

        private static string BuildReportDescription(
    Element e,
    string bucket,
    string rawDesc,
    string typeIdentity,
    string sizeText)
        {
            string source = ((typeIdentity ?? "") + " " + (rawDesc ?? "")).Trim();
            string upper = source.ToUpperInvariant();

            if (bucket == "CUSTOM FITTING")
                return "HYDROFLOW FERRULE - SCH10 - SS";

            if (bucket == "WELD")
            {
                if (upper.Contains("FLANGE") && upper.Contains("EXTERNAL"))
                    return "FLANGE FILLET WELD EXTERNAL - CLASS 3-AS4041";

                if (upper.Contains("FLANGE") && upper.Contains("FILLET WELD"))
                    return "FLANGE FILLET WELD INTERNAL - CLASS 3-AS4041";

                if (upper.Contains("STAINLESS-0MM-WELD GAP"))
                    return "STAINLESS-0MM GAP-CLASS 3-AS4041";
            }

            if (bucket == "SOCKET")
                return NormalizeDescription(rawDesc);

            if (bucket == "ELBOW")
            {
                if (upper.Contains("VICTAULIC") || upper.Contains("VIC SS"))
                    return NormalizeDescription(rawDesc);

                string angle = GetElbowAngleText(e);

                if (!string.IsNullOrWhiteSpace(angle))
                    return "LR ELBOW - BW - " + angle + " - STD WT - CS";

                return "LR ELBOW - BW - SCH10-316/L-SS";
            }

            return NormalizeDescription(rawDesc);
        }

        private static string GetElbowAngleText(Element e)
        {
            string raw = FirstNonEmptyParamInstanceOrType(
                e,
                "Angle",
                "Angle 1",
                "Elbow Angle",
                "Bend Angle",
                "Nominal Angle",
                "Fitting Angle"
            );

            if (string.IsNullOrWhiteSpace(raw))
                return "";

            double n = ExtractFirstNumber(raw);

            if (n <= 0)
                return "";

            // Revit angle may be displayed as degrees already.
            if (n > 6.5)
                return Math.Round(n).ToString(CultureInfo.InvariantCulture) + "DEG";

            // If stored in radians.
            double deg = n * 180.0 / Math.PI;
            return Math.Round(deg).ToString(CultureInfo.InvariantCulture) + "DEG";
        }

        private static string FirstNonEmptyParamInstanceOrType(Element e, params string[] names)
        {
            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string value = GetStringParamInstanceOrType(e, name);

                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return "";
        }

        private static string NormalizeDescription(string text)
        {
            string s = (text ?? "").Trim();

            if (string.IsNullOrWhiteSpace(s))
                return "";

            s = s.Replace("COUPLING150LB", "COUPLING 150LB");
            s = s.Replace("STAINLESS-0MM-WELD GAP", "STAINLESS-0MM GAP");

            if (s.Equals("DN50 FERRULES - SCH10 - SS", StringComparison.OrdinalIgnoreCase))
                return "HYDROFLOW FERRULE - SCH10 - SS";

            if (s.Equals("050DN", StringComparison.OrdinalIgnoreCase))
                return "HYDROFLOW FERRULE - SCH10 - SS";

            return s;
        }

        private static string NormalizeSizeText(string sizeText)
        {
            string s = (sizeText ?? "").Trim();

            if (string.IsNullOrWhiteSpace(s))
                return "";

            // Convert "125-125" to "125" for manual BOM style.
            var parts = s.Split(new[] { '-' }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 2 &&
                string.Equals(parts[0].Trim(), parts[1].Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return parts[0].Trim();
            }

            return s;
        }

        // -----------------------------
        // Header (same professional theme)
        // -----------------------------
        private static void DrawHeader(Section section, ProcurementConfig cfg, string title, string dateText)
        {
            var themeGreen = MigraColor.FromRgb(60, 130, 60);
            var themeGreenLight = MigraColor.FromRgb(232, 243, 232);
            var themeText = MigraColor.FromRgb(20, 60, 85);
            var themeLine = MigraColor.FromRgb(120, 170, 120);

            const double USABLE_WIDTH_CM = 27.7; // A4 landscape minus margins

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

            // Logos
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

            // Title
            var titleCell = row.Cells[2];
            titleCell.VerticalAlignment = VerticalAlignment.Top;

            string titleText = string.IsNullOrWhiteSpace(title) ? "BOM Fitting Report" : title;
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

            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.35);
        }

        private static void AddMaterialGradeHeader(Section section, string materialGrade)
        {
            string label = "MaterialGrade: ";
            string value = string.IsNullOrWhiteSpace(materialGrade) ? "(Blank)" : materialGrade.Trim();

            var p = section.AddParagraph(label + value);
            p.Style = "H2";
            p.Format.SpaceBefore = Unit.FromCentimeter(0.2);
            p.Format.SpaceAfter = Unit.FromCentimeter(0.15);
        }



        // -----------------------------
        // TYPE colored band (added WELD + SHAPED BRANCH colors not used)
        // -----------------------------
        private static void AddTypeBand(Section section, string bucket)
        {
            // Existing:
            // ELBOW - Red
            // END CAP - Gray
            // FLANGE - Green
            // REDUCER - Blue
            // SOCKET - Orange
            // TEE - Turquoise
            //
            // NEW (not used yet):
            // WELD - Purple
            // SHAPED BRANCH - Yellow

            MigraColor fill = Colors.LightGray;
            MigraColor text = Colors.Black;

            switch ((bucket ?? "").ToUpperInvariant())
            {
                case "ELBOW":
                    fill = MigraColor.FromRgb(255, 0, 0);
                    text = Colors.White;
                    break;

                case "END CAP":
                    fill = MigraColor.FromRgb(191, 191, 191);
                    text = Colors.Black;
                    break;

                case "FLANGE":
                    fill = MigraColor.FromRgb(0, 176, 80);
                    text = Colors.Black;
                    break;

                case "REDUCER":
                    fill = MigraColor.FromRgb(91, 155, 213);
                    text = Colors.Black;
                    break;

                case "SOCKET":
                    fill = MigraColor.FromRgb(244, 177, 131);
                    text = Colors.Black;
                    break;

                case "TEE":
                    fill = MigraColor.FromRgb(0, 176, 240);
                    text = Colors.Black;
                    break;

                // NEW
                case "WELD":
                    fill = MigraColor.FromRgb(112, 48, 160); // Purple
                    text = Colors.White;
                    break;

                case "SHAPED BRANCH":
                    fill = MigraColor.FromRgb(255, 242, 204); // Light Yellow
                    text = Colors.Black;
                    break;

                default:
                    fill = MigraColor.FromRgb(220, 220, 220);
                    text = Colors.Black;
                    break;
            }

            var t = section.AddTable();
            t.Borders.Visible = false;
            t.AddColumn(Unit.FromCentimeter(27.7)); // full width inside margins

            var r = t.AddRow();
            r.Height = Unit.FromCentimeter(0.55);

            var c = r.Cells[0];
            c.Shading.Color = fill;

            var p = c.AddParagraph(bucket ?? "TYPE");
            p.Format.Font.Name = "Arial";
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 11;
            p.Format.Font.Color = text;
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;

            section.AddParagraph().Format.SpaceAfter = Unit.FromCentimeter(0.10);
        }

        // -----------------------------
        // Helpers: logos + fit-to-width + single-line key/value
        // -----------------------------
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
            return (cm / 2.54) * 72.0;
        }

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
            key = key ?? "";
            value = value ?? "";

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

        // -----------------------------
        // Param helpers
        // -----------------------------
        private static string FirstNonEmptyParam(Element e, params string[] names)
        {
            foreach (var n in names)
            {
                if (string.IsNullOrWhiteSpace(n)) continue;
                string v = GetStringParam(e, n);
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
            return "";
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

        private static double ExtractFirstNumber(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;

            string digits = new string(text.Where(ch => char.IsDigit(ch) || ch == '.').ToArray());
            if (double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out double n))
                return n;

            return 0;
        }

        private static string ExtractLikelySizeFromText(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var parts = text.Split(new[] { ' ', '-', '_', '/', '\\', '|', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
                if (p.Any(char.IsDigit))
                    return p;

            return "";
        }

        // -----------------------------
        // Styles
        // -----------------------------
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

        private static void WriteFittingDebugCsv(
    RvtDoc doc,
    ProcurementConfig cfg,
    List<FittingRow> rows,
    string stage)
        {
            try
            {
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.TargetFolder))
                    return;

                if (!Directory.Exists(cfg.TargetFolder))
                    return;

                string path = Path.Combine(cfg.TargetFolder, "BOM-FITTING-DEBUG-" + stage + ".csv");

                var lines = new List<string>();

                lines.Add(string.Join(",",
                    Csv("Stage"),
                    Csv("ElementId"),
                    Csv("Category"),
                    Csv("AssemblyName"),
                    Csv("FinalMaterialGrade"),
                    Csv("FinalTypeBucket"),
                    Csv("FinalSizeText"),
                    Csv("FinalDescription"),
                    Csv("FamilyTypeIdentity"),
                    Csv("ElementName"),

                    Csv("Instance_Material"),
                    Csv("Type_Material"),

                    Csv("Instance_Type"),
                    Csv("Type_Type"),

                    Csv("Instance_Description"),
                    Csv("Type_Description"),

                    Csv("Instance_Family and Type"),
                    Csv("Type_Family and Type"),

                    Csv("Instance_Type Name"),
                    Csv("Type_Type Name"),

                    Csv("CalculatedSize"),
                    Csv("Instance_Size"),
                    Csv("Type_Size"),

                    Csv("AllModelTypeName")
                ));

                foreach (var r in rows)
                {
                    Element e = doc.GetElement(r.ElementId);

                    string familyTypeIdentity = GetElementTypeIdentity(e);

                    lines.Add(string.Join(",",
                        Csv(stage),
                        Csv(r.ElementId != null ? RevitApiCompatibility.GetElementIdValue(r.ElementId).ToString(CultureInfo.InvariantCulture) : ""),
                        Csv(e?.Category?.Name ?? ""),
                        Csv(r.AssemblyName),
                        Csv(r.MaterialGrade),
                        Csv(r.TypeBucket),
                        Csv(r.SizeText),
                        Csv(r.Description),
                        Csv(familyTypeIdentity),
                        Csv(e?.Name ?? ""),

                        Csv(GetStringParamInstanceOnly(e, "Material")),
                        Csv(GetStringParamTypeOnly(e, "Material")),

                        Csv(GetStringParamInstanceOnly(e, "Type")),
                        Csv(GetStringParamTypeOnly(e, "Type")),

                        Csv(GetStringParamInstanceOnly(e, "Description")),
                        Csv(GetStringParamTypeOnly(e, "Description")),

                        Csv(GetStringParamInstanceOnly(e, "Family and Type")),
                        Csv(GetStringParamTypeOnly(e, "Family and Type")),

                        Csv(GetStringParamInstanceOnly(e, "Type Name")),
                        Csv(GetStringParamTypeOnly(e, "Type Name")),

                        Csv(GetStringParam(e, BuiltInParameter.RBS_CALCULATED_SIZE)),
                        Csv(GetStringParamInstanceOnly(e, "Size")),
                        Csv(GetStringParamTypeOnly(e, "Size")),

                        Csv(GetStringParam(e, BuiltInParameter.ALL_MODEL_TYPE_NAME))
                    ));
                }

                File.WriteAllLines(path, lines);
            }
            catch
            {
                // Debug must never break report generation.
            }
        }

        private static void WriteFittingSummaryDebugCsv(
            ProcurementConfig cfg,
            List<IGrouping<string, FittingRow>> byMat,
            string stage)
        {
            try
            {
                if (cfg == null || string.IsNullOrWhiteSpace(cfg.TargetFolder))
                    return;

                if (!Directory.Exists(cfg.TargetFolder))
                    return;

                string path = Path.Combine(cfg.TargetFolder, "BOM-FITTING-DEBUG-" + stage + ".csv");

                var lines = new List<string>();

                lines.Add(string.Join(",",
                    Csv("Stage"),
                    Csv("MaterialGrade"),
                    Csv("TypeBucket"),
                    Csv("Qty"),
                    Csv("SizeText"),
                    Csv("Description")
                ));

                foreach (var matGroup in byMat)
                {
                    var byType = matGroup
                        .GroupBy(x => x.TypeBucket)
                        .OrderBy(g => TypeSortOrder(g.Key))
                        .ThenBy(g => g.Key)
                        .ToList();

                    foreach (var typeGroup in byType)
                    {
                        var aggregated = typeGroup
                            .GroupBy(x => new { x.SizeText, x.Description })
                            .Select(g => new
                            {
                                Qty = g.Count(),
                                SizeText = g.Key.SizeText ?? "",
                                Desc = g.Key.Description ?? "",
                                SizeSort = g.Max(z => z.SizeSort)
                            })
                            .OrderByDescending(x => x.SizeSort)
                            .ThenBy(x => x.Desc)
                            .ToList();

                        foreach (var item in aggregated)
                        {
                            lines.Add(string.Join(",",
                                Csv(stage),
                                Csv(matGroup.Key ?? ""),
                                Csv(typeGroup.Key ?? ""),
                                Csv(item.Qty.ToString(CultureInfo.InvariantCulture)),
                                Csv(item.SizeText),
                                Csv(item.Desc)
                            ));
                        }
                    }
                }

                File.WriteAllLines(path, lines);
            }
            catch
            {
                // Debug must never break report generation.
            }
        }

        private static string Csv(string value)
        {
            value = value ?? "";

            if (value.Contains("\""))
                value = value.Replace("\"", "\"\"");

            return "\"" + value + "\"";
        }

        private static string GetStringParamInstanceOnly(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return "";

            Parameter p = e.LookupParameter(paramName);

            return p?.AsString() ?? p?.AsValueString() ?? "";
        }

        private static string GetStringParamTypeOnly(Element e, string paramName)
        {
            if (e == null || string.IsNullOrWhiteSpace(paramName))
                return "";

            ElementId typeId = e.GetTypeId();

            if (typeId == ElementId.InvalidElementId)
                return "";

            Element typeElem = e.Document.GetElement(typeId);

            if (typeElem == null)
                return "";

            Parameter p = typeElem.LookupParameter(paramName);

            return p?.AsString() ?? p?.AsValueString() ?? "";
        }
    }
}
