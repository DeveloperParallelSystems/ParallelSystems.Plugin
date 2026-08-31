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
        private static FabricationElementGeometry BuildPipeGeometry(
            Document doc,
            Pipe pipe,
            IDictionary<ElementId, PipeDimensions> dimensionsById,
            ISet<ElementId> selectedSourceIds,
            IDictionary<ElementId, List<ShapedBranchConnection>>
                shapedBranchesByHeaderPipe,
            IDictionary<ElementId, ShapedBranchConnection>
                shapedBranchConnections,
            IDictionary<ElementId, List<SideCouplingConnection>>
                sideCouplingsByHeaderPipe,
            IDictionary<ElementId, SideCouplingConnection>
                sideCouplingConnections,
            IList<FabricationIssue> issues)
        {
            PipeDimensions dimensions;
            if (!dimensionsById.TryGetValue(pipe.Id, out dimensions))
                return null;

            XYZ start;
            XYZ end;
            XYZ normalizedDirection;
            double length;

            if (!TryGetStraightPipeAxis(
                    pipe,
                    out start,
                    out end,
                    out normalizedDirection,
                    out length))
            {
                bool hasLinearEndpoints =
                    start != null &&
                    end != null;

                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = pipe.Id,
                    ElementName = GetElementDisplayName(pipe),
                    Message = hasLinearEndpoints
                        ? "The pipe has zero or invalid length."
                        : "Only straight rigid pipes are supported by the fabrication STEP generator."
                });

                return null;
            }

            Solid outer = CreateCylinder(
                start,
                normalizedDirection,
                length,
                dimensions.OutsideDiameter / 2.0);

            double cutterExtension = Math.Max(
                1.0 / FeetToMillimetres,
                dimensions.WallThickness * 0.10);

            Solid inner = CreateCylinder(
                start - (normalizedDirection * cutterExtension),
                normalizedDirection,
                length + (2.0 * cutterExtension),
                dimensions.InsideDiameter / 2.0);

            Solid preparedOuter = outer;

            List<EndPreparation> endPreparations =
                ResolvePipeEndPreparations(
                    doc,
                    pipe,
                    dimensions,
                    selectedSourceIds,
                    shapedBranchConnections,
                    sideCouplingConnections,
                    issues);

            int chamferedEnds = 0;
            int plainEnds = 0;
            List<string> endPreparationNotes = new List<string>();

            foreach (EndPreparation preparation in endPreparations)
            {
                endPreparationNotes.Add(preparation.Description);

                if (!preparation.ShouldChamfer)
                {
                    plainEnds++;
                    continue;
                }

                Solid chamferCutter;
                string chamferError;

                if (!TryCreateChamferCutter(
                        preparation,
                        doc.Application.ShortCurveTolerance,
                        out chamferCutter,
                        out chamferError))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = pipe.Id,
                        ElementName = GetElementDisplayName(pipe),
                        Message = chamferError
                    });

                    return null;
                }

                try
                {
                    double beforeVolume = preparedOuter.Volume;
                    Solid chamfered =
                        BooleanOperationsUtils.ExecuteBooleanOperation(
                            preparedOuter,
                            chamferCutter,
                            BooleanOperationsType.Difference);

                    if (chamfered == null ||
                        chamfered.Volume >= beforeVolume - GeometryTolerance)
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity = FabricationIssueSeverity.Blocking,
                            ElementId = pipe.Id,
                            ElementName = GetElementDisplayName(pipe),
                            Message =
                                "The required 30 degree chamfer did not remove material at " +
                                preparation.ConnectionLabel + "."
                        });

                        return null;
                    }

                    preparedOuter = chamfered;
                    chamferedEnds++;
                }
                catch (Exception ex)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = pipe.Id,
                        ElementName = GetElementDisplayName(pipe),
                        Message =
                            "The required 30 degree chamfer failed at " +
                            preparation.ConnectionLabel + ": " + ex.Message
                    });

                    return null;
                }
            }

            int shapedBranchOpeningCount = 0;
            List<ShapedBranchConnection> headerBranches;

            if (shapedBranchesByHeaderPipe != null &&
                shapedBranchesByHeaderPipe.TryGetValue(
                    pipe.Id,
                    out headerBranches))
            {
                foreach (ShapedBranchConnection branch in headerBranches)
                {
                    Solid openingCutter;
                    string openingError;

                    if (!TryCreateSetOnHeaderOpeningCutter(
                            branch,
                            out openingCutter,
                            out openingError))
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = pipe.Id,
                            ElementName =
                                GetElementDisplayName(pipe),
                            Message = openingError
                        });

                        return null;
                    }

                    try
                    {
                        double beforeVolume =
                            preparedOuter.Volume;

                        Solid opened =
                            BooleanOperationsUtils
                                .ExecuteBooleanOperation(
                                    preparedOuter,
                                    openingCutter,
                                    BooleanOperationsType.Difference);

                        if (opened == null ||
                            opened.Volume >=
                            beforeVolume - GeometryTolerance)
                        {
                            issues.Add(new FabricationIssue
                            {
                                Severity =
                                    FabricationIssueSeverity.Blocking,
                                ElementId = pipe.Id,
                                ElementName =
                                    GetElementDisplayName(pipe),
                                Message =
                                    "The SET-ON opening did not remove " +
                                    "material from the header for " +
                                    branch.FittingName +
                                    ". Verify that the shaped branch is " +
                                    "physically attached to the header pipe."
                            });

                            return null;
                        }

                        preparedOuter = opened;
                        shapedBranchOpeningCount++;
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = pipe.Id,
                            ElementName =
                                GetElementDisplayName(pipe),
                            Message =
                                "The SET-ON header opening failed for " +
                                branch.FittingName + ": " + ex.Message
                        });

                        return null;
                    }
                }
            }

            int sideCouplingOpeningCount = 0;
            List<SideCouplingConnection> headerCouplings;

            if (sideCouplingsByHeaderPipe != null &&
                sideCouplingsByHeaderPipe.TryGetValue(
                    pipe.Id,
                    out headerCouplings))
            {
                foreach (SideCouplingConnection coupling in headerCouplings)
                {
                    Solid openingCutter;
                    string openingError;

                    if (!TryCreateSetOnHeaderOpeningCutter(
                            coupling,
                            out openingCutter,
                            out openingError))
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = pipe.Id,
                            ElementName =
                                GetElementDisplayName(pipe),
                            Message = openingError
                        });

                        return null;
                    }

                    try
                    {
                        double beforeVolume =
                            preparedOuter.Volume;

                        Solid opened =
                            BooleanOperationsUtils
                                .ExecuteBooleanOperation(
                                    preparedOuter,
                                    openingCutter,
                                    BooleanOperationsType.Difference);

                        if (opened == null ||
                            opened.Volume >=
                            beforeVolume - GeometryTolerance)
                        {
                            issues.Add(new FabricationIssue
                            {
                                Severity =
                                    FabricationIssueSeverity.Blocking,
                                ElementId = pipe.Id,
                                ElementName =
                                    GetElementDisplayName(pipe),
                                Message =
                                    "The tap-half coupling opening did not " +
                                    "remove material from the header for " +
                                    coupling.FittingName +
                                    ". Verify that the coupling is physically " +
                                    "attached to the header pipe."
                            });

                            return null;
                        }

                        preparedOuter = opened;
                        sideCouplingOpeningCount++;
                    }
                    catch (Exception ex)
                    {
                        issues.Add(new FabricationIssue
                        {
                            Severity =
                                FabricationIssueSeverity.Blocking,
                            ElementId = pipe.Id,
                            ElementName =
                                GetElementDisplayName(pipe),
                            Message =
                                "The tap-half coupling header opening failed for " +
                                coupling.FittingName + ": " + ex.Message
                        });

                        return null;
                    }
                }
            }

            Solid hollow;
            try
            {
                // Apply the end preparations to the simple outer cylinder
                // first, then subtract the bore. Revit handles this sequence
                // more reliably than beveling an already hollow thin shell.
                hollow = BooleanOperationsUtils.ExecuteBooleanOperation(
                    preparedOuter,
                    inner,
                    BooleanOperationsType.Difference);
            }
            catch (Exception ex)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = pipe.Id,
                    ElementName = GetElementDisplayName(pipe),
                    Message =
                        "The pipe hollowing operation failed after applying the end preparations: " +
                        ex.Message
                });

                return null;
            }

            string notes = dimensions.SourceDescription;

            if (endPreparationNotes.Count > 0)
            {
                notes += "; " + string.Join(
                    "; ",
                    endPreparationNotes.Distinct());
            }

            return new FabricationElementGeometry
            {
                SourceElementId = pipe.Id,
                SourceUniqueId = pipe.UniqueId,
                SourceName = GetElementDisplayName(pipe),
                CategoryName = pipe.Category?.Name ?? "Pipes",
                Geometry = new List<GeometryObject> { hollow },
                PipeDimensions = dimensions,
                Status =
                    "Generated hollow pipe; chamfered ends " +
                    chamferedEnds.ToString(CultureInfo.InvariantCulture) +
                    "; plain ends " +
                    plainEnds.ToString(CultureInfo.InvariantCulture) +
                    "; SET-ON branch openings " +
                    shapedBranchOpeningCount.ToString(
                        CultureInfo.InvariantCulture) +
                    "; tap-half coupling openings " +
                    sideCouplingOpeningCount.ToString(
                        CultureInfo.InvariantCulture),
                Notes = notes
            };
        }

        private static List<EndPreparation> ResolvePipeEndPreparations(
            Document doc,
            Pipe pipe,
            PipeDimensions dimensions,
            ISet<ElementId> selectedSourceIds,
            IDictionary<ElementId, ShapedBranchConnection>
                shapedBranchConnections,
            IDictionary<ElementId, SideCouplingConnection>
                sideCouplingConnections,
            IList<FabricationIssue> issues)
        {
            List<EndPreparation> result = new List<EndPreparation>();
            ConnectorManager manager = GetConnectorManager(pipe);

            if (manager == null)
                return result;

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
                    pipe,
                    connector,
                    selectedSourceIds);

                bool connectedIsSelected = connectedElement != null &&
                    selectedSourceIds.Contains(connectedElement.Id);

                bool connectedIsFlange = connectedElement != null &&
                    IsFlangeLike(doc, connectedElement);

                bool connectedIsReducer = connectedElement != null &&
                    IsReducerLike(doc, connectedElement);

                bool connectedUsesPlainCopperCapillaryEnds =
                    connectedElement != null &&
                    IsCopperCapillaryReducerLike(
                        doc,
                        connectedElement);

                ShapedBranchConnection shapedBranchConnection = null;

                if (connectedElement != null &&
                    shapedBranchConnections != null)
                {
                    shapedBranchConnections.TryGetValue(
                        connectedElement.Id,
                        out shapedBranchConnection);
                }

                bool shapedBranchHeaderSide =
                    shapedBranchConnection != null &&
                    pipe.Id.Equals(
                        shapedBranchConnection.HeaderPipeId);

                bool shapedBranchOutletSide =
                    shapedBranchConnection != null &&
                    pipe.Id.Equals(
                        shapedBranchConnection.BranchPipeId);

                SideCouplingConnection sideCouplingConnection = null;

                if (connectedElement != null &&
                    sideCouplingConnections != null)
                {
                    sideCouplingConnections.TryGetValue(
                        connectedElement.Id,
                        out sideCouplingConnection);
                }

                bool sideCouplingHeaderSide =
                    sideCouplingConnection != null &&
                    pipe.Id.Equals(
                        sideCouplingConnection.HeaderPipeId);

                bool sideCouplingOutletSide =
                    sideCouplingConnection != null &&
                    pipe.Id.Equals(
                        sideCouplingConnection.OutletPipeId);

                string connectionLabel = connectedElement == null
                    ? "an open connector"
                    : "connection to " +
                      GetElementDisplayName(connectedElement);

                bool shouldChamfer;
                string description;

                if (sideCouplingHeaderSide ||
                    sideCouplingOutletSide)
                {
                    shouldChamfer = false;
                    description = sideCouplingHeaderSide
                        ? "Plain header retained at the tap-half coupling opening"
                        : "Plain outlet-pipe end retained at the tap-half coupling";
                }
                else if (shapedBranchHeaderSide)
                {
                    shouldChamfer = false;
                    description =
                        "Plain header retained at the SET-ON branch opening";
                }
                else if (shapedBranchOutletSide)
                {
                    shouldChamfer =
                        connectedIsSelected &&
                        !connectedIsFlange;

                    description = shouldChamfer
                        ? "30 degree chamfer with 1 mm root face at the shaped-branch outlet"
                        : "Plain pipe end retained at the unselected shaped-branch outlet";
                }
                else if (connectedUsesPlainCopperCapillaryEnds)
                {
                    shouldChamfer = false;

                    description =
                        "Plain pipe end retained at the copper " +
                        "capillary reducer connection";
                }
                else if (connectedIsFlange)
                {
                    shouldChamfer = false;

                    description =
                        "Plain end retained at " + connectionLabel +
                        " because the joint involves a flange";
                }
                else if (!connectedIsSelected)
                {
                    shouldChamfer = false;

                    description = connectedElement == null
                        ? "Plain end retained at open connector"
                        : "Plain end retained at spool boundary to unselected element " +
                          GetElementDisplayName(connectedElement);
                }
                else if (connectedIsReducer)
                {
                    shouldChamfer = true;

                    description =
                        "30 degree chamfer with 1 mm root face at " +
                        connectionLabel;
                }
                else
                {
                    shouldChamfer = true;

                    description =
                        "30 degree chamfer with 1 mm root face at " +
                        connectionLabel;
                }

                result.Add(new EndPreparation
                {
                    Origin = connector.Origin,
                    OutwardDirection = GetConnectorOutwardDirection(
                        pipe,
                        connector,
                        connectedElement),
                    OutsideDiameter = dimensions.OutsideDiameter,
                    InsideDiameter = dimensions.InsideDiameter,
                    WallThickness = dimensions.WallThickness,
                    RootFaceMillimetres =
                        ChamferRootFaceMillimetres,
                    ShouldChamfer = shouldChamfer,
                    ConnectionLabel = connectionLabel,
                    Description = description
                });
            }

            if (result.Count != 2)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Warning,
                    ElementId = pipe.Id,
                    ElementName = GetElementDisplayName(pipe),
                    Message =
                        "Expected two round end connectors for the straight pipe, but found " +
                        result.Count.ToString(CultureInfo.InvariantCulture) +
                        ". Verify the generated end preparation in the inspection view."
                });
            }

            return result;
        }
    }
}
