using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;   // ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearHeaderNDCommand : IExternalCommand
    {
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

            // Collect target fittings in the active view
            IList<Element> fittings = CollectTargetFittings(doc, activeView.Id);
            if (fittings.Count == 0)
            {
                AppDialog.Info(uiapp, "Header ND", "No nipple / shaped branch / BSP socket fittings found in the active view.");
                return Result.Succeeded;
            }

            var result = AppDialog.Show(
               "Confirm Clear",
               "Clear mapped header nd on fittings?\n\n" +
               "This will clear header nd on threaded nipple / shaped branch / bsp socket fittings in the active view.",
               MessageDialogIcon.Warning,
               MessageDialogButtons.YesNo);

            if (result != MessageDialogResult.Yes)
                return Result.Succeeded;

            int cleared = 0;
            var win = new ProgressWindow();

            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                new WindowInteropHelper(win).Owner = hwnd;

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(fittings.Count, "Clearing Fittings Header ND…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (var tx = new Transaction(doc, "Clear Header ND"))
                {
                    tx.Start();

                    int i = 0;
                    foreach (var f in fittings)
                    {
                        if (win.IsCanceled) break;

                        var pHeader = f.LookupParameter("Header ND");
                        if (pHeader != null)
                        {
                            if (ClearHeaderParam(pHeader))
                                cleared++;
                        }

                        i++;
                        win.UpdateSmart(i, fittings.Count, $"Clearing… {i} / {fittings.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(fittings.Count, fittings.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", cleared, "Clearing fittings header nd has been cancelled");
                    return Result.Cancelled;
                }

                win.Done($"Successfully cleared {cleared} fittings Header ND", fittings.Count, "Clearing Fittings Header ND Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        // -------- helpers --------

        private static bool ClearHeaderParam(Parameter p)
        {
            try
            {
                switch (p.StorageType)
                {
                    case StorageType.Double:
                        if (Math.Abs(p.AsDouble()) > 1e-09) { p.Set(0.0); return true; }
                        break;
                    case StorageType.Integer:
                        if (p.AsInteger() != 0) { p.Set(0); return true; }
                        break;
                    case StorageType.String:
                        if (!string.IsNullOrEmpty(p.AsString())) { p.Set(string.Empty); return true; }
                        break;
                    case StorageType.ElementId:
                        if (p.AsElementId() != ElementId.InvalidElementId) { p.Set(ElementId.InvalidElementId); return true; }
                        break;
                    default:
                        // Fallback to empty string
                        if (!string.IsNullOrEmpty(p.AsString())) { p.Set(string.Empty); return true; }
                        break;
                }
            }
            catch
            {
                // read-only or type mismatch; ignore
            }
            return false;
        }

        private static IList<Element> CollectTargetFittings(Document doc, ElementId viewId)
        {
            // Fast filter on Family+Type contains any of these tokens (case-insensitive)
            try
            {
                var pvp = new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));
                var contains = new FilterStringContains();

                var ruleNip = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "nipple");
                var ruleSB = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "shaped");
                var ruleBsp = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "bsp");

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
                // Fallback if rule filter fails: scan and filter by family name
                var all = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .ToElements();

                var res = new List<Element>();
                foreach (var f in all)
                {
                    string fam = GetFamilyName(f) ?? string.Empty;
                    string fl = fam.ToLowerInvariant();
                    if (fl.Contains("nipple") || fl.Contains("shaped") || fl.Contains("bsp"))
                        res.Add(f);
                }
                return res;
            }
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
    }
}
