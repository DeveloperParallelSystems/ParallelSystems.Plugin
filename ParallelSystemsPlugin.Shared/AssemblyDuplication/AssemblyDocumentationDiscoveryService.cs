using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Changed by Jhay: read-only discovery produces the confirmed documentation contract.
    internal static class AssemblyDocumentationDiscoveryService
    {
        private const double DirectionTolerance = 1e-6;

        public static AssemblyDocumentationDiscoveryResult Discover(
            Document document,
            AssemblyInstance source,
            string targetAssemblyName)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            var issues = new List<string>();
            long sourceId = Id(source.Id);
            Transform sourceTransform = source.GetTransform();
            List<View> owned = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && Id(view.AssociatedAssemblyInstanceId) == sourceId)
                .OrderBy(view => Id(view.Id))
                .ToList();

            List<ViewSheet> sheets = owned.OfType<ViewSheet>().ToList();
            var ownedViewIds = new HashSet<long>(owned.Select(view => Id(view.Id)));
            if (sheets.Count > 1)
            {
                issues.Add("Source assembly '" + source.AssemblyTypeName + "' owns " +
                    sheets.Count + " assembly sheets; the bounded documentation phase supports exactly one.");
            }

            var viewPlans = new List<AssemblyDocumentationViewPlan>();
            foreach (View view in owned.Where(view => !(view is ViewSheet)))
            {
                AssemblyDocumentationViewPlan plan = CaptureView(
                    document,
                    source,
                    targetAssemblyName,
                    sourceTransform,
                    view,
                    issues);
                if (plan != null)
                    viewPlans.Add(plan);
            }

            AssemblyDocumentationSheetPlan sheetPlan = sheets.Count == 1
                ? CaptureSheet(
                    document,
                    source,
                    targetAssemblyName,
                    sheets[0],
                    ownedViewIds,
                    issues)
                : null;
            IReadOnlyList<AssemblyDocumentationCategoryEItem> categoryE = sheets.Count == 1
                ? CaptureCategoryE(document, sheets[0])
                : new List<AssemblyDocumentationCategoryEItem>().AsReadOnly();
            List<AssemblyDocumentationCategoryEItem> unsupportedCategoryE = categoryE
                .Where(item => item.Disposition ==
                    AssemblyDocumentationSheetOwnedItemDisposition.Unsupported)
                .ToList();
            if (unsupportedCategoryE.Count > 0)
            {
                issues.Add(
                    "Source assembly '" + source.AssemblyTypeName + "' has unsupported Category E " +
                    "sheet-owned elements: " + string.Join(", ", unsupportedCategoryE.Select(item =>
                        item.ElementId + " | " + item.RuntimeType + " | " + item.CategoryName +
                        " | owner " + item.OwnerViewId)) + ". No model changes are permitted.");
            }

            var planResult = new AssemblyDocumentationPlan(
                sourceId,
                source.AssemblyTypeName,
                targetAssemblyName,
                viewPlans,
                sheetPlan,
                categoryE,
                TransformSignature(sourceTransform));
            return new AssemblyDocumentationDiscoveryResult(planResult, issues);
        }

        public static bool Matches(
            AssemblyDocumentationPlan confirmed,
            AssemblyDocumentationPlan fresh,
            out string reason)
        {
            if (confirmed == null || fresh == null)
            {
                reason = "The documentation plan is unavailable.";
                return false;
            }
            if (!string.Equals(confirmed.Signature, fresh.Signature, StringComparison.Ordinal))
            {
                reason = "The source documentation ownership, settings, layout, or naming plan has changed.";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private static AssemblyDocumentationViewPlan CaptureView(
            Document document,
            AssemblyInstance source,
            string targetAssemblyName,
            Transform sourceTransform,
            View view,
            ICollection<string> issues)
        {
            AssemblyDocumentationViewKind kind;
            AssemblyDetailViewOrientation? orientation = null;
            long scheduleCategoryId = Id(ElementId.InvalidElementId);
            AssemblyDocumentationScheduleDefinitionPlan scheduleDefinition = null;

            if (view is View3D view3D && view.ViewType == ViewType.ThreeD && !view3D.IsPerspective)
            {
                kind = AssemblyDocumentationViewKind.Orthographic3D;
            }
            else if (view is ViewSection)
            {
                if (!TryClassifyOrientation(view, sourceTransform, out AssemblyDetailViewOrientation classified))
                {
                    XYZ localDirection = sourceTransform.Inverse
                        .OfVector(-view.ViewDirection).Normalize();
                    issues.Add("Assembly view " + Id(view.Id) + " '" + view.Name +
                        "' has ambiguous geometry-relative orientation. ViewType=" + view.ViewType +
                        ", world ViewDirection=" + Vector(view.ViewDirection) +
                        ", assembly-local looking direction=" + Vector(localDirection) +
                        ". View names were not used as a fallback.");
                    return null;
                }
                kind = AssemblyDocumentationViewKind.DetailSection;
                orientation = classified;
            }
            else if (view is ViewSchedule schedule)
            {
                if (!TryClassifySchedule(schedule, out kind, out scheduleCategoryId, out string scheduleIssue))
                {
                    issues.Add("Assembly schedule " + Id(view.Id) + " '" + view.Name +
                        "' is unsupported: " + scheduleIssue);
                    return null;
                }
                scheduleDefinition = new AssemblyDocumentationScheduleDefinitionPlan(
                    CaptureScheduleDefinitionSignature(schedule));
            }
            else
            {
                issues.Add("Assembly-owned view " + Id(view.Id) + " '" + view.Name +
                    "' has unsupported runtime/view type " + view.GetType().FullName + " / " +
                    view.ViewType + ".");
                return null;
            }

            string targetName = ReplaceExactToken(
                view.Name,
                source.AssemblyTypeName,
                targetAssemblyName,
                "view " + Id(view.Id),
                issues);

            bool cropActive = false;
            bool cropVisible = false;
            AssemblyDocumentationBoundingBoxSnapshot crop = null;
            if (!(view is ViewSchedule))
            {
                cropActive = TryGet(() => view.CropBoxActive, false);
                cropVisible = TryGet(() => view.CropBoxVisible, false);
                crop = AssemblyDocumentationBoundingBoxSnapshot.Capture(TryGet(() => view.CropBox, null));
            }

            bool sectionActive = false;
            AssemblyDocumentationBoundingBoxSnapshot section = null;
            AssemblyDocumentationXyzSnapshot eye = null;
            bool source3DOrientationLocked = false;
            if (view is View3D threeD)
            {
                sectionActive = threeD.IsSectionBoxActive;
                section = AssemblyDocumentationBoundingBoxSnapshot.Capture(threeD.GetSectionBox());
                ViewOrientation3D sourceOrientation = threeD.GetOrientation();
                eye = AssemblyDocumentationXyzSnapshot.Capture(sourceOrientation.EyePosition);
                source3DOrientationLocked = threeD.IsLocked;
            }

            AssemblyDocumentationViewAnnotationPlan annotations = view is ViewSchedule
                ? new AssemblyDocumentationViewAnnotationPlan(
                    Id(view.Id),
                    Enumerable.Empty<AssemblyDocumentationViewAnnotationItem>())
                : CaptureViewAnnotations(document, source, view, issues);

            // Changed by Jhay: a 3D source containing supported annotations must
            // have a saved effective orientation before it can define a reliable
            // target annotation contract.
            bool requiresLocked3DOrientation = kind ==
                    AssemblyDocumentationViewKind.Orthographic3D &&
                annotations.Items.Any(item =>
                    item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot ||
                    item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentTag ||
                    item.Kind == AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension);
            if (requiresLocked3DOrientation && !source3DOrientationLocked)
            {
                issues.Add(
                    "Assembly 3D view " + Id(view.Id) + " '" + view.Name +
                    "' contains supported view annotations but its orientation is not locked.");
            }

            return new AssemblyDocumentationViewPlan(
                Id(view.Id),
                view.Name,
                targetName,
                view.ViewType,
                kind,
                orientation,
                Id(view.ViewTemplateId),
                TryGet(() => view.Scale, 0),
                TryGet(() => view.DetailLevel, ViewDetailLevel.Undefined),
                TryGet(() => view.Discipline, ViewDiscipline.Coordination),
                cropActive,
                cropVisible,
                crop,
                sectionActive,
                section,
                AssemblyDocumentationXyzSnapshot.Capture(view.ViewDirection),
                AssemblyDocumentationXyzSnapshot.Capture(view.UpDirection),
                AssemblyDocumentationXyzSnapshot.Capture(view.RightDirection),
                eye,
                source3DOrientationLocked,
                scheduleCategoryId,
                scheduleDefinition,
                annotations);
        }

        // Changed by Jhay: inventory view-owned content separately from sheet content.
        internal static AssemblyDocumentationViewAnnotationPlan CaptureViewAnnotations(
            Document document,
            AssemblyInstance source,
            View view,
            ICollection<string> issues)
        {
            long viewId = Id(view.Id);
            List<Element> candidates = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(element => Id(element.OwnerViewId) == viewId)
                .ToList();
            var groupIds = new HashSet<long>(candidates.OfType<Group>().Select(group => Id(group.Id)));
            var sourceMemberIds = new HashSet<long>(source.GetMemberIds().Select(Id));
            var items = new List<AssemblyDocumentationViewAnnotationItem>();
            foreach (Element element in candidates.OrderBy(item => Id(item.Id)))
            {
                AssemblyDocumentationViewAnnotationItem item = CaptureViewAnnotationItem(
                    document,
                    source,
                    view,
                    element,
                    groupIds,
                    sourceMemberIds,
                    issues);
                items.Add(item);
                if (item.Kind == AssemblyDocumentationViewAnnotationKind.Dimension ||
                    item.Kind == AssemblyDocumentationViewAnnotationKind.SpotDimension ||
                    item.Kind == AssemblyDocumentationViewAnnotationKind.MultiReferenceAnnotation)
                {
                    issues.Add(BuildViewReferenceMappingIssue(view, item));
                }
                else if (item.Kind == AssemblyDocumentationViewAnnotationKind.Unsupported)
                {
                    issues.Add(BuildUnsupportedViewAnnotationIssue(view, item));
                }
            }
            return new AssemblyDocumentationViewAnnotationPlan(viewId, items);
        }

        private static AssemblyDocumentationViewAnnotationItem CaptureViewAnnotationItem(
            Document document,
            AssemblyInstance source,
            View view,
            Element element,
            ISet<long> groupIds,
            ISet<long> sourceMemberIds,
            ICollection<string> issues)
        {
            long elementId = Id(element.Id);
            long groupId = Id(element.GroupId);
            long copyRootId = elementId;
            AssemblyDocumentationTagPlan tagPlan = null;
            AssemblyDocumentationReferenceAnnotationPlan referencePlan = null;
            string infrastructureEvidence = string.Empty;
            AssemblyDocumentationViewAnnotationKind kind;

            if (element is Group)
            {
                kind = AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot;
            }
            else if (!RevitApiCompatibility.IsInvalidElementId(element.GroupId) &&
                     groupIds.Contains(groupId))
            {
                kind = AssemblyDocumentationViewAnnotationKind.CopiedWithGroup;
                copyRootId = groupId;
            }
            else if (element is IndependentTag tag)
            {
                tagPlan = CaptureTagPlan(document, view, tag);
#if REVIT2022_OR_GREATER
                bool hasSingleLeader = !tag.MultiLeader;
#else
                // Changed by Jhay: Revit 2021 exposes only the legacy single-target
                // tag API, so a separate multi-leader state does not exist.
                bool hasSingleLeader = true;
#endif
                bool localSingleElementReference = tagPlan.References.Count == 1 &&
                    tagPlan.References[0].ReferenceType == ElementReferenceType.REFERENCE_TYPE_NONE &&
                    sourceMemberIds.Contains(tagPlan.References[0].SourceElementId) &&
                    !tag.IsOrphaned && hasSingleLeader && !tag.IsMaterialTag &&
                    RevitApiCompatibility.IsInvalidElementId(tag.MultiReferenceAnnotationId);
                kind = localSingleElementReference
                    ? AssemblyDocumentationViewAnnotationKind.IndependentTag
                    : AssemblyDocumentationViewAnnotationKind.Unsupported;
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else if (element is SpotDimension spot)
            {
                kind = AssemblyDocumentationViewAnnotationKind.SpotDimension;
                referencePlan = CaptureDimensionPlan(document, source, view, spot, issues);
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else if (element is Dimension dimension)
            {
                referencePlan = CaptureDimensionPlan(document, source, view, dimension, issues);
                // Changed by Jhay: target references cannot exist during read-only
                // preflight. A fully understood linear dimension is therefore a
                // supported deferred operation, not a preview error.
                kind = IsSupportedDeferredDimension(
                    document, dimension, referencePlan, sourceMemberIds, out _)
                    ? AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension
                    : AssemblyDocumentationViewAnnotationKind.Dimension;
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else if (element is MultiReferenceAnnotation multiReference)
            {
                kind = AssemblyDocumentationViewAnnotationKind.MultiReferenceAnnotation;
                referencePlan = new AssemblyDocumentationReferenceAnnotationPlan(
                    "MRA tag=" + Id(multiReference.TagId) + ",dimension=" +
                    Id(multiReference.DimensionId),
                    0,
                    Enumerable.Empty<AssemblyDocumentationReferencePlan>(),
                    string.Empty);
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else if (element is TextNote || element is CurveElement ||
                     element is FilledRegion || element is AnnotationSymbol ||
                     IsSupportedViewFamilyInstance(element))
            {
                kind = AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot;
            }
            else if (TryCaptureRevitGeneratedViewInfrastructure(
                         view, element, out infrastructureEvidence))
            {
                kind = AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure;
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else
            {
                kind = AssemblyDocumentationViewAnnotationKind.Unsupported;
                copyRootId = Id(ElementId.InvalidElementId);
            }

            return new AssemblyDocumentationViewAnnotationItem(
                elementId,
                element.GetType().FullName,
                element.Category?.Name,
                Id(element.Category?.Id ?? ElementId.InvalidElementId),
                Id(element.GetTypeId()),
                Id(element.OwnerViewId),
                groupId,
                copyRootId,
                kind,
                kind == AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot
                    ? CaptureViewContentSignature(document, source, view, element, issues)
                    : kind == AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure
                        ? infrastructureEvidence
                        : kind == AssemblyDocumentationViewAnnotationKind.Unsupported
                            ? CaptureUnknownViewElementEvidence(view, element)
                            : string.Empty,
                tagPlan,
                referencePlan);
        }

        private static string CaptureUnknownViewElementEvidence(View view, Element element)
        {
            BoundingBoxXYZ bounds = TryGet(() => element.get_BoundingBox(view), null);
            bool geometryInspected = TryInspectVisibleGeometry(element, view, out int geometryObjects);
            return string.Join("|", new[]
            {
                "runtime=" + element.GetType().FullName,
                "category=" + (element.Category?.Name ?? "<none>"),
                "categoryId=" + Id(element.Category?.Id ?? ElementId.InvalidElementId),
                "typeId=" + Id(element.GetTypeId()),
                "ownerViewId=" + Id(element.OwnerViewId),
                "references=" + (element is IndependentTag || element is Dimension ||
                    element is MultiReferenceAnnotation),
                "groupId=" + Id(element.GroupId),
                "location=" + (element.Location == null
                    ? "<none>"
                    : element.Location.GetType().FullName),
                "boundingBox=" + (bounds == null ? "<none>" : "present"),
                "visibleGeometryObjects=" + (geometryInspected
                    ? geometryObjects.ToString(CultureInfo.InvariantCulture)
                    : "<inspection-failed>"),
                "canBeHidden=" + TryGet(() => element.CanBeHidden(view), false),
                "copyEligibility=unknown-not-attempted-read-only-preflight"
            });
        }

        // Changed by Jhay: classify only positively identified, view-owned Revit
        // state. Base Element instances require the full no-type/no-location/
        // no-visible-geometry evidence set; user-created elements remain unknown.
        private static bool TryCaptureRevitGeneratedViewInfrastructure(
            View view,
            Element element,
            out string evidence)
        {
            evidence = string.Empty;
            bool knownRuntime = element is SketchPlane || element is GraphicsStyle ||
                element is SunAndShadowSettings;
            long categoryId = Id(element.Category?.Id ?? ElementId.InvalidElementId);
            bool sunPath = element.GetType() == typeof(Element) &&
                (categoryId == (long)BuiltInCategory.OST_SunStudy ||
                 categoryId == (long)BuiltInCategory.OST_SunPath1 ||
                 categoryId == (long)BuiltInCategory.OST_SunPath2);
            bool baseElementWithoutCategory = element.GetType() == typeof(Element) &&
                element.Category == null;
            bool invalidType = RevitApiCompatibility.IsInvalidElementId(element.GetTypeId());
            bool invalidGroup = RevitApiCompatibility.IsInvalidElementId(element.GroupId);
            bool noLocation = element.Location == null;
            BoundingBoxXYZ bounds = TryGet(() => element.get_BoundingBox(view), null);
            bool geometryInspected = TryInspectVisibleGeometry(element, view, out int geometryObjects);
            bool noVisibleGeometry = geometryInspected && bounds == null && geometryObjects == 0;
            bool narrowInternalState = baseElementWithoutCategory && invalidType && invalidGroup &&
                noLocation && noVisibleGeometry;
            if (!knownRuntime && !sunPath && !narrowInternalState)
                return false;

            bool canBeHidden = TryGet(() => element.CanBeHidden(view), false);
            int dependentCount = TryGet(() => element.GetDependentElements(null).Count, -1);
            evidence = string.Join("|", new[]
            {
                "INFO",
                "RevitGeneratedViewInfrastructure",
                "runtime=" + element.GetType().FullName,
                "category=" + (element.Category?.Name ?? "<none>"),
                "categoryId=" + categoryId,
                "typeId=" + Id(element.GetTypeId()),
                "references=false",
                "groupId=" + Id(element.GroupId),
                "location=" + (noLocation ? "<none>" : element.Location.GetType().FullName),
                "boundingBox=" + (bounds == null ? "<none>" : "present"),
                "visibleGeometryObjects=" + (geometryInspected
                    ? geometryObjects.ToString(CultureInfo.InvariantCulture)
                    : "<inspection-failed>"),
                "canBeHidden=" + canBeHidden,
                "dependentCount=" + dependentCount,
                "copyEligibility=not-attempted-read-only-preflight",
                "automaticTargetEquivalent=required-by-post-create-validation",
                "evidence=" + (knownRuntime
                    ? "known-runtime"
                    : sunPath ? "built-in-sun-path" : "base-element-no-visible-documentation")
            });
            return true;
        }

        private static bool TryInspectVisibleGeometry(
            Element element,
            View view,
            out int geometryObjects)
        {
            geometryObjects = 0;
            try
            {
                var options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    View = view
                };
                GeometryElement geometry = element.get_Geometry(options);
                if (geometry != null)
                    geometryObjects = geometry.Cast<GeometryObject>().Count(item => item != null);
                return true;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        private static bool IsSupportedViewFamilyInstance(Element element)
        {
            if (!(element is FamilyInstance) || element.Category == null)
                return false;
            long categoryId = Id(element.Category.Id);
            return categoryId == (long)BuiltInCategory.OST_DetailComponents ||
                categoryId == (long)BuiltInCategory.OST_GenericAnnotation;
        }

        private static AssemblyDocumentationTagPlan CaptureTagPlan(
            Document document,
            View view,
            IndependentTag tag)
        {
#if REVIT2022_OR_GREATER
            List<AssemblyDocumentationReferencePlan> references = tag.GetTaggedReferences()
                .Select(reference => new AssemblyDocumentationReferencePlan(
                    Id(reference.ElementId),
                    reference.ElementReferenceType,
                    TryGet(() => reference.ConvertToStableRepresentation(document), string.Empty),
                    tag.HasLeader && tag.HasLeaderElbow(reference)
                        ? CaptureViewPoint(view, tag.GetLeaderElbow(reference))
                        : null,
                    tag.HasLeader
                        ? CaptureViewPoint(view, TryGet(() => tag.GetLeaderEnd(reference), null))
                        : null))
                .ToList();
            double rotationAngle = tag.RotationAngle;
#else
            // Changed by Jhay: preserve the equivalent legacy Revit 2021 data
            // through its single tagged-element and single leader properties.
            ElementId taggedElementId = tag.TaggedLocalElementId;
            Element taggedElement = RevitApiCompatibility.IsInvalidElementId(taggedElementId)
                ? null
                : document.GetElement(taggedElementId);
            var references = new List<AssemblyDocumentationReferencePlan>();
            if (taggedElement != null)
            {
                var reference = new Reference(taggedElement);
                references.Add(new AssemblyDocumentationReferencePlan(
                    Id(taggedElementId),
                    reference.ElementReferenceType,
                    TryGet(() => reference.ConvertToStableRepresentation(document), string.Empty),
                    tag.HasLeader
                        ? CaptureViewPoint(view, TryGet<XYZ>(() => tag.LeaderElbow, null))
                        : null,
                    tag.HasLeader
                        ? CaptureViewPoint(view, TryGet<XYZ>(() => tag.LeaderEnd, null))
                        : null));
            }
            double rotationAngle = 0.0;
#endif
            return new AssemblyDocumentationTagPlan(
                Id(tag.GetTypeId()),
                tag.HasLeader,
                tag.TagOrientation,
                rotationAngle,
                CaptureViewPoint(view, tag.TagHeadPosition),
                TryGet(() => tag.LeaderEndCondition, default(LeaderEndCondition)),
                references);
        }

        private static AssemblyDocumentationReferenceAnnotationPlan CaptureDimensionPlan(
            Document document,
            AssemblyInstance source,
            View view,
            Dimension dimension,
            ICollection<string> issues)
        {
            var references = new List<AssemblyDocumentationReferencePlan>();
            if (dimension.References != null)
            {
                foreach (Reference reference in dimension.References)
                {
                    AssemblyDocumentationReferenceSemantic semantic =
                        CaptureReferenceSemantic(document, view, reference);
                    references.Add(new AssemblyDocumentationReferencePlan(
                        Id(reference.ElementId),
                        reference.ElementReferenceType,
                        TryGet(() => reference.ConvertToStableRepresentation(document), string.Empty),
                        null,
                        null,
                        CaptureDimensionReferenceDiagnostics(
                            document, source, view, dimension, reference, issues),
                        semantic));
                }
            }
            Line line = dimension.Curve as Line;
            return new AssemblyDocumentationReferenceAnnotationPlan(
                CaptureCurveSignature(source, view, dimension, dimension.Curve, issues),
                dimension.NumberOfSegments,
                references,
                dimension.ValueString ?? string.Empty,
                line != null && line.IsBound ? CaptureViewPoint(view, line.GetEndPoint(0)) : null,
                line != null && line.IsBound ? CaptureViewPoint(view, line.GetEndPoint(1)) : null,
                CaptureDimensionFormatting(dimension),
                line?.IsBound ?? false,
                line != null && !line.IsBound ? CaptureViewPoint(view, line.Origin) : null,
                line != null && !line.IsBound
                    ? CaptureViewVectorSnapshot(view, line.Direction)
                    : null);
        }

        // Changed by Jhay: this is deliberately a narrow preflight contract. It
        // accepts only the observed two-reference, single-segment linear case and
        // only when every source reference has a proven semantic representation.
        private static bool IsSupportedDeferredDimension(
            Document document,
            Dimension dimension,
            AssemblyDocumentationReferenceAnnotationPlan plan,
            ISet<long> sourceMemberIds,
            out string reason)
        {
            reason = string.Empty;
            if (!string.Equals(
                    dimension.GetType().FullName,
                    "Autodesk.Revit.DB.LinearDimension",
                    StringComparison.Ordinal))
                reason = "runtime is not Autodesk.Revit.DB.LinearDimension";
            else if (!(dimension.Curve is Line))
                reason = "dimension curve is not a line";
            else if (plan.References.Count != 2)
                reason = "reference count is " + plan.References.Count + ", expected 2";
            else if (plan.SegmentCount > 1)
                reason = "multi-segment dimensions are not supported";
            else if (plan.References.Any(reference =>
                         !IsSupportedReferenceOwner(
                             document, reference.SourceElementId, sourceMemberIds)))
                reason = "one or more references are neither source production members nor " +
                    "proven source-hosted Pipe Insulation dependencies";
            else if (plan.References.Any(reference =>
                         reference.ReferenceType != ElementReferenceType.REFERENCE_TYPE_SURFACE &&
                         reference.ReferenceType != ElementReferenceType.REFERENCE_TYPE_LINEAR &&
                         reference.ReferenceType != ElementReferenceType.REFERENCE_TYPE_NONE))
                reason = "one or more reference types are unsupported";
            else if (plan.References.Any(reference => reference.Semantic == null))
                reason = "one or more source reference semantics are unresolved";
            else if (plan.LineIsBound &&
                     (plan.LineStartInView == null || plan.LineEndInView == null))
                reason = "bounded dimension line endpoints are unavailable";
            else if (!plan.LineIsBound &&
                     (plan.LineOriginInView == null || plan.LineDirectionInView == null))
                reason = "unbounded dimension line origin/direction are unavailable";
            return string.IsNullOrEmpty(reason);
        }

        private static bool IsSupportedReferenceOwner(
            Document document,
            long sourceElementId,
            ISet<long> sourceMemberIds)
        {
            if (sourceMemberIds.Contains(sourceElementId))
                return true;
            var insulation = document.GetElement(
                RevitApiCompatibility.CreateElementId(sourceElementId)) as PipeInsulation;
            return insulation != null && sourceMemberIds.Contains(Id(insulation.HostElementId));
        }

        private static IDictionary<string, string> CaptureDimensionFormatting(Dimension dimension)
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string propertyName in new[]
                     {
                         "Prefix", "Suffix", "Above", "Below", "ValueOverride",
                         "AreSegmentsEqual"
                     })
            {
                System.Reflection.PropertyInfo property = dimension.GetType().GetProperty(propertyName);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                    continue;
                try
                {
                    object value = property.GetValue(dimension, null);
                    result[propertyName] = Convert.ToString(
                        value, CultureInfo.InvariantCulture) ?? string.Empty;
                }
                catch (Exception)
                {
                    // Changed by Jhay: unavailable optional formatting does not
                    // erase the geometry/reference contract captured above.
                }
            }
            return result;
        }

        private static AssemblyDocumentationReferenceSemantic CaptureReferenceSemantic(
            Document document,
            View view,
            Reference sourceReference)
        {
            Element referenced = document.GetElement(sourceReference.ElementId);
            if (referenced == null)
                return null;
            string expectedStable = TryGet(
                () => sourceReference.ConvertToStableRepresentation(document), string.Empty);
            GeometryObject exact = FindExactReferenceGeometry(
                document, view, referenced, expectedStable);

            // Changed by Jhay: named/typed family references are stronger evidence
            // than an incidental nested geometry path and survive element copying.
            if (referenced is FamilyInstance family)
            {
                var matches = new List<Tuple<FamilyInstanceReferenceType, string>>();
                foreach (FamilyInstanceReferenceType referenceType in
                         Enum.GetValues(typeof(FamilyInstanceReferenceType)))
                {
                    if (referenceType == FamilyInstanceReferenceType.NotAReference)
                        continue;
                    IList<Reference> familyReferences = TryGet<IList<Reference>>(
                        () => family.GetReferences(referenceType), null);
                    if (familyReferences == null)
                        continue;
                    foreach (Reference candidate in familyReferences)
                    {
                        string stable = TryGet(
                            () => candidate.ConvertToStableRepresentation(document), string.Empty);
                        if (string.Equals(stable, expectedStable, StringComparison.Ordinal))
                        {
                            matches.Add(Tuple.Create(
                                referenceType,
                                TryGet(() => family.GetReferenceName(candidate), string.Empty)));
                        }
                    }
                }
                if (matches.Count == 1)
                {
                    PlanarFace planarFamilyReference = exact as PlanarFace;
                    return new AssemblyDocumentationReferenceSemantic(
                        AssemblyDocumentationReferenceSemanticKind.FamilyReference,
                        planarFamilyReference == null
                            ? null
                            : CaptureWorldPoint(planarFamilyReference.Origin),
                        planarFamilyReference == null
                            ? null
                            : CaptureWorldPoint(planarFamilyReference.FaceNormal),
                        planarFamilyReference?.Area ?? 0.0,
                        matches[0].Item1.ToString(),
                        matches[0].Item2,
                        planarFamilyReference == null
                            ? string.Empty
                            : CapturePlanarTopology(planarFamilyReference));
                }
            }

            if (exact is PlanarFace planar)
            {
                return new AssemblyDocumentationReferenceSemantic(
                    AssemblyDocumentationReferenceSemanticKind.PlanarFace,
                    CaptureWorldPoint(planar.Origin),
                    CaptureWorldPoint(planar.FaceNormal),
                    planar.Area,
                    string.Empty,
                    string.Empty,
                    CapturePlanarTopology(planar));
            }

            GeometryObject direct = TryGet<GeometryObject>(
                () => referenced.GetGeometryObjectFromReference(sourceReference), null);
            if (sourceReference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_NONE &&
                direct is Point point)
            {
                return new AssemblyDocumentationReferenceSemantic(
                    AssemblyDocumentationReferenceSemanticKind.ElementPoint,
                    CaptureWorldPoint(point.Coord),
                    null,
                    0.0,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    CaptureStableSemanticPath(referenced, expectedStable));
            }

            if (sourceReference.ElementReferenceType == ElementReferenceType.REFERENCE_TYPE_LINEAR &&
                referenced is Pipe)
            {
                Curve semanticCurve = exact as Curve ??
                    (referenced.Location as LocationCurve)?.Curve;
                if (semanticCurve is Line semanticLine && semanticLine.IsBound)
                {
                    return new AssemblyDocumentationReferenceSemantic(
                        AssemblyDocumentationReferenceSemanticKind.LinearCurve,
                        CaptureWorldPoint(semanticLine.GetEndPoint(0)),
                        CaptureWorldPoint(semanticLine.Direction),
                        semanticLine.Length,
                        string.Empty,
                        string.Empty,
                        "Line",
                        CaptureStableSemanticPath(referenced, expectedStable));
                }
            }
            return null;
        }

        private static string CaptureStableSemanticPath(
            Element referenced,
            string stableRepresentation)
        {
            string prefix = referenced?.UniqueId ?? string.Empty;
            return !string.IsNullOrEmpty(prefix) &&
                   stableRepresentation.StartsWith(prefix, StringComparison.Ordinal)
                ? stableRepresentation.Substring(prefix.Length)
                : string.Empty;
        }

        private static GeometryObject FindExactReferenceGeometry(
            Document document,
            View view,
            Element referenced,
            string expectedStable)
        {
            var candidates = new List<Tuple<Reference, GeometryObject>>();
            try
            {
                var options = new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = true,
                    View = view
                };
                CollectReferenceCandidates(referenced.get_Geometry(options), candidates);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return null;
            }
            List<GeometryObject> matches = candidates
                .Where(candidate => string.Equals(
                    TryGet(() => candidate.Item1.ConvertToStableRepresentation(document), string.Empty),
                    expectedStable,
                    StringComparison.Ordinal))
                .Select(candidate => candidate.Item2)
                .ToList();
            return matches.Count == 1 ? matches[0] : null;
        }

        private static AssemblyDocumentationXyzSnapshot CaptureWorldPoint(XYZ point) =>
            point == null
                ? null
                : new AssemblyDocumentationXyzSnapshot(point.X, point.Y, point.Z);

        private static string CapturePlanarTopology(PlanarFace face) =>
            string.Join(",", face.EdgeLoops.Cast<EdgeArray>()
                .Select(loop => loop.Size)
                .OrderBy(size => size));

        // Changed by Jhay: collect source-side semantic reference evidence during
        // read-only preflight. Target IDs/candidates cannot exist until the locked
        // physical engine runs, so that boundary is reported explicitly.
        private static string CaptureDimensionReferenceDiagnostics(
            Document document,
            AssemblyInstance source,
            View view,
            Dimension dimension,
            Reference reference,
            ICollection<string> issues)
        {
            var lines = new List<string>();
            Element referenced = document.GetElement(reference.ElementId);
            string stable = TryGet(
                () => reference.ConvertToStableRepresentation(document), string.Empty);
            Element type = referenced == null ? null : document.GetElement(referenced.GetTypeId());
            string familyAndType = referenced is FamilyInstance family
                ? (family.Symbol?.Family?.Name ?? "<no-family>") + " / " +
                  (family.Symbol?.Name ?? "<no-type>")
                : type?.Name ?? "<none>";
            GeometryObject geometry = referenced == null
                ? null
                : TryGet<GeometryObject>(
                    () => referenced.GetGeometryObjectFromReference(reference), null);

            lines.Add(
                "SOURCE REFERENCE | assembly " + Id(source.Id) + " '" +
                source.AssemblyTypeName + "' | dimension " + Id(dimension.Id) +
                " | view " + Id(view.Id) + " '" + view.Name + "' | element " +
                Id(reference.ElementId) + " | runtime " +
                (referenced?.GetType().FullName ?? "<missing>") + " | category " +
                (referenced?.Category?.Name ?? "<none>") + " | family/type " +
                familyAndType + " | ReferenceType " + reference.ElementReferenceType +
                " | StableRepresentation " + stable + " | geometry " +
                CaptureReferenceGeometrySignature(
                    source, view, dimension, geometry, issues));

            if (referenced != null)
            {
                foreach (string candidate in CaptureGeometryReferenceCandidates(
                             document, source, view, dimension, referenced, stable, issues))
                {
                    lines.Add("SOURCE GEOMETRY CANDIDATE | " + candidate);
                }
                if (referenced is FamilyInstance instance)
                {
                    foreach (string candidate in CaptureFamilyReferenceCandidates(
                                 document, instance, stable))
                    {
                        lines.Add("SOURCE FAMILY CANDIDATE | " + candidate);
                    }
                }
            }

            lines.Add(
                "TARGET MAPPING | mapped target element id unavailable during read-only " +
                "preflight; target candidate enumeration is deferred until the locked " +
                "physical source-to-target map exists. No mapping rule was inferred.");
            return string.Join(Environment.NewLine, lines);
        }

        private static IReadOnlyList<string> CaptureGeometryReferenceCandidates(
            Document document,
            AssemblyInstance source,
            View view,
            Element annotation,
            Element referenced,
            string expectedStable,
            ICollection<string> issues)
        {
            var candidates = new List<Tuple<Reference, GeometryObject>>();
            var diagnostics = new List<string>();
            try
            {
                var options = new Options
                {
                    ComputeReferences = true,
                    IncludeNonVisibleObjects = true,
                    View = view
                };
                CollectReferenceCandidates(referenced.get_Geometry(options), candidates);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException exception)
            {
                diagnostics.Add("enumeration failed | " + exception.Message);
                return diagnostics;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Tuple<Reference, GeometryObject> candidate in candidates)
            {
                string stable = TryGet(
                    () => candidate.Item1.ConvertToStableRepresentation(document), string.Empty);
                if (!seen.Add(stable))
                    continue;
                diagnostics.Add("stable " + stable + " | type " +
                    candidate.Item1.ElementReferenceType + " | geometry " +
                    CaptureReferenceGeometrySignature(
                        source, view, annotation, candidate.Item2, issues) +
                    " | decision " + (string.Equals(stable, expectedStable, StringComparison.Ordinal)
                        ? "ACCEPT exact source reference"
                        : "REJECT stable representation differs from source reference"));
            }
            if (seen.Count == 0)
                diagnostics.Add("<none returned by referenced element geometry>");
            return diagnostics;
        }

        private static void CollectReferenceCandidates(
            GeometryElement geometry,
            ICollection<Tuple<Reference, GeometryObject>> candidates)
        {
            if (geometry == null)
                return;
            foreach (GeometryObject item in geometry)
            {
                if (item is Solid solid)
                {
                    foreach (Face face in solid.Faces)
                    {
                        if (face.Reference != null)
                            candidates.Add(Tuple.Create(face.Reference, (GeometryObject)face));
                    }
                    foreach (Edge edge in solid.Edges)
                    {
                        if (edge.Reference != null)
                            candidates.Add(Tuple.Create(edge.Reference, (GeometryObject)edge));
                    }
                }
                else if (item is GeometryInstance instance)
                {
                    CollectReferenceCandidates(instance.GetInstanceGeometry(), candidates);
                }
                else if (item is Curve curve && curve.Reference != null)
                {
                    candidates.Add(Tuple.Create(curve.Reference, item));
                }
            }
        }

        private static IEnumerable<string> CaptureFamilyReferenceCandidates(
            Document document,
            FamilyInstance family,
            string expectedStable)
        {
            foreach (FamilyInstanceReferenceType referenceType in
                     Enum.GetValues(typeof(FamilyInstanceReferenceType)))
            {
                if (referenceType == FamilyInstanceReferenceType.NotAReference)
                    continue;
                IList<Reference> references = TryGet<IList<Reference>>(
                    () => family.GetReferences(referenceType), null);
                if (references == null)
                    continue;
                foreach (Reference candidate in references)
                {
                    string stable = TryGet(
                        () => candidate.ConvertToStableRepresentation(document), string.Empty);
                    string name = TryGet(() => family.GetReferenceName(candidate), string.Empty);
                    yield return "kind " + referenceType + " | name " +
                        (string.IsNullOrWhiteSpace(name) ? "<unnamed>" : name) +
                        " | stable " + stable + " | decision " +
                        (string.Equals(stable, expectedStable, StringComparison.Ordinal)
                            ? "ACCEPT exact source reference"
                            : "REJECT stable representation differs from source reference");
                }
            }
        }

        private static string CaptureReferenceGeometrySignature(
            AssemblyInstance source,
            View view,
            Element annotation,
            GeometryObject geometry,
            ICollection<string> issues)
        {
            if (geometry == null)
                return "<none>";
            if (geometry is PlanarFace planar)
            {
                return "PlanarFace|origin=" + CaptureViewPoint(view, planar.Origin).Signature +
                    "|normal=" + CaptureViewVector(view, planar.FaceNormal) +
                    "|x=" + CaptureViewVector(view, planar.XVector) +
                    "|y=" + CaptureViewVector(view, planar.YVector) +
                    "|area=" + Format(planar.Area);
            }
            if (geometry is Face face)
                return face.GetType().FullName + "|area=" + Format(face.Area);
            if (geometry is Edge edge)
            {
                return "Edge|" + CaptureCurveSignature(
                    source, view, annotation, edge.AsCurve(), issues);
            }
            if (geometry is Curve curve)
                return CaptureCurveSignature(source, view, annotation, curve, issues);
            return geometry.GetType().FullName;
        }

        private static string CaptureViewContentSignature(
            Document document,
            AssemblyInstance source,
            View view,
            Element element,
            ICollection<string> issues)
        {
            string own = CaptureViewElementSignature(source, view, element, issues);
            if (!(element is Group group))
                return own;
            string members = string.Join("|", group.GetMemberIds()
                .Select(document.GetElement)
                .Where(member => member != null)
                .Select(member => CaptureViewElementSignature(source, view, member, issues))
                .OrderBy(value => value, StringComparer.Ordinal));
            return own + "|members=" + members;
        }

        private static string CaptureViewElementSignature(
            AssemblyInstance source,
            View view,
            Element element,
            ICollection<string> issues)
        {
            BoundingBoxXYZ bounds = TryGet(() => element.get_BoundingBox(view), null);
            return string.Join(";", new[]
            {
                element.GetType().FullName,
                Id(element.Category?.Id ?? ElementId.InvalidElementId).ToString(CultureInfo.InvariantCulture),
                Id(element.GetTypeId()).ToString(CultureInfo.InvariantCulture),
                CaptureViewBoxSignature(view, bounds),
                CaptureViewElementSemantics(source, view, element, issues)
            });
        }

        // Changed by Jhay: independent view content is validated by its semantic
        // content and placement, not by a count or bounding box alone.
        private static string CaptureViewElementSemantics(
            AssemblyInstance source,
            View view,
            Element element,
            ICollection<string> issues)
        {
            if (element is TextNote text)
            {
                return "text=" + text.Text + "|coord=" +
                    CaptureViewPoint(view, text.Coord).Signature + "|base=" +
                    CaptureViewVector(view, text.BaseDirection) + "|up=" +
                    CaptureViewVector(view, text.UpDirection);
            }
            if (element is CurveElement curveElement)
            {
                return "curve=" + CaptureCurveSignature(
                           source, view, element, curveElement.GeometryCurve, issues) +
                    "|line-style=" + Id(curveElement.LineStyle?.Id ?? ElementId.InvalidElementId);
            }
            if (element is FilledRegion region)
            {
                return "boundaries=" + string.Join("||", region.GetBoundaries()
                    .Select(loop => string.Join("|", loop.Select(curve =>
                        CaptureCurveSignature(source, view, element, curve, issues)))));
            }
            if (element is FamilyInstance family)
            {
                string location = family.Location is LocationPoint point
                    ? "point=" + CaptureViewPoint(view, point.Point).Signature +
                      "|rotation=" + point.Rotation.ToString("G17", CultureInfo.InvariantCulture)
                    : family.Location is LocationCurve curve
                        ? "curve=" + CaptureCurveSignature(
                            source, view, element, curve.Curve, issues)
                        : "location=<none>";
                return location + "|facing=" + CaptureViewVector(view, family.FacingOrientation) +
                    "|hand=" + CaptureViewVector(view, family.HandOrientation) +
                    "|mirrored=" + family.Mirrored;
            }
            return "<no-additional-semantics>";
        }

        private static string CaptureViewVector(View view, XYZ vector)
        {
            if (vector == null)
                return "<none>";
            return string.Join(",", new[]
            {
                vector.DotProduct(view.RightDirection),
                vector.DotProduct(view.UpDirection),
                vector.DotProduct(view.ViewDirection)
            }.Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));
        }

        private static AssemblyDocumentationXyzSnapshot CaptureViewVectorSnapshot(
            View view,
            XYZ vector)
        {
            if (vector == null)
                return null;
            return new AssemblyDocumentationXyzSnapshot(
                vector.DotProduct(view.RightDirection),
                vector.DotProduct(view.UpDirection),
                vector.DotProduct(view.ViewDirection));
        }

        private static string CaptureViewBoxSignature(View view, BoundingBoxXYZ box)
        {
            if (box == null)
                return "<no-box>";
            List<XYZ> points = new List<XYZ>();
            foreach (double x in new[] { box.Min.X, box.Max.X })
            foreach (double y in new[] { box.Min.Y, box.Max.Y })
            foreach (double z in new[] { box.Min.Z, box.Max.Z })
                points.Add(CaptureViewPoint(view, box.Transform.OfPoint(new XYZ(x, y, z))).ToXyz());
            return string.Join(",", new[]
            {
                points.Min(point => point.X), points.Min(point => point.Y), points.Min(point => point.Z),
                points.Max(point => point.X), points.Max(point => point.Y), points.Max(point => point.Z)
            }.Select(value => value.ToString("G17", CultureInfo.InvariantCulture)));
        }

        private static AssemblyDocumentationXyzSnapshot CaptureViewPoint(View view, XYZ point)
        {
            if (point == null)
                return null;
            XYZ delta = point - view.Origin;
            return new AssemblyDocumentationXyzSnapshot(
                delta.DotProduct(view.RightDirection),
                delta.DotProduct(view.UpDirection),
                delta.DotProduct(view.ViewDirection));
        }

        // Changed by Jhay: annotation discovery must never ask an unbounded curve
        // for bounded-only data or allow an expected Revit geometry exception to
        // abort the complete preview.
        private static string CaptureCurveSignature(
            AssemblyInstance source,
            View view,
            Element annotation,
            Curve curve,
            ICollection<string> issues)
        {
            if (curve == null)
                return "CurveSignature|CaptureStatus=None";

            bool? capturedIsBound = null;
            try
            {
                capturedIsBound = curve.IsBound;
                bool isBound = capturedIsBound.Value;
                if (curve is Line line)
                {
                    string signature = "CurveType=Line|RuntimeType=" + curve.GetType().FullName +
                        "|IsBound=" + isBound + "|Direction=" +
                        CaptureViewVector(view, line.Direction);
                    if (!isBound)
                    {
                        return signature + "|Origin=" +
                            CaptureViewPoint(view, line.Origin).Signature +
                            "|CaptureStatus=Semantic";
                    }
                    return signature + "|" + CaptureBoundedCurveDetails(view, line, true) +
                        "|CaptureStatus=Semantic";
                }

                if (curve is Arc arc)
                {
                    string signature = "CurveType=Arc|RuntimeType=" + curve.GetType().FullName +
                        "|IsBound=" + isBound + "|Center=" +
                        CaptureViewPoint(view, arc.Center).Signature + "|Radius=" +
                        Format(arc.Radius) + "|Normal=" + CaptureViewVector(view, arc.Normal) +
                        "|XDirection=" + CaptureViewVector(view, arc.XDirection) +
                        "|YDirection=" + CaptureViewVector(view, arc.YDirection);
                    return isBound
                        ? signature + "|" + CaptureBoundedCurveDetails(view, arc, false) +
                          "|CaptureStatus=Semantic"
                        : signature + "|CaptureStatus=Semantic";
                }

                if (curve is Ellipse ellipse)
                {
                    string signature = "CurveType=Ellipse|RuntimeType=" + curve.GetType().FullName +
                        "|IsBound=" + isBound + "|Center=" +
                        CaptureViewPoint(view, ellipse.Center).Signature + "|RadiusX=" +
                        Format(ellipse.RadiusX) + "|RadiusY=" + Format(ellipse.RadiusY) +
                        "|Normal=" + CaptureViewVector(view, ellipse.Normal) +
                        "|XDirection=" + CaptureViewVector(view, ellipse.XDirection) +
                        "|YDirection=" + CaptureViewVector(view, ellipse.YDirection);
                    return isBound
                        ? signature + "|" + CaptureBoundedCurveDetails(view, ellipse, false) +
                          "|CaptureStatus=Semantic"
                        : signature + "|CaptureStatus=Semantic";
                }

                if (!isBound)
                {
                    return UnsupportedCurveSignature(
                        source,
                        view,
                        annotation,
                        curve,
                        capturedIsBound,
                        issues,
                        "Cannot safely derive a semantic signature for this unbounded curve type.");
                }

                try
                {
                    IList<XYZ> points = curve.Tessellate();
                    return "CurveType=Other|RuntimeType=" + curve.GetType().FullName +
                        "|IsBound=True|Tessellation=" + string.Join(";", points.Select(point =>
                            CaptureViewPoint(view, point).Signature)) +
                        "|CaptureStatus=Tessellated";
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException exception)
                {
                    return UnsupportedCurveSignature(
                        source,
                        view,
                        annotation,
                        curve,
                        capturedIsBound,
                        issues,
                        "Tessellation failed: " + exception.Message);
                }
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException exception)
            {
                return UnsupportedCurveSignature(
                    source,
                    view,
                    annotation,
                    curve,
                    capturedIsBound,
                    issues,
                    "Semantic curve capture failed: " + exception.Message);
            }
        }

        private static string CaptureBoundedCurveDetails(
            View view,
            Curve curve,
            bool includeLength)
        {
            string details = "Start=" + CaptureViewPoint(view, curve.GetEndPoint(0)).Signature +
                "|End=" + CaptureViewPoint(view, curve.GetEndPoint(1)).Signature +
                "|StartParameter=" + Format(curve.GetEndParameter(0)) +
                "|EndParameter=" + Format(curve.GetEndParameter(1));
            return includeLength ? details + "|Length=" + Format(curve.Length) : details;
        }

        private static string UnsupportedCurveSignature(
            AssemblyInstance source,
            View view,
            Element annotation,
            Curve curve,
            bool? capturedIsBound,
            ICollection<string> issues,
            string reason)
        {
            string runtimeType = curve?.GetType().FullName ?? "<none>";
            string isBound = capturedIsBound.HasValue
                ? capturedIsBound.Value.ToString()
                : "<unknown>";
            string issue = "VIEW ANNOTATION CURVE UNSUPPORTED | source assembly " +
                Id(source.Id) + " '" + source.AssemblyTypeName + "' | OwnerViewId " +
                Id(view.Id) + " '" + view.Name + "' | ElementId " + Id(annotation.Id) +
                " | " + annotation.GetType().FullName + " | category " +
                (annotation.Category?.Name ?? "<none>") + " | curve " + runtimeType +
                " | IsBound " + isBound + " | CaptureStatus Unsupported | " + reason;
            if (!issues.Contains(issue))
                issues.Add(issue);
            return "CurveType=" + runtimeType + "|IsBound=" + isBound +
                "|CaptureStatus=Unsupported|Reason=" + reason;
        }

        private static string Format(double value) =>
            value.ToString("G17", CultureInfo.InvariantCulture);

        private static string BuildViewReferenceMappingIssue(
            View view,
            AssemblyDocumentationViewAnnotationItem item)
        {
            IReadOnlyList<AssemblyDocumentationReferencePlan> references =
                item.ReferenceAnnotation?.References ??
                Array.Empty<AssemblyDocumentationReferencePlan>();
            string diagnostics = string.Join(" || ", references
                .Where(reference => !string.IsNullOrWhiteSpace(reference.Diagnostics))
                .SelectMany(reference => reference.Diagnostics
                    .Split(new[] { Environment.NewLine }, StringSplitOptions.None)));
            return "VIEW ANNOTATION REFERENCE MAPPING REQUIRED | view " + Id(view.Id) +
                " '" + view.Name + "' | ElementId " + item.ElementId + " | " + item.RuntimeType +
                " | category " + item.CategoryName + " | owner " + item.OwnerViewId +
                " | references " + DescribeReferences(references) +
                (diagnostics.Length == 0 ? string.Empty : " | diagnostics " + diagnostics) + ".";
        }

        private static string BuildUnsupportedViewAnnotationIssue(
            View view,
            AssemblyDocumentationViewAnnotationItem item) =>
            "VIEW ANNOTATION UNSUPPORTED | view " + Id(view.Id) + " '" + view.Name +
            "' | ElementId " + item.ElementId + " | " + item.RuntimeType +
            " | category " + item.CategoryName + " | owner " + item.OwnerViewId +
            " | references " + DescribeReferences(item.Tag?.References) +
            " | evidence " + item.ContentSignature + ".";

        private static string DescribeReferences(
            IEnumerable<AssemblyDocumentationReferencePlan> references) =>
            references == null
                ? "<none>"
                : string.Join(", ", references.Select(reference =>
                    "element " + reference.SourceElementId + " type " + reference.ReferenceType +
                    " stable '" + reference.StableRepresentation + "'"));

        private static AssemblyDocumentationSheetPlan CaptureSheet(
            Document document,
            AssemblyInstance source,
            string targetAssemblyName,
            ViewSheet sheet,
            IReadOnlyCollection<long> ownedViewIds,
            ICollection<string> issues)
        {
            List<FamilyInstance> titleBlocks = new FilteredElementCollector(document, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>()
                .Where(instance => Id(instance.OwnerViewId) == Id(sheet.Id))
                .ToList();
            if (titleBlocks.Count > 1)
            {
                issues.Add("Assembly sheet " + Id(sheet.Id) + " contains " + titleBlocks.Count +
                    " title-block instances; exactly zero or one is supported.");
            }
            ElementId titleBlockTypeId = titleBlocks.Count == 1
                ? titleBlocks[0].GetTypeId()
                : ElementId.InvalidElementId;
            Element titleBlockType = document.GetElement(titleBlockTypeId);
            string titleBlockDisplay = titleBlockType == null
                ? "<none>"
                : titleBlockType.Name + " | id " + Id(titleBlockTypeId);

            var viewports = new List<AssemblyDocumentationViewportPlan>();
            foreach (ElementId viewportId in sheet.GetAllViewports().OrderBy(Id))
            {
                Viewport viewport = document.GetElement(viewportId) as Viewport;
                View placed = viewport == null ? null : document.GetElement(viewport.ViewId) as View;
                if (viewport == null || placed == null)
                {
                    issues.Add("Assembly sheet " + Id(sheet.Id) +
                        " contains an unavailable viewport or placed view.");
                    continue;
                }

                bool reusableLegend = placed.ViewType == ViewType.Legend &&
                    RevitApiCompatibility.IsInvalidElementId(placed.AssociatedAssemblyInstanceId);
                if (!ownedViewIds.Contains(Id(placed.Id)) && !reusableLegend)
                {
                    issues.Add("Viewport " + Id(viewport.Id) + " places non-owned, non-Legend view " +
                        Id(placed.Id) + " '" + placed.Name + "'; no supported reuse rule exists.");
                    continue;
                }

                viewports.Add(new AssemblyDocumentationViewportPlan(
                    Id(viewport.Id),
                    Id(placed.Id),
                    reusableLegend,
                    placed.Name,
                    Id(viewport.GetTypeId()),
                    viewport.Rotation,
                    AssemblyDocumentationXyzSnapshot.Capture(viewport.GetBoxCenter()),
                    OptionalXyzProperty(viewport, "LabelOffset"),
                    OptionalDoubleProperty(viewport, "LabelLineLength", double.NaN),
                    OptionalPropertyValue(viewport, "ViewportPositioning")));
            }

            var schedules = new List<AssemblyDocumentationSchedulePlacementPlan>();
            int titleBlockRevisionScheduleCount = 0;
            foreach (ScheduleSheetInstance instance in new FilteredElementCollector(document)
                         .OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>()
                         .Where(item => Id(item.OwnerViewId) == Id(sheet.Id))
                         .OrderBy(item => Id(item.Id)))
            {
                if (instance.IsTitleblockRevisionSchedule)
                {
                    // Changed by Jhay: revision schedules are generated by the title-block type;
                    // they are not independently placed schedule content.
                    titleBlockRevisionScheduleCount++;
                    continue;
                }
                ViewSchedule schedule = document.GetElement(instance.ScheduleId) as ViewSchedule;
                if (schedule == null)
                {
                    issues.Add("Schedule instance " + Id(instance.Id) + " references an unavailable schedule.");
                    continue;
                }
                bool sourceOwned = Id(schedule.AssociatedAssemblyInstanceId) == Id(source.Id);
                schedules.Add(new AssemblyDocumentationSchedulePlacementPlan(
                    Id(instance.Id),
                    Id(schedule.Id),
                    sourceOwned,
                    schedule.Name,
                    AssemblyDocumentationXyzSnapshot.Capture(instance.Point),
                    instance.Rotation,
                    OptionalIntProperty(instance, "SegmentIndex", -1)));
            }

            return new AssemblyDocumentationSheetPlan(
                Id(sheet.Id),
                sheet.SheetNumber,
                ReplaceExactToken(sheet.SheetNumber, source.AssemblyTypeName, targetAssemblyName,
                    "sheet number for sheet " + Id(sheet.Id), issues),
                sheet.Name,
                ReplaceExactToken(sheet.Name, source.AssemblyTypeName, targetAssemblyName,
                    "sheet name for sheet " + Id(sheet.Id), issues),
                Id(titleBlockTypeId),
                titleBlockDisplay,
                viewports,
                schedules,
                titleBlockRevisionScheduleCount);
        }

        internal static IReadOnlyList<AssemblyDocumentationCategoryEItem> CaptureCategoryE(
            Document document,
            ViewSheet sheet)
        {
            long sheetId = Id(sheet.Id);
            List<Element> candidates = new FilteredElementCollector(document)
                .WhereElementIsNotElementType()
                .Where(element => Id(element.OwnerViewId) == sheetId)
                .Where(element => !(element is Viewport))
                .Where(element => !(element is ScheduleSheetInstance))
                .Where(element => element.Category == null ||
                    Id(element.Category.Id) != (long)BuiltInCategory.OST_TitleBlocks)
                .ToList();

            var groupIds = new HashSet<long>(candidates.OfType<Group>().Select(group => Id(group.Id)));
            var generatedIds = new HashSet<long>();
            foreach (FamilyInstance titleBlock in new FilteredElementCollector(document, sheet.Id)
                         .OfCategory(BuiltInCategory.OST_TitleBlocks)
                         .WhereElementIsNotElementType()
                         .OfType<FamilyInstance>())
            {
                AddDependentClosure(document, titleBlock, sheetId, generatedIds);
            }
            foreach (ScheduleSheetInstance revisionInstance in new FilteredElementCollector(document)
                         .OfClass(typeof(ScheduleSheetInstance))
                         .Cast<ScheduleSheetInstance>()
                         .Where(item => Id(item.OwnerViewId) == sheetId &&
                             item.IsTitleblockRevisionSchedule))
            {
                AddDependentClosure(document, revisionInstance, sheetId, generatedIds);
                ViewSchedule revisionSchedule = document.GetElement(revisionInstance.ScheduleId) as ViewSchedule;
                if (revisionSchedule != null)
                    AddDependentClosure(document, revisionSchedule, sheetId, generatedIds);
            }
            SketchPlane sketchPlane = TryGet(() => sheet.SketchPlane, null);
            if (sketchPlane != null)
                generatedIds.Add(Id(sketchPlane.Id));

            return candidates
                .Select(element => CaptureSheetOwnedItem(
                    document,
                    sheet,
                    element,
                    groupIds,
                    generatedIds))
                .OrderBy(item => item.ElementId)
                .ToList()
                .AsReadOnly();
        }

        private static AssemblyDocumentationCategoryEItem CaptureSheetOwnedItem(
            Document document,
            ViewSheet sheet,
            Element element,
            ISet<long> groupIds,
            ISet<long> generatedIds)
        {
            long elementId = Id(element.Id);
            long groupId = Id(element.GroupId);
            long copyRootId = elementId;
            AssemblyDocumentationSheetOwnedItemDisposition disposition;
            if (element is Group)
            {
                disposition = AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot;
            }
            else if (!RevitApiCompatibility.IsInvalidElementId(element.GroupId) &&
                     groupIds.Contains(groupId))
            {
                disposition = AssemblyDocumentationSheetOwnedItemDisposition.CopiedWithGroup;
                copyRootId = groupId;
            }
            else if (element is ImageInstance || element is TextNote || element is CurveElement)
            {
                disposition = AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot;
            }
            else if (generatedIds.Contains(elementId) || element is SketchPlane ||
                     element is GraphicsStyle ||
                     element is ViewSchedule schedule && schedule.IsTitleblockRevisionSchedule)
            {
                disposition = AssemblyDocumentationSheetOwnedItemDisposition.RevitGeneratedInfrastructure;
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else
            {
                disposition = AssemblyDocumentationSheetOwnedItemDisposition.Unsupported;
                copyRootId = Id(ElementId.InvalidElementId);
            }

            return new AssemblyDocumentationCategoryEItem(
                elementId,
                element.GetType().FullName,
                element.Category?.Name,
                Id(element.OwnerViewId),
                groupId,
                copyRootId,
                disposition,
                disposition == AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot
                    ? CaptureContentSignature(document, sheet, element)
                    : string.Empty);
        }

        private static string CaptureContentSignature(
            Document document,
            ViewSheet sheet,
            Element element)
        {
            string own = CaptureElementContentSignature(sheet, element);
            if (!(element is Group group))
                return own;
            string members = string.Join("|", group.GetMemberIds()
                .Select(document.GetElement)
                .Where(member => member != null)
                .Select(member => CaptureElementContentSignature(sheet, member))
                .OrderBy(value => value, StringComparer.Ordinal));
            return own + "|members=" + members;
        }

        private static string CaptureElementContentSignature(ViewSheet sheet, Element element)
        {
            BoundingBoxXYZ bounds = TryGet(() => element.get_BoundingBox(sheet), null);
            string box = AssemblyDocumentationBoundingBoxSnapshot.Capture(bounds)?.Signature ?? "<no-box>";
            return string.Join(";", new[]
            {
                element.GetType().FullName,
                Id(element.Category?.Id ?? ElementId.InvalidElementId).ToString(CultureInfo.InvariantCulture),
                Id(element.GetTypeId()).ToString(CultureInfo.InvariantCulture),
                box
            });
        }

        private static void AddDependentClosure(
            Document document,
            Element root,
            long ownerViewId,
            ISet<long> result)
        {
            if (root == null)
                return;
            var pending = new Stack<ElementId>(root.GetDependentElements(null));
            while (pending.Count > 0)
            {
                ElementId id = pending.Pop();
                if (!result.Add(Id(id)))
                    continue;
                Element dependent = document.GetElement(id);
                if (dependent == null)
                    continue;
                // Changed by Jhay: never allow dependency traversal to escape the
                // source sheet and accidentally classify unrelated content.
                if (Id(dependent.OwnerViewId) != ownerViewId)
                {
                    result.Remove(Id(id));
                    continue;
                }
                foreach (ElementId nested in dependent.GetDependentElements(null))
                    pending.Push(nested);
            }
        }

        private static bool TryClassifyOrientation(
            View view,
            Transform sourceTransform,
            out AssemblyDetailViewOrientation orientation)
        {
            // Changed by Jhay: ViewDirection points toward the viewer; the assembly
            // orientation enum describes the opposite looking/forward direction.
            XYZ local = sourceTransform.Inverse.OfVector(-view.ViewDirection).Normalize();
            // Changed by Jhay: Revit may expose an assembly elevation through a
            // non-Elevation ViewType. Directions unique to elevations remain
            // geometrically unambiguous and must not be rejected for that reason.
            if (Along(local, XYZ.BasisX))
            {
                orientation = AssemblyDetailViewOrientation.ElevationLeft;
                return true;
            }
            if (Along(local, -XYZ.BasisY))
            {
                orientation = AssemblyDetailViewOrientation.ElevationBack;
                return true;
            }
            if (Along(local, XYZ.BasisZ))
            {
                orientation = AssemblyDetailViewOrientation.ElevationBottom;
                return true;
            }

            if (view.ViewType == ViewType.Elevation)
            {
                if (Along(local, XYZ.BasisY)) orientation = AssemblyDetailViewOrientation.ElevationFront;
                else if (Along(local, -XYZ.BasisX)) orientation = AssemblyDetailViewOrientation.ElevationRight;
                else if (Along(local, -XYZ.BasisZ)) orientation = AssemblyDetailViewOrientation.ElevationTop;
                else return Fail(out orientation);
                return true;
            }

            if (view.ViewType == ViewType.Detail || view.ViewType == ViewType.Section ||
                view.ViewType == ViewType.FloorPlan || view.ViewType == ViewType.EngineeringPlan)
            {
                if (Along(local, -XYZ.BasisZ)) orientation = AssemblyDetailViewOrientation.HorizontalDetail;
                else if (Along(local, XYZ.BasisY)) orientation = AssemblyDetailViewOrientation.DetailSectionA;
                else if (Along(local, -XYZ.BasisX)) orientation = AssemblyDetailViewOrientation.DetailSectionB;
                else return Fail(out orientation);
                return true;
            }

            return Fail(out orientation);
        }

        private static bool TryClassifySchedule(
            ViewSchedule schedule,
            out AssemblyDocumentationViewKind kind,
            out long categoryId,
            out string issue)
        {
            ScheduleDefinition definition = schedule.Definition;
            categoryId = Id(definition.CategoryId);
            if (schedule.IsTitleblockRevisionSchedule || definition.IsKeySchedule || definition.HasEmbeddedSchedule)
            {
                kind = default;
                issue = "revision, key, and embedded schedules are not supported";
                return false;
            }
            if (definition.IsMaterialTakeoff)
            {
                kind = AssemblyDocumentationViewKind.MaterialTakeoff;
                issue = string.Empty;
                return true;
            }
            if (!RevitApiCompatibility.IsInvalidElementId(definition.CategoryId))
            {
                kind = AssemblyDocumentationViewKind.SingleCategorySchedule;
                issue = string.Empty;
                return true;
            }
            if (schedule.IsAssemblyView)
            {
                kind = AssemblyDocumentationViewKind.PartList;
                issue = string.Empty;
                return true;
            }
            kind = default;
            issue = "its schedule factory semantics cannot be positively identified";
            return false;
        }

        internal static string CaptureScheduleDefinitionSignature(ViewSchedule schedule)
        {
            ScheduleDefinition definition = schedule.Definition;
            var text = new StringBuilder();
            text.Append(definition.IsMaterialTakeoff).Append(';')
                .Append(Id(definition.CategoryId)).Append(';')
                .Append(definition.IsItemized).Append(';')
                .Append(OptionalPropertyValue(definition, "IsFilteredBySheet")).Append(';')
                .Append(definition.ShowHeaders).Append(';')
                .Append(definition.ShowTitle).Append(';')
                .Append(definition.ShowGridLines).Append(';')
                .Append(definition.ShowGrandTotal).Append(';')
                .Append(definition.ShowGrandTotalCount).Append(';')
                .Append(definition.ShowGrandTotalTitle).Append(';')
                .Append(definition.GrandTotalTitle ?? string.Empty);
            for (int index = 0; index < definition.GetFieldCount(); index++)
            {
                ScheduleField field = definition.GetField(index);
                text.Append("|F:").Append(index).Append(',').Append(field.FieldType).Append(',')
                    .Append(Id(field.ParameterId)).Append(',').Append(field.ColumnHeading).Append(',')
                    .Append(field.IsHidden).Append(',')
                    .Append(field.SheetColumnWidth.ToString("G17", CultureInfo.InvariantCulture)).Append(',')
                    .Append(field.DisplayType).Append(',').Append(field.HorizontalAlignment).Append(',')
                    .Append(OptionalPropertyValue(field, "VerticalAlignment")).Append(',')
                    .Append(field.HeadingOrientation);
            }
            foreach (ScheduleFilter filter in definition.GetFilters())
            {
                text.Append("|X:").Append(filter.FieldId.IntegerValue).Append(',').Append(filter.FilterType)
                    .Append(',').Append(ScheduleFilterValue(filter));
            }
            foreach (ScheduleSortGroupField sort in definition.GetSortGroupFields())
            {
                text.Append("|S:").Append(sort.FieldId.IntegerValue).Append(',').Append(sort.SortOrder)
                    .Append(',').Append(sort.ShowHeader).Append(',').Append(sort.ShowFooter)
                    .Append(',').Append(sort.ShowFooterCount).Append(',').Append(sort.ShowFooterTitle)
                    .Append(',').Append(sort.ShowBlankLine);
            }
            return text.ToString();
        }

        private static string ScheduleFilterValue(ScheduleFilter filter)
        {
            if (filter.IsNullValue) return "<null>";
            if (filter.IsStringValue) return filter.GetStringValue() ?? string.Empty;
            if (filter.IsDoubleValue) return filter.GetDoubleValue().ToString("G17", CultureInfo.InvariantCulture);
            if (filter.IsIntegerValue) return filter.GetIntegerValue().ToString(CultureInfo.InvariantCulture);
            if (filter.IsElementIdValue) return Id(filter.GetElementIdValue()).ToString(CultureInfo.InvariantCulture);
            return "<unknown>";
        }

        private static string ReplaceExactToken(
            string sourceValue,
            string sourceAssemblyName,
            string targetAssemblyName,
            string label,
            ICollection<string> issues)
        {
            if (string.IsNullOrEmpty(sourceValue) || string.IsNullOrEmpty(sourceAssemblyName))
                return null;
            int first = sourceValue.IndexOf(sourceAssemblyName, StringComparison.Ordinal);
            if (first < 0)
                return null;
            int second = sourceValue.IndexOf(
                sourceAssemblyName,
                first + sourceAssemblyName.Length,
                StringComparison.Ordinal);
            if (second >= 0)
            {
                issues.Add("The " + label + " contains the complete source assembly-name token more than once.");
                return null;
            }
            return sourceValue.Substring(0, first) + targetAssemblyName +
                sourceValue.Substring(first + sourceAssemblyName.Length);
        }

        private static bool Along(XYZ left, XYZ right) =>
            left != null && right != null && left.DistanceTo(right) <= DirectionTolerance;

        private static string Vector(XYZ value) => value == null
            ? "<null>"
            : string.Join(",",
                value.X.ToString("G17", CultureInfo.InvariantCulture),
                value.Y.ToString("G17", CultureInfo.InvariantCulture),
                value.Z.ToString("G17", CultureInfo.InvariantCulture));

        private static bool Fail(out AssemblyDetailViewOrientation orientation)
        {
            orientation = default;
            return false;
        }

        private static T TryGet<T>(Func<T> getter, T fallback)
        {
            try { return getter(); }
            catch { return fallback; }
        }

        private static string OptionalPropertyValue(object instance, string propertyName)
        {
            if (instance == null)
                return "<unsupported>";
            System.Reflection.PropertyInfo property = instance.GetType().GetProperty(propertyName);
            if (property == null || !property.CanRead)
                return "<unsupported>";
            object value = property.GetValue(instance, null);
            return value?.ToString() ?? "<null>";
        }

        private static AssemblyDocumentationXyzSnapshot OptionalXyzProperty(
            object instance,
            string propertyName)
        {
            System.Reflection.PropertyInfo property = instance?.GetType().GetProperty(propertyName);
            return property?.CanRead == true
                ? AssemblyDocumentationXyzSnapshot.Capture(property.GetValue(instance, null) as XYZ)
                : null;
        }

        private static double OptionalDoubleProperty(
            object instance,
            string propertyName,
            double fallback)
        {
            System.Reflection.PropertyInfo property = instance?.GetType().GetProperty(propertyName);
            object value = property?.CanRead == true ? property.GetValue(instance, null) : null;
            return value is double number ? number : fallback;
        }

        private static int OptionalIntProperty(
            object instance,
            string propertyName,
            int fallback)
        {
            System.Reflection.PropertyInfo property = instance?.GetType().GetProperty(propertyName);
            object value = property?.CanRead == true ? property.GetValue(instance, null) : null;
            return value is int number ? number : fallback;
        }

        private static string TransformSignature(Transform transform)
        {
            return string.Join(";", new[]
            {
                Point(transform.Origin), Point(transform.BasisX), Point(transform.BasisY), Point(transform.BasisZ)
            });
        }

        private static string Point(XYZ point) => string.Join(",",
            point.X.ToString("G17", CultureInfo.InvariantCulture),
            point.Y.ToString("G17", CultureInfo.InvariantCulture),
            point.Z.ToString("G17", CultureInfo.InvariantCulture));

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
