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
        private static FabricationElementGeometry BuildFittingGeometry(
            Document doc,
            Element element,
            IDictionary<ElementId, PipeDimensions> pipeDimensions,
            IDictionary<double, PipeDimensions> dimensionsByNominal,
            IDictionary<double, PipeDimensions> documentDimensionsByNominal,
            IDictionary<ElementId, PipeDimensions>
                componentDimensionOverrides,
            ISet<ElementId> selectedSourceIds,
            ShapedBranchConnection shapedBranchConnection,
            SideCouplingConnection sideCouplingConnection,
            IList<FabricationIssue> issues)
        {
            // Weld-gap and non-connector helper families are connection
            // metadata, not fabrication solids. They are deliberately omitted
            // and treated as transparent links in the piping network.
            if (IsIgnoredConnectionElement(doc, element))
                return null;

            List<Solid> sourceSolids = GetElementSolids(element);
            if (sourceSolids.Count == 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "No usable solid geometry was found in the fitting family at Fine detail level."
                });

                return null;
            }

            List<ConnectorBore> bores = ResolveConnectorBores(
                doc,
                element,
                sourceSolids,
                pipeDimensions,
                dimensionsByNominal,
                documentDimensionsByNominal,
                componentDimensionOverrides,
                selectedSourceIds,
                shapedBranchConnection,
                sideCouplingConnection,
                issues);

            if (bores.Count < 2)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "At least two round piping connectors with resolvable inside diameters are required."
                });

                return null;
            }

            if (bores.Any(x => x.InsideDiameter <= 0))
                return null;

            // Do not modify the imported family solid for a concentric reducer.
            // Connector/reference-plane offsets and sloped family faces caused
            // an asymmetric gap on one side and a thin web on the opposite side.
            // Rebuild the reducer from the two connected pipe dimensions so the
            // large end, small end, bore, and chamfers share one exact axis.
            if (IsConcentricReducerLike(doc, element))
            {
                return BuildProceduralConcentricReducerGeometry(
                    doc,
                    element,
                    bores,
                    issues);
            }

            // A verified SET-ON header produces one watertight smooth BRep
            // branch containing the straight body, bore, saddle, external
            // bevel, and weld land. No second touching body is retained.
            // Without a header, the safe fallback remains a plain
            // hollow standalone branch with a straight-through bore.
            if (shapedBranchConnection != null)
            {
                ConnectorBore outletBore =
                    bores.FirstOrDefault(x =>
                        x.IsShapedBranchOutletSide);

                Solid proceduralStandaloneBranch = null;
                List<GeometryObject> proceduralGeometry =
                    new List<GeometryObject>();
                string proceduralDescription;
                string proceduralError;
                int maximumExpectedStepFaceCount = 0;

                bool proceduralResolved;

                if (shapedBranchConnection.IsStandaloneComponent)
                {
                    proceduralResolved =
                        TryCreateProceduralStandaloneShapedBranchSolid(
                            element,
                            shapedBranchConnection,
                            outletBore,
                            out proceduralStandaloneBranch,
                            out proceduralDescription,
                            out proceduralError);

                    if (proceduralResolved &&
                        proceduralStandaloneBranch != null)
                    {
                        proceduralGeometry.Add(
                            proceduralStandaloneBranch);
                    }
                }
                else
                {
                    proceduralResolved =
                        TryCreateSingleBodySmoothSetOnShapedBranchGeometry(
                            shapedBranchConnection,
                            outletBore,
                            doc.Application.ShortCurveTolerance,
                            out proceduralGeometry,
                            out maximumExpectedStepFaceCount,
                            out proceduralDescription,
                            out proceduralError);
                }

                if (!proceduralResolved)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName =
                            GetElementDisplayName(element),
                        Message = proceduralError
                    });

                    return null;
                }

                bool outletChamfered =
                    outletBore != null &&
                    outletBore.ShouldChamfer;

                int proceduralChamferedEnds =
                    shapedBranchConnection.IsStandaloneComponent
                        ? (outletChamfered ? 1 : 0)
                        : 1 + (outletChamfered ? 1 : 0);

                int proceduralPlainEnds =
                    shapedBranchConnection.IsStandaloneComponent
                        ? 1 + (outletChamfered ? 0 : 1)
                        : (outletChamfered ? 0 : 1);

                return new FabricationElementGeometry
                {
                    SourceElementId = element.Id,
                    SourceUniqueId = element.UniqueId,
                    SourceName =
                        GetElementDisplayName(element),
                    CategoryName =
                        element.Category?.Name ??
                        "Pipe Fitting",
                    Geometry = proceduralGeometry,
                    RequiresCompactSetOnTopology =
                        !shapedBranchConnection.IsStandaloneComponent,
                    MaximumExpectedStepFaceCount =
                        shapedBranchConnection.IsStandaloneComponent
                            ? 0
                            : maximumExpectedStepFaceCount,
                    Status =
                        (shapedBranchConnection.IsStandaloneComponent
                            ? "Procedural standalone shaped branch"
                            : "Single-body smooth BRep SET-ON shaped branch") +
                        "; chamfered ends " +
                        proceduralChamferedEnds.ToString(
                            CultureInfo.InvariantCulture) +
                        "; plain ends " +
                        proceduralPlainEnds.ToString(
                            CultureInfo.InvariantCulture),
                    Notes = proceduralDescription
                };
            }

            List<Solid> boreCutters;
            string cutterDescription;

            try
            {
                boreCutters = CreateFittingBoreCutters(
                    element,
                    bores,
                    out cutterDescription);
            }
            catch (Exception ex)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The internal fitting bore could not be generated: " +
                        ex.Message
                });

                return null;
            }

            if (boreCutters.Count == 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message = "No fitting bore cutter was generated."
                });

                return null;
            }

            // The generic two-connector bore can stop at the shaped-branch
            // header connector/reference plane. When that plane is offset from
            // the physical saddle, a thin circular diaphragm remains visible
            // from inside the header pipe. Add an independent axial cutter that
            // starts inside the branch body and continues beyond the saddle
            // into the header bore. It is subtracted only from the fitting
            // solids, so it cannot damage the generated header pipe.
            if (shapedBranchConnection != null)
            {
                Solid continuityCutter;
                string continuityError;

                bool continuityResolved =
                    shapedBranchConnection.IsStandaloneComponent
                        ? TryCreateStandaloneShapedBranchBoreCutter(
                            element,
                            shapedBranchConnection,
                            out continuityCutter,
                            out continuityError)
                        : TryCreateShapedBranchFittingContinuityCutter(
                            element,
                            shapedBranchConnection,
                            out continuityCutter,
                            out continuityError);

                if (!continuityResolved)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message = continuityError
                    });

                    return null;
                }

                boreCutters.Add(continuityCutter);

                cutterDescription +=
                    shapedBranchConnection.IsStandaloneComponent
                        ? "; standalone shaped-branch bore opened through " +
                          "the full component while retaining the original " +
                          "family saddle profile"
                        : "; SET-ON shaped-branch bore extended completely " +
                          "through the saddle into the header bore";
            }

            // Tap-half/side-coupling families can have the same connector
            // reference-plane problem as shaped branches. The generic bore
            // may stop at the header-side connector plane and leave a thin
            // internal diaphragm. Extend the outlet bore completely through
            // the coupling saddle before hollowing the family solids.
            if (sideCouplingConnection != null)
            {
                Solid couplingContinuityCutter;
                string couplingContinuityError;

                if (!TryCreateSideCouplingFittingContinuityCutter(
                        element,
                        sideCouplingConnection,
                        out couplingContinuityCutter,
                        out couplingContinuityError))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message = couplingContinuityError
                    });

                    return null;
                }

                boreCutters.Add(couplingContinuityCutter);

                cutterDescription +=
                    "; tap-half coupling bore extended completely " +
                    "through the saddle into the header bore";
            }

            List<Solid> currentSolids = sourceSolids.ToList();
            int chamferedEnds = 0;
            int plainEnds = 0;
            bool chamferMaterialRemoved = false;
            List<string> endPreparationNotes = new List<string>();

            // Apply and verify each chamfer before hollowing the fitting.
            // This is more stable than beveling a thin shell after the bore
            // has already been subtracted, and it prevents the status report
            // from claiming a chamfer that did not actually intersect the part.
            foreach (ConnectorBore bore in bores)
            {
                endPreparationNotes.Add(bore.EndPreparationDescription);

                if (!bore.ShouldChamfer)
                {
                    plainEnds++;
                    continue;
                }

                EndPreparation preparation = new EndPreparation
                {
                    Origin = bore.Origin,
                    OutwardDirection = bore.OutwardDirection,
                    OutsideDiameter = bore.OutsideDiameter,
                    InsideDiameter = bore.InsideDiameter,
                    WallThickness = bore.WallThickness,
                    RootFaceMillimetres = bore.RootFaceMillimetres,
                    ShouldChamfer = true,
                    ConnectionLabel = bore.ConnectionLabel,
                    Description = bore.EndPreparationDescription
                };

                List<Solid> chamferedSolids;
                string chamferNote;
                string chamferError;

                if (!TryApplyVerifiedChamfer(
                        currentSolids,
                        preparation,
                        bore.OriginalConnectorOrigin,
                        bore.UsePhysicalFaceSearch,
                        GetElementExtent(element),
                        doc.Application.ShortCurveTolerance,
                        out chamferedSolids,
                        out chamferNote,
                        out chamferError))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message = chamferError
                    });

                    return null;
                }

                if (!string.IsNullOrWhiteSpace(chamferNote))
                    endPreparationNotes.Add(chamferNote);

                currentSolids = chamferedSolids;
                chamferMaterialRemoved = true;
                chamferedEnds++;
            }

            bool boreMaterialRemoved = false;

            foreach (Solid boreCutter in boreCutters)
            {
                bool removed;
                currentSolids = SubtractCutterFromSolids(
                    currentSolids,
                    boreCutter,
                    out removed);

                if (removed)
                    boreMaterialRemoved = true;
            }

            bool shapedBranchHeaderIntrusionRemoved = false;

            if (shapedBranchConnection != null &&
                !shapedBranchConnection.IsStandaloneComponent)
            {
                Solid headerEnvelopeCutter;
                string headerEnvelopeError;

                if (!TryCreateSetOnHeaderEnvelopeTrimCutter(
                        shapedBranchConnection,
                        out headerEnvelopeCutter,
                        out headerEnvelopeError))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message = headerEnvelopeError
                    });

                    return null;
                }

                // A SET-ON branch must stop at the header's outside surface.
                // Subtracting the analytical header outer cylinder removes any
                // family stub, sleeve, saddle remnant, or connector-reference
                // material that protrudes into the header bore.
                currentSolids = SubtractCutterFromSolids(
                    currentSolids,
                    headerEnvelopeCutter,
                    out shapedBranchHeaderIntrusionRemoved);

                if (shapedBranchHeaderIntrusionRemoved)
                {
                    cutterDescription +=
                        "; shaped-branch material inside the header outer " +
                        "envelope removed for a flush SET-ON connection";
                }
                else
                {
                    cutterDescription +=
                        "; shaped-branch already terminated outside the " +
                        "header outer envelope";
                }

                // Trimming a multi-solid adjustable branch against the curved
                // header envelope can expose a thin saddle lip that was hidden
                // before the trim. Run one final coaxial bore cleanup after the
                // trim so the finished outlet remains circular and unobstructed.
                Solid postTrimBoreCleanupCutter;
                string postTrimBoreCleanupError;

                if (!TryCreateShapedBranchPostTrimBoreCleanupCutter(
                        element,
                        shapedBranchConnection,
                        out postTrimBoreCleanupCutter,
                        out postTrimBoreCleanupError))
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message = postTrimBoreCleanupError
                    });

                    return null;
                }

                bool postTrimBoreMaterialRemoved;

                currentSolids = SubtractCutterFromSolids(
                    currentSolids,
                    postTrimBoreCleanupCutter,
                    out postTrimBoreMaterialRemoved);

                cutterDescription +=
                    postTrimBoreMaterialRemoved
                        ? "; shaped-branch saddle bore cleaned after " +
                          "header-envelope trimming"
                        : "; shaped-branch saddle bore already clear " +
                          "after header-envelope trimming";

            }

            List<GeometryObject> resultGeometry = currentSolids
                .Where(x =>
                    x != null &&
                    x.Volume > GeometryTolerance)
                .Cast<GeometryObject>()
                .ToList();

            if (resultGeometry.Count == 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The fitting geometry became invalid after the chamfer and hollowing operations."
                });

                return null;
            }

            string notes = cutterDescription + "; " +
                string.Join(
                    "; ",
                    bores.Select(x => x.SourceDescription).Distinct());

            if (endPreparationNotes.Count > 0)
            {
                notes += "; " + string.Join(
                    "; ",
                    endPreparationNotes.Distinct());
            }

            if (!boreMaterialRemoved)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Warning,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "No measurable bore material was removed from this fitting. " +
                        "The family may already be hollow, or its solid geometry may not intersect the generated bore. " +
                        "Inspect this fitting in the generated 3D view before releasing it for fabrication."
                });

                notes += "; no measurable bore subtraction detected";
            }

            bool geometryModified =
                boreMaterialRemoved ||
                chamferMaterialRemoved ||
                shapedBranchHeaderIntrusionRemoved;

            return new FabricationElementGeometry
            {
                SourceElementId = element.Id,
                SourceUniqueId = element.UniqueId,
                SourceName = GetElementDisplayName(element),
                CategoryName = element.Category?.Name ?? "Pipe Fitting",
                Geometry = resultGeometry,
                Status = geometryModified
                    ? "Prepared fitting; chamfered ends " +
                      chamferedEnds.ToString(CultureInfo.InvariantCulture) +
                      "; plain ends " +
                      plainEnds.ToString(CultureInfo.InvariantCulture)
                    : "Original fitting retained with warning; chamfered ends " +
                      chamferedEnds.ToString(CultureInfo.InvariantCulture) +
                      "; plain ends " +
                      plainEnds.ToString(CultureInfo.InvariantCulture),
                Notes = notes
            };
        }

        private static List<Solid> SubtractCutterFromSolids(
            IEnumerable<Solid> sourceSolids,
            Solid cutter,
            out bool materialRemoved)
        {
            double ignoredRemovedVolume;

            return SubtractCutterFromSolids(
                sourceSolids,
                cutter,
                out materialRemoved,
                out ignoredRemovedVolume);
        }

        private static List<Solid> SubtractCutterFromSolids(
            IEnumerable<Solid> sourceSolids,
            Solid cutter,
            out bool materialRemoved,
            out double removedVolume)
        {
            materialRemoved = false;
            removedVolume = 0.0;
            List<Solid> result = new List<Solid>();

            foreach (Solid sourceSolid in sourceSolids)
            {
                if (sourceSolid == null ||
                    sourceSolid.Volume <= GeometryTolerance)
                {
                    continue;
                }

                try
                {
                    double beforeVolume = sourceSolid.Volume;
                    Solid difference =
                        BooleanOperationsUtils.ExecuteBooleanOperation(
                            sourceSolid,
                            cutter,
                            BooleanOperationsType.Difference);

                    if (difference != null &&
                        difference.Volume <
                        beforeVolume - GeometryTolerance)
                    {
                        double elementRemovedVolume =
                            beforeVolume - difference.Volume;

                        materialRemoved = true;
                        removedVolume += elementRemovedVolume;
                        result.Add(difference);
                    }
                    else
                    {
                        result.Add(sourceSolid);
                    }
                }
                catch
                {
                    // A cutter may not intersect every solid in a multi-solid
                    // family. Preserve that source solid and continue.
                    result.Add(sourceSolid);
                }
            }

            return result;
        }

        private static List<ConnectorBore> ResolveConnectorBores(
            Document doc,
            Element fitting,
            IList<Solid> sourceSolids,
            IDictionary<ElementId, PipeDimensions> pipeDimensions,
            IDictionary<double, PipeDimensions> dimensionsByNominal,
            IDictionary<double, PipeDimensions> documentDimensionsByNominal,
            IDictionary<ElementId, PipeDimensions>
                componentDimensionOverrides,
            ISet<ElementId> selectedSourceIds,
            ShapedBranchConnection shapedBranchConnection,
            SideCouplingConnection sideCouplingConnection,
            IList<FabricationIssue> issues)
        {
            ConnectorManager manager = GetConnectorManager(fitting);
            List<ConnectorBore> result = new List<ConnectorBore>();

            if (manager == null)
                return result;

            bool ownerIsFlange = IsFlangeLike(doc, fitting);
            bool ownerIsReducer = IsReducerLike(doc, fitting);
            bool ownerIsConcentricReducer =
                IsConcentricReducerLike(doc, fitting);
            bool ownerUsesPlainCopperCapillaryEnds =
                IsCopperCapillaryReducerLike(doc, fitting);

            foreach (Connector connector in manager.Connectors)
            {
                if (connector == null ||
                    connector.Domain != Domain.DomainPiping ||
                    connector.ConnectorType != ConnectorType.End)
                {
                    continue;
                }

                if (connector.Shape != ConnectorProfileType.Round)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "A non-round piping connector is not supported by the hollow fitting generator."
                    });

                    continue;
                }

                double nominal = connector.Radius * 2.0;
                PipeDimensions matched = null;
                string source = null;

                Element connectedElement = GetConnectedElement(
                    fitting,
                    connector,
                    selectedSourceIds);

                bool shapedBranchHeaderSide =
                    shapedBranchConnection != null &&
                    (
                        ConnectorOriginsMatch(
                            connector.Origin,
                            shapedBranchConnection
                                .HeaderConnectorMatchOrigin) ||
                        (
                            shapedBranchConnection
                                .IsStandaloneComponent &&
                            !ConnectorOriginsMatch(
                                connector.Origin,
                                shapedBranchConnection
                                    .OutletConnectorOrigin)
                        ) ||
                        (
                            connectedElement != null &&
                            shapedBranchConnection.HeaderPipeId != null &&
                            !shapedBranchConnection.HeaderPipeId.Equals(
                                ElementId.InvalidElementId) &&
                            connectedElement.Id.Equals(
                                shapedBranchConnection.HeaderPipeId)
                        )
                    );

                bool shapedBranchOutletSide =
                    shapedBranchConnection != null &&
                    (
                        ConnectorOriginsMatch(
                            connector.Origin,
                            shapedBranchConnection
                                .OutletConnectorOrigin) ||
                        (
                            connectedElement != null &&
                            (
                                (
                                    shapedBranchConnection.BranchPipeId !=
                                        null &&
                                    !shapedBranchConnection.BranchPipeId
                                        .Equals(
                                            ElementId.InvalidElementId) &&
                                    connectedElement.Id.Equals(
                                        shapedBranchConnection.BranchPipeId)
                                ) ||
                                (
                                    shapedBranchConnection
                                            .BranchConnectedElementId !=
                                        null &&
                                    !shapedBranchConnection
                                        .BranchConnectedElementId.Equals(
                                            ElementId.InvalidElementId) &&
                                    connectedElement.Id.Equals(
                                        shapedBranchConnection
                                            .BranchConnectedElementId)
                                )
                            )
                        )
                    );

                bool sideCouplingHeaderSide =
                    sideCouplingConnection != null &&
                    connectedElement != null &&
                    connectedElement.Id.Equals(
                        sideCouplingConnection.HeaderPipeId);

                bool sideCouplingOutletSide =
                    sideCouplingConnection != null &&
                    connectedElement != null &&
                    connectedElement.Id.Equals(
                        sideCouplingConnection.OutletPipeId);

                Pipe connectedPipe = connectedElement as Pipe;

                // A SET-ON shaped branch uses the smaller branch-pipe bore on
                // both fitting connectors. The header-side connector must not
                // inherit the large header pipe's ID, OD, or wall thickness.
                if ((shapedBranchHeaderSide ||
                     shapedBranchOutletSide) &&
                    shapedBranchConnection.BranchDimensions != null)
                {
                    matched =
                        shapedBranchConnection.BranchDimensions;

                    if (matched.NominalDiameter > 0)
                        nominal = matched.NominalDiameter;

                    source =
                        shapedBranchConnection.IsStandaloneComponent
                            ? "Standalone shaped-branch dimensions resolved from " +
                              (shapedBranchConnection.BranchDimensionSource ??
                               "the shaped-branch family")
                            : "SET-ON shaped-branch dimensions resolved from " +
                              (shapedBranchConnection.BranchDimensionSource ??
                               "the branch-side connection");
                }
                else if ((sideCouplingHeaderSide ||
                          sideCouplingOutletSide) &&
                         sideCouplingConnection.OutletDimensions != null)
                {
                    // A tap-half coupling uses the smaller outlet-pipe bore on
                    // both connectors. The header-side connector must never
                    // inherit the large header pipe's dimensions.
                    matched =
                        sideCouplingConnection.OutletDimensions;

                    if (matched.NominalDiameter > 0)
                        nominal = matched.NominalDiameter;

                    source =
                        "Tap-half coupling dimensions matched from outlet pipe " +
                        RevitApiCompatibility.GetElementIdValue(
                            sideCouplingConnection.OutletPipeId)
                            .ToString(CultureInfo.InvariantCulture);
                }
                else if (connectedElement != null &&
                         componentDimensionOverrides != null &&
                         componentDimensionOverrides.TryGetValue(
                             connectedElement.Id,
                             out matched) &&
                         matched != null)
                {
                    source =
                        "Bore matched from connected special fabrication " +
                        "component " +
                        RevitApiCompatibility.GetElementIdValue(
                            connectedElement.Id).ToString(
                                CultureInfo.InvariantCulture);
                }
                else if (connectedPipe != null)
                {
                    if (!pipeDimensions.TryGetValue(
                            connectedPipe.Id,
                            out matched))
                    {
                        string ignoredError;
                        TryResolvePipeDimensions(
                            doc,
                            connectedPipe,
                            out matched,
                            out ignoredError);
                    }

                    if (matched != null)
                    {
                        source =
                            "Bore matched from directly connected pipe " +
                            RevitApiCompatibility.GetElementIdValue(
                                connectedPipe.Id).ToString(
                                    CultureInfo.InvariantCulture);
                    }
                }

                if (matched == null)
                {
                    ElementId networkPipeId;
                    if (TryFindPipeDimensionsInConnectedNetwork(
                            doc,
                            fitting,
                            connector,
                            pipeDimensions,
                            nominal,
                            componentDimensionOverrides,
                            out matched,
                            out networkPipeId))
                    {
                        source =
                            "Bore matched from connected network pipe " +
                            RevitApiCompatibility.GetElementIdValue(
                                networkPipeId).ToString(
                                    CultureInfo.InvariantCulture);
                    }
                }

                // Copper capillary connectors commonly report the tube OD
                // (for example 200 mm), while the connected Revit pipe may
                // report a different nominal value. If the strict size match
                // fails, resolve the nearest unambiguous pipe on this exact
                // connector branch. This does not cross back through the
                // reducer to the opposite end.
                if (matched == null &&
                    ownerIsConcentricReducer &&
                    IsCopperTubeFamilyLike(doc, fitting))
                {
                    ElementId nearestPipeId;

                    if (TryFindNearestPipeDimensionsInConnectedNetwork(
                            doc,
                            fitting,
                            connector,
                            pipeDimensions,
                            out matched,
                            out nearestPipeId))
                    {
                        source =
                            "Copper reducer bore matched from nearest " +
                            "unambiguous network pipe " +
                            RevitApiCompatibility.GetElementIdValue(
                                nearestPipeId).ToString(
                                    CultureInfo.InvariantCulture);
                    }
                }

                if (matched == null)
                {
                    dimensionsByNominal.TryGetValue(
                        RoundDiameterKey(nominal),
                        out matched);

                    if (matched != null)
                        source = "Bore matched from selected pipe size";
                }

                if (matched == null &&
                    documentDimensionsByNominal != null)
                {
                    documentDimensionsByNominal.TryGetValue(
                        RoundDiameterKey(nominal),
                        out matched);

                    if (matched != null)
                    {
                        source =
                            "Bore matched from the unique pipe dimensions for this nominal size in the Revit model";
                    }
                }

                double inside = matched?.InsideDiameter ?? 0;
                double outside = matched?.OutsideDiameter ?? 0;
                double wall = matched?.WallThickness ?? 0;

                if (inside <= 0)
                {
                    inside = GetNamedDoubleParameter(
                        doc,
                        fitting,
                        "Inside Diameter",
                        "Inner Diameter",
                        "Pipe Inside Diameter",
                        "Actual Inside Diameter",
                        "Bore Diameter",
                        "Bore",
                        "Tube Inside Diameter",
                        "Copper Tube Inside Diameter",
                        "Actual Tube Inside Diameter",
                        "ID");

                    if (inside > 0)
                        source = "Bore read from fitting Inside Diameter parameter";
                }

                if (outside <= 0)
                {
                    outside = GetNamedDoubleParameter(
                        doc,
                        fitting,
                        "Outside Diameter",
                        "Outer Diameter",
                        "Pipe Outside Diameter",
                        "Actual Outside Diameter",
                        "Tube Outside Diameter",
                        "Copper Tube Outside Diameter",
                        "Actual Tube Outside Diameter",
                        "Tube Diameter",
                        "OD");
                }

                if (wall <= 0)
                {
                    wall = GetNamedDoubleParameter(
                        doc,
                        fitting,
                        "Wall Thickness",
                        "Pipe Wall Thickness",
                        "Standard Wall Thickness",
                        "Tube Wall Thickness",
                        "Copper Tube Wall Thickness",
                        "Actual Tube Wall Thickness",
                        "Tube Thickness",
                        "Wall",
                        "WT",
                        "Thickness");
                }

                // A reducer connected directly to a flange or another hollow
                // fitting may have no pipe from which to inherit its ID. Read
                // the physical opening from the mating fitting first. This is
                // especially important for the MM Kembla copper reducer whose
                // own source family can be solid before the procedural bore is
                // generated.
                if (inside <= 0 &&
                    connectedElement != null &&
                    !(connectedElement is Pipe))
                {
                    double connectedInside;
                    string connectedOpeningSource;

                    if (TryInferInsideDiameterFromConnectedFitting(
                            doc,
                            fitting,
                            connector,
                            connectedElement,
                            nominal,
                            out connectedInside,
                            out connectedOpeningSource))
                    {
                        inside = connectedInside;
                        source = connectedOpeningSource;
                    }
                }

                // Copper flange and reducer libraries commonly expose only
                // connector size, with no usable ID/wall parameter and no
                // connected pipe carrying resolvable dimensions. Recover the
                // actual opening directly from the family solid by probing
                // annular sections near the connector plane.
                if ((inside <= 0 || outside <= 0) &&
                    (ownerIsFlange || ownerIsReducer))
                {
                    double inferredInside;
                    double inferredOutside;
                    double inferredOffset;

                    XYZ geometryProbeDirection =
                        GetConnectorOutwardDirection(
                            fitting,
                            connector,
                            connectedElement);

                    if (TryInferConnectorDiametersFromGeometry(
                            sourceSolids,
                            connector.Origin,
                            geometryProbeDirection,
                            nominal,
                            GetElementExtent(fitting),
                            out inferredInside,
                            out inferredOutside,
                            out inferredOffset))
                    {
                        if (inside <= 0 &&
                            inferredInside > GeometryTolerance)
                        {
                            inside = inferredInside;
                        }

                        if (outside <= 0 &&
                            inferredOutside >
                            inferredInside + GeometryTolerance)
                        {
                            outside = inferredOutside;
                        }

                        source =
                            (source ?? "Connector dimensions unresolved") +
                            "; ID" +
                            (outside > 0 ? "/OD" : string.Empty) +
                            " inferred from physical fitting geometry " +
                            FormatMillimetres(
                                Math.Abs(inferredOffset)) +
                            (inferredOffset < 0
                                ? " inward from connector"
                                : " outward from connector");
                    }
                }

                // A solid reducer cannot provide an annular ID section, but
                // its circular outside profile can still be measured near the
                // connector. Use that profile as the end OD.
                if (outside <= 0 && ownerIsReducer)
                {
                    double inferredReducerOutside;
                    double inferredReducerOffset;

                    XYZ reducerProbeDirection =
                        GetConnectorOutwardDirection(
                            fitting,
                            connector,
                            connectedElement);

                    if (TryInferConnectorOutsideDiameterFromGeometry(
                            sourceSolids,
                            connector.Origin,
                            reducerProbeDirection,
                            nominal,
                            GetElementExtent(fitting),
                            out inferredReducerOutside,
                            out inferredReducerOffset))
                    {
                        outside = inferredReducerOutside;

                        source =
                            (source ?? "Connector dimensions unresolved") +
                            "; reducer OD inferred from physical end profile " +
                            FormatMillimetres(
                                Math.Abs(inferredReducerOffset)) +
                            (inferredReducerOffset < 0
                                ? " inward from connector"
                                : " outward from connector");
                    }
                }

                // Metric capillary copper fitting connector size represents
                // the mating tube outside diameter. Keep this fallback narrow
                // to copper/Kembla families and use it only after both
                // parameter and geometry-based OD resolution have failed.
                if (outside <= 0 &&
                    nominal > GeometryTolerance &&
                    IsCopperTubeFamilyLike(doc, fitting))
                {
                    outside = nominal;

                    source =
                        (source ?? "Connector dimensions unresolved") +
                        "; OD taken from metric copper tube connector size";
                }

                if (inside <= 0 && outside > 0 && wall > 0)
                    inside = outside - (2.0 * wall);

                if (outside <= 0 && inside > 0 && wall > 0)
                    outside = inside + (2.0 * wall);

                if (wall <= 0 && outside > inside && inside > 0)
                    wall = (outside - inside) / 2.0;

                if (inside <= 0 &&
                    !ownerIsConcentricReducer &&
                    !ownerIsFlange)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "The inside diameter for connector size " +
                            FormatMillimetres(nominal) +
                            " could not be resolved. The plugin searched directly connected pipes, the connected fitting network, " +
                            "selected pipes, copper/tube parameters, and the physical fitting geometry. " +
                            "Connect this fitting network to a pipe with valid ID/OD data, or add an Inside Diameter/Wall Thickness parameter to the fitting type."
                    });
                }

                bool connectedIsSelected = connectedElement != null &&
                    selectedSourceIds.Contains(connectedElement.Id);

                bool connectedIsFlange =
                    connectedElement != null &&
                    IsFlangeLike(doc, connectedElement);

                string connectionLabel = connectedElement == null
                    ? "an open connector"
                    : "connection to " +
                      GetElementDisplayName(connectedElement);

                bool shouldChamfer;
                string endPreparationDescription;

                if (sideCouplingHeaderSide ||
                    sideCouplingOutletSide)
                {
                    // Both coupling interfaces are plain-ended. The large
                    // header receives a side opening, and the outlet pipe
                    // remains plain against the threaded half coupling.
                    shouldChamfer = false;

                    endPreparationDescription = sideCouplingHeaderSide
                        ? "Plain tap-half coupling face retained at the header opening"
                        : "Plain tap-half coupling outlet retained by fabrication rule";
                }
                else if (shapedBranchHeaderSide)
                {
                    // A SET-ON header attachment is a saddle/opening, not a
                    // butt-welded end. The large header receives its own hole.
                    shouldChamfer = false;

                    endPreparationDescription =
                        "Plain SET-ON face retained at the header opening";
                }
                else if (shapedBranchOutletSide)
                {
                    // GetConnectedElement has already walked through the weld
                    // helper. The actual fabrication joint is the shaped-branch
                    // outlet against the small branch pipe.
                    shouldChamfer =
                        connectedIsSelected &&
                        !connectedIsFlange;

                    endPreparationDescription = shouldChamfer
                        ? "30 degree chamfer with 1 mm root face at the shaped-branch outlet"
                        : "Plain end retained at the unselected shaped-branch outlet";
                }
                else if (ownerUsesPlainCopperCapillaryEnds)
                {
                    // Capillary copper reducers are socket/solder/braze
                    // fittings. A 30 degree butt-weld bevel is not applicable.
                    shouldChamfer = false;

                    endPreparationDescription =
                        "Plain capillary reducer end retained at " +
                        connectionLabel;
                }
                else if (ownerIsFlange || connectedIsFlange)
                {
                    shouldChamfer = false;

                    endPreparationDescription =
                        "Plain end retained at " + connectionLabel +
                        " because the joint involves a flange";
                }
                else if (!connectedIsSelected)
                {
                    shouldChamfer = false;

                    endPreparationDescription = connectedElement == null
                        ? "Plain end retained at open connector"
                        : "Plain end retained at spool boundary to unselected element " +
                          GetElementDisplayName(connectedElement);
                }
                else if (ownerIsReducer)
                {
                    shouldChamfer = true;

                    endPreparationDescription =
                        "30 degree chamfer with 1 mm root face at " +
                        connectionLabel;
                }
                else
                {
                    shouldChamfer = true;

                    endPreparationDescription =
                        "30 degree chamfer with 1 mm root face at " +
                        connectionLabel;
                }

                if (shouldChamfer &&
                    (outside <= 0 || wall <= 0) &&
                    !ownerIsConcentricReducer)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Blocking,
                        ElementId = fitting.Id,
                        ElementName = GetElementDisplayName(fitting),
                        Message =
                            "A chamfer is required at " + connectionLabel +
                            ", but OD/wall thickness could not be resolved for connector size " +
                            FormatMillimetres(nominal) + ". " +
                            "Connect this fitting network to a pipe with valid OD/ID data, or provide Outside Diameter and Wall Thickness parameters on the fitting type."
                    });
                }

                XYZ outwardDirection =
                    GetConnectorOutwardDirection(
                        fitting,
                        connector,
                        connectedElement);

                XYZ chamferOrigin = connector.Origin;
                double faceOffset = 0.0;

                // Shaped-branch and butt-weld reducer families commonly place
                // their Revit connector on a reference or weld-gap plane rather
                // than on the physical metal face. For reducers, recover the
                // physical end independently on both the large and small ends.
                bool usePhysicalFaceSearch =
                    shouldChamfer &&
                    (shapedBranchOutletSide ||
                     (ownerIsReducer &&
                      !IsConcentricReducerLike(doc, fitting)));

                if (usePhysicalFaceSearch)
                {
                    XYZ physicalFaceOrigin;

                    if (TryResolvePhysicalEndFaceOrigin(
                            sourceSolids,
                            connector.Origin,
                            outwardDirection,
                            outside,
                            inside,
                            GetElementExtent(fitting),
                            out physicalFaceOrigin,
                            out faceOffset))
                    {
                        chamferOrigin = physicalFaceOrigin;

                        source =
                            (source ?? "Bore dimensions resolved") +
                            "; physical fitting end face located " +
                            FormatMillimetres(Math.Abs(faceOffset)) +
                            (faceOffset < 0
                                ? " inward from connector"
                                : " outward from connector");
                    }
                }

                result.Add(new ConnectorBore
                {
                    Origin = chamferOrigin,
                    OriginalConnectorOrigin = connector.Origin,
                    OutwardDirection = outwardDirection,
                    NominalDiameter = nominal,
                    OutsideDiameter = outside,
                    InsideDiameter = inside,
                    WallThickness = wall,
                    RootFaceMillimetres =
                        ChamferRootFaceMillimetres,
                    ConnectedElementId = connectedElement?.Id,
                    ConnectedElementName = connectedElement == null
                        ? string.Empty
                        : GetElementDisplayName(connectedElement),
                    ShouldChamfer = shouldChamfer,
                    IsShapedBranchHeaderSide =
                        shapedBranchHeaderSide,
                    IsShapedBranchOutletSide =
                        shapedBranchOutletSide,
                    UsePhysicalFaceSearch = usePhysicalFaceSearch,
                    ConnectionLabel = connectionLabel,
                    EndPreparationDescription =
                        endPreparationDescription,
                    SourceDescription = source ?? "Bore unresolved"
                });
            }

            if (shapedBranchConnection != null &&
                !result.Any(x =>
                    x.IsShapedBranchHeaderSide))
            {
                PipeDimensions branchDimensions =
                    shapedBranchConnection.BranchDimensions;

                result.Add(new ConnectorBore
                {
                    Origin =
                        shapedBranchConnection.HeaderConnectorOrigin,
                    OriginalConnectorOrigin =
                        shapedBranchConnection.HeaderConnectorOrigin,
                    OutwardDirection =
                        shapedBranchConnection.HeaderInwardDirection,
                    NominalDiameter =
                        branchDimensions.NominalDiameter,
                    OutsideDiameter =
                        branchDimensions.OutsideDiameter,
                    InsideDiameter =
                        branchDimensions.InsideDiameter,
                    WallThickness =
                        branchDimensions.WallThickness,
                    RootFaceMillimetres =
                        ChamferRootFaceMillimetres,
                    ConnectedElementId =
                        shapedBranchConnection.HeaderPipeId,
                    ConnectedElementName =
                        shapedBranchConnection.IsStandaloneComponent
                            ? "Standalone shaped-branch saddle opening"
                            : "SET-ON header pipe",
                    ShouldChamfer = false,
                    IsShapedBranchHeaderSide = true,
                    IsSynthetic = true,
                    ConnectionLabel =
                        shapedBranchConnection.IsStandaloneComponent
                            ? "the standalone shaped-branch saddle opening"
                            : "the SET-ON header opening",
                    EndPreparationDescription =
                        shapedBranchConnection.IsStandaloneComponent
                            ? "Synthetic plain saddle-side bore added for " +
                              "standalone component export"
                            : "Synthetic plain SET-ON header-side bore added " +
                              "because the family has no usable connected " +
                              "header connector",
                    SourceDescription =
                        shapedBranchConnection.IsStandaloneComponent
                            ? "Synthetic standalone saddle-side bore using " +
                              "resolved shaped-branch outlet dimensions"
                            : "Synthetic header-side bore using resolved " +
                              "shaped-branch outlet dimensions"
                });
            }

            if (ownerIsFlange)
            {
                CompleteFlangeEndDimensions(
                    doc,
                    fitting,
                    result,
                    issues);
            }

            if (ownerIsConcentricReducer)
            {
                CompleteConcentricReducerEndDimensions(
                    doc,
                    fitting,
                    result,
                    issues);
            }

            return result;
        }

        private static List<Solid> CreateFittingBoreCutters(
            Element fitting,
            IList<ConnectorBore> bores,
            out string description)
        {
            double extent = GetElementExtent(fitting);
            // Only a small amount of cutter extension is required outside
            // each connector face. Extending by a full pipe diameter caused
            // tee branch cutters to continue through the opposite wall.
            double openingExtension = Math.Max(
                5.0 / FeetToMillimetres,
                bores.Max(x => x.InsideDiameter) * 0.05);

            List<Solid> cutters = new List<Solid>();

            // A flange family may place one or both connectors on reference
            // planes inside the physical flange instead of exactly on the
            // outer mating faces. The normal two-connector bore only extends
            // a small distance beyond those connector origins, which can
            // leave a thin web of material where two flange faces meet.
            //
            // For a straight two-connector flange, create one continuous
            // through-bore whose end profiles are deliberately placed beyond
            // both physical ends of the fitting. This keeps the flange ends
            // plain while guaranteeing an unobstructed flow path through a
            // flange-to-flange joint.
            if (bores.Count == 2 &&
                IsFlangeLike(fitting.Document, fitting) &&
                !IsBlindFlangeLike(fitting.Document, fitting))
            {
                ConnectorBore firstFlangeBore = bores[0];
                ConnectorBore secondFlangeBore = bores[1];

                XYZ connectorSpan =
                    secondFlangeBore.Origin -
                    firstFlangeBore.Origin;

                XYZ boreAxis;

                if (connectorSpan.GetLength() > GeometryTolerance)
                {
                    boreAxis = connectorSpan.Normalize();
                }
                else
                {
                    XYZ fallbackAxis =
                        firstFlangeBore.OutwardDirection;

                    if (fallbackAxis == null ||
                        fallbackAxis.GetLength() <= GeometryTolerance)
                    {
                        throw new InvalidOperationException(
                            "The flange through-bore axis could not be resolved from its connectors.");
                    }

                    boreAxis = fallbackAxis.Normalize();
                }

                double throughExtension = Math.Max(
                    extent,
                    Math.Max(
                        firstFlangeBore.InsideDiameter,
                        secondFlangeBore.InsideDiameter) * 2.0);

                XYZ firstExtendedOrigin =
                    firstFlangeBore.Origin -
                    (boreAxis * throughExtension);

                XYZ secondExtendedOrigin =
                    secondFlangeBore.Origin +
                    (boreAxis * throughExtension);

                CurveLoop firstExtendedLoop = CreateCircleLoop(
                    firstExtendedOrigin,
                    boreAxis,
                    firstFlangeBore.InsideDiameter / 2.0);

                CurveLoop secondExtendedLoop = CreateCircleLoop(
                    secondExtendedOrigin,
                    boreAxis,
                    secondFlangeBore.InsideDiameter / 2.0);

                cutters.Add(
                    GeometryCreationUtilities.CreateLoftGeometry(
                        new List<CurveLoop>
                        {
                            firstExtendedLoop,
                            secondExtendedLoop
                        },
                        new SolidOptions(
                            ElementId.InvalidElementId,
                            ElementId.InvalidElementId)));

                description =
                    "Flange through-bore extended beyond both physical mating faces";

                return cutters;
            }

            if (bores.Count == 2)
            {
                ConnectorBore first = bores[0];
                ConnectorBore second = bores[1];

                XYZ firstPathTangent = -first.OutwardDirection;
                XYZ secondPathTangent = second.OutwardDirection;

                double directionDot = Math.Abs(
                    firstPathTangent.DotProduct(secondPathTangent));

                if (directionDot >= ConnectorDirectionTolerance)
                {
                    CurveLoop firstLoop = CreateCircleLoop(
                        first.Origin,
                        first.OutwardDirection,
                        first.InsideDiameter / 2.0);

                    CurveLoop secondLoop = CreateCircleLoop(
                        second.Origin,
                        second.OutwardDirection,
                        second.InsideDiameter / 2.0);

                    cutters.Add(
                        GeometryCreationUtilities.CreateLoftGeometry(
                            new List<CurveLoop>
                            {
                                firstLoop,
                                secondLoop
                            },
                            new SolidOptions(
                                ElementId.InvalidElementId,
                                ElementId.InvalidElementId)));

                    AddConnectorOpeningCutters(
                        cutters,
                        bores,
                        openingExtension);

                    bool isReducer = Math.Abs(
                        first.InsideDiameter -
                        second.InsideDiameter) > DiameterTolerance;

                    bool isEccentric = DistancePointToInfiniteLine(
                        second.Origin,
                        first.Origin,
                        first.OutwardDirection) >
                        Math.Max(
                            GeometryTolerance,
                            Math.Max(
                                first.InsideDiameter,
                                second.InsideDiameter) * 0.005);

                    if (!isReducer)
                    {
                        description = "Straight two-connector bore";
                    }
                    else
                    {
                        description = isEccentric
                            ? "Eccentric transition/reducer bore"
                            : "Concentric transition/reducer bore";
                    }

                    return cutters;
                }

                double boreDifference = Math.Abs(
                    first.InsideDiameter - second.InsideDiameter);

                if (boreDifference > Math.Max(
                        DiameterTolerance,
                        Math.Max(
                            first.InsideDiameter,
                            second.InsideDiameter) * 0.01))
                {
                    throw new InvalidOperationException(
                        "A reducing elbow was detected. Standard reducers are supported, but a reducing elbow requires an approved family-specific bore rule.");
                }

                Arc centerlineArc = CreateTangentArc(
                    first.Origin,
                    firstPathTangent,
                    second.Origin,
                    secondPathTangent);

                CurveLoop sweepPath = new CurveLoop();
                sweepPath.Append(centerlineArc);

                CurveLoop profile = CreateCircleLoop(
                    first.Origin,
                    firstPathTangent,
                    first.InsideDiameter / 2.0);

                cutters.Add(
                    GeometryCreationUtilities.CreateSweptGeometry(
                        sweepPath,
                        0,
                        centerlineArc.GetEndParameter(0),
                        new List<CurveLoop> { profile }));

                AddConnectorOpeningCutters(
                    cutters,
                    bores,
                    openingExtension);

                description = "Curved elbow bore";
                return cutters;
            }

            ConnectorBore runFirst;
            ConnectorBore runSecond;

            bool hasRun = TryFindMainRun(
                bores,
                out runFirst,
                out runSecond);

            XYZ junctionPoint = hasRun
                ? EstimateRunJunctionPoint(
                    runFirst,
                    runSecond,
                    bores)
                : EstimateConnectorJunctionPoint(bores);

            if (hasRun)
            {
                CurveLoop runFirstLoop = CreateCircleLoop(
                    runFirst.Origin,
                    runFirst.OutwardDirection,
                    runFirst.InsideDiameter / 2.0);

                CurveLoop runSecondLoop = CreateCircleLoop(
                    runSecond.Origin,
                    runSecond.OutwardDirection,
                    runSecond.InsideDiameter / 2.0);

                cutters.Add(
                    GeometryCreationUtilities.CreateLoftGeometry(
                        new List<CurveLoop>
                        {
                            runFirstLoop,
                            runSecondLoop
                        },
                        new SolidOptions(
                            ElementId.InvalidElementId,
                            ElementId.InvalidElementId)));

                AddConnectorOpeningCutters(
                    cutters,
                    new[] { runFirst, runSecond },
                    openingExtension);
            }

            double mainRunInsideRadius = hasRun
                ? Math.Min(
                    runFirst.InsideDiameter,
                    runSecond.InsideDiameter) / 2.0
                : 0.0;

            IEnumerable<ConnectorBore> branchBores = hasRun
                ? bores.Where(x =>
                    !ReferenceEquals(x, runFirst) &&
                    !ReferenceEquals(x, runSecond))
                : bores;

            foreach (ConnectorBore bore in branchBores)
            {
                XYZ inwardDirection = -bore.OutwardDirection;
                double distanceToJunction = Math.Max(
                    0,
                    (junctionPoint - bore.Origin)
                        .DotProduct(inwardDirection));

                if (distanceToJunction <= GeometryTolerance)
                {
                    distanceToJunction = Math.Min(
                        bore.Origin.DistanceTo(junctionPoint),
                        extent);
                }

                double branchInsideRadius =
                    bore.InsideDiameter / 2.0;

                // The branch bore only needs to reach the main-run bore.
                // Do not extend it by a percentage of the diameter: for an
                // equal tee that drives the cutter toward the far wall and
                // creates the top/bottom breakout holes seen in the test.
                double safeJunctionOverlap = 0.0;

                if (hasRun &&
                    mainRunInsideRadius >
                    branchInsideRadius + GeometryTolerance)
                {
                    double geometricLimit = Math.Sqrt(
                        Math.Max(
                            0.0,
                            (mainRunInsideRadius *
                             mainRunInsideRadius) -
                            (branchInsideRadius *
                             branchInsideRadius)));

                    safeJunctionOverlap = Math.Min(
                        0.5 / FeetToMillimetres,
                        geometricLimit * 0.25);
                }

                XYZ start =
                    bore.Origin +
                    (bore.OutwardDirection * openingExtension);

                cutters.Add(CreateCylinder(
                    start,
                    inwardDirection,
                    openingExtension +
                    distanceToJunction +
                    safeJunctionOverlap,
                    branchInsideRadius));
            }

            description = hasRun
                ? "Tee/cross bore with a lofted main run and branch bores terminating at the connector-axis junction"
                : "Multi-branch bore terminating at the estimated connector-axis junction";

            return cutters;
        }

        private static void AddConnectorOpeningCutters(
            ICollection<Solid> cutters,
            IEnumerable<ConnectorBore> bores,
            double extension)
        {
            foreach (ConnectorBore bore in bores)
            {
                XYZ start =
                    bore.Origin +
                    (bore.OutwardDirection * extension);

                cutters.Add(CreateCylinder(
                    start,
                    -bore.OutwardDirection,
                    extension * 2.0,
                    bore.InsideDiameter / 2.0));
            }
        }

        private static bool TryFindMainRun(
            IList<ConnectorBore> bores,
            out ConnectorBore first,
            out ConnectorBore second)
        {
            first = null;
            second = null;
            double bestScore = double.MinValue;

            for (int i = 0; i < bores.Count - 1; i++)
            {
                for (int j = i + 1; j < bores.Count; j++)
                {
                    ConnectorBore candidateFirst = bores[i];
                    ConnectorBore candidateSecond = bores[j];

                    double alignment = Math.Abs(
                        candidateFirst.OutwardDirection.DotProduct(
                            candidateSecond.OutwardDirection));

                    if (alignment < ConnectorDirectionTolerance)
                        continue;

                    double separation = candidateFirst.Origin.DistanceTo(
                        candidateSecond.Origin);

                    double score =
                        (alignment * 1000.0) + separation;

                    if (score <= bestScore)
                        continue;

                    bestScore = score;
                    first = candidateFirst;
                    second = candidateSecond;
                }
            }

            return first != null && second != null;
        }

        private static XYZ EstimateRunJunctionPoint(
            ConnectorBore runFirst,
            ConnectorBore runSecond,
            IEnumerable<ConnectorBore> bores)
        {
            XYZ runDirection = (
                runSecond.Origin - runFirst.Origin).Normalize();

            List<XYZ> projectedPoints = new List<XYZ>();

            foreach (ConnectorBore bore in bores)
            {
                if (ReferenceEquals(bore, runFirst) ||
                    ReferenceEquals(bore, runSecond))
                {
                    continue;
                }

                XYZ branchDirection = -bore.OutwardDirection;
                XYZ runPoint;
                XYZ branchPoint;

                if (TryGetClosestPointsOnInfiniteLines(
                        runFirst.Origin,
                        runDirection,
                        bore.Origin,
                        branchDirection,
                        out runPoint,
                        out branchPoint))
                {
                    projectedPoints.Add((runPoint + branchPoint) / 2.0);
                }
            }

            if (projectedPoints.Count == 0)
            {
                return (runFirst.Origin + runSecond.Origin) / 2.0;
            }

            return AveragePoints(projectedPoints);
        }

        private static XYZ EstimateConnectorJunctionPoint(
            IList<ConnectorBore> bores)
        {
            List<XYZ> intersections = new List<XYZ>();

            for (int i = 0; i < bores.Count - 1; i++)
            {
                for (int j = i + 1; j < bores.Count; j++)
                {
                    XYZ firstPoint;
                    XYZ secondPoint;

                    if (!TryGetClosestPointsOnInfiniteLines(
                            bores[i].Origin,
                            -bores[i].OutwardDirection,
                            bores[j].Origin,
                            -bores[j].OutwardDirection,
                            out firstPoint,
                            out secondPoint))
                    {
                        continue;
                    }

                    intersections.Add((firstPoint + secondPoint) / 2.0);
                }
            }

            return intersections.Count > 0
                ? AveragePoints(intersections)
                : AveragePoints(bores.Select(x => x.Origin));
        }

        private static bool TryGetClosestPointsOnInfiniteLines(
            XYZ firstOrigin,
            XYZ firstDirection,
            XYZ secondOrigin,
            XYZ secondDirection,
            out XYZ firstPoint,
            out XYZ secondPoint)
        {
            XYZ u = firstDirection.Normalize();
            XYZ v = secondDirection.Normalize();
            XYZ w0 = firstOrigin - secondOrigin;

            double a = u.DotProduct(u);
            double b = u.DotProduct(v);
            double c = v.DotProduct(v);
            double d = u.DotProduct(w0);
            double e = v.DotProduct(w0);
            double denominator = (a * c) - (b * b);

            if (Math.Abs(denominator) <= GeometryTolerance)
            {
                firstPoint = null;
                secondPoint = null;
                return false;
            }

            double firstParameter =
                ((b * e) - (c * d)) / denominator;

            double secondParameter =
                ((a * e) - (b * d)) / denominator;

            firstPoint = firstOrigin + (u * firstParameter);
            secondPoint = secondOrigin + (v * secondParameter);
            return true;
        }

        private static XYZ AveragePoints(IEnumerable<XYZ> points)
        {
            List<XYZ> values = points
                .Where(x => x != null)
                .ToList();

            if (values.Count == 0)
                return XYZ.Zero;

            double x = values.Sum(value => value.X) / values.Count;
            double y = values.Sum(value => value.Y) / values.Count;
            double z = values.Sum(value => value.Z) / values.Count;

            return new XYZ(x, y, z);
        }

        private static double DistancePointToInfiniteLine(
            XYZ point,
            XYZ lineOrigin,
            XYZ lineDirection)
        {
            XYZ direction = lineDirection.Normalize();
            XYZ offset = point - lineOrigin;
            XYZ projected = direction * offset.DotProduct(direction);
            return (offset - projected).GetLength();
        }

        private static Arc CreateTangentArc(
            XYZ start,
            XYZ startTangent,
            XYZ end,
            XYZ endTangent)
        {
            XYZ t0 = startTangent.Normalize();
            XYZ t1 = endTangent.Normalize();
            XYZ planeNormal = t0.CrossProduct(t1);

            if (planeNormal.GetLength() <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The elbow connector directions are parallel and cannot define a curved centerline.");
            }

            planeNormal = planeNormal.Normalize();

            XYZ radialLine0 =
                planeNormal.CrossProduct(t0).Normalize();
            XYZ radialLine1 =
                planeNormal.CrossProduct(t1).Normalize();

            double denominator =
                radialLine0
                    .CrossProduct(radialLine1)
                    .DotProduct(planeNormal);

            if (Math.Abs(denominator) <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The elbow center could not be calculated from its connectors.");
            }

            double parameter =
                (end - start)
                    .CrossProduct(radialLine1)
                    .DotProduct(planeNormal) /
                denominator;

            XYZ center = start + (radialLine0 * parameter);
            double startRadius = start.DistanceTo(center);
            double endRadius = end.DistanceTo(center);

            if (startRadius <= GeometryTolerance ||
                Math.Abs(startRadius - endRadius) >
                Math.Max(GeometryTolerance, startRadius * 0.01))
            {
                throw new InvalidOperationException(
                    "The fitting connector positions do not define a consistent circular elbow.");
            }

            XYZ u0 = (start - center).Normalize();
            XYZ u1 = (end - center).Normalize();
            XYZ axis = u0.CrossProduct(u1);

            if (axis.GetLength() <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The elbow arc is degenerate.");
            }

            axis = axis.Normalize();

            if (axis.CrossProduct(u0).DotProduct(t0) < 0)
                axis = -axis;

            double angle = Math.Atan2(
                axis.DotProduct(u0.CrossProduct(u1)),
                Clamp(u0.DotProduct(u1), -1.0, 1.0));

            if (angle <= 0)
                angle += 2.0 * Math.PI;

            if (angle >= Math.PI)
            {
                throw new InvalidOperationException(
                    "Elbows with a centerline sweep of 180 degrees or more are not supported.");
            }

            XYZ midpointDirection = RotateVector(
                u0,
                axis,
                angle / 2.0);

            XYZ midpoint = center +
                (midpointDirection * startRadius);

            return Arc.Create(start, end, midpoint);
        }

        private static XYZ RotateVector(
            XYZ vector,
            XYZ axis,
            double angle)
        {
            double cosine = Math.Cos(angle);
            double sine = Math.Sin(angle);

            return
                (vector * cosine) +
                (axis.CrossProduct(vector) * sine) +
                (axis * axis.DotProduct(vector) * (1.0 - cosine));
        }

        private sealed class ConnectorBore
        {
            public XYZ Origin { get; set; }
            public XYZ OriginalConnectorOrigin { get; set; }
            public XYZ OutwardDirection { get; set; }
            public double NominalDiameter { get; set; }
            public double OutsideDiameter { get; set; }
            public double InsideDiameter { get; set; }
            public double WallThickness { get; set; }
            public double RootFaceMillimetres { get; set; }
            public ElementId ConnectedElementId { get; set; }
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
    }
}
