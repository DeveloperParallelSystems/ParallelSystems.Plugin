using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Configs;
using ParallelSystemsPlugin.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        // Created by Jhay: preflight query used by the command to open the
        // exact configuration field only when a selected Atlas table/plate
        // slip-on flange actually requires this fabrication rule.
        internal static bool RequiresSlipOnPipeFaceSetback(
            Document doc,
            FabricationSelection selection)
        {
            if (doc == null || selection?.SourceElementIds == null)
                return false;

            foreach (ElementId elementId in selection.SourceElementIds)
            {
                Element flange = doc.GetElement(elementId);
                if (!IsFlangeLike(doc, flange))
                    continue;

                AtlasFlangeReferenceRow configuration;
                int nominalDiameter;
                string error;

                if (TryResolveFlangeConfiguration(
                        doc,
                        flange,
                        GetPhysicalConnectorNominalSizesMillimetres(flange),
                        out configuration,
                        out nominalDiameter,
                        out error) &&
                    IsConfirmedAtlasTablePlateSlipOnFlange(
                        doc,
                        flange,
                        configuration))
                {
                    return true;
                }
            }

            return false;
        }

        // Created by Jhay: resolve design-connector versus physical pipe-end
        // semantics before any pipe solid is built. No standard-derived or
        // implicit axial fit-up value is allowed here.
        private static Dictionary<ElementId, List<SlipOnFlangeFitUp>>
            ResolveSelectedAtlasSlipOnFlangeFitUps(
                Document doc,
                IList<Element> sourceElements,
                IDictionary<ElementId, PipeDimensions> pipeDimensions,
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

            double? configuredSetbackMillimetres =
                AppConfig.CurrentConfig?.Fabrication
                    ?.SlipOnPipeFaceSetbackMillimetres;

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

                List<Tuple<Connector, Pipe>> selectedPipeConnections =
                    new List<Tuple<Connector, Pipe>>();

                foreach (Connector connector in manager.Connectors)
                {
                    if (!IsRoundEndConnector(connector))
                        continue;

                    Pipe pipe = GetConnectedElement(
                        flange,
                        connector,
                        selectedSourceIds) as Pipe;

                    if (pipe == null ||
                        !selectedSourceIds.Contains(pipe.Id) ||
                        selectedPipeConnections.Any(x =>
                            x.Item2.Id.Equals(pipe.Id)))
                    {
                        continue;
                    }

                    selectedPipeConnections.Add(
                        Tuple.Create(connector, pipe));
                }

                // A selected flange without its connected pipe does not alter
                // an unselected component at the spool boundary.
                foreach (Tuple<Connector, Pipe> connection in
                         selectedPipeConnections)
                {
                    Connector flangeConnector = connection.Item1;
                    Pipe pipe = connection.Item2;
                    PipeDimensions dimensions;

                    if (!pipeDimensions.TryGetValue(
                            pipe.Id,
                            out dimensions) ||
                        dimensions == null)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The connected pipe dimensions could not be " +
                            "resolved for slip-on flange fabrication fit-up.");
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

                    if (!configuredSetbackMillimetres.HasValue)
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "Slip-on flange fabrication fit-up could not be " +
                            "resolved. The flange dimensions and pipe " +
                            "clearance are known, but the required axial " +
                            "pipe-end setback must be supplied in " +
                            "Configurations > Fabrication from the applicable " +
                            "fabrication/WPS configuration before the STEP " +
                            "can be generated.");
                        continue;
                    }

                    double setbackMillimetres =
                        configuredSetbackMillimetres.Value;
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
                            pipe);
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
                    bool isPipeStart = startDistance <= endDistance;
                    double connectorDistance = Math.Min(
                        startDistance,
                        endDistance);
                    double connectionTolerance = Math.Max(
                        1.0 / FeetToMillimetres,
                        dimensions.OutsideDiameter * 0.01);

                    if (connectorDistance > connectionTolerance ||
                        Math.Abs(
                            pipeDirection.Normalize().DotProduct(
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
                    XYZ designPipeEnd = isPipeStart
                        ? pipeStart
                        : pipeEnd;
                    double extensionDirection =
                        (fabricationPipeEnd - designPipeEnd)
                            .DotProduct(pipeDirection.Normalize());

                    if ((isPipeStart &&
                         extensionDirection >= -GeometryTolerance) ||
                        (!isPipeStart &&
                         extensionDirection <= GeometryTolerance))
                    {
                        AddSlipOnFitUpBlockingIssue(
                            issues,
                            flange,
                            "The resolved flange axis does not extend away " +
                            "from the selected pipe end. Verify the flange " +
                            "connector orientation.");
                        continue;
                    }

                    List<SlipOnFlangeFitUp> pipeFitUps;
                    if (!result.TryGetValue(pipe.Id, out pipeFitUps))
                    {
                        pipeFitUps = new List<SlipOnFlangeFitUp>();
                        result[pipe.Id] = pipeFitUps;
                    }

                    if (pipeFitUps.Any(x =>
                            x.IsPipeStart == isPipeStart))
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
                            PipeId = pipe.Id,
                            FlangeId = flange.Id,
                            FlangeName = GetElementDisplayName(flange),
                            StandardSection = configuration.Section,
                            NominalDiameterMillimetres = nominalDiameter,
                            IsPipeStart = isPipeStart,
                            DesignConnectionOrigin = flangePipeSideFace,
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
                            PipeInsertionDepthMillimetres =
                                insertionMillimetres
                        };

                    pipeFitUps.Add(fitUp);

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
                Pipe attachedPipe)
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
                        isAttached ? attachedPipe : null),
                    NominalDiameter = connector.Radius * 2.0,
                    ConnectedElementId = isAttached
                        ? attachedPipe.Id
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
                "; Pipe insertion depth " +
                FormatCatalogMillimetres(
                    fitUp.PipeInsertionDepthMillimetres) +
                "; Connector role: design connection datum; " +
                "pipe physical end extended into flange: Yes; flange " +
                "mating face retained: Yes; pipe/flange solids remain " +
                "separate: Yes.";
        }

        private sealed class SlipOnFlangeFitUp
        {
            public ElementId PipeId { get; set; }
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
            public double PipeInsertionDepthMillimetres { get; set; }
        }
    }
}
