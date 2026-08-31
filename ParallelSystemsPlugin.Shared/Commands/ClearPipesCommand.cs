using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI; // ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Interop;

// ... (usings unchanged)

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearPipesCommand : IExternalCommand
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

            IList<Element> pipes = new FilteredElementCollector(doc, activeView.Id)
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


            var result = AppDialog.Show(
               "Confirm Clear",
               "Clear mapped end-prep on pipes?\n\n" +
               $"This will clear {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeMapParameters, false, false)} on all pipes in the active view.",
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
                win.Initialize(pipes.Count, "Clearing Pipe End Prep…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (Transaction tx = new Transaction(doc, "Clear Pipe End Prep"))
                {
                    tx.Start();

                    foreach (Element pipe in pipes)
                    {
                        if (win.IsCanceled) break;

                        paramResets += ClearParam(pipe, end1);
                        paramResets += ClearParam(pipe, end2);
                        paramResets += ClearParam(pipe, endPrep);

                        processed++;
                        win.UpdateSmart(processed, pipes.Count, $"Clearing… {processed} / {pipes.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(pipes.Count, pipes.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Clearing Cancelled", processed, "Clearing pipe end prep has been cancelled");
                    return Result.Cancelled;
                }

                win.Done($"Successfully cleared {paramResets} pipe end prep", pipes.Count, "Clearing Pipe End Prep Completed");        
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
