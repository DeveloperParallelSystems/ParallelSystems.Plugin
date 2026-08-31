using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ParallelSystemPlugin.UI;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearPipeFilterCommand : IExternalCommand
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

            UIDocument uidoc = data.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            string[] filterNames =
            {
            "Pipe Max Length",
            "Pipe Too Long",
            "Pipe Too Short"
            };

            using (Transaction t = new Transaction(doc, "Remove Pipe Filters"))
            {
                t.Start();

                foreach (ElementId filterId in activeView.GetFilters())
                {
                    ParameterFilterElement filter =
                        doc.GetElement(filterId) as ParameterFilterElement;

                    if (filter != null && filterNames.Contains(filter.Name))
                    {
                        activeView.SetIsFilterEnabled(filterId, false);
                    }
                }

                t.Commit();
            }

            AppDialog.Success("Remove Pipe Filter", "Pipe filters have been removed.");

            return Result.Succeeded;
        }
    }
}
