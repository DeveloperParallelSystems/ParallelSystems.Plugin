using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParallelSystemsPlugin.Fabrication;
using ParallelSystemPlugin.UI;
using System;
using System.IO;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ExportFabricationDiagnosticsCommand : IExternalCommand
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

            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument uiDoc = uiApp.ActiveUIDocument;
                Document doc = uiDoc?.Document;

                if (doc == null)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Export Fabrication Diagnostics",
                        "No active Revit project is open.");

                    return Result.Cancelled;
                }

                if (doc.IsFamilyDocument)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Export Fabrication Diagnostics",
                        "This developer diagnostic command must be run from a Revit project, not from the Family Editor.");

                    return Result.Cancelled;
                }

                FabricationDiagnosticsSelection selection =
                    FabricationDiagnosticsExporter.CollectSelection(uiDoc);

                if (selection == null ||
                    selection.SourceElementIds.Count == 0)
                {
                    return Result.Cancelled;
                }

                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "Save Fabrication Diagnostics",
                    Filter = "JSON file (*.json)|*.json",
                    DefaultExt = ".json",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName =
                        FabricationDiagnosticsExporter
                            .BuildSuggestedFileName(doc)
                };

                bool? accepted = dialog.ShowDialog();

                if (accepted != true)
                    return Result.Cancelled;

                string outputDirectory =
                    Path.GetDirectoryName(dialog.FileName);

                if (string.IsNullOrWhiteSpace(outputDirectory) ||
                    !Directory.Exists(outputDirectory))
                {
                    AppDialog.Warn(
                        uiApp,
                        "Export Fabrication Diagnostics",
                        "The selected output folder does not exist. Nothing was saved.");

                    return Result.Cancelled;
                }

                FabricationDiagnosticsExportResult result =
                    FabricationDiagnosticsExporter.Export(
                        uiApp,
                        selection,
                        dialog.FileName);

                AppDialog.Success(
                    uiApp,
                    "Export Fabrication Diagnostics",
                    "Developer diagnostics exported successfully.\n\n" +
                    "Selected components: " +
                    result.SelectedElementCount + "\n" +
                    "Connection-context components: " +
                    result.ContextElementCount + "\n" +
                    "Connector relationships: " +
                    result.ConnectionCount + "\n" +
                    (result.ContextTruncated
                        ? "Context limit reached: Yes\n"
                        : string.Empty) +
                    "\nJSON:\n" + result.FilePath);

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.ToString();

                AppDialog.Error(
                    commandData.Application,
                    "Export Fabrication Diagnostics Error",
                    "The diagnostics file could not be generated.\n\n" +
                    ex.Message);

                return Result.Failed;
            }
        }
    }
}
