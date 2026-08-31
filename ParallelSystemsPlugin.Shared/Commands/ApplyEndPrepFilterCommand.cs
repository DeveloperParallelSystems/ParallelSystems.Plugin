using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Markup;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Compatibility;
using ParallelSystemsPlugin.Models.Configs;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ApplyEndPrepFilterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn(
                    "Access Denied",
                    "Your account is not authorized to use this function.");

                return Result.Cancelled;
            }

            UIDocument uiDoc = commandData.Application.ActiveUIDocument;
            Document doc = uiDoc.Document;
            View view = doc.ActiveView;

            UIApplication uiapp = commandData.Application;

            using (Transaction trans = new Transaction(doc, "Create End Prep Filters"))
            {
                trans.Start();

                ElementId pipeEndPrepParamId = GetPipeEndPrepParameterId(doc);

                if (pipeEndPrepParamId == null)
                {
                    AppDialog.Error("Error", "Pipe End Prep parameter not found.");
                    return Result.Failed;
                }

                var filters = ParallelSystemsPlugin.Helpers.EndPrepFilter.GetFilterConfigurations();

                var categories = new List<ElementId>
                {
                    new ElementId(BuiltInCategory.OST_PipeCurves),
                    new ElementId(BuiltInCategory.OST_PipeFitting)
                };

                foreach (var filter in filters)
                {
                    // CreateFilter(doc, view, filter, pipeEndPrepParamId);
                    var filterElement = GetOrCreateFilter(doc, filter.Name, categories, CreateEqualsRule(filter.Values, pipeEndPrepParamId));

                    // Skip if color is white
                    if (filter.Color.Red == 255 &&
                        filter.Color.Green == 255 &&
                        filter.Color.Blue == 255)
                    {
                        continue;
                    }


                    ApplyFilterToView(
                        view,
                        filterElement.Id,
                        new Color(filter.Color.Red, filter.Color.Green, filter.Color.Blue),
                        doc);
                }

                trans.Commit();
            }

            AppDialog.Info(uiapp, "End Prep Filter", "End Prep filters created");

            return Result.Succeeded;
        }

        private FillPatternElement GetSolidFillPattern(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);
        }

        private void ApplyFilterToView(View view, ElementId filterId, Color color, Document doc)
        {
            if (view.GetFilters().Contains(filterId))
            {
                view.RemoveFilter(filterId);
            }

            view.AddFilter(filterId);
            //// Ensure filter is enabled
            //view.SetFilterVisibility(filterId, true);

            FillPatternElement solidFill = GetSolidFillPattern(doc);

            OverrideGraphicSettings ogs = new OverrideGraphicSettings();

            ogs.SetSurfaceForegroundPatternId(solidFill.Id);
            ogs.SetSurfaceForegroundPatternColor(color);

            view.SetFilterOverrides(filterId, ogs);
        }


        private ElementId GetPipeEndPrepParameterId(Document doc)
        {
            var element = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .WhereElementIsNotElementType()
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .FirstOrDefault();

            if (element == null)
                return null;

            var param = element.LookupParameter("Pipe End Prep");

            if (param == null)
                return null;

            return param.Id;
        }

        private ElementFilter CreateEqualsRule(List<string> values, ElementId paramId)
        {
            List<ElementFilter> filters = new List<ElementFilter>();

            foreach (string value in values)
            {
                FilterRule rule = RevitApiCompatibility.CreateCaseInsensitiveEqualsRule(paramId, value);
                filters.Add(new ElementParameterFilter(rule));
            }

            // OR condition
            return new LogicalOrFilter(filters);
        }

        private ParameterFilterElement GetOrCreateFilter(
            Document doc,
            string name,
            ICollection<ElementId> categoryIds,
            ElementFilter ruleFilter)
        {
            ParameterFilterElement filter = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(f => f.Name == name);

            if (filter == null)
            {
                filter = ParameterFilterElement.Create(
                    doc,
                    name,
                    categoryIds
                );
            }
            else
            {
                // Reset categories in case user modified them
                filter.SetCategories(categoryIds);
            }

            // Overwrite the filter rule
            filter.SetElementFilter(ruleFilter);

            return filter;
        }
    }
}
