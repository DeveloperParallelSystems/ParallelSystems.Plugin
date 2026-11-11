using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models;
using ParallelSystemsPlugin.Models.Configs;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        internal static bool IsSupportedSlipOnFitUpConnectedElement(
            bool isPipe,
            bool isShapedBranch)
        {
            return isPipe || isShapedBranch;
        }

        // Created by Jhay: resolve design-connector versus physical pipe-end
        // semantics before any pipe solid is built. No standard-derived or
        // implicit axial fit-up value is allowed here.
        private static Dictionary<ElementId, List<SlipOnFlangeFitUp>>
            ResolveSelectedAtlasSlipOnFlangeFitUps(
                Document doc,
                IList<Element> sourceElements,
                IDictionary<ElementId, PipeDimensions> pipeDimensions,
                IDictionary<ElementId, PipeDimensions>
                    componentDimensionOverrides,
                ISet<ElementId> selectedSourceIds,
                FabricationFlangeGeometryMode flangeGeometryMode,
                IList<FabricationIssue> issues)
        {
            Dictionary<ElementId, List<SlipOnFlangeFitUp>> result =
                new Dictionary<ElementId, List<SlipOnFlangeFitUp>>();

            if (doc == null ||
                sourceElements == null ||
                flangeGeometryMode !=
                    FabricationFlangeGeometryMode.AtlasConfiguration)
            {
                return result;
            }

            FabricationConfig fabricationConfiguration =
                AppConfig.CurrentConfig?.Fabrication ??
                new FabricationConfig();

            foreach (Element flange in sourceElements.Where(x =>
                         IsFlangeLike(doc, x)))
            {
                AtlasFlangeReferenceRow configuration;
                int nominalDiameter;
                string configurationError;

                if (!TryResolveFlangeConfiguration(
                        doc,
                        flange,
                        GetPhysicalConnectorNominalSizesMillimetres(flange),
                        out configuration,
                        out nominalDiameter,
                        out configurationError) ||
                    !IsConfirmedAtlasTablePlateSlipOnFlange(
                        doc,
                        flange,
                        configuration))
                {
                    continue;
                }

                ConnectorManager manager = GetConnectorManager(flange);
                if (manager == null)
                    continue;

                List<Tuple<Connector, Element>> selectedConnections =
                    new List<Tuple<Connector, Element>>();

                foreach (Connector connector in manager.Connectors)
                {
                    if (!IsRoundEndConnector(connector))
                        continue;

                    Element connectedElement = GetConnectedElement(
                        flange,
                        connector,
                        selectedSourceIds);

                    bool isPipe = connectedElement is Pipe;
                    bool isShapedBranch =
                        connectedElement != null &&
                        IsShapedBranchLike(doc, connectedElement);

                    if (!IsSupportedSlipOnFitUpConnectedElement(
                            isPipe,
                            isShapedBranch) ||
                        !selectedSourceIds.Contains(connectedElement.Id) ||
                        selectedConnections.Any(x =>
                            x.Item2.Id.Equals(connectedElement.Id)))
                    {
                        continue;
                    }

                    selectedConnections.Add(
                        Tuple.Create(connector, connectedElement));
                }

                // A selected flange without its connected pipe or shaped
                // branch does not alter an unselected component at the spool
                // boundary.
                foreach (Tuple<Connector, Element> connection in
                         selectedConnections)
                {
                    Connector flangeConnector = connection.Item1;
                    Element connectedElement = connection.Item2;
                    Pipe pipe = connectedElement as Pipe;
                    bool isShapedBranch =
                        pipe == null &&
                        IsShapedBranchLike(doc, connectedElement);
                    PipeDimensions dimensions = null;

                    bool dimensionsResolved = pipe != null
                        ? pipeDimensions.TryGetValue(
                            connectedElement.Id,
                            out dimensions)
                        : componentDimensionOverrides != null &&
                          componentDimensionOverrides.TryGetValue(
                              connectedElement.Id,
                              out dimensions);

                    if (!dimensionsResolved ||
                        dimensions == null)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The connected pipe or shaped-branch outlet " +
                            "dimensions could not be resolved for slip-on " +
                            "flange fabrication fit-up.");
                        continue;
                    }

                    double flangeOutsideMillimetres;
                    double flangeThicknessMillimetres;
                    string dimensionError;
                    string thicknessValue = string.Equals(
                        configuration.Kind,
                        "EN1092",
                        StringComparison.Ordinal)
                            ? configuration.SlipOnWeldingThickness
                            : configuration.D;

                    if (!TryParseRequiredAtlasDimension(
                            configuration,
                            configuration.A,
                            "outside diameter A",
                            out flangeOutsideMillimetres,
                            out dimensionError) ||
                        !TryParseRequiredAtlasDimension(
                            configuration,
                            thicknessValue,
                            "slip-on welding thickness D",
                            out flangeThicknessMillimetres,
                            out dimensionError))
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            dimensionError);
                        continue;
                    }

                    double pipeOutsideMillimetres =
                        dimensions.OutsideDiameter * FeetToMillimetres;
                    double flangeBoreMillimetres;

                    if (!TryResolveTableFlangeBoreDiameter(
                            configuration,
                            new[] { pipeOutsideMillimetres },
                            flangeOutsideMillimetres,
                            out flangeBoreMillimetres,
                            out dimensionError))
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            dimensionError);
                        continue;
                    }

                    double diametralClearance =
                        flangeBoreMillimetres -
                        pipeOutsideMillimetres;

                    if (diametralClearance <= 0.0 ||
                        diametralClearance > 4.0 + 0.01)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The resolved flange bore/pipe diametral " +
                            "clearance is outside the supported Atlas table-" +
                            "flange limit.");
                        continue;
                    }

                    // Changed by Jhay: resolve the populated setback band for
                    // this flange's own DN so mixed-size spools stay dynamic.
                    double setbackMillimetres;

                    try
                    {
                        setbackMillimetres = fabricationConfiguration
                            .ResolveSlipOnPipeFaceSetbackMillimetres(
                                nominalDiameter);
                    }
                    catch (InvalidOperationException ex)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            ex.Message);
                        continue;
                    }
                    double insertionMillimetres =
                        flangeThicknessMillimetres -
                        setbackMillimetres;

                    if (setbackMillimetres < 0.0 ||
                        insertionMillimetres <= 0.01 ||
                        insertionMillimetres >
                            flangeThicknessMillimetres + 0.01)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The configured pipe-face setback " +
                            FormatCatalogMillimetres(setbackMillimetres) +
                            " is not valid for flange thickness " +
                            FormatCatalogMillimetres(
                                flangeThicknessMillimetres) + ".");
                        continue;
                    }

                    List<ConnectorBore> placementConnectors =
                        CreateAtlasFitUpPlacementConnectors(
                            doc,
                            flange,
                            flangeConnector,
                            connectedElement);
                    XYZ flangePipeSideFace;
                    XYZ flangeAxis;
                    string placementError;

                    if (!TryResolveAtlasFlangePlacement(
                            doc,
                            flange,
                            placementConnectors,
                            out flangePipeSideFace,
                            out flangeAxis,
                            out placementError))
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            placementError);
                        continue;
                    }

                    bool isPipeStart = false;
                    XYZ designConnectedEnd = flangeConnector.Origin;
                    XYZ connectedAxis = GetConnectorOutwardDirection(
                        flange,
                        flangeConnector,
                        connectedElement);
                    double connectorDistance =
                        designConnectedEnd.DistanceTo(flangePipeSideFace);

                    if (pipe != null)
                    {
                        XYZ pipeStart;
                        XYZ pipeEnd;
                        XYZ pipeDirection;
                        double pipeLength;

                        if (!TryGetStraightPipeAxis(
                                pipe,
                                out pipeStart,
                                out pipeEnd,
                                out pipeDirection,
                                out pipeLength))
                        {
                            AddSlipOnFitUpBlockingIssue(
                                issues,
                                flange,
                                "Slip-on fit-up requires a straight rigid pipe.");
                            continue;
                        }

                        double startDistance =
                            pipeStart.DistanceTo(flangePipeSideFace);
                        double endDistance =
                            pipeEnd.DistanceTo(flangePipeSideFace);
                        isPipeStart = startDistance <= endDistance;
                        connectorDistance = Math.Min(
                            startDistance,
                            endDistance);
                        designConnectedEnd = isPipeStart
                            ? pipeStart
                            : pipeEnd;
                        connectedAxis = pipeDirection.Normalize();
                    }

                    double connectionTolerance = Math.Max(
                        1.0 / FeetToMillimetres,
                        dimensions.OutsideDiameter * 0.01);

                    if (connectorDistance > connectionTolerance ||
                        Math.Abs(
                            connectedAxis.Normalize().DotProduct(
                                flangeAxis.Normalize())) <
                            ConnectorDirectionTolerance)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The pipe and slip-on flange connection axes are " +
                            "not concentric/aligned within fabrication " +
                            "tolerance.");
                        continue;
                    }

                    double insertionFeet =
                        insertionMillimetres / FeetToMillimetres;
                    double thicknessFeet =
                        flangeThicknessMillimetres / FeetToMillimetres;
                    XYZ fabricationPipeEnd =
                        flangePipeSideFace +
                        (flangeAxis.Normalize() * insertionFeet);
                    XYZ matingFace =
                        flangePipeSideFace +
                        (flangeAxis.Normalize() * thicknessFeet);
                    if (pipe != null)
                    {
                        double extensionDirection =
                            (fabricationPipeEnd - designConnectedEnd)
                                .DotProduct(connectedAxis.Normalize());

                        if ((isPipeStart &&
                             extensionDirection >= -GeometryTolerance) ||
                            (!isPipeStart &&
                             extensionDirection <= GeometryTolerance))
                        {
                            AddSlipOnFitUpBlockingIssue(
                                issues,
                                flange,
                                "The resolved flange axis does not extend " +
                                "away from the selected pipe end. Verify " +
                                "the flange connector orientation.");
                            continue;
                        }
                    }

                    List<SlipOnFlangeFitUp> connectedElementFitUps;
                    if (!result.TryGetValue(
                            connectedElement.Id,
                            out connectedElementFitUps))
                    {
                        connectedElementFitUps =
                            new List<SlipOnFlangeFitUp>();
                        result[connectedElement.Id] =
                            connectedElementFitUps;
                    }

                    if (connectedElementFitUps.Any(x =>
                            pipe != null
                                ? x.IsPipeStart == isPipeStart
                                : ConnectorOriginsMatch(
                                    x.DesignConnectionOrigin,
                                    designConnectedEnd)))
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "More than one selected slip-on flange resolved " +
                            "to the same physical pipe end.");
                        continue;
                    }

                    SlipOnFlangeFitUp fitUp =
                        new SlipOnFlangeFitUp
                        {
                            ConnectedElementId = connectedElement.Id,
                            ConnectedElementIsShapedBranch = isShapedBranch,
                            FlangeId = flange.Id,
                            FlangeName = GetElementDisplayName(flange),
                            StandardSection = configuration.Section,
                            NominalDiameterMillimetres = nominalDiameter,
                            IsPipeStart = isPipeStart,
                            DesignConnectionOrigin = designConnectedEnd,
                            FabricationPipeEnd = fabricationPipeEnd,
                            FlangeMatingFace = matingFace,
                            FlangeAxis = flangeAxis.Normalize(),
                            PipeOutsideDiameterMillimetres =
                                pipeOutsideMillimetres,
                            FlangeBoreMillimetres =
                                flangeBoreMillimetres,
                            FlangeThicknessMillimetres =
                                flangeThicknessMillimetres,
                            PipeFaceSetbackMillimetres =
                                setbackMillimetres,
                            PipeFaceSetbackSource = nominalDiameter <= 125
                                ? "DN125-and-below configuration"
                                : "DN150-and-above configuration",
                            PipeInsertionDepthMillimetres =
                                insertionMillimetres
                        };

                    connectedElementFitUps.Add(fitUp);

                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Information,
                        ElementId = flange.Id,
                        ElementName = GetElementDisplayName(flange),
                        Message = BuildSlipOnFitUpDiagnostic(fitUp)
                    });
                }
            }

            return result;
        }

        private static bool IsConfirmedAtlasTablePlateSlipOnFlange(
            Document doc,
            Element flange,
            AtlasFlangeReferenceRow configuration)
        {
            if (configuration == null ||
                string.Equals(
                    configuration.Kind,
                    "ASME",
                    StringComparison.Ordinal))
            {
                return false;
            }

            return IsConfirmedPlateSlipOnFlange(doc, flange);
        }

        private static List<ConnectorBore>
            CreateAtlasFitUpPlacementConnectors(
                Document doc,
                Element flange,
                Connector attachedConnector,
                Element attachedElement)
        {
            List<ConnectorBore> result = new List<ConnectorBore>();
            ConnectorManager manager = GetConnectorManager(flange);

            if (manager == null)
                return result;

            foreach (Connector connector in manager.Connectors)
            {
                if (!IsRoundEndConnector(connector))
                    continue;

                bool isAttached = ReferenceEquals(
                    connector,
                    attachedConnector) ||
                    ConnectorOriginsMatch(
                        connector.Origin,
                        attachedConnector.Origin);

                result.Add(new ConnectorBore
                {
                    Origin = connector.Origin,
                    OriginalConnectorOrigin = connector.Origin,
                    OutwardDirection = GetConnectorOutwardDirection(
                        flange,
                        connector,
                        isAttached ? attachedElement : null),
                    NominalDiameter = connector.Radius * 2.0,
                    ConnectedElementId = isAttached
                        ? attachedElement.Id
                        : ElementId.InvalidElementId,
                    IsSynthetic = false
                });
            }

            return result;
        }

        private static bool IsRoundEndConnector(Connector connector)
        {
            return connector != null &&
                   connector.Domain == Domain.DomainPiping &&
                   connector.ConnectorType == ConnectorType.End &&
                   connector.Shape == ConnectorProfileType.Round &&
                   connector.Radius > GeometryTolerance;
        }

        private static void AddSlipOnFitUpBlockingIssue(
            IList<FabricationIssue> issues,
            Element flange,
            string message)
        {
            issues.Add(new FabricationIssue
            {
                Severity = FabricationIssueSeverity.Blocking,
                ElementId = flange?.Id,
                ElementName = GetElementDisplayName(flange),
                Message = message
            });
        }

        private static string BuildSlipOnFitUpDiagnostic(
            SlipOnFlangeFitUp fitUp)
        {
            return
                "Slip-On Flange fit-up: Standard/Table " +
                fitUp.StandardSection +
                "; DN " +
                fitUp.NominalDiameterMillimetres.ToString(
                    CultureInfo.InvariantCulture) +
                "; Pipe OD " +
                FormatCatalogMillimetres(
                    fitUp.PipeOutsideDiameterMillimetres) +
                "; Flange bore " +
                FormatCatalogMillimetres(
                    fitUp.FlangeBoreMillimetres) +
                "; Diametral clearance " +
                FormatCatalogMillimetres(
                    fitUp.FlangeBoreMillimetres -
                    fitUp.PipeOutsideDiameterMillimetres) +
                "; Radial clearance " +
                FormatCatalogMillimetres(
                    (fitUp.FlangeBoreMillimetres -
                     fitUp.PipeOutsideDiameterMillimetres) / 2.0) +
                "; Flange thickness " +
                FormatCatalogMillimetres(
                    fitUp.FlangeThicknessMillimetres) +
                "; Pipe-face setback " +
                FormatCatalogMillimetres(
                    fitUp.PipeFaceSetbackMillimetres) +
                " (" + fitUp.PipeFaceSetbackSource + ")" +
                "; Pipe insertion depth " +
                FormatCatalogMillimetres(
                    fitUp.PipeInsertionDepthMillimetres) +
                "; Connector role: design connection datum; " +
                (fitUp.ConnectedElementIsShapedBranch
                    ? "shaped-branch outlet"
                    : "pipe physical end") +
                " extended into flange: Yes; flange " +
                "mating face retained: Yes; connected/flange solids remain " +
                "separate: Yes.";
        }

        private sealed class SlipOnFlangeFitUp
        {
            public ElementId ConnectedElementId { get; set; }
            public bool ConnectedElementIsShapedBranch { get; set; }
            public ElementId FlangeId { get; set; }
            public string FlangeName { get; set; }
            public string StandardSection { get; set; }
            public int NominalDiameterMillimetres { get; set; }
            public bool IsPipeStart { get; set; }
            public XYZ DesignConnectionOrigin { get; set; }
            public XYZ FabricationPipeEnd { get; set; }
            public XYZ FlangeMatingFace { get; set; }
            public XYZ FlangeAxis { get; set; }
            public double PipeOutsideDiameterMillimetres { get; set; }
            public double FlangeBoreMillimetres { get; set; }
            public double FlangeThicknessMillimetres { get; set; }
            public double PipeFaceSetbackMillimetres { get; set; }
            public string PipeFaceSetbackSource { get; set; }
            public double PipeInsertionDepthMillimetres { get; set; }
        }
    }
}
