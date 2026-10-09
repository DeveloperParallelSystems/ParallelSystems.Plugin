using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.UI.Dialogs;
using System;
using ParallelSystemPlugin.UI;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowConfigurationsCommand : IExternalCommand
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

            var uiapp = data.Application;
            UIDocument uidoc = uiapp.ActiveUIDocument;
            Document doc = uidoc?.Document;
            var dlg = new Configurations(doc, int.Parse(uiapp.Application.VersionNumber));

            dlg.ShowModal(uiapp.MainWindowHandle);
            // Run the existing Revit command after the modal window has closed so
            // it can show its confirmation and allow the updater to close Revit.
            if (dlg.UpdateRequested)
                return new ParallelSystemsPlugin.Commands.OpenUpdaterCommand().Execute(data, ref message, elements);

            return Result.Succeeded;
        }
    }
}
