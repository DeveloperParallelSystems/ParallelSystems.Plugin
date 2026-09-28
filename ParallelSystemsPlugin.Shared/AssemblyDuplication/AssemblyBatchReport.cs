using Autodesk.Revit.DB;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: durable evidence for immutable planning and atomic batch execution.
    internal static class AssemblyBatchReport
    {
        public static string Write(
            Document document,
            AssemblyBatchResult result,
            Exception exception)
        {
            string folder = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystems",
                "AssemblyDuplication");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(
                folder,
                "AssemblyBatch-" + DateTime.UtcNow.ToString(
                    "yyyyMMdd-HHmmss-fff",
                    CultureInfo.InvariantCulture) + ".txt");

            var text = new StringBuilder();
            text.AppendLine("Parallel Systems Assembly Duplication Batch Report");
            text.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            text.AppendLine("Revit: " + document.Application.VersionNumber);
            text.AppendLine("Document: " + document.Title);
            text.AppendLine("Status: " + result.Status);
            text.AppendLine("Failed stage: " + (result.FailedStage ?? "<none>"));
            text.AppendLine("Failure: " + (result.FailureMessage ?? "<none>"));
            text.AppendLine("Group start: " + FormatStatus(result.GroupStartStatus));
            text.AppendLine("Group final: " + FormatStatus(result.GroupFinalStatus));
            text.AppendLine();

            AppendPlan(text, result.Plan);
            text.AppendLine("REVALIDATION");
            if (result.RevalidationIssues.Count == 0)
                text.AppendLine("PASS | Confirmed plan remained valid.");
            foreach (AssemblyBatchPreflightIssue issue in result.RevalidationIssues)
            {
                text.AppendLine(
                    "FAIL | " + issue.Kind + " | " +
                    (issue.SourceAssemblyId?.ToString(CultureInfo.InvariantCulture) ?? "<batch>") +
                    " | " + issue.SourceAssemblyName + " | " + issue.Message);
            }
            text.AppendLine();

            text.AppendLine("IDENTITY FAMILY");
            if (result.FamilyResolution == null)
                text.AppendLine("<not resolved>");
            else
            {
                text.AppendLine("Asset: " + result.FamilyResolution.AssetPath);
                text.AppendLine("Loaded by batch: " + result.FamilyResolution.WasLoaded);
                text.AppendLine("Family id: " + result.FamilyResolution.FamilyId);
                text.AppendLine("Category: " + result.FamilyResolution.CategoryName);
                text.AppendLine("Placement: " + result.FamilyResolution.PlacementType);
            }
            text.AppendLine();

            foreach (AssemblyBatchItemResult item in result.Items)
                AppendItem(text, item);

            text.AppendLine("BATCH SUMMARY");
            foreach (AssemblyBatchItemStatus status in Enum.GetValues(typeof(AssemblyBatchItemStatus)))
            {
                text.AppendLine(
                    status + ": " + result.Items.Count(item => item.Status == status));
            }

            if (exception != null)
            {
                text.AppendLine();
                text.AppendLine("EXCEPTION");
                text.AppendLine(exception.ToString());
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static void AppendPlan(StringBuilder text, AssemblyBatchPlan plan)
        {
            text.AppendLine("CONFIRMED PLAN");
            text.AppendLine("Starting number: " + plan.StartingNumber);
            text.AppendLine(
                "Destination: " + plan.DestinationLevelName + " | id " +
                plan.DestinationLevelId + " | elevation " +
                plan.DestinationLevelElevation.ToString("G17", CultureInfo.InvariantCulture));
            text.AppendLine(
                "Skipped complete-name conflicts: " +
                (plan.SkippedConflictingNames.Count == 0
                    ? "<none>"
                    : string.Join(", ", plan.SkippedConflictingNames)));
            foreach (AssemblyBatchPlanItem item in plan.Items)
            {
                text.AppendLine(
                    item.Order + " | " + item.SourceAssemblyId + " | " +
                    item.SourceAssemblyName + " -> " + item.TargetAssemblyName +
                    " | requested anchor " +
                    (item.RequestedAnchorNumber.HasValue
                        ? item.RequestedAnchorNumber.Value.ToString(CultureInfo.InvariantCulture)
                        : "<automatic>") +
                    " | number " + item.AssignedNumber +
                    " | skipped names " +
                    (item.SkippedConflictingNames.Count == 0
                        ? "<none>"
                        : string.Join(",", item.SkippedConflictingNames)) +
                    " | documentation " + (item.Documentation?.Summary ?? "<unavailable>"));
                if (item.Documentation != null)
                {
                    foreach (AssemblyDocumentationViewPlan view in item.Documentation.Views)
                    {
                        text.AppendLine(
                            "  VIEW | " + view.SourceViewId + " | " + view.SourceName +
                            " | " + view.Kind + " | orientation " +
                            (view.Orientation?.ToString() ?? "<not applicable>") +
                            " | template " + view.TemplateId + " | target name " +
                            (view.TargetName ?? "<Revit assembly-owned naming>"));
                        foreach (AssemblyDocumentationViewAnnotationItem annotation in
                                 view.Annotations.Items)
                        {
                            text.AppendLine(
                                "    VIEW ANNOTATION | " + annotation.ElementId + " | " +
                                annotation.RuntimeType + " | " + annotation.CategoryName +
                                " | type " + annotation.TypeId + " | owner " +
                                annotation.OwnerViewId + " | " + annotation.Kind +
                                (annotation.CopyRootId >= 0
                                    ? " | copy root " + annotation.CopyRootId
                                    : string.Empty));
                            if (annotation.Tag != null)
                            {
                                foreach (AssemblyDocumentationReferencePlan reference in
                                         annotation.Tag.References)
                                {
                                    text.AppendLine(
                                        "      TAG REFERENCE | source element " +
                                        reference.SourceElementId + " | " + reference.ReferenceType +
                                        " | stable " + reference.StableRepresentation);
                                }
                            }
                            if (annotation.ReferenceAnnotation != null)
                            {
                                text.AppendLine(
                                    "      REFERENCE ANNOTATION | curve " +
                                    annotation.ReferenceAnnotation.CurveSignature + " | segments " +
                                    annotation.ReferenceAnnotation.SegmentCount + " | value " +
                                    annotation.ReferenceAnnotation.ValueText);
                                foreach (AssemblyDocumentationReferencePlan reference in
                                         annotation.ReferenceAnnotation.References)
                                {
                                    text.AppendLine(
                                        "        REFERENCE | source element " +
                                        reference.SourceElementId + " | " + reference.ReferenceType +
                                        " | stable " + reference.StableRepresentation);
                                }
                            }
                        }
                    }
                    if (item.Documentation.Sheet != null)
                    {
                        text.AppendLine(
                            "  SHEET | " + item.Documentation.Sheet.SourceSheetId +
                            " | title block " + item.Documentation.Sheet.TitleBlockTypeDisplay +
                            " | viewports " + item.Documentation.Sheet.Viewports.Count +
                            " | schedules " + item.Documentation.Sheet.Schedules.Count +
                            " | title-block revision schedules " +
                            item.Documentation.Sheet.TitleBlockRevisionScheduleCount);
                        foreach (AssemblyDocumentationViewportPlan viewport in
                                 item.Documentation.Sheet.Viewports.Where(value => value.IsReusableLegend))
                        {
                            text.AppendLine(
                                "    LEGEND | " + viewport.SourceViewId + " | " + viewport.ViewName +
                                " | center " + viewport.Center.Signature);
                        }
                    }
                    foreach (AssemblyDocumentationCategoryEItem categoryE in item.Documentation.CategoryEItems)
                    {
                        text.AppendLine(
                            "  CATEGORY E | " + categoryE.ElementId + " | " +
                            categoryE.RuntimeType + " | " + categoryE.CategoryName +
                            " | owner " + categoryE.OwnerViewId +
                            " | " + categoryE.Disposition +
                            (categoryE.CopyRootId >= 0
                                ? " | copy root " + categoryE.CopyRootId
                                : string.Empty));
                    }
                }
            }
            text.AppendLine();
        }

        private static void AppendItem(StringBuilder text, AssemblyBatchItemResult item)
        {
            text.AppendLine(
                "ITEM " + item.PlanItem.Order + " | " +
                item.PlanItem.SourceAssemblyName + " -> " +
                item.PlanItem.TargetAssemblyName);
            text.AppendLine("Status: " + item.Status);
            text.AppendLine("Target instance: " + FormatId(item.TargetInstanceId));
            text.AppendLine("Target type: " + FormatId(item.TargetTypeId));
            text.AppendLine("Failed stage: " + (item.FailedStage ?? "<none>"));
            text.AppendLine("Failure: " + (item.FailureMessage ?? "<none>"));
            if (item.Evidence != null)
            {
                AppendLines(text, "Transactions", item.Evidence.TransactionStages.Select(stage =>
                    stage.Status + " | " + stage.Name));
                AppendLines(text, "Copy matching", item.Evidence.CopyMatchingObservations);
                AppendLines(text, "Transform alignment", item.Evidence.TransformAlignmentObservations);
                AppendLines(text, "Destination level", item.Evidence.DestinationLevelObservations);
                AppendLines(text, "Production evidence", item.Evidence.ProductionEvidenceObservations);
                AppendLines(text, "Invariants", item.Evidence.Invariants.Select(invariant =>
                    (invariant.Passed ? "PASS" : "FAIL") + " | " +
                    invariant.Name + " | " + invariant.Details));
            }
            text.AppendLine("DOCUMENTATION:");
            if (item.Documentation == null)
                text.AppendLine("  <not executed>");
            else
            {
                foreach (string observation in item.Documentation.Evidence.Observations)
                    text.AppendLine("  " + observation);
                foreach (AssemblyDuplicationInvariant invariant in item.Documentation.Evidence.Invariants)
                {
                    text.AppendLine(
                        "  " + (invariant.Passed ? "PASS" : "FAIL") + " | " +
                        invariant.Name + " | " + invariant.Details);
                }
            }
            text.AppendLine();
        }

        private static void AppendLines(
            StringBuilder text,
            string heading,
            System.Collections.Generic.IEnumerable<string> lines)
        {
            text.AppendLine(heading + ":");
            foreach (string line in lines)
                text.AppendLine("  " + line);
        }

        private static string FormatStatus(TransactionStatus? status) =>
            status.HasValue ? status.Value.ToString() : "<not started>";

        private static string FormatId(long? id) =>
            id.HasValue ? id.Value.ToString(CultureInfo.InvariantCulture) : "<none>";
    }
}
