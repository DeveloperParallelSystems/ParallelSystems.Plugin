using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.Commands;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Models.Configs;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ClearEndPrepFilterCommand : IExternalCommand
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

            UIApplication uiapp = data.Application;

            List<string> filterNames = GetFilterNames();

            using (Transaction t = new Transaction(doc, "Remove End Prep Filter"))
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

            AppDialog.Info(uiapp, "End Prep Filter", "End Prep filters have been removed.");
            
            return Result.Succeeded;
        }


        private List<string> GetFilterNames()
        {
            return ParallelSystemsPlugin.Helpers.EndPrepFilter.GetFilterConfigurations()
                .Select(x => x.Name)
                .ToList();
        }
    }
}
