using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.Helpers;

namespace ParallelSystemPlugin.UI
{
    public static class ProcurementMenu
    {
        public static void Build(RibbonPanel panel)
        {
            if (panel == null) return;

            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);
            if (string.IsNullOrEmpty(assemblyDirectory)) return;

            // Export BOM + Filter Items are kept together under one split button.
            Autodesk.Revit.UI.SplitButton bomSplit = ParallelSystemsPlugin.Helpers.SplitButton.AddSplitButton(
                panel,
                "PS_ExportBomSplit",
                "Export BOM");

            PushButtonData exportBomData = ParallelSystemsPlugin.Helpers.PushButton.Create(
                "PS_ExportBom",
                "Export BOM",
                "ParallelSystemsPlugin.Commands.ExportBomCommand");

            PushButtonData filterItemsData = ParallelSystemsPlugin.Helpers.PushButton.Create(
                "PS_FilterItems",
                "Filter Items",
                "ParallelSystemsPlugin.Commands.FilterItemsCommand");

            Autodesk.Revit.UI.PushButton exportBomButton = bomSplit.AddPushButton(exportBomData);
            Autodesk.Revit.UI.PushButton filterItemsButton = bomSplit.AddPushButton(filterItemsData);

            if (exportBomButton != null)
                bomSplit.CurrentButton = exportBomButton;

            ParallelSystemsPlugin.Helpers.PushButton.ApplySettings(
                exportBomButton,
                "Exports a grouped Bill of Materials (BOM) from components in the active view.",
                Path.Combine(assemblyDirectory, "Icons", "export_16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "export_32.ico"));

            ParallelSystemsPlugin.Helpers.PushButton.ApplySettings(
                filterItemsButton,
                "Lists BOM components visible in the active view, grouped by component family/type with quantities. Unchecking an item temporarily hides every matching instance for report validation.",
                null,
                Path.Combine(assemblyDirectory, "Icons", "ApplyFilter32.ico"));

            // New: Publish BOM
            ParallelSystemsPlugin.Helpers.PushButton.Add(
                panel,
                "PS_PublishBom",
                "Publish BOM",
                "ParallelSystemsPlugin.Commands.PublishBomCommand",
                "Generates the sheets CSV and publishes it to the configured Publish Site folder.",
                Path.Combine(assemblyDirectory, "Icons", "publish16.ico"),
                Path.Combine(assemblyDirectory, "Icons", "publish32.ico")
            );
        }
    }
}
