using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Xml.Linq;
using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Newtonsoft.Json.Linq;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemsPlugin.Helpers
{
    public static class Elements
    {
        #region Pipes
        public static class Pipes
        {
            public static bool IsIgnoreComponents(Element e, Dictionary<ElementId, string> nameCache)
            {
                string nm = GetBestName(e.Document, e, nameCache).ToLowerInvariant();

                foreach (var item in AppConfig.CurrentConfig.PipeIgnoreComponents)
                {
                    if (nm.Contains(item.NameContains.ToLowerInvariant()))
                        return true;
                }

                return false;
            }
            public static bool IsIgnoreComponentsByCat(Element e)
            {
                try
                {
                    var cat = e.Category;
                    if (cat == null || string.IsNullOrWhiteSpace(cat.Name))
                        return false;

                    // Loop through all ignore entries dynamically
                    foreach (var item in AppConfig.CurrentConfig.PipeIgnoreComponents)
                    {
                        if (!string.IsNullOrWhiteSpace(item.NameContains) &&
                            cat.Name.IndexOf(item.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true; // matched one, return true
                        }
                    }
                }
                catch { }

                return false; // no match found
            }

            public static bool IsIgnoreComponentsByFamName(Element e)
            {
                try
                {
                    if (e is FamilyInstance fi && fi.Symbol?.Family != null)
                    {
                        string famName = fi.Symbol.Family.Name ?? string.Empty;

                        // Loop through the config and check dynamically
                        foreach (var item in AppConfig.CurrentConfig.PipeIgnoreComponents)
                        {
                            if (!string.IsNullOrWhiteSpace(item.NameContains) &&
                                famName.IndexOf(item.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch { }

                return false;
            }

            public static string GetElementValue(string elementName)
            {
                var name = elementName.ToLowerInvariant();

                return AppConfig.CurrentConfig.PipeEndPreps
                    .OrderByDescending(i => i.NameContains.Length) // longest first
                    .FirstOrDefault(i => name.Contains(i.NameContains.ToLowerInvariant()))
                    ?.Value;
            }
        }
        #endregion
        #region Fittings
        public static class Fittings
        {
            public static bool IsIgnoreComponents(Element e, Dictionary<ElementId, string> nameCache)
            {
                string nm = GetBestName(e.Document, e, nameCache).ToLowerInvariant();

                foreach (var item in AppConfig.CurrentConfig.FittingsIgnoreComponents)
                {
                    if (nm.Contains(item.NameContains.ToLowerInvariant()))
                        return true;
                }

                return false;
            }
            public static bool IsIgnoreComponentsByCat(Element e)
            {
                try
                {
                    var cat = e.Category;
                    if (cat == null || string.IsNullOrWhiteSpace(cat.Name))
                        return false;

                    // Loop through all ignore entries dynamically
                    foreach (var item in AppConfig.CurrentConfig.FittingsIgnoreComponents)
                    {
                        if (!string.IsNullOrWhiteSpace(item.NameContains) &&
                            cat.Name.IndexOf(item.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return true; // matched one, return true
                        }
                    }
                }
                catch { }

                return false; // no match found
            }

            public static bool IsIgnoreComponentsByFamName(Element e)
            {
                try
                {
                    if (e is FamilyInstance fi && fi.Symbol?.Family != null)
                    {
                        string famName = fi.Symbol.Family.Name ?? string.Empty;

                        // Loop through the config and check dynamically
                        foreach (var item in AppConfig.CurrentConfig.FittingsIgnoreComponents)
                        {
                            if (!string.IsNullOrWhiteSpace(item.NameContains) &&
                                famName.IndexOf(item.NameContains, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return true;
                            }
                        }
                    }
                }
                catch { }

                return false;
            }

            public static string GetElementValue(string elementName)
            {
                var name = elementName.ToLowerInvariant();

                return AppConfig.CurrentConfig.FittingsEndPreps
                    .OrderByDescending(i => i.NameContains.Length) // longest first
                    .FirstOrDefault(i => name.Contains(i.NameContains.ToLowerInvariant()))
                    ?.Value;
            }

            public static string GetPossiblePipeCode(List<string> pipeNames)
            {
                string result = string.Empty;

                foreach (var pipeName in pipeNames)
                {
                    string pipeCode = GetElementValue(pipeName);

                    if (!string.IsNullOrEmpty(pipeCode))
                    {
                        result = pipeCode;
                        break;
                    }
                }

                return result;
            }
        }
        #endregion
        #region PipeWeight
        public static class PipeWeight
        {
            public static void AssignDefault(Element e, PipeWeightParams pipeWeightParams)
            {
                string dryParam = AppConfig.CurrentConfig.PipeWeightMapParameters.DryWeight;
                string wetParam = AppConfig.CurrentConfig.PipeWeightMapParameters.WetWeight;
                string claddingParam = AppConfig.CurrentConfig.PipeMapParameters.CladdingWeight;

                decimal zero = 0;

                if (pipeWeightParams.HaveDry)
                {
                    var dry = e.LookupParameter(dryParam);
                    dry.Set(zero.ToString($"F{pipeWeightParams.NumDecimals}", CultureInfo.InvariantCulture));
                }

                if (pipeWeightParams.HaveWet)
                {
                    var wet = e.LookupParameter(wetParam);
                    wet.Set(zero.ToString($"F{pipeWeightParams.NumDecimals}", CultureInfo.InvariantCulture));
                }

                
            }

            public static double GetSystemAbbreviationDensity(string abbreviation)
            {
                var name = abbreviation.ToLowerInvariant();

                double density = 0;

                var densityModel = AppConfig.CurrentConfig.SystemAbbreviations
                    .OrderByDescending(i => i.AbbreviationContains.Length) // longest first
                    .FirstOrDefault(i => name.Contains(i.AbbreviationContains.ToLowerInvariant()));

                if (densityModel != null)
                    density = densityModel.Density;

                return density;
            }
        }
        #endregion

        public static string GetBestName(Document doc, Element e, Dictionary<ElementId, string> cache)
        {
            if (e == null) return "Unnamed";
            if (cache.TryGetValue(e.Id, out var cached)) return cached;
            string result = "Unnamed";

            //System.Diagnostics.Debug.WriteLine("=============================");
            //foreach (Parameter p in e.Parameters)
            //{
            //    string name = p.Definition.Name;
            //    string value = p.AsValueString() ?? p.AsString() ?? "<no value>";
            //    System.Diagnostics.Debug.WriteLine("Param", $"{name} = {value}");
            //}
            //System.Diagnostics.Debug.WriteLine("=============================");

            try
            {
                var p = e.LookupParameter("Description BOM");
                if (p != null && p.HasValue)
                {
                    var s = p.AsString();
                    if (!string.IsNullOrWhiteSpace(s))
                    {
                        result = s;
                        cache[e.Id] = result;
                        return result;
                    }
                }
            }
            catch { }

            try
            {
                var typeElem = doc.GetElement(e.GetTypeId());
                if (typeElem != null)
                {
                    var nameParam = typeElem.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                    if (nameParam != null && nameParam.HasValue)
                    {
                        string tn = nameParam.AsString();
                        if (!string.IsNullOrWhiteSpace(tn))
                        {
                            result = tn;
                            cache[e.Id] = result;
                            return result;
                        }
                    }
                    if (!string.IsNullOrWhiteSpace(typeElem.Name))
                    {
                        result = typeElem.Name;
                        cache[e.Id] = result;
                        return result;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(e.Name))
                result = e.Name;

            cache[e.Id] = result;
            return result;
        }

        public static bool IsPipe(Element element)
        {
            var param = element?.LookupParameter("Family and Type");
            var value = param?.AsValueString() ?? param?.AsString() ?? "";
            return value.Contains("pipe") && !value.Contains("fittings");
        }
        public static bool HasParam(Element e, string name)
        {
            if (name == "")
                return true;

            return e.LookupParameter(name) != null;
        }

        public static double GetOverallSizeM(this Element element)
        {
            double insulationThicknessM = 0;
            double outsideDiamater = 0;
            double overallSizeM = 0;

            Parameter insulationThicknessParam = element.LookupParameter("Insulation Thickness");

            if (insulationThicknessParam != null)
                insulationThicknessM = insulationThicknessParam.AsDouble().FeetToM();

            Parameter outsideDiameterParameter = element.LookupParameter("Outside Diameter");

            if (outsideDiameterParameter != null)
                outsideDiamater = outsideDiameterParameter.AsDouble().FeetToM();

            if (insulationThicknessM > 0)
            {
                overallSizeM = insulationThicknessM + (2 * outsideDiamater);
            }

            return overallSizeM;
        }

        public static double GetOverallSizeMm(this Element element)
        {
            double insulationThicknessM = 0;
            double outsideDiamater = 0;
            double overallSizeM = 0;

            Parameter insulationThicknessParam = element.LookupParameter("Insulation Thickness");

            if (insulationThicknessParam != null)
                insulationThicknessM = insulationThicknessParam.AsDouble().FeetToMm();

            Parameter outsideDiameterParameter = element.LookupParameter("Outside Diameter");

            if (outsideDiameterParameter != null)
                outsideDiamater = outsideDiameterParameter.AsDouble().FeetToMm();

            if (insulationThicknessM > 0)
            {
                overallSizeM = insulationThicknessM + (2 * outsideDiamater);
            }

            return overallSizeM;
        }

        public static void AddToDebugLog(Element a)
        {
            if (Debugger.IsAttached)
            {
                System.Diagnostics.Debug.WriteLine("=============================");
                foreach (Parameter p in a.Parameters)
                {
                    string name = p.Definition.Name;
                    string value = p.AsValueString() ?? p.AsString() ?? "<no value>";
                    System.Diagnostics.Debug.WriteLine("Param", $"{name} = {value}");
                }
                System.Diagnostics.Debug.WriteLine("=============================");
            }
        }

        public static string GetStringParam(Element e, string paramName)
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
                    return id == null ? "" : RevitApiCompatibility.GetElementIdValue(id).ToString();

                default:
                    return "";
            }
        }

        public static string GetMaterialGrade(Document doc, AssemblyInstance a)
        {
            string materialGrade = "Segment Description";
            // Fix for missing grades: scan ALL members + member types (not just the first member)

            // (1) assembly instance
            var v = GetStringParam(a, materialGrade);
            if (!string.IsNullOrWhiteSpace(v)) return v;

            // (2) assembly type
            var at = doc.GetElement(a.GetTypeId());
            if (at != null)
            {
                v = GetStringParam(at, materialGrade);
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

                    v = GetStringParam(m, materialGrade);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }

                // (4) all member types
                foreach (var mid in members)
                {
                    var m = doc.GetElement(mid);
                    if (m == null) continue;

                    var mt = doc.GetElement(m.GetTypeId());
                    v = GetStringParam(mt, materialGrade);
                    if (!string.IsNullOrWhiteSpace(v)) return v;
                }
            }

            return "";
        }

        public static string GetAssemblyName(Document doc, Element e)
        {
            if (e.AssemblyInstanceId == ElementId.InvalidElementId)
                return "";

            AssemblyInstance assembly =
                doc.GetElement(e.AssemblyInstanceId) as AssemblyInstance;

            return assembly?.Name ?? "";
        }

        public static string GetProcurementPackageNameFromAssembly(
            AssemblyInstance assembly)
        {
            if (assembly == null)
                return "";

            string assemblyName = (assembly.Name ?? "").Trim();
            if (string.IsNullOrWhiteSpace(assemblyName))
                return "";

            // Assembly Register convention:
            // S6P3-L1-P1A-TCSF-087 -> S6P3-L1-P1A-TCSF
            int lastDash = assemblyName.LastIndexOf('-');
            return lastDash > 0
                ? assemblyName.Substring(0, lastDash).Trim()
                : assemblyName;
        }

        public static string GetProcurementPackageName(Document doc, Element element)
        {
            if (doc == null || element == null) return "";

            // Field-material instances can contain unresolved numeric references in
            // Vic_Package. The owning assembly is the authoritative procurement
            // source, so inspect it before the individual family instance.
            AssemblyInstance assembly = null;
            if (element.AssemblyInstanceId != ElementId.InvalidElementId)
                assembly = doc.GetElement(element.AssemblyInstanceId) as AssemblyInstance;

            string package = GetReadableProcurementPackage(doc, assembly);
            if (!string.IsNullOrWhiteSpace(package)) return package;

            Element assemblyType = assembly == null ? null : doc.GetElement(assembly.GetTypeId());
            package = GetReadableProcurementPackage(doc, assemblyType);
            if (!string.IsNullOrWhiteSpace(package)) return package;

            package = GetReadableProcurementPackage(doc, element);
            if (!string.IsNullOrWhiteSpace(package)) return package;

            Element type = doc.GetElement(element.GetTypeId());
            package = GetReadableProcurementPackage(doc, type);
            if (!string.IsNullOrWhiteSpace(package)) return package;

            if (assembly != null)
            {
                foreach (ElementId memberId in assembly.GetMemberIds())
                {
                    Element member = doc.GetElement(memberId);
                    package = GetReadableProcurementPackage(doc, member);
                    if (!string.IsNullOrWhiteSpace(package)) return package;
                }

                foreach (ElementId memberId in assembly.GetMemberIds())
                {
                    Element member = doc.GetElement(memberId);
                    Element memberType = member == null ? null : doc.GetElement(member.GetTypeId());
                    package = GetReadableProcurementPackage(doc, memberType);
                    if (!string.IsNullOrWhiteSpace(package)) return package;
                }
            }

            return "";
        }

        private static string GetReadableProcurementPackage(Document doc, Element element)
        {
            if (doc == null || element == null) return "";

            // Vic_Package is the primary procurement value. Vic_Area_PT is the
            // readable fallback used by older/project-specific content.
            string[] parameterNames = { "Vic_Package", "Vic_Area_PT" };
            foreach (string parameterName in parameterNames)
            {
                string resolved = ResolveProcurementPackageNames(
                    doc,
                    GetStringParam(element, parameterName));

                if (!string.IsNullOrWhiteSpace(resolved))
                    return resolved;
            }

            return "";
        }

        private static string ResolveProcurementPackageNames(Document doc, string rawValue)
        {
            if (doc == null || string.IsNullOrWhiteSpace(rawValue)) return "";

            var resolvedNames = new List<string>();
            string[] values = rawValue.Split(
                new[] { ';', ',', '\r', '\n' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string value in values)
            {
                string token = (value ?? "").Trim();
                if (string.IsNullOrWhiteSpace(token)) continue;

                string displayName = "";
                long elementIdValue;
                if (long.TryParse(token, out elementIdValue))
                {
                    Element packageElement = doc.GetElement(
                        RevitApiCompatibility.CreateElementId(elementIdValue));

                    if (packageElement != null)
                    {
                        displayName = packageElement.Name;
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = GetStringParam(packageElement, "Name");
                        if (string.IsNullOrWhiteSpace(displayName))
                            displayName = GetStringParam(packageElement, "Number");
                    }
                }
                else
                {
                    // Already-readable values should pass through unchanged.
                    displayName = token;
                }

                if (!string.IsNullOrWhiteSpace(displayName) &&
                    !resolvedNames.Any(x => string.Equals(x, displayName, StringComparison.OrdinalIgnoreCase)))
                {
                    resolvedNames.Add(displayName.Trim());
                }
            }

            return string.Join("; ", resolvedNames);
        }

        public static List<SiteMeasureAssemblies> GetSiteMeasureAssemblies(Document revitDoc)
        {
            var siteMeasureAssemblies = new List<SiteMeasureAssemblies>();

            var titleBlocks = new FilteredElementCollector(revitDoc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .ToList();

            foreach (var block in titleBlocks)
            {
                Parameter param = block.LookupParameter("SITE MEASURE");
                Parameter sheetNumberParam = block.LookupParameter("Sheet Number");

                bool isSiteMeasure = param?.AsInteger() == 1;

                //Debug.WriteLine("SITE MEASURE: " + isSiteMeasure + " | " + block.Name);
                //Debug.WriteLine("SHEET NUMBER: " + sheetNumberParam?.AsString());

                // 🔹 STEP 1: Get the sheet that owns this title block
                ViewSheet sheet = revitDoc.GetElement(block.OwnerViewId) as ViewSheet;

                if (sheet == null)
                {
                    Debug.WriteLine("NO SHEET FOUND");
                    continue;
                }

                // 🔹 STEP 2: Get associated assembly from sheet
                ElementId assemblyId = sheet.AssociatedAssemblyInstanceId;

                if (assemblyId != ElementId.InvalidElementId)
                {
                    AssemblyInstance assembly =
                        revitDoc.GetElement(assemblyId) as AssemblyInstance;

                    string assemblyName = assembly?.Name ?? "";

                    if (assemblyName == "")
                        assemblyName = sheetNumberParam?.ToString();

                    if (isSiteMeasure)
                        siteMeasureAssemblies.Add(new SiteMeasureAssemblies { AssemblyName = assemblyName });

                    //Debug.WriteLine("ASSEMBLY: " + assembly?.Name);
                }
                else
                {
                    //Debug.WriteLine("NO ASSEMBLY");
                }
            }

            return siteMeasureAssemblies;
        }
    }
}
