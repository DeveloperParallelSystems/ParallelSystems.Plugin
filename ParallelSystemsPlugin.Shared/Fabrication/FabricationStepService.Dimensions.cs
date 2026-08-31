using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        private static Dictionary<ElementId, PipeDimensions>
            ResolvePipeDimensions(
                Document doc,
                IList<Element> sourceElements,
                IList<FabricationIssue> issues)
        {
            Dictionary<ElementId, PipeDimensions> result =
                new Dictionary<ElementId, PipeDimensions>();

            foreach (Pipe pipe in sourceElements.OfType<Pipe>())
            {
                PipeDimensions dimensions;
                string error;

                if (!TryResolvePipeDimensions(
                        doc,
                        pipe,
                        out dimensions,
                        out error))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = pipe.Id,
                        ElementName = GetElementDisplayName(pipe),
                        Message = error
                    });

                    continue;
                }

                result[pipe.Id] = dimensions;
            }

            return result;
        }

        private static Dictionary<double, PipeDimensions>
            BuildNominalDimensionMap(
                IEnumerable<PipeDimensions> dimensions,
                IList<FabricationIssue> issues)
        {
            Dictionary<double, PipeDimensions> result =
                new Dictionary<double, PipeDimensions>();

            foreach (IGrouping<double, PipeDimensions> group in dimensions
                         .Where(x => x != null && x.NominalDiameter > 0)
                         .GroupBy(x => RoundDiameterKey(x.NominalDiameter)))
            {
                List<PipeDimensions> candidates = group.ToList();
                PipeDimensions first = candidates[0];

                bool hasConflictingInsideDiameters = candidates.Any(x =>
                    Math.Abs(
                        x.InsideDiameter - first.InsideDiameter) >
                    Math.Max(
                        DiameterTolerance,
                        first.InsideDiameter * 0.001));

                if (hasConflictingInsideDiameters)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Warning,
                        Message =
                            "Multiple inside diameters were found for nominal size " +
                            FormatMillimetres(group.Key) + ". " +
                            "The plugin will not guess a bore for an unconnected fitting of this size. " +
                            "Connect the fitting to a selected pipe or provide an Inside Diameter parameter on the fitting."
                    });

                    continue;
                }

                result[group.Key] = first;
            }

            return result;
        }

        private static Dictionary<double, PipeDimensions>
            BuildDocumentNominalDimensionMap(
                Document doc,
                IList<Element> sourceElements,
                IDictionary<ElementId, PipeDimensions> selectedDimensions)
        {
            Dictionary<double, List<PipeDimensions>> candidatesByNominal =
                new Dictionary<double, List<PipeDimensions>>();

            HashSet<double> requiredNominalKeys =
                BuildRequiredNominalDimensionKeys(
                    sourceElements);

            // Selected pipes are always included. They are the authoritative
            // dimension source for the current assembly/export.
            foreach (PipeDimensions dimensions in
                     selectedDimensions.Values)
            {
                AddNominalDimensionCandidate(
                    candidatesByNominal,
                    dimensions);
            }

            // The previous implementation resolved every pipe in the Revit
            // document. On large projects that dominated fabrication time even
            // though only one assembly was being exported. Limit the fallback
            // search to pipes whose bounding boxes intersect a padded box
            // around the selected source elements.
            Outline searchOutline =
                BuildFabricationDimensionSearchOutline(
                    sourceElements);

            if (searchOutline != null)
            {
                FilteredElementCollector collector =
                    new FilteredElementCollector(doc)
                        .OfCategory(BuiltInCategory.OST_PipeCurves)
                        .WhereElementIsNotElementType()
                        .WherePasses(
                            new BoundingBoxIntersectsFilter(
                                searchOutline));

                foreach (Pipe pipe in collector.Cast<Pipe>())
                {
                    if (selectedDimensions.ContainsKey(pipe.Id))
                        continue;

                    // The bounded collector can still contain thousands of
                    // nearby pipes in a large package. The document-level map
                    // is consulted only by fitting connectors, so do not run
                    // full ID/OD/geometry resolution for nominal sizes that no
                    // selected fitting requests.
                    double rawNominal =
                        GetPositiveDouble(
                            pipe.get_Parameter(
                                BuiltInParameter
                                    .RBS_PIPE_DIAMETER_PARAM));

                    if (requiredNominalKeys.Count == 0 ||
                        (rawNominal > GeometryTolerance &&
                         !requiredNominalKeys.Any(x =>
                             NominalDiametersMatch(
                                 rawNominal,
                                 x))))
                    {
                        continue;
                    }

                    PipeDimensions dimensions;
                    string ignoredError;

                    if (!TryResolvePipeDimensions(
                            doc,
                            pipe,
                            out dimensions,
                            out ignoredError))
                    {
                        continue;
                    }

                    AddNominalDimensionCandidate(
                        candidatesByNominal,
                        dimensions);
                }
            }

            Dictionary<double, PipeDimensions> result =
                new Dictionary<double, PipeDimensions>();

            foreach (KeyValuePair<double, List<PipeDimensions>> pair in
                     candidatesByNominal)
            {
                PipeDimensions first = pair.Value.FirstOrDefault();
                if (first == null)
                    continue;

                bool unambiguous = pair.Value.All(x =>
                    ArePipeDimensionsEquivalent(first, x));

                if (unambiguous)
                    result[pair.Key] = first;
            }

            return result;
        }

        private static HashSet<double>
            BuildRequiredNominalDimensionKeys(
                IEnumerable<Element> sourceElements)
        {
            HashSet<double> result =
                new HashSet<double>();

            foreach (Element element in
                     sourceElements ?? Enumerable.Empty<Element>())
            {
                if (element == null || element is Pipe)
                    continue;

                ConnectorManager manager =
                    GetConnectorManager(element);

                if (manager == null)
                    continue;

                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null ||
                        connector.Domain != Domain.DomainPiping ||
                        connector.ConnectorType != ConnectorType.End ||
                        connector.Shape != ConnectorProfileType.Round)
                    {
                        continue;
                    }

                    double nominal =
                        connector.Radius * 2.0;

                    if (nominal > GeometryTolerance)
                    {
                        result.Add(
                            RoundDiameterKey(nominal));
                    }
                }
            }

            return result;
        }

        private static void AddNominalDimensionCandidate(
            IDictionary<double, List<PipeDimensions>> candidatesByNominal,
            PipeDimensions dimensions)
        {
            if (dimensions == null ||
                dimensions.NominalDiameter <= GeometryTolerance)
            {
                return;
            }

            double key =
                RoundDiameterKey(dimensions.NominalDiameter);

            List<PipeDimensions> candidates;

            if (!candidatesByNominal.TryGetValue(
                    key,
                    out candidates))
            {
                candidates = new List<PipeDimensions>();
                candidatesByNominal[key] = candidates;
            }

            candidates.Add(dimensions);
        }

        private static Outline BuildFabricationDimensionSearchOutline(
            IEnumerable<Element> sourceElements)
        {
            XYZ minimum = null;
            XYZ maximum = null;

            foreach (Element element in
                     sourceElements ?? Enumerable.Empty<Element>())
            {
                BoundingBoxXYZ box = null;

                try
                {
                    box = GetElementBoundingBoxCached(element);
                }
                catch
                {
                    // Skip malformed family bounds. Selected pipe dimensions
                    // remain available even when a fallback search box cannot
                    // include one source fitting.
                }

                if (box == null)
                    continue;

                foreach (XYZ corner in
                         GetBoundingBoxWorldCorners(box))
                {
                    minimum = minimum == null
                        ? corner
                        : new XYZ(
                            Math.Min(minimum.X, corner.X),
                            Math.Min(minimum.Y, corner.Y),
                            Math.Min(minimum.Z, corner.Z));

                    maximum = maximum == null
                        ? corner
                        : new XYZ(
                            Math.Max(maximum.X, corner.X),
                            Math.Max(maximum.Y, corner.Y),
                            Math.Max(maximum.Z, corner.Z));
                }
            }

            if (minimum == null || maximum == null)
                return null;

            // Connected-network traversal remains the primary resolver.
            // This 1 m envelope is only the final unambiguous-size fallback.
            double padding = 1000.0 / FeetToMillimetres;
            XYZ offset = new XYZ(padding, padding, padding);

            return new Outline(
                minimum - offset,
                maximum + offset);
        }

        private static bool ArePipeDimensionsEquivalent(
            PipeDimensions first,
            PipeDimensions second)
        {
            if (first == null || second == null)
                return false;

            double insideTolerance = Math.Max(
                DiameterTolerance,
                Math.Max(
                    first.InsideDiameter,
                    second.InsideDiameter) * 0.001);

            double outsideTolerance = Math.Max(
                DiameterTolerance,
                Math.Max(
                    first.OutsideDiameter,
                    second.OutsideDiameter) * 0.001);

            return
                Math.Abs(
                    first.InsideDiameter -
                    second.InsideDiameter) <= insideTolerance &&
                Math.Abs(
                    first.OutsideDiameter -
                    second.OutsideDiameter) <= outsideTolerance;
        }

        private static bool TryResolvePipeDimensions(
            Document doc,
            Pipe pipe,
            out PipeDimensions dimensions,
            out string error)
        {
            dimensions = null;
            error = null;

            bool cachedSucceeded;

            if (TryGetCachedPipeDimensions(
                    doc,
                    pipe,
                    out cachedSucceeded,
                    out dimensions,
                    out error))
            {
                return cachedSucceeded;
            }

            double nominal = GetDoubleParameter(
                doc,
                pipe,
                BuiltInParameter.RBS_PIPE_DIAMETER_PARAM,
                "Diameter",
                "Nominal Diameter");

            double outside = GetDoubleParameter(
                doc,
                pipe,
                BuiltInParameter.RBS_PIPE_OUTER_DIAMETER,
                "Outside Diameter",
                "Outer Diameter",
                "OD");

            double inside = GetDoubleParameter(
                doc,
                pipe,
                BuiltInParameter.RBS_PIPE_INNER_DIAM_PARAM,
                "Inside Diameter",
                "Inner Diameter",
                "ID");

            double wall = GetNamedDoubleParameter(
                doc,
                pipe,
                "Wall Thickness",
                "Pipe Wall Thickness",
                "Thickness");

            List<string> sources = new List<string>();

            if (outside > 0)
                sources.Add("OD from Revit pipe data");

            if (inside > 0)
                sources.Add("ID from Revit pipe data");

            if (outside <= 0)
            {
                outside = InferOutsideDiameterFromGeometry(pipe);
                if (outside > 0)
                    sources.Add("OD inferred from pipe solid geometry");
            }

            if (inside <= 0 && outside > 0 && wall > 0)
            {
                inside = outside - (2.0 * wall);
                sources.Add("ID calculated from OD and wall thickness");
            }

            if (wall <= 0 && outside > 0 && inside > 0)
                wall = (outside - inside) / 2.0;

            if (nominal <= 0)
                nominal = outside;

            if (outside <= 0)
            {
                error =
                    "Outside Diameter could not be resolved. " +
                    "Provide the built-in Outside Diameter value or a parameter named Outside Diameter/OD.";
                CachePipeDimensions(
                    doc,
                    pipe,
                    false,
                    null,
                    error);

                return false;
            }

            if (inside <= 0)
            {
                error =
                    "Inside Diameter could not be resolved. " +
                    "Provide the built-in Inside Diameter value, or provide Wall Thickness so the plugin can calculate ID = OD - 2 x thickness.";
                CachePipeDimensions(
                    doc,
                    pipe,
                    false,
                    null,
                    error);

                return false;
            }

            if (inside >= outside)
            {
                error =
                    "Inside Diameter must be smaller than Outside Diameter. " +
                    "Resolved values were ID " + FormatMillimetres(inside) +
                    " and OD " + FormatMillimetres(outside) + ".";
                CachePipeDimensions(
                    doc,
                    pipe,
                    false,
                    null,
                    error);

                return false;
            }

            if (wall <= 0)
            {
                error =
                    "Wall thickness is zero or invalid after resolving ID and OD.";
                CachePipeDimensions(
                    doc,
                    pipe,
                    false,
                    null,
                    error);

                return false;
            }

            dimensions = new PipeDimensions
            {
                NominalDiameter = nominal,
                OutsideDiameter = outside,
                InsideDiameter = inside,
                WallThickness = wall,
                SourceDescription = string.Join("; ", sources.Distinct())
            };

            CachePipeDimensions(
                doc,
                pipe,
                true,
                dimensions,
                null);

            return true;
        }

        private static void CompleteFlangeEndDimensions(
            Document doc,
            Element fitting,
            IList<ConnectorBore> bores,
            IList<FabricationIssue> issues)
        {
            if (bores == null ||
                bores.Count == 0)
            {
                return;
            }

            List<ConnectorBore> resolved = bores
                .Where(x =>
                    x.InsideDiameter >
                    GeometryTolerance)
                .ToList();

            foreach (ConnectorBore unresolved in bores.Where(x =>
                         x.InsideDiameter <=
                         GeometryTolerance))
            {
                ConnectorBore matching = resolved
                    .Where(x =>
                        NominalDiametersMatch(
                            unresolved.NominalDiameter,
                            x.NominalDiameter))
                    .OrderBy(x =>
                        Math.Abs(
                            unresolved.NominalDiameter -
                            x.NominalDiameter))
                    .FirstOrDefault();

                // Most non-blind flanges use one continuous bore. When only
                // one end was connected to a dimensioned component, propagate
                // that verified bore to the opposite connector instead of
                // reporting the same flange twice.
                if (matching == null &&
                    resolved.Count == 1 &&
                    bores.Count == 2)
                {
                    matching = resolved[0];
                }

                if (matching == null)
                    continue;

                unresolved.InsideDiameter =
                    matching.InsideDiameter;

                if (unresolved.OutsideDiameter <=
                    GeometryTolerance)
                {
                    unresolved.OutsideDiameter =
                        matching.OutsideDiameter;
                }

                if (unresolved.WallThickness <=
                    GeometryTolerance)
                {
                    unresolved.WallThickness =
                        matching.WallThickness;
                }

                unresolved.SourceDescription =
                    AppendSourceDescription(
                        unresolved.SourceDescription,
                        "Flange bore propagated from the opposite " +
                        "connector after one side was resolved from " +
                        "a connected pipe or special fabrication component");

                resolved.Add(unresolved);
            }

            List<ConnectorBore> stillUnresolved = bores
                .Where(x =>
                    x.InsideDiameter <=
                    GeometryTolerance)
                .ToList();

            foreach (IGrouping<double, ConnectorBore> group in
                     stillUnresolved.GroupBy(x =>
                         RoundDiameterKey(
                             x.NominalDiameter)))
            {
                issues.Add(new FabricationIssue
                {
                    Severity =
                        FabricationIssueSeverity.Blocking,
                    ElementId = fitting.Id,
                    ElementName =
                        GetElementDisplayName(fitting),
                    Message =
                        "The inside diameter for flange connector size " +
                        FormatMillimetres(
                            group.First().NominalDiameter) +
                        " could not be resolved. The plugin checked " +
                        "connected pipes, special shaped-branch/coupling " +
                        "dimensions, the connected fitting network, " +
                        "selected pipe sizes, fitting parameters, and " +
                        "physical flange geometry. Connect at least one " +
                        "flange side to a dimensioned pipe/component or " +
                        "provide an Inside Diameter parameter."
                });
            }
        }

        private static void CompleteConcentricReducerEndDimensions(
            Document doc,
            Element fitting,
            IList<ConnectorBore> bores,
            IList<FabricationIssue> issues)
        {
            if (bores == null || bores.Count == 0)
                return;

            bool isCopperFamily =
                IsCopperTubeFamilyLike(doc, fitting);

            foreach (ConnectorBore bore in bores)
            {
                if (bore.OutsideDiameter <= 0 &&
                    bore.NominalDiameter > GeometryTolerance &&
                    isCopperFamily)
                {
                    bore.OutsideDiameter =
                        bore.NominalDiameter;

                    bore.SourceDescription =
                        AppendSourceDescription(
                            bore.SourceDescription,
                            "OD taken from metric copper tube connector size");
                }

                if (bore.WallThickness <= 0 &&
                    bore.OutsideDiameter >
                        bore.InsideDiameter + GeometryTolerance &&
                    bore.InsideDiameter > GeometryTolerance)
                {
                    bore.WallThickness =
                        (bore.OutsideDiameter -
                         bore.InsideDiameter) / 2.0;
                }
            }

            List<ConnectorBore> resolvedEnds = bores
                .Where(x =>
                    x.InsideDiameter > GeometryTolerance &&
                    x.OutsideDiameter >
                        x.InsideDiameter + GeometryTolerance &&
                    x.WallThickness > GeometryTolerance)
                .ToList();

            foreach (ConnectorBore unresolved in bores.Where(x =>
                         x.InsideDiameter <= GeometryTolerance))
            {
                ConnectorBore referenceEnd = resolvedEnds
                    .OrderBy(x =>
                        Math.Abs(
                            x.NominalDiameter -
                            unresolved.NominalDiameter))
                    .FirstOrDefault();

                if (isCopperFamily &&
                    referenceEnd != null &&
                    unresolved.OutsideDiameter >
                        (2.0 * referenceEnd.WallThickness) +
                        GeometryTolerance)
                {
                    // This fallback is intentionally limited to a two-ended
                    // concentric reducer. When one end is grounded by an
                    // actual pipe/opening and the other copper end exposes
                    // only its tube OD, preserve the resolved fitting wall
                    // thickness across the missing end instead of guessing a
                    // schedule from nominal size alone.
                    unresolved.WallThickness =
                        referenceEnd.WallThickness;

                    unresolved.InsideDiameter =
                        unresolved.OutsideDiameter -
                        (2.0 * unresolved.WallThickness);

                    unresolved.SourceDescription =
                        AppendSourceDescription(
                            unresolved.SourceDescription,
                            "ID derived from connector OD using the " +
                            "resolved wall thickness of the opposite " +
                            "concentric-reducer end");
                }
            }

            foreach (ConnectorBore unresolved in bores.Where(x =>
                         x.InsideDiameter <= GeometryTolerance ||
                         x.OutsideDiameter <=
                             x.InsideDiameter + GeometryTolerance ||
                         x.WallThickness <= GeometryTolerance))
            {
                issues.Add(new FabricationIssue
                {
                    Severity =
                        FabricationIssueSeverity.Blocking,
                    ElementId = fitting.Id,
                    ElementName =
                        GetElementDisplayName(fitting),
                    Message =
                        "The concentric reducer end dimensions could not " +
                        "be resolved for connector size " +
                        FormatMillimetres(
                            unresolved.NominalDiameter) +
                        ". The plugin checked directly connected pipes, " +
                        "the same connector branch, tube OD, fitting " +
                        "geometry, and the validated wall thickness from " +
                        "the opposite reducer end. Add explicit end ID/OD " +
                        "parameters if this family uses different wall " +
                        "thicknesses at each end."
                });
            }
        }

        private static string AppendSourceDescription(
            string current,
            string addition)
        {
            if (string.IsNullOrWhiteSpace(current))
                return addition ?? string.Empty;

            if (string.IsNullOrWhiteSpace(addition))
                return current;

            return current + "; " + addition;
        }

        private static bool TryInferInsideDiameterFromConnectedFitting(
            Document doc,
            Element sourceFitting,
            Connector sourceConnector,
            Element connectedFitting,
            double targetNominalDiameter,
            out double insideDiameter,
            out string sourceDescription)
        {
            insideDiameter = 0.0;
            sourceDescription = null;

            if (sourceConnector == null ||
                connectedFitting == null ||
                connectedFitting is Pipe ||
                IsIgnoredConnectionElement(
                    doc,
                    connectedFitting))
            {
                return false;
            }

            List<Solid> connectedSolids =
                GetElementSolids(connectedFitting);

            if (connectedSolids.Count == 0)
                return false;

            ConnectorManager connectedManager =
                GetConnectorManager(connectedFitting);

            if (connectedManager == null)
                return false;

            Connector bestConnector = null;
            double bestScore = double.MaxValue;

            foreach (Connector candidate in
                     connectedManager.Connectors)
            {
                if (candidate == null ||
                    candidate.Domain != Domain.DomainPiping ||
                    candidate.ConnectorType != ConnectorType.End ||
                    candidate.Shape != ConnectorProfileType.Round)
                {
                    continue;
                }

                double distance =
                    candidate.Origin.DistanceTo(
                        sourceConnector.Origin);

                double candidateNominal =
                    candidate.Radius * 2.0;

                double nominalPenalty =
                    targetNominalDiameter > GeometryTolerance
                        ? Math.Abs(
                            candidateNominal -
                            targetNominalDiameter) * 5.0
                        : 0.0;

                double score =
                    distance + nominalPenalty;

                if (score >= bestScore)
                    continue;

                bestScore = score;
                bestConnector = candidate;
            }

            if (bestConnector == null)
                return false;

            XYZ direction =
                GetConnectorOutwardDirection(
                    connectedFitting,
                    bestConnector,
                    sourceFitting);

            double inferredInside;
            double ignoredOutside;
            double inferredOffset;

            if (!TryInferConnectorDiametersFromGeometry(
                    connectedSolids,
                    bestConnector.Origin,
                    direction,
                    bestConnector.Radius * 2.0,
                    GetElementExtent(connectedFitting),
                    out inferredInside,
                    out ignoredOutside,
                    out inferredOffset))
            {
                return false;
            }

            if (inferredInside <= GeometryTolerance)
                return false;

            insideDiameter = inferredInside;

            sourceDescription =
                "Bore inherited from physical opening of connected fitting " +
                GetElementDisplayName(connectedFitting) +
                " at " +
                FormatMillimetres(
                    Math.Abs(inferredOffset)) +
                (inferredOffset < 0
                    ? " inward from its connector"
                    : " outward from its connector");

            return true;
        }

        private static bool
            TryInferConnectorOutsideDiameterFromGeometry(
                IList<Solid> sourceSolids,
                XYZ connectorOrigin,
                XYZ outwardDirection,
                double nominalDiameter,
                double elementExtent,
                out double outsideDiameter,
                out double signedOffset)
        {
            outsideDiameter = 0.0;
            signedOffset = 0.0;

            if (sourceSolids == null ||
                sourceSolids.Count == 0 ||
                connectorOrigin == null ||
                outwardDirection == null ||
                outwardDirection.GetLength() <= GeometryTolerance)
            {
                return false;
            }

            XYZ outward =
                outwardDirection.Normalize();

            double nominalReference =
                nominalDiameter > GeometryTolerance
                    ? nominalDiameter
                    : Math.Max(
                        50.0 / FeetToMillimetres,
                        elementExtent * 0.25);

            double maximumSearchDistance = Math.Min(
                Math.Max(
                    35.0 / FeetToMillimetres,
                    nominalReference * 0.35),
                Math.Max(
                    35.0 / FeetToMillimetres,
                    elementExtent * 0.50));

            double step =
                0.5 / FeetToMillimetres;

            for (double distance = step;
                 distance <=
                 maximumSearchDistance + GeometryTolerance;
                 distance += step)
            {
                double[] offsets =
                {
                    -distance,
                    distance
                };

                foreach (double offset in offsets)
                {
                    XYZ sectionCenter =
                        connectorOrigin +
                        (outward * offset);

                    double radius;

                    if (!TryMeasureCircularOuterRadiusFromGeometry(
                            sourceSolids,
                            sectionCenter,
                            outward,
                            nominalReference,
                            elementExtent,
                            out radius))
                    {
                        continue;
                    }

                    double candidateOutside =
                        radius * 2.0;

                    if (nominalDiameter > GeometryTolerance)
                    {
                        double minimum =
                            nominalDiameter * 0.70;

                        double maximum =
                            nominalDiameter * 1.50;

                        if (candidateOutside <
                                minimum -
                                DiameterTolerance ||
                            candidateOutside >
                                maximum +
                                DiameterTolerance)
                        {
                            continue;
                        }
                    }

                    outsideDiameter =
                        candidateOutside;

                    signedOffset = offset;
                    return true;
                }
            }

            return false;
        }

        private static bool
            TryMeasureCircularOuterRadiusFromGeometry(
                IList<Solid> sourceSolids,
                XYZ sectionCenter,
                XYZ sectionNormal,
                double nominalDiameter,
                double elementExtent,
                out double outerRadius)
        {
            outerRadius = 0.0;

            XYZ normal =
                sectionNormal.Normalize();

            XYZ helper =
                Math.Abs(normal.Z) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;

            XYZ radialX =
                normal.CrossProduct(helper).Normalize();

            XYZ radialY =
                normal.CrossProduct(radialX).Normalize();

            double rayLength = Math.Max(
                Math.Max(
                    nominalDiameter * 1.75,
                    100.0 / FeetToMillimetres),
                elementExtent);

            const int probeCount = 16;
            List<double> radii =
                new List<double>();

            for (int probeIndex = 0;
                 probeIndex < probeCount;
                 probeIndex++)
            {
                double angle =
                    (2.0 * Math.PI * probeIndex) /
                    probeCount;

                XYZ radialDirection =
                    (radialX * Math.Cos(angle)) +
                    (radialY * Math.Sin(angle));

                Line ray;

                try
                {
                    ray = Line.CreateBound(
                        sectionCenter,
                        sectionCenter +
                        (radialDirection * rayLength));
                }
                catch
                {
                    continue;
                }

                double farthestMaterialDistance = 0.0;

                foreach (Solid solid in sourceSolids)
                {
                    if (solid == null ||
                        solid.Volume <= GeometryTolerance)
                    {
                        continue;
                    }

                    try
                    {
                        using (SolidCurveIntersectionOptions options =
                               new SolidCurveIntersectionOptions())
                        {
                            options.ResultType =
                                SolidCurveIntersectionMode
                                    .CurveSegmentsInside;

                            using (SolidCurveIntersection intersection =
                                   solid.IntersectWithCurve(
                                       ray,
                                       options))
                            {
                                for (int segmentIndex = 0;
                                     segmentIndex <
                                     intersection.SegmentCount;
                                     segmentIndex++)
                                {
                                    Curve segment =
                                        intersection.GetCurveSegment(
                                            segmentIndex);

                                    double first =
                                        (segment.GetEndPoint(0) -
                                         sectionCenter)
                                        .DotProduct(radialDirection);

                                    double second =
                                        (segment.GetEndPoint(1) -
                                         sectionCenter)
                                        .DotProduct(radialDirection);

                                    farthestMaterialDistance =
                                        Math.Max(
                                            farthestMaterialDistance,
                                            Math.Max(first, second));
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Probe the remaining valid solids/directions.
                    }
                }

                if (farthestMaterialDistance >
                    GeometryTolerance)
                {
                    radii.Add(
                        farthestMaterialDistance);
                }
            }

            if (radii.Count <
                Math.Max(10, probeCount * 3 / 4))
            {
                return false;
            }

            radii.Sort();

            double median =
                radii[radii.Count / 2];

            double spread =
                radii.Last() -
                radii.First();

            double allowedSpread =
                Math.Max(
                    2.0 / FeetToMillimetres,
                    median * 0.05);

            if (median <= GeometryTolerance ||
                spread > allowedSpread)
            {
                return false;
            }

            outerRadius = median;
            return true;
        }

        private static bool TryInferConnectorDiametersFromGeometry(
            IList<Solid> sourceSolids,
            XYZ connectorOrigin,
            XYZ outwardDirection,
            double nominalDiameter,
            double elementExtent,
            out double insideDiameter,
            out double outsideDiameter,
            out double signedOffset)
        {
            insideDiameter = 0.0;
            outsideDiameter = 0.0;
            signedOffset = 0.0;

            if (sourceSolids == null ||
                sourceSolids.Count == 0 ||
                connectorOrigin == null ||
                outwardDirection == null ||
                outwardDirection.GetLength() <= GeometryTolerance)
            {
                return false;
            }

            XYZ outward = outwardDirection.Normalize();

            double nominalReference =
                nominalDiameter > GeometryTolerance
                    ? nominalDiameter
                    : Math.Max(
                        50.0 / FeetToMillimetres,
                        elementExtent * 0.25);

            double maximumSearchDistance = Math.Min(
                Math.Max(
                    50.0 / FeetToMillimetres,
                    nominalReference * 0.60),
                Math.Max(
                    50.0 / FeetToMillimetres,
                    elementExtent));

            List<double> searchDistances =
                new List<double>();

            // Search nearest to the connector first. Negative offsets are
            // tested first because GetConnectorOutwardDirection points toward
            // the connected element; the fitting body is normally inward.
            double halfMillimetre =
                0.5 / FeetToMillimetres;

            for (double distance = halfMillimetre;
                 distance <=
                 maximumSearchDistance + GeometryTolerance;)
            {
                searchDistances.Add(-distance);
                searchDistances.Add(distance);

                double distanceMillimetres =
                    distance * FeetToMillimetres;

                double incrementMillimetres =
                    distanceMillimetres < 10.0
                        ? 0.5
                        : distanceMillimetres < 30.0
                            ? 1.0
                            : 2.5;

                distance +=
                    incrementMillimetres /
                    FeetToMillimetres;
            }

            foreach (double offset in searchDistances)
            {
                XYZ sectionCenter =
                    connectorOrigin +
                    (outward * offset);

                double innerRadius;
                double outerRadius;

                if (!TryMeasureAnnularSectionFromGeometry(
                        sourceSolids,
                        sectionCenter,
                        outward,
                        nominalReference,
                        elementExtent,
                        out innerRadius,
                        out outerRadius))
                {
                    continue;
                }

                double candidateInside =
                    innerRadius * 2.0;

                // Connector diameter is normally the nominal/bore reference.
                // Reject bolt-circle, flange-ring, and unrelated outer-body
                // openings that are much smaller or larger than that size.
                if (nominalDiameter > GeometryTolerance)
                {
                    double minimumInside =
                        nominalDiameter * 0.35;

                    double maximumInside =
                        nominalDiameter * 1.05;

                    if (candidateInside <
                            minimumInside -
                            DiameterTolerance ||
                        candidateInside >
                            maximumInside +
                            DiameterTolerance)
                    {
                        continue;
                    }
                }

                insideDiameter = candidateInside;

                if (outerRadius >
                    innerRadius + GeometryTolerance)
                {
                    outsideDiameter =
                        outerRadius * 2.0;
                }

                signedOffset = offset;
                return true;
            }

            return false;
        }

        private static bool TryMeasureAnnularSectionFromGeometry(
            IList<Solid> sourceSolids,
            XYZ sectionCenter,
            XYZ sectionNormal,
            double nominalDiameter,
            double elementExtent,
            out double innerRadius,
            out double outerRadius)
        {
            innerRadius = 0.0;
            outerRadius = 0.0;

            XYZ normal = sectionNormal.Normalize();

            XYZ helper =
                Math.Abs(normal.Z) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;

            XYZ radialX =
                normal.CrossProduct(helper).Normalize();

            XYZ radialY =
                normal.CrossProduct(radialX).Normalize();

            double rayLength = Math.Max(
                Math.Max(
                    nominalDiameter * 1.50,
                    100.0 / FeetToMillimetres),
                elementExtent * 0.75);

            const int probeCount = 12;
            List<double> innerRadii =
                new List<double>();

            List<double> outerRadii =
                new List<double>();

            double centerTolerance =
                0.10 / FeetToMillimetres;

            for (int probeIndex = 0;
                 probeIndex < probeCount;
                 probeIndex++)
            {
                double angle =
                    (2.0 * Math.PI * probeIndex) /
                    probeCount;

                XYZ radialDirection =
                    (radialX * Math.Cos(angle)) +
                    (radialY * Math.Sin(angle));

                XYZ rayEnd =
                    sectionCenter +
                    (radialDirection * rayLength);

                Line ray;

                try
                {
                    ray = Line.CreateBound(
                        sectionCenter,
                        rayEnd);
                }
                catch
                {
                    continue;
                }

                double nearestStart =
                    double.MaxValue;

                double nearestEnd = 0.0;
                bool centerIsMaterial = false;

                foreach (Solid solid in sourceSolids)
                {
                    if (solid == null ||
                        solid.Volume <= GeometryTolerance)
                    {
                        continue;
                    }

                    try
                    {
                        using (SolidCurveIntersectionOptions options =
                               new SolidCurveIntersectionOptions())
                        {
                            options.ResultType =
                                SolidCurveIntersectionMode
                                    .CurveSegmentsInside;

                            using (SolidCurveIntersection intersection =
                                   solid.IntersectWithCurve(
                                       ray,
                                       options))
                            {
                                for (int segmentIndex = 0;
                                     segmentIndex <
                                     intersection.SegmentCount;
                                     segmentIndex++)
                                {
                                    Curve segment =
                                        intersection.GetCurveSegment(
                                            segmentIndex);

                                    double firstDistance =
                                        (segment.GetEndPoint(0) -
                                         sectionCenter)
                                        .DotProduct(radialDirection);

                                    double secondDistance =
                                        (segment.GetEndPoint(1) -
                                         sectionCenter)
                                        .DotProduct(radialDirection);

                                    double segmentStart =
                                        Math.Min(
                                            firstDistance,
                                            secondDistance);

                                    double segmentEnd =
                                        Math.Max(
                                            firstDistance,
                                            secondDistance);

                                    if (segmentEnd <=
                                        GeometryTolerance)
                                    {
                                        continue;
                                    }

                                    segmentStart =
                                        Math.Max(
                                            0.0,
                                            segmentStart);

                                    if (segmentStart <=
                                        centerTolerance)
                                    {
                                        centerIsMaterial = true;
                                        break;
                                    }

                                    if (segmentStart <
                                        nearestStart)
                                    {
                                        nearestStart =
                                            segmentStart;

                                        nearestEnd =
                                            segmentEnd;
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Continue probing the remaining valid family solids.
                    }

                    if (centerIsMaterial)
                        break;
                }

                if (centerIsMaterial ||
                    nearestStart == double.MaxValue ||
                    nearestEnd <=
                    nearestStart + GeometryTolerance)
                {
                    continue;
                }

                innerRadii.Add(nearestStart);
                outerRadii.Add(nearestEnd);
            }

            if (innerRadii.Count <
                Math.Max(6, probeCount / 2))
            {
                return false;
            }

            innerRadii.Sort();
            outerRadii.Sort();

            double medianInner =
                innerRadii[innerRadii.Count / 2];

            double medianOuter =
                outerRadii[outerRadii.Count / 2];

            double innerSpread =
                innerRadii.Last() -
                innerRadii.First();

            double permittedInnerSpread =
                Math.Max(
                    1.5 / FeetToMillimetres,
                    medianInner * 0.04);

            if (medianInner <= GeometryTolerance ||
                innerSpread >
                permittedInnerSpread)
            {
                return false;
            }

            innerRadius = medianInner;

            double outerSpread =
                outerRadii.Last() -
                outerRadii.First();

            double permittedOuterSpread =
                Math.Max(
                    5.0 / FeetToMillimetres,
                    medianOuter * 0.12);

            if (medianOuter >
                    medianInner + GeometryTolerance &&
                outerSpread <= permittedOuterSpread)
            {
                outerRadius = medianOuter;
            }

            return true;
        }

        private static double InferOutsideDiameterFromGeometry(
            Element pipe)
        {
            double maximumRadius = 0;

            foreach (Solid solid in GetElementSolids(pipe))
            {
                foreach (Face face in solid.Faces)
                {
                    CylindricalFace cylindrical =
                        face as CylindricalFace;

                    if (cylindrical != null)
                    {
                        XYZ radiusVector = cylindrical.get_Radius(0);
                        if (radiusVector != null)
                        {
                            maximumRadius = Math.Max(
                                maximumRadius,
                                radiusVector.GetLength());
                        }
                    }
                }
            }

            return maximumRadius > 0
                ? maximumRadius * 2.0
                : 0;
        }

        private static double GetDoubleParameter(
            Document doc,
            Element element,
            BuiltInParameter builtInParameter,
            params string[] fallbackNames)
        {
            Parameter parameter = element.get_Parameter(
                builtInParameter);

            double value = GetPositiveDouble(parameter);
            if (value > 0)
                return value;

            Element type = GetElementTypeCached(doc, element);
            parameter = type?.get_Parameter(builtInParameter);
            value = GetPositiveDouble(parameter);

            if (value > 0)
                return value;

            return GetNamedDoubleParameter(
                doc,
                element,
                fallbackNames);
        }

        private static double GetNamedDoubleParameter(
            Document doc,
            Element element,
            params string[] names)
        {
            if (element == null || names == null)
                return 0;

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                double value = GetPositiveDouble(
                    element.LookupParameter(name));

                if (value > 0)
                    return value;
            }

            Element type = GetElementTypeCached(doc, element);
            if (type == null)
                return 0;

            foreach (string name in names)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                double value = GetPositiveDouble(
                    type.LookupParameter(name));

                if (value > 0)
                    return value;
            }

            return 0;
        }

        private static double GetPositiveDouble(Parameter parameter)
        {
            if (parameter == null ||
                parameter.StorageType != StorageType.Double)
            {
                return 0;
            }

            double value = parameter.AsDouble();
            return value > GeometryTolerance ? value : 0;
        }

        private static double RoundDiameterKey(double value)
        {
            return Math.Round(value, 6);
        }

        private static string FormatMillimetres(double feet)
        {
            return (feet * FeetToMillimetres).ToString(
                       "0.###",
                       CultureInfo.InvariantCulture) +
                   " mm";
        }
    }
}
