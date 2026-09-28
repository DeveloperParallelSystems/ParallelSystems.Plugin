using Autodesk.Revit.DB;
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

                stage = "create target assembly views";
                RunTransaction(document, "Duplicate Assembly Documentation Views", () =>
                {
                    foreach (AssemblyDocumentationViewPlan viewPlan in plan.Views)
                    {
                        View targetView = CreateTargetView(document, target, viewPlan);
                        document.Regenerate();
                        ApplyViewSettings(document, source, target, targetView, viewPlan, evidence);
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
                    DuplicateViewAnnotations(
                        document,
                        plan,
                        targetViewIds,
                        sourceToTargetMemberIds,
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
                    sourceToTargetMemberIds,
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

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
