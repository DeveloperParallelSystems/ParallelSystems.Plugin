using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        private static bool TryCreateFlangeBoltHoleCutters(
            Document doc,
            Element flange,
            IList<ConnectorBore> bores,
            out List<Solid> cutters,
            out string description,
            out string error)
        {
            cutters = new List<Solid>();
            description = null;
            error = null;

            AtlasFlangeReferenceRow configuration;

            if (!TryResolveFlangeConfiguration(
                    doc,
                    flange,
                    bores,
                    out configuration,
                    out error))
            {
                return false;
            }

            return TryCreateFlangeBoltHoleCutters(
                flange,
                bores,
                configuration,
                out cutters,
                out description,
                out error);
        }

        private static bool TryCreateFlangeBoltHoleCutters(
            Element flange,
            IList<ConnectorBore> bores,
            AtlasFlangeReferenceRow configuration,
            out List<Solid> cutters,
            out string description,
            out string error)
        {
            cutters = new List<Solid>();
            description = null;
            error = null;

            int holeCount;
            double pitchCircleDiameterMillimetres;
            double holeDiameterMillimetres;

            if (!int.TryParse(
                    configuration.Bolts,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out holeCount) ||
                holeCount <= 0)
            {
                error =
                    "The flange catalog row for " +
                    BuildFlangeConfigurationLabel(configuration) +
                    " does not define a usable bolt-hole count.";
                return false;
            }

            if (!TryParseCatalogMillimetres(
                    configuration.K,
                    out pitchCircleDiameterMillimetres) ||
                pitchCircleDiameterMillimetres <= 0)
            {
                error =
                    "The flange catalog row for " +
                    BuildFlangeConfigurationLabel(configuration) +
                    " does not define a usable pitch-circle diameter.";
                return false;
            }

            if (!TryParseCatalogMillimetres(
                    configuration.H,
                    out holeDiameterMillimetres) ||
                holeDiameterMillimetres <= 0)
            {
                error =
                    "The flange catalog row for " +
                    BuildFlangeConfigurationLabel(configuration) +
                    " does not define a usable bolt-hole diameter.";
                return false;
            }

            double outsideDiameterMillimetres;

            if (TryParseCatalogMillimetres(
                    string.Equals(
                        configuration.Kind,
                        "ASME",
                        StringComparison.Ordinal)
                        ? configuration.O
                        : configuration.A,
                    out outsideDiameterMillimetres) &&
                outsideDiameterMillimetres > 0 &&
                pitchCircleDiameterMillimetres +
                    holeDiameterMillimetres >=
                outsideDiameterMillimetres)
            {
                error =
                    "The flange catalog row for " +
                    BuildFlangeConfigurationLabel(configuration) +
                    " places the bolt holes outside the configured flange " +
                    "diameter.";
                return false;
            }

            List<ConnectorBore> physicalBores = bores
                .Where(x =>
                    x != null &&
                    !x.IsSynthetic &&
                    x.OriginalConnectorOrigin != null)
                .ToList();

            if (physicalBores.Count == 0)
            {
                error =
                    "The flange bolt-circle centre and axis could not be " +
                    "resolved because the flange has no usable physical " +
                    "piping connector.";
                return false;
            }

            XYZ centre = new XYZ(
                physicalBores.Average(x =>
                    x.OriginalConnectorOrigin.X),
                physicalBores.Average(x =>
                    x.OriginalConnectorOrigin.Y),
                physicalBores.Average(x =>
                    x.OriginalConnectorOrigin.Z));

            XYZ axis = null;

            if (physicalBores.Count >= 2)
            {
                XYZ connectorSpan =
                    physicalBores[physicalBores.Count - 1]
                        .OriginalConnectorOrigin -
                    physicalBores[0].OriginalConnectorOrigin;

                if (connectorSpan.GetLength() > GeometryTolerance)
                    axis = connectorSpan.Normalize();
            }

            if (axis == null)
            {
                axis = physicalBores
                    .Select(x => x.OutwardDirection)
                    .FirstOrDefault(x =>
                        x != null &&
                        x.GetLength() > GeometryTolerance);

                if (axis != null)
                    axis = axis.Normalize();
            }

            if (axis == null)
            {
                error =
                    "The flange bolt-hole axis could not be resolved from " +
                    "its piping connectors.";
                return false;
            }

            XYZ radialX = ProjectOntoPerpendicularPlane(
                physicalBores[0].RadialBasisX,
                axis);

            if (radialX == null)
            {
                radialX = ProjectOntoPerpendicularPlane(
                    physicalBores[0].RadialBasisY,
                    axis);
            }

            if (radialX == null)
            {
                XYZ fallback =
                    Math.Abs(axis.DotProduct(XYZ.BasisZ)) < 0.90
                        ? XYZ.BasisZ
                        : XYZ.BasisX;

                radialX = ProjectOntoPerpendicularPlane(
                    fallback,
                    axis);
            }

            if (radialX == null)
            {
                error =
                    "A stable radial orientation could not be established " +
                    "for the flange bolt pattern.";
                return false;
            }

            XYZ radialY = axis.CrossProduct(radialX);

            if (radialY.GetLength() <= GeometryTolerance)
            {
                error =
                    "A stable radial orientation could not be established " +
                    "for the flange bolt pattern.";
                return false;
            }

            radialY = radialY.Normalize();

            double pitchRadius =
                pitchCircleDiameterMillimetres /
                (2.0 * FeetToMillimetres);
            double holeRadius =
                holeDiameterMillimetres /
                (2.0 * FeetToMillimetres);
            // Use twice the larger of the source extent and configured OD.
            // The procedural Atlas body is anchored at its connected-pipe
            // face and can be longer than the original family thickness, so
            // a cutter centred on the original connector span must extend in
            // both axial directions far enough to pass through the new body.
            double cutterLength =
                (2.0 * Math.Max(
                    GetElementExtent(flange),
                    outsideDiameterMillimetres /
                        FeetToMillimetres)) +
                (20.0 / FeetToMillimetres);
            XYZ cutterStart =
                centre - (axis * (cutterLength / 2.0));

            // Half of one pitch places every hole between the connector's
            // radial axes, matching the conventional straddled-centreline
            // orientation requested for the first implementation.
            double startAngle = Math.PI / holeCount;
            double pitchAngle =
                (2.0 * Math.PI) / holeCount;

            try
            {
                for (int index = 0; index < holeCount; index++)
                {
                    double angle =
                        startAngle + (index * pitchAngle);
                    XYZ radialOffset =
                        (radialX * Math.Cos(angle)) +
                        (radialY * Math.Sin(angle));
                    XYZ holeCentre =
                        cutterStart +
                        (radialOffset * pitchRadius);

                    cutters.Add(
                        CreateCylinder(
                            holeCentre,
                            axis,
                            cutterLength,
                            holeRadius));
                }
            }
            catch (Exception ex)
            {
                cutters.Clear();
                error =
                    "The configured flange bolt-hole cutters could not be " +
                    "created: " + ex.Message;
                return false;
            }

            description =
                BuildFlangeConfigurationLabel(configuration) +
                "; bolt pattern " +
                holeCount.ToString(CultureInfo.InvariantCulture) +
                " x " +
                holeDiameterMillimetres.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm on " +
                pitchCircleDiameterMillimetres.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm PCD; holes straddle connector centreline axes";

            return cutters.Count == holeCount;
        }

        private static FabricationElementGeometry
            BuildAtlasConfiguredFlangeGeometry(
                Document doc,
                Element flange,
                IList<ConnectorBore> drillingConnectors,
                IList<FabricationIssue> issues)
        {
            AtlasFlangeReferenceRow configuration;
            string configurationError;

            if (!TryResolveFlangeConfiguration(
                    doc,
                    flange,
                    drillingConnectors,
                    out configuration,
                    out configurationError))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = flange.Id,
                    ElementName = GetElementDisplayName(flange),
                    Message = configurationError
                });

                return null;
            }

            Solid configuredBody;
            string bodyDescription;
            string bodyError;

            if (!TryCreateAtlasFlangeBody(
                    doc,
                    flange,
                    drillingConnectors,
                    configuration,
                    out configuredBody,
                    out bodyDescription,
                    out bodyError))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = flange.Id,
                    ElementName = GetElementDisplayName(flange),
                    Message = bodyError
                });

                return null;
            }

            List<Solid> boltHoleCutters;
            string boltHoleDescription;
            string boltHoleError;

            if (!TryCreateFlangeBoltHoleCutters(
                    flange,
                    drillingConnectors,
                    configuration,
                    out boltHoleCutters,
                    out boltHoleDescription,
                    out boltHoleError))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = flange.Id,
                    ElementName = GetElementDisplayName(flange),
                    Message = boltHoleError
                });

                return null;
            }

            List<Solid> currentSolids = new List<Solid>
            {
                configuredBody
            };
            int holesCut = 0;

            foreach (Solid cutter in boltHoleCutters)
            {
                bool removed;
                currentSolids = SubtractCutterFromSolids(
                    currentSolids,
                    cutter,
                    out removed);

                if (!removed)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = flange.Id,
                        ElementName = GetElementDisplayName(flange),
                        Message =
                            "A configured Atlas flange bolt-hole cutter did " +
                            "not pass through the configured flange body. " +
                            "The STEP export was stopped rather than emitting " +
                            "an incomplete bolt pattern."
                    });

                    return null;
                }

                holesCut++;
            }

            List<GeometryObject> geometry = currentSolids
                .Where(x =>
                    x != null &&
                    x.Volume > GeometryTolerance)
                .Cast<GeometryObject>()
                .ToList();

            if (geometry.Count == 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = flange.Id,
                    ElementName = GetElementDisplayName(flange),
                    Message =
                        "The flange geometry became invalid after applying " +
                        "the configured Atlas bolt drilling."
                });

                return null;
            }

            return new FabricationElementGeometry
            {
                SourceElementId = flange.Id,
                SourceUniqueId = flange.UniqueId,
                SourceName = GetElementDisplayName(flange),
                CategoryName =
                    flange.Category?.Name ?? "Pipe Fitting",
                Geometry = geometry,
                Status =
                    "Atlas-configured flange; verified bolt holes " +
                    holesCut.ToString(CultureInfo.InvariantCulture),
                Notes =
                    bodyDescription + "; " + boltHoleDescription +
                    "; central opening and every bolt hole pass completely " +
                    "through the generated flange body"
            };
        }

        private static bool TryCreateAtlasFlangeBody(
            Document doc,
            Element flange,
            IList<ConnectorBore> bores,
            AtlasFlangeReferenceRow configuration,
            out Solid body,
            out string description,
            out string error)
        {
            body = null;
            description = null;
            error = null;

            XYZ start;
            XYZ axis;

            if (!TryResolveAtlasFlangePlacement(
                    doc,
                    flange,
                    bores,
                    out start,
                    out axis,
                    out error))
            {
                return false;
            }

            string name = NormalizeClassificationText(
                BuildFlangeReferenceNameText(doc, flange));
            bool isBlind =
                name.Contains("BLIND") ||
                name.Contains("BLANK");
            bool isWeldingNeck =
                name.Contains("WELDING NECK") ||
                name.Contains("WELD NECK") ||
                (" " + name + " ").Contains(" WN ");
            bool isBoss =
                (" " + name + " ").Contains(" BOSS ");
            bool isAsme = string.Equals(
                configuration.Kind,
                "ASME",
                StringComparison.Ordinal);

            double outsideDiameterMillimetres;
            double totalLengthMillimetres;
            double boreDiameterMillimetres = 0.0;
            double hubStartDiameterMillimetres = 0.0;
            double hubEndDiameterMillimetres = 0.0;
            double plateThicknessMillimetres = 0.0;
            double raisedFaceDiameterMillimetres = 0.0;
            double raisedFaceHeightMillimetres = 0.0;
            string subtype;

            if (isAsme)
            {
                if (!TryParseRequiredAtlasDimension(
                        configuration,
                        configuration.O,
                        "flange outside diameter O",
                        out outsideDiameterMillimetres,
                        out error) ||
                    !TryParseRequiredAtlasDimension(
                        configuration,
                        configuration.Tf,
                        "minimum flange thickness tf",
                        out plateThicknessMillimetres,
                        out error))
                {
                    return false;
                }

                if (isBlind)
                {
                    subtype = "blind";
                    totalLengthMillimetres =
                        plateThicknessMillimetres;
                }
                else if (isWeldingNeck)
                {
                    subtype = "welding-neck";

                    if (!TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.YWeldingNeck,
                            "welding-neck length through hub Y",
                            out totalLengthMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.BWeldingNeck,
                            "welding-neck bore B",
                            out boreDiameterMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.Ah,
                            "welding-neck hub diameter Ah",
                            out hubStartDiameterMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.X,
                            "hub diameter X",
                            out hubEndDiameterMillimetres,
                            out error))
                    {
                        return false;
                    }
                }
                else
                {
                    if (name.Contains("THREADED") ||
                        name.Contains("SOCKET") ||
                        name.Contains("LAPPED") ||
                        name.Contains("LAP JOINT"))
                    {
                        error =
                            "The Atlas table identifies this ASME subtype, " +
                            "but the manual does not provide every thread, " +
                            "socket, or lap detail required to generate it " +
                            "without guessing. Use Original model geometry " +
                            "for this flange subtype.";
                        return false;
                    }

                    subtype = "slip-on welding";

                    if (!TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.YSlip,
                            "slip-on length through hub Y",
                            out totalLengthMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.BSlip,
                            "slip-on bore B",
                            out boreDiameterMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.X,
                            "hub diameter X",
                            out hubStartDiameterMillimetres,
                            out error))
                    {
                        return false;
                    }

                    hubEndDiameterMillimetres =
                        hubStartDiameterMillimetres;
                }
            }
            else
            {
                if (isWeldingNeck || isBoss)
                {
                    error =
                        "The Atlas table-flange row provides plate drilling " +
                        "dimensions but does not provide the neck/boss height " +
                        "or scheduled bore needed to generate this subtype " +
                        "without guessing. Use Original model geometry for " +
                        "this flange subtype.";
                    return false;
                }

                subtype = isBlind ? "blind" : "plate slip-on welding";

                if (!TryParseRequiredAtlasDimension(
                        configuration,
                        configuration.A,
                        "outside diameter A",
                        out outsideDiameterMillimetres,
                        out error))
                {
                    return false;
                }

                string thicknessValue = configuration.D;

                if (string.Equals(
                        configuration.Kind,
                        "EN1092",
                        StringComparison.Ordinal))
                {
                    thicknessValue = isBlind
                        ? configuration.BlindThickness
                        : configuration.SlipOnWeldingThickness;
                }

                if (!TryParseRequiredAtlasDimension(
                        configuration,
                        thicknessValue,
                        isBlind
                            ? "blind thickness"
                            : "slip-on welding thickness D",
                        out totalLengthMillimetres,
                        out error))
                {
                    return false;
                }

                plateThicknessMillimetres =
                    totalLengthMillimetres;

                if (!isBlind &&
                    !TryResolveTableFlangeBoreDiameter(
                        configuration,
                        bores,
                        outsideDiameterMillimetres,
                        out boreDiameterMillimetres,
                        out error))
                {
                    return false;
                }

                if (!string.Equals(
                        configuration.Kind,
                        "AS2129",
                        StringComparison.Ordinal))
                {
                    if (!TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.RaisedFaceHeight,
                            "raised-face height",
                            out raisedFaceHeightMillimetres,
                            out error) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.G,
                            "raised-face diameter G",
                            out raisedFaceDiameterMillimetres,
                            out error))
                    {
                        return false;
                    }
                }
            }

            if (totalLengthMillimetres <= 0 ||
                outsideDiameterMillimetres <= 0 ||
                (!isBlind &&
                 (boreDiameterMillimetres <= 0 ||
                  boreDiameterMillimetres >=
                      outsideDiameterMillimetres)))
            {
                error =
                    "The resolved Atlas body dimensions are not physically " +
                    "valid for " +
                    BuildFlangeConfigurationLabel(configuration) + ".";
                return false;
            }

            try
            {
                body = CreateAtlasAxisymmetricFlangeSolid(
                    start,
                    axis,
                    outsideDiameterMillimetres /
                        (2.0 * FeetToMillimetres),
                    totalLengthMillimetres /
                        FeetToMillimetres,
                    isBlind
                        ? 0.0
                        : boreDiameterMillimetres /
                          (2.0 * FeetToMillimetres),
                    plateThicknessMillimetres /
                        FeetToMillimetres,
                    hubStartDiameterMillimetres /
                        (2.0 * FeetToMillimetres),
                    hubEndDiameterMillimetres /
                        (2.0 * FeetToMillimetres),
                    raisedFaceDiameterMillimetres /
                        (2.0 * FeetToMillimetres),
                    raisedFaceHeightMillimetres /
                        FeetToMillimetres,
                    doc.Application.ShortCurveTolerance);
            }
            catch (Exception ex)
            {
                error =
                    "The Atlas flange body could not be generated from " +
                    BuildFlangeConfigurationLabel(configuration) + ": " +
                    ex.Message;
                return false;
            }

            description =
                BuildFlangeConfigurationLabel(configuration) +
                "; generated " + subtype +
                " body: outside diameter " +
                FormatCatalogMillimetres(outsideDiameterMillimetres) +
                ", overall axial length " +
                FormatCatalogMillimetres(totalLengthMillimetres) +
                (isBlind
                    ? ", no central opening (blind flange)"
                    : ", continuous central opening " +
                      FormatCatalogMillimetres(
                          boreDiameterMillimetres)) +
                (raisedFaceHeightMillimetres > 0
                    ? ", raised face diameter " +
                      FormatCatalogMillimetres(
                          raisedFaceDiameterMillimetres) +
                      " x " +
                      FormatCatalogMillimetres(
                          raisedFaceHeightMillimetres)
                    : string.Empty);

            return body != null &&
                   body.Volume > GeometryTolerance;
        }

        private static bool TryResolveAtlasFlangePlacement(
            Document doc,
            Element flange,
            IList<ConnectorBore> bores,
            out XYZ start,
            out XYZ axis,
            out string error)
        {
            start = null;
            axis = null;
            error = null;

            List<ConnectorBore> physical = (bores ??
                    new List<ConnectorBore>())
                .Where(x =>
                    x != null &&
                    !x.IsSynthetic &&
                    x.OriginalConnectorOrigin != null)
                .ToList();

            if (physical.Count == 0)
            {
                error =
                    "The configured flange placement could not be resolved " +
                    "because no round physical connector was found.";
                return false;
            }

            ConnectorBore attached = physical
                .Where(x =>
                    x.ConnectedElementId != null &&
                    !x.ConnectedElementId.Equals(
                        ElementId.InvalidElementId))
                .OrderByDescending(x =>
                    doc.GetElement(x.ConnectedElementId) is
                        Autodesk.Revit.DB.Plumbing.Pipe)
                .FirstOrDefault();

            if (attached != null)
            {
                ConnectorBore opposite = physical
                    .Where(x => !ReferenceEquals(x, attached))
                    .OrderByDescending(x =>
                        x.OriginalConnectorOrigin.DistanceTo(
                            attached.OriginalConnectorOrigin))
                    .FirstOrDefault();

                if (opposite != null)
                {
                    XYZ span =
                        opposite.OriginalConnectorOrigin -
                        attached.OriginalConnectorOrigin;

                    if (span.GetLength() > GeometryTolerance)
                    {
                        start = attached.OriginalConnectorOrigin;
                        axis = span.Normalize();
                        return true;
                    }
                }

                if (attached.OutwardDirection != null &&
                    attached.OutwardDirection.GetLength() >
                        GeometryTolerance)
                {
                    start = attached.OriginalConnectorOrigin;
                    axis = attached.OutwardDirection
                        .Normalize()
                        .Negate();
                    return true;
                }
            }

            if (physical.Count >= 2)
            {
                ConnectorBore first = physical[0];
                ConnectorBore last = physical
                    .OrderByDescending(x =>
                        x.OriginalConnectorOrigin.DistanceTo(
                            first.OriginalConnectorOrigin))
                    .First();
                XYZ span =
                    last.OriginalConnectorOrigin -
                    first.OriginalConnectorOrigin;

                if (span.GetLength() > GeometryTolerance)
                {
                    start = first.OriginalConnectorOrigin;
                    axis = span.Normalize();
                    return true;
                }
            }

            ConnectorBore directional = physical
                .FirstOrDefault(x =>
                    x.OutwardDirection != null &&
                    x.OutwardDirection.GetLength() >
                        GeometryTolerance);

            if (directional != null)
            {
                start = directional.OriginalConnectorOrigin;
                axis = directional.OutwardDirection.Normalize();
                return true;
            }

            error =
                "The configured flange axis could not be resolved from " +
                "its physical connectors.";
            return false;
        }

        private static Solid CreateAtlasAxisymmetricFlangeSolid(
            XYZ start,
            XYZ axis,
            double outsideRadius,
            double totalLength,
            double boreRadius,
            double plateThickness,
            double hubStartRadius,
            double hubEndRadius,
            double raisedFaceRadius,
            double raisedFaceHeight,
            double shortCurveTolerance)
        {
            XYZ normalizedAxis = axis.Normalize();

            if (boreRadius <= GeometryTolerance)
            {
                return CreateCylinder(
                    start,
                    normalizedAxis,
                    totalLength,
                    outsideRadius);
            }

            // A flat table flange is an annular extrusion, not a revolved
            // profile. Revit divides the planar ends of a full 360-degree
            // revolved annulus at the revolution seam. That coplanar split is
            // preserved by the STEP exporter and appears as a line across the
            // flange face. Building the plate directly from nested circular
            // loops keeps each end as one continuous planar face (with the
            // bore and drilled holes represented as inner loops).
            bool hasHub =
                hubStartRadius > GeometryTolerance &&
                hubEndRadius > GeometryTolerance;
            bool hasRaisedFace =
                raisedFaceHeight > GeometryTolerance &&
                raisedFaceRadius > boreRadius + GeometryTolerance &&
                raisedFaceRadius < outsideRadius - GeometryTolerance;

            if (!hasHub && !hasRaisedFace)
            {
                CurveLoop outerLoop = CreateCircleLoop(
                    start,
                    normalizedAxis,
                    outsideRadius);
                CurveLoop boreLoop = CreateCircleLoop(
                    start,
                    normalizedAxis,
                    boreRadius);

                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop>
                    {
                        outerLoop,
                        boreLoop
                    },
                    normalizedAxis,
                    totalLength,
                    new SolidOptions(
                        ElementId.InvalidElementId,
                        ElementId.InvalidElementId));
            }

            XYZ helper =
                Math.Abs(normalizedAxis.DotProduct(XYZ.BasisZ)) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;
            XYZ radial =
                normalizedAxis.CrossProduct(helper).Normalize();
            XYZ tangential =
                normalizedAxis.CrossProduct(radial).Normalize();
            List<XYZ> points = new List<XYZ>();

            points.Add(
                AtlasFlangeProfilePoint(
                    start,
                    normalizedAxis,
                    radial,
                    0.0,
                    boreRadius));

            if (hubStartRadius > GeometryTolerance &&
                hubEndRadius > GeometryTolerance)
            {
                double plateStart = Math.Max(
                    shortCurveTolerance * 1.01,
                    totalLength - plateThickness);

                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        0.0,
                        hubStartRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        plateStart,
                        hubEndRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        plateStart,
                        outsideRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        totalLength,
                        outsideRadius));
            }
            else if (raisedFaceHeight > GeometryTolerance &&
                     raisedFaceRadius > boreRadius +
                         GeometryTolerance &&
                     raisedFaceRadius < outsideRadius -
                         GeometryTolerance)
            {
                // Atlas states that table-flange D includes any optional
                // raised-face height. Keep total axial length equal to D.
                double baseEnd =
                    totalLength - raisedFaceHeight;

                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        0.0,
                        outsideRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        baseEnd,
                        outsideRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        baseEnd,
                        raisedFaceRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        totalLength,
                        raisedFaceRadius));
            }
            else
            {
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        0.0,
                        outsideRadius));
                points.Add(
                    AtlasFlangeProfilePoint(
                        start,
                        normalizedAxis,
                        radial,
                        totalLength,
                        outsideRadius));
            }

            points.Add(
                AtlasFlangeProfilePoint(
                    start,
                    normalizedAxis,
                    radial,
                    totalLength,
                    boreRadius));

            CurveLoop profile = CreateClosedLinearProfileLoop(
                points,
                shortCurveTolerance);
            Frame frame = new Frame(
                start,
                radial,
                tangential,
                normalizedAxis);

            return GeometryCreationUtilities.CreateRevolvedGeometry(
                frame,
                new List<CurveLoop> { profile },
                0.0,
                2.0 * Math.PI,
                new SolidOptions(
                    ElementId.InvalidElementId,
                    ElementId.InvalidElementId));
        }

        private static XYZ AtlasFlangeProfilePoint(
            XYZ origin,
            XYZ axis,
            XYZ radial,
            double axialStation,
            double radius)
        {
            return origin +
                   (axis * axialStation) +
                   (radial * radius);
        }

        private static bool TryResolveTableFlangeBoreDiameter(
            AtlasFlangeReferenceRow configuration,
            IList<ConnectorBore> bores,
            double outsideDiameterMillimetres,
            out double boreDiameterMillimetres,
            out string error)
        {
            boreDiameterMillimetres = 0.0;
            error = null;
            double nominal = configuration.NominalSizeSort;

            List<double> outsideDiameters = (bores ??
                    new List<ConnectorBore>())
                .Where(x =>
                    x != null &&
                    !x.IsSynthetic &&
                    x.OutsideDiameter > GeometryTolerance)
                .Select(x =>
                    x.OutsideDiameter * FeetToMillimetres)
                .Where(x =>
                    x > nominal &&
                    x < outsideDiameterMillimetres &&
                    x <= nominal * 1.60)
                .OrderBy(x => x)
                .ToList();

            return TryResolveTableFlangeBoreDiameter(
                configuration,
                outsideDiameters,
                outsideDiameterMillimetres,
                out boreDiameterMillimetres,
                out error);
        }

        // Created by Jhay: shared table-flange bore resolver so body creation
        // and slip-on fit-up validation use the identical Atlas clearance rule.
        private static bool TryResolveTableFlangeBoreDiameter(
            AtlasFlangeReferenceRow configuration,
            IEnumerable<double> pipeOutsideDiametersMillimetres,
            double outsideDiameterMillimetres,
            out double boreDiameterMillimetres,
            out string error)
        {
            boreDiameterMillimetres = 0.0;
            error = null;
            double nominal = configuration.NominalSizeSort;

            List<double> outsideDiameters =
                (pipeOutsideDiametersMillimetres ??
                 Enumerable.Empty<double>())
                    .Where(x =>
                        x > nominal &&
                        x < outsideDiameterMillimetres &&
                        x <= nominal * 1.60)
                    .OrderBy(x => x)
                    .ToList();

            if (outsideDiameters.Count == 0)
            {
                error =
                    "The pipe/tube outside diameter required for the Atlas " +
                    "plate-flange opening could not be resolved from a " +
                    "connected pipe or verified fabrication component. The " +
                    "manual permits no more than 4 mm diametral clearance, " +
                    "so the opening will not be guessed from nominal size.";
                return false;
            }

            double pipeOutsideDiameter = outsideDiameters[0];

            // Section 3, table-flange notes: a diametral clearance of 4 mm
            // maximum applies to pipe or tube OD for plate flanges. Use the
            // stated maximum so the generated slip-on opening is guaranteed
            // to pass the verified connected pipe OD without exceeding Atlas.
            boreDiameterMillimetres =
                pipeOutsideDiameter + 4.0;

            double pitchCircle;
            double boltHoleDiameter;

            if (TryParseCatalogMillimetres(
                    configuration.K,
                    out pitchCircle) &&
                TryParseCatalogMillimetres(
                    configuration.H,
                    out boltHoleDiameter) &&
                boreDiameterMillimetres + boltHoleDiameter >=
                    pitchCircle)
            {
                error =
                    "The resolved slip-on opening would overlap the Atlas " +
                    "bolt circle for " +
                    BuildFlangeConfigurationLabel(configuration) + ".";
                return false;
            }

            return true;
        }

        private static bool TryParseRequiredAtlasDimension(
            AtlasFlangeReferenceRow configuration,
            string value,
            string dimensionName,
            out double millimetres,
            out string error)
        {
            if (TryParseCatalogMillimetres(
                    value,
                    out millimetres) &&
                millimetres > 0)
            {
                error = null;
                return true;
            }

            error =
                "The Atlas row for " +
                BuildFlangeConfigurationLabel(configuration) +
                " does not define a numeric " + dimensionName +
                ". The configured flange cannot be generated without " +
                "guessing.";
            return false;
        }

        private static string FormatCatalogMillimetres(
            double millimetres)
        {
            return millimetres.ToString(
                       "0.###",
                       CultureInfo.InvariantCulture) +
                   " mm";
        }

        internal static IList<FabricationFlangeReferenceMatch>
            InspectSelectedFlanges(
                Document doc,
                FabricationSelection selection)
        {
            List<FabricationFlangeReferenceMatch> results =
                new List<FabricationFlangeReferenceMatch>();

            if (doc == null || selection == null)
                return results;

            foreach (Element flange in selection.SourceElementIds
                         .Where(x => x != null)
                         .Select(doc.GetElement)
                         .Where(x => IsFlangeLike(doc, x)))
            {
                List<double> nominalSizes =
                    GetPhysicalConnectorNominalSizesMillimetres(flange);
                AtlasFlangeReferenceRow row;
                int nominalDiameter;
                string error;
                bool matched = TryResolveFlangeConfiguration(
                    doc,
                    flange,
                    nominalSizes,
                    out row,
                    out nominalDiameter,
                    out error);

                results.Add(new FabricationFlangeReferenceMatch
                {
                    ElementId = flange.Id,
                    ElementName = GetElementDisplayName(flange),
                    NominalDiameterMm = nominalDiameter,
                    ReferenceTable = row?.Section,
                    IsMatched = matched,
                    Error = error
                });
            }

            return results;
        }

        private static bool TryResolveFlangeConfiguration(
            Document doc,
            Element flange,
            IList<ConnectorBore> bores,
            out AtlasFlangeReferenceRow configuration,
            out string error)
        {
            List<double> nominalSizes = (bores ??
                    new List<ConnectorBore>())
                .Where(x =>
                    x != null &&
                    !x.IsSynthetic &&
                    x.NominalDiameter > GeometryTolerance)
                .Select(x =>
                    x.NominalDiameter * FeetToMillimetres)
                .ToList();
            int ignoredNominalDiameter;

            return TryResolveFlangeConfiguration(
                doc,
                flange,
                nominalSizes,
                out configuration,
                out ignoredNominalDiameter,
                out error);
        }

        private static bool TryResolveFlangeConfiguration(
            Document doc,
            Element flange,
            IList<double> nominalSizes,
            out AtlasFlangeReferenceRow configuration,
            out int nominalDiameter,
            out string error)
        {
            configuration = null;
            nominalDiameter = 0;
            error = null;

            string referenceSection =
                ResolveAtlasReferenceSectionFromName(doc, flange);

            if (string.IsNullOrWhiteSpace(referenceSection))
            {
                error =
                    "The flange name does not identify one supported Atlas " +
                    "table. Include Class 150/300/600/900/1500/2500, " +
                    "Table D/E/F/H, AS 4087 PN16, or EN 1092 PN16 in the " +
                    "family or type name.";
                return false;
            }

            List<double> usableNominalSizes = (nominalSizes ??
                    new List<double>())
                .Where(x => x > 0)
                .ToList();

            if (usableNominalSizes.Count == 0)
            {
                error =
                    "The nominal diameter could not be resolved from a " +
                    "round physical piping connector.";
                return false;
            }

            if (usableNominalSizes.Max() -
                usableNominalSizes.Min() > 1.0)
            {
                error =
                    "The flange connectors report different nominal " +
                    "diameters, so one Atlas row cannot be selected safely.";
                return false;
            }

            double nominalMillimetres = usableNominalSizes.Average();
            nominalDiameter = (int)Math.Round(
                nominalMillimetres,
                MidpointRounding.AwayFromZero);

            List<AtlasFlangeReferenceRow> matches =
                AtlasFlangeReferenceCatalog.Load()
                    .Where(x =>
                        string.Equals(
                            x.Section,
                            referenceSection,
                            StringComparison.Ordinal) &&
                        Math.Abs(
                            x.NominalSizeSort -
                            nominalMillimetres) <= 1.0)
                    .ToList();

            if (matches.Count != 1)
            {
                error =
                    "No unique row exists in " + referenceSection +
                    " for connector nominal diameter " +
                    nominalMillimetres.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm.";
                return false;
            }

            configuration = matches[0];
            nominalDiameter = configuration.NominalSizeSort;
            return true;
        }

        private static List<double>
            GetPhysicalConnectorNominalSizesMillimetres(Element flange)
        {
            List<double> result = new List<double>();
            ConnectorManager manager = GetConnectorManager(flange);

            if (manager == null)
                return result;

            foreach (Connector connector in manager.Connectors)
            {
                if (connector == null ||
                    connector.Domain != Domain.DomainPiping ||
                    connector.ConnectorType != ConnectorType.End ||
                    connector.Shape != ConnectorProfileType.Round ||
                    connector.Radius <= GeometryTolerance)
                {
                    continue;
                }

                result.Add(
                    connector.Radius * 2.0 * FeetToMillimetres);
            }

            return result;
        }

        private static string ResolveAtlasReferenceSectionFromName(
            Document doc,
            Element flange)
        {
            string normalized = NormalizeClassificationText(
                BuildFlangeReferenceNameText(doc, flange));
            string compact = normalized.Replace(" ", string.Empty);

            if (compact.Contains("AS4087") &&
                compact.Contains("PN16"))
            {
                return "PN16 Flanges to AS 4087";
            }

            if (compact.Contains("EN1092") &&
                compact.Contains("PN16"))
            {
                return "PN16 Flanges to EN 1092";
            }

            string[] tableLetters = { "D", "E", "F", "H" };
            string padded = " " + normalized + " ";

            foreach (string tableLetter in tableLetters)
            {
                if (padded.Contains(
                        " TABLE " + tableLetter + " ") ||
                    compact.Contains("TABLE" + tableLetter))
                {
                    return "Table " + tableLetter +
                           " Flanges to AS 2129";
                }
            }

            string[] classes =
            {
                "2500", "1500", "900", "600", "300", "150"
            };

            foreach (string flangeClass in classes)
            {
                if (padded.Contains(
                        " CLASS " + flangeClass + " ") ||
                    compact.Contains("CLASS" + flangeClass))
                {
                    return "Class " + flangeClass +
                           " Flanges to ASME B16.5";
                }
            }

            return null;
        }

        private static string BuildFlangeReferenceNameText(
            Document doc,
            Element flange)
        {
            StringBuilder text = new StringBuilder();
            text.Append(GetElementDisplayName(flange));
            text.Append(' ');
            text.Append(flange?.Name);

            Element type = GetElementTypeCached(doc, flange);
            if (type != null)
            {
                text.Append(' ');
                text.Append(type.Name);
            }

            FamilyInstance familyInstance = flange as FamilyInstance;
            FamilySymbol symbol = familyInstance?.Symbol;
            if (symbol != null)
            {
                text.Append(' ');
                text.Append(symbol.FamilyName);
            }

            return text.ToString();
        }

        private static bool TryParseCatalogMillimetres(
            string value,
            out double millimetres)
        {
            millimetres = 0.0;

            if (string.IsNullOrWhiteSpace(value) ||
                value == "-")
            {
                return false;
            }

            string candidate = value.Trim();
            int dualUnitSeparator =
                candidate.LastIndexOf(
                    " - ",
                    StringComparison.Ordinal);

            if (dualUnitSeparator >= 0)
            {
                candidate = candidate.Substring(
                    dualUnitSeparator + 3);
            }

            candidate = candidate
                .Replace("mm", string.Empty)
                .TrimStart('*')
                .Trim();

            return double.TryParse(
                candidate,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out millimetres);
        }

        private static XYZ ProjectOntoPerpendicularPlane(
            XYZ vector,
            XYZ normal)
        {
            if (vector == null ||
                normal == null ||
                vector.GetLength() <= GeometryTolerance ||
                normal.GetLength() <= GeometryTolerance)
            {
                return null;
            }

            XYZ normalizedNormal = normal.Normalize();
            XYZ projected =
                vector -
                (normalizedNormal *
                 vector.DotProduct(normalizedNormal));

            return projected.GetLength() > GeometryTolerance
                ? projected.Normalize()
                : null;
        }

        private static string BuildFlangeConfigurationLabel(
            AtlasFlangeReferenceRow configuration)
        {
            return configuration.Section + ", DN " +
                   configuration.DN;
        }
    }
}
