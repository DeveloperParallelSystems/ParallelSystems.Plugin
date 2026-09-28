using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal enum AssemblyDocumentationViewKind
    {
        Orthographic3D,
        DetailSection,
        PartList,
        MaterialTakeoff,
        SingleCategorySchedule
    }

    internal sealed class AssemblyDocumentationPlan
    {
        public AssemblyDocumentationPlan(
            long sourceAssemblyId,
            string sourceAssemblyName,
            string targetAssemblyName,
            IEnumerable<AssemblyDocumentationViewPlan> views,
            AssemblyDocumentationSheetPlan sheet,
            IEnumerable<AssemblyDocumentationCategoryEItem> categoryEItems,
            string sourceTransformSignature)
        {
            SourceAssemblyId = sourceAssemblyId;
            SourceAssemblyName = sourceAssemblyName ?? string.Empty;
            TargetAssemblyName = targetAssemblyName ?? string.Empty;
            Views = ReadOnly(views);
            Sheet = sheet;
            CategoryEItems = ReadOnly(categoryEItems);
            SourceTransformSignature = sourceTransformSignature ?? string.Empty;
            Signature = BuildSignature();
        }

        public long SourceAssemblyId { get; }
        public string SourceAssemblyName { get; }
        public string TargetAssemblyName { get; }
        public IReadOnlyList<AssemblyDocumentationViewPlan> Views { get; }
        public AssemblyDocumentationSheetPlan Sheet { get; }
        public IReadOnlyList<AssemblyDocumentationCategoryEItem> CategoryEItems { get; }
        public string SourceTransformSignature { get; }
        public string Signature { get; }
        public bool HasDocumentation => Views.Count > 0 || Sheet != null;

        public string Summary
        {
            get
            {
                int viewCount = Views.Count(view => !view.IsSchedule);
                int scheduleCount = Views.Count(view => view.IsSchedule) +
                    (Sheet?.Schedules.Count(schedule => !schedule.IsSourceAssemblyOwned) ?? 0);
                int legendCount = Sheet?.Viewports.Count(viewport => viewport.IsReusableLegend) ?? 0;
                string legendNames = Sheet == null
                    ? string.Empty
                    : string.Join(", ", Sheet.Viewports
                        .Where(viewport => viewport.IsReusableLegend)
                        .Select(viewport => viewport.ViewName)
                        .Distinct(StringComparer.Ordinal));
                string scheduleNames = string.Join(", ", Views
                    .Where(view => view.IsSchedule)
                    .Select(view => view.SourceName)
                    .Concat(Sheet?.Schedules.Select(schedule => schedule.ScheduleName) ??
                        Enumerable.Empty<string>())
                    .Distinct(StringComparer.Ordinal));
                string orientations = string.Join(", ", Views
                    .Where(view => !view.IsSchedule)
                    .Select(view => view.Orientation.HasValue
                        ? view.Orientation.Value.ToString()
                        : view.Kind.ToString()));
                List<AssemblyDocumentationViewAnnotationItem> viewAnnotations = Views
                    .SelectMany(view => view.Annotations.Items)
                    .ToList();
                return !HasDocumentation
                    ? "None"
                    : "Views " + viewCount +
                      (orientations.Length == 0 ? string.Empty : " [" + orientations + "]") +
                      " • Sheets " + (Sheet == null ? 0 : 1) +
                      " • Legends " + legendCount +
                      (legendNames.Length == 0 ? string.Empty : " [" + legendNames + "]") +
                      " • Schedules " + scheduleCount +
                      (scheduleNames.Length == 0 ? string.Empty : " [" + scheduleNames + "]") +
                      " • View annotation roots " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot) +
                      " • View tags " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentTag) +
                      " • Reference annotations " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension ||
                          item.Kind == AssemblyDocumentationViewAnnotationKind.Dimension ||
                          item.Kind == AssemblyDocumentationViewAnnotationKind.SpotDimension ||
                          item.Kind == AssemblyDocumentationViewAnnotationKind.MultiReferenceAnnotation) +
                      " • Dimensions deferred " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension) +
                      " • Revit view infrastructure " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.RevitGeneratedInfrastructure) +
                      " • Unsupported view items " + viewAnnotations.Count(item =>
                          item.Kind == AssemblyDocumentationViewAnnotationKind.Unsupported) +
                      " • Sheet annotations " + CategoryEItems.Count(item =>
                          item.Disposition == AssemblyDocumentationSheetOwnedItemDisposition.CopyRoot) +
                      " • Group members " + CategoryEItems.Count(item =>
                          item.Disposition == AssemblyDocumentationSheetOwnedItemDisposition.CopiedWithGroup) +
                      " • Revit infrastructure " + CategoryEItems.Count(item =>
                          item.Disposition == AssemblyDocumentationSheetOwnedItemDisposition.RevitGeneratedInfrastructure) +
                      " • Unsupported " + CategoryEItems.Count(item =>
                          item.Disposition == AssemblyDocumentationSheetOwnedItemDisposition.Unsupported) +
                      (Sheet == null ? string.Empty : " • Title block " + Sheet.TitleBlockTypeDisplay);
            }
        }

        public IEnumerable<ProposedDocumentationName> ProposedUniqueNames()
        {
            foreach (AssemblyDocumentationViewPlan view in Views)
            {
                if (!string.IsNullOrWhiteSpace(view.TargetName))
                {
                    yield return new ProposedDocumentationName(
                        SourceAssemblyName,
                        DocumentationNameKind.ViewName,
                        view.TargetName);
                }
            }

            if (Sheet != null && !string.IsNullOrWhiteSpace(Sheet.TargetSheetNumber))
            {
                yield return new ProposedDocumentationName(
                    SourceAssemblyName,
                    DocumentationNameKind.SheetNumber,
                    Sheet.TargetSheetNumber);
            }
        }

        private string BuildSignature()
        {
            return string.Join("||", new[]
            {
                SourceAssemblyId.ToString(CultureInfo.InvariantCulture),
                SourceAssemblyName,
                TargetAssemblyName,
                SourceTransformSignature,
                string.Join("|", Views.OrderBy(view => view.SourceViewId).Select(view => view.Signature)),
                Sheet?.Signature ?? "<no-sheet>",
                string.Join("|", CategoryEItems.OrderBy(item => item.ElementId).Select(item => item.Signature))
            });
        }

        private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
            new ReadOnlyCollection<T>((values ?? Enumerable.Empty<T>()).ToList());
    }

    internal sealed class AssemblyDocumentationViewPlan
    {
        public AssemblyDocumentationViewPlan(
            long sourceViewId,
            string sourceName,
            string targetName,
            ViewType viewType,
            AssemblyDocumentationViewKind kind,
            AssemblyDetailViewOrientation? orientation,
            long templateId,
            int scale,
            ViewDetailLevel detailLevel,
            ViewDiscipline discipline,
            bool cropBoxActive,
            bool cropBoxVisible,
            AssemblyDocumentationBoundingBoxSnapshot cropBox,
            bool sectionBoxActive,
            AssemblyDocumentationBoundingBoxSnapshot sectionBox,
            AssemblyDocumentationXyzSnapshot viewDirection,
            AssemblyDocumentationXyzSnapshot upDirection,
            AssemblyDocumentationXyzSnapshot rightDirection,
            AssemblyDocumentationXyzSnapshot eyePosition,
            bool source3DOrientationLocked,
            long scheduleCategoryId,
            AssemblyDocumentationScheduleDefinitionPlan scheduleDefinition,
            AssemblyDocumentationViewAnnotationPlan annotations)
        {
            SourceViewId = sourceViewId;
            SourceName = sourceName ?? string.Empty;
            TargetName = targetName;
            ViewType = viewType;
            Kind = kind;
            Orientation = orientation;
            TemplateId = templateId;
            Scale = scale;
            DetailLevel = detailLevel;
            Discipline = discipline;
            CropBoxActive = cropBoxActive;
            CropBoxVisible = cropBoxVisible;
            CropBox = cropBox;
            SectionBoxActive = sectionBoxActive;
            SectionBox = sectionBox;
            ViewDirection = viewDirection;
            UpDirection = upDirection;
            RightDirection = rightDirection;
            EyePosition = eyePosition;
            Source3DOrientationLocked = source3DOrientationLocked;
            ScheduleCategoryId = scheduleCategoryId;
            ScheduleDefinition = scheduleDefinition;
            Annotations = annotations ?? new AssemblyDocumentationViewAnnotationPlan(
                sourceViewId,
                Enumerable.Empty<AssemblyDocumentationViewAnnotationItem>());
            Signature = BuildSignature();
        }

        public long SourceViewId { get; }
        public string SourceName { get; }
        public string TargetName { get; }
        public ViewType ViewType { get; }
        public AssemblyDocumentationViewKind Kind { get; }
        public AssemblyDetailViewOrientation? Orientation { get; }
        public long TemplateId { get; }
        public int Scale { get; }
        public ViewDetailLevel DetailLevel { get; }
        public ViewDiscipline Discipline { get; }
        public bool CropBoxActive { get; }
        public bool CropBoxVisible { get; }
        public AssemblyDocumentationBoundingBoxSnapshot CropBox { get; }
        public bool SectionBoxActive { get; }
        public AssemblyDocumentationBoundingBoxSnapshot SectionBox { get; }
        public AssemblyDocumentationXyzSnapshot ViewDirection { get; }
        public AssemblyDocumentationXyzSnapshot UpDirection { get; }
        public AssemblyDocumentationXyzSnapshot RightDirection { get; }
        public AssemblyDocumentationXyzSnapshot EyePosition { get; }
        public bool Source3DOrientationLocked { get; }
        public long ScheduleCategoryId { get; }
        public AssemblyDocumentationScheduleDefinitionPlan ScheduleDefinition { get; }
        public AssemblyDocumentationViewAnnotationPlan Annotations { get; }
        public string Signature { get; }
        public bool IsSchedule => Kind == AssemblyDocumentationViewKind.PartList ||
            Kind == AssemblyDocumentationViewKind.MaterialTakeoff ||
            Kind == AssemblyDocumentationViewKind.SingleCategorySchedule;

        // Changed by Jhay: centralize the lock requirement for every currently
        // supported annotation class that can be created/copied into a View3D.
        public bool RequiresLocked3DOrientation =>
            Kind == AssemblyDocumentationViewKind.Orthographic3D &&
            Annotations.Items.Any(item =>
                item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentCopyRoot ||
                item.Kind == AssemblyDocumentationViewAnnotationKind.IndependentTag ||
                item.Kind == AssemblyDocumentationViewAnnotationKind.SupportedDeferredDimension);

        public bool Target3DOrientationShouldBeLocked =>
            Kind == AssemblyDocumentationViewKind.Orthographic3D &&
            (Source3DOrientationLocked || RequiresLocked3DOrientation);

        private string BuildSignature() => string.Join(";", new[]
        {
            SourceViewId.ToString(CultureInfo.InvariantCulture), SourceName, TargetName ?? "<auto>",
            ViewType.ToString(), Kind.ToString(), Orientation?.ToString() ?? "<none>",
            TemplateId.ToString(CultureInfo.InvariantCulture), Scale.ToString(CultureInfo.InvariantCulture),
            DetailLevel.ToString(), Discipline.ToString(), CropBoxActive.ToString(), CropBoxVisible.ToString(),
            CropBox?.Signature ?? "<no-crop>", SectionBoxActive.ToString(),
            SectionBox?.Signature ?? "<no-section>", ViewDirection?.Signature ?? string.Empty,
            UpDirection?.Signature ?? string.Empty, RightDirection?.Signature ?? string.Empty,
            EyePosition?.Signature ?? string.Empty, Source3DOrientationLocked.ToString(),
            ScheduleCategoryId.ToString(CultureInfo.InvariantCulture),
            ScheduleDefinition?.Signature ?? "<no-schedule-definition>",
            Annotations.Signature
        });
    }

    internal enum AssemblyDocumentationViewAnnotationKind
    {
        IndependentCopyRoot,
        CopiedWithGroup,
        IndependentTag,
        SupportedDeferredDimension,
        Dimension,
        SpotDimension,
        MultiReferenceAnnotation,
        RevitGeneratedInfrastructure,
        Unsupported
    }

    internal sealed class AssemblyDocumentationViewAnnotationPlan
    {
        public AssemblyDocumentationViewAnnotationPlan(
            long sourceViewId,
            IEnumerable<AssemblyDocumentationViewAnnotationItem> items)
        {
            SourceViewId = sourceViewId;
            Items = new ReadOnlyCollection<AssemblyDocumentationViewAnnotationItem>(
                (items ?? Enumerable.Empty<AssemblyDocumentationViewAnnotationItem>())
                    .OrderBy(item => item.ElementId)
                    .ToList());
            Signature = string.Join("|", Items.Select(item => item.Signature));
        }

        public long SourceViewId { get; }
        public IReadOnlyList<AssemblyDocumentationViewAnnotationItem> Items { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationViewAnnotationItem
    {
        public AssemblyDocumentationViewAnnotationItem(
            long elementId,
            string runtimeType,
            string categoryName,
            long categoryId,
            long typeId,
            long ownerViewId,
            long groupId,
            long copyRootId,
            AssemblyDocumentationViewAnnotationKind kind,
            string contentSignature,
            AssemblyDocumentationTagPlan tag,
            AssemblyDocumentationReferenceAnnotationPlan referenceAnnotation)
        {
            ElementId = elementId;
            RuntimeType = runtimeType ?? string.Empty;
            CategoryName = categoryName ?? "<none>";
            CategoryId = categoryId;
            TypeId = typeId;
            OwnerViewId = ownerViewId;
            GroupId = groupId;
            CopyRootId = copyRootId;
            Kind = kind;
            ContentSignature = contentSignature ?? string.Empty;
            Tag = tag;
            ReferenceAnnotation = referenceAnnotation;
            Signature = string.Join(";", ElementId, RuntimeType, CategoryName, CategoryId,
                TypeId, OwnerViewId, GroupId, CopyRootId, Kind, ContentSignature,
                Tag?.Signature ?? string.Empty,
                ReferenceAnnotation?.Signature ?? string.Empty);
        }

        public long ElementId { get; }
        public string RuntimeType { get; }
        public string CategoryName { get; }
        public long CategoryId { get; }
        public long TypeId { get; }
        public long OwnerViewId { get; }
        public long GroupId { get; }
        public long CopyRootId { get; }
        public AssemblyDocumentationViewAnnotationKind Kind { get; }
        public string ContentSignature { get; }
        public AssemblyDocumentationTagPlan Tag { get; }
        public AssemblyDocumentationReferenceAnnotationPlan ReferenceAnnotation { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationTagPlan
    {
        public AssemblyDocumentationTagPlan(
            long typeId,
            bool hasLeader,
            TagOrientation orientation,
            double rotationAngle,
            AssemblyDocumentationXyzSnapshot headInView,
            LeaderEndCondition leaderEndCondition,
            IEnumerable<AssemblyDocumentationReferencePlan> references)
        {
            TypeId = typeId;
            HasLeader = hasLeader;
            Orientation = orientation;
            RotationAngle = rotationAngle;
            HeadInView = headInView;
            LeaderEndCondition = leaderEndCondition;
            References = new ReadOnlyCollection<AssemblyDocumentationReferencePlan>(
                (references ?? Enumerable.Empty<AssemblyDocumentationReferencePlan>()).ToList());
            Signature = string.Join(";", TypeId, HasLeader, Orientation,
                rotationAngle.ToString("G17", CultureInfo.InvariantCulture),
                HeadInView?.Signature ?? string.Empty, LeaderEndCondition,
                string.Join("|", References.Select(reference => reference.Signature)));
        }

        public long TypeId { get; }
        public bool HasLeader { get; }
        public TagOrientation Orientation { get; }
        public double RotationAngle { get; }
        public AssemblyDocumentationXyzSnapshot HeadInView { get; }
        public LeaderEndCondition LeaderEndCondition { get; }
        public IReadOnlyList<AssemblyDocumentationReferencePlan> References { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationReferenceAnnotationPlan
    {
        public AssemblyDocumentationReferenceAnnotationPlan(
            string curveSignature,
            int segmentCount,
            IEnumerable<AssemblyDocumentationReferencePlan> references,
            string valueText,
            AssemblyDocumentationXyzSnapshot lineStartInView = null,
            AssemblyDocumentationXyzSnapshot lineEndInView = null,
            IDictionary<string, string> formatting = null,
            bool lineIsBound = true,
            AssemblyDocumentationXyzSnapshot lineOriginInView = null,
            AssemblyDocumentationXyzSnapshot lineDirectionInView = null)
        {
            CurveSignature = curveSignature ?? string.Empty;
            SegmentCount = segmentCount;
            References = new ReadOnlyCollection<AssemblyDocumentationReferencePlan>(
                (references ?? Enumerable.Empty<AssemblyDocumentationReferencePlan>()).ToList());
            ValueText = valueText ?? string.Empty;
            LineStartInView = lineStartInView;
            LineEndInView = lineEndInView;
            Formatting = new ReadOnlyDictionary<string, string>(
                new Dictionary<string, string>(formatting ??
                    new Dictionary<string, string>(), StringComparer.Ordinal));
            LineIsBound = lineIsBound;
            LineOriginInView = lineOriginInView;
            LineDirectionInView = lineDirectionInView;
            Signature = string.Join(";", CurveSignature, SegmentCount, ValueText,
                LineStartInView?.Signature ?? string.Empty,
                LineEndInView?.Signature ?? string.Empty,
                LineIsBound,
                LineOriginInView?.Signature ?? string.Empty,
                LineDirectionInView?.Signature ?? string.Empty,
                string.Join("|", Formatting.OrderBy(item => item.Key)
                    .Select(item => item.Key + "=" + item.Value)),
                string.Join("|", References.Select(reference => reference.Signature)));
        }

        public string CurveSignature { get; }
        public int SegmentCount { get; }
        public IReadOnlyList<AssemblyDocumentationReferencePlan> References { get; }
        public string ValueText { get; }
        public AssemblyDocumentationXyzSnapshot LineStartInView { get; }
        public AssemblyDocumentationXyzSnapshot LineEndInView { get; }
        public IReadOnlyDictionary<string, string> Formatting { get; }
        public bool LineIsBound { get; }
        public AssemblyDocumentationXyzSnapshot LineOriginInView { get; }
        public AssemblyDocumentationXyzSnapshot LineDirectionInView { get; }
        public string Signature { get; }
    }

    internal enum AssemblyDocumentationReferenceSemanticKind
    {
        PlanarFace,
        FamilyReference,
        ElementPoint,
        LinearCurve
    }

    internal sealed class AssemblyDocumentationReferenceSemantic
    {
        public AssemblyDocumentationReferenceSemantic(
            AssemblyDocumentationReferenceSemanticKind kind,
            AssemblyDocumentationXyzSnapshot originOrPoint,
            AssemblyDocumentationXyzSnapshot normal,
            double area,
            string familyReferenceType,
            string familyReferenceName,
            string topologySignature = null,
            string stableSemanticPath = null)
        {
            Kind = kind;
            OriginOrPoint = originOrPoint;
            Normal = normal;
            Area = area;
            FamilyReferenceType = familyReferenceType ?? string.Empty;
            FamilyReferenceName = familyReferenceName ?? string.Empty;
            TopologySignature = topologySignature ?? string.Empty;
            StableSemanticPath = stableSemanticPath ?? string.Empty;
            Signature = string.Join(";", Kind,
                OriginOrPoint?.Signature ?? string.Empty,
                Normal?.Signature ?? string.Empty,
                Area.ToString("G17", CultureInfo.InvariantCulture),
                FamilyReferenceType,
                FamilyReferenceName,
                TopologySignature,
                StableSemanticPath);
        }

        public AssemblyDocumentationReferenceSemanticKind Kind { get; }
        public AssemblyDocumentationXyzSnapshot OriginOrPoint { get; }
        public AssemblyDocumentationXyzSnapshot Normal { get; }
        public double Area { get; }
        public string FamilyReferenceType { get; }
        public string FamilyReferenceName { get; }
        public string TopologySignature { get; }
        public string StableSemanticPath { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationReferencePlan
    {
        public AssemblyDocumentationReferencePlan(
            long sourceElementId,
            ElementReferenceType referenceType,
            string stableRepresentation,
            AssemblyDocumentationXyzSnapshot leaderElbowInView,
            AssemblyDocumentationXyzSnapshot leaderEndInView,
            string diagnostics = null,
            AssemblyDocumentationReferenceSemantic semantic = null)
        {
            SourceElementId = sourceElementId;
            ReferenceType = referenceType;
            StableRepresentation = stableRepresentation ?? string.Empty;
            LeaderElbowInView = leaderElbowInView;
            LeaderEndInView = leaderEndInView;
            Diagnostics = diagnostics ?? string.Empty;
            Semantic = semantic;
            Signature = string.Join(";", SourceElementId, ReferenceType,
                StableRepresentation, LeaderElbowInView?.Signature ?? string.Empty,
                LeaderEndInView?.Signature ?? string.Empty, Diagnostics,
                Semantic?.Signature ?? string.Empty);
        }

        public long SourceElementId { get; }
        public ElementReferenceType ReferenceType { get; }
        public string StableRepresentation { get; }
        public AssemblyDocumentationXyzSnapshot LeaderElbowInView { get; }
        public AssemblyDocumentationXyzSnapshot LeaderEndInView { get; }
        public string Diagnostics { get; }
        public AssemblyDocumentationReferenceSemantic Semantic { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationSheetPlan
    {
        public AssemblyDocumentationSheetPlan(
            long sourceSheetId,
            string sourceSheetNumber,
            string targetSheetNumber,
            string sourceSheetName,
            string targetSheetName,
            long titleBlockTypeId,
            string titleBlockTypeDisplay,
            IEnumerable<AssemblyDocumentationViewportPlan> viewports,
            IEnumerable<AssemblyDocumentationSchedulePlacementPlan> schedules,
            int titleBlockRevisionScheduleCount)
        {
            SourceSheetId = sourceSheetId;
            SourceSheetNumber = sourceSheetNumber ?? string.Empty;
            TargetSheetNumber = targetSheetNumber;
            SourceSheetName = sourceSheetName ?? string.Empty;
            TargetSheetName = targetSheetName;
            TitleBlockTypeId = titleBlockTypeId;
            TitleBlockTypeDisplay = titleBlockTypeDisplay ?? "<none>";
            Viewports = ReadOnly(viewports);
            Schedules = ReadOnly(schedules);
            TitleBlockRevisionScheduleCount = titleBlockRevisionScheduleCount;
            Signature = string.Join(";", new[]
            {
                SourceSheetId.ToString(CultureInfo.InvariantCulture), SourceSheetNumber,
                TargetSheetNumber ?? "<auto>", SourceSheetName, TargetSheetName ?? "<auto>",
                TitleBlockTypeId.ToString(CultureInfo.InvariantCulture), TitleBlockTypeDisplay,
                string.Join("|", Viewports.OrderBy(item => item.SourceViewportId).Select(item => item.Signature)),
                string.Join("|", Schedules.OrderBy(item => item.SourceInstanceId).Select(item => item.Signature)),
                TitleBlockRevisionScheduleCount.ToString(CultureInfo.InvariantCulture)
            });
        }

        public long SourceSheetId { get; }
        public string SourceSheetNumber { get; }
        public string TargetSheetNumber { get; }
        public string SourceSheetName { get; }
        public string TargetSheetName { get; }
        public long TitleBlockTypeId { get; }
        public string TitleBlockTypeDisplay { get; }
        public IReadOnlyList<AssemblyDocumentationViewportPlan> Viewports { get; }
        public IReadOnlyList<AssemblyDocumentationSchedulePlacementPlan> Schedules { get; }
        public int TitleBlockRevisionScheduleCount { get; }
        public string Signature { get; }

        private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
            new ReadOnlyCollection<T>((values ?? Enumerable.Empty<T>()).ToList());
    }

    internal sealed class AssemblyDocumentationViewportPlan
    {
        public AssemblyDocumentationViewportPlan(
            long sourceViewportId,
            long sourceViewId,
            bool isReusableLegend,
            string viewName,
            long viewportTypeId,
            ViewportRotation rotation,
            AssemblyDocumentationXyzSnapshot center,
            AssemblyDocumentationXyzSnapshot labelOffset,
            double labelLineLength,
            string positioning)
        {
            SourceViewportId = sourceViewportId;
            SourceViewId = sourceViewId;
            IsReusableLegend = isReusableLegend;
            ViewName = viewName ?? string.Empty;
            ViewportTypeId = viewportTypeId;
            Rotation = rotation;
            Center = center;
            LabelOffset = labelOffset;
            LabelLineLength = labelLineLength;
            Positioning = positioning ?? "<unsupported>";
            Signature = string.Join(";", SourceViewportId, SourceViewId, IsReusableLegend,
                ViewName, ViewportTypeId, Rotation, Center?.Signature, LabelOffset?.Signature,
                labelLineLength.ToString("G17", CultureInfo.InvariantCulture), Positioning);
        }

        public long SourceViewportId { get; }
        public long SourceViewId { get; }
        public bool IsReusableLegend { get; }
        public string ViewName { get; }
        public long ViewportTypeId { get; }
        public ViewportRotation Rotation { get; }
        public AssemblyDocumentationXyzSnapshot Center { get; }
        public AssemblyDocumentationXyzSnapshot LabelOffset { get; }
        public double LabelLineLength { get; }
        public string Positioning { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationSchedulePlacementPlan
    {
        public AssemblyDocumentationSchedulePlacementPlan(
            long sourceInstanceId,
            long sourceScheduleId,
            bool isSourceAssemblyOwned,
            string scheduleName,
            AssemblyDocumentationXyzSnapshot point,
            ViewportRotation rotation,
            int segmentIndex)
        {
            SourceInstanceId = sourceInstanceId;
            SourceScheduleId = sourceScheduleId;
            IsSourceAssemblyOwned = isSourceAssemblyOwned;
            ScheduleName = scheduleName ?? string.Empty;
            Point = point;
            Rotation = rotation;
            SegmentIndex = segmentIndex;
            Signature = string.Join(";", SourceInstanceId, SourceScheduleId, IsSourceAssemblyOwned,
                ScheduleName, Point?.Signature, Rotation, SegmentIndex);
        }

        public long SourceInstanceId { get; }
        public long SourceScheduleId { get; }
        public bool IsSourceAssemblyOwned { get; }
        public string ScheduleName { get; }
        public AssemblyDocumentationXyzSnapshot Point { get; }
        public ViewportRotation Rotation { get; }
        public int SegmentIndex { get; }
        public string Signature { get; }
    }

    internal enum AssemblyDocumentationSheetOwnedItemDisposition
    {
        CopyRoot,
        CopiedWithGroup,
        RevitGeneratedInfrastructure,
        Unsupported
    }

    internal sealed class AssemblyDocumentationCategoryEItem
    {
        public AssemblyDocumentationCategoryEItem(
            long elementId,
            string runtimeType,
            string categoryName,
            long ownerViewId,
            long groupId,
            long copyRootId,
            AssemblyDocumentationSheetOwnedItemDisposition disposition,
            string contentSignature)
        {
            ElementId = elementId;
            RuntimeType = runtimeType ?? string.Empty;
            CategoryName = categoryName ?? "<none>";
            OwnerViewId = ownerViewId;
            GroupId = groupId;
            CopyRootId = copyRootId;
            Disposition = disposition;
            ContentSignature = contentSignature ?? string.Empty;
            Signature = string.Join(";", ElementId, RuntimeType, CategoryName, OwnerViewId,
                GroupId, CopyRootId, Disposition, ContentSignature);
        }

        public long ElementId { get; }
        public string RuntimeType { get; }
        public string CategoryName { get; }
        public long OwnerViewId { get; }
        public long GroupId { get; }
        public long CopyRootId { get; }
        public AssemblyDocumentationSheetOwnedItemDisposition Disposition { get; }
        public string ContentSignature { get; }
        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationScheduleDefinitionPlan
    {
        public AssemblyDocumentationScheduleDefinitionPlan(string signature)
        {
            Signature = signature ?? string.Empty;
        }

        public string Signature { get; }
    }

    internal sealed class AssemblyDocumentationXyzSnapshot
    {
        public AssemblyDocumentationXyzSnapshot(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
            Signature = string.Join(",", G(x), G(y), G(z));
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }
        public string Signature { get; }
        public XYZ ToXyz() => new XYZ(X, Y, Z);
        public static AssemblyDocumentationXyzSnapshot Capture(XYZ value) =>
            value == null ? null : new AssemblyDocumentationXyzSnapshot(value.X, value.Y, value.Z);
        private static string G(double value) => value.ToString("G17", CultureInfo.InvariantCulture);
    }

    internal sealed class AssemblyDocumentationBoundingBoxSnapshot
    {
        public AssemblyDocumentationBoundingBoxSnapshot(
            AssemblyDocumentationXyzSnapshot min,
            AssemblyDocumentationXyzSnapshot max,
            AssemblyDocumentationXyzSnapshot origin,
            AssemblyDocumentationXyzSnapshot basisX,
            AssemblyDocumentationXyzSnapshot basisY,
            AssemblyDocumentationXyzSnapshot basisZ)
        {
            Min = min;
            Max = max;
            Origin = origin;
            BasisX = basisX;
            BasisY = basisY;
            BasisZ = basisZ;
            Signature = string.Join(";", min?.Signature, max?.Signature, origin?.Signature,
                basisX?.Signature, basisY?.Signature, basisZ?.Signature);
        }

        public AssemblyDocumentationXyzSnapshot Min { get; }
        public AssemblyDocumentationXyzSnapshot Max { get; }
        public AssemblyDocumentationXyzSnapshot Origin { get; }
        public AssemblyDocumentationXyzSnapshot BasisX { get; }
        public AssemblyDocumentationXyzSnapshot BasisY { get; }
        public AssemblyDocumentationXyzSnapshot BasisZ { get; }
        public string Signature { get; }

        public static AssemblyDocumentationBoundingBoxSnapshot Capture(BoundingBoxXYZ box)
        {
            if (box == null)
                return null;
            Transform transform = box.Transform ?? Transform.Identity;
            return new AssemblyDocumentationBoundingBoxSnapshot(
                AssemblyDocumentationXyzSnapshot.Capture(box.Min),
                AssemblyDocumentationXyzSnapshot.Capture(box.Max),
                AssemblyDocumentationXyzSnapshot.Capture(transform.Origin),
                AssemblyDocumentationXyzSnapshot.Capture(transform.BasisX),
                AssemblyDocumentationXyzSnapshot.Capture(transform.BasisY),
                AssemblyDocumentationXyzSnapshot.Capture(transform.BasisZ));
        }

        public BoundingBoxXYZ ToBoundingBox()
        {
            var transform = Transform.Identity;
            transform.Origin = Origin.ToXyz();
            transform.BasisX = BasisX.ToXyz();
            transform.BasisY = BasisY.ToXyz();
            transform.BasisZ = BasisZ.ToXyz();
            return new BoundingBoxXYZ { Min = Min.ToXyz(), Max = Max.ToXyz(), Transform = transform };
        }
    }

    internal sealed class AssemblyDocumentationDiscoveryResult
    {
        public AssemblyDocumentationDiscoveryResult(
            AssemblyDocumentationPlan plan,
            IEnumerable<string> issues)
        {
            Plan = plan;
            Issues = new ReadOnlyCollection<string>((issues ?? Enumerable.Empty<string>()).ToList());
        }

        public AssemblyDocumentationPlan Plan { get; }
        public IReadOnlyList<string> Issues { get; }
        public bool IsValid => Plan != null && Issues.Count == 0;
    }

    internal sealed class AssemblyDocumentationEvidence
    {
        public IList<string> Observations { get; } = new List<string>();
        public IList<AssemblyDuplicationInvariant> Invariants { get; } =
            new List<AssemblyDuplicationInvariant>();
    }

    internal sealed class AssemblyDocumentationResult
    {
        public AssemblyDocumentationResult(
            IDictionary<long, long> targetViewIds,
            long? targetSheetId,
            AssemblyDocumentationEvidence evidence)
        {
            TargetViewIds = new ReadOnlyDictionary<long, long>(
                new Dictionary<long, long>(targetViewIds ?? new Dictionary<long, long>()));
            TargetSheetId = targetSheetId;
            Evidence = evidence;
        }

        public IReadOnlyDictionary<long, long> TargetViewIds { get; }
        public long? TargetSheetId { get; }
        public AssemblyDocumentationEvidence Evidence { get; }
    }

    internal sealed class AssemblyDocumentationException : InvalidOperationException
    {
        public AssemblyDocumentationException(
            string stage,
            AssemblyDocumentationEvidence evidence,
            Exception innerException)
            : base(innerException?.Message, innerException)
        {
            Stage = stage;
            Evidence = evidence;
        }

        public string Stage { get; }
        public AssemblyDocumentationEvidence Evidence { get; }
    }

    internal static class AssemblyDocumentationModelIds
    {
        public static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
