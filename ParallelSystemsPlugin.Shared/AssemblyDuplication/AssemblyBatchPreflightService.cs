using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: read-only batch planning and immutable confirmed-plan revalidation.
    internal static class AssemblyBatchPreflightService
    {
        private const double ScalarTolerance = 1e-6;

        public static AssemblyBatchPreflightResult CreatePlan(
            Document document,
            IReadOnlyCollection<ElementId> selectedIds,
            ElementId destinationLevelId,
            long startingNumber,
            IReadOnlyDictionary<long, long> requestedAnchors,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (selectedIds == null)
                throw new ArgumentNullException(nameof(selectedIds));
            if (requestedAnchors == null)
                throw new ArgumentNullException(nameof(requestedAnchors));

            var issues = new List<AssemblyBatchPreflightIssue>();
            List<AssemblyInstance> sources = OrderSources(
                ResolveSources(document, selectedIds, issues));
            Level destinationLevel = ResolveDestinationLevel(
                document,
                destinationLevelId,
                issues);
            ExistingAssemblyIndex existing = CaptureExistingAssemblies(document);

            var candidates = sources
                .Select(source => new AssemblyNamingCandidate(
                    source.AssemblyTypeName,
                    Id(source.Id)))
                .ToList();
            AssemblyBatchNumberingResult numbering = AssemblyBatchNumberingService.CreatePlan(
                candidates,
                startingNumber,
                existing.Names,
                requestedAnchors);
            foreach (AssemblyBatchNumberingIssue issue in numbering.Issues)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    issue.Message.IndexOf("Int64", StringComparison.OrdinalIgnoreCase) >= 0
                        ? AssemblyBatchPreflightIssueKind.NumberExhaustion
                        : AssemblyBatchPreflightIssueKind.InvalidSourceName,
                    issue.StableElementId,
                    issue.SourceName,
                    issue.Message));
            }

            AssemblyIdentityFamilyAvailability familyAvailability = null;
            try
            {
                familyAvailability = AssemblyIdentityFamilyService.InspectAvailability(
                    document,
                    assemblyDirectory);
                if (!familyAvailability.IsAvailable)
                {
                    issues.Add(new AssemblyBatchPreflightIssue(
                        AssemblyBatchPreflightIssueKind.IdentityFamilyUnavailable,
                        null,
                        string.Empty,
                        familyAvailability.Issue));
                }
            }
            catch (Exception exception)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.IdentityFamilyUnavailable,
                    null,
                    string.Empty,
                    exception.Message));
            }

            var livePlans = new Dictionary<long, SourcePlan>();
            var assignmentBySource = numbering.Plan == null
                ? new Dictionary<long, AssemblyBatchNumberingAssignment>()
                : numbering.Plan.Assignments.ToDictionary(item => item.Proposal.StableElementId);
            if (destinationLevel != null)
            {
                foreach (AssemblyInstance source in sources)
                {
                    long sourceId = Id(source.Id);
                    List<ElementId> productionIds = GetProductionMemberIds(document, source);
                    if (productionIds.Count == 0)
                    {
                        issues.Add(new AssemblyBatchPreflightIssue(
                            AssemblyBatchPreflightIssueKind.EmptyProductionMembers,
                            sourceId,
                            source.AssemblyTypeName,
                            "Assembly '" + source.AssemblyTypeName +
                            "' has no production members to duplicate."));
                        continue;
                    }

                    try
                    {
                        AssemblyDestinationLevelPlan destinationPlan =
                            AssemblyDestinationLevelService.CreatePlan(
                                document,
                                source,
                                productionIds,
                                destinationLevel);
                        AssemblyDocumentationPlan documentationPlan = null;
                        if (assignmentBySource.TryGetValue(
                                sourceId,
                                out AssemblyBatchNumberingAssignment assignment))
                        {
                            AssemblyDocumentationDiscoveryResult documentation =
                                AssemblyDocumentationDiscoveryService.Discover(
                                    document,
                                    source,
                                    assignment.Proposal.ProposedName);
                            documentationPlan = documentation.Plan;
                            foreach (string documentationIssue in documentation.Issues)
                            {
                                issues.Add(new AssemblyBatchPreflightIssue(
                                    documentationIssue.IndexOf("Category E", StringComparison.Ordinal) >= 0
                                        ? AssemblyBatchPreflightIssueKind.UnsupportedSheetAnnotations
                                        : AssemblyBatchPreflightIssueKind.InvalidDocumentationPlan,
                                    sourceId,
                                    source.AssemblyTypeName,
                                    documentationIssue));
                            }
                        }
                        livePlans[sourceId] = new SourcePlan(
                            productionIds,
                            destinationPlan,
                            documentationPlan);
                    }
                    catch (Exception exception)
                    {
                        issues.Add(new AssemblyBatchPreflightIssue(
                            AssemblyBatchPreflightIssueKind.InvalidDestinationPlan,
                            sourceId,
                            source.AssemblyTypeName,
                            exception.Message));
                    }
                }
            }

            if (numbering.IsValid)
            {
                List<ProposedDocumentationName> documentationNames = livePlans.Values
                    .Where(plan => plan.DocumentationPlan != null)
                    .SelectMany(plan => plan.DocumentationPlan.ProposedUniqueNames())
                    .ToList();
                IReadOnlyList<AssemblyPreflightIssue> nameIssues =
                    AssemblyPreflightService.ValidateNames(
                        numbering.Plan.Assignments.Select(item => item.Proposal).ToList(),
                        new AssemblyConflictIndex(
                            existing.Names,
                            existing.SheetNumbers,
                            existing.ViewNames),
                        documentationNames);
                foreach (AssemblyPreflightIssue issue in nameIssues)
                {
                    issues.Add(new AssemblyBatchPreflightIssue(
                        issue.Kind == AssemblyPreflightIssueKind.AssemblyNameConflict ||
                        issue.Kind == AssemblyPreflightIssueKind.DuplicateProposedName
                            ? AssemblyBatchPreflightIssueKind.TargetNameConflict
                            : AssemblyBatchPreflightIssueKind.DocumentationNameConflict,
                        FindSourceId(sources, issue.SourceName),
                        issue.SourceName,
                        issue.Message));
                }
            }

            IReadOnlyList<AssemblyBatchPreviewItem> previewItems = BuildPreviewItems(
                sources,
                numbering.Plan,
                livePlans,
                issues);
            if (issues.Count > 0 || !numbering.IsValid || destinationLevel == null)
                return new AssemblyBatchPreflightResult(null, issues, previewItems);

            var sourceById = sources.ToDictionary(source => Id(source.Id));
            var items = new List<AssemblyBatchPlanItem>(numbering.Plan.Assignments.Count);
            for (int index = 0; index < numbering.Plan.Assignments.Count; index++)
            {
                AssemblyBatchNumberingAssignment assignment = numbering.Plan.Assignments[index];
                ProposedAssemblyName proposal = assignment.Proposal;
                AssemblyInstance source = sourceById[proposal.StableElementId];
                SourcePlan sourcePlan = livePlans[proposal.StableElementId];
                AssemblyNameParser.TryParse(
                    source.AssemblyTypeName,
                    out AssemblyNameParts sourceNameParts,
                    out string _);

                items.Add(new AssemblyBatchPlanItem(
                    index + 1,
                    proposal.StableElementId,
                    source.AssemblyTypeName,
                    sourceNameParts,
                    assignment.RequestedAnchorNumber,
                    proposal.AssignedNumericValue,
                    proposal.ProposedName,
                    assignment.SkippedConflictingNames,
                    sourcePlan.ProductionIds.Select(Id),
                    Snapshot(sourcePlan.DestinationPlan),
                    sourcePlan.DocumentationPlan));
            }

            return new AssemblyBatchPreflightResult(
                new AssemblyBatchPlan(
                    numbering.Plan.StartingNumber,
                    Id(destinationLevel.Id),
                    destinationLevel.Name,
                    destinationLevel.ProjectElevation,
                    items,
                    numbering.Plan.SkippedConflictingNames),
                issues,
                previewItems);
        }

        public static AssemblyBatchRevalidationResult Revalidate(
            Document document,
            AssemblyBatchPlan confirmedPlan,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (confirmedPlan == null)
                throw new ArgumentNullException(nameof(confirmedPlan));

            var issues = new List<AssemblyBatchPreflightIssue>();
            var executionItems = new List<AssemblyBatchExecutionItem>();
            Level destinationLevel = document.GetElement(
                RevitApiCompatibility.CreateElementId(confirmedPlan.DestinationLevelId)) as Level;
            if (destinationLevel == null || !destinationLevel.IsValidObject ||
                !string.Equals(destinationLevel.Name, confirmedPlan.DestinationLevelName, StringComparison.Ordinal) ||
                Math.Abs(destinationLevel.ProjectElevation - confirmedPlan.DestinationLevelElevation) > ScalarTolerance)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.InvalidDestinationLevel,
                    null,
                    string.Empty,
                    "The confirmed destination level is unavailable or has changed."));
            }

            ExistingAssemblyIndex existing = CaptureExistingAssemblies(document);
            IReadOnlyList<AssemblyPreflightIssue> documentationNameIssues =
                AssemblyPreflightService.ValidateNames(
                    new List<ProposedAssemblyName>(),
                    new AssemblyConflictIndex(
                        existing.Names,
                        existing.SheetNumbers,
                        existing.ViewNames),
                    confirmedPlan.Items
                        .Where(item => item.Documentation != null)
                        .SelectMany(item => item.Documentation.ProposedUniqueNames())
                        .ToList());
            foreach (AssemblyPreflightIssue nameIssue in documentationNameIssues)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.DocumentationNameConflict,
                    FindSourceIdByName(confirmedPlan, nameIssue.SourceName),
                    nameIssue.SourceName,
                    nameIssue.Message));
            }
            var confirmedTargetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyBatchPlanItem item in confirmedPlan.Items)
            {
                if (!confirmedTargetNames.Add(item.TargetAssemblyName))
                {
                    issues.Add(Stale(
                        item,
                        "Confirmed target assembly name '" + item.TargetAssemblyName +
                        "' is duplicated within the batch."));
                    continue;
                }
                AssemblyInstance source = document.GetElement(
                    RevitApiCompatibility.CreateElementId(item.SourceAssemblyId)) as AssemblyInstance;
                if (source == null || !source.IsValidObject)
                {
                    issues.Add(Stale(item, "The confirmed source assembly is unavailable."));
                    continue;
                }
                if (!string.Equals(source.AssemblyTypeName, item.SourceAssemblyName, StringComparison.Ordinal))
                {
                    issues.Add(Stale(item, "The confirmed source assembly name has changed."));
                    continue;
                }

                List<ElementId> productionIds = GetProductionMemberIds(document, source);
                if (!AssemblyMemberSetComparer.Matches(
                        item.SourceProductionMemberIds,
                        productionIds.Select(Id).ToList()))
                {
                    issues.Add(Stale(item, "The confirmed source production-member set has changed."));
                    continue;
                }

                if (existing.Names.Contains(item.TargetAssemblyName))
                    issues.Add(Stale(item, "Target assembly name '" + item.TargetAssemblyName + "' now exists."));

                AssemblyDocumentationDiscoveryResult freshDocumentation =
                    AssemblyDocumentationDiscoveryService.Discover(
                        document,
                        source,
                        item.TargetAssemblyName);
                if (freshDocumentation.Issues.Count > 0)
                {
                    issues.Add(Stale(
                        item,
                        "Documentation revalidation failed: " +
                        string.Join(" | ", freshDocumentation.Issues)));
                    continue;
                }
                if (!AssemblyDocumentationDiscoveryService.Matches(
                        item.Documentation,
                        freshDocumentation.Plan,
                        out string documentationReason))
                {
                    issues.Add(Stale(item, documentationReason));
                    continue;
                }

                if (destinationLevel == null || !destinationLevel.IsValidObject)
                    continue;

                try
                {
                    AssemblyDestinationLevelPlan fresh = AssemblyDestinationLevelService.CreatePlan(
                        document,
                        source,
                        productionIds,
                        destinationLevel);
                    if (!DestinationMatches(item.Destination, fresh))
                    {
                        issues.Add(Stale(item, "The confirmed destination-level plan is stale."));
                        continue;
                    }

                    executionItems.Add(new AssemblyBatchExecutionItem(
                        item,
                        source,
                        productionIds.AsReadOnly(),
                        fresh));
                }
                catch (Exception exception)
                {
                    issues.Add(Stale(item, "Destination revalidation failed: " + exception.Message));
                }
            }

            try
            {
                AssemblyIdentityFamilyAvailability availability =
                    AssemblyIdentityFamilyService.InspectAvailability(document, assemblyDirectory);
                if (!availability.IsAvailable)
                {
                    issues.Add(new AssemblyBatchPreflightIssue(
                        AssemblyBatchPreflightIssueKind.IdentityFamilyUnavailable,
                        null,
                        string.Empty,
                        availability.Issue));
                }
            }
            catch (Exception exception)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.IdentityFamilyUnavailable,
                    null,
                    string.Empty,
                    exception.Message));
            }

            if (issues.Count > 0)
                executionItems.Clear();
            return new AssemblyBatchRevalidationResult(executionItems, issues);
        }

        private static List<AssemblyInstance> ResolveSources(
            Document document,
            IEnumerable<ElementId> selectedIds,
            ICollection<AssemblyBatchPreflightIssue> issues)
        {
            var seen = new HashSet<long>();
            var sources = new List<AssemblyInstance>();
            foreach (ElementId selectedId in selectedIds)
            {
                long value = Id(selectedId);
                if (!seen.Add(value))
                {
                    issues.Add(new AssemblyBatchPreflightIssue(
                        AssemblyBatchPreflightIssueKind.DuplicateSource,
                        value,
                        string.Empty,
                        "Assembly " + value + " was selected more than once."));
                    continue;
                }

                AssemblyInstance source = document.GetElement(selectedId) as AssemblyInstance;
                if (source == null || !source.IsValidObject)
                {
                    issues.Add(new AssemblyBatchPreflightIssue(
                        AssemblyBatchPreflightIssueKind.InvalidSelection,
                        value,
                        string.Empty,
                        "Selected element " + value + " is not an available Assembly Instance."));
                    continue;
                }
                sources.Add(source);
            }

            if (sources.Count == 0)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.InvalidSelection,
                    null,
                    string.Empty,
                    "Select at least one Revit Assembly Instance."));
            }
            return sources;
        }

        // Changed by Jhay: selection order never controls production numbering order.
        private static List<AssemblyInstance> OrderSources(
            IEnumerable<AssemblyInstance> sources)
        {
            return sources
                .Select(source =>
                {
                    bool parsed = AssemblyNameParser.TryParse(
                        source.AssemblyTypeName,
                        out AssemblyNameParts parts,
                        out string _);
                    return new
                    {
                        Source = source,
                        Parsed = parsed,
                        NumericValue = parsed ? parts.NumericValue : long.MaxValue
                    };
                })
                .OrderBy(item => item.Parsed ? 0 : 1)
                .ThenBy(item => item.NumericValue)
                .ThenBy(item => item.Source.AssemblyTypeName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => Id(item.Source.Id))
                .Select(item => item.Source)
                .ToList();
        }

        // Changed by Jhay: keep every resolved source visible even when confirmation is blocked.
        private static IReadOnlyList<AssemblyBatchPreviewItem> BuildPreviewItems(
            IReadOnlyList<AssemblyInstance> sources,
            AssemblyBatchNumberingPlan numberingPlan,
            IReadOnlyDictionary<long, SourcePlan> livePlans,
            IReadOnlyCollection<AssemblyBatchPreflightIssue> issues)
        {
            var assignments = numberingPlan == null
                ? new Dictionary<long, AssemblyBatchNumberingAssignment>()
                : numberingPlan.Assignments.ToDictionary(
                    item => item.Proposal.StableElementId);
            List<string> batchIssues = issues
                .Where(issue => !issue.SourceAssemblyId.HasValue)
                .Select(issue => issue.Message)
                .ToList();
            var preview = new List<AssemblyBatchPreviewItem>(sources.Count);
            for (int index = 0; index < sources.Count; index++)
            {
                AssemblyInstance source = sources[index];
                long sourceId = Id(source.Id);
                assignments.TryGetValue(sourceId, out AssemblyBatchNumberingAssignment assignment);
                livePlans.TryGetValue(sourceId, out SourcePlan sourcePlan);
                List<string> rowIssues = issues
                    .Where(issue => issue.SourceAssemblyId == sourceId)
                    .Select(issue => issue.Message)
                    .Concat(batchIssues)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (assignment == null && rowIssues.Count == 0)
                    rowIssues.Add("No final target assignment is available.");

                string status = rowIssues.Count > 0
                    ? "Invalid"
                    : assignment.IsManualAnchor
                        ? assignment.RequestedAnchorNumber == assignment.Proposal.AssignedNumericValue
                            ? "Ready • Manual"
                            : "Ready • Manual " + assignment.RequestedAnchorNumber + " → " +
                              assignment.Proposal.AssignedNumericValue
                        : assignment.SkippedConflictingNames.Count > 0
                            ? "Ready • Conflicts skipped"
                            : "Ready";
                preview.Add(new AssemblyBatchPreviewItem
                {
                    Order = index + 1,
                    SourceAssemblyId = sourceId,
                    SourceAssemblyName = source.AssemblyTypeName,
                    SourceLevelName = sourcePlan?.DestinationPlan?.SourceLevel?.Name ?? "<unavailable>",
                    RequestedAnchorNumber = assignment?.RequestedAnchorNumber,
                    AssignedNumber = assignment?.Proposal.AssignedNumericValue,
                    TargetAssemblyName = assignment?.Proposal.ProposedName ?? "<unavailable>",
                    DocumentationSummary = sourcePlan?.DocumentationPlan?.Summary ?? "<unavailable>",
                    Status = status,
                    SkippedConflictingNames = assignment?.SkippedConflictingNames ??
                        new List<string>().AsReadOnly(),
                    Issues = rowIssues.AsReadOnly()
                });
            }
            return preview.AsReadOnly();
        }

        private static Level ResolveDestinationLevel(
            Document document,
            ElementId destinationLevelId,
            ICollection<AssemblyBatchPreflightIssue> issues)
        {
            Level level = destinationLevelId == null
                ? null
                : document.GetElement(destinationLevelId) as Level;
            if (level == null || !level.IsValidObject)
            {
                issues.Add(new AssemblyBatchPreflightIssue(
                    AssemblyBatchPreflightIssueKind.InvalidDestinationLevel,
                    null,
                    string.Empty,
                    "Choose an available destination level."));
                return null;
            }
            return level;
        }

        private static ExistingAssemblyIndex CaptureExistingAssemblies(Document document)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var sheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var viewNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyInstance assembly in new FilteredElementCollector(document)
                         .OfClass(typeof(AssemblyInstance))
                         .Cast<AssemblyInstance>())
            {
                string name = assembly.AssemblyTypeName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(name))
                    names.Add(name);
            }
            foreach (View view in new FilteredElementCollector(document)
                         .OfClass(typeof(View))
                         .Cast<View>())
            {
                if (!string.IsNullOrWhiteSpace(view.Name))
                    viewNames.Add(view.Name);
                if (view is ViewSheet sheet && !string.IsNullOrWhiteSpace(sheet.SheetNumber))
                    sheetNumbers.Add(sheet.SheetNumber);
            }
            return new ExistingAssemblyIndex(names, sheetNumbers, viewNames);
        }

        private static List<ElementId> GetProductionMemberIds(
            Document document,
            AssemblyInstance source)
        {
            return source.GetMemberIds()
                .Where(id => !IsIdentityMarker(document.GetElement(id)))
                .ToList();
        }

        private static bool IsIdentityMarker(Element element)
        {
            var instance = element as FamilyInstance;
            return instance?.Symbol?.Family != null &&
                string.Equals(
                    instance.Symbol.Family.Name,
                    AssemblyIdentityFamilyService.FamilyName,
                    StringComparison.Ordinal) &&
                instance.Symbol.Name.StartsWith("PS_ASM_ID_", StringComparison.Ordinal);
        }

        private static AssemblyDestinationPlanSnapshot Snapshot(AssemblyDestinationLevelPlan plan)
        {
            return new AssemblyDestinationPlanSnapshot(
                Id(plan.SourceLevel.Id),
                plan.SourceLevel.Name,
                plan.SourceLevel.ProjectElevation,
                Id(plan.DestinationLevel.Id),
                plan.DestinationLevel.Name,
                plan.DestinationLevel.ProjectElevation,
                plan.SourceAssemblyOffset,
                plan.DeltaZ,
                TransformSnapshot.Capture(plan.TargetTransform));
        }

        private static bool DestinationMatches(
            AssemblyDestinationPlanSnapshot confirmed,
            AssemblyDestinationLevelPlan fresh)
        {
            return confirmed != null &&
                confirmed.SourceLevelId == Id(fresh.SourceLevel.Id) &&
                string.Equals(confirmed.SourceLevelName, fresh.SourceLevel.Name, StringComparison.Ordinal) &&
                Math.Abs(confirmed.SourceLevelElevation - fresh.SourceLevel.ProjectElevation) <= ScalarTolerance &&
                confirmed.DestinationLevelId == Id(fresh.DestinationLevel.Id) &&
                string.Equals(confirmed.DestinationLevelName, fresh.DestinationLevel.Name, StringComparison.Ordinal) &&
                Math.Abs(confirmed.DestinationLevelElevation - fresh.DestinationLevel.ProjectElevation) <= ScalarTolerance &&
                Math.Abs(confirmed.SourceAssemblyOffset - fresh.SourceAssemblyOffset) <= ScalarTolerance &&
                Math.Abs(confirmed.DeltaZ - fresh.DeltaZ) <= ScalarTolerance &&
                confirmed.TargetTransform.Matches(fresh.TargetTransform, ScalarTolerance);
        }

        private static AssemblyBatchPreflightIssue Stale(
            AssemblyBatchPlanItem item,
            string message)
        {
            return new AssemblyBatchPreflightIssue(
                AssemblyBatchPreflightIssueKind.StaleConfirmedPlan,
                item.SourceAssemblyId,
                item.SourceAssemblyName,
                message);
        }

        private static long? FindSourceId(
            IEnumerable<AssemblyInstance> sources,
            string sourceName)
        {
            AssemblyInstance source = sources.FirstOrDefault(item =>
                string.Equals(item.AssemblyTypeName, sourceName, StringComparison.OrdinalIgnoreCase));
            return source == null ? (long?)null : Id(source.Id);
        }

        private static long? FindSourceIdByName(AssemblyBatchPlan plan, string sourceName)
        {
            AssemblyBatchPlanItem item = plan.Items.FirstOrDefault(candidate =>
                string.Equals(candidate.SourceAssemblyName, sourceName, StringComparison.OrdinalIgnoreCase));
            return item?.SourceAssemblyId;
        }

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);

        private sealed class ExistingAssemblyIndex
        {
            public ExistingAssemblyIndex(
                HashSet<string> names,
                HashSet<string> sheetNumbers,
                HashSet<string> viewNames)
            {
                Names = names;
                SheetNumbers = sheetNumbers;
                ViewNames = viewNames;
            }

            public HashSet<string> Names { get; }
            public HashSet<string> SheetNumbers { get; }
            public HashSet<string> ViewNames { get; }
        }

        private sealed class SourcePlan
        {
            public SourcePlan(
                List<ElementId> productionIds,
                AssemblyDestinationLevelPlan destinationPlan,
                AssemblyDocumentationPlan documentationPlan)
            {
                ProductionIds = productionIds;
                DestinationPlan = destinationPlan;
                DocumentationPlan = documentationPlan;
            }

            public List<ElementId> ProductionIds { get; }
            public AssemblyDestinationLevelPlan DestinationPlan { get; }
            public AssemblyDocumentationPlan DocumentationPlan { get; }
        }
    }
}
