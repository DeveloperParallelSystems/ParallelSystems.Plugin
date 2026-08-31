using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using adWin = Autodesk.Windows;

namespace ParallelSystemsPlugin.UI
{
    public static class ToolsMenu
    {
        private const string RibbonTabName = "ParallelSystems";
        private const string RibbonPanelName = "Tools";

        public static void Build(RibbonPanel panel)
        {
            // =========================================================
            // Get Assembly Path (used for loading icons)
            // =========================================================
            var assemblyPath = typeof(ParallelSystemsPlugin.App).Assembly.Location;
            var assemblyDirectory = Path.GetDirectoryName(assemblyPath);

            // =========================================================
            // Create Main Ribbon Buttons
            // =========================================================

            // Pipe Elevation Checker button
            PushButtonData elevationCheckerBtn = Helpers.PushButton.Create(
                "pipeSlopeBtn",
                "Elevation Check",
                "ParallelSystemPlugin.Commands.PipeSlopeCheckCommand"
            );

            // Renaming dropdown button
            PulldownButtonData renameDropdownBtn = Helpers.PulldownButton.Create(
                "assemblyRenameDropdown",
                "Renaming"
            );

            // Rename sheet //
            PushButtonData renameSheetBtn = Helpers.PushButton.Create(
                "renameSheetBtn",
                "Sheet Number Check",
                "ParallelSystemPlugin.Commands.RenameSheetNumbersCommand"
            );

            // Rename sheet //
            PushButtonData bomCheckBtn = Helpers.PushButton.Create(
                "bomCheckBtn",
                "BOM Check",
                "ParallelSystemPlugin.Commands.BOMCheckCommand"
            );

            // =========================================================
            // Add Buttons to Ribbon (Stacked Layout)
            // =========================================================
            List<RibbonItem> stackedItems = panel
                .AddStackedItems(elevationCheckerBtn, renameDropdownBtn, renameSheetBtn)
                .ToList();

            // =========================================================
            // Renaming Pulldown Setup
            // =========================================================
            Autodesk.Revit.UI.PulldownButton renamePulldown = stackedItems[1] as Autodesk.Revit.UI.PulldownButton;

            // Dropdown Button Data
            PushButtonData importCsvBtn = Helpers.PushButton.Create(
                "renameImportBtn",
                "Import CSV",
                "ParallelSystemPlugin.Commands.RenameImportCommand"
            );

            PushButtonData exportCsvBtn = Helpers.PushButton.Create(
                "renameExportBtn",
                "Export CSV",
                "ParallelSystemPlugin.Commands.RenameExportCommand"
            );

            // Add buttons to rename pulldown
            Autodesk.Revit.UI.PushButton importButton = renamePulldown.AddPushButton(importCsvBtn);
            Autodesk.Revit.UI.PushButton exportButton = renamePulldown.AddPushButton(exportCsvBtn);

            // End Prep Check dropdown button
            PulldownButtonData endPrepPulldownButton = Helpers.PulldownButton.Create(
                "endPrepCheckLengthDropdown",
                "End Prep Check"
            );

            // Pipe Length Check dropdown button
            PulldownButtonData pipeLengthDropdownBtn = Helpers.PulldownButton.Create(
                "pipeCheckLengthDropdown",
                "Pipe Length Check"
            );

            // =========================================================
            // Add Buttons to Ribbon (Stacked Layout)
            // =========================================================
            List<RibbonItem> stackedItems2 = panel
                .AddStackedItems(pipeLengthDropdownBtn, endPrepPulldownButton, bomCheckBtn)
                .ToList();

            // =========================================================
            // Pipe Length Check Pulldown Setup
            // =========================================================
            Autodesk.Revit.UI.PulldownButton pipeLengthPulldown = stackedItems2[0] as Autodesk.Revit.UI.PulldownButton;

            PushButtonData applyPipeFilterBtnData = Helpers.PushButton.Create(
                "applyPipeFilterBtn",
                "Apply Pipe Filter",
                "ParallelSystemPlugin.Commands.ApplyPipeFilterCommand"
            );

            PushButtonData clearPipeFilterBtnData = Helpers.PushButton.Create(
                "clearPipeFilterBtn",
                "Clear Pipe Filter",
                "ParallelSystemPlugin.Commands.ClearPipeFilterCommand"
            );

            Autodesk.Revit.UI.PushButton applyPipeFilterBtn = pipeLengthPulldown.AddPushButton(applyPipeFilterBtnData);
            Autodesk.Revit.UI.PushButton clearPipeFilterBtn = pipeLengthPulldown.AddPushButton(clearPipeFilterBtnData);

            // =========================================================
            // End Prep Pulldown Setup
            // =========================================================
            Autodesk.Revit.UI.PulldownButton endPrepPulldown = stackedItems2[1] as Autodesk.Revit.UI.PulldownButton;

            PushButtonData applyEndPrepBtnData = Helpers.PushButton.Create(
                "applyEndPrepFilterBtn",
                "Apply End Prep Filter",
                "ParallelSystemPlugin.Commands.ApplyEndPrepFilterCommand"
            );

            PushButtonData clearEndPrepBtnData = Helpers.PushButton.Create(
                "clearEndPrepFilterBtn",
                "Clear End Prep Filter",
                "ParallelSystemPlugin.Commands.ClearEndPrepFilterCommand"
            );

            Autodesk.Revit.UI.PushButton applyEndPrepFilterBtn = endPrepPulldown.AddPushButton(applyEndPrepBtnData);
            Autodesk.Revit.UI.PushButton clearEndPrepFilterBtn = endPrepPulldown.AddPushButton(clearEndPrepBtnData);

            // =========================================================
            // Apply Button Settings (Icons & Tooltips)
            // =========================================================

            // Elevation Checker
            Helpers.PushButton.ApplySettings(
                stackedItems[0] as Autodesk.Revit.UI.PushButton,
                tooltip: "Pipe Slope Check",
                icon16: Path.Combine(assemblyDirectory, "Icons", "PipeSlope16.ico")
            );


            // Rename Sheet
            Helpers.PushButton.ApplySettings(
                stackedItems[2] as Autodesk.Revit.UI.PushButton,
                tooltip: "Rename Sheet",
                icon16: Path.Combine(assemblyDirectory, "Icons", "Paper16.ico")
            );

            // BOM Check
            Helpers.PushButton.ApplySettings(
                stackedItems2[2] as Autodesk.Revit.UI.PushButton,
                tooltip: "BOM Check",
                icon16: Path.Combine(assemblyDirectory, "Icons", "quality-control16.ico")
            );

            // Rename - Import CSV
            Helpers.PushButton.ApplySettings(
                importButton,
                tooltip: "Import CSV",
                icon32: Path.Combine(assemblyDirectory, "Icons", "import32.ico")
            );

            // Rename - Export CSV
            Helpers.PushButton.ApplySettings(
                exportButton,
                tooltip: "Export CSV",
                icon32: Path.Combine(assemblyDirectory, "Icons", "export32.ico")
            );

            // Rename Pulldown
            Helpers.PulldownButton.ApplySettings(
                renamePulldown,
                tooltip: "Assembly Rename",
                icon16: Path.Combine(assemblyDirectory, "Icons", "Pencil16.ico")
            );

            // Pipe Filter - Apply
            Helpers.PushButton.ApplySettings(
                applyPipeFilterBtn,
                tooltip: "Apply Pipe Filter",
                icon32: Path.Combine(assemblyDirectory, "Icons", "ApplyFilter32.ico")
            );

            // Pipe Filter - Clear
            Helpers.PushButton.ApplySettings(
                clearPipeFilterBtn,
                tooltip: "Clear Pipe Filter",
                icon32: Path.Combine(assemblyDirectory, "Icons", "ClearFilter32.ico")
            );

            // Pipe Length Pulldown
            Helpers.PulldownButton.ApplySettings(
                pipeLengthPulldown,
                tooltip: "Pipe Filter",
                icon16: Path.Combine(assemblyDirectory, "Icons", "PipeCheck16.ico")
            );

            // End Prep - Apply
            Helpers.PushButton.ApplySettings(
                applyEndPrepFilterBtn,
                tooltip: "Apply End Prep Filter",
                icon32: Path.Combine(assemblyDirectory, "Icons", "ApplyFilter32.ico")
            );

            // End Prep - Clear
            Helpers.PushButton.ApplySettings(
                clearEndPrepFilterBtn,
                tooltip: "Clear End Prep Filter",
                icon32: Path.Combine(assemblyDirectory, "Icons", "ClearFilter32.ico")
            );

            // End Prep Pulldown
            Helpers.PulldownButton.ApplySettings(
                endPrepPulldown,
                tooltip: "End Prep Filter",
                icon16: Path.Combine(assemblyDirectory, "Icons", "pipe_v1_16.ico")
            );

            // =========================================================
            // Hide stacked item text so only icons are shown
            // =========================================================
            HideRibbonItemText(RibbonTabName, RibbonPanelName, "pipeSlopeBtn");
            HideRibbonItemText(RibbonTabName, RibbonPanelName, "assemblyRenameDropdown");
            HideRibbonItemText(RibbonTabName, RibbonPanelName, "pipeCheckLengthDropdown");
            HideRibbonItemText(RibbonTabName, RibbonPanelName, "endPrepCheckLengthDropdown");
        }

        private static void HideRibbonItemText(string tabName, string panelName, string itemId)
        {
            try
            {
                var ribbon = adWin.ComponentManager.Ribbon;
                if (ribbon == null) return;

                var tab = ribbon.Tabs
                    .FirstOrDefault(t => string.Equals(t.Title, tabName, StringComparison.OrdinalIgnoreCase));
                if (tab == null) return;

                var panel = tab.Panels
                    .FirstOrDefault(p => p.Source != null &&
                                         string.Equals(p.Source.Title, panelName, StringComparison.OrdinalIgnoreCase));
                if (panel?.Source == null) return;

                var awItem = FindRibbonItem(panel.Source.Items, itemId);
                if (awItem == null) return;

                SetIconOnly(awItem);
            }
            catch
            {
                // Ignore UI-only failures so ribbon creation still succeeds
            }
        }

        private static object FindRibbonItem(System.Collections.IEnumerable items, string itemId)
        {
            if (items == null) return null;

            foreach (var item in items)
            {
                if (item == null) continue;

                var idProp = item.GetType().GetProperty("Id");
                if (idProp != null)
                {
                    var idValue = idProp.GetValue(item) as string;
                    if (string.Equals(idValue, itemId, StringComparison.OrdinalIgnoreCase))
                        return item;
                }

                var sourceProp = item.GetType().GetProperty("Source");
                var sourceObj = sourceProp?.GetValue(item);
                var sourceItemsProp = sourceObj?.GetType().GetProperty("Items");
                var sourceItems = sourceItemsProp?.GetValue(sourceObj) as System.Collections.IEnumerable;

                var nestedFromSource = FindRibbonItem(sourceItems, itemId);
                if (nestedFromSource != null) return nestedFromSource;

                var itemsProp = item.GetType().GetProperty("Items");
                var nestedItems = itemsProp?.GetValue(item) as System.Collections.IEnumerable;

                var nested = FindRibbonItem(nestedItems, itemId);
                if (nested != null) return nested;
            }

            return null;
        }

        private static void SetIconOnly(object ribbonItem)
        {
            if (ribbonItem == null) return;

            var type = ribbonItem.GetType();

            var showTextProp = type.GetProperty("ShowText");
            if (showTextProp != null && showTextProp.CanWrite)
                showTextProp.SetValue(ribbonItem, false);

            var showImageProp = type.GetProperty("ShowImage");
            if (showImageProp != null && showImageProp.CanWrite)
                showImageProp.SetValue(ribbonItem, true);
        }
    }
}