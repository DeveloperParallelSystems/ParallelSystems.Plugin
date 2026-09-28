using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Changed by Jhay: documentation must pass completely before the batch may commit.
    internal static class AssemblyDocumentationValidationService
    {
        private const double Tolerance = 1e-6;

        public static void Validate(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            AssemblyDocumentationPlan plan,
            IReadOnlyDictionary<long, long> targetViewIds,
            long? targetSheetId,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            AssemblyDocumentationEvidence evidence)
        {
            var failures = new List<string>();
            Check(evidence, failures, "Documentation mapping count",
                targetViewIds.Count == plan.Views.Count,
                "Expected " + plan.Views.Count + ", found " + targetViewIds.Count + ".");

            AssemblyDocumentationDiscoveryResult freshSource =
                AssemblyDocumentationDiscoveryService.Discover(document, source, plan.TargetAssemblyName);
            bool sourcePreserved = freshSource.Plan != null &&
                string.Equals(plan.Signature, freshSource.Plan.Signature, StringComparison.Ordinal);
            Check(evidence, failures, "Source documentation unchanged", sourcePreserved,
                sourcePreserved
                    ? "Source documentation signature is unchanged."
                    : "Source discovery changed: " + string.Join(" | ", freshSource.Issues));

            var expectedOwnedIds = new HashSet<long>(targetViewIds.Values);
            if (targetSheetId.HasValue)
                expectedOwnedIds.Add(targetSheetId.Value);
            List<View> actualOwned = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && Id(view.AssociatedAssemblyInstanceId) == Id(target.Id))
                .ToList();
            var actualOwnedIds = new HashSet<long>(actualOwned.Select(view => Id(view.Id)));
            Check(evidence, failures, "Target-owned documentation IDs",
                actualOwnedIds.SetEquals(expectedOwnedIds),
                "Expected [" + Join(expectedOwnedIds) + "], found [" + Join(actualOwnedIds) + "].");

            foreach (AssemblyDocumentationViewPlan viewPlan in plan.Views)
            {
                if (!targetViewIds.TryGetValue(viewPlan.SourceViewId, out long targetViewId))
                {
                    Check(evidence, failures, "View " + viewPlan.SourceViewId + " mapping", false,
                        "No target view was mapped.");
                    continue;
                }
                View sourceView = document.GetElement(
                    RevitApiCompatibility.CreateElementId(viewPlan.SourceViewId)) as View;
                View targetView = document.GetElement(
                    RevitApiCompatibility.CreateElementId(targetViewId)) as View;
                Check(evidence, failures, "View " + viewPlan.SourceViewId + " source ownership",
                    sourceView != null && Id(sourceView.AssociatedAssemblyInstanceId) == Id(source.Id),
                    "Source owner must remain " + Id(source.Id) + ".");
                if (targetView == null)
                {
                    Check(evidence, failures, "View " + viewPlan.SourceViewId + " target availability",
                        false, "Mapped target view " + targetViewId + " is unavailable.");
                    continue;
                }
                Check(evidence, failures, "View " + viewPlan.SourceViewId + " target ownership",
                    Id(targetView.AssociatedAssemblyInstanceId) == Id(target.Id),
                    "Target " + targetViewId + " owner is " + Id(targetView.AssociatedAssemblyInstanceId) + ".");
                if (!string.IsNullOrWhiteSpace(viewPlan.TargetName))
                {
                    Check(evidence, failures, "View " + viewPlan.SourceViewId + " name",
                        string.Equals(targetView.Name, viewPlan.TargetName, StringComparison.Ordinal),
                        "Expected '" + viewPlan.TargetName + "', found '" + targetView.Name + "'.");
                }
                Check(evidence, failures, "View " + viewPlan.SourceViewId + " template",
                    Id(targetView.ViewTemplateId) == viewPlan.TemplateId,
                    "Expected template " + viewPlan.TemplateId + ", found " + Id(targetView.ViewTemplateId) + ".");
                ValidateViewGeometry(source, target, targetView, viewPlan, evidence, failures);
                ValidateViewAnnotations(
                    document,
                    source,
                    target,
                    targetView,
                    viewPlan,
                    sourceToTargetMemberIds,
                    evidence,
                    failures);
            }

            ValidateSheet(
                document, source, target, plan, targetViewIds, targetSheetId, evidence, failures);

            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "Documentation validation failed:" + Environment.NewLine +
                    string.Join(Environment.NewLine, failures));
            }
        }

        private static void ValidateViewAnnotations(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            View targetView,
            AssemblyDocumentationViewPlan sourceViewPlan,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            AssemblyDocumentationEvidence evidence,
            ICollection<string> failures)
        {
            var discoveryIssues = new List<string>();
            AssemblyDocumentationViewAnnotationPlan targetPlan =
                AssemblyDocumentationDiscoveryService.CaptureViewAnnotations(
                    document,
                    target,
                    targetView,
                    discoveryIssues);
            foreach (string issue in discoveryIssues)
            {
                Check(evidence, failures,
                    "View " + sourceViewPlan.SourceViewId + " target annotation classification",
                    false,
                    issue);
            }

            // Changed by Jhay: report and validate each semantic view-content class
            // independently so equal total counts cannot hide a missing class.
            Dictionary<string, int> expectedInventory = AnnotationInventory(
                sourceViewPlan.Annotations.Items);
            Dictionary<string, int> actualInventory = AnnotationInventory(targetPlan.Items);
            foreach (string annotationClass in expectedInventory.Keys
                         .Union(actualInventory.Keys)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                expectedInventory.TryGetValue(annotationClass, out int expectedCount);
                actualInventory.TryGetValue(annotationClass, out int actualCount);
                Check(evidence, failures,
                    "View " + sourceViewPlan.SourceViewId +
                    " annotation inventory " + annotationClass,
                    expectedCount == actualCount,
                    "Expected " + expectedCount + ", found " + actualCount + ".");
            }

            List<string> expectedInfrastructure = sourceViewPlan.Annotations.Items
                .Where(item => item.Kind ==
                    AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            List<string> actualInfrastructure = targetPlan.Items
                .Where(item => item.Kind ==
                    AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId +
                " automatically generated view infrastructure",
                expectedInfrastructure.SequenceEqual(actualInfrastructure),
                "Expected " + expectedInfrastructure.Count +
                " managed infrastructure elements, found " + actualInfrastructure.Count + ".");

            List<string> expectedIndependent = sourceViewPlan.Annotations.Items
                .Where(item => item.Kind ==
                    AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            List<string> actualIndependent = targetPlan.Items
                .Where(item => item.Kind ==
                    AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId + " independent annotation content",
                expectedIndependent.SequenceEqual(actualIndependent),
                "Expected " + expectedIndependent.Count + " independent roots, found " +
                actualIndependent.Count + ".");

            List<AssemblyDocumentationViewAnnotationItem> sourceTags = sourceViewPlan.Annotations.Items
                .Where(item => item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentTag)
                .ToList();
            List<AssemblyDocumentationViewAnnotationItem> targetTags = targetPlan.Items
                .Where(item => item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentTag)
                .ToList();
            var matchedTargetTagIds = new HashSet<long>();
            foreach (AssemblyDocumentationViewAnnotationItem sourceTag in sourceTags)
            {
                AssemblyDocumentationReferencePlan sourceReference = sourceTag.Tag.References.Single();
                bool hasMemberMap = sourceToTargetMemberIds.TryGetValue(
                    sourceReference.SourceElementId,
                    out long targetMemberId);
                List<AssemblyDocumentationViewAnnotationItem> matches = hasMemberMap
                    ? targetTags.Where(candidate =>
                        !matchedTargetTagIds.Contains(candidate.ElementId) &&
                        candidate.TypeId == sourceTag.TypeId &&
                        candidate.Tag.References.Count == 1 &&
                        candidate.Tag.References[0].SourceElementId == targetMemberId &&
                        SamePoint(candidate.Tag.HeadInView, sourceTag.Tag.HeadInView) &&
                        candidate.Tag.HasLeader == sourceTag.Tag.HasLeader &&
                        candidate.Tag.LeaderEndCondition == sourceTag.Tag.LeaderEndCondition &&
                        SameOptionalPoint(
                            candidate.Tag.References[0].LeaderElbowInView,
                            sourceReference.LeaderElbowInView) &&
                        SameOptionalPoint(
                            candidate.Tag.References[0].LeaderEndInView,
                            sourceReference.LeaderEndInView) &&
                        candidate.Tag.Orientation == sourceTag.Tag.Orientation &&
                        Math.Abs(candidate.Tag.RotationAngle - sourceTag.Tag.RotationAngle) <= Tolerance)
                        .ToList()
                    : new List<AssemblyDocumentationViewAnnotationItem>();
                bool exact = matches.Count == 1;
                if (exact)
                    matchedTargetTagIds.Add(matches[0].ElementId);
                Check(evidence, failures,
                    "View " + sourceViewPlan.SourceViewId + " tag " + sourceTag.ElementId,
                    exact,
                    hasMemberMap
                        ? "Expected one tag of type " + sourceTag.TypeId + " referencing mapped target " +
                          targetMemberId + " at view point " + sourceTag.Tag.HeadInView.Signature +
                          ", found " + matches.Count + "."
                        : "Source referenced member " + sourceReference.SourceElementId +
                          " has no target mapping.");
            }
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId + " tag count",
                sourceTags.Count == targetTags.Count && matchedTargetTagIds.Count == targetTags.Count,
                "Expected " + sourceTags.Count + " mapped tags, found " + targetTags.Count +
                " with " + matchedTargetTagIds.Count + " exact matches.");

            // Changed by Jhay: deferred is only a preflight lifecycle state. Final
            // validation requires a unique recreated target dimension whose ordered
            // references all point to the locked engine's mapped target members.
            List<AssemblyDocumentationViewAnnotationItem> sourceDeferred =
                sourceViewPlan.Annotations.Items
                    .Where(item => item.Kind ==
                        AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension)
                    .ToList();
            List<AssemblyDocumentationViewAnnotationItem> targetDeferred = targetPlan.Items
                .Where(item => item.Kind ==
                    AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension)
                .ToList();
            var matchedTargetDimensionIds = new HashSet<long>();
            Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
            var sourceMemberIds = new HashSet<long>(source.GetMemberIds().Select(Id));
            foreach (AssemblyDocumentationViewAnnotationItem sourceDimension in sourceDeferred)
            {
                var evaluations = new List<Tuple<
                    AssemblyDocumentationViewAnnotationItem,
                    bool,
                    IReadOnlyList<string>>>();
                foreach (AssemblyDocumentationViewAnnotationItem candidate in targetDeferred
                             .Where(candidate =>
                                 !matchedTargetDimensionIds.Contains(candidate.ElementId)))
                {
                    bool equivalent = IsMappedDimensionEquivalent(
                        sourceDimension,
                        candidate,
                        Id(targetView.Id),
                        sourceToTargetMemberIds,
                        sourceMemberIds,
                        mapping,
                        out IReadOnlyList<string> predicateResults);
                    evaluations.Add(Tuple.Create(candidate, equivalent, predicateResults));
                    // Changed by Jhay: report every strict predicate independently so
                    // a valid created dimension is never rejected by an opaque boolean.
                    evidence.Observations.Add(
                        "STRICT DIMENSION MATCH | source " + sourceDimension.ElementId +
                        " | target candidate " + candidate.ElementId);
                    foreach (string predicateResult in predicateResults)
                        evidence.Observations.Add("  " + predicateResult);
                }
                List<AssemblyDocumentationViewAnnotationItem> matches = evaluations
                    .Where(evaluation => evaluation.Item2)
                    .Select(evaluation => evaluation.Item1)
                    .ToList();
                if (matches.Count == 1)
                    matchedTargetDimensionIds.Add(matches[0].ElementId);
                Check(evidence, failures,
                    "View " + sourceViewPlan.SourceViewId + " deferred dimension " +
                    sourceDimension.ElementId,
                    matches.Count == 1,
                    "Expected one strict mapped target dimension, found " + matches.Count + ".");
            }
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId + " deferred dimension count",
                sourceDeferred.Count == targetDeferred.Count &&
                matchedTargetDimensionIds.Count == targetDeferred.Count,
                "Expected " + sourceDeferred.Count + " mapped dimensions, found " +
                targetDeferred.Count + " with " + matchedTargetDimensionIds.Count +
                " strict matches.");

            int sourceDimensions = sourceViewPlan.Annotations.Items.Count(item =>
                item.Kind == AssemblyDocumentationViewAnnotationKind.Dimension ||
                item.Kind == AssemblyDocumentationViewAnnotationKind.SpotDimension ||
                item.Kind == AssemblyDocumentationViewAnnotationKind.MultiReferenceAnnotation);
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId + " reference annotation count",
                sourceDimensions == 0,
                sourceDimensions == 0
                    ? "No unsupported reference annotations were planned."
                    : sourceDimensions + " reference annotations require semantic geometry mapping.");

            int targetUnclassified = targetPlan.Items.Count(item =>
                item.Kind == AssemblyDocumentationViewAnnotationKind.Unsupported);
            Check(evidence, failures,
                "View " + sourceViewPlan.SourceViewId + " unclassified target annotations",
                targetUnclassified == 0,
                targetUnclassified + " unclassified target view-owned elements.");
        }

        private static bool IsMappedDimensionEquivalent(
            AssemblyDocumentationViewAnnotationItem source,
            AssemblyDocumentationViewAnnotationItem target,
            long expectedTargetViewId,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            ISet<long> sourceMemberIds,
            Transform mapping,
            out IReadOnlyList<string> predicateResults)
        {
            var results = new List<string>();
            AssemblyDocumentationReferenceAnnotationPlan sourcePlan = source.ReferenceAnnotation;
            AssemblyDocumentationReferenceAnnotationPlan targetPlan = target.ReferenceAnnotation;
            bool equivalent = Predicate(results, "OwnerViewId",
                target.OwnerViewId == expectedTargetViewId,
                "expected " + expectedTargetViewId + ", found " + target.OwnerViewId);
            equivalent &= Predicate(results, "DimensionTypeId",
                source.TypeId == target.TypeId,
                "expected " + source.TypeId + ", found " + target.TypeId);
            if (sourcePlan == null || targetPlan == null)
            {
                equivalent &= Predicate(results, "Reference plan", false,
                    "source=" + (sourcePlan == null ? "<null>" : "available") +
                    ", target=" + (targetPlan == null ? "<null>" : "available"));
                predicateResults = results;
                return false;
            }

            equivalent &= Predicate(results, "Reference count",
                sourcePlan.References.Count == targetPlan.References.Count,
                "expected " + sourcePlan.References.Count + ", found " +
                targetPlan.References.Count);
            var expectedReferenceOwnerIds = new List<long>();
            bool everyReferenceMapped = true;
            foreach (AssemblyDocumentationReferencePlan reference in sourcePlan.References)
            {
                if (sourceToTargetMemberIds.TryGetValue(
                        reference.SourceElementId, out long mappedTargetId))
                    expectedReferenceOwnerIds.Add(mappedTargetId);
                else
                    everyReferenceMapped = false;
            }
            List<long> actualReferenceOwnerIds = targetPlan.References
                .Select(reference => reference.SourceElementId)
                .ToList();
            bool referenceOrderMatches = everyReferenceMapped &&
                expectedReferenceOwnerIds.SequenceEqual(actualReferenceOwnerIds);
            equivalent &= Predicate(results, "Reference order",
                referenceOrderMatches,
                "expected [" + string.Join(",", expectedReferenceOwnerIds) + "], found [" +
                string.Join(",", actualReferenceOwnerIds) + "]");
            bool referenceOwnersMatch = everyReferenceMapped &&
                expectedReferenceOwnerIds.OrderBy(id => id)
                    .SequenceEqual(actualReferenceOwnerIds.OrderBy(id => id));
            equivalent &= Predicate(results, "Referenced target ElementIds",
                referenceOwnersMatch,
                "expected [" + Join(expectedReferenceOwnerIds) + "], found [" +
                Join(actualReferenceOwnerIds) + "]");
            equivalent &= Predicate(results, "No source assembly references",
                actualReferenceOwnerIds.All(id => !sourceMemberIds.Contains(id)),
                "actual target reference owners [" + Join(actualReferenceOwnerIds) + "]");
            equivalent &= Predicate(results, "Segment count",
                sourcePlan.SegmentCount == targetPlan.SegmentCount,
                "expected " + sourcePlan.SegmentCount + ", found " +
                targetPlan.SegmentCount);
            equivalent &= Predicate(results, "Dimension value",
                string.Equals(sourcePlan.ValueText, targetPlan.ValueText,
                    StringComparison.Ordinal),
                "expected '" + sourcePlan.ValueText + "', found '" +
                targetPlan.ValueText + "'");
            equivalent &= Predicate(results, "Prefix/suffix/text and display settings",
                DictionaryEqual(sourcePlan.Formatting, targetPlan.Formatting),
                "expected [" + DictionarySignature(sourcePlan.Formatting) +
                "], found [" + DictionarySignature(targetPlan.Formatting) + "]");
            equivalent &= Predicate(results, "Dimension curve kind",
                sourcePlan.LineIsBound == targetPlan.LineIsBound,
                "source IsBound=" + sourcePlan.LineIsBound +
                ", target IsBound=" + targetPlan.LineIsBound);

            bool directionEquivalent = SameDimensionLineDirection(sourcePlan, targetPlan);
            equivalent &= Predicate(results, "Dimension curve direction",
                directionEquivalent,
                DimensionLineDirectionDetails(sourcePlan, targetPlan));
            bool placementEquivalent = SameDimensionLinePlacement(sourcePlan, targetPlan);
            equivalent &= Predicate(results, "Dimension curve placement/origin",
                placementEquivalent,
                DimensionLinePlacementDetails(sourcePlan, targetPlan));
            // Changed by Jhay: raw curve signatures are diagnostic only. Revit may
            // reverse an unbounded line direction or choose another origin on that
            // same infinite line when NewDimension canonicalizes the result.
            results.Add("Raw curve signature ........ INFO | source [" +
                sourcePlan.CurveSignature + "] | target [" + targetPlan.CurveSignature + "]");

            int comparableReferenceCount = Math.Min(
                sourcePlan.References.Count, targetPlan.References.Count);
            for (int index = 0; index < comparableReferenceCount; index++)
            {
                AssemblyDocumentationReferencePlan sourceReference = sourcePlan.References[index];
                AssemblyDocumentationReferencePlan targetReference = targetPlan.References[index];
                bool hasExpectedTarget = sourceToTargetMemberIds.TryGetValue(
                    sourceReference.SourceElementId, out long expectedTargetId);
                bool ownerMatches = hasExpectedTarget &&
                    targetReference.SourceElementId == expectedTargetId;
                equivalent &= Predicate(results, "Ref[" + index + "] owner/order",
                    ownerMatches,
                    "source " + sourceReference.SourceElementId + " -> expected target " +
                    (hasExpectedTarget ? expectedTargetId.ToString(CultureInfo.InvariantCulture) :
                        "<unmapped>") + ", found " + targetReference.SourceElementId);
                equivalent &= Predicate(results, "Ref[" + index + "] excludes source members",
                    !sourceMemberIds.Contains(targetReference.SourceElementId),
                    "actual owner " + targetReference.SourceElementId);
                equivalent &= Predicate(results, "Ref[" + index + "] ReferenceType",
                    sourceReference.ReferenceType == targetReference.ReferenceType,
                    "expected " + sourceReference.ReferenceType + ", found " +
                    targetReference.ReferenceType);
                bool semanticKindMatches = sourceReference.Semantic != null &&
                    targetReference.Semantic != null &&
                    sourceReference.Semantic.Kind == targetReference.Semantic.Kind;
                equivalent &= Predicate(results, "Ref[" + index + "] semantic kind",
                    semanticKindMatches,
                    "expected " + SemanticKind(sourceReference.Semantic) + ", found " +
                    SemanticKind(targetReference.Semantic));
                bool semanticMatches = SameMappedSemantic(
                    sourceReference.Semantic, targetReference.Semantic, mapping);
                equivalent &= Predicate(results, "Ref[" + index + "] mapped semantic geometry",
                    semanticMatches,
                    "source semantic [" + SemanticSignature(sourceReference.Semantic) +
                    "] | target semantic [" + SemanticSignature(targetReference.Semantic) + "]");
                results.Add("Ref[" + index + "] stable representation  INFO | source [" +
                    sourceReference.StableRepresentation + "] | target [" +
                    targetReference.StableRepresentation + "]");
            }
            predicateResults = results;
            return equivalent;
        }

        private static bool Predicate(
            ICollection<string> results,
            string name,
            bool passed,
            string details)
        {
            results.Add(name.PadRight(34, '.') + " " + (passed ? "PASS" : "FAIL") +
                " | " + details);
            return passed;
        }

        private static bool SameDimensionLineDirection(
            AssemblyDocumentationReferenceAnnotationPlan source,
            AssemblyDocumentationReferenceAnnotationPlan target)
        {
            XYZ sourceDirection = DimensionLineDirection(source);
            XYZ targetDirection = DimensionLineDirection(target);
            if (sourceDirection == null || targetDirection == null)
                return sourceDirection == null && targetDirection == null;
            sourceDirection = sourceDirection.Normalize();
            targetDirection = targetDirection.Normalize();
            return Math.Min(
                sourceDirection.DistanceTo(targetDirection),
                sourceDirection.DistanceTo(targetDirection.Negate())) <= Tolerance;
        }

        private static bool SameDimensionLinePlacement(
            AssemblyDocumentationReferenceAnnotationPlan source,
            AssemblyDocumentationReferenceAnnotationPlan target)
        {
            if (source.LineIsBound != target.LineIsBound)
                return false;
            if (source.LineIsBound)
            {
                return SameUnorderedEndpoints(
                    source.LineStartInView,
                    source.LineEndInView,
                    target.LineStartInView,
                    target.LineEndInView);
            }
            if (source.LineOriginInView == null || target.LineOriginInView == null)
                return source.LineOriginInView == null && target.LineOriginInView == null;
            XYZ direction = DimensionLineDirection(source);
            if (direction == null || direction.GetLength() <= Tolerance)
                return false;
            XYZ delta = target.LineOriginInView.ToXyz() - source.LineOriginInView.ToXyz();
            XYZ normalComponent = delta - direction.Normalize() *
                delta.DotProduct(direction.Normalize());
            return normalComponent.GetLength() <= Tolerance;
        }

        private static XYZ DimensionLineDirection(
            AssemblyDocumentationReferenceAnnotationPlan plan)
        {
            if (!plan.LineIsBound)
                return plan.LineDirectionInView?.ToXyz();
            if (plan.LineStartInView == null || plan.LineEndInView == null)
                return null;
            return plan.LineEndInView.ToXyz() - plan.LineStartInView.ToXyz();
        }

        private static bool SameUnorderedEndpoints(
            AssemblyDocumentationXyzSnapshot sourceStart,
            AssemblyDocumentationXyzSnapshot sourceEnd,
            AssemblyDocumentationXyzSnapshot targetStart,
            AssemblyDocumentationXyzSnapshot targetEnd)
        {
            if (sourceStart == null || sourceEnd == null || targetStart == null || targetEnd == null)
            {
                return sourceStart == null && sourceEnd == null &&
                    targetStart == null && targetEnd == null;
            }
            return SamePoint(sourceStart, targetStart) && SamePoint(sourceEnd, targetEnd) ||
                SamePoint(sourceStart, targetEnd) && SamePoint(sourceEnd, targetStart);
        }

        private static string DimensionLineDirectionDetails(
            AssemblyDocumentationReferenceAnnotationPlan source,
            AssemblyDocumentationReferenceAnnotationPlan target) =>
            "source " + OptionalPoint(DimensionLineDirection(source)) +
            ", target " + OptionalPoint(DimensionLineDirection(target)) +
            " (opposite directions represent the same dimension line)";

        private static string DimensionLinePlacementDetails(
            AssemblyDocumentationReferenceAnnotationPlan source,
            AssemblyDocumentationReferenceAnnotationPlan target) =>
            source.LineIsBound
                ? "source endpoints " + OptionalPoint(source.LineStartInView?.ToXyz()) + " -> " +
                  OptionalPoint(source.LineEndInView?.ToXyz()) + ", target endpoints " +
                  OptionalPoint(target.LineStartInView?.ToXyz()) + " -> " +
                  OptionalPoint(target.LineEndInView?.ToXyz())
                : "source origin " + OptionalPoint(source.LineOriginInView?.ToXyz()) +
                  ", target origin " + OptionalPoint(target.LineOriginInView?.ToXyz()) +
                  " (compared as the same infinite line in view coordinates)";

        private static string DictionarySignature(
            IReadOnlyDictionary<string, string> values) =>
            string.Join(", ", values.OrderBy(item => item.Key)
                .Select(item => item.Key + "=" + item.Value));

        private static string SemanticKind(AssemblyDocumentationReferenceSemantic semantic) =>
            semantic == null ? "<none>" : semantic.Kind.ToString();

        private static string SemanticSignature(AssemblyDocumentationReferenceSemantic semantic) =>
            semantic == null ? "<none>" : semantic.Signature;

        private static string OptionalPoint(XYZ point) => point == null
            ? "<none>"
            : string.Join(",", new[] { point.X, point.Y, point.Z }
                .Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));

        private static bool DictionaryEqual(
            IReadOnlyDictionary<string, string> left,
            IReadOnlyDictionary<string, string> right) =>
            left.Count == right.Count && left.All(item =>
                right.TryGetValue(item.Key, out string value) &&
                string.Equals(item.Value, value, StringComparison.Ordinal));

        private static bool SameMappedSemantic(
            AssemblyDocumentationReferenceSemantic source,
            AssemblyDocumentationReferenceSemantic target,
            Transform mapping)
        {
            if (source == null || target == null || source.Kind != target.Kind)
                return false;
            if (source.Kind == AssemblyDocumentationReferenceSemanticKind.FamilyReference)
            {
                bool familyIdentity = string.Equals(
                           source.FamilyReferenceType,
                           target.FamilyReferenceType,
                           StringComparison.Ordinal) &&
                    string.Equals(
                        source.FamilyReferenceName,
                        target.FamilyReferenceName,
                        StringComparison.Ordinal);
                if (!familyIdentity || source.OriginOrPoint == null)
                    return familyIdentity && target.OriginOrPoint == null;
                if (target.OriginOrPoint == null || source.Normal == null || target.Normal == null)
                    return false;
                XYZ expectedFamilyPoint = mapping.OfPoint(source.OriginOrPoint.ToXyz());
                XYZ expectedFamilyNormal = mapping.OfVector(source.Normal.ToXyz()).Normalize();
                return target.OriginOrPoint.ToXyz().DistanceTo(expectedFamilyPoint) <= Tolerance &&
                    target.Normal.ToXyz().Normalize().DistanceTo(expectedFamilyNormal) <= Tolerance &&
                    Math.Abs(source.Area - target.Area) <= Tolerance &&
                    string.Equals(
                        source.TopologySignature,
                        target.TopologySignature,
                        StringComparison.Ordinal);
            }
            if (source.Kind == AssemblyDocumentationReferenceSemanticKind.LinearCurve)
            {
                if (source.OriginOrPoint == null || target.OriginOrPoint == null ||
                    source.Normal == null || target.Normal == null ||
                    !string.Equals(source.TopologySignature, target.TopologySignature,
                        StringComparison.Ordinal) ||
                    Math.Abs(source.Area - target.Area) > Tolerance)
                    return false;
                XYZ expectedStart = mapping.OfPoint(source.OriginOrPoint.ToXyz());
                XYZ expectedDirection = mapping.OfVector(source.Normal.ToXyz()).Normalize();
                XYZ expectedEnd = expectedStart + expectedDirection * source.Area;
                XYZ targetStart = target.OriginOrPoint.ToXyz();
                XYZ targetEnd = targetStart + target.Normal.ToXyz().Normalize() * target.Area;
                return targetStart.DistanceTo(expectedStart) <= Tolerance &&
                           targetEnd.DistanceTo(expectedEnd) <= Tolerance ||
                       targetStart.DistanceTo(expectedEnd) <= Tolerance &&
                           targetEnd.DistanceTo(expectedStart) <= Tolerance;
            }
            XYZ expectedPoint = mapping.OfPoint(source.OriginOrPoint.ToXyz());
            if (target.OriginOrPoint == null ||
                target.OriginOrPoint.ToXyz().DistanceTo(expectedPoint) > Tolerance)
                return false;
            if (source.Kind == AssemblyDocumentationReferenceSemanticKind.ElementPoint)
                return string.Equals(
                    source.StableSemanticPath,
                    target.StableSemanticPath,
                    StringComparison.Ordinal);
            XYZ expectedNormal = mapping.OfVector(source.Normal.ToXyz()).Normalize();
            return target.Normal != null &&
                target.Normal.ToXyz().Normalize().DistanceTo(expectedNormal) <= Tolerance &&
                Math.Abs(source.Area - target.Area) <= Tolerance &&
                string.Equals(
                    source.TopologySignature,
                    target.TopologySignature,
                    StringComparison.Ordinal);
        }

        private static bool SamePoint(
            AssemblyDocumentationXyzSnapshot left,
            AssemblyDocumentationXyzSnapshot right) =>
            left != null && right != null &&
            Math.Abs(left.X - right.X) <= Tolerance &&
            Math.Abs(left.Y - right.Y) <= Tolerance &&
            Math.Abs(left.Z - right.Z) <= Tolerance;

        private static Dictionary<string, int> AnnotationInventory(
            IEnumerable<AssemblyDocumentationViewAnnotationItem> items) =>
            items.Where(item =>
                    item.Kind != AssemblyDocumentationViewAnnotationKind.CopiedWithGroup &&
                    item.Kind != AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure)
                .GroupBy(item => item.Kind + " | " + item.RuntimeType, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        // Changed by Jhay: leader geometry is optional, but when it exists its
        // view-local placement is part of the tag's strict documentation evidence.
        private static bool SameOptionalPoint(
            AssemblyDocumentationXyzSnapshot left,
            AssemblyDocumentationXyzSnapshot right) =>
            left == null && right == null || SamePoint(left, right);

        private static void ValidateViewGeometry(
            AssemblyInstance source,
            AssemblyInstance target,
            View targetView,
            AssemblyDocumentationViewPlan plan,
            AssemblyDocumentationEvidence evidence,
            ICollection<string> failures)
        {
            if (plan.IsSchedule)
            {
                ViewSchedule schedule = targetView as ViewSchedule;
                string actual = schedule == null
                    ? string.Empty
                    : AssemblyDocumentationDiscoveryService.CaptureScheduleDefinitionSignature(schedule);
                Check(evidence, failures, "View " + plan.SourceViewId + " schedule definition",
                    schedule != null && string.Equals(
                        actual, plan.ScheduleDefinition?.Signature, StringComparison.Ordinal),
                    "The target schedule definition differs from the source definition.");
                return;
            }

            Check(evidence, failures, "View " + plan.SourceViewId + " scale",
                targetView.Scale == plan.Scale,
                "Expected " + plan.Scale + ", found " + targetView.Scale + ".");
            Check(evidence, failures, "View " + plan.SourceViewId + " detail level",
                targetView.DetailLevel == plan.DetailLevel,
                "Expected " + plan.DetailLevel + ", found " + targetView.DetailLevel + ".");
            Check(evidence, failures, "View " + plan.SourceViewId + " discipline",
                targetView.Discipline == plan.Discipline,
                "Expected " + plan.Discipline + ", found " + targetView.Discipline + ".");

            if (plan.Orientation.HasValue)
            {
                XYZ localDirection = target.GetTransform().Inverse
                    .OfVector(-targetView.ViewDirection).Normalize();
                XYZ expected = ExpectedLocalDirection(plan.Orientation.Value);
                Check(evidence, failures, "View " + plan.SourceViewId + " orientation",
                    localDirection.DistanceTo(expected) <= Tolerance,
                    "Expected " + plan.Orientation.Value + " direction " + Point(expected) +
                    ", found " + Point(localDirection) + ".");
            }

            if (targetView is View3D target3D)
            {
                Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
                XYZ expectedViewDirection = mapping.OfVector(plan.ViewDirection.ToXyz()).Normalize();
                XYZ expectedUpDirection = mapping.OfVector(plan.UpDirection.ToXyz()).Normalize();
                Check(evidence, failures, "View " + plan.SourceViewId + " 3D orientation",
                    target3D.ViewDirection.Normalize().DistanceTo(expectedViewDirection) <= Tolerance &&
                    target3D.UpDirection.Normalize().DistanceTo(expectedUpDirection) <= Tolerance,
                    "Target 3D view direction/up direction differ from the transformed source orientation.");
                // Changed by Jhay: tags and other supported 3D annotations are
                // created only after the final orientation is saved and locked.
                Check(evidence, failures, "View " + plan.SourceViewId + " 3D orientation lock",
                    target3D.IsLocked == plan.Target3DOrientationShouldBeLocked,
                    "Expected locked=" + plan.Target3DOrientationShouldBeLocked +
                    " (source locked=" + plan.Source3DOrientationLocked +
                    ", annotations require lock=" + plan.RequiresLocked3DOrientation +
                    "), found locked=" + target3D.IsLocked + ".");
                Check(evidence, failures, "View " + plan.SourceViewId + " section-box state",
                    target3D.IsSectionBoxActive == plan.SectionBoxActive,
                    "Expected active=" + plan.SectionBoxActive +
                    ", found active=" + target3D.IsSectionBoxActive + ".");
                if (plan.SectionBox != null)
                {
                    BoundingBoxXYZ actual = target3D.GetSectionBox();
                    Check(evidence, failures, "View " + plan.SourceViewId + " section-box geometry",
                        SameWorldBox(
                            TransformBox(plan.SectionBox.ToBoundingBox(), mapping),
                            actual),
                        "Target section-box geometry differs from the transformed source.");
                }
            }
            else if (plan.CropBox != null)
            {
                Check(evidence, failures, "View " + plan.SourceViewId + " crop state",
                    targetView.CropBoxActive == plan.CropBoxActive &&
                    targetView.CropBoxVisible == plan.CropBoxVisible,
                    "Expected active/visible " + plan.CropBoxActive + "/" + plan.CropBoxVisible +
                    ", found " + targetView.CropBoxActive + "/" + targetView.CropBoxVisible + ".");
                bool cropExtentsMatch = SameWorldBox(
                    TransformBox(
                        plan.CropBox.ToBoundingBox(),
                        target.GetTransform().Multiply(source.GetTransform().Inverse)),
                    targetView.CropBox);
                if (plan.CropBoxActive)
                {
                    Check(evidence, failures, "View " + plan.SourceViewId + " crop extents",
                        cropExtentsMatch,
                        "Target active crop extents differ from the source.");
                }
                else
                {
                    // Changed by Jhay: Revit may normalize hidden crop-box storage.
                    // Inactive extents do not participate in the effective visible view.
                    Check(evidence, failures, "View " + plan.SourceViewId + " crop extents",
                        true,
                        cropExtentsMatch
                            ? "Crop is inactive on source and target; stored extents match."
                            : "Crop is inactive on source and target; stored crop-box extents differ " +
                              "but are not part of the effective visible view state.");
                }
            }
        }

        private static void ValidateSheet(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            AssemblyDocumentationPlan plan,
            IReadOnlyDictionary<long, long> targetViewIds,
            long? targetSheetId,
            AssemblyDocumentationEvidence evidence,
            ICollection<string> failures)
        {
            if (plan.Sheet == null)
            {
                Check(evidence, failures, "Target assembly sheet", !targetSheetId.HasValue,
                    targetSheetId.HasValue ? "An unexplained target sheet was created." : "No sheet expected or created.");
                return;
            }

            ViewSheet sourceSheet = document.GetElement(
                RevitApiCompatibility.CreateElementId(plan.Sheet.SourceSheetId)) as ViewSheet;
            ViewSheet targetSheet = targetSheetId.HasValue
                ? document.GetElement(RevitApiCompatibility.CreateElementId(targetSheetId.Value)) as ViewSheet
                : null;
            Check(evidence, failures, "Source assembly sheet ownership",
                sourceSheet != null && Id(sourceSheet.AssociatedAssemblyInstanceId) == Id(source.Id),
                "Source sheet must remain owned by source assembly " + Id(source.Id) + ".");
            if (targetSheet == null)
            {
                Check(evidence, failures, "Target assembly sheet", false, "The mapped target sheet is unavailable.");
                return;
            }
            Check(evidence, failures, "Target assembly sheet ownership",
                Id(targetSheet.AssociatedAssemblyInstanceId) == Id(target.Id),
                "Expected owner " + Id(target.Id) + ", found " + Id(targetSheet.AssociatedAssemblyInstanceId) + ".");
            if (!string.IsNullOrWhiteSpace(plan.Sheet.TargetSheetNumber))
                Check(evidence, failures, "Target sheet number",
                    string.Equals(targetSheet.SheetNumber, plan.Sheet.TargetSheetNumber, StringComparison.Ordinal),
                    "Expected '" + plan.Sheet.TargetSheetNumber + "', found '" + targetSheet.SheetNumber + "'.");
            if (!string.IsNullOrWhiteSpace(plan.Sheet.TargetSheetName))
                Check(evidence, failures, "Target sheet name",
                    string.Equals(targetSheet.Name, plan.Sheet.TargetSheetName, StringComparison.Ordinal),
                    "Expected '" + plan.Sheet.TargetSheetName + "', found '" + targetSheet.Name + "'.");

            List<long> titleBlockTypes = new FilteredElementCollector(document, targetSheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(instance => Id(instance.OwnerViewId) == Id(targetSheet.Id))
                .Select(instance => Id(instance.GetTypeId()))
                .ToList();
            bool titleBlockMatches = RevitApiCompatibility.IsInvalidElementId(
                    RevitApiCompatibility.CreateElementId(plan.Sheet.TitleBlockTypeId))
                ? titleBlockTypes.Count == 0
                : titleBlockTypes.Count == 1 && titleBlockTypes[0] == plan.Sheet.TitleBlockTypeId;
            Check(evidence, failures, "Target title-block type", titleBlockMatches,
                "Expected type " + plan.Sheet.TitleBlockTypeId + ", found [" + Join(titleBlockTypes) + "].");

            List<Viewport> targetViewports = targetSheet.GetAllViewports()
                .Select(id => document.GetElement(id) as Viewport)
                .Where(viewport => viewport != null)
                .ToList();
            Check(evidence, failures, "Target viewport count",
                targetViewports.Count == plan.Sheet.Viewports.Count,
                "Expected " + plan.Sheet.Viewports.Count + ", found " + targetViewports.Count + ".");
            foreach (AssemblyDocumentationViewportPlan viewportPlan in plan.Sheet.Viewports)
            {
                long expectedViewId = viewportPlan.IsReusableLegend
                    ? viewportPlan.SourceViewId
                    : targetViewIds[viewportPlan.SourceViewId];
                List<Viewport> matches = targetViewports
                    .Where(viewport => Id(viewport.ViewId) == expectedViewId)
                    .ToList();
                XYZ actualCenter = matches.Count == 1 ? matches[0].GetBoxCenter() : null;
                bool exact = matches.Count == 1 &&
                    SameSheetPoint(actualCenter, viewportPlan.Center) &&
                    Id(matches[0].GetTypeId()) == viewportPlan.ViewportTypeId &&
                    matches[0].Rotation == viewportPlan.Rotation;
                Check(evidence, failures,
                    (viewportPlan.IsReusableLegend ? "Reusable legend " : "Viewport ") +
                    viewportPlan.SourceViewId,
                    exact,
                    "Expected view " + expectedViewId + " at sheet XY " +
                    SheetPoint(viewportPlan.Center) + ", type " + viewportPlan.ViewportTypeId +
                    ", rotation " + viewportPlan.Rotation + "; found " +
                    (matches.Count == 1
                        ? "XY " + Point(actualCenter) + ", type " + Id(matches[0].GetTypeId()) +
                          ", rotation " + matches[0].Rotation
                        : matches.Count + " matching placements") + ".");
            }

            List<ScheduleSheetInstance> targetSchedules = new FilteredElementCollector(document)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Where(item => Id(item.OwnerViewId) == Id(targetSheet.Id) &&
                    !item.IsTitleblockRevisionSchedule)
                .ToList();
            int targetRevisionScheduleCount = new FilteredElementCollector(document)
                .OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>()
                .Count(item => Id(item.OwnerViewId) == Id(targetSheet.Id) &&
                    item.IsTitleblockRevisionSchedule);
            Check(evidence, failures, "Title-block revision schedules",
                targetRevisionScheduleCount == plan.Sheet.TitleBlockRevisionScheduleCount,
                "Expected title-block-derived count " + plan.Sheet.TitleBlockRevisionScheduleCount +
                ", found " + targetRevisionScheduleCount + ".");
            Check(evidence, failures, "Target schedule placement count",
                targetSchedules.Count == plan.Sheet.Schedules.Count,
                "Expected " + plan.Sheet.Schedules.Count + ", found " + targetSchedules.Count + ".");
            foreach (AssemblyDocumentationSchedulePlacementPlan schedulePlan in plan.Sheet.Schedules)
            {
                long expectedScheduleId = schedulePlan.IsSourceAssemblyOwned
                    ? targetViewIds[schedulePlan.SourceScheduleId]
                    : schedulePlan.SourceScheduleId;
                List<ScheduleSheetInstance> matches = targetSchedules
                    .Where(instance => Id(instance.ScheduleId) == expectedScheduleId)
                    .ToList();
                bool exact = matches.Count == 1 &&
                    matches[0].Point.DistanceTo(schedulePlan.Point.ToXyz()) <= Tolerance &&
                    matches[0].Rotation == schedulePlan.Rotation;
                Check(evidence, failures, "Schedule placement " + schedulePlan.SourceScheduleId,
                    exact,
                    "Expected one placement of schedule " + expectedScheduleId + " at " +
                    schedulePlan.Point.Signature + ".");
            }

            // Changed by Jhay: validate supported sheet annotations independently
            // from Revit-generated title-block/sheet infrastructure.
            IReadOnlyList<AssemblyDocumentationCategoryEItem> targetSheetOwned =
                AssemblyDocumentationDiscoveryService.CaptureCategoryE(document, targetSheet);
            List<AssemblyDocumentationCategoryEItem> unsupportedTarget = targetSheetOwned
                .Where(item => item.Disposition ==
                    AssemblyDocumentationSheetOwnedItemDisposition.Unsupported)
                .ToList();
            Check(evidence, failures, "Unclassified target sheet elements",
                unsupportedTarget.Count == 0,
                unsupportedTarget.Count == 0
                    ? "0"
                    : string.Join(", ", unsupportedTarget.Select(item =>
                        item.ElementId + " " + item.RuntimeType + " " + item.CategoryName)));

            List<string> expectedAnnotations = plan.CategoryEItems
                .Where(item => item.Disposition ==
                    AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            List<string> actualAnnotations = targetSheetOwned
                .Where(item => item.Disposition ==
                    AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot)
                .Select(item => item.ContentSignature)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToList();
            bool annotationsMatch = expectedAnnotations.SequenceEqual(actualAnnotations);
            Check(evidence, failures, "Sheet-specific annotation content",
                annotationsMatch,
                "Expected " + expectedAnnotations.Count + " top-level annotation roots, found " +
                actualAnnotations.Count + ".");
        }

        private static XYZ ExpectedLocalDirection(AssemblyDetailViewOrientation orientation)
        {
            switch (orientation)
            {
                case AssemblyDetailViewOrientation.ElevationFront:
                case AssemblyDetailViewOrientation.DetailSectionA:
                    return XYZ.BasisY;
                case AssemblyDetailViewOrientation.ElevationBack:
                    return -XYZ.BasisY;
                case AssemblyDetailViewOrientation.ElevationRight:
                case AssemblyDetailViewOrientation.DetailSectionB:
                    return -XYZ.BasisX;
                case AssemblyDetailViewOrientation.ElevationLeft:
                    return XYZ.BasisX;
                case AssemblyDetailViewOrientation.ElevationTop:
                case AssemblyDetailViewOrientation.HorizontalDetail:
                    return -XYZ.BasisZ;
                case AssemblyDetailViewOrientation.ElevationBottom:
                    return XYZ.BasisZ;
                default:
                    throw new ArgumentOutOfRangeException(nameof(orientation));
            }
        }

        private static BoundingBoxXYZ TransformBox(BoundingBoxXYZ source, Transform mapping)
        {
            if (source == null)
                return null;
            return new BoundingBoxXYZ
            {
                Min = source.Min,
                Max = source.Max,
                Transform = mapping.Multiply(source.Transform)
            };
        }

        private static bool SameWorldBox(BoundingBoxXYZ expected, BoundingBoxXYZ actual)
        {
            if (expected == null || actual == null)
                return false;
            XYZ[] expectedCorners = Corners(expected);
            XYZ[] actualCorners = Corners(actual);
            return expectedCorners.All(expectedCorner =>
                actualCorners.Any(actualCorner => expectedCorner.DistanceTo(actualCorner) <= Tolerance));
        }

        private static XYZ[] Corners(BoundingBoxXYZ box)
        {
            var result = new List<XYZ>();
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            foreach (double z in new[] { box.Min.Z, box.Max.Z })
                result.Add(box.Transform.OfPoint(new XYZ(x, y, z)));
            return result.ToArray();
        }

        private static void Check(
            AssemblyDocumentationEvidence evidence,
            ICollection<string> failures,
            string name,
            bool passed,
            string details)
        {
            evidence.Invariants.Add(new AssemblyDuplicationInvariant(name, passed, details));
            if (!passed)
                failures.Add(name + " | " + details);
        }

        private static string Join(IEnumerable<long> ids) =>
            string.Join(",", ids.OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));

        private static string Point(XYZ point) => string.Join(",",
            point.X.ToString("G17", CultureInfo.InvariantCulture),
            point.Y.ToString("G17", CultureInfo.InvariantCulture),
            point.Z.ToString("G17", CultureInfo.InvariantCulture));

        // Changed by Jhay: viewport placement on a sheet is two-dimensional.
        // Revit may expose nonzero raw Z values for assembly-owned source viewports.
        private static bool SameSheetPoint(
            XYZ actual,
            AssemblyDocumentationXyzSnapshot expected) =>
            actual != null && expected != null &&
            Math.Abs(actual.X - expected.X) <= Tolerance &&
            Math.Abs(actual.Y - expected.Y) <= Tolerance;

        private static string SheetPoint(AssemblyDocumentationXyzSnapshot point) =>
            point == null
                ? "<null>"
                : point.X.ToString("G17", CultureInfo.InvariantCulture) + "," +
                  point.Y.ToString("G17", CultureInfo.InvariantCulture);

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
