using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Linq;
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
                var documents = data.Application.Application.Documents.Cast<Document>()
                    .Where(document => !document.IsLinked).ToList();
                var checkedOut = documents.Where(HasCheckedOutItems).ToList();
                if (checkedOut.Count > 0)
                {
                    var dialog = new TaskDialog("Parallel Systems update")
                    {
                        MainInstruction = "You have checked-out elements or worksets. " + action + "?",
                        MainContent = "You own checked-out items in:\n" +
                            string.Join("\n", checkedOut.Select(document => document.Title)) +
                            "\n\nYes will relinquish your checked-out items (Relinquish All Mine), then close ALL open documents without saving and discard their unsaved changes. " +
                            "Revit will close, the update will install automatically, and Revit will restart. " +
                            "Nothing will be saved or synchronized with central. Discarded changes cannot be recovered. " +
                            "Choose No to leave everything unchanged.",
                        CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                        DefaultButton = TaskDialogResult.No
                    };
                    if (dialog.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                }

                // Untitled and modified documents are intentionally closed without saving.
                foreach (Document document in documents)
                {
                    if (document.IsModifiable)
                        throw new System.InvalidOperationException("Finish editing before updating: " + document.Title);
                }
                ParallelSystems.ProductSupport.ProductLifecycle.EnsureUpdaterBackground();
                App.SetDiscardChangesForUpdate(true);
                ParallelSystems.ProductSupport.ProductLifecycle.InstallStartupUpdate(year, action.Substring("Install v".Length), unattended: true);
                return Result.Succeeded;
            }
            catch (System.Exception ex)
            {
                App.SetDiscardChangesForUpdate(false);
                TaskDialog.Show("Parallel Systems updates", "The update could not proceed. Revit will remain open.\n\n" + ex.Message);
                return Result.Cancelled;
            }
        }

        private static bool HasCheckedOutItems(Document document)
        {
            if (!document.IsWorkshared) return false;
            using (var worksets = new FilteredWorksetCollector(document))
            {
                if (worksets.Any(workset => workset.IsEditable)) return true;
            }
            // Include element types as well as instances: both can be borrowed.
            using (var elements = new FilteredElementCollector(document))
            {
                elements.WherePasses(new LogicalOrFilter(
                    new ElementIsElementTypeFilter(), new ElementIsElementTypeFilter(true)));
                return elements.Any(element => WorksharingUtils.GetCheckoutStatus(document, element.Id) == CheckoutStatus.OwnedByCurrentUser);
            }
        }
    }
}
