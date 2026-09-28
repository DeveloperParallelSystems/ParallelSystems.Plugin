using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Changed by Jhay: immutable result of complete-name conflict planning.
    public sealed class AssemblyBatchNumberingResult
    {
        public AssemblyBatchNumberingResult(
            AssemblyBatchNumberingPlan plan,
            IEnumerable<AssemblyBatchNumberingIssue> issues)
        {
            Plan = plan;
            Issues = ReadOnly(issues);
        }

        public bool IsValid => Plan != null && Issues.Count == 0;
        public AssemblyBatchNumberingPlan Plan { get; }
        public IReadOnlyList<AssemblyBatchNumberingIssue> Issues { get; }

        private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
            new ReadOnlyCollection<T>((values ?? Enumerable.Empty<T>()).ToList());
    }

    // Created by Jhay: complete deterministic assignment before Revit mutation.
    public sealed class AssemblyBatchNumberingPlan
    {
        public AssemblyBatchNumberingPlan(
            long startingNumber,
            IEnumerable<AssemblyBatchNumberingAssignment> assignments,
            IEnumerable<string> skippedConflictingNames)
        {
            StartingNumber = startingNumber;
            Assignments = new ReadOnlyCollection<AssemblyBatchNumberingAssignment>(
                assignments.ToList());
            SkippedConflictingNames = new ReadOnlyCollection<string>(
                skippedConflictingNames.ToList());
        }

        public long StartingNumber { get; }
        public IReadOnlyList<AssemblyBatchNumberingAssignment> Assignments { get; }
        public IReadOnlyList<string> SkippedConflictingNames { get; }
    }

    // Changed by Jhay: one final assignment with optional manual-anchor provenance.
    public sealed class AssemblyBatchNumberingAssignment
    {
        public AssemblyBatchNumberingAssignment(
            ProposedAssemblyName proposal,
            long? requestedAnchorNumber,
            IEnumerable<string> skippedConflictingNames)
        {
            Proposal = proposal;
            RequestedAnchorNumber = requestedAnchorNumber;
            SkippedConflictingNames = new ReadOnlyCollection<string>(
                skippedConflictingNames.ToList());
        }

        public ProposedAssemblyName Proposal { get; }
        public long? RequestedAnchorNumber { get; }
        public bool IsManualAnchor => RequestedAnchorNumber.HasValue;
        public IReadOnlyList<string> SkippedConflictingNames { get; }
    }

    // Created by Jhay: pre-mutation numbering error tied to a source where possible.
    public sealed class AssemblyBatchNumberingIssue
    {
        public AssemblyBatchNumberingIssue(
            long? stableElementId,
            string sourceName,
            string message)
        {
            StableElementId = stableElementId;
            SourceName = sourceName;
            Message = message;
        }

        public long? StableElementId { get; }
        public string SourceName { get; }
        public string Message { get; }
    }
}
