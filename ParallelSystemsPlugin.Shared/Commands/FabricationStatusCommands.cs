using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.Fabrication;
using System;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ShowFabricationReadyCommand : IExternalCommand
    {
        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            try
            {
                UIApplication uiApp =
                    commandData.Application;

                UIDocument uiDoc =
                    uiApp.ActiveUIDocument;

                FabricationPreflightResult preflight =
                    FabricationPreflightService
                        .CheckViewForTemporaryIsolation(
                            uiDoc?.Document,
                            uiDoc?.ActiveView);

                if (!preflight.CanProceed)
                {
                    AppDialog.ShowDetailed(
                        uiApp,
                        "Fabrication Status Preflight",
                        "Show Ready cannot continue.",
                        "The active view is not safe to modify for " +
                        "temporary fabrication isolation. Resolve the " +
                        "worksharing condition, then run Show Ready again.",
                        preflight.BuildDetails(),
                        MessageDialogIcon.Error);

                    return Result.Cancelled;
                }

                string activeViewName;

                int count = FabricationStepService.ShowFabricationStatus(
                    commandData.Application,
                    true,
                    out activeViewName);

                if (count == 0)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Fabrication Status",
                        "No processed fabrication components are visible in the active view.\n\n" +
                        "Generate at least one Fabrication STEP, then run Show Ready from a view containing those source elements.");

                    return Result.Succeeded;
                }

                AppDialog.Success(
                    uiApp,
                    "Fabrication Status",
                    "Fabrication-ready components isolated: " +
                    count +
                    "\n\nActive view: " + activeViewName +
                    "\n\nUse Revit's built-in Reset Temporary Hide/Isolate command to show all elements again.");

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                AppDialog.Error(
                    commandData.Application,
                    "Fabrication Status Error",
                    ex.Message);

                return Result.Cancelled;
            }
        }
    }
}
