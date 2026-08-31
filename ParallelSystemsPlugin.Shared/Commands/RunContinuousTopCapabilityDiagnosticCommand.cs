using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.Fabrication;
using System;
using System.Globalization;
using System.IO;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class RunContinuousTopCapabilityDiagnosticCommand :
        IExternalCommand
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

#if !REVIT2025_OR_GREATER
            AppDialog.Warn(
                "Continuous-Top Capability Test",
                "This diagnostic requires Revit 2025 or newer.");

            return Result.Cancelled;
#else
            string temporaryDirectory = null;

            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument uiDoc = uiApp.ActiveUIDocument;
                Document document = uiDoc?.Document;

                if (document == null)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Continuous-Top Capability Test",
                        "No active Revit project is open.");

                    return Result.Cancelled;
                }

                if (document.IsFamilyDocument)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Continuous-Top Capability Test",
                        "Run this diagnostic from a Revit project, not from the Family Editor.");

                    return Result.Cancelled;
                }

                FabricationSelection selection =
                    FabricationStepService.CollectSelection(uiDoc);

                if (selection == null)
                    return Result.Cancelled;

                string selectionError;
                if (!FabricationStepService
                        .ValidateContinuousTopDiagnosticSelection(
                            document,
                            selection,
                            out selectionError))
                {
                    AppDialog.Warn(
                        uiApp,
                        "Continuous-Top Capability Test",
                        selectionError);

                    return Result.Cancelled;
                }

                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "Save Continuous-Top Capability Report",
                    Filter = "Text report (*.txt)|*.txt",
                    DefaultExt = ".txt",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName =
                        "ContinuousTopCapability-" +
                        DateTime.Now.ToString(
                            "yyyyMMdd-HHmmss",
                            CultureInfo.InvariantCulture) +
                        ".txt"
                };

                bool? accepted = dialog.ShowDialog();
                if (accepted != true)
                    return Result.Cancelled;

                string reportPath = dialog.FileName;
                temporaryDirectory = Path.Combine(
                    Path.GetTempPath(),
                    "ParallelSystems",
                    "ContinuousTopDiagnostic",
                    Guid.NewGuid().ToString("N"));

                Directory.CreateDirectory(temporaryDirectory);

                string temporaryStepPath = Path.Combine(
                    temporaryDirectory,
                    "diagnostic-do-not-use.step");

                FabricationStepResult generationResult;

                using (ContinuousTopCapabilityDiagnosticSession session =
                       FabricationStepService
                           .BeginContinuousTopCapabilityDiagnostic(
                               uiApp,
                               document,
                               selection,
                               reportPath))
                {
                    generationResult = FabricationStepService.Generate(
                        uiApp,
                        selection,
                        temporaryStepPath);

                    if (!session.Completed)
                    {
                        session.CompleteFromGenerationFailure(
                            generationResult);
                    }

                    AppDialog.ShowDetailed(
                        uiApp,
                        "Continuous-Top Capability Test",
                        "The isolated Revit BRep tests are complete.",
                        session.Summary,
                        "Decision: " + session.Decision +
                        "\n\nReport:\n" + session.ReportPath +
                        "\n\nUpload this text report for review. " +
                        "Do not judge the result from the production STEP error dialog; " +
                        "the diagnostic intentionally stops STEP generation.",
                        session.Decision != null &&
                        session.Decision.StartsWith(
                            "CONTINUE_",
                            StringComparison.Ordinal)
                            ? MessageDialogIcon.Success
                            : MessageDialogIcon.Warning);
                }

                return Result.Succeeded;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                message = ex.Message;

                AppDialog.ShowDetailed(
                    commandData.Application,
                    "Continuous-Top Capability Test",
                    "The capability diagnostic failed.",
                    ex.Message,
                    ex.ToString(),
                    MessageDialogIcon.Error);

                return Result.Failed;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(temporaryDirectory))
                {
                    try
                    {
                        if (Directory.Exists(temporaryDirectory))
                            Directory.Delete(temporaryDirectory, true);
                    }
                    catch
                    {
                        // Diagnostic cleanup must never hide the report.
                    }
                }
            }
#endif
        }
    }
}
