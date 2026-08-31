using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using ParallelSystemPlugin.UI;                 // AppDialog
using ParallelSystemsPlugin.Helpers;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ExportBomCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!App.IsUserAuthorized)
                {
                    AppDialog.Warn(
                        "Access Denied",
                        "Your account is not authorized to use this function.");

                    return Result.Cancelled;
                }

                UIApplication uiapp = commandData.Application;
                UIDocument uidoc = uiapp.ActiveUIDocument;
                Document doc = uidoc?.Document;

                if (doc == null)
                {
                    AppDialog.Warn(uiapp, "Export BOM", "No active document.");
                    return Result.Failed;
                }

                // Load config (keep your existing approach)
                ApplicationConfig config = Config.Load(); // adjust only if your helper differs

                if (config == null || config.Procurement == null)
                {
                    AppDialog.Warn(uiapp, "Export BOM", "Procurement configuration is missing.");
                    return Result.Failed;
                }

                ProcurementConfig p = config.Procurement;

                // Validate output folder (only if any report is selected)
                bool anySelected =
                    p.BomAssemblyRegister ||
                    p.BomCutList ||
                    p.BomFittingReport ||
                    p.BomLoadingReport ||
                    p.BomPipeReport ||
                    p.LabelReport ||
                    p.BomFieldMaterialReport ||
                    p.BomAccessoryReport;

                if (!anySelected)
                {
                    AppDialog.Info(uiapp, "Export BOM", "No Procurement outputs selected.");
                    return Result.Cancelled;
                }

                if (string.IsNullOrWhiteSpace(p.TargetFolder) || !Directory.Exists(p.TargetFolder))
                {
                    AppDialog.Warn(uiapp, "Export BOM",
                        "Target Folder is empty or does not exist.\n\nPlease set it in Configurations > Procurement.");
                    return Result.Failed;
                }

                // === Run selected outputs ===
                // One empty report must not kill the whole BOM export.
                // Example: Loading Report needs AssemblyInstance elements in the active view, while Pipe/Fitting reports can still be valid.
                var generatedReports = new List<string>();
                var skippedReports = new List<string>();

                if (p.BomAssemblyRegister)
                    RunBomReport("BOM-ASSEMBLY REGISTER", () => BomAssemblyRegisterReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.BomCutList)
                {
                    IList<Element> cutListPipes = CollectActiveViewPipes(doc);
                    if (cutListPipes.Count == 0)
                    {
                        skippedReports.Add("BOM-CUT LIST: no valid pipes found in the active view.");
                    }
                    else
                    {
                        RunBomReport("BOM-CUT LIST", () => BomCutListReport.Generate(doc, p), generatedReports, skippedReports);
                    }
                }

                if (p.BomFittingReport)
                    RunBomReport("BOM-FITTING REPORT", () => BomFittingReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.BomLoadingReport)
                    RunBomReport("BOM-LOADING REPORT", () => BomLoadingReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.BomPipeReport)
                    RunBomReport("BOM-PIPE REPORT", () => BomPipeReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.LabelReport)
                    RunBomReport("LABEL REPORT", () => LabelReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.BomFieldMaterialReport)
                    RunBomReport("BOM-FIELD MATERIAL", () => BomFieldMaterialReport.Generate(doc, p), generatedReports, skippedReports);

                if (p.BomAccessoryReport)
                    RunBomReport("BOM-ACCESSORY REPORT", () => BomAccessoryReport.Generate(doc, p), generatedReports, skippedReports);

                // Publish phase belongs here (NOT in Configurations.xaml.cs)
                // If user enabled ExportPdf / ExportImage -> run PublishBomCommand logic
                if (p.ExportPdf || p.ExportImage)
                {
                    var publishRes = PublishBomCommand.Run(uiapp, doc, config);
                    if (publishRes != Result.Succeeded)
                        return publishRes;
                }

                string completionMessage = BuildCompletionMessage(generatedReports, skippedReports);
                AppDialog.Info(uiapp, "Export BOM", completionMessage);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }


        private static void RunBomReport(string reportName, Action generate, List<string> generatedReports, List<string> skippedReports)
        {
            try
            {
                generate();
                generatedReports.Add(reportName);
            }
            catch (InvalidOperationException ex) when (IsNoDataReportException(ex))
            {
                skippedReports.Add(reportName + ": " + NormalizeNoDataMessage(ex.Message));
            }
        }

        private static bool IsNoDataReportException(InvalidOperationException ex)
        {
            string msg = ex.Message ?? "";
            return msg.IndexOf("No valid pipes found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("No fittings found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("No accessories found", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("No assemblies found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string NormalizeNoDataMessage(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return "no data found in the active view.";

            string clean = message.Trim();
            if (!clean.EndsWith(".", StringComparison.Ordinal))
                clean += ".";
            return char.ToLowerInvariant(clean[0]) + clean.Substring(1);
        }

        private static string BuildCompletionMessage(List<string> generatedReports, List<string> skippedReports)
        {
            var lines = new List<string>();

            if (generatedReports.Count > 0)
            {
                lines.Add("Procurement process completed.");
                lines.Add("");
                lines.Add("Generated:");
                lines.AddRange(generatedReports.Select(x => "- " + x));
            }
            else
            {
                lines.Add("No BOM report was generated from the current active view.");
            }

            if (skippedReports.Count > 0)
            {
                lines.Add("");
                lines.Add("Skipped:");
                lines.AddRange(skippedReports.Select(x => "- " + x));
            }

            return string.Join(Environment.NewLine, lines);
        }

        // Active-view pipe collector (safe: ToElements() BEFORE LINQ)
        private static IList<Element> CollectActiveViewPipes(Document doc)
        {
            if (doc == null || doc.ActiveView == null)
                return new List<Element>();

            var collector = new FilteredElementCollector(doc, doc.ActiveView.Id)
                .OfCategory(BuiltInCategory.OST_PipeCurves)
                .WhereElementIsNotElementType();

            IList<Element> elements = collector.ToElements();

            return elements
                .Where(e =>
                {
                    Parameter p = e.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH);
                    return p != null &&
                           p.StorageType == StorageType.Double &&
                           p.AsDouble() > 0;
                })
                .ToList();
        }
    }
}
