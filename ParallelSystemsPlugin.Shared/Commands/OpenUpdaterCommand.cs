using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class CheckForUpdatesCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { ParallelSystems.ProductSupport.ProductLifecycle.CheckForUpdates("plugin", int.Parse(data.Application.Application.VersionNumber)); return Result.Succeeded; }
            catch (System.Exception) { TaskDialog.Show("Parallel Systems updates", "The background check could not be requested. Install or repair the per-user Updater and try again."); return Result.Cancelled; }
        }
    }
    [Transaction(TransactionMode.Manual)]
    public class OpenUpdaterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { ParallelSystems.ProductSupport.ProductLifecycle.OpenUpdater("plugin", int.Parse(data.Application.Application.VersionNumber)); return Result.Succeeded; }
            catch (System.Exception) { TaskDialog.Show("Parallel Systems updates", "The Updater could not be opened. Install or repair the per-user Updater and try again."); return Result.Cancelled; }
        }
    }
}
