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
            double cutterLength = Math.Max(
                GetElementExtent(flange),
                20.0 / FeetToMillimetres);
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

        private static List<ConnectorBore>
            CreateFlangeDrillingConnectorBores(
                Document doc,
                Element flange,
                ISet<ElementId> selectedSourceIds)
        {
            List<ConnectorBore> result = new List<ConnectorBore>();
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

                Element connectedElement = GetConnectedElement(
                    flange,
                    connector,
                    selectedSourceIds);
                XYZ radialBasisX = null;
                XYZ radialBasisY = null;

                try
                {
                    Transform coordinateSystem =
                        connector.CoordinateSystem;

                    if (coordinateSystem != null)
                    {
                        radialBasisX = coordinateSystem.BasisX;
                        radialBasisY = coordinateSystem.BasisY;
                    }
                }
                catch
                {
                    // The bolt-pattern builder has a deterministic fallback
                    // when a family connector has no usable radial basis.
                }

                result.Add(new ConnectorBore
                {
                    Origin = connector.Origin,
                    OriginalConnectorOrigin = connector.Origin,
                    OutwardDirection = GetConnectorOutwardDirection(
                        flange,
                        connector,
                        connectedElement),
                    RadialBasisX = radialBasisX,
                    RadialBasisY = radialBasisY,
                    NominalDiameter = connector.Radius * 2.0,
                    ConnectedElementId = connectedElement?.Id,
                    ConnectedElementName = connectedElement == null
                        ? string.Empty
                        : GetElementDisplayName(connectedElement),
                    IsSynthetic = false,
                    SourceDescription =
                        "Physical flange connector used for Atlas table lookup"
                });
            }

            return result;
        }

        private static FabricationElementGeometry
            BuildAtlasConfiguredFlangeGeometry(
                Document doc,
                Element flange,
                IList<Solid> sourceSolids,
                IList<ConnectorBore> drillingConnectors,
                IList<FabricationIssue> issues)
        {
            List<Solid> boltHoleCutters;
            string boltHoleDescription;
            string boltHoleError;

            if (!TryCreateFlangeBoltHoleCutters(
                    doc,
                    flange,
                    drillingConnectors,
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

            List<Solid> currentSolids = sourceSolids.ToList();
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
                            "not intersect the retained source flange body. " +
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
                    "Source flange body and its existing central opening " +
                    "were retained without generated bore or chamfer " +
                    "changes; " + boltHoleDescription
            };
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
