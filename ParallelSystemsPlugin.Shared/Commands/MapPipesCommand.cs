using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI; // <-- for ProgressWindow
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
using System.Windows.Interop;
using System.Xml.Linq;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class MapPipesCommand : IExternalCommand
    {
        private static readonly HashSet<string> RG_LIST =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "coupling" };
        private static readonly HashSet<string> SC_LIST =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "branch" };
        private static readonly Regex _reStubEnd =
            new Regex(@"\bstub end\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex _reEnd =
            new Regex(@"\bend\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);


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

            var pipes = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType()
                .ToElements();

            if (pipes.Count == 0)
            {
                AppDialog.Info(uiapp,"Pipe End Prep", "No pipes found in the active view.");
                return Result.Succeeded;
            }
            string end1 = AppConfig.CurrentConfig.PipeMapParameters.End1;
            string end2 = AppConfig.CurrentConfig.PipeMapParameters.End2;
            string endPrep = AppConfig.CurrentConfig.PipeMapParameters.EndPrep;

            //bool paramsExist = pipes.Any(p =>
            //    p.LookupParameter(end1) != null &&
            //    p.LookupParameter(end2) != null &&
            //    p.LookupParameter(endPrep) != null);

            bool paramsExist = pipes.Any(p =>
                Elements.HasParam(p, end1) &&
                Elements.HasParam(p, end2) &&
                Elements.HasParam(p,endPrep));


            if (!paramsExist)
            {
                AppDialog.Info(uiapp,"Pipe End Prep",
                    $"Parameters not found. Pipes must have text parameters: {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeMapParameters)}");
                return Result.Succeeded;
            }

            int updated = 0;
            var nameCache = new Dictionary<ElementId, string>();
            var cmCache = new Dictionary<ElementId, ConnectorManager>();

            var win = new ProgressWindow();
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                new WindowInteropHelper(win).Owner = hwnd;

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(pipes.Count, "Mapping Pipe End Prep…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);


                using (var tx = new Transaction(doc, "Map Pipe End Prep"))
                {
                    tx.Start();

                    int index = 0;
                    foreach (var pipe in pipes)
                    {
                        if (win.IsCanceled) break;

                        var c1Param = pipe.LookupParameter(end1);
                        var c2Param = pipe.LookupParameter(end2);
                        var pepParam = pipe.LookupParameter(endPrep);
                        if (c1Param == null || c2Param == null || pepParam == null)
                        {
                            index++;
                            if ((index & 31) == 0) win.Update(index);
                            continue;
                        }

                        var pair = GetConnectedNames(doc, pipe, nameCache, cmCache);
                        var ordered = AlphabeticalReorder(pair.c1Name, pair.c2Name, pair.c1Element, pair.c2Element);

                        var pipeConfig = ParallelSystemsPlugin.Configs.AppConfig.CurrentConfig.PipeMapParameters;

                        string defaultUnconnectedValue = "Unconnected";
                        string strUnconnected = pipeConfig.EnableMapping ? pipeConfig.Unconnected : defaultUnconnectedValue;

                        if (string.IsNullOrEmpty(strUnconnected))
                            strUnconnected = defaultUnconnectedValue;

                        c1Param.Set(ordered.c1Out ?? strUnconnected);
                        c2Param.Set(ordered.c2Out ?? strUnconnected);
                        pepParam.Set(ordered.prepOut ?? string.Empty); 

                        updated++;
                        index++;
                        win.UpdateSmart(index, pipes.Count, $"Mapping… {index} / {pipes.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(pipes.Count, pipes.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    // optional: call tx.RollBack() instead of Commit above if you want all-or-nothing
                    win.Canceled("Mapping Cancelled", updated, "Mapping pipe end prep has been cancelled");
                    return Result.Cancelled;
                }

              
                win.Done($"Successfully updated {updated} pipe end prep", pipes.Count, "Mapping Pipe End Prep Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
            // no finally auto-close; user will click "Complete" or "Close" on the window
        }

        // ===== Mapping helpers (same logic as your Python) =====

        private static (string c1Name, string c2Name, Element c1Element, Element c2Element) GetConnectedNames(
            Document doc,
            Element pipe,
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, ConnectorManager> cmCache)
        {
            string startName = "Unconnected";
            string endName = "Unconnected";

            Element c1Element = null;
            Element c2Element = null;

            var lc = pipe.Location as LocationCurve;
            if (lc == null || lc.Curve == null)
                return (startName, endName, c1Element, c2Element);

            XYZ startPt = lc.Curve.GetEndPoint(0);
            XYZ endPt = lc.Curve.GetEndPoint(1);

            var cm = GetConnectorManager(pipe, cmCache);
            if (cm == null) return (startName, endName, c1Element, c2Element);

            foreach (Connector conn in cm.Connectors)
            {
                foreach (Connector aref in conn.AllRefs)
                {
                    Element owner = aref.Owner;
                    if (owner == null || owner.Id == pipe.Id) continue;


                    if (owner.Category != null &&
                        owner.Category.Name?.IndexOf("System", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    var finalOwner = GetValidConnectedElement(doc, owner, pipe, nameCache, cmCache, new HashSet<long>());
                    if (finalOwner == null) continue;

                    string compName = Elements.GetBestName(doc, finalOwner, nameCache);

                    if (conn.Origin.IsAlmostEqualTo(startPt)) 
                    {
                        c1Element = finalOwner;
                        startName = compName; 
                    }
                    else if (conn.Origin.IsAlmostEqualTo(endPt)) 
                    {
                        c2Element = finalOwner;
                        endName = compName; 
                    }
                }
            }

            return (startName, endName, c1Element, c2Element);
        }

        private static Element GetValidConnectedElement(
            Document doc,
            Element elem,
            Element comingFrom,
            Dictionary<ElementId, string> nameCache,
            Dictionary<ElementId, ConnectorManager> cmCache,
            HashSet<long> visited)
        {
            if (elem == null) return null;
            long id = RevitApiCompatibility.GetElementIdValue(elem.Id);
            if (visited.Contains(id)) return null;
            visited.Add(id);


            
            if (!(Elements.Pipes.IsIgnoreComponents(elem, nameCache)
                || Elements.Pipes.IsIgnoreComponentsByCat(elem) 
                || Elements.Pipes.IsIgnoreComponentsByFamName(elem)
                ))

                return elem;

            //if (!(IsWeld(elem, nameCache) || IsInsulation(elem) || IsNonConnector(elem)))
            //    return elem;

            var cm = GetConnectorManager(elem, cmCache);
            if (cm == null) return null;

            foreach (Connector c in cm.Connectors)
            {
                foreach (Connector r in c.AllRefs)
                {
                    var owner = r.Owner;
                    if (owner == null || owner.Id == elem.Id || owner.Id == comingFrom.Id)
                        continue;

                    var result = GetValidConnectedElement(doc, owner, elem, nameCache, cmCache, visited);
                    if (result != null) return result;
                }
            }
            return null;
        }

        private static ConnectorManager GetConnectorManager(Element e, Dictionary<ElementId, ConnectorManager> cmCache)
        {
            if (e == null) return null;
            if (cmCache.TryGetValue(e.Id, out var cm)) return cm;

            ConnectorManager outCm = null;

            if (e is FamilyInstance fi)
                outCm = fi.MEPModel?.ConnectorManager;

            if (outCm == null && e is MEPCurve mc)
                outCm = mc.ConnectorManager;

            cmCache[e.Id] = outCm;
            return outCm;
        }

        private static string GetBestName(Document doc, Element e, Dictionary<ElementId, string> cache)
        {
            if (e == null) return "Unnamed";
            if (cache.TryGetValue(e.Id, out var cached)) return cached;

            string result = "Unnamed";

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

        private static bool IsWeld(Element e, Dictionary<ElementId, string> nameCache)
        {
            string nm = Elements.GetBestName(e.Document, e, nameCache).ToLowerInvariant();
            return nm.Contains("weld");
        }

        private static bool IsInsulation(Element e)
        {
            try
            {
                var cat = e.Category;
                return cat != null && cat.Name != null &&
                       cat.Name.IndexOf("insulations", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        private static bool IsNonConnector(Element e)
        {
            try
            {
                if (e is FamilyInstance fi && fi.Symbol?.Family != null)
                {
                    string fam = fi.Symbol.Family.Name ?? string.Empty;
                    return fam.IndexOf("non-connector", StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch { }
            return false;
        }

        private static string MapToEndPrep(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "Unconnected")
                return null;

            string lower = name.ToLowerInvariant();

            return Elements.Pipes.GetElementValue(lower);

            //if (_reStubEnd.IsMatch(lower) || _reEnd.IsMatch(lower))
            //    return "BE";

            //if (lower.Contains("elbow") || lower.Contains("tee") || lower.Contains("cross") ||
            //    lower.Contains("reducer") || lower.Contains("lateral") || lower.Contains("wye"))
            //    return "BE";

            //if (lower.Contains("flange"))
            //    return "PE";

            //if (RG_LIST.Any(k => lower.Contains(k)))
            //    return "RG";

            //if (SC_LIST.Any(k => lower.Contains(k)))
            //    return "SC";

            //return null;
        }

        private static (string c1Out, string c2Out, string prepOut) AlphabeticalReorder(string c1Name, string c2Name, Element c1Element, Element c2Element)
        {
            var items = new[]
            {
                new
                {
                    Code = MapToEndPrep(c1Name),
                    Name = c1Name,
                    IsPipe = Elements.IsPipe(c1Element)
                },
                new
                {
                    Code = MapToEndPrep(c2Name),
                    Name = c2Name,
                    IsPipe = Elements.IsPipe(c2Element)
                }
            };

            var pipeConfigs = AppConfig.CurrentConfig.PipeMapParameters;

            var pairs = new List<(string code, string name)>();

            foreach (var item in items)
            {
                // Add if code exists OR element is NOT a pipe
                if (!string.IsNullOrEmpty(item.Code) || !item.IsPipe)
                {
                    pairs.Add((item.Code, item.Name));
                }
            }

            pairs.Sort((a, b) => string.CompareOrdinal(a.code, b.code));

            while (pairs.Count < 2)
            {
                if (pipeConfigs.EnableMapping && pipeConfigs.Unconnected != string.Empty)
                    pairs.Add((pipeConfigs.Unconnected, "Unconnected"));
                else
                    pairs.Add((null, "Unconnected"));
            }

            string prep = string.Join("-", pairs.Where(p => !string.IsNullOrEmpty(p.code)).Select(p => p.code));

            return (pairs[0].name, pairs[1].name, prep);
        }

        private static (string c1Out, string c2Out, string prepOut) AlphabeticalReorder(string c1Name, string c2Name)
        {
            var codes = new[] { MapToEndPrep(c1Name), MapToEndPrep(c2Name) };
            var names = new[] { c1Name, c2Name };

            var pipeConfigs = AppConfig.CurrentConfig.PipeMapParameters;
            
            var pairs = new List<(string code, string name)>();
            
            for (int i = 0; i < 2; i++)
            {
                if (!string.IsNullOrEmpty(codes[i]))
                    pairs.Add((codes[i], names[i]));
            }

            pairs.Sort((a, b) => string.CompareOrdinal(a.code, b.code));

            while (pairs.Count < 2)
            {
                if (pipeConfigs.EnableMapping && pipeConfigs.Unconnected != string.Empty)
                    pairs.Add((pipeConfigs.Unconnected, "Unconnected"));
                else 
                    pairs.Add((null, "Unconnected"));  
            }

            string prep = string.Join("-", pairs.Where(p => !string.IsNullOrEmpty(p.code)).Select(p => p.code));

            return (pairs[0].name, pairs[1].name, prep);
        }
    }
}
