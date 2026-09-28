using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.AssemblyDuplication;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Commands
{
    // Created by Jhay: production multi-assembly selection, preview, and atomic execution workflow.
    [Transaction(TransactionMode.Manual)]
    public sealed class DuplicateAssembliesCommand : IExternalCommand
    {
        private const string DialogTitle = "Duplicate Assemblies";

        public Result Execute(
            ExternalCommandData commandData,
            ref string message,
            ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn("Access Denied", "Your account is not authorized to use this function.");
                return Result.Cancelled;
            }

            UIApplication uiApp = commandData.Application;
            UIDocument uiDocument = uiApp?.ActiveUIDocument;
            Document document = uiDocument?.Document;
            if (document == null)
            {
                AppDialog.Warn(uiApp, DialogTitle, "No active Revit project is open.");
                return Result.Cancelled;
            }
            if (document.IsFamilyDocument)
            {
                AppDialog.Warn(uiApp, DialogTitle, "Run this command from a Revit project.");
                return Result.Cancelled;
            }

            try
            {
                IReadOnlyList<ElementId> sourceIds = SelectAssemblies(uiDocument, document, uiApp);
                if (sourceIds == null || sourceIds.Count == 0)
                    return Result.Cancelled;

                List<DuplicateAssemblyLevelOption> levels = new FilteredElementCollector(document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => level.ProjectElevation)
                    .ThenBy(level => level.Name)
                    .Select(level => new DuplicateAssemblyLevelOption
                    {
                        LevelId = level.Id,
                        DisplayName = level.Name + "  |  Elevation " +
                            level.ProjectElevation.ToString("G17", CultureInfo.InvariantCulture)
                    })
                    .ToList();
                if (levels.Count == 0)
                    throw new InvalidOperationException("The project contains no destination levels.");

                string assemblyDirectory = Path.GetDirectoryName(typeof(App).Assembly.Location);
                var dialog = new DuplicateAssembliesDialog(
                    uiApp.MainWindowHandle,
                    levels,
                    (destinationLevelId, startingNumber, requestedAnchors) =>
                        AssemblyBatchPreflightService.CreatePlan(
                            document,
                            sourceIds,
                            destinationLevelId,
                            startingNumber,
                            requestedAnchors,
                            assemblyDirectory));
                bool confirmed = dialog.ShowDialog() == true;
                AssemblyBatchPlan confirmedPlan = dialog.ConfirmedPlan;
                if (!confirmed || confirmedPlan == null)
                    return Result.Cancelled;

                AssemblyBatchResult result = AssemblyBatchDuplicationService.Execute(
                    document,
                    confirmedPlan,
                    assemblyDirectory);
                ShowResult(uiApp, result);
                return result.Status == AssemblyBatchStatus.Succeeded
                    ? Result.Succeeded
                    : Result.Failed;
            }
            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (Exception exception)
            {
                message = exception.Message;
                AppDialog.ShowDetailed(
                    uiApp,
                    DialogTitle,
                    "Assembly duplication could not run.",
                    exception.Message,
                    exception.ToString(),
                    MessageDialogIcon.Error);
                return Result.Failed;
            }
        }

        private static IReadOnlyList<ElementId> SelectAssemblies(
            UIDocument uiDocument,
            Document document,
            UIApplication uiApp)
        {
            ICollection<ElementId> selected = uiDocument.Selection.GetElementIds();
            if (selected.Count > 0)
            {
                // Changed by Jhay: resolve both directly selected assemblies and selected assembly-owned members.
                IReadOnlyList<ElementId> preselectedAssemblies = DistinctIds(
                    selected.Select(id => ResolveOwningAssemblyId(document, id))
                        .Where(id => id != null));
                if (preselectedAssemblies.Count > 0)
                    return preselectedAssemblies;

                AppDialog.Warn(
                    uiApp,
                    DialogTitle,
                    "No assemblies found in the current selection.");
                return Array.Empty<ElementId>();
            }

            IList<Reference> picked = uiDocument.Selection.PickObjects(
                ObjectType.Element,
                new AssemblyInstanceSelectionFilter(),
                "Select one or more Revit Assembly Instances, then click Finish.");
            IReadOnlyList<ElementId> pickedAssemblies = DistinctIds(
                picked.Select(reference => reference.ElementId));
            if (pickedAssemblies.Count == 0)
            {
                AppDialog.Warn(
                    uiApp,
                    DialogTitle,
                    "No assemblies found in the current selection.");
            }
            return pickedAssemblies;
        }

        // Changed by Jhay: ordinary selected components contribute only their valid owning assembly.
        private static ElementId ResolveOwningAssemblyId(Document document, ElementId selectedId)
        {
            Element element = document.GetElement(selectedId);
            if (element is AssemblyInstance assembly && assembly.IsValidObject)
                return assembly.Id;
            if (element == null ||
                Compatibility.RevitApiCompatibility.IsInvalidElementId(element.AssemblyInstanceId))
                return null;

            var owner = document.GetElement(element.AssemblyInstanceId) as AssemblyInstance;
            return owner != null && owner.IsValidObject ? owner.Id : null;
        }

        private static IReadOnlyList<ElementId> DistinctIds(IEnumerable<ElementId> ids)
        {
            return ids
                .GroupBy(id => Compatibility.RevitApiCompatibility.GetElementIdValue(id))
                .Select(group => group.First())
                .OrderBy(id => Compatibility.RevitApiCompatibility.GetElementIdValue(id))
                .ToList()
                .AsReadOnly();
        }

        private static void ShowResult(UIApplication uiApp, AssemblyBatchResult result)
        {
            bool succeeded = result.Status == AssemblyBatchStatus.Succeeded;
            string skipped = result.Plan.SkippedConflictingNames.Count == 0
                ? "none"
                : string.Join(", ", result.Plan.SkippedConflictingNames);
            string assignments = string.Join(
                ", ",
                result.Plan.Items.Select(item => item.AssignedNumber.ToString(CultureInfo.InvariantCulture)));
            string summary = succeeded
                ? result.Plan.Items.Count + " assemblies duplicated to " +
                  result.Plan.DestinationLevelName + ". Assigned numbers: " + assignments +
                  ". Skipped complete-name conflicts: " + skipped +
                  ". All strict validations passed."
                : "Zero assemblies were committed. Status: " + result.Status +
                  ". Failed stage: " + (result.FailedStage ?? "unknown") + ".";

            AppDialog.ShowDetailed(
                uiApp,
                DialogTitle,
                succeeded
                    ? "Batch assembly duplication completed."
                    : "Batch assembly duplication did not commit.",
                summary,
                BuildDetails(result),
                succeeded ? MessageDialogIcon.Success : MessageDialogIcon.Warning);
        }

        private static string BuildDetails(AssemblyBatchResult result)
        {
            var text = new StringBuilder();
            text.AppendLine("Status: " + result.Status);
            text.AppendLine("Destination: " + result.Plan.DestinationLevelName);
            text.AppendLine("Failed stage: " + (result.FailedStage ?? "<none>"));
            text.AppendLine("Failure: " + (result.FailureMessage ?? "<none>"));
            text.AppendLine();
            foreach (AssemblyBatchItemResult item in result.Items)
            {
                text.AppendLine(
                    item.PlanItem.SourceAssemblyName + " -> " +
                    item.PlanItem.TargetAssemblyName + " | " + item.Status +
                    (string.IsNullOrWhiteSpace(item.FailedStage)
                        ? string.Empty
                        : " | " + item.FailedStage));
            }
            if (result.RevalidationIssues.Count > 0)
            {
                text.AppendLine();
                text.AppendLine("Revalidation issues:");
                foreach (AssemblyBatchPreflightIssue issue in result.RevalidationIssues)
                    text.AppendLine("• " + issue.Message);
            }
            text.AppendLine();
            text.AppendLine(string.IsNullOrWhiteSpace(result.ReportPath)
                ? "Report: not written"
                : "Report: " + result.ReportPath);
            return text.ToString();
        }
    }
}
