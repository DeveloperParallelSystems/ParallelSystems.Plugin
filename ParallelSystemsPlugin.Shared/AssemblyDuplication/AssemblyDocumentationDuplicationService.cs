using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Changed by Jhay: duplicates documentation after the locked physical engine succeeds.
    internal static class AssemblyDocumentationDuplicationService
    {
        private const double SheetPositionTolerance = 1e-6;
        private const double ReferenceTolerance = 1e-6;

        public static AssemblyDocumentationResult Duplicate(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            AssemblyDocumentationPlan plan,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (plan == null) throw new ArgumentNullException(nameof(plan));
            if (sourceToTargetMemberIds == null)
                throw new ArgumentNullException(nameof(sourceToTargetMemberIds));

            var evidence = new AssemblyDocumentationEvidence();
            var targetViewIds = new Dictionary<long, long>();
            var targetViewportIds = new Dictionary<long, long>();
            IReadOnlyDictionary<long, long> documentationElementMap =
                sourceToTargetMemberIds;
            long? targetSheetId = null;
            string stage = "inspect documentation plan";
            try
            {
                if (!plan.HasDocumentation)
                {
                    evidence.Observations.Add("Documentation: None | physical duplication only.");
                    AssemblyDocumentationValidationService.Validate(
                        document,
                        source,
                        target,
                        plan,
                        targetViewIds,
                        targetSheetId,
                        sourceToTargetMemberIds,
                        evidence);
                    return new AssemblyDocumentationResult(targetViewIds, targetSheetId, evidence);
                }

                stage = "resolve documentation reference owners";
                documentationElementMap = BuildDocumentationElementMap(
                    document,
                    source,
                    target,
                    plan,
                    sourceToTargetMemberIds,
                    evidence);

                stage = "create target assembly views";
                RunTransaction(document, "Duplicate Assembly Documentation Views", () =>
                {
                    foreach (AssemblyDocumentationViewPlan viewPlan in plan.Views)
                    {
                        View targetView = CreateTargetView(document, target, viewPlan);
                        document.Regenerate();
                        ApplyViewSettings(document, source, target, targetView, viewPlan, evidence);
                        FinalizeTargetViewForAnnotations(
                            document, targetView, viewPlan, evidence);
                        targetViewIds.Add(viewPlan.SourceViewId, Id(targetView.Id));
                        evidence.Observations.Add(
                            "View " + viewPlan.SourceViewId + " -> " + Id(targetView.Id) +
                            " | " + viewPlan.Kind +
                            (viewPlan.Orientation.HasValue ? " | " + viewPlan.Orientation.Value : string.Empty));
                    }
                    document.Regenerate();
                });

                stage = "duplicate target assembly view annotations";
                RunTransaction(document, "Duplicate Assembly View Annotations", () =>
                {
                    // Changed by Jhay: reference-bearing model geometry must be
                    // current before target face enumeration begins.
                    document.Regenerate();
                    DuplicateViewAnnotations(
                        document,
                        source,
                        target,
                        plan,
                        targetViewIds,
                        documentationElementMap,
                        evidence);
                    document.Regenerate();
                });

                if (plan.Sheet != null)
                {
                    stage = "create target assembly sheet and layout";
                    RunTransaction(document, "Duplicate Assembly Documentation Sheet", () =>
                    {
                        ElementId titleBlockTypeId = RevitApiCompatibility.CreateElementId(
                            plan.Sheet.TitleBlockTypeId);
                        ViewSheet targetSheet = AssemblyViewUtils.CreateSheet(
                            document,
                            target.Id,
                            titleBlockTypeId);
                        document.Regenerate();
                        if (!string.IsNullOrWhiteSpace(plan.Sheet.TargetSheetNumber))
                            targetSheet.SheetNumber = plan.Sheet.TargetSheetNumber;
                        if (!string.IsNullOrWhiteSpace(plan.Sheet.TargetSheetName))
                            targetSheet.Name = plan.Sheet.TargetSheetName;
                        document.Regenerate();

                        foreach (AssemblyDocumentationViewportPlan viewportPlan in plan.Sheet.Viewports)
                        {
                            ElementId contentId = viewportPlan.IsReusableLegend
                                ? RevitApiCompatibility.CreateElementId(viewportPlan.SourceViewId)
                                : RevitApiCompatibility.CreateElementId(
                                    RequireMapped(targetViewIds, viewportPlan.SourceViewId));
                            if (!Viewport.CanAddViewToSheet(document, targetSheet.Id, contentId))
                            {
                                throw new InvalidOperationException(
                                    "View " + viewportPlan.SourceViewId + " cannot be placed on target sheet '" +
                                    targetSheet.SheetNumber + "'.");
                            }
                            Viewport viewport = Viewport.Create(
                                document,
                                targetSheet.Id,
                                contentId,
                                SheetPoint(viewportPlan.Center));
                            ApplyViewportSettings(viewport, viewportPlan);
                            targetViewportIds.Add(viewportPlan.SourceViewportId, Id(viewport.Id));
                            evidence.Observations.Add(
                                (viewportPlan.IsReusableLegend ? "Legend" : "Viewport") + " " +
                                viewportPlan.SourceViewId + " -> " + Id(viewport.Id) +
                                " | center " + viewportPlan.Center.Signature);
                        }

                        foreach (AssemblyDocumentationSchedulePlacementPlan schedulePlan in plan.Sheet.Schedules)
                        {
                            long scheduleId = schedulePlan.IsSourceAssemblyOwned
                                ? RequireMapped(targetViewIds, schedulePlan.SourceScheduleId)
                                : schedulePlan.SourceScheduleId;
                            ElementId targetScheduleId = RevitApiCompatibility.CreateElementId(scheduleId);
                            ScheduleSheetInstance instance = CreateSchedulePlacement(
                                document,
                                targetSheet.Id,
                                targetScheduleId,
                                schedulePlan.Point.ToXyz(),
                                schedulePlan.SegmentIndex);
                            instance.Rotation = schedulePlan.Rotation;
                            evidence.Observations.Add(
                                "Schedule " + schedulePlan.SourceScheduleId + " -> " + scheduleId +
                                " | instance " + Id(instance.Id) +
                                " | point " + schedulePlan.Point.Signature);
                        }

                        // Changed by Jhay: copy only positively classified, top-level
                        // sheet annotations. Group members travel with their group root.
                        List<ElementId> annotationRoots = plan.CategoryEItems
                            .Where(item => item.Disposition ==
                                AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot)
                            .Select(item => RevitApiCompatibility.CreateElementId(item.CopyRootId))
                            .Distinct()
                            .ToList();
                        if (annotationRoots.Count > 0)
                        {
                            ViewSheet sourceSheet = document.GetElement(
                                RevitApiCompatibility.CreateElementId(plan.Sheet.SourceSheetId)) as ViewSheet;
                            if (sourceSheet == null)
                            {
                                throw new InvalidOperationException(
                                    "Source assembly sheet " + plan.Sheet.SourceSheetId + " is unavailable.");
                            }
                            ICollection<ElementId> copiedAnnotationIds;
                            using (var options = new CopyPasteOptions())
                            {
                                copiedAnnotationIds = ElementTransformUtils.CopyElements(
                                    sourceSheet,
                                    annotationRoots,
                                    targetSheet,
                                    Transform.Identity,
                                    options);
                            }
                            evidence.Observations.Add(
                                "Sheet annotations | requested roots " + annotationRoots.Count +
                                " | copied elements " + copiedAnnotationIds.Count +
                                " | target IDs [" + string.Join(",", copiedAnnotationIds.Select(Id)) + "]");
                        }
                        document.Regenerate();
                        targetSheetId = Id(targetSheet.Id);
                    });

                    // Changed by Jhay: placed-view extents can change after Revit
                    // creates every viewport and sheet annotation. Recenter once,
                    // deterministically, after that final content regeneration.
                    stage = "finalize target assembly viewport positions";
                    RunTransaction(document, "Finalize Assembly Documentation Layout", () =>
                    {
                        document.Regenerate();
                        foreach (AssemblyDocumentationViewportPlan viewportPlan in plan.Sheet.Viewports)
                        {
                            if (!targetViewportIds.TryGetValue(
                                    viewportPlan.SourceViewportId,
                                    out long targetViewportId))
                            {
                                throw new InvalidOperationException(
                                    "No target viewport maps source viewport " +
                                    viewportPlan.SourceViewportId + ".");
                            }

                            Viewport viewport = document.GetElement(
                                RevitApiCompatibility.CreateElementId(targetViewportId)) as Viewport;
                            if (viewport == null || !viewport.IsValidObject)
                            {
                                throw new InvalidOperationException(
                                    "Mapped target viewport " + targetViewportId + " is unavailable.");
                            }

                            XYZ before = viewport.GetBoxCenter();
                            XYZ planned = SheetPoint(viewportPlan.Center);
                            bool recentered = !SameSheetPoint(before, planned);
                            if (recentered)
                                viewport.SetBoxCenter(planned);
                            evidence.Observations.Add(
                                "Final viewport recenter " + viewportPlan.SourceViewportId +
                                " -> " + targetViewportId + " | before " + SheetPoint(before) +
                                " | planned " + SheetPoint(planned) +
                                " | action " + (recentered ? "SetBoxCenter" : "none"));
                        }
                        document.Regenerate();
                    });
                }

                stage = "validate target assembly documentation";
                AssemblyDocumentationValidationService.Validate(
                    document,
                    source,
                    target,
                    plan,
                    targetViewIds,
                    targetSheetId,
                    documentationElementMap,
                    evidence);
                return new AssemblyDocumentationResult(targetViewIds, targetSheetId, evidence);
            }
            catch (Exception exception)
            {
                throw new AssemblyDocumentationException(stage, evidence, exception);
            }
        }

        private static void DuplicateViewAnnotations(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            AssemblyDocumentationPlan plan,
            IReadOnlyDictionary<long, long> targetViewIds,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            AssemblyDocumentationEvidence evidence)
        {
            foreach (AssemblyDocumentationViewPlan viewPlan in plan.Views.Where(view => !view.IsSchedule))
            {
                View sourceView = document.GetElement(
                    RevitApiCompatibility.CreateElementId(viewPlan.SourceViewId)) as View;
                View targetView = document.GetElement(
                    RevitApiCompatibility.CreateElementId(
                        RequireMapped(targetViewIds, viewPlan.SourceViewId))) as View;
                if (sourceView == null || targetView == null)
                {
                    throw new InvalidOperationException(
                        "The source or target view is unavailable while duplicating view annotations for " +
                        viewPlan.SourceViewId + ".");
                }

                List<ElementId> copyRoots = viewPlan.Annotations.Items
                    .Where(item => item.Kind ==
                        AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot)
                    .Select(item => RevitApiCompatibility.CreateElementId(item.CopyRootId))
                    .Distinct()
                    .ToList();
                if (copyRoots.Count > 0)
                {
                    ICollection<ElementId> copied;
                    using (var options = new CopyPasteOptions())
                    {
                        copied = ElementTransformUtils.CopyElements(
                            sourceView,
                            copyRoots,
                            targetView,
                            Transform.Identity,
                            options);
                    }
                    evidence.Observations.Add(
                        "VIEW ANNOTATIONS | view " + viewPlan.SourceViewId + " -> " +
                        Id(targetView.Id) + " | independent roots requested " + copyRoots.Count +
                        " | returned " + copied.Count + " target ids [" +
                        string.Join(",", copied.Select(Id)) + "]");
                }

                foreach (AssemblyDocumentationViewAnnotationItem item in viewPlan.Annotations.Items
                             .Where(annotation => annotation.Kind ==
                                 AssemblyDocumentationViewAnnotationKind.IndependentTag))
                {
                    CreateMappedTag(
                        document,
                        targetView,
                        item,
                        sourceToTargetMemberIds,
                        evidence);
                }

                foreach (AssemblyDocumentationViewAnnotationItem item in viewPlan.Annotations.Items
                             .Where(annotation => annotation.Kind ==
                                 AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension))
                {
                    CreateMappedDimension(
                        document,
                        source,
                        target,
                        targetView,
                        item,
                        sourceToTargetMemberIds,
                        evidence);
                }

                AssemblyDocumentationViewAnnotationItem unsupported = viewPlan.Annotations.Items
                    .FirstOrDefault(item => item.Kind == AssemblyDocumentationViewAnnotationKind.Unsupported ||
                        item.Kind == AssemblyDocumentationViewAnnotationKind.Dimension ||
                        item.Kind == AssemblyDocumentationViewAnnotationKind.SpotDimension ||
                        item.Kind == AssemblyDocumentationViewAnnotationKind.MultiReferenceAnnotation);
                if (unsupported != null)
                {
                    throw new InvalidOperationException(
                        "View annotation " + unsupported.ElementId + " (" + unsupported.RuntimeType +
                        ") has no approved duplication rule.");
                }
            }
        }

        // Changed by Jhay: documentation may reference a validated hosted
        // PipeInsulation dependency that sits outside the source assembly member
        // set. Extend the engine map only when host, type, thickness, ownership,
        // and transformed world bounds prove one unique target dependency.
        private static IReadOnlyDictionary<long, long> BuildDocumentationElementMap(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            AssemblyDocumentationPlan plan,
            IReadOnlyDictionary<long, long> physicalMap,
            AssemblyDocumentationEvidence evidence)
        {
            // Changed by Jhay: net48 has no Dictionary constructor accepting
            // IReadOnlyDictionary, so copy entries explicitly across Revit targets.
            var result = physicalMap.ToDictionary(item => item.Key, item => item.Value);
            IEnumerable<long> referencedIds = plan.Views
                .SelectMany(view => view.Annotations.Items)
                .SelectMany(item =>
                    (item.Tag?.References ?? Array.Empty<AssemblyDocumentationReferencePlan>())
                    .Concat(item.ReferenceAnnotation?.References ??
                        Array.Empty<AssemblyDocumentationReferencePlan>()))
                .Select(reference => reference.SourceElementId)
                .Distinct();
            Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
            var targetMemberIds = new HashSet<long>(target.GetMemberIds().Select(Id));
            foreach (long sourceId in referencedIds.Where(id => !result.ContainsKey(id)))
            {
                var sourceInsulation = document.GetElement(
                    RevitApiCompatibility.CreateElementId(sourceId)) as PipeInsulation;
                if (sourceInsulation == null || !physicalMap.TryGetValue(
                        Id(sourceInsulation.HostElementId), out long targetHostId))
                    continue;

                List<PipeInsulation> matches = target.GetMemberIds()
                    .Select(document.GetElement)
                    .OfType<PipeInsulation>()
                    .Where(candidate => Id(candidate.HostElementId) == targetHostId &&
                        Id(candidate.GetTypeId()) == Id(sourceInsulation.GetTypeId()) &&
                        Math.Abs(candidate.Thickness - sourceInsulation.Thickness) <=
                            ReferenceTolerance &&
                        BoundsMatch(
                            sourceInsulation.get_BoundingBox(null),
                            candidate.get_BoundingBox(null),
                            mapping))
                    .ToList();
                if (matches.Count != 1 || !targetMemberIds.Contains(Id(matches[0].Id)))
                {
                    throw new InvalidOperationException(
                        "Documentation source Pipe Insulation " + sourceId + " hosted by " +
                        Id(sourceInsulation.HostElementId) + " resolved to " + matches.Count +
                        " target dependencies on mapped host " + targetHostId +
                        "; exactly one target assembly member is required.");
                }
                result.Add(sourceId, Id(matches[0].Id));
                evidence.Observations.Add(
                    "DOCUMENTATION HOST DEPENDENCY | source Pipe Insulation " + sourceId +
                    " -> target " + Id(matches[0].Id) + " | host " +
                    Id(sourceInsulation.HostElementId) + " -> " + targetHostId +
                    " | type/thickness/world bounds PASS");
            }
            return new System.Collections.ObjectModel.ReadOnlyDictionary<long, long>(result);
        }

        private static bool BoundsMatch(
            BoundingBoxXYZ source,
            BoundingBoxXYZ target,
            Transform mapping)
        {
            if (source == null || target == null)
                return false;
            List<XYZ> expected = BoxCorners(source)
                .Select(mapping.OfPoint)
                .ToList();
            List<XYZ> actual = BoxCorners(target).ToList();
            XYZ expectedMin = new XYZ(
                expected.Min(point => point.X),
                expected.Min(point => point.Y),
                expected.Min(point => point.Z));
            XYZ expectedMax = new XYZ(
                expected.Max(point => point.X),
                expected.Max(point => point.Y),
                expected.Max(point => point.Z));
            XYZ actualMin = new XYZ(
                actual.Min(point => point.X),
                actual.Min(point => point.Y),
                actual.Min(point => point.Z));
            XYZ actualMax = new XYZ(
                actual.Max(point => point.X),
                actual.Max(point => point.Y),
                actual.Max(point => point.Z));
            return expectedMin.DistanceTo(actualMin) <= ReferenceTolerance &&
                expectedMax.DistanceTo(actualMax) <= ReferenceTolerance;
        }

        private static IEnumerable<XYZ> BoxCorners(BoundingBoxXYZ box)
        {
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            foreach (double z in new[] { box.Min.Z, box.Max.Z })
                yield return box.Transform.OfPoint(new XYZ(x, y, z));
        }

        // Changed by Jhay: resolve dimension references only after the locked
        // physical engine has supplied its proven source-to-target member map.
        private static void CreateMappedDimension(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            View targetView,
            AssemblyDocumentationViewAnnotationItem item,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            AssemblyDocumentationEvidence evidence)
        {
            AssemblyDocumentationReferenceAnnotationPlan plan = item.ReferenceAnnotation;
            if (plan == null ||
                plan.LineIsBound &&
                    (plan.LineStartInView == null || plan.LineEndInView == null) ||
                !plan.LineIsBound &&
                    (plan.LineOriginInView == null || plan.LineDirectionInView == null))
                throw new InvalidOperationException(
                    "Deferred dimension " + item.ElementId + " has no complete line plan.");

            Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
            var targetReferences = new ReferenceArray();
            var mappings = new List<string>();
            foreach (AssemblyDocumentationReferencePlan sourceReference in plan.References)
            {
                if (!sourceToTargetMemberIds.TryGetValue(
                        sourceReference.SourceElementId,
                        out long targetElementId))
                {
                    throw new InvalidOperationException(
                        "Dimension " + item.ElementId + " references source element " +
                        sourceReference.SourceElementId + " without a proven target mapping.");
                }
                Element targetElement = document.GetElement(
                    RevitApiCompatibility.CreateElementId(targetElementId));
                if (targetElement == null)
                    throw new InvalidOperationException(
                        "Mapped dimension target " + targetElementId + " is unavailable.");

                Reference targetReference = ResolveTargetReference(
                    document,
                    targetView,
                    targetElement,
                    sourceReference,
                    mapping,
                    item.ElementId,
                    evidence);
                targetReferences.Append(targetReference);
                mappings.Add(
                    sourceReference.SourceElementId + " -> " + targetElementId +
                    " | " + sourceReference.Semantic.Kind);
            }

            Line line = plan.LineIsBound
                ? Line.CreateBound(
                    ToWorldPoint(targetView, plan.LineStartInView),
                    ToWorldPoint(targetView, plan.LineEndInView))
                : Line.CreateUnbound(
                    ToWorldPoint(targetView, plan.LineOriginInView),
                    ToWorldVector(targetView, plan.LineDirectionInView).Normalize());
            DimensionType dimensionType = document.GetElement(
                RevitApiCompatibility.CreateElementId(item.TypeId)) as DimensionType;
            if (dimensionType == null)
                throw new InvalidOperationException(
                    "Dimension type " + item.TypeId + " is unavailable for source " +
                    item.ElementId + ".");
            Dimension targetDimension = document.Create.NewDimension(
                targetView, line, targetReferences, dimensionType);
            if (targetDimension == null)
                throw new InvalidOperationException(
                    "Revit did not create mapped dimension for source " + item.ElementId + ".");
            ApplyDimensionFormatting(targetDimension, plan.Formatting);
            evidence.Observations.Add(
                "DEFERRED DIMENSION | source " + item.ElementId + " -> target " +
                Id(targetDimension.Id) + " | target view " + Id(targetView.Id) +
                " | references " + string.Join("; ", mappings));
        }

        private static Reference ResolveTargetReference(
            Document document,
            View targetView,
            Element targetElement,
            AssemblyDocumentationReferencePlan sourceReference,
            Transform mapping,
            long sourceDimensionId,
            AssemblyDocumentationEvidence evidence)
        {
            AssemblyDocumentationReferenceSemantic semantic = sourceReference.Semantic;
            if (semantic == null)
                throw new InvalidOperationException(
                    "Dimension " + sourceDimensionId + " source reference on element " +
                    sourceReference.SourceElementId + " has no semantic plan.");

            List<Reference> matches;
            if (semantic.Kind == AssemblyDocumentationReferenceSemanticKind.FamilyReference)
            {
                FamilyInstance family = targetElement as FamilyInstance;
                if (family == null || !Enum.TryParse(
                        semantic.FamilyReferenceType,
                        out FamilyInstanceReferenceType referenceType))
                {
                    throw new InvalidOperationException(
                        "Dimension " + sourceDimensionId + " expected family reference " +
                        semantic.FamilyReferenceType + " on mapped target " + Id(targetElement.Id) + ".");
                }
                IList<Reference> candidates = family.GetReferences(referenceType);
                matches = (candidates ?? new List<Reference>())
                    .Where(candidate => string.IsNullOrEmpty(semantic.FamilyReferenceName) ||
                        string.Equals(
                            TryGet(() => family.GetReferenceName(candidate), string.Empty),
                            semantic.FamilyReferenceName,
                            StringComparison.Ordinal))
                    .Where(candidate => semantic.OriginOrPoint == null ||
                        IsMappedPlanarReference(
                            family,
                            candidate,
                            semantic,
                            mapping))
                    .GroupBy(candidate => TryGet(
                        () => candidate.ConvertToStableRepresentation(document),
                        string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
            }
            else if (semantic.Kind == AssemblyDocumentationReferenceSemanticKind.ElementPoint)
            {
                XYZ expected = mapping.OfPoint(semantic.OriginOrPoint.ToXyz());
                var candidates = new List<Reference>();
                if (!string.IsNullOrEmpty(semantic.StableSemanticPath))
                {
                    Reference structured = TryGet<Reference>(
                        () => Reference.ParseFromStableRepresentation(
                            document,
                            targetElement.UniqueId + semantic.StableSemanticPath),
                        null);
                    if (structured != null)
                        candidates.Add(structured);
                }
                else
                {
                    candidates.Add(new Reference(targetElement));
                }
                matches = candidates
                    .Where(candidate => Id(candidate.ElementId) == Id(targetElement.Id) &&
                        candidate.ElementReferenceType ==
                            ElementReferenceType.REFERENCE_TYPE_NONE)
                    .Where(candidate =>
                    {
                        GeometryObject geometry = TryGet<GeometryObject>(
                            () => targetElement.GetGeometryObjectFromReference(candidate), null);
                        return geometry is Point point &&
                            point.Coord.DistanceTo(expected) <= ReferenceTolerance;
                    })
                    .GroupBy(candidate => TryGet(
                        () => candidate.ConvertToStableRepresentation(document),
                        string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();
            }
            else if (semantic.Kind == AssemblyDocumentationReferenceSemanticKind.LinearCurve)
            {
                matches = EnumerateGeometryReferences(targetElement, targetView)
                    .Where(candidate => candidate.Item1.ElementReferenceType ==
                        ElementReferenceType.REFERENCE_TYPE_LINEAR &&
                        candidate.Item2 is Line)
                    .Where(candidate => IsMappedLinearCurve(
                        (Line)candidate.Item2,
                        semantic,
                        mapping))
                    .GroupBy(candidate => TryGet(
                        () => candidate.Item1.ConvertToStableRepresentation(document),
                        string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First().Item1)
                    .ToList();
            }
            else
            {
                XYZ expectedOrigin = mapping.OfPoint(semantic.OriginOrPoint.ToXyz());
                XYZ expectedNormal = mapping.OfVector(semantic.Normal.ToXyz()).Normalize();
                bool useCompleteModelGeometry = targetElement is PipeInsulation;
                List<Tuple<Reference, GeometryObject>> candidates =
                    EnumerateGeometryReferences(
                            targetElement,
                            targetView,
                            useCompleteModelGeometry)
                        .ToList();
                if (useCompleteModelGeometry)
                {
                    AppendPipeInsulationReferenceCandidateEvidence(
                        document,
                        targetElement,
                        candidates,
                        expectedOrigin,
                        expectedNormal,
                        semantic,
                        evidence);
                }
                matches = candidates
                    .Where(candidate => IsMatchingPlanarSurface(
                        candidate, expectedOrigin, expectedNormal, semantic))
                    .GroupBy(candidate => TryGet(
                        () => candidate.Item1.ConvertToStableRepresentation(document),
                        string.Empty), StringComparer.Ordinal)
                    .Select(group => group.First().Item1)
                    .ToList();
            }

            if (matches.Count != 1)
            {
                throw new InvalidOperationException(
                    "Dimension " + sourceDimensionId + " reference on source element " +
                    sourceReference.SourceElementId + " resolved to " + matches.Count +
                    " target references on mapped element " + Id(targetElement.Id) +
                    "; exactly one is required.");
            }
            return matches[0];
        }

        private static bool IsMappedLinearCurve(
            Line line,
            AssemblyDocumentationReferenceSemantic semantic,
            Transform mapping)
        {
            if (!line.IsBound || semantic.OriginOrPoint == null || semantic.Normal == null)
                return false;
            XYZ expectedStart = mapping.OfPoint(semantic.OriginOrPoint.ToXyz());
            XYZ expectedDirection = mapping.OfVector(semantic.Normal.ToXyz()).Normalize();
            XYZ expectedEnd = expectedStart + expectedDirection * semantic.Area;
            XYZ actualStart = line.GetEndPoint(0);
            XYZ actualEnd = line.GetEndPoint(1);
            bool forward = actualStart.DistanceTo(expectedStart) <= ReferenceTolerance &&
                actualEnd.DistanceTo(expectedEnd) <= ReferenceTolerance;
            bool reverse = actualStart.DistanceTo(expectedEnd) <= ReferenceTolerance &&
                actualEnd.DistanceTo(expectedStart) <= ReferenceTolerance;
            return (forward || reverse) &&
                Math.Abs(line.Length - semantic.Area) <= ReferenceTolerance;
        }

        private static bool IsMappedPlanarReference(
            Element element,
            Reference reference,
            AssemblyDocumentationReferenceSemantic semantic,
            Transform mapping)
        {
            GeometryObject geometry = TryGet<GeometryObject>(
                () => element.GetGeometryObjectFromReference(reference), null);
            if (!(geometry is PlanarFace face) || semantic.Normal == null)
                return false;
            XYZ expectedOrigin = mapping.OfPoint(semantic.OriginOrPoint.ToXyz());
            XYZ expectedNormal = mapping.OfVector(semantic.Normal.ToXyz()).Normalize();
            return face.Origin.DistanceTo(expectedOrigin) <= ReferenceTolerance &&
                face.FaceNormal.Normalize().DistanceTo(expectedNormal) <= ReferenceTolerance &&
                Math.Abs(face.Area - semantic.Area) <= ReferenceTolerance &&
                string.Equals(
                    CapturePlanarTopology(face),
                    semantic.TopologySignature,
                    StringComparison.Ordinal);
        }

        private static IEnumerable<Tuple<Reference, GeometryObject>> EnumerateGeometryReferences(
            Element element,
            View view,
            bool completeModelGeometry = false)
        {
            var result = new List<Tuple<Reference, GeometryObject>>();
            var options = new Options
            {
                ComputeReferences = true,
                IncludeNonVisibleObjects = true
            };
            // Changed by Jhay: a target assembly view can suppress valid
            // PipeInsulation faces. Model-reference resolution therefore omits
            // the view filter for PipeInsulation while existing element cases
            // retain their proven view-specific geometry context.
            if (!completeModelGeometry)
                options.View = view;
            CollectGeometryReferences(element.get_Geometry(options), result);
            return result;
        }

        private static bool IsMatchingPlanarSurface(
            Tuple<Reference, GeometryObject> candidate,
            XYZ expectedOrigin,
            XYZ expectedNormal,
            AssemblyDocumentationReferenceSemantic semantic)
        {
            if (candidate.Item1.ElementReferenceType !=
                    ElementReferenceType.REFERENCE_TYPE_SURFACE ||
                !(candidate.Item2 is PlanarFace face))
                return false;
            return face.Origin.DistanceTo(expectedOrigin) <= ReferenceTolerance &&
                face.FaceNormal.Normalize().DistanceTo(expectedNormal) <= ReferenceTolerance &&
                Math.Abs(face.Area - semantic.Area) <= ReferenceTolerance &&
                string.Equals(
                    CapturePlanarTopology(face),
                    semantic.TopologySignature,
                    StringComparison.Ordinal);
        }

        private static void AppendPipeInsulationReferenceCandidateEvidence(
            Document document,
            Element targetElement,
            IEnumerable<Tuple<Reference, GeometryObject>> candidates,
            XYZ expectedOrigin,
            XYZ expectedNormal,
            AssemblyDocumentationReferenceSemantic semantic,
            AssemblyDocumentationEvidence evidence)
        {
            List<Tuple<Reference, GeometryObject>> values = candidates.ToList();
            if (values.Count == 0)
            {
                evidence.Observations.Add(
                    "TARGET REFERENCE CANDIDATE | target PipeInsulation " +
                    Id(targetElement.Id) + " | <none returned by complete model geometry>" +
                    " | expected origin " + Point(expectedOrigin) +
                    " | expected normal " + Point(expectedNormal) +
                    " | expected area " + Format(semantic.Area));
                return;
            }
            foreach (Tuple<Reference, GeometryObject> candidate in values)
            {
                string stable = TryGet(
                    () => candidate.Item1.ConvertToStableRepresentation(document), string.Empty);
                string geometry = candidate.Item2?.GetType().FullName ?? "<none>";
                string details;
                string decision;
                if (!(candidate.Item2 is PlanarFace face))
                {
                    details = "origin <not-planar> | normal <not-planar> | area " +
                        (candidate.Item2 is Face otherFace
                            ? Format(otherFace.Area)
                            : "<not-face>");
                    decision = "REJECT geometry is not PlanarFace";
                }
                else
                {
                    double originDistance = face.Origin.DistanceTo(expectedOrigin);
                    double normalDistance = face.FaceNormal.Normalize().DistanceTo(expectedNormal);
                    double areaDifference = Math.Abs(face.Area - semantic.Area);
                    bool topologyMatches = string.Equals(
                        CapturePlanarTopology(face),
                        semantic.TopologySignature,
                        StringComparison.Ordinal);
                    details = "origin " + Point(face.Origin) + " | normal " +
                        Point(face.FaceNormal) + " | area " + Format(face.Area) +
                        " | expected origin " + Point(expectedOrigin) +
                        " | expected normal " + Point(expectedNormal) +
                        " | expected area " + Format(semantic.Area);
                    decision = IsMatchingPlanarSurface(
                            candidate, expectedOrigin, expectedNormal, semantic)
                        ? "ACCEPT unique semantic candidate"
                        : "REJECT originDistance=" + Format(originDistance) +
                          ", normalDistance=" + Format(normalDistance) +
                          ", areaDifference=" + Format(areaDifference) +
                          ", topologyMatches=" + topologyMatches;
                }
                evidence.Observations.Add(
                    "TARGET REFERENCE CANDIDATE | target PipeInsulation " +
                    Id(targetElement.Id) + " | stable " + stable + " | " +
                    candidate.Item1.ElementReferenceType + " | geometry " + geometry +
                    " | " + details + " | " + decision);
            }
        }

        private static void CollectGeometryReferences(
            GeometryElement geometry,
            ICollection<Tuple<Reference, GeometryObject>> references)
        {
            if (geometry == null)
                return;
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                        if (face.Reference != null)
                            references.Add(Tuple.Create(face.Reference, (GeometryObject)face));
                    foreach (Edge edge in solid.Edges)
                        if (edge.Reference != null)
                            references.Add(Tuple.Create(edge.Reference, (GeometryObject)edge));
                }
                else if (item is GeometryInstance instance)
                {
                    CollectGeometryReferences(instance.GetInstanceGeometry(), references);
                }
                else if (item is Curve curve && curve.Reference != null)
                {
                    references.Add(Tuple.Create(curve.Reference, item));
                }
            }
        }

        private static string CapturePlanarTopology(PlanarFace face) =>
            string.Join(",", face.EdgeLoops.Cast<EdgeArray>()
                .Select(loop => loop.Size)
                .OrderBy(size => size));

        private static string Point(XYZ point) => point == null
            ? "<null>"
            : Format(point.X) + "," + Format(point.Y) + "," + Format(point.Z);

        private static string Format(double value) =>
            value.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);

        private static void ApplyDimensionFormatting(
            Dimension dimension,
            IReadOnlyDictionary<string, string> formatting)
        {
            foreach (KeyValuePair<string, string> item in formatting)
            {
                System.Reflection.PropertyInfo property = dimension.GetType().GetProperty(item.Key);
                if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    object value = property.PropertyType == typeof(bool)
                        ? (object)bool.Parse(item.Value)
                        : item.Value;
                    property.SetValue(dimension, value, null);
                }
                catch (Exception)
                {
                    // Changed by Jhay: final validation remains strict when Revit
                    // rejects an otherwise readable formatting property.
                }
            }
        }

        // Changed by Jhay: finalize and lock an annotation-bearing View3D only
        // after its source-equivalent orientation, section box, template, and
        // other view settings have been applied. Annotation creation runs later.
        private static void FinalizeTargetViewForAnnotations(
            Document document,
            View targetView,
            AssemblyDocumentationViewPlan plan,
            AssemblyDocumentationEvidence evidence)
        {
            if (!(targetView is View3D target3D))
                return;
            document.Regenerate();
            if (plan.Target3DOrientationShouldBeLocked && !target3D.IsLocked)
            {
                target3D.SaveOrientationAndLock();
                document.Regenerate();
            }
            evidence.Observations.Add(
                "VIEW3D ORIENTATION LOCK | source view " + plan.SourceViewId +
                " source locked=" + plan.Source3DOrientationLocked +
                " | annotations require lock=" + plan.RequiresLocked3DOrientation +
                " | target view " + Id(target3D.Id) +
                " target locked=" + target3D.IsLocked);
        }

        private static void CreateMappedTag(
            Document document,
            View targetView,
            AssemblyDocumentationViewAnnotationItem item,
            IReadOnlyDictionary<long, long> sourceToTargetMemberIds,
            AssemblyDocumentationEvidence evidence)
        {
            AssemblyDocumentationTagPlan plan = item.Tag;
            AssemblyDocumentationReferencePlan sourceReference = plan.References.Single();
            if (!sourceToTargetMemberIds.TryGetValue(
                    sourceReference.SourceElementId,
                    out long targetElementId))
            {
                throw new InvalidOperationException(
                    "Tag " + item.ElementId + " references source element " +
                    sourceReference.SourceElementId + " without a proven target mapping.");
            }
            Element targetElement = document.GetElement(
                RevitApiCompatibility.CreateElementId(targetElementId));
            if (targetElement == null)
                throw new InvalidOperationException("Mapped tag target " + targetElementId + " is unavailable.");

            var targetReference = new Reference(targetElement);
            XYZ head = ToWorldPoint(targetView, plan.HeadInView);
            IndependentTag targetTag = IndependentTag.Create(
                document,
                RevitApiCompatibility.CreateElementId(plan.TypeId),
                targetView.Id,
                targetReference,
                plan.HasLeader,
                plan.Orientation,
                head);
            if (targetTag == null)
                throw new InvalidOperationException("Revit did not create mapped tag for source " + item.ElementId + ".");

            targetTag.TagHeadPosition = head;
            TrySetTagRotation(targetTag, plan.RotationAngle);
            if (plan.HasLeader)
            {
                if (targetTag.CanLeaderEndConditionBeAssigned(plan.LeaderEndCondition))
                    targetTag.LeaderEndCondition = plan.LeaderEndCondition;
                if (sourceReference.LeaderElbowInView != null)
#if REVIT2022_OR_GREATER
                    targetTag.SetLeaderElbow(
                        targetReference,
                        ToWorldPoint(targetView, sourceReference.LeaderElbowInView));
#else
                    targetTag.LeaderElbow =
                        ToWorldPoint(targetView, sourceReference.LeaderElbowInView);
#endif
                if (sourceReference.LeaderEndInView != null &&
                    plan.LeaderEndCondition == LeaderEndCondition.Free)
                {
#if REVIT2022_OR_GREATER
                    targetTag.SetLeaderEnd(
                        targetReference,
                        ToWorldPoint(targetView, sourceReference.LeaderEndInView));
#else
                    targetTag.LeaderEnd =
                        ToWorldPoint(targetView, sourceReference.LeaderEndInView);
#endif
                }
            }
            evidence.Observations.Add(
                "VIEW TAG | source tag " + item.ElementId + " -> target tag " + Id(targetTag.Id) +
                " | source member " + sourceReference.SourceElementId + " -> target member " +
                targetElementId + " | target view " + Id(targetView.Id));
        }

        private static void TrySetTagRotation(IndependentTag tag, double rotation)
        {
#if REVIT2022_OR_GREATER
            try { tag.RotationAngle = rotation; }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException)
            {
                // Revit restricts rotation for some tag orientations; strict
                // validation below still verifies the resulting value.
            }
#else
            // Changed by Jhay: IndependentTag.RotationAngle is unavailable in
            // Revit 2021; its supported horizontal/vertical orientation is set
            // during IndependentTag.Create and remains strictly validated.
#endif
        }

        private static XYZ ToWorldPoint(
            View view,
            AssemblyDocumentationXyzSnapshot point) =>
            view.Origin + view.RightDirection * point.X + view.UpDirection * point.Y +
            view.ViewDirection * point.Z;

        private static XYZ ToWorldVector(
            View view,
            AssemblyDocumentationXyzSnapshot vector) =>
            view.RightDirection * vector.X + view.UpDirection * vector.Y +
            view.ViewDirection * vector.Z;

        private static View CreateTargetView(
            Document document,
            AssemblyInstance target,
            AssemblyDocumentationViewPlan plan)
        {
            ElementId templateId = RevitApiCompatibility.CreateElementId(plan.TemplateId);
            bool hasTemplate = !RevitApiCompatibility.IsInvalidElementId(templateId);
            switch (plan.Kind)
            {
                case AssemblyDocumentationViewKind.Orthographic3D:
                    return hasTemplate
                        ? AssemblyViewUtils.Create3DOrthographic(document, target.Id, templateId, true)
                        : AssemblyViewUtils.Create3DOrthographic(document, target.Id);
                case AssemblyDocumentationViewKind.DetailSection:
                    if (!plan.Orientation.HasValue)
                        throw new InvalidOperationException("A detail-section orientation was not confirmed.");
                    return hasTemplate
                        ? AssemblyViewUtils.CreateDetailSection(
                            document, target.Id, plan.Orientation.Value, templateId, true)
                        : AssemblyViewUtils.CreateDetailSection(document, target.Id, plan.Orientation.Value);
                case AssemblyDocumentationViewKind.PartList:
                    return hasTemplate
                        ? AssemblyViewUtils.CreatePartList(document, target.Id, templateId, true)
                        : AssemblyViewUtils.CreatePartList(document, target.Id);
                case AssemblyDocumentationViewKind.MaterialTakeoff:
                    return hasTemplate
                        ? AssemblyViewUtils.CreateMaterialTakeoff(document, target.Id, templateId, true)
                        : AssemblyViewUtils.CreateMaterialTakeoff(document, target.Id);
                case AssemblyDocumentationViewKind.SingleCategorySchedule:
                    ElementId categoryId = RevitApiCompatibility.CreateElementId(plan.ScheduleCategoryId);
                    return hasTemplate
                        ? AssemblyViewUtils.CreateSingleCategorySchedule(
                            document, target.Id, categoryId, templateId, true)
                        : AssemblyViewUtils.CreateSingleCategorySchedule(document, target.Id, categoryId);
                default:
                    throw new InvalidOperationException("Unsupported documentation view kind " + plan.Kind + ".");
            }
        }

        private static void ApplyViewSettings(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            View targetView,
            AssemblyDocumentationViewPlan plan,
            AssemblyDocumentationEvidence evidence)
        {
            if (!string.IsNullOrWhiteSpace(plan.TargetName))
                targetView.Name = plan.TargetName;

            // Changed by Jhay: a view template may be assigned without controlling
            // these settings. Always attempt the source value and let Revit reject
            // only properties that the template actually controls.
            TryApply(() => targetView.Scale = plan.Scale, "scale", plan, evidence);
            TryApply(() => targetView.DetailLevel = plan.DetailLevel, "detail level", plan, evidence);
            TryApply(() => targetView.Discipline = plan.Discipline, "discipline", plan, evidence);
            document.Regenerate();

            if (targetView is View3D target3D)
            {
                Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
                if (plan.EyePosition != null && plan.UpDirection != null && plan.ViewDirection != null)
                {
                    target3D.SetOrientation(new ViewOrientation3D(
                        mapping.OfPoint(plan.EyePosition.ToXyz()),
                        mapping.OfVector(plan.UpDirection.ToXyz()),
                        mapping.OfVector(-plan.ViewDirection.ToXyz())));
                }
                if (plan.SectionBox != null)
                {
                    BoundingBoxXYZ section = plan.SectionBox.ToBoundingBox();
                    section.Transform = mapping.Multiply(section.Transform);
                    target3D.SetSectionBox(section);
                    target3D.IsSectionBoxActive = plan.SectionBoxActive;
                }
            }
            else if (plan.CropBox != null)
            {
                BoundingBoxXYZ sourceCrop = plan.CropBox.ToBoundingBox();
                Transform mapping = target.GetTransform().Multiply(source.GetTransform().Inverse);
                targetView.CropBox = new BoundingBoxXYZ
                {
                    Min = sourceCrop.Min,
                    Max = sourceCrop.Max,
                    Transform = mapping.Multiply(sourceCrop.Transform)
                };
                targetView.CropBoxActive = plan.CropBoxActive;
                targetView.CropBoxVisible = plan.CropBoxVisible;
            }
            document.Regenerate();
        }

        private static void ApplyViewportSettings(
            Viewport viewport,
            AssemblyDocumentationViewportPlan plan)
        {
            ElementId typeId = RevitApiCompatibility.CreateElementId(plan.ViewportTypeId);
            if (!RevitApiCompatibility.IsInvalidElementId(typeId) && viewport.IsValidType(typeId))
                viewport.ChangeTypeId(typeId);
            viewport.Rotation = plan.Rotation;
            if (plan.LabelOffset != null)
                ApplyOptionalProperty(viewport, "LabelOffset", plan.LabelOffset.ToXyz());
            if (!double.IsNaN(plan.LabelLineLength))
                ApplyOptionalProperty(viewport, "LabelLineLength", plan.LabelLineLength);
            ApplyOptionalEnumProperty(viewport, "ViewportPositioning", plan.Positioning);
            viewport.SetBoxCenter(SheetPoint(plan.Center));
        }

        private static XYZ SheetPoint(AssemblyDocumentationXyzSnapshot point) =>
            new XYZ(point.X, point.Y, 0.0);

        private static bool SameSheetPoint(XYZ left, XYZ right) =>
            left != null && right != null &&
            Math.Abs(left.X - right.X) <= SheetPositionTolerance &&
            Math.Abs(left.Y - right.Y) <= SheetPositionTolerance;

        private static string SheetPoint(XYZ point) => point == null
            ? "<null>"
            : point.X.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "," +
              point.Y.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);

        private static ScheduleSheetInstance CreateSchedulePlacement(
            Document document,
            ElementId sheetId,
            ElementId scheduleId,
            XYZ point,
            int segmentIndex)
        {
            if (segmentIndex >= 0)
            {
                System.Reflection.MethodInfo segmentedCreate = typeof(ScheduleSheetInstance)
                    .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
                    .FirstOrDefault(method => method.Name == "Create" &&
                        method.GetParameters().Length == 5);
                if (segmentedCreate != null)
                {
                    return (ScheduleSheetInstance)segmentedCreate.Invoke(
                        null,
                        new object[] { document, sheetId, scheduleId, point, segmentIndex });
                }
            }
            return ScheduleSheetInstance.Create(document, sheetId, scheduleId, point);
        }

        private static void ApplyOptionalProperty(
            object instance,
            string propertyName,
            object value)
        {
            System.Reflection.PropertyInfo property = instance?.GetType().GetProperty(propertyName);
            if (property?.CanWrite == true)
                property.SetValue(instance, value, null);
        }

        private static void ApplyOptionalEnumProperty(
            object instance,
            string propertyName,
            string plannedValue)
        {
            if (instance == null || string.IsNullOrWhiteSpace(plannedValue) ||
                string.Equals(plannedValue, "<unsupported>", StringComparison.Ordinal))
                return;
            System.Reflection.PropertyInfo property = instance.GetType().GetProperty(propertyName);
            if (property == null || !property.CanWrite || !property.PropertyType.IsEnum)
                return;
            object value = Enum.Parse(property.PropertyType, plannedValue, false);
            property.SetValue(instance, value, null);
        }

        private static void TryApply(
            Action setter,
            string property,
            AssemblyDocumentationViewPlan plan,
            AssemblyDocumentationEvidence evidence)
        {
            try { setter(); }
            catch (Exception exception)
            {
                evidence.Observations.Add(
                    "INFO | View " + plan.SourceViewId + " " + property +
                    " could not be assigned directly: " + exception.Message +
                    ". Strict post-create validation remains enabled.");
            }
        }

        private static long RequireMapped(IReadOnlyDictionary<long, long> mapping, long sourceId)
        {
            if (!mapping.TryGetValue(sourceId, out long targetId))
                throw new InvalidOperationException("No target documentation view maps source view " + sourceId + ".");
            return targetId;
        }

        private static void RunTransaction(Document document, string name, Action action)
        {
            using (var transaction = new Transaction(document, name))
            {
                TransactionStatus started = transaction.Start();
                if (started != TransactionStatus.Started)
                    throw new InvalidOperationException("Transaction '" + name + "' did not start: " + started + ".");
                try
                {
                    action();
                    TransactionStatus committed = transaction.Commit();
                    if (committed != TransactionStatus.Committed)
                        throw new InvalidOperationException("Transaction '" + name + "' did not commit: " + committed + ".");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                        transaction.RollBack();
                    throw;
                }
            }
        }

        private static T TryGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch (Exception) { return fallback; }
        }

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
