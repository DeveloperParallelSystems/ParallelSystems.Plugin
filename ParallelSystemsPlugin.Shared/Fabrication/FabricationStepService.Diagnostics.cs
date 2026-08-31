using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal sealed class FabricationStepGeometryProbeReport
    {
        public string ProbeVersion { get; set; } = "2";
        public bool IsReadOnly { get; set; } = true;
        public double ShortCurveToleranceFeet { get; set; }
        public double ShortCurveToleranceMillimetres { get; set; }
        public IList<FabricationBranchGeometryProbe> ShapedBranches { get; set; } =
            new List<FabricationBranchGeometryProbe>();
        public IList<FabricationDiagnosticIssueSnapshot> Issues { get; set; } =
            new List<FabricationDiagnosticIssueSnapshot>();
    }

    internal sealed class FabricationDiagnosticIssueSnapshot
    {
        public string Severity { get; set; }
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class FabricationBranchGeometryProbe
    {
        public long FittingId { get; set; }
        public string FittingName { get; set; }
        public bool ConnectionResolved { get; set; }
        public FabricationBranchConnectionProbe Connection { get; set; }
        public FabricationBranchFrameProbe Frame { get; set; }
        public FabricationBranchDerivedProbe Derived { get; set; }
        public FabricationConnectorBoreProbe OutletBore { get; set; }
        public IList<FabricationRingProbe> WorldRings { get; set; } =
            new List<FabricationRingProbe>();
        public IList<FabricationRingProbe> LocalRings { get; set; } =
            new List<FabricationRingProbe>();
        public IList<FabricationBRepLayoutProbe> LayoutAttempts { get; set; } =
            new List<FabricationBRepLayoutProbe>();
        public FabricationAdaptiveFallbackProbe AdaptiveFallback { get; set; }
        public FabricationBranchTopologyProbe Topology { get; set; }
        public bool GeometrySucceeded { get; set; }
        public int GeneratedGeometryObjectCount { get; set; }
        public int GeneratedSolidCount { get; set; }
        public int GeneratedFaceCount { get; set; }
        public int MaximumExpectedStepFaceCount { get; set; }
        public string FinalStatus { get; set; }
        public string FinalNotes { get; set; }
        public string FinalError { get; set; }
        public IList<FabricationDiagnosticIssueSnapshot> GeometryIssues { get; set; } =
            new List<FabricationDiagnosticIssueSnapshot>();
    }

    internal sealed class FabricationBranchConnectionProbe
    {
        public long HeaderPipeId { get; set; }
        public long BranchPipeId { get; set; }
        public long BranchConnectedElementId { get; set; }
        public string HeaderResolutionSource { get; set; }
        public string BranchDimensionSource { get; set; }
        public bool HeaderPipeIsCalculationContextOnly { get; set; }
        public bool IsStandaloneComponent { get; set; }
        public FabricationPipeDimensionProbe HeaderDimensions { get; set; }
        public FabricationPipeDimensionProbe BranchDimensions { get; set; }
        public FabricationPointProbe HeaderConnectorOrigin { get; set; }
        public FabricationPointProbe HeaderConnectorMatchOrigin { get; set; }
        public FabricationPointProbe OutletConnectorOrigin { get; set; }
        public FabricationVectorProbe OutletInwardDirection { get; set; }
        public FabricationVectorProbe HeaderInwardDirection { get; set; }
        public FabricationPointProbe HeaderAxisStart { get; set; }
        public FabricationVectorProbe HeaderAxisDirection { get; set; }
        public double HeaderAxisLengthFeet { get; set; }
        public double HeaderAxisLengthMillimetres { get; set; }
    }

    internal sealed class FabricationPipeDimensionProbe
    {
        public double NominalDiameterFeet { get; set; }
        public double NominalDiameterMillimetres { get; set; }
        public double OutsideDiameterFeet { get; set; }
        public double OutsideDiameterMillimetres { get; set; }
        public double InsideDiameterFeet { get; set; }
        public double InsideDiameterMillimetres { get; set; }
        public double WallThicknessFeet { get; set; }
        public double WallThicknessMillimetres { get; set; }
        public string SourceDescription { get; set; }
    }

    internal sealed class FabricationBranchFrameProbe
    {
        public FabricationPointProbe ResolvedSurfaceOrigin { get; set; }
        public FabricationVectorProbe BranchInwardAxis { get; set; }
        public FabricationVectorProbe RadialInwardDirection { get; set; }
        public FabricationPointProbe HeaderAxisPoint { get; set; }
        public FabricationVectorProbe BranchAxis { get; set; }
        public FabricationVectorProbe RadialX { get; set; }
        public FabricationVectorProbe RadialY { get; set; }
        public FabricationVectorProbe HeaderAxis { get; set; }
        public FabricationPointProbe OutletOrigin { get; set; }
        public FabricationPointProbe LocalHeaderAxisPoint { get; set; }
        public FabricationVectorProbe LocalHeaderAxisDirection { get; set; }
    }

    internal sealed class FabricationBranchDerivedProbe
    {
        public int SegmentCount { get; set; }
        public double OutsideRadiusMillimetres { get; set; }
        public double InsideRadiusMillimetres { get; set; }
        public double SaddleRootRadiusMillimetres { get; set; }
        public double RadialBevelMillimetres { get; set; }
        public double SaddleBevelDepthMillimetres { get; set; }
        public double OutletToHeaderDistanceMillimetres { get; set; }
        public bool OutletShouldChamfer { get; set; }
        public double OutletRootFaceMillimetres { get; set; }
        public double OutletRootRadiusMillimetres { get; set; }
        public double OutletBevelDepthMillimetres { get; set; }
        public double HeaderOutsideRadiusMillimetres { get; set; }
        public double ShortCurveToleranceMillimetres { get; set; }
        public double MinimumAcceptedBRepBoundaryLengthMillimetres { get; set; }
        public double MaximumAcceptedDeviationMillimetres { get; set; }
    }

    internal sealed class FabricationConnectorBoreProbe
    {
        public bool Present { get; set; }
        public FabricationPointProbe Origin { get; set; }
        public FabricationPointProbe OriginalConnectorOrigin { get; set; }
        public FabricationVectorProbe OutwardDirection { get; set; }
        public double NominalDiameterMillimetres { get; set; }
        public double OutsideDiameterMillimetres { get; set; }
        public double InsideDiameterMillimetres { get; set; }
        public double WallThicknessMillimetres { get; set; }
        public double RootFaceMillimetres { get; set; }
        public long ConnectedElementId { get; set; }
        public string ConnectedElementName { get; set; }
        public bool ShouldChamfer { get; set; }
        public bool IsShapedBranchHeaderSide { get; set; }
        public bool IsShapedBranchOutletSide { get; set; }
        public bool IsSynthetic { get; set; }
        public bool UsePhysicalFaceSearch { get; set; }
        public string ConnectionLabel { get; set; }
        public string EndPreparationDescription { get; set; }
        public string SourceDescription { get; set; }
    }

    internal sealed class FabricationRingProbe
    {
        public string Name { get; set; }
        public int Count { get; set; }
        public IList<FabricationPointProbe> Points { get; set; } =
            new List<FabricationPointProbe>();
        public double MinimumAdjacentDistanceMillimetres { get; set; }
        public double MaximumAdjacentDistanceMillimetres { get; set; }
        public FabricationPointProbe Sample25 { get; set; }
    }

    internal sealed class FabricationBRepLayoutProbe
    {
        public string Context { get; set; }
        public int TopologicalPatchCount { get; set; }
        public int SampleCount { get; set; }
        public int PreferredSplineSpanCount { get; set; }
        public int FallbackSplineSpanCount { get; set; }
        public double ShortCurveToleranceMillimetres { get; set; }
        public double MinimumAcceptedLengthMillimetres { get; set; }
        public double MaximumAcceptedDeviationMillimetres { get; set; }
        public IList<int> BandDegrees { get; set; } = new List<int>();
        public IList<int> BandRowCounts { get; set; } = new List<int>();
        public IList<FabricationBRepOffsetProbe> Offsets { get; set; } =
            new List<FabricationBRepOffsetProbe>();
        public IList<FabricationAcceptedLayoutProbe> AcceptedLayouts { get; set; } =
            new List<FabricationAcceptedLayoutProbe>();
        public double? BestObservedClearanceMillimetres { get; set; }
        public string BestObservedClearanceContext { get; set; }
        public double? BestObservedDeviationMillimetres { get; set; }
        public string BestObservedDeviationContext { get; set; }
        public string Error { get; set; }
        public bool Succeeded { get; set; }
    }

    internal sealed class FabricationBRepOffsetProbe
    {
        public int SplineSpanCount { get; set; }
        public int Offset { get; set; }
        public bool PassedClearance { get; set; }
        public double? MinimumClearanceMillimetres { get; set; }
        public string MinimumContext { get; set; }
        public FabricationBRepWitnessProbe Witness { get; set; }
    }

    internal sealed class FabricationBRepWitnessProbe
    {
        public string Kind { get; set; }
        public int BandIndex { get; set; }
        public int PatchIndex { get; set; }
        public int SampleIndex { get; set; }
        public int FirstRowIndex { get; set; }
        public int SecondRowIndex { get; set; }
        public FabricationPointProbe FirstPoint { get; set; }
        public FabricationPointProbe SecondPoint { get; set; }
        public double DistanceMillimetres { get; set; }
        public double RequiredMillimetres { get; set; }
        public double ClearanceMillimetres { get; set; }
    }

    internal sealed class FabricationAcceptedLayoutProbe
    {
        public int SplineSpanCount { get; set; }
        public int PatchStartOffset { get; set; }
        public double MinimumClearanceMillimetres { get; set; }
        public double MaximumDeviationMillimetres { get; set; }
    }

    internal sealed class FabricationBranchTopologyProbe
    {
        public int TopologicalPatchCount { get; set; }
        public int SurfaceBandCount { get; set; }
        public int ExpectedStepFaceCount { get; set; }
        public int CircumferentialSplineSpanCount { get; set; }
        public bool UsedAnalyticStraightCylinders { get; set; }
        public bool UsedSeamFreeAnalyticCylinders { get; set; }
        public bool UsedContinuousTopSurfaces { get; set; }
        public bool UsedMergedSmoothBodyBands { get; set; }
        public bool UsedAdaptiveTessellatedFallback { get; set; }
    }

    internal sealed class FabricationAdaptiveFallbackProbe
    {
        public bool Attempted { get; set; }
        public bool Succeeded { get; set; }
        public int GeneratedFaceCount { get; set; }
        public int MaximumVSubdivisions { get; set; }
        public double MaximumObservedDeviationMillimetres { get; set; }
        public string Error { get; set; }
    }

    internal sealed class FabricationPointProbe
    {
        public double[] Feet { get; set; }
        public double[] Millimetres { get; set; }
    }

    internal sealed class FabricationVectorProbe
    {
        public double[] Value { get; set; }
        public double Length { get; set; }
    }

    internal static partial class FabricationStepService
    {
        [ThreadStatic]
        private static FabricationBranchGeometryProbe activeBranchDiagnosticProbe;

        internal static bool IsShapedBranchForDiagnostics(
            Document doc,
            Element element)
        {
            return IsShapedBranchLike(doc, element);
        }

        internal static FabricationStepGeometryProbeReport BuildGeometryDiagnosticProbe(
            Document doc,
            IList<ElementId> sourceElementIds,
            IList<ElementId> calculationContextElementIds,
            IDictionary<ElementId, ElementId> explicitHeaderPipeIdsByBranch)
        {
            FabricationStepGeometryProbeReport report =
                new FabricationStepGeometryProbeReport();

            if (doc == null)
                return report;

            report.ShortCurveToleranceFeet =
                doc.Application.ShortCurveTolerance;
            report.ShortCurveToleranceMillimetres =
                doc.Application.ShortCurveTolerance * FeetToMillimetres;

            List<Element> sourceElements =
                (sourceElementIds ?? new List<ElementId>())
                    .Where(x => x != null)
                    .Select(doc.GetElement)
                    .Where(x => x != null)
                    .GroupBy(x => x.Id)
                    .Select(x => x.First())
                    .ToList();

            HashSet<ElementId> selectedSourceIds =
                new HashSet<ElementId>(sourceElements.Select(x => x.Id));

            HashSet<ElementId> calculationIds =
                new HashSet<ElementId>(selectedSourceIds);

            foreach (ElementId id in
                     calculationContextElementIds ?? new List<ElementId>())
            {
                if (id != null)
                    calculationIds.Add(id);
            }

            List<Element> calculationElements =
                calculationIds
                    .Select(doc.GetElement)
                    .Where(x => x != null)
                    .ToList();

            List<FabricationIssue> resolutionIssues =
                new List<FabricationIssue>();

            Dictionary<ElementId, PipeDimensions> pipeDimensions =
                ResolvePipeDimensions(
                    doc,
                    calculationElements,
                    resolutionIssues);

            Dictionary<double, PipeDimensions> dimensionsByNominal =
                BuildNominalDimensionMap(
                    pipeDimensions.Values,
                    resolutionIssues);

            Dictionary<double, PipeDimensions> documentDimensionsByNominal =
                BuildDocumentNominalDimensionMap(
                    doc,
                    sourceElements,
                    pipeDimensions);

            Dictionary<ElementId, ShapedBranchConnection> connections =
                ResolveShapedBranchConnections(
                    doc,
                    sourceElements,
                    calculationElements,
                    pipeDimensions,
                    selectedSourceIds,
                    calculationIds,
                    explicitHeaderPipeIdsByBranch ??
                        new Dictionary<ElementId, ElementId>(),
                    resolutionIssues);

            foreach (FabricationIssue issue in resolutionIssues)
                report.Issues.Add(ToDiagnosticIssue(issue));

            foreach (Element fitting in sourceElements.Where(x =>
                         x != null &&
                         IsShapedBranchLike(doc, x)))
            {
                FabricationBranchGeometryProbe branchProbe =
                    new FabricationBranchGeometryProbe
                    {
                        FittingId = GetDiagnosticElementIdValue(fitting.Id),
                        FittingName = GetElementDisplayName(fitting)
                    };

                report.ShapedBranches.Add(branchProbe);

                ShapedBranchConnection connection;
                if (!connections.TryGetValue(fitting.Id, out connection) ||
                    connection == null)
                {
                    branchProbe.ConnectionResolved = false;
                    branchProbe.FinalError =
                        "The shaped-branch connection could not be resolved by the same resolver used by STEP generation.";
                    continue;
                }

                branchProbe.ConnectionResolved = true;
                branchProbe.Connection = ToConnectionProbe(connection);

                Dictionary<ElementId, PipeDimensions> componentDimensionOverrides =
                    new Dictionary<ElementId, PipeDimensions>();

                if (connection.BranchDimensions != null)
                    componentDimensionOverrides[fitting.Id] = connection.BranchDimensions;

                List<FabricationIssue> geometryIssues =
                    new List<FabricationIssue>();

                activeBranchDiagnosticProbe = branchProbe;

                try
                {
                    FabricationElementGeometry geometry =
                        BuildFittingGeometry(
                            doc,
                            fitting,
                            pipeDimensions,
                            dimensionsByNominal,
                            documentDimensionsByNominal,
                            componentDimensionOverrides,
                            selectedSourceIds,
                            connection,
                            null,
                            geometryIssues);

                    branchProbe.GeometrySucceeded = geometry != null &&
                        !geometryIssues.Any(x =>
                            x.Severity == FabricationIssueSeverity.Blocking);

                    if (geometry != null)
                    {
                        branchProbe.GeneratedGeometryObjectCount =
                            geometry.Geometry == null ? 0 : geometry.Geometry.Count;
                        branchProbe.GeneratedSolidCount =
                            geometry.Geometry == null
                                ? 0
                                : geometry.Geometry.OfType<Solid>().Count();
                        branchProbe.GeneratedFaceCount =
                            geometry.Geometry == null
                                ? 0
                                : geometry.Geometry
                                    .OfType<Solid>()
                                    .Where(x => x != null)
                                    .Sum(x => x.Faces == null ? 0 : x.Faces.Size);
                        branchProbe.MaximumExpectedStepFaceCount =
                            geometry.MaximumExpectedStepFaceCount;
                        branchProbe.FinalStatus = geometry.Status;
                        branchProbe.FinalNotes = geometry.Notes;
                    }
                }
                catch (Exception ex)
                {
                    branchProbe.GeometrySucceeded = false;
                    branchProbe.FinalError = ex.ToString();
                }
                finally
                {
                    activeBranchDiagnosticProbe = null;
                }

                foreach (FabricationIssue issue in geometryIssues)
                {
                    branchProbe.GeometryIssues.Add(ToDiagnosticIssue(issue));

                    if (issue.Severity == FabricationIssueSeverity.Blocking &&
                        string.IsNullOrWhiteSpace(branchProbe.FinalError))
                    {
                        branchProbe.FinalError = issue.Message;
                    }
                }
            }

            return report;
        }

        private static FabricationDiagnosticIssueSnapshot ToDiagnosticIssue(
            FabricationIssue issue)
        {
            if (issue == null)
                return null;

            return new FabricationDiagnosticIssueSnapshot
            {
                Severity = issue.Severity.ToString(),
                ElementId = GetDiagnosticElementIdValue(issue.ElementId),
                ElementName = issue.ElementName,
                Message = issue.Message
            };
        }

        private static long GetDiagnosticElementIdValue(ElementId id)
        {
            if (id == null)
                return -1;

            try
            {
                return RevitApiCompatibility.GetElementIdValue(id);
            }
            catch
            {
                return -1;
            }
        }

        private static FabricationBranchConnectionProbe ToConnectionProbe(
            ShapedBranchConnection connection)
        {
            if (connection == null)
                return null;

            return new FabricationBranchConnectionProbe
            {
                HeaderPipeId = GetDiagnosticElementIdValue(connection.HeaderPipeId),
                BranchPipeId = GetDiagnosticElementIdValue(connection.BranchPipeId),
                BranchConnectedElementId = GetDiagnosticElementIdValue(connection.BranchConnectedElementId),
                HeaderResolutionSource = connection.HeaderResolutionSource,
                BranchDimensionSource = connection.BranchDimensionSource,
                HeaderPipeIsCalculationContextOnly = connection.HeaderPipeIsCalculationContextOnly,
                IsStandaloneComponent = connection.IsStandaloneComponent,
                HeaderDimensions = ToDimensionProbe(connection.HeaderDimensions),
                BranchDimensions = ToDimensionProbe(connection.BranchDimensions),
                HeaderConnectorOrigin = ToPointProbe(connection.HeaderConnectorOrigin),
                HeaderConnectorMatchOrigin = ToPointProbe(connection.HeaderConnectorMatchOrigin),
                OutletConnectorOrigin = ToPointProbe(connection.OutletConnectorOrigin),
                OutletInwardDirection = ToVectorProbe(connection.OutletInwardDirection),
                HeaderInwardDirection = ToVectorProbe(connection.HeaderInwardDirection),
                HeaderAxisStart = ToPointProbe(connection.HeaderAxisStart),
                HeaderAxisDirection = ToVectorProbe(connection.HeaderAxisDirection),
                HeaderAxisLengthFeet = connection.HeaderAxisLength,
                HeaderAxisLengthMillimetres = connection.HeaderAxisLength * FeetToMillimetres
            };
        }

        private static FabricationPipeDimensionProbe ToDimensionProbe(
            PipeDimensions dimensions)
        {
            if (dimensions == null)
                return null;

            return new FabricationPipeDimensionProbe
            {
                NominalDiameterFeet = dimensions.NominalDiameter,
                NominalDiameterMillimetres = dimensions.NominalDiameter * FeetToMillimetres,
                OutsideDiameterFeet = dimensions.OutsideDiameter,
                OutsideDiameterMillimetres = dimensions.OutsideDiameter * FeetToMillimetres,
                InsideDiameterFeet = dimensions.InsideDiameter,
                InsideDiameterMillimetres = dimensions.InsideDiameter * FeetToMillimetres,
                WallThicknessFeet = dimensions.WallThickness,
                WallThicknessMillimetres = dimensions.WallThickness * FeetToMillimetres,
                SourceDescription = dimensions.SourceDescription
            };
        }

        private static FabricationPointProbe ToPointProbe(XYZ point)
        {
            if (point == null)
                return null;

            return new FabricationPointProbe
            {
                Feet = new[] { point.X, point.Y, point.Z },
                Millimetres = new[]
                {
                    point.X * FeetToMillimetres,
                    point.Y * FeetToMillimetres,
                    point.Z * FeetToMillimetres
                }
            };
        }

        private static FabricationVectorProbe ToVectorProbe(XYZ vector)
        {
            if (vector == null)
                return null;

            return new FabricationVectorProbe
            {
                Value = new[] { vector.X, vector.Y, vector.Z },
                Length = vector.GetLength()
            };
        }

        private static FabricationRingProbe ToRingProbe(
            string name,
            IList<XYZ> ring)
        {
            FabricationRingProbe result = new FabricationRingProbe
            {
                Name = name,
                Count = ring == null ? 0 : ring.Count
            };

            if (ring == null || ring.Count == 0)
                return result;

            double minimumAdjacent = double.MaxValue;
            double maximumAdjacent = 0.0;

            for (int index = 0; index < ring.Count; index++)
            {
                XYZ point = ring[index];
                result.Points.Add(ToPointProbe(point));

                XYZ next = ring[(index + 1) % ring.Count];
                if (point != null && next != null)
                {
                    double distance = point.DistanceTo(next) * FeetToMillimetres;
                    minimumAdjacent = Math.Min(minimumAdjacent, distance);
                    maximumAdjacent = Math.Max(maximumAdjacent, distance);
                }
            }

            result.MinimumAdjacentDistanceMillimetres =
                minimumAdjacent == double.MaxValue ? 0.0 : minimumAdjacent;
            result.MaximumAdjacentDistanceMillimetres = maximumAdjacent;

            if (ring.Count > 25)
                result.Sample25 = ToPointProbe(ring[25]);

            return result;
        }

        private static void DiagnosticCaptureBranchGeometryInputs(
            ShapedBranchConnection branch,
            ConnectorBore outletBore,
            XYZ surfaceOrigin,
            XYZ branchInwardAxis,
            XYZ radialInwardDirection,
            XYZ headerAxisPoint,
            XYZ branchAxis,
            XYZ radialX,
            XYZ radialY,
            XYZ headerAxis,
            XYZ outletOrigin,
            XYZ localHeaderAxisPoint,
            XYZ localHeaderAxisDirection,
            int segmentCount,
            double outsideRadius,
            double insideRadius,
            double saddleRootRadius,
            double radialBevel,
            double saddleBevelDepth,
            double outletToHeaderDistance,
            bool outletShouldChamfer,
            double outletRootFace,
            double outletRootRadius,
            double outletBevelDepth,
            double shortCurveTolerance,
            IDictionary<string, IList<XYZ>> worldRings,
            IDictionary<string, IList<XYZ>> localRings)
        {
            FabricationBranchGeometryProbe probe = activeBranchDiagnosticProbe;
            if (probe == null)
                return;

            probe.Frame = new FabricationBranchFrameProbe
            {
                ResolvedSurfaceOrigin = ToPointProbe(surfaceOrigin),
                BranchInwardAxis = ToVectorProbe(branchInwardAxis),
                RadialInwardDirection = ToVectorProbe(radialInwardDirection),
                HeaderAxisPoint = ToPointProbe(headerAxisPoint),
                BranchAxis = ToVectorProbe(branchAxis),
                RadialX = ToVectorProbe(radialX),
                RadialY = ToVectorProbe(radialY),
                HeaderAxis = ToVectorProbe(headerAxis),
                OutletOrigin = ToPointProbe(outletOrigin),
                LocalHeaderAxisPoint = ToPointProbe(localHeaderAxisPoint),
                LocalHeaderAxisDirection = ToVectorProbe(localHeaderAxisDirection)
            };

            probe.Derived = new FabricationBranchDerivedProbe
            {
                SegmentCount = segmentCount,
                OutsideRadiusMillimetres = outsideRadius * FeetToMillimetres,
                InsideRadiusMillimetres = insideRadius * FeetToMillimetres,
                SaddleRootRadiusMillimetres = saddleRootRadius * FeetToMillimetres,
                RadialBevelMillimetres = radialBevel * FeetToMillimetres,
                SaddleBevelDepthMillimetres = saddleBevelDepth * FeetToMillimetres,
                OutletToHeaderDistanceMillimetres = outletToHeaderDistance * FeetToMillimetres,
                OutletShouldChamfer = outletShouldChamfer,
                OutletRootFaceMillimetres = outletRootFace * FeetToMillimetres,
                OutletRootRadiusMillimetres = outletRootRadius * FeetToMillimetres,
                OutletBevelDepthMillimetres = outletBevelDepth * FeetToMillimetres,
                HeaderOutsideRadiusMillimetres =
                    branch != null && branch.HeaderDimensions != null
                        ? branch.HeaderDimensions.OutsideDiameter * FeetToMillimetres / 2.0
                        : 0.0,
                ShortCurveToleranceMillimetres = shortCurveTolerance * FeetToMillimetres,
                MinimumAcceptedBRepBoundaryLengthMillimetres =
                    shortCurveTolerance * 1.05 * FeetToMillimetres,
                MaximumAcceptedDeviationMillimetres =
                    SmoothBRepMaximumDeviationMillimetres
            };

            probe.OutletBore = ToConnectorBoreProbe(outletBore);

            probe.WorldRings.Clear();
            foreach (KeyValuePair<string, IList<XYZ>> pair in
                     worldRings ?? new Dictionary<string, IList<XYZ>>())
            {
                probe.WorldRings.Add(ToRingProbe(pair.Key, pair.Value));
            }

            probe.LocalRings.Clear();
            foreach (KeyValuePair<string, IList<XYZ>> pair in
                     localRings ?? new Dictionary<string, IList<XYZ>>())
            {
                probe.LocalRings.Add(ToRingProbe(pair.Key, pair.Value));
            }
        }

        private static FabricationConnectorBoreProbe ToConnectorBoreProbe(
            ConnectorBore bore)
        {
            if (bore == null)
                return new FabricationConnectorBoreProbe { Present = false };

            return new FabricationConnectorBoreProbe
            {
                Present = true,
                Origin = ToPointProbe(bore.Origin),
                OriginalConnectorOrigin = ToPointProbe(bore.OriginalConnectorOrigin),
                OutwardDirection = ToVectorProbe(bore.OutwardDirection),
                NominalDiameterMillimetres = bore.NominalDiameter * FeetToMillimetres,
                OutsideDiameterMillimetres = bore.OutsideDiameter * FeetToMillimetres,
                InsideDiameterMillimetres = bore.InsideDiameter * FeetToMillimetres,
                WallThicknessMillimetres = bore.WallThickness * FeetToMillimetres,
                RootFaceMillimetres = bore.RootFaceMillimetres,
                ConnectedElementId = GetDiagnosticElementIdValue(bore.ConnectedElementId),
                ConnectedElementName = bore.ConnectedElementName,
                ShouldChamfer = bore.ShouldChamfer,
                IsShapedBranchHeaderSide = bore.IsShapedBranchHeaderSide,
                IsShapedBranchOutletSide = bore.IsShapedBranchOutletSide,
                IsSynthetic = bore.IsSynthetic,
                UsePhysicalFaceSearch = bore.UsePhysicalFaceSearch,
                ConnectionLabel = bore.ConnectionLabel,
                EndPreparationDescription = bore.EndPreparationDescription,
                SourceDescription = bore.SourceDescription
            };
        }

        private static FabricationBRepLayoutProbe DiagnosticBeginLayoutProbe(
            string context,
            int topologicalPatchCount,
            int sampleCount,
            int preferredSplineSpanCount,
            int fallbackSplineSpanCount,
            double shortCurveTolerance)
        {
            FabricationBranchGeometryProbe branchProbe = activeBranchDiagnosticProbe;
            if (branchProbe == null)
                return null;

            FabricationBRepLayoutProbe probe = new FabricationBRepLayoutProbe
            {
                Context = context,
                TopologicalPatchCount = topologicalPatchCount,
                SampleCount = sampleCount,
                PreferredSplineSpanCount = preferredSplineSpanCount,
                FallbackSplineSpanCount = fallbackSplineSpanCount,
                ShortCurveToleranceMillimetres = shortCurveTolerance * FeetToMillimetres,
                MinimumAcceptedLengthMillimetres = shortCurveTolerance * 1.05 * FeetToMillimetres,
                MaximumAcceptedDeviationMillimetres = SmoothBRepMaximumDeviationMillimetres
            };

            branchProbe.LayoutAttempts.Add(probe);
            return probe;
        }

        private static void DiagnosticCaptureTopologyResult(
            int topologicalPatchCount,
            int surfaceBandCount,
            int expectedStepFaceCount,
            int circumferentialSplineSpanCount,
            bool usedAnalyticStraightCylinders,
            bool usedSeamFreeAnalyticCylinders,
            bool usedContinuousTopSurfaces,
            bool usedMergedSmoothBodyBands,
            bool usedAdaptiveTessellatedFallback)
        {
            FabricationBranchGeometryProbe probe = activeBranchDiagnosticProbe;
            if (probe == null)
                return;

            probe.Topology = new FabricationBranchTopologyProbe
            {
                TopologicalPatchCount = topologicalPatchCount,
                SurfaceBandCount = surfaceBandCount,
                ExpectedStepFaceCount = expectedStepFaceCount,
                CircumferentialSplineSpanCount = circumferentialSplineSpanCount,
                UsedAnalyticStraightCylinders = usedAnalyticStraightCylinders,
                UsedSeamFreeAnalyticCylinders = usedSeamFreeAnalyticCylinders,
                UsedContinuousTopSurfaces = usedContinuousTopSurfaces,
                UsedMergedSmoothBodyBands = usedMergedSmoothBodyBands,
                UsedAdaptiveTessellatedFallback = usedAdaptiveTessellatedFallback
            };
        }

        private static void DiagnosticCaptureAdaptiveFallback(
            bool attempted,
            bool succeeded,
            int generatedFaceCount,
            int maximumVSubdivisions,
            double maximumObservedDeviation,
            string error)
        {
            FabricationBranchGeometryProbe probe = activeBranchDiagnosticProbe;
            if (probe == null)
                return;

            probe.AdaptiveFallback = new FabricationAdaptiveFallbackProbe
            {
                Attempted = attempted,
                Succeeded = succeeded,
                GeneratedFaceCount = generatedFaceCount,
                MaximumVSubdivisions = maximumVSubdivisions,
                MaximumObservedDeviationMillimetres =
                    maximumObservedDeviation * FeetToMillimetres,
                Error = error
            };
        }
    }
}
