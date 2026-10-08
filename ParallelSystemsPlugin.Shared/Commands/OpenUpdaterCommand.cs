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
            try
            {
                var year = int.Parse(data.Application.Application.VersionNumber);
                var action = ParallelSystems.ProductSupport.ProductLifecycle.UpdateActionLabel("plugin", year);
                if (!action.StartsWith("Install v", System.StringComparison.Ordinal))
                {
                    ParallelSystems.ProductSupport.ProductLifecycle.OpenUpdater("plugin", year);
                    return Result.Succeeded;
                }
                var dialog = new TaskDialog("Parallel Systems update")
                {
                    MainInstruction = action + "?",
                    MainContent = "This will synchronize open workshared models with central, save other open documents, close Revit, install the update automatically, and start Revit again. Continue?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                    DefaultButton = TaskDialogResult.No
                };
                if (dialog.Show() != TaskDialogResult.Yes) return Result.Cancelled;

                // Preflight every document before saving any of them. Never discard work.
                foreach (Document document in data.Application.Application.Documents)
                {
                    if (document.IsLinked) continue;
                    if (document.IsReadOnly || document.IsModifiable || string.IsNullOrEmpty(document.PathName))
                        throw new System.InvalidOperationException("Save and finish editing all open documents before updating: " + document.Title);
                }
                foreach (Document document in data.Application.Application.Documents)
                {
                    if (document.IsLinked) continue;
                    if (document.IsWorkshared)
                    {
                        using (var transact = new TransactWithCentralOptions())
                        using (var sync = new SynchronizeWithCentralOptions())
                        using (var relinquish = new RelinquishOptions(true))
                        {
                            sync.SetRelinquishOptions(relinquish);
                            sync.SaveLocalBefore = true;
                            sync.SaveLocalAfter = true;
                            document.SynchronizeWithCentral(transact, sync);
                        }
                    }
                    else if (document.IsModified) document.Save();
                }
                ParallelSystems.ProductSupport.ProductLifecycle.EnsureUpdaterBackground();
                ParallelSystems.ProductSupport.ProductLifecycle.InstallStartupUpdate(year, action.Substring("Install v".Length), unattended: true);
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                TaskDialog.Show("Parallel Systems updates", "The update could not proceed. Revit will remain open.\n\n" + ex.Message);
                return Result.Cancelled;
            }
        }
    }
}
