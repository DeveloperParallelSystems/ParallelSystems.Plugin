using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.AssemblyDuplication;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Commands
{
    // Created by Jhay: internal Phase 2 command; intentionally not on the production ribbon.
    [Transaction(TransactionMode.Manual)]
    public sealed class RunAssemblyDuplicationDiagnosticCommand : IExternalCommand
    {
        private const string DialogTitle = "Assembly Independence Diagnostic";

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
                AppDialog.Warn(uiApp, DialogTitle, "Run this diagnostic from a Revit project.");
                return Result.Cancelled;
            }

            try
            {
                AssemblyInstance source = SelectOneAssembly(uiDocument, document, uiApp);
                if (source == null)
                    return Result.Cancelled;

                string targetName = FindUniqueDiagnosticName(document);
                bool confirmed = AppDialog.ConfirmDetailed(
                    uiApp,
                    DialogTitle,
                    "Run the model-only independence proof on a disposable model copy?",
                    "This command will copy one assembly's members and attempt to create " +
                    "a new assembly named '" + targetName + "'.",
                    "Source: " + source.AssemblyTypeName + Environment.NewLine +
                    "No source element, source type, source view, or source sheet will be edited. " +
                    "Any failed independence check rolls back all target artifacts.",
                    true);

                if (!confirmed)
                    return Result.Cancelled;

                AssemblyDuplicationDiagnosticResult result =
                    AssemblyDuplicationDiagnosticService.Run(
                        document,
                        source,
                        targetName);

                AppDialog.ShowDetailed(
                    uiApp,
                    DialogTitle,
                    result.Succeeded
                        ? "The independent assembly proof passed."
                        : "The proof did not pass; all diagnostic model changes were rolled back.",
                    result.Summary,
                    BuildDetails(result),
                    result.Succeeded
                        ? MessageDialogIcon.Success
                        : MessageDialogIcon.Warning);

                return Result.Succeeded;
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
                    "The diagnostic could not run.",
                    exception.Message,
                    exception.ToString(),
                    MessageDialogIcon.Error);
                return Result.Failed;
            }
        }

        private static AssemblyInstance SelectOneAssembly(
            UIDocument uiDocument,
            Document document,
            UIApplication uiApp)
        {
            ICollection<ElementId> selectedIds = uiDocument.Selection.GetElementIds();
            if (selectedIds.Count > 0)
            {
                List<AssemblyInstance> assemblies = selectedIds
                    .Select(document.GetElement)
                    .OfType<AssemblyInstance>()
                    .ToList();

                if (selectedIds.Count != 1 || assemblies.Count != 1)
                {
                    AppDialog.Warn(
                        uiApp,
                        DialogTitle,
                        "Preselect exactly one Revit Assembly Instance for this diagnostic.");
                    return null;
                }

                return assemblies[0];
            }

            Reference picked = uiDocument.Selection.PickObject(
                ObjectType.Element,
                new AssemblyInstanceSelectionFilter(),
                "Select one Revit Assembly Instance for the independence diagnostic.");

            return document.GetElement(picked.ElementId) as AssemblyInstance;
        }

        private static string FindUniqueDiagnosticName(Document document)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(AssemblyInstance))
                    .Cast<AssemblyInstance>()
                    .Select(assembly => assembly.AssemblyTypeName),
                StringComparer.OrdinalIgnoreCase);

            const string baseName = "CHW_TEST_100";
            if (!existing.Contains(baseName))
                return baseName;

            for (int suffix = 1; suffix < int.MaxValue; suffix++)
            {
                string candidate = baseName + "_" + suffix;
                if (!existing.Contains(candidate))
                    return candidate;
            }

            throw new InvalidOperationException("Unable to generate a unique diagnostic assembly name.");
        }

        private static string BuildDetails(AssemblyDuplicationDiagnosticResult result)
        {
            var details = new StringBuilder();
            AppendEvidence(details, "Source before", result.SourceBefore);
            AppendEvidence(details, "Source after", result.SourceAfter);
            AppendEvidence(details, "Target after", result.TargetAfter);
            details.AppendLine();
            details.AppendLine("Checks:");
            foreach (AssemblyDuplicationInvariant invariant in result.Invariants)
            {
                details.AppendLine(
                    (invariant.Passed ? "PASS" : "FAIL") +
                    " - " + invariant.Name + ": " + invariant.Details);
            }

            details.AppendLine();
            details.AppendLine(
                string.IsNullOrWhiteSpace(result.ReportPath)
                    ? "Report: not written"
                    : "Report: " + result.ReportPath);
            details.AppendLine();
            details.Append(result.Details);
            return details.ToString();
        }

        private static void AppendEvidence(
            StringBuilder details,
            string label,
            AssemblyEvidence evidence)
        {
            if (evidence == null)
            {
                details.AppendLine(label + ": not captured");
                return;
            }

            details.AppendLine(
                label + ": Instance " + evidence.InstanceId +
                ", Type " + evidence.TypeId +
                ", Name " + evidence.TypeName +
                ", Members " + evidence.Members.Count);
        }
    }
}
