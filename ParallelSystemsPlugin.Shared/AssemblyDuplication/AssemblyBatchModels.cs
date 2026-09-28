using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: immutable confirmed multi-assembly duplication plan.
    internal sealed class AssemblyBatchPlan
    {
        public AssemblyBatchPlan(
            long startingNumber,
            long destinationLevelId,
            string destinationLevelName,
            double destinationLevelElevation,
            IEnumerable<AssemblyBatchPlanItem> items,
            IEnumerable<string> skippedConflictingNames)
        {
            StartingNumber = startingNumber;
            DestinationLevelId = destinationLevelId;
            DestinationLevelName = destinationLevelName;
            DestinationLevelElevation = destinationLevelElevation;
            Items = ReadOnly(items);
            SkippedConflictingNames = ReadOnly(skippedConflictingNames);
        }

        public long StartingNumber { get; }
        public long DestinationLevelId { get; }
        public string DestinationLevelName { get; }
        public double DestinationLevelElevation { get; }
        public IReadOnlyList<AssemblyBatchPlanItem> Items { get; }
        public IReadOnlyList<string> SkippedConflictingNames { get; }

        private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
            new ReadOnlyCollection<T>((values ?? Enumerable.Empty<T>()).ToList());
    }

    // Created by Jhay: one immutable confirmed source-to-target assignment.
    internal sealed class AssemblyBatchPlanItem
    {
        public AssemblyBatchPlanItem(
            int order,
            long sourceAssemblyId,
            string sourceAssemblyName,
            AssemblyNameParts sourceNameParts,
            long? requestedAnchorNumber,
            long assignedNumber,
            string targetAssemblyName,
            IEnumerable<string> skippedConflictingNames,
            IEnumerable<long> sourceProductionMemberIds,
            AssemblyDestinationPlanSnapshot destination,
            AssemblyDocumentationPlan documentation)
        {
            Order = order;
            SourceAssemblyId = sourceAssemblyId;
            SourceAssemblyName = sourceAssemblyName;
            SourceNameParts = sourceNameParts;
            RequestedAnchorNumber = requestedAnchorNumber;
            AssignedNumber = assignedNumber;
            TargetAssemblyName = targetAssemblyName;
            SkippedConflictingNames = new ReadOnlyCollection<string>(
                (skippedConflictingNames ?? Enumerable.Empty<string>()).ToList());
            SourceProductionMemberIds = new ReadOnlyCollection<long>(
                AssemblyMemberSetComparer.Canonicalize(sourceProductionMemberIds).ToList());
            Destination = destination;
            Documentation = documentation;
        }

        public int Order { get; }
        public long SourceAssemblyId { get; }
        public string SourceAssemblyName { get; }
        public AssemblyNameParts SourceNameParts { get; }
        public long? RequestedAnchorNumber { get; }
        public bool IsManualAnchor => RequestedAnchorNumber.HasValue;
        public long AssignedNumber { get; }
        public string TargetAssemblyName { get; }
        public IReadOnlyList<string> SkippedConflictingNames { get; }
        public IReadOnlyList<long> SourceProductionMemberIds { get; }
        public AssemblyDestinationPlanSnapshot Destination { get; }
        public AssemblyDocumentationPlan Documentation { get; }
    }

    // Created by Jhay: scalar destination evidence retained by the confirmed plan.
    internal sealed class AssemblyDestinationPlanSnapshot
    {
        public AssemblyDestinationPlanSnapshot(
            long sourceLevelId,
            string sourceLevelName,
            double sourceLevelElevation,
            long destinationLevelId,
            string destinationLevelName,
            double destinationLevelElevation,
            double sourceAssemblyOffset,
            double deltaZ,
            TransformSnapshot targetTransform)
        {
            SourceLevelId = sourceLevelId;
            SourceLevelName = sourceLevelName;
            SourceLevelElevation = sourceLevelElevation;
            DestinationLevelId = destinationLevelId;
            DestinationLevelName = destinationLevelName;
            DestinationLevelElevation = destinationLevelElevation;
            SourceAssemblyOffset = sourceAssemblyOffset;
            DeltaZ = deltaZ;
            TargetTransform = targetTransform;
        }

        public long SourceLevelId { get; }
        public string SourceLevelName { get; }
        public double SourceLevelElevation { get; }
        public long DestinationLevelId { get; }
        public string DestinationLevelName { get; }
        public double DestinationLevelElevation { get; }
        public double SourceAssemblyOffset { get; }
        public double DeltaZ { get; }
        public TransformSnapshot TargetTransform { get; }
    }

    // Created by Jhay: immutable scalar representation of a Revit transform.
    internal sealed class TransformSnapshot
    {
        private readonly double[] values;

        private TransformSnapshot(IEnumerable<double> values)
        {
            this.values = values.ToArray();
        }

        public static TransformSnapshot Capture(Transform transform)
        {
            if (transform == null)
                throw new ArgumentNullException(nameof(transform));

            return new TransformSnapshot(Coordinates(transform));
        }

        public bool Matches(Transform transform, double tolerance)
        {
            if (transform == null)
                return false;

            return values.Zip(Coordinates(transform), (left, right) => Math.Abs(left - right))
                .All(difference => difference <= tolerance);
        }

        private static IEnumerable<double> Coordinates(Transform transform)
        {
            XYZ[] vectors =
            {
                transform.Origin,
                transform.BasisX,
                transform.BasisY,
                transform.BasisZ
            };
            foreach (XYZ vector in vectors)
            {
                yield return vector.X;
                yield return vector.Y;
                yield return vector.Z;
            }
        }
    }

    internal enum AssemblyBatchPreflightIssueKind
    {
        InvalidSelection,
        DuplicateSource,
        InvalidSourceName,
        NumberExhaustion,
        TargetNameConflict,
        UnavailableSource,
        EmptyProductionMembers,
        InvalidDestinationLevel,
        InvalidDestinationPlan,
        InvalidDocumentationPlan,
        DocumentationNameConflict,
        UnsupportedSheetAnnotations,
        IdentityFamilyUnavailable,
        StaleConfirmedPlan
    }

    // Created by Jhay: one aggregated read-only preflight or revalidation issue.
    internal sealed class AssemblyBatchPreflightIssue
    {
        public AssemblyBatchPreflightIssue(
            AssemblyBatchPreflightIssueKind kind,
            long? sourceAssemblyId,
            string sourceAssemblyName,
            string message)
        {
            Kind = kind;
            SourceAssemblyId = sourceAssemblyId;
            SourceAssemblyName = sourceAssemblyName;
            Message = message;
        }

        public AssemblyBatchPreflightIssueKind Kind { get; }
        public long? SourceAssemblyId { get; }
        public string SourceAssemblyName { get; }
        public string Message { get; }
    }

    internal sealed class AssemblyBatchPreflightResult
    {
        public AssemblyBatchPreflightResult(
            AssemblyBatchPlan plan,
            IEnumerable<AssemblyBatchPreflightIssue> issues,
            IEnumerable<AssemblyBatchPreviewItem> previewItems)
        {
            Plan = plan;
            Issues = new ReadOnlyCollection<AssemblyBatchPreflightIssue>(issues.ToList());
            PreviewItems = new ReadOnlyCollection<AssemblyBatchPreviewItem>(
                (previewItems ?? Enumerable.Empty<AssemblyBatchPreviewItem>()).ToList());
        }

        public bool IsValid => Plan != null && Issues.Count == 0;
        public AssemblyBatchPlan Plan { get; }
        public IReadOnlyList<AssemblyBatchPreflightIssue> Issues { get; }
        public IReadOnlyList<AssemblyBatchPreviewItem> PreviewItems { get; }
    }

    // Changed by Jhay: preview evidence remains available even when the immutable plan is invalid.
    internal sealed class AssemblyBatchPreviewItem
    {
        public int Order { get; set; }
        public long SourceAssemblyId { get; set; }
        public string SourceAssemblyName { get; set; }
        public string SourceLevelName { get; set; }
        public long? RequestedAnchorNumber { get; set; }
        public long? AssignedNumber { get; set; }
        public string TargetAssemblyName { get; set; }
        public string DocumentationSummary { get; set; }
        public string Status { get; set; }
        public IReadOnlyList<string> SkippedConflictingNames { get; set; }
        public IReadOnlyList<string> Issues { get; set; }
    }

    internal sealed class AssemblyBatchExecutionItem
    {
        public AssemblyBatchExecutionItem(
            AssemblyBatchPlanItem confirmedItem,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionMemberIds,
            AssemblyDestinationLevelPlan destinationPlan)
        {
            ConfirmedItem = confirmedItem;
            Source = source;
            SourceProductionMemberIds = sourceProductionMemberIds;
            DestinationPlan = destinationPlan;
        }

        public AssemblyBatchPlanItem ConfirmedItem { get; }
        public AssemblyInstance Source { get; }
        public IReadOnlyList<ElementId> SourceProductionMemberIds { get; }
        public AssemblyDestinationLevelPlan DestinationPlan { get; }
    }

    internal sealed class AssemblyBatchRevalidationResult
    {
        public AssemblyBatchRevalidationResult(
            IEnumerable<AssemblyBatchExecutionItem> executionItems,
            IEnumerable<AssemblyBatchPreflightIssue> issues)
        {
            ExecutionItems = new ReadOnlyCollection<AssemblyBatchExecutionItem>(
                executionItems.ToList());
            Issues = new ReadOnlyCollection<AssemblyBatchPreflightIssue>(issues.ToList());
        }

        public bool IsValid => Issues.Count == 0;
        public IReadOnlyList<AssemblyBatchExecutionItem> ExecutionItems { get; }
        public IReadOnlyList<AssemblyBatchPreflightIssue> Issues { get; }
    }

    // Created by Jhay: caller-supplied inputs for one locked engine execution.
    internal sealed class AssemblyDuplicationEngineRequest
    {
        public AssemblyDuplicationEngineRequest(
            Document document,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionMemberIds,
            string targetName,
            AssemblyDestinationLevelPlan destinationPlan,
            FamilySymbol identityMarkerBaseSymbol,
            string stagePrefix)
        {
            Document = document;
            Source = source;
            SourceProductionMemberIds = sourceProductionMemberIds;
            TargetName = targetName;
            DestinationPlan = destinationPlan;
            IdentityMarkerBaseSymbol = identityMarkerBaseSymbol;
            StagePrefix = stagePrefix;
        }

        public Document Document { get; }
        public AssemblyInstance Source { get; }
        public IReadOnlyList<ElementId> SourceProductionMemberIds { get; }
        public string TargetName { get; }
        public AssemblyDestinationLevelPlan DestinationPlan { get; }
        public FamilySymbol IdentityMarkerBaseSymbol { get; }
        public string StagePrefix { get; }
    }

    // Created by Jhay: engine evidence shared by diagnostics and production reporting.
    internal sealed class AssemblyDuplicationExecutionEvidence
    {
        public IList<AssemblyTransactionStage> TransactionStages { get; } =
            new List<AssemblyTransactionStage>();
        public IList<string> CopyMatchingObservations { get; } = new List<string>();
        public IList<string> TransformAlignmentObservations { get; } = new List<string>();
        public IList<string> DestinationLevelObservations { get; } = new List<string>();
        public IList<string> ProductionEvidenceObservations { get; } = new List<string>();
        public IList<AssemblyDuplicationInvariant> Invariants { get; } =
            new List<AssemblyDuplicationInvariant>();
    }

    internal sealed class AssemblyDuplicationEngineResult
    {
        public AssemblyInstance TargetAssembly { get; set; }
        public IReadOnlyList<ElementId> CopiedProductionMemberIds { get; set; }
        // Changed by Jhay: expose the matcher-proven pairing for documentation
        // reference remapping without rerunning or changing physical matching.
        public IReadOnlyDictionary<long, long> SourceToTargetProductionMemberIds { get; set; }
        public AssemblyIdentityMarkerEvidence Marker { get; set; }
        public AssemblyEvidence SourceBefore { get; set; }
        public AssemblyEvidence SourceAfter { get; set; }
        public AssemblyEvidence TargetAfter { get; set; }
        public AssemblyDuplicationExecutionEvidence Evidence { get; set; }
    }

    internal sealed class AssemblyDuplicationEngineException : InvalidOperationException
    {
        public AssemblyDuplicationEngineException(
            long sourceAssemblyId,
            string sourceAssemblyName,
            string targetName,
            string stage,
            AssemblyDuplicationExecutionEvidence evidence,
            Exception innerException)
            : base(innerException?.Message, innerException)
        {
            SourceAssemblyId = sourceAssemblyId;
            SourceAssemblyName = sourceAssemblyName;
            TargetName = targetName;
            Stage = stage;
            Evidence = evidence;
        }

        public long SourceAssemblyId { get; }
        public string SourceAssemblyName { get; }
        public string TargetName { get; }
        public string Stage { get; }
        public AssemblyDuplicationExecutionEvidence Evidence { get; }
    }

    internal enum AssemblyBatchStatus
    {
        Succeeded,
        PreflightRejected,
        StalePlan,
        RolledBack
    }

    internal enum AssemblyBatchItemStatus
    {
        Planned,
        Succeeded,
        Failed,
        RolledBack,
        NotStarted
    }

    internal sealed class AssemblyBatchItemResult
    {
        public AssemblyBatchPlanItem PlanItem { get; set; }
        public AssemblyBatchItemStatus Status { get; set; }
        public long? TargetInstanceId { get; set; }
        public long? TargetTypeId { get; set; }
        public string FailedStage { get; set; }
        public string FailureMessage { get; set; }
        public AssemblyDuplicationExecutionEvidence Evidence { get; set; }
        public AssemblyDocumentationResult Documentation { get; set; }
    }

    internal sealed class AssemblyBatchResult
    {
        public AssemblyBatchStatus Status { get; set; }
        public AssemblyBatchPlan Plan { get; set; }
        // Changed by Jhay: retain pre-transaction and transaction evidence for batch reporting.
        public IList<AssemblyBatchPreflightIssue> RevalidationIssues { get; } =
            new List<AssemblyBatchPreflightIssue>();
        public IList<AssemblyBatchItemResult> Items { get; } =
            new List<AssemblyBatchItemResult>();
        public AssemblyIdentityFamilyResolution FamilyResolution { get; set; }
        public TransactionStatus? GroupStartStatus { get; set; }
        public TransactionStatus? GroupFinalStatus { get; set; }
        public string FailedStage { get; set; }
        public string FailureMessage { get; set; }
        public string ReportPath { get; set; }
    }
}
