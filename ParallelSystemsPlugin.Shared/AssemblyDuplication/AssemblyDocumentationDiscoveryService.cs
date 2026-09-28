using Autodesk.Revit.DB;
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
            if (view is View3D threeD)
            {
                sectionActive = threeD.IsSectionBoxActive;
                section = AssemblyDocumentationBoundingBoxSnapshot.Capture(threeD.GetSectionBox());
                ViewOrientation3D sourceOrientation = threeD.GetOrientation();
                eye = AssemblyDocumentationXyzSnapshot.Capture(sourceOrientation.EyePosition);
            }

            AssemblyDocumentationViewAnnotationPlan annotations = view is ViewSchedule
                ? new AssemblyDocumentationViewAnnotationPlan(
                    Id(view.Id),
                    Enumerable.Empty<AssemblyDocumentationViewAnnotationItem>())
                : CaptureViewAnnotations(document, source, view, issues);

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
                    view,
                    element,
                    groupIds,
                    sourceMemberIds);
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
            View view,
            Element element,
            ISet<long> groupIds,
            ISet<long> sourceMemberIds)
        {
            long elementId = Id(element.Id);
            long groupId = Id(element.GroupId);
            long copyRootId = elementId;
            AssemblyDocumentationTagPlan tagPlan = null;
            AssemblyDocumentationReferenceAnnotationPlan referencePlan = null;
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
                referencePlan = CaptureDimensionPlan(document, view, spot);
                copyRootId = Id(ElementId.InvalidElementId);
            }
            else if (element is Dimension dimension)
            {
                kind = AssemblyDocumentationViewAnnotationKind.Dimension;
                referencePlan = CaptureDimensionPlan(document, view, dimension);
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
            else if (element is SketchPlane || element is GraphicsStyle ||
                     element is SunAndShadowSettings)
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
                    ? CaptureViewContentSignature(document, view, element)
                    : string.Empty,
                tagPlan,
                referencePlan);
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
            View view,
            Dimension dimension)
        {
            var references = new List<AssemblyDocumentationReferencePlan>();
            if (dimension.References != null)
            {
                foreach (Reference reference in dimension.References)
                {
                    references.Add(new AssemblyDocumentationReferencePlan(
                        Id(reference.ElementId),
                        reference.ElementReferenceType,
                        TryGet(() => reference.ConvertToStableRepresentation(document), string.Empty),
                        null,
                        null));
                }
            }
            return new AssemblyDocumentationReferenceAnnotationPlan(
                CaptureCurveSignature(view, dimension.Curve),
                dimension.NumberOfSegments,
                references,
                dimension.ValueString ?? string.Empty);
        }

        private static string CaptureViewContentSignature(
            Document document,
            View view,
            Element element)
        {
            string own = CaptureViewElementSignature(view, element);
            if (!(element is Group group))
                return own;
            string members = string.Join("|", group.GetMemberIds()
                .Select(document.GetElement)
                .Where(member => member != null)
                .Select(member => CaptureViewElementSignature(view, member))
                .OrderBy(value => value, StringComparer.Ordinal));
            return own + "|members=" + members;
        }

        private static string CaptureViewElementSignature(View view, Element element)
        {
            BoundingBoxXYZ bounds = TryGet(() => element.get_BoundingBox(view), null);
            return string.Join(";", new[]
            {
                element.GetType().FullName,
                Id(element.Category?.Id ?? ElementId.InvalidElementId).ToString(CultureInfo.InvariantCulture),
                Id(element.GetTypeId()).ToString(CultureInfo.InvariantCulture),
                CaptureViewBoxSignature(view, bounds),
                CaptureViewElementSemantics(view, element)
            });
        }

        // Changed by Jhay: independent view content is validated by its semantic
        // content and placement, not by a count or bounding box alone.
        private static string CaptureViewElementSemantics(View view, Element element)
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
                return "curve=" + CaptureCurveSignature(view, curveElement.GeometryCurve) +
                    "|line-style=" + Id(curveElement.LineStyle?.Id ?? ElementId.InvalidElementId);
            }
            if (element is FilledRegion region)
            {
                return "boundaries=" + string.Join("||", region.GetBoundaries()
                    .Select(loop => string.Join("|", loop.Select(curve =>
                        CaptureCurveSignature(view, curve)))));
            }
            if (element is FamilyInstance family)
            {
                string location = family.Location is LocationPoint point
                    ? "point=" + CaptureViewPoint(view, point.Point).Signature +
                      "|rotation=" + point.Rotation.ToString("G17", CultureInfo.InvariantCulture)
                    : family.Location is LocationCurve curve
                        ? "curve=" + CaptureCurveSignature(view, curve.Curve)
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

        private static string CaptureCurveSignature(View view, Curve curve)
        {
            if (curve == null)
                return "<none>";
            return curve.GetType().FullName + ";" + string.Join(";", curve.Tessellate()
                .Select(point => CaptureViewPoint(view, point).Signature));
        }

        private static string BuildViewReferenceMappingIssue(
            View view,
            AssemblyDocumentationViewAnnotationItem item) =>
            "VIEW ANNOTATION REFERENCE MAPPING REQUIRED | view " + Id(view.Id) +
            " '" + view.Name + "' | ElementId " + item.ElementId + " | " + item.RuntimeType +
            " | category " + item.CategoryName + " | owner " + item.OwnerViewId +
            " | references " + DescribeReferences(item.ReferenceAnnotation?.References) + ".";

        private static string BuildUnsupportedViewAnnotationIssue(
            View view,
            AssemblyDocumentationViewAnnotationItem item) =>
            "VIEW ANNOTATION UNSUPPORTED | view " + Id(view.Id) + " '" + view.Name +
            "' | ElementId " + item.ElementId + " | " + item.RuntimeType +
            " | category " + item.CategoryName + " | owner " + item.OwnerViewId +
            " | references " + DescribeReferences(item.Tag?.References) + ".";

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
