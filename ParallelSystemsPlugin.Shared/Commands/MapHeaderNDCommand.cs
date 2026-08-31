using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;   // ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MapHeaderNDCommand : IExternalCommand
    {
        private const double FT_TO_MM = 304.8;

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

            // ---- quick preflight: any fittings & do they have Header ND? ----
            Element sample = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsNotElementType()
                .FirstElement();

            if (sample == null)
            {
                AppDialog.Info(uiapp,"Header ND", "No pipe fittings found in the active view.");
                return Result.Succeeded;
            }

            // collect target fittings (nipple, shaped branch, BSP socket)
            IList<Element> fittings = CollectTargetFittings(doc, activeView.Id);
            if (fittings.Count == 0)
            {
                AppDialog.Info(uiapp,"Header ND", "No nipple / shaped branch / BSP socket fittings found in the active view.");
                return Result.Succeeded;
            }

            // Caches to keep performance decent
            var cmCache = new Dictionary<ElementId, ConnectorManager>();
            var nameCache = new Dictionary<ElementId, string>();
            var famLowerCache = new Dictionary<ElementId, string>();
            var isPipeCache = new Dictionary<ElementId, bool>();
            var mainConnCache = new Dictionary<ElementId, List<Element>>();
            var diameterCache = new Dictionary<ElementId, double?>();

            int touched = 0;

            var win = new ProgressWindow();
            try
            {

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(fittings.Count, "Mapping Fittings Header ND…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (var tx = new Transaction(doc, "Map Header ND"))
                {
                    tx.Start();

                    int i = 0;
                    foreach (Element fitting in fittings)
                    {
                        if (win.IsCanceled) break;

                        Parameter pHeader = fitting.LookupParameter("Header ND");
                        if (pHeader == null)
                        {
                            // No target parameter on this fitting; skip
                            i++; if ((i & 31) == 0) win.Update(i); continue;
                        }

                        // Find connected "main" elements (skip weld/insulation/non-connector)
                        var neigh = GetConnectedMainEnds(fitting, cmCache, nameCache, famLowerCache, isPipeCache, mainConnCache);
                        if (neigh.Count == 0)
                        {
                            i++; if ((i & 31) == 0) win.Update(i); continue;
                        }

                        // Inspect connected pipes, take the largest diameter
                        double? maxDiamFt = null;
                        foreach (var e in neigh)
                        {
                            if (!IsPipe(e, isPipeCache)) continue;
                            double? d = GetPipeDiameterFeet(e, diameterCache);
                            if (d.HasValue && (!maxDiamFt.HasValue || d.Value > maxDiamFt.Value))
                                maxDiamFt = d;
                        }

                        // Write Header ND if we found a pipe
                        if (maxDiamFt.HasValue)
                        {
                            SetHeaderND(pHeader, maxDiamFt.Value);
                            touched++;
                        }

                        i++;
                        win.UpdateSmart(touched, fittings.Count, $"Mapping… {touched} / {fittings.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(fittings.Count, fittings.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", touched, "Mapping fittings header nd has been cancelled");
                    return Result.Cancelled;
                }
                win.Done($"Successfully updated {touched} fittings header nd", fittings.Count, "Mapping Fittings Header ND Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        // ------------------- Collection -------------------

        private static IList<Element> CollectTargetFittings(Document doc, ElementId viewId)
        {
            // Fast substring filter on Family+Type: “nipple”, “shaped branch”, “bsp socket”
            try
            {
                var pvp = new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));
                var contains = new FilterStringContains();
                var ruleNip = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "nipple");
                var ruleSB = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "shaped branch");
                var ruleBsp = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "bsp socket");

                var fNip = new ElementParameterFilter(ruleNip);
                var fSB = new ElementParameterFilter(ruleSB);
                var fBsp = new ElementParameterFilter(ruleBsp);

                var or1 = new LogicalOrFilter(fNip, fSB);
                var or2 = new LogicalOrFilter(or1, fBsp);

                return new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .WherePasses(or2)
                    .ToElements();
            }
            catch
            {
                // Fallback: name filter
                var all = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var res = new List<Element>();
                foreach (var f in all)
                {
                    string fam = GetFamilyName(f) ?? string.Empty;
                    string fl = fam.ToLowerInvariant();
                    if (fl.Contains("nipple") || fl.Contains("shaped branch") || fl.Contains("bsp socket"))
                        res.Add(f);
                }
                return res;
            }
        }

        // ------------------- Connector graph helpers -------------------

        private static List<Element> GetConnectedMainEnds(
            Element fitting,
            Dictionary<ElementId, ConnectorManager> cmCache,
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, string> famLowerCache,
            Dictionary<ElementId, bool> isPipeCache,
            Dictionary<ElementId, List<Element>> mainConnCache)
        {
            if (mainConnCache.TryGetValue(fitting.Id, out var cached))
                return cached;

            var cm = GetConnectorManager(fitting, cmCache);
            var results = new List<Element>(2);
            if (cm == null) { mainConnCache[fitting.Id] = results; return results; }

            var seen = new HashSet<long>();
            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    var owner = r.Owner;
                    if (owner == null || owner.Id == fitting.Id) continue;

                    // Skip "System" categories quickly
                    try
                    {
                        var oc = owner.Category;
                        if (oc != null && oc.Name != null &&
                            oc.Name.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0)
                            continue;
                    }
                    catch { }

                    var final = GetValidConnectedElement(owner, fitting, cmCache, nameCache, famLowerCache, isPipeCache,
                                                         new HashSet<long>(), 0, 8);
                    if (final != null)
                    {
                        long id = RevitApiCompatibility.GetElementIdValue(final.Id);
                        if (seen.Add(id))
                        {
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
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, string> famLowerCache,
            Dictionary<ElementId, bool> isPipeCache,
            HashSet<long> visited,
            int depth,
            int maxDepth)
        {
            if (elem == null) return null;
            long key = RevitApiCompatibility.GetElementIdValue(elem.Id);
            if (visited.Contains(key) || depth > maxDepth) return null;
            visited.Add(key);

            if (!IsWeld(elem, nameCache) && !IsInsulation(elem) && !IsNonConnector(elem))
                return elem;

            var cm = GetConnectorManager(elem, cmCache);
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    var owner = r.Owner;
                    if (owner == null || owner.Id == elem.Id || (comingFrom != null && owner.Id == comingFrom.Id))
                        continue;

                    var result = GetValidConnectedElement(owner, elem, cmCache, nameCache, famLowerCache, isPipeCache,
                                                          visited, depth + 1, maxDepth);
                    if (result != null) return result;
                }
            }
            return null;
        }

        // ------------------- Parameter writes -------------------

        private static void SetHeaderND(Parameter headerParam, double maxDiamFt)
        {
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

        // ------------------- Utilities -------------------

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

        private static string GetFamilyName(Element e)
        {
            try
            {
                var fi = e as FamilyInstance;
                if (fi != null && fi.Symbol != null && fi.Symbol.Family != null)
                    return fi.Symbol.Family.Name;
            }
            catch { }
            return null;
        }

        private static bool IsPipe(Element e, Dictionary<ElementId, bool> cache)
        {
            if (e == null) return false;
            if (cache.TryGetValue(e.Id, out bool found)) return found;

            bool ok = false;
            try
            {
                var cat = e.Category;
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
                var c = e.Category;
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
                var fi = e as FamilyInstance;
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
            if (cmCache.TryGetValue(e.Id, out var cached)) return cached;

            ConnectorManager cm = null;

            var fi = e as FamilyInstance;
            if (fi != null && fi.MEPModel != null)
                cm = fi.MEPModel.ConnectorManager;

            if (cm == null)
            {
                var mc = e as MEPCurve;
                if (mc != null) cm = mc.ConnectorManager;
            }

            cmCache[e.Id] = cm;
            return cm;
        }

        private static string GetBestName(Document doc, Element e, Dictionary<ElementId, string> cache)
        {
            if (e == null) return "Unnamed";
            if (cache.TryGetValue(e.Id, out var found)) return found;

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
    }
}
