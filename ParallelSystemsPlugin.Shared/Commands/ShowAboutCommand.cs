using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using System;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowAboutCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            var uiapp = data.Application;
            var dlg = new AboutDialog();
            dlg.ShowModal(uiapp.MainWindowHandle); // C#7.3-safe, overload below
            return Result.Succeeded;
        }
    }
}
