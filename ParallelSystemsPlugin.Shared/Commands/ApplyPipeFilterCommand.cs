using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ParallelSystemPlugin.UI;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ApplyPipeFilterCommand : IExternalCommand
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

            UIDocument uidoc = commandData.Application.ActiveUIDocument;
            Document doc = uidoc.Document;
            View activeView = doc.ActiveView;

            var pipeFilterConfig = AppConfig.CurrentConfig.ToolsConfig.PipeFilterConfig;

            using (Transaction t = new Transaction(doc, "Apply Pipe Filter"))
            {
                t.Start();

                ElementId pipeCategoryId = new ElementId(BuiltInCategory.OST_PipeCurves);

                ParameterFilterElement maxFilter = GetOrCreateFilter(
                    doc,
                    "Pipe Max Length",
                    pipeCategoryId,
                    CreateEqualsRule(pipeFilterConfig.MaxPipeLength)
                );

                ParameterFilterElement longFilter = GetOrCreateFilter(
                    doc,
                    "Pipe Too Long",
                    pipeCategoryId,
                    CreateGreaterRule(pipeFilterConfig.LongPipeLength)
                );

                ParameterFilterElement shortFilter = GetOrCreateFilter(
                    doc,
                    "Pipe Too Short",
                    pipeCategoryId,
                    CreateLessRule(pipeFilterConfig.ShortPipeLength)
                );

                var maxPipeColor = pipeFilterConfig.MaxPipeColor;
                var longPipeColor = pipeFilterConfig.LongPipeColor;
                var shortPipeColor = pipeFilterConfig.ShortPipeColor;

                ApplyFilterToView(activeView, maxFilter.Id, new Color(maxPipeColor.R, maxPipeColor.G, maxPipeColor.B), doc);
                ApplyFilterToView(activeView, longFilter.Id, new Color(longPipeColor.R, longPipeColor.G, longPipeColor.B), doc);
                ApplyFilterToView(activeView, shortFilter.Id, new Color(shortPipeColor.R, shortPipeColor.G, shortPipeColor.B), doc);

                t.Commit();
            }

            AppDialog.Success("Apply Pipe Filter", "Filters applied to the active view.");

            return Result.Succeeded;
        }

        // ------------------------------------------------
        // Create or Overwrite Filter
        // ------------------------------------------------
        private ParameterFilterElement GetOrCreateFilter(
            Document doc,
            string name,
            ElementId categoryId,
            ElementFilter ruleFilter)
        {
            ParameterFilterElement filter = new FilteredElementCollector(doc)
                .OfClass(typeof(ParameterFilterElement))
                .Cast<ParameterFilterElement>()
                .FirstOrDefault(f => f.Name == name);

            if (filter == null)
            {
                IList<ElementId> categories = new List<ElementId> { categoryId };

                filter = ParameterFilterElement.Create(
                    doc,
                    name,
                    categories
                );
            }
            else
            {
                // Reset categories in case user modified them
                filter.SetCategories(new List<ElementId> { categoryId });
            }

            // Overwrite the filter rule
            filter.SetElementFilter(ruleFilter);

            return filter;
        }

        private FillPatternElement GetSolidFillPattern(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(FillPatternElement))
                .Cast<FillPatternElement>()
                .FirstOrDefault(x => x.GetFillPattern().IsSolidFill);
        }

        // ------------------------------------------------
        // Apply Filter to View with Graphics
        // ------------------------------------------------
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

        // ------------------------------------------------
        // Filter Rules
        // ------------------------------------------------
        private ElementFilter CreateEqualsRule(double valueMM)
        {
            ElementId paramId = new ElementId(BuiltInParameter.CURVE_ELEM_LENGTH);

            double valueFeet = valueMM / 304.8;

            FilterRule rule = ParameterFilterRuleFactory.CreateEqualsRule(
                paramId,
                valueFeet,
                0.001);

            return new ElementParameterFilter(rule);
        }

        private ElementFilter CreateGreaterRule(double valueMM)
        {
            ElementId paramId = new ElementId(BuiltInParameter.CURVE_ELEM_LENGTH);

            double valueFeet = valueMM / 304.8;

            FilterRule rule = ParameterFilterRuleFactory.CreateGreaterRule(
                paramId,
                valueFeet,
                0.001);

            return new ElementParameterFilter(rule);
        }

        private ElementFilter CreateLessRule(double valueMM)
        {
            ElementId paramId = new ElementId(BuiltInParameter.CURVE_ELEM_LENGTH);

            double valueFeet = valueMM / 304.8;

            FilterRule rule = ParameterFilterRuleFactory.CreateLessRule(
                paramId,
                valueFeet,
                0.001);

            return new ElementParameterFilter(rule);
        }
    }
}
