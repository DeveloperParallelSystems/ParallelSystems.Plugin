using Autodesk.Revit.UI;
using System.IO;

namespace ParallelSystemsPlugin.UI
{
    public static class FabricationMenu
    {
        public static void Build(RibbonPanel panel)
        {
            if (panel == null)
                return;

            string assemblyPath = typeof(App).Assembly.Location;
            string assemblyDirectory = Path.GetDirectoryName(assemblyPath);

            string icon16 = string.IsNullOrWhiteSpace(assemblyDirectory)
                ? null
                : Path.Combine(
                    assemblyDirectory,
                    "Icons",
                    "pipe_v1_16.ico");

            string icon32 = string.IsNullOrWhiteSpace(assemblyDirectory)
                ? null
                : Path.Combine(
                    assemblyDirectory,
                    "Icons",
                    "pipe_v1_32.ico");

            SplitButton splitButton =
                Helpers.SplitButton.AddSplitButton(
                    panel,
                    "PS_FabricationStepSplit",
                    "Fabrication STEP");

            PushButtonData generateData = Helpers.PushButton.Create(
                "PS_GenerateFabricationStep",
                "Fabrication\nSTEP",
                "ParallelSystemsPlugin.Commands.GenerateFabricationStepCommand");

            PushButtonData readyData = Helpers.PushButton.Create(
                "PS_ShowFabricationReady",
                "Show Ready",
                "ParallelSystemsPlugin.Commands.ShowFabricationReadyCommand");

            PushButtonData diagnosticsData = Helpers.PushButton.Create(
                "PS_ExportFabricationDiagnostics",
                "Export\nDiagnostics",
                "ParallelSystemsPlugin.Commands.ExportFabricationDiagnosticsCommand");

            PushButton generateButton =
                splitButton.AddPushButton(generateData);

            PushButton readyButton =
                splitButton.AddPushButton(readyData);

            PushButton diagnosticsButton =
                splitButton.AddPushButton(diagnosticsData);

            if (generateButton != null)
                splitButton.CurrentButton = generateButton;

            Helpers.PushButton.ApplySettings(
                generateButton,
                "Checks worksharing freshness and ownership, creates temporary hollow fabrication geometry, validates ID/OD/wall thickness, exports a verified STEP, and rolls back the temporary Revit model. Revit 2025 or newer is required.",
                icon16,
                icon32);

            Helpers.PushButton.ApplySettings(
                readyButton,
                "Temporarily isolates source pipes, fittings, and accessories successfully exported on this workstation. The active view is checked for worksharing conflicts before isolation.",
                icon16,
                icon32);

            Helpers.PushButton.ApplySettings(
                diagnosticsButton,
                "Developer use only. Exports selected fabrication components, direct connection context, parameters, connectors, worksharing state, and detailed source geometry into one compact JSON file. The model is not modified.",
                icon16,
                icon32);
        }
    }
}
