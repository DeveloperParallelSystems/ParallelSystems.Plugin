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

            string spoolIcon16 = ResolveIconPath(
                assemblyDirectory,
                "FabricationSpool16.ico");
            string spoolIcon32 = ResolveIconPath(
                assemblyDirectory,
                "FabricationSpool32.ico");
            string moduleIcon16 = ResolveIconPath(
                assemblyDirectory,
                "FabricationModule16.ico");
            string moduleIcon32 = ResolveIconPath(
                assemblyDirectory,
                "FabricationModule32.ico");
            string readyIcon16 = ResolveIconPath(
                assemblyDirectory,
                "FabricationReady16.ico");
            string readyIcon32 = ResolveIconPath(
                assemblyDirectory,
                "FabricationReady32.ico");
            string diagnosticsIcon16 = ResolveIconPath(
                assemblyDirectory,
                "FabricationDiagnostics16.ico");
            string diagnosticsIcon32 = ResolveIconPath(
                assemblyDirectory,
                "FabricationDiagnostics32.ico");

            SplitButton splitButton =
                Helpers.SplitButton.AddSplitButton(
                    panel,
                    "PS_FabricationStepSplit",
                    "Fabrication STEP");

            PushButtonData generateData = Helpers.PushButton.Create(
                "PS_GenerateFabricationStep",
                "Spool\nSTEP",
                "ParallelSystemsPlugin.Commands.GenerateFabricationStepCommand");

            PushButtonData moduleData = Helpers.PushButton.Create(
                "PS_GenerateModuleStep",
                "Module\nSTEP",
                "ParallelSystemsPlugin.Commands.GenerateModuleStepCommand");

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

            PushButton moduleButton =
                splitButton.AddPushButton(moduleData);

            PushButton readyButton =
                splitButton.AddPushButton(readyData);

            PushButton diagnosticsButton =
                splitButton.AddPushButton(diagnosticsData);

            if (generateButton != null)
                splitButton.CurrentButton = generateButton;

            Helpers.PushButton.ApplySettings(
                generateButton,
                "Checks worksharing freshness and ownership, creates temporary hollow fabrication geometry, validates ID/OD/wall thickness, exports a verified STEP, and rolls back the temporary Revit model. Revit 2025 or newer is required.",
                spoolIcon16,
                spoolIcon32);

            Helpers.PushButton.ApplySettings(
                moduleButton,
                "Combines multiple selected spool and support assemblies into one verified object-centred STEP. Includes supported brackets and lower clamp halves while omitting butterfly valves, Wood solids, upper KSH clamp halves, insulation, and connection helpers. Revit 2025 or newer is required.",
                moduleIcon16,
                moduleIcon32);

            Helpers.PushButton.ApplySettings(
                readyButton,
                "Temporarily isolates source pipes, fittings, and accessories successfully exported on this workstation. The active view is checked for worksharing conflicts before isolation.",
                readyIcon16,
                readyIcon32);

            Helpers.PushButton.ApplySettings(
                diagnosticsButton,
                "Developer use only. Exports selected fabrication components, direct connection context, parameters, connectors, worksharing state, and detailed source geometry into one compact JSON file. The model is not modified.",
                diagnosticsIcon16,
                diagnosticsIcon32);
        }

        private static string ResolveIconPath(
            string assemblyDirectory,
            string fileName)
        {
            return string.IsNullOrWhiteSpace(assemblyDirectory)
                ? null
                : Path.Combine(
                    assemblyDirectory,
                    "Icons",
                    fileName);
        }
    }
}
