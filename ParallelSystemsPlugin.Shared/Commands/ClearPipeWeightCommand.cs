using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI; // AppDialog, ProgressWindow
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearPipeWeightCommand : IExternalCommand
    {
        private const string DRY_PARAM = "Vic_Weight";
        private const string WET_PARAM = "Wet Weight";

        private static List<Element> CollectTargetFittings(Document doc, ElementId viewId)
        {
            List<Element> targets = new List<Element>();

            try
            {
                // Fast filter: family+type string contains "nipple" OR "shaped branch" (case-insensitive)
                ParameterValueProvider pvp =
                    new ParameterValueProvider(new ElementId(BuiltInParameter.ELEM_FAMILY_AND_TYPE_PARAM));
                FilterStringRuleEvaluator contains = new FilterStringContains();
                FilterStringRule ruleBranch = RevitApiCompatibility.CreateCaseInsensitiveStringRule(pvp, contains, "branch");
                ElementParameterFilter f2 = new ElementParameterFilter(ruleBranch);
                LogicalOrFilter orFilter = new LogicalOrFilter(new List<ElementFilter> { f2 });

                targets = new FilteredElementCollector(doc, viewId)
                    .OfCategory(BuiltInCategory.OST_PipeFitting)
                    .WhereElementIsNotElementType()
                    .WherePasses(orFilter)
                    .ToElements()
                    .ToList();
            }
            catch
            {
            }

            return targets;
        }

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
            var uidoc = data.Application.ActiveUIDocument;
            var doc = uidoc?.Document;
            var view = uidoc?.ActiveView;

            string strDryParam = AppConfig.CurrentConfig.PipeWeightMapParameters.DryWeight;
            string strWetParam = AppConfig.CurrentConfig.PipeWeightMapParameters.WetWeight;
            string strCladdingWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.CladdingWeight;
            string strFluidWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.FluidWeight;
            string strInsulationWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.InsulationWeight;
            string strOverallSizeParam = AppConfig.CurrentConfig.PipeWeightMapParameters.ComputedOverallSize;
            string strTotalWeightParam = AppConfig.CurrentConfig.PipeWeightMapParameters.TotalWeight;

            int numDecimals = AppConfig.CurrentConfig.PipeWeightMapParameters.NumOfDecimals;


            if (doc == null || view == null)
            {
                AppDialog.Info(uiapp,"Clear Pipe Weight", "No active document/view.");
                return Result.Cancelled;
            }

            var fittings = CollectTargetFittings(doc, view.Id);

            var pipes = new FilteredElementCollector(doc, view.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType()
                .ToElements()
                .ToList();

            var pipesAndFittings = fittings.Concat(pipes).ToList();

            if (pipesAndFittings.Count == 0)
            {
                AppDialog.Info(uiapp,"Clear Pipe Weight", "No pipes found in the active view.");
                return Result.Succeeded;
            }

            var result = AppDialog.Show(
              "Confirm Clear",
              "Clear mapped pipe's weight?\n\n" +
              $"This will clear {ParallelSystemsPlugin.Helpers.Config.BuildMapParametersConfig(AppConfig.CurrentConfig.PipeWeightMapParameters, false, false).ToLower()} on all pipes in the active view.",
              MessageDialogIcon.Warning,
              MessageDialogButtons.YesNo);

            if (result != MessageDialogResult.Yes)
                return Result.Succeeded;

            // Mirror your progress signature requirement
            // var fittings = pipes; // alias so we can call UpdateSmart(touched, fittings.Count, ...)

            int cleared = 0;
            int touched = 0;

            var win = new ProgressWindow();

            try
            {
                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(pipesAndFittings.Count, $"Clearing Pipe's Weight…", "Procesing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);

                using (var tx = new Transaction(doc, "Clear Pipe Weight"))
                {
                    tx.Start();

                    foreach (var e in pipesAndFittings)
                    {
                        bool anyCleared = false;

                        var dryParam = e.LookupParameter(strDryParam);
                        if (TryClearParameter(dryParam)) anyCleared = true;

                        var wetParam = e.LookupParameter(strWetParam);
                        if (TryClearParameter(wetParam)) anyCleared = true;

                        var claddingWeightParam = e.LookupParameter(strCladdingWeightParam);
                        if (TryClearParameter(claddingWeightParam)) anyCleared = true;
                       
                        var fluidWeightParam = e.LookupParameter(strFluidWeightParam);
                        if (TryClearParameter(fluidWeightParam)) anyCleared = true;
                        
                        var insulationWeightParam = e.LookupParameter(strInsulationWeightParam);
                        if (TryClearParameter(insulationWeightParam)) anyCleared = true;
                        
                        var overallSizeParam = e.LookupParameter(strOverallSizeParam);
                        if (TryClearParameter(overallSizeParam)) anyCleared = true;
                        
                        var totalWeightParam = e.LookupParameter(strTotalWeightParam);
                        if (TryClearParameter(totalWeightParam)) anyCleared = true;

                        if (anyCleared)
                        {
                            cleared++;
                            touched++;
                        }

                        // Progress (uses your exact signature/text)
                        win.UpdateSmart(touched, pipesAndFittings.Count, $"Mapping… {touched} / {pipesAndFittings.Count}");
                    }

                    tx.Commit();
                    win.UpdateSmart(touched, pipesAndFittings.Count, "Finalizing…", force: true);
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Mapping Cancelled", touched, "Clearing pipe's weight has been cancelled");
                    return Result.Cancelled;
                }
                win.Done($"Successfully Cleared {touched} pipe's weight.", pipesAndFittings.Count, "Clearing Pipe's Weight Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                if (win.IsVisible) win.Close();
                message = ex.Message;
                return Result.Failed;
            }
        }

        private static bool TryClearParameter(Parameter p)
        {
            if (p == null || p.IsReadOnly) return false;

            try
            {
                switch (p.StorageType)
                {
                    case StorageType.String:
                        p.Set(string.Empty);
                        return true;
                    case StorageType.Double:
                        p.Set(0.0);
                        return true;
                    case StorageType.Integer:
                        p.Set(0);
                        return true;
                    default:
                        try { p.SetValueString(string.Empty); return true; }
                        catch { return false; }
                }
            }
            catch
            {
                try { p.SetValueString(string.Empty); return true; } catch { return false; }
            }
        }
    }
}
