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
