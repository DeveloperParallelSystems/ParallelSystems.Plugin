using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using MigraDoc.DocumentObjectModel;
using MigraDoc.DocumentObjectModel.Tables;
using ParallelSystemsPlugin.Classes;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using RvtDoc = Autodesk.Revit.DB.Document;
using MigraColor = MigraDoc.DocumentObjectModel.Color;
using MigraUnit = MigraDoc.DocumentObjectModel.Unit;

namespace ParallelSystemsPlugin.Reports.Procurement
{
    public class BomFieldMaterialReport
    {
        private const bool ExportDebugCsv = false;
        private const string FieldMaterialParameterName = "Vic_Field Material";

        public static void Generate(RvtDoc doc, ProcurementConfig cfg)
        {
            ParallelSystemsPlugin.Classes.PdfRuntime.EnsureInitialized();

            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (doc.ActiveView == null)
                throw new InvalidOperationException("Active view is not available.");

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "BOM-FIELD MATERIAL.pdf");

            var culture = new CultureInfo("en-US");
            DateTime dt = cfg.Date == default ? DateTime.Today : cfg.Date;
            string dateText = dt.ToString("dddd, dd MMMM yyyy", culture);

            if (ExportDebugCsv)
                ExportFieldMaterialDebugCsv(doc, cfg);

            bool includeSiteMeasureAssemblies = cfg.IncludeSiteMeasure;

            var siteMeasureAssemblies = Helpers.Elements.GetSiteMeasureAssemblies(doc);
            var siteMeasureNames = siteMeasureAssemblies
                .Select(x => x.AssemblyName ?? "")
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var fieldMaterials = CollectFieldMaterialElements(doc)
                .Select(fi => new
                {
                    Instance = fi,
                    AssemblyName = Helpers.Elements.GetAssemblyName(doc, fi)
                })
                .Where(x =>
                    includeSiteMeasureAssemblies ||
                    !siteMeasureNames.Contains(x.AssemblyName ?? ""))
                .Select(x => x.Instance)
                .Where(IsFieldMaterial)
                .ToList();

            var grouped = fieldMaterials
                .Select(fi =>
                {
                    string description = GetReportDescription(fi);

                    return new Data
                    {
                        PackageName = ResolveFieldMaterialPackageName(doc, fi),
                        Category = GetCategory(fi, description),
                        Description = description,
                        Size = GetDN(fi),
                        Quantity = 1
                    };
                })
                .GroupBy(x => new
                {
                    x.PackageName,
                    x.Category,
                    x.Description,
                    x.Size
                })
                .Select(g => new Data
                {
                    PackageName = g.Key.PackageName,
                    Category = g.Key.Category,
                    Description = g.Key.Description,
                    Size = g.Key.Size,
                    Quantity = g.Sum(x => x.Quantity)
                })
                .OrderBy(x => string.IsNullOrWhiteSpace(x.PackageName) ? 1 : 0)
                .ThenBy(x => x.PackageName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => GetCategorySortOrder(x.Category))
                .ThenBy(x => TryParseSizeNumber(x.Size))
                .ThenBy(x => x.Description)
                .ToList();

            string note = "";

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
            section.PageSetup.TopMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.BottomMargin = MigraUnit.FromCentimeter(1.5);
            section.PageSetup.LeftMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.RightMargin = MigraUnit.FromCentimeter(1.0);
            section.PageSetup.FooterDistance = MigraUnit.FromCentimeter(1.0);

            PdfLayoutHelpers.DrawHeader(section, cfg, "FIELD MATERIAL REPORT", dateText);

            PdfLayoutHelpers.AddFooter(section, note, !cfg.IncludeSiteMeasure);

            bool shade = false;
            string currentGroup = "";
            Table table = null;

            foreach (var r in grouped)
            {
                string groupName = r.Category ?? "";
                if (string.IsNullOrWhiteSpace(currentGroup) ||
                    !string.Equals(currentGroup, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(currentGroup))
                    {
                        var spacer = section.AddParagraph();
                        spacer.Format.SpaceBefore = MigraUnit.FromCentimeter(0.6);
                    }

                    AddTypeBand(
                        section,
                        r.Category ?? "");
                    table = BuildMainTable(section);
                    AddMainHeaderRow(table);

                    currentGroup = groupName;
                    shade = false;
                }

                var tr = table.AddRow();
                tr.VerticalAlignment = VerticalAlignment.Center;

                if (shade)
                    tr.Shading.Color = Colors.WhiteSmoke;

                shade = !shade;

                tr.Cells[0].AddParagraph(r.PackageName ?? "");
                tr.Cells[1].AddParagraph(r.Size ?? "");
                tr.Cells[2].AddParagraph(r.Description ?? "");
                tr.Cells[3].AddParagraph(r.Quantity.ToString(CultureInfo.InvariantCulture));

                tr.Cells[3].Format.Alignment = ParagraphAlignment.Right;
            }

            section.AddParagraph().Format.SpaceBefore = MigraUnit.FromCentimeter(0.6);

            builder.Save(outPath);
            }

            if (cfg.ExportReportsToExcel)
                ExportExcel(cfg, grouped, note);
        }

        private static List<FamilyInstance> CollectFieldMaterialElements(RvtDoc doc)
        {
            var results = new List<FamilyInstance>();

            results.AddRange(
                new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfCategory(BuiltInCategory.OST_PipeAccessory)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>());

            results.AddRange(
                new FilteredElementCollector(doc, doc.ActiveView.Id)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .OfType<FamilyInstance>());

            return results;
        }

        private static bool IsFieldMaterial(FamilyInstance fi)
        {
            if (fi == null)
                return false;

            Parameter instanceParameter =
                fi.LookupParameter(FieldMaterialParameterName);

            if (instanceParameter != null)
                return IsChecked(instanceParameter);

            Parameter typeParameter =
                fi.Symbol?.LookupParameter(FieldMaterialParameterName);

            return IsChecked(typeParameter);
        }

        private static string ResolveFieldMaterialPackageName(
            RvtDoc doc,
            FamilyInstance fieldMaterial)
        {
            if (doc == null || fieldMaterial == null)
                return "";

            // The owning assembly is authoritative when the field material is
            // already part of an assembly.
            if (fieldMaterial.AssemblyInstanceId != ElementId.InvalidElementId)
            {
                AssemblyInstance assembly =
                    doc.GetElement(fieldMaterial.AssemblyInstanceId) as AssemblyInstance;

                return Helpers.Elements
                    .GetProcurementPackageNameFromAssembly(assembly);
            }

            // For loose field material, follow both connector sides to their
            // pipes, treating Victaulic rigid/flex couplings as transparent.
            // The pipe's owning assembly determines the package using the
            // exact same assembly-name rule as Assembly Register.
            List<Element> connectedElements = GetDirectlyConnectedElements(fieldMaterial);

            foreach (Pipe connectedPipe in GetConnectedPipesThroughVictaulicCouplings(
                fieldMaterial)
                .OrderBy(x => RevitApiCompatibility.GetElementIdValue(x.Id)))
            {
                string connectedPackage = GetPackageFromOwningAssembly(
                    doc,
                    connectedPipe);

                if (!string.IsNullOrWhiteSpace(connectedPackage))
                    return connectedPackage;
            }

            // Some field-material families connect directly to another MEP
            // component instead of a pipe. Use that component's assembly only;
            // never use a loose component's own package parameter here.
            foreach (Element connected in connectedElements
                .Where(x => !(x is Pipe))
                .OrderBy(x => RevitApiCompatibility.GetElementIdValue(x.Id)))
            {
                string connectedPackage = GetPackageFromOwningAssembly(
                    doc,
                    connected);

                if (!string.IsNullOrWhiteSpace(connectedPackage))
                    return connectedPackage;
            }

            return "";
        }

        private static List<Pipe> GetConnectedPipesThroughVictaulicCouplings(
            FamilyInstance fieldMaterial)
        {
            const int maximumCouplingHops = 8;
            var pipes = new List<Pipe>();
            var pipeIds = new HashSet<long>();
            var visited = new HashSet<long>();
            var queue = new Queue<ConnectionTraversalItem>();

            if (fieldMaterial == null)
                return pipes;

            visited.Add(RevitApiCompatibility.GetElementIdValue(fieldMaterial.Id));

            foreach (Element connected in GetDirectlyConnectedElements(fieldMaterial))
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
                !IsBuiltInCategory(familyInstance, BuiltInCategory.OST_PipeFitting))
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

        private static string GetPackageFromOwningAssembly(
            RvtDoc doc,
            Element element)
        {
            if (doc == null || element == null ||
                element.AssemblyInstanceId == ElementId.InvalidElementId)
            {
                return "";
            }

            AssemblyInstance assembly =
                doc.GetElement(element.AssemblyInstanceId) as AssemblyInstance;

            return Helpers.Elements
                .GetProcurementPackageNameFromAssembly(assembly);
        }

        private static List<Element> GetDirectlyConnectedElements(
            Element source)
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
                // An unresolved connector graph leaves the package unassigned;
                // report generation must continue for the field-material item.
            }

            return connected;
        }

        private sealed class ConnectionTraversalItem
        {
            public Element Element { get; set; }
            public int CouplingHops { get; set; }
        }

        private static bool IsChecked(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return false;

            try
            {
                if (parameter.StorageType == StorageType.Integer)
                    return parameter.AsInteger() != 0;

                string value =
                    parameter.AsString() ??
                    parameter.AsValueString();

                return
                    string.Equals(value, "1", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string GetReportDescription(FamilyInstance fi)
        {
            string typeDescription =
                GetStringParameter(fi.Symbol, "Description") ??
                GetStringParameter(fi, "Description") ??
                GetStringParameter(fi.Symbol, "BOM Description") ??
                GetStringParameter(fi, "BOM Description") ??
                GetStringParameter(fi.Symbol, "Procurement Description") ??
                GetStringParameter(fi, "Procurement Description");

            string manufacturer = GetStringParameter(fi.Symbol, "Manufacturer");
            string familyName = fi.Symbol?.Family?.Name ?? "";
            string typeName = fi.Symbol?.Name ?? "";

            if (!string.IsNullOrWhiteSpace(typeDescription))
            {
                if (IsFlowMeter(fi) && !string.IsNullOrWhiteSpace(manufacturer))
                {
                    if (!typeDescription.ToUpperInvariant().Contains(manufacturer.ToUpperInvariant()))
                    {
                        return string.Format(
                            CultureInfo.InvariantCulture,
                            "{0} - {1}",
                            manufacturer.ToUpperInvariant(),
                            typeDescription.ToUpperInvariant());
                    }
                }

                return typeDescription.Trim();
            }

            if (!string.IsNullOrWhiteSpace(typeName) &&
                !string.Equals(typeName, "Standard", StringComparison.OrdinalIgnoreCase))
            {
                return typeName.Trim();
            }

            return familyName.Trim();
        }

        private static bool IsFlowMeter(FamilyInstance fi)
        {
            string key = GetSearchKey(fi);

            return
                key.Contains("FLOW METER") ||
                key.Contains("FLOWMETER") ||
                key.Contains("7ME6580");
        }

        private static string GetCategory(FamilyInstance fi, string description)
        {
            string key = (GetSearchKey(fi) + " " + (description ?? "")).ToUpperInvariant();

            // Keep these groups aligned with FilterItemsCommand.GetGroupName.
            if (IsBuiltInCategory(fi, BuiltInCategory.OST_PipeFitting))
            {
                if (ContainsAny(key, "SHAPED BRANCH", "WELDOLET", "OLET"))
                    return "SHAPED BRANCH";

                if (ContainsAny(key, "WELD"))
                    return "WELD";

                if (ContainsAny(key, "ELBOW"))
                    return "ELBOW";

                if (ContainsAny(key, "END CAP", "ENDCAP", "CAP"))
                    return "END CAP";

                if (ContainsAny(key, "FLANGE"))
                    return "FLANGE";

                if (ContainsAny(key, "REDUCER", "REDUCTION"))
                    return "REDUCER";

                if (ContainsAny(key, "COUPLING", "SOCKET"))
                    return "COUPLING";

                if (ContainsAny(key, "TEE", "T-E"))
                    return "TEE";

                if (ContainsAny(key, "BRANCH"))
                    return "SHAPED BRANCH";

                return "OTHER FITTING";
            }

            if (ContainsAny(key, "FLOW METER", "FLOWMETER", "7ME6580"))
                return "FLOW METER";

            if (ContainsAny(key, "VALVE", "BUTTERFLY"))
                return "VALVE";

            if (ContainsAny(key, "STRAINER"))
                return "STRAINER";

            if (ContainsAny(key, "GAUGE"))
                return "GAUGE";

            if (ContainsAny(key, "INSTRUMENT"))
                return "INSTRUMENT";

            if (ContainsAny(
                key,
                "FLANGE",
                "TRI-CLAMP",
                "TRICLAMP",
                "TRI CLAMP",
                "TRI-CLOVER",
                "TRICLOVER"))
            {
                return "PIPE ACCESSORY - FLANGE / CLAMP";
            }

            return "PIPE ACCESSORY";
        }

        private static bool IsBuiltInCategory(
            Element element,
            BuiltInCategory category)
        {
            if (element?.Category == null)
                return false;

            return RevitApiCompatibility
                .GetElementIdValue(element.Category.Id) == (long)category;
        }

        private static bool ContainsAny(string text, params string[] values)
        {
            foreach (string value in values)
            {
                if (text.IndexOf(value, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static int GetCategorySortOrder(string category)
        {
            switch ((category ?? "").ToUpperInvariant())
            {
                case "ELBOW": return 10;
                case "COUPLING": return 20;
                case "TEE": return 30;
                case "REDUCER": return 40;
                case "END CAP": return 50;
                case "FLANGE": return 60;
                case "SHAPED BRANCH": return 70;
                case "WELD": return 80;
                case "VALVE": return 90;
                case "FLOW METER": return 100;
                case "STRAINER": return 110;
                case "GAUGE": return 120;
                case "INSTRUMENT": return 130;
                case "PIPE ACCESSORY - FLANGE / CLAMP": return 140;
                case "PIPE ACCESSORY": return 150;
                case "OTHER FITTING": return 160;
                default: return 999;
            }
        }

        private static string GetSearchKey(FamilyInstance fi)
        {
            if (fi == null)
                return "";

            string familyName = fi.Symbol?.Family?.Name ?? "";
            string typeName = fi.Symbol?.Name ?? "";
            string instanceName = fi.Name ?? "";

            string instanceDescription = GetStringParameter(fi, "Description");
            string typeDescription = GetStringParameter(fi.Symbol, "Description");

            string instanceBomDescription = GetStringParameter(fi, "BOM Description");
            string typeBomDescription = GetStringParameter(fi.Symbol, "BOM Description");

            string instanceProcDescription = GetStringParameter(fi, "Procurement Description");
            string typeProcDescription = GetStringParameter(fi.Symbol, "Procurement Description");

            string manufacturer = GetStringParameter(fi.Symbol, "Manufacturer");
            string model = GetStringParameter(fi.Symbol, "Model");

            return string.Join(" ",
                    familyName,
                    typeName,
                    instanceName,
                    instanceDescription,
                    typeDescription,
                    instanceBomDescription,
                    typeBomDescription,
                    instanceProcDescription,
                    typeProcDescription,
                    manufacturer,
                    model)
                .ToUpperInvariant();
        }

        private static string GetStringParameter(Element e, string parameterName)
        {
            if (e == null || string.IsNullOrWhiteSpace(parameterName))
                return null;

            Parameter p = e.LookupParameter(parameterName);

            if (p == null || !p.HasValue)
                return null;

            if (p.StorageType == StorageType.String)
                return p.AsString();

            return p.AsValueString();
        }

        private static string GetDN(FamilyInstance fi)
        {
            double diameterFeet = 0;

            // 1. Try connector diameter first.
            if (fi.MEPModel?.ConnectorManager != null)
            {
                try
                {
                    foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                    {
                        if (c == null)
                            continue;

                        if (c.Domain == Domain.DomainPiping && c.Radius > 0)
                        {
                            diameterFeet = c.Radius * 2;
                            break;
                        }
                    }
                }
                catch
                {
                    diameterFeet = 0;
                }
            }

            // 2. Try built-in pipe diameter.
            if (diameterFeet == 0)
            {
                Parameter p = fi.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);

                if (p != null && p.HasValue && p.StorageType == StorageType.Double)
                    diameterFeet = p.AsDouble();
            }

            // 3. Try instance parameters.
            if (diameterFeet == 0)
            {
                string textSize = TryGetTextSize(fi);

                if (!string.IsNullOrWhiteSpace(textSize))
                    return NormalizeSize(textSize);

                Parameter p =
                    fi.LookupParameter("Nominal Diameter") ??
                    fi.LookupParameter("Diameter") ??
                    fi.LookupParameter("Size") ??
                    fi.LookupParameter("DN") ??
                    fi.LookupParameter("Pipe Size");

                if (p != null && p.HasValue)
                {
                    if (p.StorageType == StorageType.Double)
                    {
                        diameterFeet = p.AsDouble();
                    }
                    else
                    {
                        return NormalizeSize(p.AsString() ?? p.AsValueString());
                    }
                }
            }

            // 4. Try type parameters.
            if (diameterFeet == 0 && fi.Symbol != null)
            {
                string textSize = TryGetTextSize(fi.Symbol);

                if (!string.IsNullOrWhiteSpace(textSize))
                    return NormalizeSize(textSize);

                Parameter p =
                    fi.Symbol.LookupParameter("Nominal Diameter") ??
                    fi.Symbol.LookupParameter("Diameter") ??
                    fi.Symbol.LookupParameter("Size") ??
                    fi.Symbol.LookupParameter("DN") ??
                    fi.Symbol.LookupParameter("Pipe Size");

                if (p != null && p.HasValue)
                {
                    if (p.StorageType == StorageType.Double)
                    {
                        diameterFeet = p.AsDouble();
                    }
                    else
                    {
                        return NormalizeSize(p.AsString() ?? p.AsValueString());
                    }
                }
            }

            // 5. Convert Revit internal feet to millimeters.
            if (diameterFeet > 0)
            {
                double mm = UnitUtils.ConvertFromInternalUnits(
                    diameterFeet,
                    UnitTypeId.Millimeters);

                return Math.Round(mm).ToString(CultureInfo.InvariantCulture);
            }

            return "";
        }

        private static string TryGetTextSize(Element e)
        {
            string[] names =
            {
                "Nominal Diameter",
                "Diameter",
                "Size",
                "DN",
                "NPS",
                "Pipe Size"
            };

            foreach (string name in names)
            {
                string value = GetStringParameter(e, name);

                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static string NormalizeSize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string s = value.Trim();

            s = s.Replace("DN", "");
            s = s.Replace("dn", "");
            s = s.Replace("Dn", "");
            s = s.Replace("dN", "");
            s = s.Replace("Ø", "");
            s = s.Replace("MM", "");
            s = s.Replace("mm", "");
            s = s.Trim();

            var chars = new List<char>();

            foreach (char ch in s)
            {
                if (char.IsDigit(ch) || ch == '.')
                {
                    chars.Add(ch);
                }
                else if (chars.Count > 0)
                {
                    break;
                }
            }

            string numeric = new string(chars.ToArray());

            double d;
            if (double.TryParse(numeric, NumberStyles.Any, CultureInfo.InvariantCulture, out d))
            {
                if (Math.Abs(d - Math.Round(d)) < 0.0001)
                    return Math.Round(d).ToString(CultureInfo.InvariantCulture);

                return d.ToString("0.##", CultureInfo.InvariantCulture);
            }

            return s;
        }

        private static double TryParseSizeNumber(string size)
        {
            if (string.IsNullOrWhiteSpace(size))
                return double.MaxValue;

            string cleaned = NormalizeSize(size);
            string number = new string(cleaned.Where(c => char.IsDigit(c) || c == '.').ToArray());

            double result;
            if (double.TryParse(number, NumberStyles.Any, CultureInfo.InvariantCulture, out result))
                return result;

            return double.MaxValue;
        }

        private static void AddTypeBand(Section section, string bucket)
        {
            MigraColor fill = Colors.LightGray;
            MigraColor text = Colors.Black;

            switch ((bucket ?? "").ToUpperInvariant())
            {
                case "FLOW METER":
                    fill = MigraColor.FromRgb(217, 217, 217);
                    text = Colors.Black;
                    break;

                case "VALVE":
                    fill = MigraColor.FromRgb(112, 48, 160);
                    text = Colors.White;
                    break;

                case "PIPE ACCESSORY - FLANGE / CLAMP":
                    fill = MigraColor.FromRgb(198, 239, 206);
                    text = Colors.Black;
                    break;

                default:
                    fill = MigraColor.FromRgb(220, 220, 220);
                    text = Colors.Black;
                    break;
            }

            var t = section.AddTable();
            t.Borders.Visible = false;
            t.AddColumn(MigraUnit.FromCentimeter(27.7));

            var r = t.AddRow();
            r.Height = MigraUnit.FromCentimeter(0.55);

            var c = r.Cells[0];
            c.Shading.Color = fill;
            c.VerticalAlignment = VerticalAlignment.Center;

            var p = c.AddParagraph(bucket ?? "TYPE");
            p.Format.Font.Name = "Arial";
            p.Format.Font.Bold = true;
            p.Format.Font.Size = 11;
            p.Format.Font.Color = text;
            p.Format.Alignment = ParagraphAlignment.Center;
            p.Format.SpaceBefore = 0;
            p.Format.SpaceAfter = 0;

            section.AddParagraph().Format.SpaceAfter = MigraUnit.FromCentimeter(0.10);
        }

        private static void AddMainHeaderRow(Table table)
        {
            var hr = table.AddRow();
            hr.Shading.Color = Colors.WhiteSmoke;
            hr.Format.Font.Bold = true;
            hr.VerticalAlignment = VerticalAlignment.Center;

            hr.Cells[0].AddParagraph("Package Name");
            hr.Cells[1].AddParagraph("Size");
            hr.Cells[2].AddParagraph("Description");
            hr.Cells[3].AddParagraph("Qty");

            hr.Cells[3].Format.Alignment = ParagraphAlignment.Right;
        }

        private static Table BuildMainTable(Section section)
        {
            var table = section.AddTable();

            table.Borders.Width = 0.25;
            table.Borders.Color = Colors.Gray;

            table.AddColumn(MigraUnit.FromCentimeter(6.0));   // Package Name
            table.AddColumn(MigraUnit.FromCentimeter(3.5));   // Size
            table.AddColumn(MigraUnit.FromCentimeter(15.0));  // Description
            table.AddColumn(MigraUnit.FromCentimeter(3.2));   // Qty

            return table;
        }

        private static void ExportExcel(ProcurementConfig cfg, List<Data> grouped, string note)
        {
            var sheet = ExcelReportExporter.CreateReportSheet(
                cfg,
                "FIELD MATERIAL REPORT",
                new[] { "Package Name", "Size", "Description", "Qty" },
                note);

            bool alt = false;
            string currentGroup = "";

            foreach (var r in grouped)
            {
                string groupName = r.Category ?? "";
                if (string.IsNullOrWhiteSpace(currentGroup) ||
                    !string.Equals(currentGroup, groupName, StringComparison.OrdinalIgnoreCase))
                {
                    currentGroup = groupName;
                    sheet.Add(
                        ExcelReportExporter.RowKind.Group,
                        r.Category ?? "");
                    alt = false;
                }

                sheet.Add(
                    alt
                        ? ExcelReportExporter.RowKind.AlternateData
                        : ExcelReportExporter.RowKind.Data,
                    r.PackageName ?? "",
                    r.Size ?? "",
                    r.Description ?? "",
                    r.Quantity);

                alt = !alt;
            }

            if (!string.IsNullOrWhiteSpace(note))
            {
                sheet.Add(ExcelReportExporter.RowKind.Blank);
                sheet.Add(ExcelReportExporter.RowKind.Note, note);
            }

            ExcelReportExporter.SaveWorkbook(
                ExcelReportExporter.BuildOutputPath(cfg, "BOM-FIELD MATERIAL"),
                new[] { sheet });
        }

        private static void ExportFieldMaterialDebugCsv(RvtDoc doc, ProcurementConfig cfg)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (cfg == null) throw new ArgumentNullException(nameof(cfg));

            if (doc.ActiveView == null)
                throw new InvalidOperationException("Active view is not available.");

            if (string.IsNullOrWhiteSpace(cfg.TargetFolder) || !Directory.Exists(cfg.TargetFolder))
                throw new InvalidOperationException("TargetFolder is empty or does not exist.");

            string outPath = Path.Combine(cfg.TargetFolder, "BOM-FIELD-MATERIAL-DEBUG.csv");

            var rows = new List<string[]>();

            rows.Add(new[]
            {
                "Category",
                "ElementId",
                "AssemblyName",
                "FamilyName",
                "TypeName",
                "InstanceName",
                "Instance Description",
                "Type Description",
                "Instance BOM Description",
                "Type BOM Description",
                "Instance Procurement Description",
                "Type Procurement Description",
                "Manufacturer",
                "Model",
                "Type Mark",
                "Comments",
                "Nominal Diameter Instance",
                "Nominal Diameter Type",
                "Diameter Instance",
                "Diameter Type",
                "Size Instance",
                "Size Type",
                "DN Instance",
                "DN Type",
                "Connector Sizes",
                "Calculated GetDN",
                "Search Key",
                "Is Current Field Material Match"
            });

            var elements = CollectFieldMaterialElements(doc);

            foreach (FamilyInstance fi in elements
                .OrderBy(x => x.Category?.Name)
                .ThenBy(x => x.Symbol?.Family?.Name)
                .ThenBy(x => x.Symbol?.Name))
            {
                string category = fi.Category?.Name ?? "";
                string elementId = RevitApiCompatibility
                    .GetElementIdValue(fi.Id)
                    .ToString(CultureInfo.InvariantCulture);

                string assemblyName = Helpers.Elements.GetAssemblyName(doc, fi);

                string familyName = fi.Symbol?.Family?.Name ?? "";
                string typeName = fi.Symbol?.Name ?? "";
                string instanceName = fi.Name ?? "";

                string instanceDescription = GetParamDebugValue(fi, "Description");
                string typeDescription = GetParamDebugValue(fi.Symbol, "Description");

                string instanceBomDescription = GetParamDebugValue(fi, "BOM Description");
                string typeBomDescription = GetParamDebugValue(fi.Symbol, "BOM Description");

                string instanceProcDescription = GetParamDebugValue(fi, "Procurement Description");
                string typeProcDescription = GetParamDebugValue(fi.Symbol, "Procurement Description");

                string manufacturer = GetParamDebugValue(fi.Symbol, "Manufacturer");
                string model = GetParamDebugValue(fi.Symbol, "Model");

                string typeMark = FirstNotEmpty(
                    GetParamDebugValue(fi, "Type Mark"),
                    GetParamDebugValue(fi.Symbol, "Type Mark"));

                string comments = FirstNotEmpty(
                    GetParamDebugValue(fi, "Comments"),
                    GetParamDebugValue(fi.Symbol, "Comments"));

                string nominalDiameterInstance = GetParamDebugValue(fi, "Nominal Diameter");
                string nominalDiameterType = GetParamDebugValue(fi.Symbol, "Nominal Diameter");

                string diameterInstance = GetParamDebugValue(fi, "Diameter");
                string diameterType = GetParamDebugValue(fi.Symbol, "Diameter");

                string sizeInstance = GetParamDebugValue(fi, "Size");
                string sizeType = GetParamDebugValue(fi.Symbol, "Size");

                string dnInstance = GetParamDebugValue(fi, "DN");
                string dnType = GetParamDebugValue(fi.Symbol, "DN");

                string connectorSizes = GetConnectorSizesDebug(fi);
                string calculatedDn = GetDN(fi);
                string searchKey = GetSearchKey(fi);

                bool isCurrentMatch = IsFieldMaterial(fi);

                rows.Add(new[]
                {
                    category,
                    elementId,
                    assemblyName,
                    familyName,
                    typeName,
                    instanceName,
                    instanceDescription,
                    typeDescription,
                    instanceBomDescription,
                    typeBomDescription,
                    instanceProcDescription,
                    typeProcDescription,
                    manufacturer,
                    model,
                    typeMark,
                    comments,
                    nominalDiameterInstance,
                    nominalDiameterType,
                    diameterInstance,
                    diameterType,
                    sizeInstance,
                    sizeType,
                    dnInstance,
                    dnType,
                    connectorSizes,
                    calculatedDn,
                    searchKey,
                    isCurrentMatch ? "YES" : "NO"
                });
            }

            var sb = new StringBuilder();

            foreach (string[] row in rows)
            {
                sb.AppendLine(string.Join(",", row.Select(EscapeCsv)));
            }

            File.WriteAllText(outPath, sb.ToString(), Encoding.UTF8);
        }

        private static string GetParamDebugValue(Element e, string parameterName)
        {
            if (e == null || string.IsNullOrWhiteSpace(parameterName))
                return "";

            Parameter p = e.LookupParameter(parameterName);

            if (p == null)
                return "";

            try
            {
                if (!p.HasValue)
                    return "";

                switch (p.StorageType)
                {
                    case StorageType.String:
                        return p.AsString() ?? "";

                    case StorageType.Double:
                        return p.AsValueString() ?? p.AsDouble().ToString(CultureInfo.InvariantCulture);

                    case StorageType.Integer:
                        return p.AsValueString() ?? p.AsInteger().ToString(CultureInfo.InvariantCulture);

                    case StorageType.ElementId:
                        return p.AsValueString()
                            ?? RevitApiCompatibility
                                .GetElementIdValue(p.AsElementId())
                                .ToString(CultureInfo.InvariantCulture);

                    default:
                        return p.AsValueString() ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static string GetConnectorSizesDebug(FamilyInstance fi)
        {
            if (fi?.MEPModel?.ConnectorManager == null)
                return "";

            var sizes = new List<string>();

            try
            {
                foreach (Connector c in fi.MEPModel.ConnectorManager.Connectors)
                {
                    if (c == null)
                        continue;

                    if (c.Domain != Domain.DomainPiping)
                        continue;

                    if (c.Radius <= 0)
                        continue;

                    double diameterFeet = c.Radius * 2;

                    double mm = UnitUtils.ConvertFromInternalUnits(
                        diameterFeet,
                        UnitTypeId.Millimeters);

                    sizes.Add(Math.Round(mm).ToString(CultureInfo.InvariantCulture));
                }
            }
            catch
            {
                return "";
            }

            return string.Join(" | ", sizes.Distinct());
        }

        private static string FirstNotEmpty(params string[] values)
        {
            if (values == null)
                return "";

            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return "";
        }

        private static string EscapeCsv(string value)
        {
            if (value == null)
                return "";

            bool mustQuote =
                value.Contains(",") ||
                value.Contains("\"") ||
                value.Contains("\r") ||
                value.Contains("\n");

            string escaped = value.Replace("\"", "\"\"");

            return mustQuote ? "\"" + escaped + "\"" : escaped;
        }

        public static List<Pipe> GetAllPipes(RvtDoc doc)
        {
            if (doc?.ActiveView == null)
                return new List<Pipe>();

            return new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfClass(typeof(Pipe))
                .Cast<Pipe>()
                .ToList();
        }

        public static double GetPipeDiameterMm(Pipe pipe)
        {
            if (pipe == null)
                return 0;

            Parameter p = pipe.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);

            if (p == null || p.StorageType != StorageType.Double)
                return 0;

            double diameterFeet = p.AsDouble();

            return UnitUtils.ConvertFromInternalUnits(
                diameterFeet,
                UnitTypeId.Millimeters);
        }

        private sealed class Data
        {
            public string PackageName { get; set; }
            public string Category { get; set; }
            public string Description { get; set; }
            public string Size { get; set; }
            public int Quantity { get; set; }
        }
    }
}
