using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearFittingsCommand : IExternalCommand
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

            IList<Element> fittings = new FilteredElementCollector(doc, activeView.Id)
                .OfCategory(BuiltInCategory.OST_PipeFitting)
                .WhereElementIsNotElementType()
                .ToElements();

            if (fittings.Count == 0)
            {
                AppDialog.Info(uiapp,"Pipe End Prep", "No pipe fittings found in the active view.");
                return Result.Succeeded;
            }

            string end1 = AppConfig.CurrentConfig.FittingsMapParameters.End1;
            string end2 = AppConfig.CurrentConfig.FittingsMapParameters.End2;
            string endPrep = AppConfig.CurrentConfig.FittingsMapParameters.EndPrep;
            string headerND = AppConfig.CurrentConfig.FittingsMapParameters.HeaderND;

            var result = AppDialog.Show(
               "Confirm Clear",
               "Clear mapped end-prep on pipe fittings?\n\n" +
               $"This will clear {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.FittingsMapParameters, false, false)} on all fittings in the active view.",
               MessageDialogIcon.Warning,
               MessageDialogButtons.YesNo);

            if (result != MessageDialogResult.Yes)
                return Result.Succeeded;

            int processed = 0, paramResets = 0;

            var win = new ProgressWindow();
            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                new WindowInteropHelper(win).Owner = hwnd;

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(fittings.Count, "Clearing Fittings End Prep…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (Transaction tx = new Transaction(doc, "Clear Fitting End Prep"))
                {
                    tx.Start();

                    foreach (Element f in fittings)
                    {
                        if (win.IsCanceled) break;

                        paramResets += ClearParam(f, end1);
                        paramResets += ClearParam(f, end2);
                        paramResets += ClearParam(f, endPrep);
                        paramResets += ClearParam(f, headerND);

                        processed++;
                        win.UpdateSmart(processed, fittings.Count, $"Clearing… {processed} / {fittings.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(fittings.Count, fittings.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", processed, "Clearing fittings end prep has been cancelled");
                    return Result.Cancelled;
                }

                win.Done($"Successfully cleared {paramResets} fittings end prep", fittings.Count, "Clearing Fittings End Prep Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static int ClearParam(Element e, string paramName)
        {
            if(e == null) return 0;

            Parameter p = e.LookupParameter(paramName);
           
            if (p == null) return 0;

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        if (!string.IsNullOrEmpty(p.AsString())) { p.Set(string.Empty); return 1; }
                        break;
                    case StorageType.ElementId:
                        if (p.AsElementId() != ElementId.InvalidElementId) { p.Set(ElementId.InvalidElementId); return 1; }
                        break;
                    case StorageType.Integer:
                        if (p.AsInteger() != 0) { p.Set(0); return 1; }
                        break;
                    case StorageType.Double:
                        if (Math.Abs(p.AsDouble()) > 1e-09) { p.Set(0.0); return 1; }
                        break;
                }
            }
            catch { /* read-only etc. */ }

            return 0;
        }
    }
}
