using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.Win32;
using ParallelSystemsPlugin.Fabrication;
using System;
using System.IO;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class GenerateFabricationStepCommand : IExternalCommand
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
                "Fabrication STEP",
                "Native STEP export is available only in Revit 2025 or newer.\n\n" +
                "Use the Revit 2025 or Revit 2026 build of this add-in.");

            return Result.Cancelled;
#else
            string temporaryDirectory = null;

            try
            {
                UIApplication uiApp = commandData.Application;
                UIDocument uiDoc = uiApp.ActiveUIDocument;
                Document doc = uiDoc?.Document;

                if (doc == null)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Fabrication STEP",
                        "No active Revit project is open.");

                    return Result.Cancelled;
                }

                if (doc.IsFamilyDocument)
                {
                    AppDialog.Warn(
                        uiApp,
                        "Fabrication STEP",
                        "This command must be run from a Revit project, not from the Family Editor.");

                    return Result.Cancelled;
                }

                FabricationSelection selection =
                    FabricationStepService.CollectSelection(uiDoc);

                if (selection == null || selection.SourceElementIds.Count == 0)
                    return Result.Cancelled;

                FabricationPreflightResult preflight =
                    FabricationPreflightService.Check(
                        doc,
                        selection);

                if (!preflight.CanProceed)
                {
                    AppDialog.ShowDetailed(
                        uiApp,
                        "Fabrication STEP Preflight",
                        "Fabrication STEP cannot continue.",
                        preflight.BuildBlockingMessage(),
                        preflight.BuildDetails(),
                        MessageDialogIcon.Error);

                    return Result.Cancelled;
                }

                if (preflight.RequiresConfirmation)
                {
                    bool continueExport =
                        AppDialog.ConfirmDetailed(
                            uiApp,
                            "Fabrication STEP Preflight",
                            "Selected elements are owned by another user.",
                            preflight.BuildWarningMessage(),
                            preflight.BuildDetails(),
                            defaultNo: true);

                    if (!continueExport)
                        return Result.Cancelled;
                }

                // Generate and validate privately first. The user is not asked
                // for a destination, and no final STEP file is created, until
                // the complete fabrication generation succeeds without a
                // blocking issue.
                temporaryDirectory = CreateTemporaryDirectory();

                string temporaryStepPath = Path.Combine(
                    temporaryDirectory,
                    BuildTemporaryFileName(selection.SuggestedFileName));

                FabricationStepResult result =
                    FabricationStepService.Generate(
                        uiApp,
                        selection,
                        temporaryStepPath);

                if (!result.Succeeded)
                {
                    // The complete blocking report is shown directly to the
                    // user before any Save dialog is opened.
                    result.StepFilePath = null;

                    AppDialog.ShowDetailed(
                        uiApp,
                        "Fabrication STEP",
                        "The fabrication STEP was not generated.",
                        result.BuildUserMessage(),
                        result.BuildDetailedMessage(),
                        MessageDialogIcon.Error);

                    return Result.Failed;
                }

                if (string.IsNullOrWhiteSpace(result.StepFilePath) ||
                    !File.Exists(result.StepFilePath) ||
                    new FileInfo(result.StepFilePath).Length == 0)
                {
                    result.Issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        Message =
                            "Fabrication generation completed, but the temporary " +
                            "STEP file is missing or empty. Nothing was saved."
                    });

                    result.Succeeded = false;
                    result.StepFilePath = null;

                    AppDialog.ShowDetailed(
                        uiApp,
                        "Fabrication STEP",
                        "The fabrication STEP was not generated.",
                        result.BuildUserMessage(),
                        result.BuildDetailedMessage(),
                        MessageDialogIcon.Error);

                    return Result.Failed;
                }

                // Only now, after generation and blocking validation succeeded,
                // ask the user where the verified STEP file should be saved.
                SaveFileDialog dialog = new SaveFileDialog
                {
                    Title = "Save Fabrication STEP",
                    Filter = "STEP file (*.step)|*.step",
                    DefaultExt = ".step",
                    AddExtension = true,
                    OverwritePrompt = true,
                    FileName = selection.SuggestedFileName + ".step"
                };

                bool? accepted = dialog.ShowDialog();
                if (accepted != true)
                    return Result.Cancelled;

                string outputPath = EnsureStepExtension(dialog.FileName);
                string outputDirectory = Path.GetDirectoryName(outputPath);

                if (string.IsNullOrWhiteSpace(outputDirectory) ||
                    !Directory.Exists(outputDirectory))
                {
                    AppDialog.Warn(
                        uiApp,
                        "Fabrication STEP",
                        "The selected output folder does not exist. Nothing was saved.");

                    return Result.Cancelled;
                }

                try
                {
                    CommitVerifiedFile(
                        result.StepFilePath,
                        outputPath,
                        requireNonEmpty: true);
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        Message =
                            "The STEP geometry was generated successfully, but " +
                            "the verified file could not be saved to the selected " +
                            "destination: " + ex.Message
                    });

                    result.Succeeded = false;
                    result.StepFilePath = null;

                    AppDialog.ShowDetailed(
                        uiApp,
                        "Fabrication STEP",
                        "The fabrication STEP was not saved.",
                        result.BuildUserMessage(),
                        result.BuildDetailedMessage(),
                        MessageDialogIcon.Error);

                    return Result.Failed;
                }

                result.StepFilePath = outputPath;
                result.Succeeded = true;

                try
                {
                    FabricationProcessedRegistry.MarkProcessed(
                        doc,
                        selection.SourceElementIds);
                }
                catch (Exception ex)
                {
                    result.Issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Warning,
                        Message =
                            "The STEP file was saved successfully, but the " +
                            "local fabrication-ready status could not be " +
                            "updated: " + ex.Message
                    });
                }

                AppDialog.ShowDetailed(
                    uiApp,
                    "Fabrication STEP",
                    "Fabrication STEP generated successfully.",
                    result.BuildUserMessage(),
                    result.BuildDetailedMessage(),
                    MessageDialogIcon.Success);

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
                    "Fabrication STEP Error",
                    "An unexpected error occurred.\n\n" + ex.Message);

                return Result.Failed;
            }
            finally
            {
                TryDeleteDirectory(temporaryDirectory);
            }
#endif
        }

#if REVIT2025_OR_GREATER
        private static string CreateTemporaryDirectory()
        {
            string directory = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystems",
                "FabricationStep",
                Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string BuildTemporaryFileName(string suggestedFileName)
        {
            string fileName = string.IsNullOrWhiteSpace(suggestedFileName)
                ? "Fabrication-Spool"
                : suggestedFileName.Trim();

            foreach (char invalidCharacter in Path.GetInvalidFileNameChars())
                fileName = fileName.Replace(invalidCharacter, '_');

            fileName = Path.GetFileNameWithoutExtension(fileName);

            if (string.IsNullOrWhiteSpace(fileName))
                fileName = "Fabrication-Spool";

            return fileName + ".step";
        }

        private static string EnsureStepExtension(string path)
        {
            if (string.Equals(
                    Path.GetExtension(path),
                    ".step",
                    StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            return Path.ChangeExtension(path, ".step");
        }

        private static void CommitVerifiedFile(
            string sourcePath,
            string destinationPath,
            bool requireNonEmpty)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "The verified temporary file does not exist.",
                    sourcePath);
            }

            if (requireNonEmpty && new FileInfo(sourcePath).Length == 0)
            {
                throw new InvalidDataException(
                    "The verified temporary file is empty.");
            }

            string destinationDirectory =
                Path.GetDirectoryName(destinationPath);

            if (string.IsNullOrWhiteSpace(destinationDirectory) ||
                !Directory.Exists(destinationDirectory))
            {
                throw new DirectoryNotFoundException(
                    "The selected destination folder does not exist.");
            }

            string pendingPath = Path.Combine(
                destinationDirectory,
                "." + Path.GetFileName(destinationPath) + "." +
                Guid.NewGuid().ToString("N") + ".pending");

            string backupPath = pendingPath + ".backup";

            try
            {
                File.Copy(sourcePath, pendingPath, true);

                if (!File.Exists(pendingPath) ||
                    (requireNonEmpty && new FileInfo(pendingPath).Length == 0))
                {
                    throw new IOException(
                        "The verified file could not be staged in the selected folder.");
                }

                if (File.Exists(destinationPath))
                {
                    File.Replace(
                        pendingPath,
                        destinationPath,
                        backupPath,
                        true);
                }
                else
                {
                    File.Move(pendingPath, destinationPath);
                }

                if (!File.Exists(destinationPath) ||
                    (requireNonEmpty &&
                     new FileInfo(destinationPath).Length == 0))
                {
                    throw new IOException(
                        "The saved file is missing or empty after the save operation.");
                }
            }
            finally
            {
                TryDeleteFile(pendingPath);
                TryDeleteFile(backupPath);
            }
        }

        private static void TryDeleteFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Cleanup failure must not replace the actual command result.
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Cleanup failure must not replace the actual command result.
            }
        }
#endif
    }
}
