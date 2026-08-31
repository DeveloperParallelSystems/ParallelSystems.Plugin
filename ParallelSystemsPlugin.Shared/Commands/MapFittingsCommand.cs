using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;   // ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MapFittingsCommand : IExternalCommand
    {
        private const double FT_TO_MM = 304.8;

        // ==== Tokens / Regex (from your Python) ====
        private static readonly string[] TOK_ELBOWISH = { "elbow", "tee", "cross", "reducer", "lateral", "wye" };
        private static readonly HashSet<string> RG_LIST = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coupling" };
        private static readonly HashSet<string> SC_LIST = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "branch" };
        private static readonly Regex RE_STUB_END = new Regex(@"\bstub end\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex RE_END = new Regex(@"\bend\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const string PAGE_TITLE = "Fittings End Prep";

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {

            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            UIApplication uiapp = data.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = uidoc.ActiveView;

            string end1 = AppConfig.CurrentConfig.FittingsMapParameters.End1;
            string end2 = AppConfig.CurrentConfig.FittingsMapParameters.End2;
            string endPrep = AppConfig.CurrentConfig.FittingsMapParameters.EndPrep;
            string headerND = AppConfig.CurrentConfig.FittingsMapParameters.HeaderND;

            List<string> parameters = new List<string>();

            if (!string.IsNullOrEmpty(end1))
                parameters.Add(end1);
            if (!string.IsNullOrEmpty(end2))
                parameters.Add(end2);
            if (!string.IsNullOrEmpty(endPrep))
                parameters.Add(endPrep);
            if (!string.IsNullOrEmpty(headerND))
                parameters.Add(headerND);

            // Collect nipples or shaped branches (fast string rules with fallback)
            IList<Element> fittings = CollectTargetFittingsByConfig(doc, activeView.Id);
          
            if (fittings.Count == 0)
            {
                AppDialog.Info(uiapp, PAGE_TITLE, "No target pipe fittings found in the active view.");
                return Result.Succeeded;
            }

            string previousElem = "";
          
            foreach(var sample in fittings.OrderBy(e => e.Name))
            {
                if (previousElem == sample.Name)
                    continue;

                previousElem = sample.Name;

                if (sample == null
                || !(HasParam(sample, end1) && HasParam(sample, end2) && HasParam(sample, endPrep) && HasParam(sample, headerND))
                )
                {

                    string promptMessage = "No eligible fittings or missing parameters. Fittings must have text parameters: " +
                        ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.FittingsMapParameters);

                    AppDialog.Confirm(
                        uiapp,
                        PAGE_TITLE,
                        promptMessage);

                    return Result.Succeeded;
                }
            }
           

            //// Preflight: at least one fitting with needed params?
            //Element sample = new FilteredElementCollector(doc, activeView.Id)
            //    .OfCategory(BuiltInCategory.OST_PipeFitting)
            //    .WhereElementIsNotElementType()
            //    .FirstElement();



            // Caches
            Dictionary<ElementId, string> bestNameCache = new Dictionary<ElementId, string>();
            Dictionary<ElementId, string> famNameLower = new Dictionary<ElementId, string>();
            Dictionary<ElementId, bool> isPipeCache = new Dictionary<ElementId, bool>();
            Dictionary<ElementId, ConnectorManager> cmCache = new Dictionary<ElementId, ConnectorManager>();
            Dictionary<ElementId, string> endPrepCache = new Dictionary<ElementId, string>();
            Dictionary<ElementId, List<Element>> mainConnCache = new Dictionary<ElementId, List<Element>>();
            var diameterCache = new Dictionary<ElementId, double?>();

            int updated = 0;

            var win = new ProgressWindow();
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                new WindowInteropHelper(win).Owner = hwnd;

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(fittings.Count, "Mapping Fittings End Prep…", "Procesing...");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (Transaction tx = new Transaction(doc, "Map Fitting End Prep"))
                {
                    tx.Start();

                    int i = 0;
                    foreach (Element fitting in fittings)
                    {
                        if (win.IsCanceled) break;

                        Parameter pC1 = fitting.LookupParameter(end1);
                        Parameter pC2 = fitting.LookupParameter(end2);
                        Parameter pPEP = fitting.LookupParameter(endPrep);
                        Parameter pHeaderND = fitting.LookupParameter(headerND);
                      
                        if (
                            (!string.IsNullOrEmpty(end1) && pC1 == null)
                            || (!string.IsNullOrEmpty(end2) && pC2 == null)
                            || (!string.IsNullOrEmpty(endPrep) && pPEP == null)
                            || (!string.IsNullOrEmpty(headerND) && pHeaderND == null)
                            )
                        {
                            i++;
                            if ((i & 31) == 0) win.Update(i);
                            continue;
                        }

                        // Neighbors (skip weld/insulation/non-connector; cap recursion)
                        List<Element> neigh = GetConnectedMainEnds(fitting, cmCache, bestNameCache, famNameLower, isPipeCache, mainConnCache);
                        Element c1Elem = neigh.Count > 0 ? neigh[0] : null;
                        Element c2Elem = neigh.Count > 1 ? neigh[1] : null;

                        // Inspect connected pipes, take the largest diameter
                        double? maxDiamFt = null;
                        foreach (var e in neigh)
                        {
                            if (!IsPipe(e, isPipeCache)) continue;
                            double? d = GetPipeDiameterFeet(e, diameterCache);
                            if (d.HasValue && (!maxDiamFt.HasValue || d.Value > maxDiamFt.Value))
                                maxDiamFt = d;
                        }

                        string defaultUnconnectedValue = "Unconnected";
                        
                        string strUnconnected = AppConfig.CurrentConfig.FittingsMapParameters.EnableMapping ? AppConfig.CurrentConfig.FittingsMapParameters.Unconnected : defaultUnconnectedValue;

                        if (string.IsNullOrEmpty(strUnconnected))
                            strUnconnected = defaultUnconnectedValue;

                        // Names
                        string c1Name = c1Elem != null ? GetBestName(doc, c1Elem, bestNameCache) : strUnconnected;
                        string c2Name = c2Elem != null ? GetBestName(doc, c2Elem, bestNameCache) : strUnconnected;

                        // End-preps (base mapping)
                        string prep1 = MapToEndPrep(doc, c1Elem, bestNameCache, famNameLower, isPipeCache, endPrepCache);
                        string prep2 = MapToEndPrep(doc, c2Elem, bestNameCache, famNameLower, isPipeCache, endPrepCache);

                        string nippleCode = ParallelSystemsPlugin.Helpers.Elements.Fittings.GetElementValue("nipple") ?? "";
                        string pipeCode = "";

                        if (IsPipe(c1Elem, isPipeCache))
                            pipeCode = prep1;
                        else if(string.IsNullOrEmpty(pipeCode) && IsPipe(c2Elem, isPipeCache))
                            pipeCode = prep2;
                        else
                            pipeCode = Elements.Fittings.GetPossiblePipeCode(doc.GetAllPipeNames());

                        // Nipple overrides (from Python)
                        bool nipple = IsNipple(fitting);
                        
                        if (nipple)
                        {
                            bool p1Pipe = IsPipe(c1Elem, isPipeCache);
                            bool p2Pipe = IsPipe(c2Elem, isPipeCache);

                            if (p1Pipe ^ p2Pipe)
                            {
                                //// Exactly one pipe connected
                                //if (p1Pipe) 
                                //{ 
                                //    //c1elem is pipe
                                //    prep1 = "SC"; 
                                //    prep2 = "THR"; 
                                //}
                                //else 
                                //{ 
                                //    //c2 elem is pipe
                                //    prep1 = "THR"; 
                                //    prep2 = "SC"; 
                                //}
                            }
                            else
                            {

                                // Ensure at least one THR; favor C2 for determinism
                                if (prep1 != nippleCode && prep2 != nippleCode)
                                {
                                    if (prep1 == pipeCode && prep2 != pipeCode)
                                        prep2 = nippleCode;
                                    else if (prep2 == pipeCode && prep1 != pipeCode)
                                        prep1 = nippleCode;
                                    else prep2 = nippleCode;
                                }
                                // If both THR, keep only C2 = THR
                                if (prep1 == nippleCode && prep2 == nippleCode)
                                    prep1 = null;

                                //// Ensure at least one THR; favor C2 for determinism
                                //if (prep1 != "THR" && prep2 != "THR")
                                //{
                                //    if (prep1 == "SC" && prep2 != "SC") 
                                //        prep2 = "THR";
                                //    else if (prep2 == "SC" && prep1 != "SC") 
                                //        prep1 = "THR";
                                //    else prep2 = "THR";
                                //}
                                //// If both THR, keep only C2 = THR
                                //if (prep1 == "THR" && prep2 == "THR") 
                                //    prep1 = null;
                            }
                        }

                        // === Global ordering for fittings ===
                        // Rule 1: If exactly one end is SC, SC goes first (swap names to match).
                        bool exactlyOneSC = (prep1 == pipeCode) ^ (prep2 == pipeCode);
                        if (exactlyOneSC)
                        {
                            if (prep2 == pipeCode)
                            {
                                string tCode = prep1; prep1 = prep2; prep2 = tCode;
                                string tName = c1Name; c1Name = c2Name; c2Name = tName;
                            }
                        }
                        else
                        {
                            // Rule 2: Otherwise, alphabetical order of codes (nulls last), names aligned.
                            bool bothHaveCodes = !string.IsNullOrEmpty(prep1) && !string.IsNullOrEmpty(prep2) &&
                                                 !prep1.Equals(prep2, StringComparison.Ordinal);
                            if (bothHaveCodes && string.CompareOrdinal(prep1, prep2) > 0)
                            {
                                string tCode = prep1; prep1 = prep2; prep2 = tCode;
                                string tName = c1Name; c1Name = c2Name; c2Name = tName;
                            }
                        }

                        // Final joined code
                        string endPrepValue = BuildCode(prep1, prep2);

                        // Writes (only if changed)
                        SetIfChanged(pC1, c1Name);
                        SetIfChanged(pC2, c2Name);
                        SetIfChanged(pPEP, endPrepValue);

                        // Write Header ND if we found a pipe
                        if (maxDiamFt.HasValue)
                        {
                            SetHeaderND(pHeaderND, maxDiamFt.Value);
                        }

                        updated++;
                        i++;
                        win.UpdateSmart(updated, fittings.Count, $"Mapping… {updated} / {fittings.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(fittings.Count, fittings.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", updated, "Mapping fittings end prep has been cancelled");
                    return Result.Cancelled;
                }

                win.Done($"Successfully updated {updated} fittings end prep", fittings.Count, "Mapping Fittings End Prep Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ===== Helpers =====

        private static bool HasParam(Element e, string name)
        {
            if (name == "")
                return true;

            return e.LookupParameter(name) != null;
        }

        private static IList<Element> CollectTargetFittings(Document doc, ElementId viewId)
        {
            try
            {
                // Fast filter: family+type string contains "nipple" OR "shaped branch" (case-insensitive)
                ParameterValueProvider pvp =
                    new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));
                FilterStringRuleEvaluator contains = new FilterStringContains();
                FilterStringRule ruleNipple = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "nipple");
                FilterStringRule ruleBranch = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "shaped branch");

                ElementParameterFilter f1 = new ElementParameterFilter(ruleNipple);
                ElementParameterFilter f2 = new ElementParameterFilter(ruleBranch);
                LogicalOrFilter orFilter = new LogicalOrFilter(new List<ElementFilter> { f1, f2 });

                return new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .WherePasses(orFilter)
                    .ToElements();
            }
            catch
            {
                // Fallback: pull all fittings in view and filter by family name
                IList<Element> all = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .ToElements();

                List<Element> res = new List<Element>();
                foreach (Element f in all)
                {
                    string fam = GetFamilyName(f);
                    if (!string.IsNullOrEmpty(fam))
                    {
                        string fl = fam.ToLowerInvariant();
                        if (fl.Contains("nipple") || fl.Contains("shaped branch"))
                            res.Add(f);
                    }
                }
                return res;
            }
        }

        private static IList<Element> CollectTargetFittingsByConfig(Document doc, ElementId viewId)
        {
            try
            {
                IEnumerable<ParallelSystemsPlugin.Models.Configs.AllowedMapFittingsElement> rules = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.AllowedMapFittingsElements;

                if (rules == null)
                    return new List<Element>();

                var keywords = rules
                .Select(r => r.NameContains)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();

                if (!keywords.Any())
                    return new List<Element>();

                // Fast filter: family+type string contains "nipple" OR "shaped branch" (case-insensitive)
                var pvp = new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));

                var evaluator = new FilterStringContains();

                var filters = keywords.Select(k => (ElementFilter)new ElementParameterFilter(RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, evaluator, k))).ToList();

                var orFilter = new LogicalOrFilter(filters);

                return new FilteredElementCollector(doc, viewId)
                        .OfCategory(BuiltInCategory.OST_PipeFitting)
                        .WhereElementIsNotElementType()
                        .WherePasses(orFilter)
                        .ToElements();
            }
            catch
            {
                // Fallback: pull all fittings in view and filter by family name
                IList<Element> all = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .ToElements();

                List<Element> res = new List<Element>();
                foreach (Element f in all)
                {
                    string fam = GetFamilyName(f);
                    if (!string.IsNullOrEmpty(fam))
                    {
                        string fl = fam.ToLowerInvariant();
                        if (fl.Contains("nipple") || fl.Contains("shaped branch"))
                            res.Add(f);
                    }
                }
                return res;
            }
        }

        private static List<Element> GetConnectedMainEnds(
            Element fitting,
            Dictionary<ElementId, ConnectorManager> cmCache,
            Dictionary<ElementId, string> bestNameCache,
            Dictionary<ElementId, string> famNameLower,
            Dictionary<ElementId, bool> isPipeCache,
            Dictionary<ElementId, List<Element>> mainConnCache)
        {
            List<Element> cached;
            if (mainConnCache.TryGetValue(fitting.Id, out cached))
                return cached;

            ConnectorManager cm = GetConnectorManager(fitting, cmCache);
            List<Element> results = new List<Element>(2);
            if (cm == null)
            {
                mainConnCache[fitting.Id] = results;
                return results;
            }

            HashSet<long> seen = new HashSet<long>();
            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    Element owner = r.Owner;
                    if (owner == null || owner.Id == fitting.Id) continue;

                    // Skip "System" categories
                    try
                    {
                        Category oc = owner.Category;
                        if (oc != null && oc.Name != null &&
                            oc.Name.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                    }
                    catch { }

                    Element final = GetValidConnectedElement(
                        owner, fitting, cmCache, bestNameCache, famNameLower, isPipeCache,
                        new HashSet<long>(), 0, 8);

                    if (final != null)
                    {
                        long id = RevitApiCompatibility.GetElementIdValue(final.Id);
                        if (!seen.Contains(id))
                        {
                            seen.Add(id);
                            results.Add(final);
                            if (results.Count == 2) { mainConnCache[fitting.Id] = results; return results; }
                        }
                    }
                }
            }

            mainConnCache[fitting.Id] = results;
            return results;
        }

        private static Element GetValidConnectedElement(
            Element elem,
            Element comingFrom,
            Dictionary<ElementId, ConnectorManager> cmCache,
            Dictionary<ElementId, string> bestNameCache,
            Dictionary<ElementId, string> famNameLower,
            Dictionary<ElementId, bool> isPipeCache,
            HashSet<long> visited,
            int depth,
            int maxDepth)
        {
            if (elem == null) return null;
            long key = RevitApiCompatibility.GetElementIdValue(elem.Id);
            if (visited.Contains(key) || depth > maxDepth) return null;
            visited.Add(key);

            if (!(Elements.Fittings.IsIgnoreComponents(elem, bestNameCache)
                || Elements.Pipes.IsIgnoreComponentsByCat(elem)
                || Elements.Fittings.IsIgnoreComponentsByFamName(elem)))
                return elem;

            //if (!IsWeld(elem, bestNameCache) && !IsInsulation(elem) && !IsNonConnector(elem))
            //    return elem;

            ConnectorManager cm = GetConnectorManager(elem, cmCache);
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    Element owner = r.Owner;
                    if (owner == null || owner.Id == elem.Id || (comingFrom != null && owner.Id == comingFrom.Id))
                        continue;

                    Element result = GetValidConnectedElement(
                        owner, elem, cmCache, bestNameCache, famNameLower, isPipeCache,
                        visited, depth + 1, maxDepth);

                    if (result != null) return result;
                }
            }
            return null;
        }

        private static string MapToEndPrep(
            Document doc,
            Element elem,
            Dictionary<ElementId, string> bestNameCache,
            Dictionary<ElementId, string> famNameLower,
            Dictionary<ElementId, bool> isPipeCache,
            Dictionary<ElementId, string> endPrepCache)
        {
            if (elem == null) return null;

            string cached;
            if (endPrepCache.TryGetValue(elem.Id, out cached))
                return cached;

            //if (IsPipe(elem, isPipeCache))
            //{
            //    endPrepCache[elem.Id] = "SC";
            //    return "SC";
            //}

            string nameLower = GetBestName(doc, elem, bestNameCache).ToLowerInvariant();
            string famLower = GetFamilyNameLower(elem, famNameLower);
            string fullLower = (nameLower + " " + famLower).Trim();

            string code = Elements.Fittings.GetElementValue(nameLower);

            if(code == null)
                code = Elements.Fittings.GetElementValue(famLower);

            if (code == null)
                code = Elements.Fittings.GetElementValue(fullLower);
            
            endPrepCache[elem.Id] = code;
            
            return code;

            //// Elbow/Tee/Cross/Reducer/Lateral/Wye
            //if (ContainsAny(famLower, TOK_ELBOWISH) || ContainsAny(fullLower, TOK_ELBOWISH))
            //{ endPrepCache[elem.Id] = "BE"; return "BE"; }

            //// 'stub end' or 'end'
            //if (RE_STUB_END.IsMatch(fullLower) || RE_END.IsMatch(fullLower))
            //{ endPrepCache[elem.Id] = "BE"; return "BE"; }

            //// flange → PE
            //if (fullLower.Contains("flange"))
            //{ endPrepCache[elem.Id] = "PE"; return "PE"; }

            //// coupling → RG
            //foreach (string t in RG_LIST) if (fullLower.Contains(t))
            //    { endPrepCache[elem.Id] = "RG"; return "RG"; }

            //// branch → SC
            //foreach (string t in SC_LIST) if (fullLower.Contains(t))
            //    { endPrepCache[elem.Id] = "SC"; return "SC"; }

            //endPrepCache[elem.Id] = null;
            //return null;
        }

        private static double? GetPipeDiameterFeet(Element e, Dictionary<ElementId, double?> diameterCache)
        {
            if (e == null) return null;
            if (diameterCache.TryGetValue(e.Id, out var cached)) return cached;

            double? result = null;

            try
            {
                // Try BuiltInParameter first (more reliable than a display "Diameter" param name)
                var bip = e.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                if (bip != null && bip.HasValue) result = bip.AsDouble();
            }
            catch { }

            if (!result.HasValue)
            {
                try
                {
                    var p = e.LookupParameter("Diameter");
                    if (p != null && p.HasValue) result = p.AsDouble();
                }
                catch { }
            }

            diameterCache[e.Id] = result;
            return result;
        }

        private static string BuildCode(string p1, string p2)
        {

            if (ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.FittingsMapParameters.EnableMapping)
            {
                string unconnectedValue = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.FittingsMapParameters.Unconnected;
                p1 = string.IsNullOrEmpty(p1) ? unconnectedValue : p1;
                p2 = string.IsNullOrEmpty(p2) ? unconnectedValue : p2;
            }

            string end1 = AppConfig.CurrentConfig.FittingsMapParameters.End1;
            string end2 = AppConfig.CurrentConfig.FittingsMapParameters.End2;

            if (string.IsNullOrEmpty(end1))
                p1 = string.Empty;
            
            if (string.IsNullOrEmpty(end2))
                p2 = string.Empty;

            if (string.IsNullOrEmpty(p1) && string.IsNullOrEmpty(p2)) return string.Empty;

            List<string> parts = new List<string>(2);

            if (!string.IsNullOrEmpty(p1))
                parts.Add(p1);

            if (!string.IsNullOrEmpty(p2))
                parts.Add(p2);

            return string.Join("-", parts);
        }

        private static void SetIfChanged(Parameter p, object newVal)
        {
            if (p == null) return;

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        string sNew = newVal != null ? newVal.ToString() : string.Empty;
                        string sCur = p.AsString() ?? string.Empty;
                        if (!string.Equals(sCur, sNew, StringComparison.Ordinal)) p.Set(sNew);
                        break;

                    case StorageType.Integer:
                        int iNew = 0;
                        if (newVal is int) iNew = (int)newVal;
                        else if (newVal != null) int.TryParse(newVal.ToString(), out iNew);
                        if (p.AsInteger() != iNew) p.Set(iNew);
                        break;

                    case StorageType.Double:
                        double dNew = 0.0;
                        if (newVal is double) dNew = (double)newVal;
                        else if (newVal != null) double.TryParse(newVal.ToString(), out dNew);
                        if (Math.Abs(p.AsDouble() - dNew) > 1e-09) p.Set(dNew);
                        break;

                    case StorageType.ElementId:
                        ElementId idNew = ElementId.InvalidElementId;
                        if (newVal is ElementId) idNew = (ElementId)newVal;
                        if (p.AsElementId() != idNew) p.Set(idNew);
                        break;

                    default:
                        string s = newVal != null ? newVal.ToString() : string.Empty;
                        string cur = p.AsString() ?? string.Empty;
                        if (!string.Equals(cur, s, StringComparison.Ordinal)) p.Set(s);
                        break;
                }
            }
            catch
            {
                // ignore read-only or type mismatch
            }
        }

        private static void SetHeaderND(Parameter headerParam, double maxDiamFt)
        {
            if (headerParam == null) return;
            
            try
            {
                switch (headerParam.StorageType)
                {
                    case StorageType.Double:
                        // write in feet
                        if (Math.Abs(headerParam.AsDouble() - maxDiamFt) > 1e-09)
                            headerParam.Set(maxDiamFt);
                        break;

                    case StorageType.Integer:
                        // write mm (rounded)
                        int mm = (int)Math.Round(maxDiamFt * FT_TO_MM);
                        if (headerParam.AsInteger() != mm) headerParam.Set(mm);
                        break;

                    case StorageType.String:
                        string s = ((int)Math.Round(maxDiamFt * FT_TO_MM)).ToString();
                        if (!string.Equals(headerParam.AsString() ?? string.Empty, s, StringComparison.Ordinal))
                            headerParam.Set(s);
                        break;

                    default:
                        // fallback as string mm
                        string v = ((int)Math.Round(maxDiamFt * FT_TO_MM)).ToString();
                        if (!string.Equals(headerParam.AsString() ?? string.Empty, v, StringComparison.Ordinal))
                            headerParam.Set(v);
                        break;
                }
            }
            catch { /* read-only or mismatch */ }
        }

        // ===== name/family helpers & type checks =====

        private static string GetBestName(Document doc, Element e, Dictionary<ElementId, string> cache)
        {
            if (e == null) return "Unnamed";
            string found;
            if (cache.TryGetValue(e.Id, out found)) return found;

            string name = null;
            try
            {
                Parameter p = e.LookupParameter("Description BOM");
                if (p != null && p.HasValue)
                {
                    string s = p.AsString();
                    if (!string.IsNullOrWhiteSpace(s)) name = s;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(name))
            {
                try
                {
                    Element type = doc.GetElement(e.GetTypeId());
                    if (type != null)
                    {
                        Parameter symName = type.get_Parameter(BuiltInParameter.SYMBOL_NAME_PARAM);
                        if (symName != null && symName.HasValue)
                        {
                            string tn = symName.AsString();
                            if (!string.IsNullOrWhiteSpace(tn)) name = tn;
                        }
                        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(type.Name))
                            name = type.Name;
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(name))
                name = e.Name ?? "Unnamed";

            cache[e.Id] = name;
            return name;
        }

        private static string GetFamilyName(Element e)
        {
            try
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi != null && fi.Symbol != null && fi.Symbol.Family != null)
                    return fi.Symbol.Family.Name;
            }
            catch { }
            return null;
        }

        private static string GetFamilyNameLower(Element e, Dictionary<ElementId, string> famLowerCache)
        {
            string cached;
            if (famLowerCache.TryGetValue(e.Id, out cached)) return cached;

            string fam = GetFamilyName(e) ?? string.Empty;
            string lower = fam.ToLowerInvariant();
            famLowerCache[e.Id] = lower;
            return lower;
        }

        private static bool IsPipe(Element e, Dictionary<ElementId, bool> cache)
        {
            if (e == null) return false;
            bool found;
            if (cache.TryGetValue(e.Id, out found)) return found;

            bool ok = false;
            try
            {
                Category cat = e.Category;
                ok = (cat != null && cat.Id != null && RevitApiCompatibility.GetElementIdValue(cat.Id) == (long)(int)BuiltInCategory.OST_PipeCurves);
            }
            catch { ok = false; }

            cache[e.Id] = ok;
            return ok;
        }

        private static bool IsInsulation(Element e)
        {
            try
            {
                Category c = e.Category;
                return c != null && c.Name != null &&
                       c.Name.IndexOf("insulations", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsWeld(Element e, Dictionary<ElementId, string> nameCache)
        {
            string nm = GetBestName(e.Document, e, nameCache).ToLowerInvariant();
            return nm.Contains("weld");
        }

        private static bool IsNonConnector(Element e)
        {
            try
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi != null && fi.Symbol != null && fi.Symbol.Family != null)
                {
                    string fam = fi.Symbol.Family.Name ?? string.Empty;
                    return fam.IndexOf("non-connector", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }

        private static ConnectorManager GetConnectorManager(Element e, Dictionary<ElementId, ConnectorManager> cmCache)
        {
            if (e == null) return null;
            ConnectorManager cached;
            if (cmCache.TryGetValue(e.Id, out cached)) return cached;

            ConnectorManager cm = null;

            FamilyInstance fi = e as FamilyInstance;
            if (fi != null && fi.MEPModel != null)
                cm = fi.MEPModel.ConnectorManager;

            if (cm == null)
            {
                MEPCurve mc = e as MEPCurve;
                if (mc != null) cm = mc.ConnectorManager;
            }

            cmCache[e.Id] = cm;
            return cm;
        }

        private static bool ContainsAny(string text, string[] tokens)
        {
            if (string.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < tokens.Length; i++)
                if (text.Contains(tokens[i])) return true;
            return false;
        }

        private static bool IsNipple(Element e)
        {
            try
            {
                FamilyInstance fi = e as FamilyInstance;
                if (fi != null && fi.Symbol != null && fi.Symbol.Family != null)
                {
                    string fam = fi.Symbol.Family.Name ?? string.Empty;
                    return fam.IndexOf("nipple", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }
    }
}
