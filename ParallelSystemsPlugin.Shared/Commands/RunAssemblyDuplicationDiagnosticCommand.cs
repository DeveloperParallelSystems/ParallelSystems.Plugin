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
    // Changed by Jhay: single-target destination-level proof using independent assembly identity.
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

                List<Level> levels = new FilteredElementCollector(document)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(level => level.ProjectElevation)
                    .ThenBy(level => level.Name)
                    .ToList();
                if (levels.Count == 0)
                    throw new InvalidOperationException("The project contains no destination levels.");

                List<string> levelOptions = levels
                    .Select(level => level.Name + "  |  Elevation " + level.ProjectElevation.ToString("G17"))
                    .ToList();
                int selectedLevelIndex = AppDialog.Choose(
                    uiApp,
                    DialogTitle,
                    "Choose Destination Level",
                    "The duplicate will preserve source X/Y and its vertical offset from the source reference level.",
                    levelOptions,
                    0);
                if (selectedLevelIndex < 0)
                    return Result.Cancelled;

                Level destinationLevel = levels[selectedLevelIndex];
                ProposedAssemblyName targetName = CreateTargetName(source);
                AssemblyDestinationLevelPlan plan = AssemblyDestinationLevelService.CreatePlan(
                    document,
                    source,
                    source.GetMemberIds().ToList(),
                    destinationLevel);
                bool confirmed = AppDialog.ConfirmDetailed(
                    uiApp,
                    DialogTitle,
                    "Duplicate this assembly to the selected Destination Level?",
                    "This command will create '" + targetName.ProposedName + "' on '" +
                    destinationLevel.Name + "'.",
                    "Source: " + source.AssemblyTypeName + Environment.NewLine +
                    "Source reference level: " + plan.SourceLevel.Name + Environment.NewLine +
                    "Destination level: " + destinationLevel.Name + Environment.NewLine +
                    "Vertical delta (internal feet): " + plan.DeltaZ.ToString("G17") + Environment.NewLine +
                    "Preserved assembly offset (internal feet): " + plan.SourceAssemblyOffset.ToString("G17") + Environment.NewLine +
                    "The target receives one no-geometry internal identity marker with a unique type. " +
                    "No source element, source type, source view, or source sheet will be edited. " +
                    "Any failed level, geometry, offset, or independence check rolls back all target artifacts.",
                    true);

                if (!confirmed)
                    return Result.Cancelled;

                AssemblyDuplicationDiagnosticResult result =
                    AssemblyDuplicationDiagnosticService.RunSingleToLevel(
                        document,
                        source,
                        targetName.ProposedName,
                        destinationLevel,
                        Path.GetDirectoryName(typeof(App).Assembly.Location));

                AppDialog.ShowDetailed(
                    uiApp,
                    DialogTitle,
                    result.Succeeded
                        ? "The destination-level assembly proof passed."
                        : "The destination-level proof did not pass; all model changes were rolled back.",
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

        private static ProposedAssemblyName CreateTargetName(
            AssemblyInstance source)
        {
            var candidate = new AssemblyNamingCandidate(
                source.AssemblyTypeName,
                RevitApiCompatibility.GetElementIdValue(source.Id));
            return AssemblyNamingService.Generate(
                new[] { candidate },
                500)[0];
        }

        private static string BuildDetails(AssemblyDuplicationDiagnosticResult result)
        {
            var details = new StringBuilder();
            AppendEvidence(details, "Source before", result.SourceBefore);
            AppendEvidence(details, "Source after", result.SourceAfter);
            AppendEvidence(details, "Target 500 after", result.Target500After);
            AppendMarker(details, "Marker 500", result.Target500Marker);
            if (result.FamilyResolution != null)
            {
                details.AppendLine(
                    "Identity family: " + result.FamilyResolution.CategoryName +
                    ", placement " + result.FamilyResolution.PlacementType +
                    ", loaded " + result.FamilyResolution.WasLoaded +
                    ", path " + result.FamilyResolution.AssetPath);
            }

            details.AppendLine(
                "Type IDs: " +
                (result.SourceAfter ?? result.SourceBefore)?.TypeId + " / " +
                result.Target500After?.TypeId);
            details.AppendLine();
            details.AppendLine("Destination level observations:");
            foreach (string observation in result.DestinationLevelObservations)
                details.AppendLine(observation);
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
