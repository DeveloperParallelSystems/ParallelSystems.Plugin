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
        private static Dictionary<ElementId, ShapedBranchConnection>
            ResolveShapedBranchConnections(
                Document doc,
                IList<Element> sourceElements,
                IList<Element> calculationElements,
                IDictionary<ElementId, PipeDimensions> knownPipeDimensions,
                ISet<ElementId> selectedSourceIds,
                ISet<ElementId> calculationContextIds,
                IDictionary<ElementId, ElementId>
                    explicitHeaderPipeIdsByBranch,
                IList<FabricationIssue> issues)
        {
            Dictionary<ElementId, ShapedBranchConnection> result =
                new Dictionary<ElementId, ShapedBranchConnection>();

            List<Pipe> contextPipes =
                (calculationElements ?? sourceElements)
                    .OfType<Pipe>()
                    .Where(x =>
                        x != null &&
                        knownPipeDimensions.ContainsKey(x.Id))
                    .GroupBy(x => x.Id)
                    .Select(x => x.First())
                    .ToList();

            foreach (Element fitting in sourceElements.Where(x =>
                         IsShapedBranchLike(doc, x)))
            {
                ConnectorManager manager = GetConnectorManager(fitting);

                if (manager == null)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped branch has no readable piping connectors."
                    });

                    continue;
                }

                List<Connector> connectors = manager.Connectors
                    .Cast<Connector>()
                    .Where(x =>
                        x != null &&
                        x.Domain == Domain.DomainPiping &&
                        x.ConnectorType == ConnectorType.End &&
                        x.Shape == ConnectorProfileType.Round)
                    .ToList();

                if (connectors.Count == 0)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped branch has no round end connectors."
                    });

                    continue;
                }

                double maximumConnectorDiameter = connectors
                    .Select(x =>
                    {
                        try
                        {
                            return x.Radius * 2.0;
                        }
                        catch
                        {
                            return 0.0;
                        }
                    })
                    .DefaultIfEmpty(0.0)
                    .Max();

                List<ShapedBranchPipeCandidate> networkPipeCandidates =
                    new List<ShapedBranchPipeCandidate>();

                foreach (Connector connector in connectors)
                {
                    Pipe connectedPipe;
                    PipeDimensions connectedDimensions;

                    if (!TryFindNearestPipeOnConnectorSide(
                            doc,
                            fitting,
                            connector,
                            knownPipeDimensions,
                            calculationContextIds,
                            false,
                            out connectedPipe,
                            out connectedDimensions))
                    {
                        continue;
                    }

                    AddShapedBranchPipeCandidate(
                        networkPipeCandidates,
                        connector,
                        connectedPipe,
                        connectedDimensions);
                }

                ElementId explicitHeaderPipeId =
                    ElementId.InvalidElementId;

                bool hasExplicitHeader =
                    explicitHeaderPipeIdsByBranch != null &&
                    explicitHeaderPipeIdsByBranch.TryGetValue(
                        fitting.Id,
                        out explicitHeaderPipeId) &&
                    explicitHeaderPipeId != null &&
                    !explicitHeaderPipeId.Equals(
                        ElementId.InvalidElementId);

                Pipe explicitHeaderPipe = hasExplicitHeader
                    ? doc.GetElement(explicitHeaderPipeId) as Pipe
                    : null;

                List<Pipe> primaryHeaderPipes =
                    explicitHeaderPipe != null
                        ? new List<Pipe> { explicitHeaderPipe }
                        : contextPipes;

                ShapedBranchSpatialHeader spatialHeader;
                bool spatialHeaderResolved =
                    TryResolveSpatialShapedBranchHeader(
                        fitting,
                        connectors,
                        primaryHeaderPipes,
                        knownPipeDimensions,
                        out spatialHeader);

                ShapedBranchPipeCandidate header = null;
                string headerResolutionSource = null;

                if (spatialHeaderResolved)
                {
                    header = networkPipeCandidates.FirstOrDefault(x =>
                        x.Pipe.Id.Equals(spatialHeader.Pipe.Id));

                    if (header == null)
                    {
                        header = new ShapedBranchPipeCandidate
                        {
                            Connector = spatialHeader.MatchConnector,
                            Pipe = spatialHeader.Pipe,
                            Dimensions = spatialHeader.Dimensions
                        };
                    }

                    headerResolutionSource = hasExplicitHeader
                        ? "explicit user-picked header pipe"
                        : "provided fabrication calculation context";
                }
                else if (!hasExplicitHeader)
                {
                    header = networkPipeCandidates
                        .Where(x =>
                            x?.Dimensions != null &&
                            x.Dimensions.OutsideDiameter >
                                maximumConnectorDiameter +
                                DiameterTolerance)
                        .OrderByDescending(x =>
                            x.Dimensions.OutsideDiameter)
                        .FirstOrDefault();

                    if (header != null)
                    {
                        headerResolutionSource =
                            "provided connected fabrication context";
                    }
                }

                if (hasExplicitHeader &&
                    (header == null ||
                     header.Pipe == null ||
                     header.Dimensions == null))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The pipe selected as this shaped branch's header " +
                            "does not physically intersect the branch axis at " +
                            "the fitting. Select the correct main/header pipe."
                    });

                    continue;
                }

                // No explicit or provided header resolved. First traverse the
                // connected network, then run one bounded nearby-pipe search.
                // This avoids a project-wide pipe scan while still supporting
                // adjustable branch families with an unconnected header side.
                if (header == null ||
                    header.Pipe == null ||
                    header.Dimensions == null)
                {
                    List<ShapedBranchPipeCandidate>
                        automaticNetworkCandidates =
                            new List<ShapedBranchPipeCandidate>();

                    foreach (Connector connector in connectors)
                    {
                        Pipe connectedPipe;
                        PipeDimensions connectedDimensions;

                        if (!TryFindNearestPipeOnConnectorSide(
                                doc,
                                fitting,
                                connector,
                                knownPipeDimensions,
                                calculationContextIds,
                                true,
                                out connectedPipe,
                                out connectedDimensions))
                        {
                            continue;
                        }

                        AddShapedBranchPipeCandidate(
                            automaticNetworkCandidates,
                            connector,
                            connectedPipe,
                            connectedDimensions);

                        AddShapedBranchPipeCandidate(
                            networkPipeCandidates,
                            connector,
                            connectedPipe,
                            connectedDimensions);
                    }

                    List<Pipe> nearbyPipes =
                        CollectNearbyShapedBranchHeaderPipes(
                            doc,
                            fitting,
                            connectors,
                            knownPipeDimensions);

                    foreach (ShapedBranchPipeCandidate candidate in
                             automaticNetworkCandidates)
                    {
                        if (candidate?.Pipe != null &&
                            !nearbyPipes.Any(x =>
                                x.Id.Equals(candidate.Pipe.Id)))
                        {
                            nearbyPipes.Add(candidate.Pipe);
                        }
                    }

                    spatialHeaderResolved =
                        TryResolveSpatialShapedBranchHeader(
                            fitting,
                            connectors,
                            nearbyPipes,
                            knownPipeDimensions,
                            out spatialHeader);

                    if (!spatialHeaderResolved &&
                        spatialHeader != null &&
                        spatialHeader.IsAmbiguous)
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = fitting.Id,
                            ElementName =
                                GetElementDisplayName(fitting),
                            Message =
                                "Multiple similarly valid main/header pipes " +
                                "were found near the selected shaped branch. " +
                                "Run Fabrication STEP again and explicitly " +
                                "pick the intended header pipe."
                        });

                        continue;
                    }

                    if (spatialHeaderResolved)
                    {
                        header = networkPipeCandidates.FirstOrDefault(x =>
                            x.Pipe.Id.Equals(spatialHeader.Pipe.Id));

                        if (header == null)
                        {
                            header = new ShapedBranchPipeCandidate
                            {
                                Connector = spatialHeader.MatchConnector,
                                Pipe = spatialHeader.Pipe,
                                Dimensions = spatialHeader.Dimensions
                            };
                        }

                        headerResolutionSource =
                            "automatic bounded model search";
                    }
                    else
                    {
                        header = automaticNetworkCandidates
                            .Where(x =>
                                x?.Dimensions != null &&
                                x.Dimensions.OutsideDiameter >
                                    maximumConnectorDiameter +
                                    DiameterTolerance)
                            .OrderByDescending(x =>
                                x.Dimensions.OutsideDiameter)
                            .FirstOrDefault();

                        if (header != null)
                        {
                            headerResolutionSource =
                                "automatic connected-network search";
                        }
                    }
                }

                if (header == null ||
                    header.Pipe == null ||
                    header.Dimensions == null)
                {
                    ShapedBranchConnection standaloneConnection;
                    bool usedStandaloneStandardFallback;
                    string standaloneError;

                    if (TryResolveStandaloneShapedBranchConnection(
                            doc,
                            fitting,
                            connectors,
                            networkPipeCandidates,
                            knownPipeDimensions,
                            selectedSourceIds,
                            out standaloneConnection,
                            out usedStandaloneStandardFallback,
                            out standaloneError))
                    {
                        result[fitting.Id] =
                            standaloneConnection;

                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Information,
                            ElementId = fitting.Id,
                            ElementName =
                                GetElementDisplayName(fitting),
                            Message =
                                "No explicit, connected, or bounded nearby " +
                                "header pipe was resolved. The shaped branch " +
                                "was therefore exported as a standalone plain " +
                                "hollow component with a straight-through bore; " +
                                "no SET-ON saddle, saddle bevel, or header-pipe " +
                                "opening was generated."
                        });

                        if (usedStandaloneStandardFallback)
                        {
                            issues.Add(new FabricationIssue
                            {
                                Severity =
                                    FabricationIssueSeverity.Information,
                                ElementId = fitting.Id,
                                ElementName =
                                    GetElementDisplayName(fitting),
                                Message =
                                    "Standalone shaped-branch outlet " +
                                    "dimensions were resolved from the explicit " +
                                    "STD WT-CS family classification, nominal " +
                                    "size, and the controlled standard-weight " +
                                    "carbon-steel dimension table."
                            });
                        }

                        continue;
                    }

                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped branch could not be generated either " +
                            "as a SET-ON assembly component or as a standalone " +
                            "component. " +
                            (string.IsNullOrWhiteSpace(standaloneError)
                                ? "The outlet connector or outlet dimensions " +
                                  "could not be resolved."
                                : standaloneError)
                    });

                    continue;
                }

                if (!calculationContextIds.Contains(
                        header.Pipe.Id))
                {
                    string freshnessError;

                    if (!TryVerifyCalculationContextIsCurrent(
                            doc,
                            header.Pipe,
                            out freshnessError))
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = fitting.Id,
                            ElementName =
                                GetElementDisplayName(fitting),
                            Message = freshnessError
                        });

                        continue;
                    }

                    calculationContextIds.Add(
                        header.Pipe.Id);
                }

                XYZ headerAxisStart;
                XYZ headerAxisEnd;
                XYZ headerAxisDirection;
                double headerAxisLength;

                if (!TryGetStraightPipeAxis(
                        header.Pipe,
                        out headerAxisStart,
                        out headerAxisEnd,
                        out headerAxisDirection,
                        out headerAxisLength))
                {
                    bool hasLinearEndpoints =
                        headerAxisStart != null &&
                        headerAxisEnd != null;

                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message = hasLinearEndpoints
                            ? "The shaped-branch header pipe has zero or invalid length."
                            : "SET-ON shaped branches currently require a straight rigid header pipe."
                    });

                    continue;
                }

                XYZ headerSurfaceOrigin;
                XYZ headerInwardDirection;
                Connector headerMatchConnector = null;

                if (spatialHeaderResolved &&
                    spatialHeader.Pipe.Id.Equals(header.Pipe.Id))
                {
                    headerSurfaceOrigin =
                        spatialHeader.SurfaceOrigin;

                    headerInwardDirection =
                        spatialHeader.InwardDirection;

                    headerMatchConnector =
                        spatialHeader.MatchConnector;

                    if (headerMatchConnector == null &&
                        connectors.Count > 1)
                    {
                        // Some adjustable branch families expose a physical
                        // header connector but leave it unconnected and offset
                        // from the exact saddle face. With two or more
                        // connectors, the connector nearest the resolved header
                        // surface is the header-side connector; the farther one
                        // remains the branch outlet.
                        headerMatchConnector = connectors
                            .OrderBy(x =>
                                x.Origin.DistanceTo(
                                    headerSurfaceOrigin))
                            .FirstOrDefault();
                    }

                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Information,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped-branch header was resolved from the " +
                            "physical branch-axis intersection with header " +
                            "pipe " +
                            RevitApiCompatibility.GetElementIdValue(
                                header.Pipe.Id).ToString(
                                    CultureInfo.InvariantCulture) +
                            " using " +
                            (headerResolutionSource ??
                             "fabrication calculation context") +
                            ". This supports families whose header connector " +
                            "is unconnected or omitted."
                    });
                }
                else
                {
                    if (!TryResolveHeaderAttachmentFromConnectedPipe(
                            fitting,
                            header,
                            headerAxisStart,
                            headerAxisDirection,
                            headerAxisLength,
                            out headerSurfaceOrigin,
                            out headerInwardDirection,
                            out headerMatchConnector))
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity = FabricationIssueSeverity.Blocking,
                            ElementId = fitting.Id,
                            ElementName = GetElementDisplayName(fitting),
                            Message =
                                "The shaped-branch header attachment point " +
                                "could not be resolved from the connected " +
                                "header pipe and fitting connector."
                        });

                        continue;
                    }
                }

                Connector outletConnector =
                    ResolveShapedBranchOutletConnector(
                        connectors,
                        headerMatchConnector,
                        headerSurfaceOrigin,
                        header.Pipe.Id,
                        networkPipeCandidates);

                if (outletConnector == null)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped-branch outlet connector could not be " +
                            "identified after the header attachment was resolved."
                    });

                    continue;
                }

                ShapedBranchPipeCandidate branchPipeCandidate =
                    networkPipeCandidates
                        .Where(x =>
                            !x.Pipe.Id.Equals(header.Pipe.Id))
                        .OrderBy(x =>
                            x.Connector == null
                                ? double.MaxValue
                                : x.Connector.Origin.DistanceTo(
                                    outletConnector.Origin))
                        .FirstOrDefault();

                PipeDimensions branchDimensions = null;
                ElementId branchPipeId =
                    ElementId.InvalidElementId;

                string branchDimensionSource = null;
                bool usedControlledStandardFallback = false;

                if (branchPipeCandidate != null)
                {
                    branchDimensions =
                        branchPipeCandidate.Dimensions;

                    branchPipeId =
                        branchPipeCandidate.Pipe.Id;

                    branchDimensionSource =
                        "selected branch pipe " +
                        RevitApiCompatibility.GetElementIdValue(
                            branchPipeId).ToString(
                                CultureInfo.InvariantCulture);
                }
                else if (!TryResolveShapedBranchOutletDimensions(
                             doc,
                             fitting,
                             outletConnector,
                             knownPipeDimensions,
                             selectedSourceIds,
                             out branchDimensions,
                             out branchPipeId,
                             out branchDimensionSource,
                             out usedControlledStandardFallback))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped-branch outlet dimensions could not be " +
                            "resolved. A physical branch pipe is not required, " +
                            "but the plugin needs valid branch ID/OD data from " +
                            "a connected pipe, a connected fitting, shaped-branch " +
                            "parameters, physical family geometry, or an explicit " +
                            "STD WT-CS family classification."
                    });

                    continue;
                }

                if (branchDimensions == null ||
                    branchDimensions.InsideDiameter <= GeometryTolerance ||
                    branchDimensions.OutsideDiameter <=
                        branchDimensions.InsideDiameter + GeometryTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The resolved shaped-branch outlet dimensions are " +
                            "invalid. ID must be greater than zero and smaller " +
                            "than OD."
                    });

                    continue;
                }

                if (branchDimensions.InsideDiameter >=
                    header.Dimensions.InsideDiameter - DiameterTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The shaped-branch outlet bore must be smaller than " +
                            "the large header-pipe bore for the SET-ON rule."
                    });

                    continue;
                }

                Element branchConnectedElement =
                    GetConnectedElement(
                        fitting,
                        outletConnector,
                        selectedSourceIds);

                result[fitting.Id] =
                    new ShapedBranchConnection
                    {
                        FittingId = fitting.Id,
                        FittingName =
                            GetElementDisplayName(fitting),

                        HeaderPipeId = header.Pipe.Id,
                        BranchPipeId = branchPipeId,
                        BranchConnectedElementId =
                            branchConnectedElement?.Id ??
                            ElementId.InvalidElementId,

                        HeaderDimensions =
                            header.Dimensions,

                        BranchDimensions =
                            branchDimensions,

                        HeaderConnectorOrigin =
                            headerSurfaceOrigin,

                        HeaderConnectorMatchOrigin =
                            headerMatchConnector?.Origin,

                        OutletConnectorOrigin =
                            outletConnector.Origin,

                        // Preserve the actual branch outlet axis. Using a line
                        // from the outlet origin to an estimated header point
                        // can tilt the bore cutter and create a capsule-shaped
                        // outlet opening. GetConnectorOutwardDirection points
                        // toward the connected branch-side element; negate it
                        // to obtain the direction into the fitting/header.
                        OutletInwardDirection =
                            -GetConnectorOutwardDirection(
                                fitting,
                                outletConnector,
                                branchConnectedElement)
                                .Normalize(),

                        HeaderInwardDirection =
                            headerInwardDirection.Normalize(),

                        HeaderAxisStart =
                            headerAxisStart,

                        HeaderAxisDirection =
                            headerAxisDirection,

                        HeaderAxisLength =
                            headerAxisLength,

                        BranchDimensionSource =
                            branchDimensionSource,

                        HeaderPipeIsCalculationContextOnly =
                            !selectedSourceIds.Contains(
                                header.Pipe.Id),

                        HeaderResolutionSource =
                            headerResolutionSource
                    };

                if (!selectedSourceIds.Contains(
                        header.Pipe.Id))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Information,
                        ElementId = fitting.Id,
                        ElementName =
                            GetElementDisplayName(fitting),
                        Message =
                            "Header pipe " +
                            RevitApiCompatibility.GetElementIdValue(
                                header.Pipe.Id).ToString(
                                    CultureInfo.InvariantCulture) +
                            " was used as read-only calculation context for " +
                            "the SET-ON saddle, bore, and external bevel. " +
                            "Only the selected shaped branch will be included " +
                            "in the STEP file."
                    });
                }

                if (usedControlledStandardFallback)
                {
                    // This path is deterministic rather than an unresolved
                    // geometry condition. It runs only when:
                    // - the family explicitly identifies itself as STD WT-CS;
                    // - a valid nominal/OD match exists in the controlled
                    //   standard-weight carbon-steel dimension table; and
                    // - the resulting OD, ID, and wall thickness are valid.
                    //
                    // Keep the audit evidence, but do not report it as a
                    // warning because it does not require corrective action
                    // and does not change the generated geometry.
                    issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Information,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "Shaped-branch outlet dimensions were resolved " +
                            "from the explicit STD WT-CS family classification, " +
                            "nominal size, and the controlled standard-weight " +
                            "carbon-steel dimension table because no branch " +
                            "pipe was included in the selected assembly."
                    });
                }
            }

            return result;
        }

        private static bool
            TryResolveStandaloneShapedBranchConnection(
                Document doc,
                Element fitting,
                IList<Connector> connectors,
                IList<ShapedBranchPipeCandidate>
                    networkPipeCandidates,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions,
                ISet<ElementId> selectedSourceIds,
                out ShapedBranchConnection connection,
                out bool usedControlledStandardFallback,
                out string error)
        {
            connection = null;
            usedControlledStandardFallback = false;
            error = null;

            if (fitting == null ||
                connectors == null ||
                connectors.Count == 0)
            {
                error =
                    "The standalone shaped branch has no usable round " +
                    "piping connector.";

                return false;
            }

            Connector outletConnector = null;

            ShapedBranchPipeCandidate selectedBranchCandidate =
                networkPipeCandidates?
                    .Where(x =>
                        x != null &&
                        x.Pipe != null &&
                        x.Connector != null)
                    .OrderBy(x =>
                        x.Dimensions == null
                            ? double.MaxValue
                            : x.Dimensions.OutsideDiameter)
                    .FirstOrDefault();

            if (selectedBranchCandidate != null)
            {
                outletConnector =
                    selectedBranchCandidate.Connector;
            }
            else if (connectors.Count == 1)
            {
                outletConnector = connectors[0];
            }
            else
            {
                XYZ fittingCenter =
                    GetElementCenter(fitting);

                outletConnector = connectors
                    .OrderByDescending(x =>
                        fittingCenter == null
                            ? 0.0
                            : x.Origin.DistanceTo(
                                fittingCenter))
                    .FirstOrDefault();
            }

            if (outletConnector == null)
            {
                error =
                    "The standalone shaped-branch outlet connector could " +
                    "not be identified.";

                return false;
            }

            PipeDimensions branchDimensions;
            ElementId branchPipeId;
            string branchDimensionSource;

            if (selectedBranchCandidate != null &&
                selectedBranchCandidate.Dimensions != null)
            {
                branchDimensions =
                    selectedBranchCandidate.Dimensions;

                branchPipeId =
                    selectedBranchCandidate.Pipe.Id;

                branchDimensionSource =
                    "selected branch pipe " +
                    RevitApiCompatibility.GetElementIdValue(
                        branchPipeId).ToString(
                            CultureInfo.InvariantCulture);
            }
            else if (!TryResolveShapedBranchOutletDimensions(
                         doc,
                         fitting,
                         outletConnector,
                         knownPipeDimensions,
                         selectedSourceIds,
                         out branchDimensions,
                         out branchPipeId,
                         out branchDimensionSource,
                         out usedControlledStandardFallback))
            {
                error =
                    "The standalone shaped-branch outlet dimensions could " +
                    "not be resolved from a connected component, family " +
                    "parameters, physical family geometry, or the explicit " +
                    "STD WT-CS dimension table.";

                return false;
            }

            if (branchDimensions == null ||
                branchDimensions.InsideDiameter <=
                    GeometryTolerance ||
                branchDimensions.OutsideDiameter <=
                    branchDimensions.InsideDiameter +
                    GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch outlet dimensions are " +
                    "invalid. ID must be greater than zero and smaller " +
                    "than OD.";

                return false;
            }

            Element connectedElement =
                GetConnectedElement(
                    fitting,
                    outletConnector,
                    selectedSourceIds);

            XYZ outletInward =
                -GetConnectorOutwardDirection(
                    fitting,
                    outletConnector,
                    connectedElement)
                    .Normalize();

            XYZ fittingCenterPoint =
                GetElementCenter(fitting);

            if (fittingCenterPoint != null)
            {
                XYZ outletToCenter =
                    fittingCenterPoint -
                    outletConnector.Origin;

                if (outletToCenter.GetLength() >
                        GeometryTolerance &&
                    outletInward.DotProduct(
                        outletToCenter) < 0)
                {
                    outletInward = -outletInward;
                }
            }

            Connector saddleConnector = connectors
                .Where(x =>
                    !ConnectorOriginsMatch(
                        x.Origin,
                        outletConnector.Origin))
                .Select(x => new
                {
                    Connector = x,
                    Projection =
                        (x.Origin - outletConnector.Origin)
                        .DotProduct(outletInward)
                })
                .Where(x =>
                    x.Projection > GeometryTolerance)
                .OrderByDescending(x => x.Projection)
                .Select(x => x.Connector)
                .FirstOrDefault();

            double fittingExtent =
                GetElementExtent(fitting);

            XYZ saddleOrigin =
                saddleConnector?.Origin ??
                (outletConnector.Origin +
                 (outletInward * fittingExtent));

            connection =
                new ShapedBranchConnection
                {
                    FittingId = fitting.Id,
                    FittingName =
                        GetElementDisplayName(fitting),
                    HeaderPipeId =
                        ElementId.InvalidElementId,
                    BranchPipeId = branchPipeId,
                    BranchConnectedElementId =
                        connectedElement?.Id ??
                        ElementId.InvalidElementId,
                    HeaderDimensions = null,
                    BranchDimensions = branchDimensions,
                    HeaderConnectorOrigin =
                        saddleOrigin,
                    HeaderConnectorMatchOrigin =
                        saddleConnector?.Origin,
                    OutletConnectorOrigin =
                        outletConnector.Origin,
                    OutletInwardDirection =
                        outletInward,
                    HeaderInwardDirection =
                        outletInward,
                    HeaderAxisStart = null,
                    HeaderAxisDirection = null,
                    HeaderAxisLength = 0.0,
                    BranchDimensionSource =
                        branchDimensionSource,
                    IsStandaloneComponent = true
                };

            return true;
        }

        private static bool
            TryVerifyCalculationContextIsCurrent(
                Document doc,
                Element contextElement,
                out string error)
        {
            error = null;

            if (doc == null || contextElement == null)
            {
                error =
                    "The automatically resolved header pipe is no longer " +
                    "available.";

                return false;
            }

            if (!doc.IsWorkshared)
                return true;

            try
            {
                ModelUpdatesStatus status =
                    WorksharingUtils.GetModelUpdatesStatus(
                        doc,
                        contextElement.Id);

                if (status ==
                    ModelUpdatesStatus.UpdatedInCentral)
                {
                    error =
                        "The automatically resolved header pipe has newer " +
                        "changes in the central model. Run Reload Latest " +
                        "before generating the shaped branch.";

                    return false;
                }

                if (status ==
                    ModelUpdatesStatus.DeletedInCentral)
                {
                    error =
                        "The automatically resolved header pipe was deleted " +
                        "in the central model. Run Reload Latest before " +
                        "generating the shaped branch.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The plugin could not verify the automatically resolved " +
                    "header pipe against the central model: " + ex.Message;

                return false;
            }
        }

        private static void AddShapedBranchPipeCandidate(
            IList<ShapedBranchPipeCandidate> candidates,
            Connector connector,
            Pipe pipe,
            PipeDimensions dimensions)
        {
            if (candidates == null ||
                pipe == null ||
                dimensions == null ||
                candidates.Any(x =>
                    x?.Pipe != null &&
                    x.Pipe.Id.Equals(pipe.Id)))
            {
                return;
            }

            candidates.Add(
                new ShapedBranchPipeCandidate
                {
                    Connector = connector,
                    Pipe = pipe,
                    Dimensions = dimensions
                });
        }

        private static List<Pipe>
            CollectNearbyShapedBranchHeaderPipes(
                Document doc,
                Element fitting,
                IList<Connector> connectors,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions)
        {
            List<Pipe> result = new List<Pipe>();

            if (doc == null || fitting == null)
                return result;

            Outline searchOutline =
                BuildFabricationDimensionSearchOutline(
                    new[] { fitting });

            if (searchOutline == null)
                return result;

            double branchNominal =
                (connectors ?? new List<Connector>())
                    .Select(x =>
                    {
                        try
                        {
                            return x?.Shape ==
                                       ConnectorProfileType.Round
                                ? x.Radius * 2.0
                                : 0.0;
                        }
                        catch
                        {
                            return 0.0;
                        }
                    })
                    .DefaultIfEmpty(0.0)
                    .Max();

            XYZ fittingCenter =
                GetElementCenter(fitting);

            List<Pipe> boundedCandidates =
                new FilteredElementCollector(doc)
                    .OfCategory(
                        BuiltInCategory.OST_PipeCurves)
                    .WhereElementIsNotElementType()
                    .WherePasses(
                        new BoundingBoxIntersectsFilter(
                            searchOutline))
                    .Cast<Pipe>()
                    .Where(x => x != null)
                    .Where(x =>
                    {
                        double rawNominal =
                            GetPositiveDouble(
                                x.get_Parameter(
                                    BuiltInParameter
                                        .RBS_PIPE_DIAMETER_PARAM));

                        return branchNominal <=
                                   GeometryTolerance ||
                               rawNominal >
                                   branchNominal +
                                   DiameterTolerance;
                    })
                    .OrderBy(x =>
                        fittingCenter == null
                            ? 0.0
                            : DistancePointToPipeAxis(
                                fittingCenter,
                                x))
                    .Take(150)
                    .ToList();

            foreach (Pipe pipe in boundedCandidates)
            {
                XYZ axisStart;
                XYZ axisEnd;
                XYZ axisDirection;
                double axisLength;

                if (!TryGetStraightPipeAxis(
                        pipe,
                        out axisStart,
                        out axisEnd,
                        out axisDirection,
                        out axisLength))
                {
                    continue;
                }

                PipeDimensions dimensions;

                if (!knownPipeDimensions.TryGetValue(
                        pipe.Id,
                        out dimensions))
                {
                    string ignoredError;

                    if (!TryResolvePipeDimensions(
                            doc,
                            pipe,
                            out dimensions,
                            out ignoredError))
                    {
                        continue;
                    }

                    knownPipeDimensions[pipe.Id] =
                        dimensions;
                }

                if (dimensions == null ||
                    (branchNominal > GeometryTolerance &&
                     dimensions.OutsideDiameter <=
                         branchNominal +
                         DiameterTolerance))
                {
                    continue;
                }

                result.Add(pipe);
            }

            return result;
        }

        private static bool
            TryFindNearestPipeOnConnectorSide(
                Document doc,
                Element sourceElement,
                Connector sourceConnector,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions,
                ISet<ElementId> allowedPipeIds,
                bool allowAnyModelPipe,
                out Pipe pipe,
                out PipeDimensions dimensions)
        {
            pipe = null;
            dimensions = null;

            if (sourceElement == null ||
                sourceConnector == null ||
                (!allowAnyModelPipe &&
                 allowedPipeIds == null))
            {
                return false;
            }

            Queue<Tuple<Element, int>> queue =
                new Queue<Tuple<Element, int>>();

            HashSet<ElementId> visited =
                new HashSet<ElementId>
                {
                    sourceElement.Id
                };

            foreach (Connector reference in
                     sourceConnector.AllRefs)
            {
                Element candidate = reference?.Owner;

                if (candidate == null ||
                    candidate.Id.Equals(sourceElement.Id) ||
                    reference.Domain != Domain.DomainPiping)
                {
                    continue;
                }

                queue.Enqueue(
                    Tuple.Create(candidate, 0));
            }

            const int maximumDepth = 12;
            int? nearestDepth = null;

            List<Tuple<Pipe, PipeDimensions>> candidates =
                new List<Tuple<Pipe, PipeDimensions>>();

            while (queue.Count > 0)
            {
                Tuple<Element, int> node = queue.Dequeue();
                Element current = node.Item1;
                int depth = node.Item2;

                if (current == null ||
                    visited.Contains(current.Id) ||
                    depth > maximumDepth ||
                    (nearestDepth.HasValue &&
                     depth > nearestDepth.Value))
                {
                    continue;
                }

                visited.Add(current.Id);

                Pipe currentPipe = current as Pipe;

                if (currentPipe != null)
                {
                    if (!allowAnyModelPipe &&
                        !allowedPipeIds.Contains(
                            currentPipe.Id))
                    {
                        continue;
                    }

                    PipeDimensions currentDimensions;

                    if (!knownPipeDimensions.TryGetValue(
                            currentPipe.Id,
                            out currentDimensions))
                    {
                        string ignoredError;

                        if (!TryResolvePipeDimensions(
                                doc,
                                currentPipe,
                                out currentDimensions,
                                out ignoredError))
                        {
                            continue;
                        }

                        knownPipeDimensions[
                            currentPipe.Id] =
                            currentDimensions;
                    }

                    if (currentDimensions == null)
                        continue;

                    if (!nearestDepth.HasValue)
                        nearestDepth = depth;

                    if (depth == nearestDepth.Value)
                    {
                        candidates.Add(
                            Tuple.Create(
                                currentPipe,
                                currentDimensions));
                    }

                    continue;
                }

                if (!IsPipingNetworkElement(current))
                    continue;

                ConnectorManager manager =
                    GetConnectorManager(current);

                if (manager == null)
                    continue;

                foreach (Connector connector in
                         manager.Connectors)
                {
                    if (connector == null ||
                        connector.Domain != Domain.DomainPiping)
                    {
                        continue;
                    }

                    foreach (Connector reference in
                             connector.AllRefs)
                    {
                        Element candidate = reference?.Owner;

                        if (candidate == null ||
                            candidate.Id.Equals(current.Id) ||
                            candidate.Id.Equals(sourceElement.Id) ||
                            reference.Domain != Domain.DomainPiping ||
                            visited.Contains(candidate.Id))
                        {
                            continue;
                        }

                        queue.Enqueue(
                            Tuple.Create(
                                candidate,
                                depth + 1));
                    }
                }
            }

            if (candidates.Count == 0)
                return false;

            Tuple<Pipe, PipeDimensions> selected =
                candidates
                    .OrderBy(x =>
                        DistancePointToPipeAxis(
                            sourceConnector.Origin,
                            x.Item1))
                    .First();

            pipe = selected.Item1;
            dimensions = selected.Item2;
            return true;
        }

        private static double DistancePointToPipeAxis(
            XYZ point,
            Pipe pipe)
        {
            XYZ start;
            XYZ end;
            XYZ direction;
            double length;

            if (point == null ||
                !TryGetStraightPipeAxis(
                    pipe,
                    out start,
                    out end,
                    out direction,
                    out length))
            {
                return double.MaxValue;
            }

            return DistancePointToInfiniteLine(
                point,
                start,
                direction);
        }

        private static bool
            TryResolveSpatialShapedBranchHeader(
                Element fitting,
                IList<Connector> connectors,
                IList<Pipe> selectedPipes,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions,
                out ShapedBranchSpatialHeader resolved)
        {
            resolved = null;

            if (fitting == null ||
                connectors == null ||
                selectedPipes == null)
            {
                return false;
            }

            double bestScore = double.MaxValue;

            Dictionary<ElementId, double> bestScoreByPipe =
                new Dictionary<ElementId, double>();

            double fittingExtent =
                GetElementExtent(fitting);

            List<Tuple<Connector, XYZ>> connectorAxes =
                new List<Tuple<Connector, XYZ>>();

            foreach (Connector connector in connectors)
            {
                XYZ connectorAxis =
                    GetRawConnectorAxisDirection(
                        fitting,
                        connector);

                if (connectorAxis == null ||
                    connectorAxis.GetLength() <=
                    GeometryTolerance)
                {
                    continue;
                }

                connectorAxes.Add(
                    Tuple.Create(
                        connector,
                        connectorAxis.Normalize()));
            }

            foreach (Pipe pipe in selectedPipes)
            {
                PipeDimensions dimensions;

                if (!knownPipeDimensions.TryGetValue(
                        pipe.Id,
                        out dimensions) ||
                    dimensions == null)
                {
                    continue;
                }

                XYZ axisStart;
                XYZ axisEnd;
                XYZ axisDirection;
                double axisLength;

                if (!TryGetStraightPipeAxis(
                        pipe,
                        out axisStart,
                        out axisEnd,
                        out axisDirection,
                        out axisLength))
                {
                    continue;
                }

                foreach (Tuple<Connector, XYZ> connectorAxisEntry in
                         connectorAxes)
                {
                    Connector connector =
                        connectorAxisEntry.Item1;

                    XYZ connectorAxis =
                        connectorAxisEntry.Item2;

                    XYZ[] directions =
                    {
                        connectorAxis,
                        -connectorAxis
                    };

                    foreach (XYZ direction in directions)
                    {
                        // A branch axis should cross the header, not run along
                        // the header centreline.
                        if (Math.Abs(
                                direction.DotProduct(
                                    axisDirection)) >= 0.98)
                        {
                            continue;
                        }

                        double near;
                        double far;

                        if (!TryGetLineCylinderIntersections(
                                connector.Origin,
                                direction,
                                axisStart,
                                axisDirection,
                                dimensions.OutsideDiameter / 2.0,
                                out near,
                                out far))
                        {
                            continue;
                        }

                        double intersectionDistance;

                        if (!TryGetIntersectionAtOrAfter(
                                near,
                                far,
                                0.0,
                                out intersectionDistance))
                        {
                            continue;
                        }

                        XYZ surfaceOrigin =
                            connector.Origin +
                            (direction *
                             intersectionDistance);

                        double projection =
                            (surfaceOrigin - axisStart)
                                .DotProduct(axisDirection);

                        double endTolerance = Math.Max(
                            2.0 / FeetToMillimetres,
                            dimensions.OutsideDiameter * 0.02);

                        if (projection < -endTolerance ||
                            projection >
                            axisLength + endTolerance)
                        {
                            continue;
                        }

                        // Prefer the nearest valid physical intersection.
                        // A tiny OD-based tiebreaker favours the larger header
                        // when two selected pipes are nearly coincident.
                        double score =
                            Math.Max(0.0, intersectionDistance) -
                            (dimensions.OutsideDiameter * 0.001);

                        double existingPipeScore;

                        if (!bestScoreByPipe.TryGetValue(
                                pipe.Id,
                                out existingPipeScore) ||
                            score < existingPipeScore)
                        {
                            bestScoreByPipe[pipe.Id] = score;
                        }

                        if (score >= bestScore)
                            continue;

                        double connectorToSurface =
                            connector.Origin.DistanceTo(
                                surfaceOrigin);

                        double connectorMatchTolerance =
                            Math.Max(
                                5.0 / FeetToMillimetres,
                                Math.Min(
                                    fittingExtent * 0.20,
                                    dimensions.OutsideDiameter * 0.25));

                        bestScore = score;

                        resolved =
                            new ShapedBranchSpatialHeader
                            {
                                Pipe = pipe,
                                Dimensions = dimensions,
                                SurfaceOrigin = surfaceOrigin,
                                InwardDirection = direction.Normalize(),
                                MatchConnector =
                                    connectorToSurface <=
                                        connectorMatchTolerance
                                        ? connector
                                        : null
                            };
                    }
                }

                // Family connector axes are not always authored correctly.
                // As a secondary spatial check, use the radial distance from
                // each connector to the selected pipe centreline. A coaxial
                // branch pipe is rejected because its connector lies on that
                // pipe axis, while a SET-ON header remains radially offset.
                foreach (Connector connector in connectors)
                {
                    double projection =
                        (connector.Origin - axisStart)
                            .DotProduct(axisDirection);

                    double endTolerance = Math.Max(
                        2.0 / FeetToMillimetres,
                        dimensions.OutsideDiameter * 0.02);

                    if (projection < -endTolerance ||
                        projection >
                        axisLength + endTolerance)
                    {
                        continue;
                    }

                    XYZ axisPoint =
                        axisStart +
                        (axisDirection * projection);

                    XYZ connectorToAxis =
                        axisPoint - connector.Origin;

                    double radialDistance =
                        connectorToAxis.GetLength();

                    double outerRadius =
                        dimensions.OutsideDiameter / 2.0;

                    if (radialDistance <=
                        outerRadius * 0.75)
                    {
                        continue;
                    }

                    double surfaceGap =
                        Math.Abs(
                            radialDistance -
                            outerRadius);

                    double maximumGap = Math.Max(
                        fittingExtent * 1.50,
                        dimensions.OutsideDiameter * 2.0);

                    if (surfaceGap > maximumGap)
                        continue;

                    XYZ inwardDirection =
                        connectorToAxis.Normalize();

                    XYZ surfaceOrigin =
                        axisPoint -
                        (inwardDirection *
                         outerRadius);

                    // Add a penalty so a true connector-axis/cylinder
                    // intersection always wins over this radial fallback.
                    double score =
                        surfaceGap +
                        (100.0 / FeetToMillimetres) -
                        (dimensions.OutsideDiameter * 0.001);

                    double existingPipeScore;

                    if (!bestScoreByPipe.TryGetValue(
                            pipe.Id,
                            out existingPipeScore) ||
                        score < existingPipeScore)
                    {
                        bestScoreByPipe[pipe.Id] = score;
                    }

                    if (score >= bestScore)
                        continue;

                    double connectorMatchTolerance =
                        Math.Max(
                            5.0 / FeetToMillimetres,
                            Math.Min(
                                fittingExtent * 0.20,
                                dimensions.OutsideDiameter * 0.25));

                    bestScore = score;

                    resolved =
                        new ShapedBranchSpatialHeader
                        {
                            Pipe = pipe,
                            Dimensions = dimensions,
                            SurfaceOrigin = surfaceOrigin,
                            InwardDirection =
                                inwardDirection,
                            MatchConnector =
                                connector.Origin.DistanceTo(
                                    surfaceOrigin) <=
                                    connectorMatchTolerance
                                    ? connector
                                    : null
                        };
                }
            }

            if (resolved == null)
                return false;

            List<KeyValuePair<ElementId, double>> orderedScores =
                bestScoreByPipe
                    .OrderBy(x => x.Value)
                    .ToList();

            if (orderedScores.Count > 1)
            {
                double ambiguityTolerance = Math.Max(
                    5.0 / FeetToMillimetres,
                    fittingExtent * 0.05);

                if ((orderedScores[1].Value -
                     orderedScores[0].Value) <=
                    ambiguityTolerance)
                {
                    // Do not guess between two similarly valid nearby headers.
                    // The user can rerun and explicitly pick the intended pipe.
                    resolved.IsAmbiguous = true;
                    return false;
                }
            }

            return true;
        }

        private static XYZ GetRawConnectorAxisDirection(
            Element owner,
            Connector connector)
        {
            try
            {
                XYZ basisZ =
                    connector?.CoordinateSystem?.BasisZ;

                if (basisZ != null &&
                    basisZ.GetLength() >
                    GeometryTolerance)
                {
                    return basisZ.Normalize();
                }
            }
            catch
            {
                // Fall back to the owner centre below.
            }

            XYZ ownerCenter =
                GetElementCenter(owner);

            if (ownerCenter == null ||
                connector?.Origin == null)
            {
                return null;
            }

            XYZ direction =
                connector.Origin - ownerCenter;

            return direction.GetLength() >
                GeometryTolerance
                    ? direction.Normalize()
                    : null;
        }

        private static bool
            TryResolveHeaderAttachmentFromConnectedPipe(
                Element fitting,
                ShapedBranchPipeCandidate header,
                XYZ headerAxisStart,
                XYZ headerAxisDirection,
                double headerAxisLength,
                out XYZ headerSurfaceOrigin,
                out XYZ headerInwardDirection,
                out Connector headerMatchConnector)
        {
            headerSurfaceOrigin = null;
            headerInwardDirection = null;
            headerMatchConnector = null;

            if (header?.Pipe == null ||
                header.Connector == null ||
                header.Dimensions == null)
            {
                return false;
            }

            XYZ connectorOrigin =
                header.Connector.Origin;

            double projection =
                (connectorOrigin - headerAxisStart)
                    .DotProduct(headerAxisDirection);

            double endTolerance = Math.Max(
                1.0 / FeetToMillimetres,
                header.Dimensions.OutsideDiameter * 0.01);

            if (projection < -endTolerance ||
                projection >
                headerAxisLength + endTolerance)
            {
                return false;
            }

            XYZ closestAxisPoint =
                headerAxisStart +
                (headerAxisDirection * projection);

            XYZ connectorToAxis =
                closestAxisPoint - connectorOrigin;

            if (connectorToAxis.GetLength() <=
                GeometryTolerance)
            {
                return false;
            }

            XYZ inward =
                GetConnectorOutwardDirection(
                    fitting,
                    header.Connector,
                    header.Pipe)
                .Normalize();

            if (inward.DotProduct(connectorToAxis) < 0)
                inward = -inward;

            if (inward.DotProduct(
                    connectorToAxis.Normalize()) < 0.50)
            {
                inward =
                    connectorToAxis.Normalize();
            }

            headerSurfaceOrigin = connectorOrigin;
            headerInwardDirection = inward;
            headerMatchConnector = header.Connector;
            return true;
        }

        private static Connector
            ResolveShapedBranchOutletConnector(
                IList<Connector> connectors,
                Connector headerMatchConnector,
                XYZ headerSurfaceOrigin,
                ElementId headerPipeId,
                IList<ShapedBranchPipeCandidate>
                    networkPipeCandidates)
        {
            if (connectors == null ||
                connectors.Count == 0)
            {
                return null;
            }

            List<Connector> nonHeaderConnectors = connectors
                .Where(x =>
                    headerMatchConnector == null ||
                    !ConnectorOriginsMatch(
                        x.Origin,
                        headerMatchConnector.Origin))
                .ToList();

            if (nonHeaderConnectors.Count == 0)
                nonHeaderConnectors = connectors.ToList();

            ShapedBranchPipeCandidate branchCandidate =
                networkPipeCandidates?
                    .FirstOrDefault(x =>
                        x.Pipe != null &&
                        !x.Pipe.Id.Equals(headerPipeId) &&
                        x.Connector != null);

            if (branchCandidate != null)
                return branchCandidate.Connector;

            return nonHeaderConnectors
                .OrderByDescending(x =>
                    headerSurfaceOrigin == null
                        ? 0.0
                        : x.Origin.DistanceTo(
                            headerSurfaceOrigin))
                .FirstOrDefault();
        }

        private static bool
            TryResolveShapedBranchOutletDimensions(
                Document doc,
                Element fitting,
                Connector outletConnector,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions,
                ISet<ElementId> selectedSourceIds,
                out PipeDimensions dimensions,
                out ElementId branchPipeId,
                out string sourceDescription,
                out bool usedControlledStandardFallback)
        {
            dimensions = null;
            branchPipeId = ElementId.InvalidElementId;
            sourceDescription = null;
            usedControlledStandardFallback = false;

            if (fitting == null ||
                outletConnector == null)
            {
                return false;
            }

            double connectorNominal =
                outletConnector.Radius * 2.0;

            ElementId networkPipeId;

            if (TryFindPipeDimensionsInConnectedNetwork(
                    doc,
                    fitting,
                    outletConnector,
                    knownPipeDimensions,
                    connectorNominal,
                    null,
                    out dimensions,
                    out networkPipeId))
            {
                branchPipeId = networkPipeId;
                sourceDescription =
                    "connected network pipe " +
                    RevitApiCompatibility.GetElementIdValue(
                        networkPipeId).ToString(
                            CultureInfo.InvariantCulture);

                return true;
            }

            if (TryFindNearestPipeDimensionsInConnectedNetwork(
                    doc,
                    fitting,
                    outletConnector,
                    knownPipeDimensions,
                    out dimensions,
                    out networkPipeId))
            {
                branchPipeId = networkPipeId;
                sourceDescription =
                    "nearest unambiguous connected network pipe " +
                    RevitApiCompatibility.GetElementIdValue(
                        networkPipeId).ToString(
                            CultureInfo.InvariantCulture);

                return true;
            }

            double nominal =
                GetNamedDoubleParameter(
                    doc,
                    fitting,
                    "ND",
                    "Nominal Diameter",
                    "Branch Nominal Diameter",
                    "Outlet Nominal Diameter",
                    "Branch Diameter",
                    "Outlet Diameter");

            if (nominal <= GeometryTolerance)
                nominal = connectorNominal;

            // When no physical branch pipe is available, an explicitly
            // classified STD WT-CS family must use the controlled standard
            // dimensions before attempting to infer a wall from family
            // graphics. Adjustable branch families can contain symbolic,
            // nested, or clearance faces that look cylindrical but do not
            // represent the fabrication wall thickness. Using those faces can
            // produce an unrealistically thin wall and a bevel edge shorter
            // than Revit's ShortCurveTolerance.
            PipeDimensions controlledStandardDimensions;

            if (TryResolveStandardWeightCarbonSteelDimensions(
                    doc,
                    fitting,
                    nominal,
                    out controlledStandardDimensions))
            {
                dimensions =
                    controlledStandardDimensions;

                usedControlledStandardFallback =
                    true;

                sourceDescription =
                    dimensions.SourceDescription;

                return true;
            }

            double inside =
                GetNamedDoubleParameter(
                    doc,
                    fitting,
                    "Branch Inside Diameter",
                    "Outlet Inside Diameter",
                    "Inside Diameter",
                    "Actual Inside Diameter",
                    "Bore Diameter",
                    "Bore",
                    "ID");

            double outside =
                GetNamedDoubleParameter(
                    doc,
                    fitting,
                    "Branch Outside Diameter",
                    "Outlet Outside Diameter",
                    "Outside Diameter",
                    "Actual Outside Diameter",
                    "OD");

            double wall =
                GetNamedDoubleParameter(
                    doc,
                    fitting,
                    "Branch Wall Thickness",
                    "Outlet Wall Thickness",
                    "Wall Thickness",
                    "Pipe Wall Thickness",
                    "WT",
                    "Thickness");

            if (inside <= GeometryTolerance ||
                outside <= GeometryTolerance)
            {
                double inferredInside;
                double inferredOutside;
                double ignoredOffset;

                XYZ probeDirection =
                    GetConnectorOutwardDirection(
                        fitting,
                        outletConnector,
                        GetConnectedElement(
                            fitting,
                            outletConnector,
                            selectedSourceIds));

                if (TryInferConnectorDiametersFromGeometry(
                        GetElementSolids(fitting),
                        outletConnector.Origin,
                        probeDirection,
                        nominal,
                        GetElementExtent(fitting),
                        out inferredInside,
                        out inferredOutside,
                        out ignoredOffset))
                {
                    if (inside <= GeometryTolerance)
                        inside = inferredInside;

                    if (outside <= GeometryTolerance)
                        outside = inferredOutside;
                }
            }

            if (inside <= GeometryTolerance &&
                outside > GeometryTolerance &&
                wall > GeometryTolerance)
            {
                inside = outside - (2.0 * wall);
            }

            if (outside <= GeometryTolerance &&
                inside > GeometryTolerance &&
                wall > GeometryTolerance)
            {
                outside = inside + (2.0 * wall);
            }

            if (wall <= GeometryTolerance &&
                outside >
                    inside + GeometryTolerance &&
                inside > GeometryTolerance)
            {
                wall = (outside - inside) / 2.0;
            }

            if (inside > GeometryTolerance &&
                outside >
                    inside + GeometryTolerance &&
                wall > GeometryTolerance)
            {
                dimensions = new PipeDimensions
                {
                    NominalDiameter =
                        nominal > GeometryTolerance
                            ? nominal
                            : outside,
                    OutsideDiameter = outside,
                    InsideDiameter = inside,
                    WallThickness = wall,
                    SourceDescription =
                        "Shaped-branch family parameters/geometry"
                };

                sourceDescription =
                    dimensions.SourceDescription;

                return true;
            }

            if (TryResolveStandardWeightCarbonSteelDimensions(
                    doc,
                    fitting,
                    nominal,
                    out dimensions))
            {
                usedControlledStandardFallback = true;
                sourceDescription =
                    dimensions.SourceDescription;

                return true;
            }

            return false;
        }

        private static bool
            TryResolveStandardWeightCarbonSteelDimensions(
                Document doc,
                Element fitting,
                double nominalDiameter,
                out PipeDimensions dimensions)
        {
            dimensions = null;

            if (fitting == null ||
                nominalDiameter <= GeometryTolerance)
            {
                return false;
            }

            string classification =
                NormalizeClassificationText(
                    BuildElementClassificationText(
                        doc,
                        fitting));

            bool isStandardWeight =
                classification.Contains("STD WT") ||
                classification.Contains("STDWT") ||
                classification.Contains("STANDARD WT") ||
                classification.Contains("STANDARD WEIGHT");

            bool isCarbonSteel =
                classification.Contains("CARBON") ||
                classification.Contains(" CS ") ||
                classification.EndsWith(
                    " CS",
                    StringComparison.Ordinal);

            if (!isStandardWeight || !isCarbonSteel)
                return false;

            double nominalMillimetres =
                nominalDiameter * FeetToMillimetres;

            // ASME B36.10 standard-weight carbon-steel dimensions. The
            // fallback is intentionally enabled only when the Revit family
            // explicitly identifies itself as STD WT-CS.
            double[,] standardWeight =
            {
                { 15.0, 21.3, 2.77 },
                { 20.0, 26.7, 2.87 },
                { 25.0, 33.4, 3.38 },
                { 32.0, 42.2, 3.56 },
                { 40.0, 48.3, 3.68 },
                { 50.0, 60.3, 3.91 },
                { 65.0, 73.0, 5.16 },
                { 80.0, 88.9, 5.49 },
                { 90.0, 101.6, 5.74 },
                { 100.0, 114.3, 6.02 },
                { 125.0, 141.3, 6.55 },
                { 150.0, 168.3, 7.11 },
                { 200.0, 219.1, 8.18 },
                { 250.0, 273.0, 9.27 },
                { 300.0, 323.9, 9.53 },
                { 350.0, 355.6, 9.53 },
                { 400.0, 406.4, 9.53 },
                { 450.0, 457.0, 9.53 },
                { 500.0, 508.0, 9.53 },
                { 550.0, 559.0, 9.53 },
                { 600.0, 610.0, 9.53 }
            };

            int bestIndex = -1;
            double bestDifference = double.MaxValue;

            for (int index = 0;
                 index < standardWeight.GetLength(0);
                 index++)
            {
                // Revit families are inconsistent: some connector sizes
                // expose DN/NPS nominal size while others expose the physical
                // tube OD. Accept either only inside this explicit STD WT-CS
                // fallback.
                double difference = Math.Min(
                    Math.Abs(
                        standardWeight[index, 0] -
                        nominalMillimetres),
                    Math.Abs(
                        standardWeight[index, 1] -
                        nominalMillimetres));

                if (difference < bestDifference)
                {
                    bestDifference = difference;
                    bestIndex = index;
                }
            }

            double toleranceMillimetres = Math.Max(
                1.5,
                nominalMillimetres * 0.01);

            if (bestIndex < 0 ||
                bestDifference >
                toleranceMillimetres)
            {
                return false;
            }

            double outsideMillimetres =
                standardWeight[bestIndex, 1];

            double wallMillimetres =
                standardWeight[bestIndex, 2];

            double insideMillimetres =
                outsideMillimetres -
                (2.0 * wallMillimetres);

            dimensions = new PipeDimensions
            {
                NominalDiameter =
                    standardWeight[bestIndex, 0] /
                    FeetToMillimetres,
                OutsideDiameter =
                    outsideMillimetres /
                    FeetToMillimetres,
                InsideDiameter =
                    insideMillimetres /
                    FeetToMillimetres,
                WallThickness =
                    wallMillimetres /
                    FeetToMillimetres,
                SourceDescription =
                    "ASME B36.10 STD WT-CS fallback from explicit family classification"
            };

            return true;
        }

        private static bool ConnectorOriginsMatch(
            XYZ first,
            XYZ second)
        {
            if (first == null || second == null)
                return false;

            double tolerance = Math.Max(
                1.0 / FeetToMillimetres,
                GeometryTolerance * 100.0);

            return first.DistanceTo(second) <= tolerance;
        }

        private static Dictionary<ElementId, SideCouplingConnection>
            ResolveSideCouplingConnections(
                Document doc,
                IList<Element> sourceElements,
                IDictionary<ElementId, PipeDimensions> knownPipeDimensions,
                ISet<ElementId> selectedSourceIds,
                IList<FabricationIssue> issues)
        {
            Dictionary<ElementId, SideCouplingConnection> result =
                new Dictionary<ElementId, SideCouplingConnection>();

            foreach (Element fitting in sourceElements.Where(x =>
                         IsSideCouplingLike(doc, x)))
            {
                ConnectorManager manager = GetConnectorManager(fitting);

                if (manager == null)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling has no readable piping connectors."
                    });

                    continue;
                }

                List<ShapedBranchPipeCandidate> candidates =
                    new List<ShapedBranchPipeCandidate>();

                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null ||
                        connector.Domain != Domain.DomainPiping ||
                        connector.ConnectorType != ConnectorType.End ||
                        connector.Shape != ConnectorProfileType.Round)
                    {
                        continue;
                    }

                    Element connectedElement = GetConnectedElement(
                        fitting,
                        connector,
                        selectedSourceIds);

                    Pipe connectedPipe = connectedElement as Pipe;
                    if (connectedPipe == null ||
                        candidates.Any(x =>
                            x.Pipe.Id.Equals(connectedPipe.Id)))
                    {
                        continue;
                    }

                    PipeDimensions dimensions;
                    if (!knownPipeDimensions.TryGetValue(
                            connectedPipe.Id,
                            out dimensions))
                    {
                        continue;
                    }

                    candidates.Add(new ShapedBranchPipeCandidate
                    {
                        Connector = connector,
                        Pipe = connectedPipe,
                        Dimensions = dimensions
                    });
                }

                if (candidates.Count != 2)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "A tap-half coupling must resolve exactly two pipe " +
                            "connections: one large header pipe and one smaller " +
                            "outlet pipe. Resolved pipe connections: " +
                            candidates.Count.ToString(
                                CultureInfo.InvariantCulture) + "."
                    });

                    continue;
                }

                List<ShapedBranchPipeCandidate> ordered = candidates
                    .OrderByDescending(x =>
                        x.Dimensions.OutsideDiameter)
                    .ToList();

                ShapedBranchPipeCandidate header = ordered[0];
                ShapedBranchPipeCandidate outlet = ordered[1];

                double diameterDifference =
                    header.Dimensions.OutsideDiameter -
                    outlet.Dimensions.OutsideDiameter;

                double minimumDifference = Math.Max(
                    DiameterTolerance,
                    header.Dimensions.OutsideDiameter * 0.01);

                if (diameterDifference <= minimumDifference)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling header could not be identified. " +
                            "The two connected pipe outside diameters are equal " +
                            "or too close to classify safely."
                    });

                    continue;
                }

                if (outlet.Dimensions.InsideDiameter >=
                    header.Dimensions.InsideDiameter - DiameterTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling outlet bore must be smaller " +
                            "than the large header-pipe bore."
                    });

                    continue;
                }

                if (!selectedSourceIds.Contains(header.Pipe.Id) ||
                    !selectedSourceIds.Contains(outlet.Pipe.Id))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling, large header pipe, and " +
                            "smaller outlet pipe must all be included in the " +
                            "fabrication selection."
                    });

                    continue;
                }

                XYZ headerAxisStart;
                XYZ headerAxisEnd;
                XYZ headerAxisDirection;
                double headerAxisLength;

                if (!TryGetStraightPipeAxis(
                        header.Pipe,
                        out headerAxisStart,
                        out headerAxisEnd,
                        out headerAxisDirection,
                        out headerAxisLength))
                {
                    bool hasLinearEndpoints =
                        headerAxisStart != null &&
                        headerAxisEnd != null;

                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message = hasLinearEndpoints
                            ? "The tap-half coupling header pipe has zero or invalid length."
                            : "Tap-half coupling side openings currently require a straight rigid header pipe."
                    });

                    continue;
                }

                XYZ connectorOrigin =
                    header.Connector.Origin;

                double axisProjection =
                    (connectorOrigin - headerAxisStart)
                        .DotProduct(headerAxisDirection);

                double endTolerance = Math.Max(
                    1.0 / FeetToMillimetres,
                    header.Dimensions.OutsideDiameter * 0.01);

                if (axisProjection < -endTolerance ||
                    axisProjection >
                    headerAxisLength + endTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling header connector projects " +
                            "outside the physical length of the header pipe."
                    });

                    continue;
                }

                XYZ closestAxisPoint =
                    headerAxisStart +
                    (headerAxisDirection * axisProjection);

                XYZ connectorToAxis =
                    closestAxisPoint - connectorOrigin;

                if (connectorToAxis.GetLength() <= GeometryTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling header connector is located " +
                            "on the header centreline instead of the outer " +
                            "attachment surface."
                    });

                    continue;
                }

                XYZ inwardDirection =
                    GetConnectorOutwardDirection(
                        fitting,
                        header.Connector,
                        header.Pipe)
                    .Normalize();

                if (inwardDirection.DotProduct(connectorToAxis) < 0)
                    inwardDirection = -inwardDirection;

                double directionAlignment =
                    inwardDirection.DotProduct(
                        connectorToAxis.Normalize());

                if (directionAlignment < 0.50)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The tap-half coupling header connector does not " +
                            "point toward the header centreline. Verify the " +
                            "connector direction in the Revit family."
                    });

                    continue;
                }

                result[fitting.Id] =
                    new SideCouplingConnection
                    {
                        FittingId = fitting.Id,
                        FittingName =
                            GetElementDisplayName(fitting),

                        HeaderPipeId = header.Pipe.Id,
                        OutletPipeId = outlet.Pipe.Id,

                        HeaderDimensions =
                            header.Dimensions,

                        OutletDimensions =
                            outlet.Dimensions,

                        HeaderConnectorOrigin =
                            connectorOrigin,

                        HeaderInwardDirection =
                            inwardDirection.Normalize(),

                        HeaderAxisStart =
                            headerAxisStart,

                        HeaderAxisDirection =
                            headerAxisDirection,

                        HeaderAxisLength =
                            headerAxisLength
                    };
            }

            return result;
        }

        private static bool TryFindPipeDimensionsInConnectedNetwork(
            Document doc,
            Element sourceElement,
            Connector sourceConnector,
            IDictionary<ElementId, PipeDimensions> knownPipeDimensions,
            double targetNominalDiameter,
            IDictionary<ElementId, PipeDimensions>
                knownComponentDimensions,
            out PipeDimensions dimensions,
            out ElementId pipeId)
        {
            dimensions = null;
            pipeId = ElementId.InvalidElementId;

            Queue<Tuple<Element, int>> queue =
                new Queue<Tuple<Element, int>>();
            HashSet<ElementId> visited =
                new HashSet<ElementId>
                {
                    sourceElement.Id
                };

            foreach (Connector reference in sourceConnector.AllRefs)
            {
                Element candidate = reference?.Owner;
                if (candidate == null ||
                    candidate.Id.Equals(sourceElement.Id) ||
                    reference.Domain != Domain.DomainPiping)
                {
                    continue;
                }

                queue.Enqueue(Tuple.Create(candidate, 0));
            }

            const int maximumDepth = 12;

            while (queue.Count > 0)
            {
                Tuple<Element, int> node = queue.Dequeue();
                Element current = node.Item1;
                int depth = node.Item2;

                if (current == null ||
                    visited.Contains(current.Id) ||
                    depth > maximumDepth)
                {
                    continue;
                }

                visited.Add(current.Id);

                PipeDimensions componentDimensions;

                if (knownComponentDimensions != null &&
                    knownComponentDimensions.TryGetValue(
                        current.Id,
                        out componentDimensions) &&
                    componentDimensions != null &&
                    ConnectorSizeMatchesPipeDimensions(
                        targetNominalDiameter,
                        componentDimensions))
                {
                    dimensions = componentDimensions;
                    pipeId = current.Id;
                    return true;
                }

                Pipe pipe = current as Pipe;
                if (pipe != null)
                {
                    PipeDimensions candidateDimensions;
                    if (!knownPipeDimensions.TryGetValue(
                            pipe.Id,
                            out candidateDimensions))
                    {
                        string ignoredError;
                        if (!TryResolvePipeDimensions(
                                doc,
                                pipe,
                                out candidateDimensions,
                                out ignoredError))
                        {
                            continue;
                        }
                    }

                    if (candidateDimensions != null &&
                        ConnectorSizeMatchesPipeDimensions(
                            targetNominalDiameter,
                            candidateDimensions))
                    {
                        dimensions = candidateDimensions;
                        pipeId = pipe.Id;
                        return true;
                    }

                    continue;
                }

                if (!IsPipingNetworkElement(current))
                    continue;

                bool currentIsIgnoredConnection =
                    IsIgnoredConnectionElement(doc, current);

                ConnectorManager manager = GetConnectorManager(current);
                if (manager == null)
                    continue;

                foreach (Connector connector in manager.Connectors)
                {
                    if (connector == null ||
                        connector.Domain != Domain.DomainPiping ||
                        connector.Shape != ConnectorProfileType.Round ||
                        (!currentIsIgnoredConnection &&
                         connector.ConnectorType != ConnectorType.End) ||
                        (!currentIsIgnoredConnection &&
                         !NominalDiametersMatch(
                             targetNominalDiameter,
                             connector.Radius * 2.0)))
                    {
                        continue;
                    }

                    foreach (Connector reference in connector.AllRefs)
                    {
                        Element candidate = reference?.Owner;
                        if (candidate == null ||
                            candidate.Id.Equals(current.Id) ||
                            reference.Domain != Domain.DomainPiping ||
                            visited.Contains(candidate.Id))
                        {
                            continue;
                        }

                        queue.Enqueue(
                            Tuple.Create(candidate, depth + 1));
                    }
                }
            }

            return false;
        }

        private static bool
            TryFindNearestPipeDimensionsInConnectedNetwork(
                Document doc,
                Element sourceElement,
                Connector sourceConnector,
                IDictionary<ElementId, PipeDimensions>
                    knownPipeDimensions,
                out PipeDimensions dimensions,
                out ElementId pipeId)
        {
            dimensions = null;
            pipeId = ElementId.InvalidElementId;

            if (sourceElement == null || sourceConnector == null)
                return false;

            Queue<Tuple<Element, int>> queue =
                new Queue<Tuple<Element, int>>();

            HashSet<ElementId> visited =
                new HashSet<ElementId>
                {
                    sourceElement.Id
                };

            foreach (Connector reference in
                     sourceConnector.AllRefs)
            {
                Element candidate = reference?.Owner;

                if (candidate == null ||
                    candidate.Id.Equals(sourceElement.Id) ||
                    reference.Domain != Domain.DomainPiping)
                {
                    continue;
                }

                queue.Enqueue(
                    Tuple.Create(candidate, 0));
            }

            const int maximumDepth = 12;
            int? nearestPipeDepth = null;

            List<Tuple<Pipe, PipeDimensions>> candidates =
                new List<Tuple<Pipe, PipeDimensions>>();

            while (queue.Count > 0)
            {
                Tuple<Element, int> node = queue.Dequeue();
                Element current = node.Item1;
                int depth = node.Item2;

                if (current == null ||
                    visited.Contains(current.Id) ||
                    depth > maximumDepth ||
                    (nearestPipeDepth.HasValue &&
                     depth > nearestPipeDepth.Value))
                {
                    continue;
                }

                visited.Add(current.Id);

                Pipe pipe = current as Pipe;

                if (pipe != null)
                {
                    PipeDimensions candidateDimensions;

                    if (!knownPipeDimensions.TryGetValue(
                            pipe.Id,
                            out candidateDimensions))
                    {
                        string ignoredError;

                        if (!TryResolvePipeDimensions(
                                doc,
                                pipe,
                                out candidateDimensions,
                                out ignoredError))
                        {
                            continue;
                        }
                    }

                    if (candidateDimensions == null)
                        continue;

                    if (!nearestPipeDepth.HasValue)
                        nearestPipeDepth = depth;

                    if (depth == nearestPipeDepth.Value)
                    {
                        candidates.Add(
                            Tuple.Create(
                                pipe,
                                candidateDimensions));
                    }

                    continue;
                }

                if (!IsPipingNetworkElement(current))
                    continue;

                ConnectorManager manager =
                    GetConnectorManager(current);

                if (manager == null)
                    continue;

                foreach (Connector connector in
                         manager.Connectors)
                {
                    if (connector == null ||
                        connector.Domain != Domain.DomainPiping)
                    {
                        continue;
                    }

                    foreach (Connector reference in
                             connector.AllRefs)
                    {
                        Element candidate = reference?.Owner;

                        if (candidate == null ||
                            candidate.Id.Equals(current.Id) ||
                            candidate.Id.Equals(sourceElement.Id) ||
                            reference.Domain != Domain.DomainPiping ||
                            visited.Contains(candidate.Id))
                        {
                            continue;
                        }

                        queue.Enqueue(
                            Tuple.Create(
                                candidate,
                                depth + 1));
                    }
                }
            }

            if (candidates.Count == 0)
                return false;

            PipeDimensions first =
                candidates[0].Item2;

            bool unambiguous = candidates.All(x =>
                ArePipeDimensionsEquivalent(
                    first,
                    x.Item2));

            if (!unambiguous)
                return false;

            dimensions = first;
            pipeId = candidates[0].Item1.Id;
            return true;
        }

        private static bool ConnectorSizeMatchesPipeDimensions(
            double connectorSize,
            PipeDimensions dimensions)
        {
            if (dimensions == null)
                return false;

            return
                NominalDiametersMatch(
                    connectorSize,
                    dimensions.NominalDiameter) ||
                NominalDiametersMatch(
                    connectorSize,
                    dimensions.OutsideDiameter);
        }

        private static bool NominalDiametersMatch(
            double first,
            double second)
        {
            double tolerance = Math.Max(
                0.5 / FeetToMillimetres,
                Math.Max(first, second) * 0.005);

            return Math.Abs(first - second) <= tolerance;
        }

        private static Element GetConnectedElement(
            Element owner,
            Connector connector,
            ISet<ElementId> selectedSourceIds)
        {
            if (owner == null || connector == null)
                return null;

            Element cachedConnectedElement;
            ConnectorLookupCacheKey cacheKey;

            if (TryGetCachedConnectedElement(
                    owner,
                    connector,
                    out cachedConnectedElement,
                    out cacheKey))
            {
                return cachedConnectedElement;
            }

            Queue<Tuple<Element, int>> queue =
                new Queue<Tuple<Element, int>>();

            HashSet<ElementId> visited = new HashSet<ElementId>
            {
                owner.Id
            };

            foreach (Connector reference in connector.AllRefs)
            {
                Element candidate = reference?.Owner;

                if (candidate == null ||
                    candidate.Id.Equals(owner.Id) ||
                    reference.Domain != Domain.DomainPiping)
                {
                    continue;
                }

                queue.Enqueue(Tuple.Create(candidate, 0));
            }

            Element anyMatch = null;
            const int maximumIgnoredConnectionDepth = 8;

            while (queue.Count > 0)
            {
                Tuple<Element, int> node = queue.Dequeue();
                Element current = node.Item1;
                int depth = node.Item2;

                if (current == null || visited.Contains(current.Id))
                    continue;

                visited.Add(current.Id);

                if (!IsPipingNetworkElement(current))
                    continue;

                if (!IsIgnoredConnectionElement(
                        current.Document,
                        current))
                {
                    if (selectedSourceIds != null &&
                        selectedSourceIds.Contains(current.Id))
                    {
                        CacheConnectedElement(
                            owner,
                            cacheKey,
                            current);

                        return current;
                    }

                    if (anyMatch == null)
                        anyMatch = current;

                    // Only weld/non-connector helper elements are transparent.
                    // A normal fitting or pipe is the actual connection target.
                    continue;
                }

                if (depth >= maximumIgnoredConnectionDepth)
                    continue;

                ConnectorManager manager = GetConnectorManager(current);
                if (manager == null)
                    continue;

                foreach (Connector bridgeConnector in manager.Connectors)
                {
                    if (bridgeConnector == null ||
                        bridgeConnector.Domain != Domain.DomainPiping)
                    {
                        continue;
                    }

                    foreach (Connector reference in bridgeConnector.AllRefs)
                    {
                        Element candidate = reference?.Owner;

                        if (candidate == null ||
                            candidate.Id.Equals(current.Id) ||
                            reference.Domain != Domain.DomainPiping ||
                            visited.Contains(candidate.Id))
                        {
                            continue;
                        }

                        queue.Enqueue(
                            Tuple.Create(candidate, depth + 1));
                    }
                }
            }

            CacheConnectedElement(
                owner,
                cacheKey,
                anyMatch);

            return anyMatch;
        }

        private static XYZ GetConnectorOutwardDirection(
            Element owner,
            Connector connector,
            Element connectedElement)
        {
            XYZ connectorOrigin = null;
            XYZ direction = null;

            try
            {
                if (connector != null)
                {
                    connectorOrigin = connector.Origin;

                    Transform coordinateSystem = connector.CoordinateSystem;
                    XYZ basisZ = coordinateSystem?.BasisZ;

                    if (basisZ != null &&
                        basisZ.GetLength() > GeometryTolerance)
                    {
                        direction = basisZ.Normalize();
                    }
                }
            }
            catch
            {
                // Some connector wrappers can become invalid after a Revit
                // regeneration. Fall back to element geometry below instead
                // of terminating the fabrication command.
            }

            XYZ ownerCenter = GetElementCenter(owner);

            if (direction == null &&
                connectorOrigin != null &&
                ownerCenter != null)
            {
                XYZ ownerToConnector = connectorOrigin - ownerCenter;

                if (ownerToConnector.GetLength() > GeometryTolerance)
                    direction = ownerToConnector.Normalize();
            }

            if (direction == null)
                direction = XYZ.BasisZ;

            // An open connector or a connector whose referenced owner was
            // deleted can legitimately have no connected element. Never call
            // into Element.Location unless the element is present and valid.
            XYZ connectedCenter = GetElementCenter(connectedElement);

            if (connectedCenter != null && connectorOrigin != null)
            {
                XYZ connectorToConnected =
                    connectedCenter - connectorOrigin;

                if (connectorToConnected.GetLength() > GeometryTolerance)
                {
                    if (direction.DotProduct(connectorToConnected) < 0)
                        direction = -direction;

                    return direction;
                }
            }

            if (ownerCenter == null || connectorOrigin == null)
                return direction;

            XYZ centerToConnector =
                connectorOrigin - ownerCenter;

            if (centerToConnector.GetLength() > GeometryTolerance &&
                direction.DotProduct(centerToConnector) < 0)
            {
                direction = -direction;
            }

            return direction;
        }

        private static XYZ GetElementCenter(Element element)
        {
            if (element == null)
                return null;

            XYZ cachedCenter;

            if (TryGetCachedElementCenter(
                    element,
                    out cachedCenter))
            {
                return cachedCenter;
            }

            XYZ resolvedCenter = null;

            try
            {
                if (!element.IsValidObject)
                {
                    CacheElementCenter(
                        element,
                        null);

                    return null;
                }
            }
            catch
            {
                return null;
            }

            try
            {
                LocationCurve locationCurve =
                    element.Location as LocationCurve;

                if (locationCurve?.Curve != null)
                {
                    resolvedCenter =
                        locationCurve.Curve.Evaluate(
                            0.5,
                            true);
                }
            }
            catch
            {
                // Continue to the bounding-box/location-point fallbacks.
            }

            if (resolvedCenter == null)
            {
                try
                {
                    // For fittings, the insertion point is not always at the
                    // physical center. The bounding-box center is a safer
                    // reference for connector direction checks.
                    BoundingBoxXYZ box =
                        GetElementBoundingBoxCached(element);

                    if (box != null)
                    {
                        XYZ localCenter =
                            (box.Min + box.Max) / 2.0;

                        Transform transform =
                            box.Transform ?? Transform.Identity;

                        resolvedCenter =
                            transform.OfPoint(localCenter);
                    }
                }
                catch
                {
                    // Continue to the location-point fallback.
                }
            }

            if (resolvedCenter == null)
            {
                try
                {
                    LocationPoint locationPoint =
                        element.Location as LocationPoint;

                    resolvedCenter =
                        locationPoint?.Point;
                }
                catch
                {
                    resolvedCenter = null;
                }
            }

            CacheElementCenter(
                element,
                resolvedCenter);

            return resolvedCenter;
        }

        private static ConnectorManager GetConnectorManager(
            Element element)
        {
            MEPCurve curve = element as MEPCurve;
            if (curve != null)
                return curve.ConnectorManager;

            FamilyInstance familyInstance =
                element as FamilyInstance;

            return familyInstance?.MEPModel?.ConnectorManager;
        }

        private sealed class ShapedBranchPipeCandidate
        {
            public Connector Connector { get; set; }
            public Pipe Pipe { get; set; }
            public PipeDimensions Dimensions { get; set; }
        }

        private sealed class ShapedBranchSpatialHeader
        {
            public Pipe Pipe { get; set; }
            public PipeDimensions Dimensions { get; set; }
            public XYZ SurfaceOrigin { get; set; }
            public XYZ InwardDirection { get; set; }
            public Connector MatchConnector { get; set; }
            public bool IsAmbiguous { get; set; }
        }

        private sealed class ShapedBranchConnection
        {
            public ElementId FittingId { get; set; }
            public string FittingName { get; set; }

            public ElementId HeaderPipeId { get; set; }
            public ElementId BranchPipeId { get; set; }
            public ElementId BranchConnectedElementId { get; set; }

            public PipeDimensions HeaderDimensions { get; set; }
            public PipeDimensions BranchDimensions { get; set; }

            public XYZ HeaderConnectorOrigin { get; set; }
            public XYZ HeaderConnectorMatchOrigin { get; set; }
            public XYZ OutletConnectorOrigin { get; set; }
            public XYZ OutletInwardDirection { get; set; }
            public XYZ HeaderInwardDirection { get; set; }

            public string BranchDimensionSource { get; set; }
            public string HeaderResolutionSource { get; set; }
            public bool HeaderPipeIsCalculationContextOnly { get; set; }

            public XYZ HeaderAxisStart { get; set; }
            public XYZ HeaderAxisDirection { get; set; }
            public double HeaderAxisLength { get; set; }

            public bool IsStandaloneComponent { get; set; }
        }

        private sealed class SideCouplingConnection
        {
            public ElementId FittingId { get; set; }
            public string FittingName { get; set; }

            public ElementId HeaderPipeId { get; set; }
            public ElementId OutletPipeId { get; set; }

            public PipeDimensions HeaderDimensions { get; set; }
            public PipeDimensions OutletDimensions { get; set; }

            public XYZ HeaderConnectorOrigin { get; set; }
            public XYZ HeaderInwardDirection { get; set; }

            public XYZ HeaderAxisStart { get; set; }
            public XYZ HeaderAxisDirection { get; set; }
            public double HeaderAxisLength { get; set; }
        }
    }
}
