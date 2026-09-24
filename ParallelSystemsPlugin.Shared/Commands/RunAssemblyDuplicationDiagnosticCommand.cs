using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.AssemblyDuplication;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Commands
{
    // Created by Jhay: live two-target proof of independent assembly identity.
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

                IReadOnlyList<ProposedAssemblyName> targetNames = CreateTargetNames(source);
                string target500Name = targetNames[0].ProposedName;
                string target501Name = targetNames[1].ProposedName;
                bool confirmed = AppDialog.ConfirmDetailed(
                    uiApp,
                    DialogTitle,
                    "Run the model-only independence proof on a disposable model copy?",
                    "This command will copy one assembly twice and create '" +
                    target500Name + "' and '" + target501Name + "'.",
                    "Source: " + source.AssemblyTypeName + Environment.NewLine +
                    "Each target receives one no-geometry internal identity marker with a unique type. " +
                    "No source element, source type, source view, or source sheet will be edited. " +
                    "Any failed independence check rolls back all target artifacts.",
                    true);

                if (!confirmed)
                    return Result.Cancelled;

                AssemblyDuplicationDiagnosticResult result =
                    AssemblyDuplicationDiagnosticService.Run(
                        document,
                        source,
                        target500Name,
                        target501Name,
                        Path.GetDirectoryName(typeof(App).Assembly.Location));

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

        private static IReadOnlyList<ProposedAssemblyName> CreateTargetNames(
            AssemblyInstance source)
        {
            var candidate = new AssemblyNamingCandidate(
                source.AssemblyTypeName,
                RevitApiCompatibility.GetElementIdValue(source.Id));
            ProposedAssemblyName target500 = AssemblyNamingService.Generate(
                new[] { candidate },
                500)[0];
            ProposedAssemblyName target501 = AssemblyNamingService.Generate(
                new[] { candidate },
                501)[0];
            return new[] { target500, target501 };
        }

        private static string BuildDetails(AssemblyDuplicationDiagnosticResult result)
        {
            var details = new StringBuilder();
            AppendEvidence(details, "Source before", result.SourceBefore);
            AppendEvidence(details, "Source after", result.SourceAfter);
            AppendEvidence(details, "Target 500 after", result.Target500After);
            AppendEvidence(details, "Target 501 after", result.Target501After);
            AppendMarker(details, "Marker 500", result.Target500Marker);
            AppendMarker(details, "Marker 501", result.Target501Marker);
            if (result.FamilyResolution != null)
            {
                details.AppendLine(
                    "Identity family: " + result.FamilyResolution.CategoryName +
                    ", placement " + result.FamilyResolution.PlacementType +
                    ", loaded " + result.FamilyResolution.WasLoaded +
                    ", path " + result.FamilyResolution.AssetPath);
            }

            details.AppendLine(
                "Pairwise type IDs: " +
                (result.SourceAfter ?? result.SourceBefore)?.TypeId + " / " +
                result.Target500After?.TypeId + " / " +
                result.Target501After?.TypeId);
            details.AppendLine(
                "Rename probe: " + (result.RenameProbePassed ? "PASS" : "FAIL") +
                " - " + (result.RenameProbeDetails ?? "not run"));
            details.AppendLine();
            details.AppendLine("Checks:");
            foreach (AssemblyDuplicationInvariant invariant in result.Invariants)
            {
                details.AppendLine(
                    (invariant.Passed ? "PASS" : "FAIL") +
                    " - " + invariant.Name + ": " + invariant.Details);
            }

            details.AppendLine();
            details.AppendLine("Transaction stages:");
            foreach (AssemblyTransactionStage stage in result.TransactionStages)
                details.AppendLine(stage.Status + " - " + stage.Name);

            details.AppendLine();
            details.AppendLine("Marker observations:");
            foreach (AssemblyContaminationObservation observation in result.ContaminationObservations)
                details.AppendLine(observation.Surface + ": " + observation.Result);

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
                ", Production " + evidence.ProductionMembers.Count +
                ", Internal " + evidence.InternalMembers.Count);
        }

        private static void AppendMarker(
            StringBuilder details,
            string label,
            AssemblyIdentityMarkerEvidence marker)
        {
            if (marker == null)
            {
                details.AppendLine(label + ": not captured");
                return;
            }

            details.AppendLine(
                label + ": Element " + marker.MarkerId +
                ", Symbol " + marker.SymbolId + " (" + marker.SymbolName + ")" +
                ", Level " + marker.LevelId + " (" + marker.LevelName + ")" +
                ", Offset " + marker.Offset);
        }
    }
}
