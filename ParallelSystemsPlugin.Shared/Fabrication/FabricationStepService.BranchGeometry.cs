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
        private static bool
            TryCreateSideCouplingFittingContinuityCutter(
                Element fitting,
                SideCouplingConnection coupling,
                out Solid cutter,
                out string error)
        {
            cutter = null;
            error = null;

            if (coupling == null)
            {
                error =
                    "The tap-half coupling fitting continuity " +
                    "information is missing.";

                return false;
            }

            // A side coupling follows the same axial continuity rule as a
            // SET-ON shaped branch. The header opening is generated on the
            // pipe separately; this cutter affects only the coupling solid.
            ShapedBranchConnection equivalentConnection =
                new ShapedBranchConnection
                {
                    FittingId = coupling.FittingId,
                    FittingName = coupling.FittingName,
                    HeaderPipeId = coupling.HeaderPipeId,
                    BranchPipeId = coupling.OutletPipeId,
                    HeaderDimensions = coupling.HeaderDimensions,
                    BranchDimensions = coupling.OutletDimensions,
                    HeaderConnectorOrigin =
                        coupling.HeaderConnectorOrigin,
                    HeaderInwardDirection =
                        coupling.HeaderInwardDirection,
                    HeaderAxisStart =
                        coupling.HeaderAxisStart,
                    HeaderAxisDirection =
                        coupling.HeaderAxisDirection,
                    HeaderAxisLength =
                        coupling.HeaderAxisLength
                };

            bool succeeded =
                TryCreateShapedBranchFittingContinuityCutter(
                    fitting,
                    equivalentConnection,
                    out cutter,
                    out error);

            if (!succeeded &&
                !string.IsNullOrWhiteSpace(error))
            {
                error = error
                    .Replace(
                        "shaped-branch fitting",
                        "tap-half coupling fitting")
                    .Replace(
                        "Shaped-branch fitting",
                        "Tap-half coupling fitting")
                    .Replace(
                        "shaped-branch",
                        "tap-half coupling")
                    .Replace(
                        "Shaped-branch",
                        "Tap-half coupling")
                    .Replace(
                        "through the saddle into the header bore",
                        "through the coupling saddle into the header bore");
            }

            return succeeded;
        }

        private static bool
            TryResolveSetOnBranchFrame(
                ShapedBranchConnection branch,
                out XYZ resolvedSurfaceOrigin,
                out XYZ branchInwardAxis,
                out XYZ radialInwardDirection,
                out XYZ headerAxisPoint,
                out string error)
        {
            resolvedSurfaceOrigin = null;
            branchInwardAxis = null;
            radialInwardDirection = null;
            headerAxisPoint = null;
            error = null;

            if (branch == null ||
                branch.HeaderDimensions == null ||
                branch.BranchDimensions == null ||
                branch.HeaderConnectorOrigin == null ||
                branch.HeaderAxisStart == null ||
                branch.HeaderAxisDirection == null)
            {
                error =
                    "The SET-ON branch frame information is missing.";

                return false;
            }

            XYZ headerAxisDirection =
                branch.HeaderAxisDirection.Normalize();

            // Prefer the physical outlet connector axis. This guarantees that
            // the generated fitting bore is coaxial with the branch outlet,
            // so the opening remains circular when viewed from the branch.
            //
            // The previous outlet-to-estimated-surface vector could be slightly
            // tilted by an adjustable-family reference point. That tilt removed
            // more material on two opposing sides and left a capsule-shaped bore.
            if (branch.OutletConnectorOrigin != null &&
                branch.OutletInwardDirection != null &&
                branch.OutletInwardDirection.GetLength() >
                    GeometryTolerance)
            {
                XYZ outletOrigin =
                    branch.OutletConnectorOrigin;

                XYZ outletAxis =
                    branch.OutletInwardDirection.Normalize();

                double outletProjection =
                    (outletOrigin -
                     branch.HeaderAxisStart)
                    .DotProduct(headerAxisDirection);

                XYZ outletAxisPoint =
                    branch.HeaderAxisStart +
                    (headerAxisDirection * outletProjection);

                XYZ outletToHeaderAxis =
                    outletAxisPoint - outletOrigin;

                if (outletToHeaderAxis.GetLength() >
                        GeometryTolerance &&
                    outletAxis.DotProduct(
                        outletToHeaderAxis) < 0)
                {
                    outletAxis = -outletAxis;
                }

                double nearDistance;
                double farDistance;

                if (TryGetLineCylinderIntersections(
                        outletOrigin,
                        outletAxis,
                        branch.HeaderAxisStart,
                        headerAxisDirection,
                        branch.HeaderDimensions
                            .OutsideDiameter / 2.0,
                        out nearDistance,
                        out farDistance))
                {
                    double surfaceDistance;

                    if (TryGetIntersectionAtOrAfter(
                            nearDistance,
                            farDistance,
                            0.0,
                            out surfaceDistance))
                    {
                        XYZ outletSurfaceOrigin =
                            outletOrigin +
                            (outletAxis * surfaceDistance);

                        double resolvedProjection =
                            (outletSurfaceOrigin -
                             branch.HeaderAxisStart)
                            .DotProduct(headerAxisDirection);

                        double outletEndTolerance = Math.Max(
                            2.0 / FeetToMillimetres,
                            branch.HeaderDimensions
                                .OutsideDiameter * 0.02);

                        if (resolvedProjection >=
                                -outletEndTolerance &&
                            resolvedProjection <=
                                branch.HeaderAxisLength +
                                outletEndTolerance)
                        {
                            XYZ resolvedHeaderAxisPoint =
                                branch.HeaderAxisStart +
                                (headerAxisDirection *
                                 resolvedProjection);

                            XYZ resolvedSurfaceToAxis =
                                resolvedHeaderAxisPoint -
                                outletSurfaceOrigin;

                            if (resolvedSurfaceToAxis.GetLength() >
                                GeometryTolerance)
                            {
                                XYZ resolvedRadialInward =
                                    resolvedSurfaceToAxis.Normalize();

                                if (outletAxis.DotProduct(
                                        resolvedRadialInward) >=
                                    0.10)
                                {
                                    resolvedSurfaceOrigin =
                                        outletSurfaceOrigin;

                                    branchInwardAxis =
                                        outletAxis;

                                    radialInwardDirection =
                                        resolvedRadialInward;

                                    headerAxisPoint =
                                        resolvedHeaderAxisPoint;

                                    return true;
                                }
                            }
                        }
                    }
                }
            }

            // Fallback for legacy families that do not expose a usable outlet
            // connector direction. This path preserves the prior spatial
            // header-resolution behavior.
            double headerProjection =
                (branch.HeaderConnectorOrigin -
                 branch.HeaderAxisStart)
                .DotProduct(headerAxisDirection);

            double endTolerance = Math.Max(
                2.0 / FeetToMillimetres,
                branch.HeaderDimensions.OutsideDiameter * 0.02);

            if (headerProjection < -endTolerance ||
                headerProjection >
                branch.HeaderAxisLength + endTolerance)
            {
                error =
                    "The SET-ON branch attachment projects outside " +
                    "the physical header-pipe length.";

                return false;
            }

            headerAxisPoint =
                branch.HeaderAxisStart +
                (headerAxisDirection * headerProjection);

            XYZ surfaceToAxis =
                headerAxisPoint -
                branch.HeaderConnectorOrigin;

            if (surfaceToAxis.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The SET-ON branch attachment is located on the " +
                    "header centreline instead of its outer surface.";

                return false;
            }

            radialInwardDirection =
                surfaceToAxis.Normalize();

            // Snap the attachment point to the analytical outer cylinder.
            // Spatially resolved family connectors can be several millimetres
            // away from the physical pipe face. Using that raw point makes the
            // cutter depth dependent on header size and can produce either a
            // remaining membrane or an opposite-wall breakout.
            resolvedSurfaceOrigin =
                headerAxisPoint -
                (radialInwardDirection *
                 (branch.HeaderDimensions.OutsideDiameter / 2.0));

            XYZ axisCandidate = null;

            if (branch.OutletConnectorOrigin != null)
            {
                XYZ outletToSurface =
                    resolvedSurfaceOrigin -
                    branch.OutletConnectorOrigin;

                if (outletToSurface.GetLength() >
                    GeometryTolerance)
                {
                    axisCandidate =
                        outletToSurface.Normalize();
                }
            }

            if (axisCandidate == null &&
                branch.HeaderInwardDirection != null &&
                branch.HeaderInwardDirection.GetLength() >
                    GeometryTolerance)
            {
                axisCandidate =
                    branch.HeaderInwardDirection.Normalize();
            }

            if (axisCandidate == null)
            {
                error =
                    "The SET-ON branch centre axis could not be resolved.";

                return false;
            }

            if (axisCandidate.DotProduct(
                    radialInwardDirection) < 0)
            {
                axisCandidate = -axisCandidate;
            }

            // Reject a nearly tangential branch axis. It cannot produce a
            // controlled SET-ON opening through the local header wall.
            if (axisCandidate.DotProduct(
                    radialInwardDirection) < 0.10)
            {
                error =
                    "The SET-ON branch axis is nearly tangential to the " +
                    "header pipe. Verify the branch position and connector " +
                    "orientation.";

                return false;
            }

            branchInwardAxis = axisCandidate.Normalize();
            return true;
        }

        private static bool
            TryCreateStandaloneShapedBranchBoreCutter(
                Element fitting,
                ShapedBranchConnection branch,
                out Solid cutter,
                out string error)
        {
            cutter = null;
            error = null;

            if (fitting == null ||
                branch == null ||
                branch.BranchDimensions == null ||
                branch.OutletConnectorOrigin == null)
            {
                error =
                    "The standalone shaped-branch bore information is " +
                    "missing.";

                return false;
            }

            double boreDiameter =
                branch.BranchDimensions.InsideDiameter;

            if (boreDiameter <= GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch bore diameter is zero " +
                    "or invalid.";

                return false;
            }

            XYZ inwardAxis =
                branch.OutletInwardDirection;

            if (inwardAxis == null ||
                inwardAxis.GetLength() <= GeometryTolerance)
            {
                XYZ fittingCenter =
                    GetElementCenter(fitting);

                if (fittingCenter == null)
                {
                    error =
                        "The standalone shaped-branch bore axis could not " +
                        "be resolved.";

                    return false;
                }

                inwardAxis =
                    fittingCenter -
                    branch.OutletConnectorOrigin;
            }

            if (inwardAxis.GetLength() <= GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch bore axis has zero or " +
                    "invalid length.";

                return false;
            }

            inwardAxis =
                inwardAxis.Normalize();

            XYZ centerPoint =
                GetElementCenter(fitting);

            if (centerPoint != null)
            {
                XYZ outletToCenter =
                    centerPoint -
                    branch.OutletConnectorOrigin;

                if (outletToCenter.GetLength() >
                        GeometryTolerance &&
                    inwardAxis.DotProduct(
                        outletToCenter) < 0)
                {
                    inwardAxis = -inwardAxis;
                }
            }

            double fittingExtent =
                GetElementExtent(fitting);

            double outletBackExtension = Math.Max(
                10.0 / FeetToMillimetres,
                branch.BranchDimensions.OutsideDiameter);

            XYZ cutterStart =
                branch.OutletConnectorOrigin -
                (inwardAxis * outletBackExtension);

            // The cutter deliberately continues beyond the opposite side of
            // the fitting. It is subtracted only from the selected shaped
            // branch, so it opens the complete standalone flow path without
            // creating or modifying a header-pipe opening.
            double cutterLength =
                outletBackExtension +
                (fittingExtent * 2.0);

            double radialBooleanOverlap =
                0.05 / FeetToMillimetres;

            try
            {
                cutter = CreateCylinder(
                    cutterStart,
                    inwardAxis,
                    cutterLength,
                    (boreDiameter / 2.0) +
                    radialBooleanOverlap);

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty standalone " +
                        "shaped-branch bore cutter.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The standalone shaped-branch bore could not be " +
                    "generated: " + ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateShapedBranchFittingContinuityCutter(
                Element fitting,
                ShapedBranchConnection branch,
                out Solid cutter,
                out string error)
        {
            cutter = null;
            error = null;

            if (fitting == null ||
                branch == null ||
                branch.BranchDimensions == null ||
                branch.HeaderDimensions == null)
            {
                error =
                    "The shaped-branch fitting continuity geometry is missing.";

                return false;
            }

            XYZ surfaceOrigin;
            XYZ inwardAxis;
            XYZ radialInward;
            XYZ headerAxisPoint;

            if (!TryResolveSetOnBranchFrame(
                    branch,
                    out surfaceOrigin,
                    out inwardAxis,
                    out radialInward,
                    out headerAxisPoint,
                    out error))
            {
                return false;
            }

            double boreDiameter =
                branch.BranchDimensions.InsideDiameter;

            if (boreDiameter <= GeometryTolerance)
            {
                error =
                    "The shaped-branch fitting continuity bore diameter " +
                    "is zero or invalid.";

                return false;
            }

            double outsideOverlap = Math.Max(
                5.0 / FeetToMillimetres,
                BooleanExtensionMillimetres /
                FeetToMillimetres);

            XYZ cutterStart;
            double distanceToSurface;

            if (branch.OutletConnectorOrigin != null)
            {
                cutterStart =
                    branch.OutletConnectorOrigin -
                    (inwardAxis * outsideOverlap);

                distanceToSurface =
                    (surfaceOrigin -
                     branch.OutletConnectorOrigin)
                    .DotProduct(inwardAxis);

                if (distanceToSurface <=
                    GeometryTolerance)
                {
                    distanceToSurface =
                        branch.OutletConnectorOrigin.DistanceTo(
                            surfaceOrigin);
                }
            }
            else
            {
                double fittingExtent =
                    GetElementExtent(fitting);

                double branchSideExtension = Math.Max(
                    10.0 / FeetToMillimetres,
                    Math.Min(
                        fittingExtent,
                        branch.BranchDimensions.OutsideDiameter * 2.0));

                cutterStart =
                    surfaceOrigin -
                    (inwardAxis *
                     (branchSideExtension + outsideOverlap));

                distanceToSurface =
                    branchSideExtension;
            }

            // This cutter is applied only to the shaped-branch family solid,
            // never to the header pipe. It can safely continue beyond the
            // saddle so the family cannot retain a circular diaphragm at the
            // connection face.
            double headerSideExtension = Math.Max(
                branch.HeaderDimensions.OutsideDiameter,
                branch.BranchDimensions.OutsideDiameter * 2.0);

            double cutterLength =
                outsideOverlap +
                distanceToSurface +
                headerSideExtension;

            if (cutterLength <= GeometryTolerance)
            {
                error =
                    "The shaped-branch fitting continuity cutter length " +
                    "is zero or invalid.";

                return false;
            }

            // A tiny radial Boolean overlap prevents a coincident family face
            // from leaving a paper-thin membrane. This is computational
            // tolerance only; it is not fabrication clearance.
            double radialBooleanOverlap =
                0.05 / FeetToMillimetres;

            try
            {
                cutter = CreateCylinder(
                    cutterStart,
                    inwardAxis,
                    cutterLength,
                    (boreDiameter / 2.0) +
                    radialBooleanOverlap);

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty shaped-branch fitting " +
                        "continuity cutter.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The shaped-branch fitting bore could not be extended " +
                    "through the saddle into the header bore: " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateShapedBranchPostTrimBoreCleanupCutter(
                Element fitting,
                ShapedBranchConnection branch,
                out Solid cutter,
                out string error)
        {
            cutter = null;
            error = null;

            if (fitting == null ||
                branch == null ||
                branch.BranchDimensions == null ||
                branch.HeaderDimensions == null)
            {
                error =
                    "The shaped-branch post-trim bore cleanup " +
                    "information is missing.";

                return false;
            }

            XYZ surfaceOrigin;
            XYZ inwardAxis;
            XYZ radialInward;
            XYZ headerAxisPoint;

            if (!TryResolveSetOnBranchFrame(
                    branch,
                    out surfaceOrigin,
                    out inwardAxis,
                    out radialInward,
                    out headerAxisPoint,
                    out error))
            {
                return false;
            }

            double boreDiameter =
                branch.BranchDimensions.InsideDiameter;

            if (boreDiameter <= GeometryTolerance)
            {
                error =
                    "The shaped-branch post-trim bore diameter " +
                    "is zero or invalid.";

                return false;
            }

            // Start behind the physical outlet face so the complete neck,
            // saddle, and header-side throat are cleaned in one coaxial pass.
            double fittingExtent =
                GetElementExtent(fitting);

            double outletBackExtension = Math.Max(
                10.0 / FeetToMillimetres,
                Math.Min(
                    fittingExtent,
                    branch.BranchDimensions
                        .OutsideDiameter * 1.50));

            XYZ cutterStart;
            double distanceToSurface;

            if (branch.OutletConnectorOrigin != null)
            {
                cutterStart =
                    branch.OutletConnectorOrigin -
                    (inwardAxis * outletBackExtension);

                distanceToSurface =
                    (surfaceOrigin -
                     branch.OutletConnectorOrigin)
                    .DotProduct(inwardAxis);

                if (distanceToSurface <=
                    GeometryTolerance)
                {
                    distanceToSurface =
                        branch.OutletConnectorOrigin.DistanceTo(
                            surfaceOrigin);
                }
            }
            else
            {
                cutterStart =
                    surfaceOrigin -
                    (inwardAxis * outletBackExtension);

                distanceToSurface =
                    outletBackExtension;
            }

            // Continue far enough beyond the saddle to make any fitting solid
            // inside the intended branch flow path unreachable. The cutter is
            // subtracted only from the shaped-branch fitting, never the header.
            double headerSideExtension = Math.Max(
                branch.HeaderDimensions.OutsideDiameter,
                branch.BranchDimensions.OutsideDiameter * 2.0);

            double cutterLength =
                outletBackExtension +
                distanceToSurface +
                headerSideExtension;

            if (cutterLength <= GeometryTolerance)
            {
                error =
                    "The shaped-branch post-trim bore cleanup " +
                    "cutter length is zero or invalid.";

                return false;
            }

            // Use a slightly stronger Boolean overlap for this final cleanup
            // pass. It is applied only around the intended ID and does not
            // change the fitting's outside profile or header opening size.
            double radialBooleanCleanup =
                0.25 / FeetToMillimetres;

            try
            {
                cutter = CreateCylinder(
                    cutterStart,
                    inwardAxis,
                    cutterLength,
                    (boreDiameter / 2.0) +
                    radialBooleanCleanup);

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty shaped-branch " +
                        "post-trim bore cleanup cutter.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The shaped-branch bore could not be cleaned " +
                    "after trimming it to the header surface: " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateProceduralStandaloneShapedBranchSolid(
                Element fitting,
                ShapedBranchConnection branch,
                ConnectorBore outletBore,
                out Solid solid,
                out string description,
                out string error)
        {
            solid = null;
            description = null;
            error = null;

            if (fitting == null ||
                branch == null ||
                !branch.IsStandaloneComponent ||
                branch.BranchDimensions == null ||
                branch.OutletConnectorOrigin == null)
            {
                error =
                    "The procedural standalone shaped-branch information " +
                    "is missing.";

                return false;
            }

            double outsideRadius =
                branch.BranchDimensions.OutsideDiameter / 2.0;

            double insideRadius =
                branch.BranchDimensions.InsideDiameter / 2.0;

            if (outsideRadius <= GeometryTolerance ||
                insideRadius <= GeometryTolerance ||
                outsideRadius <=
                    insideRadius + GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch OD/ID is invalid.";

                return false;
            }

            XYZ axis = branch.OutletInwardDirection;

            if (axis == null ||
                axis.GetLength() <= GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch axis could not be resolved.";

                return false;
            }

            axis = axis.Normalize();

            XYZ outletOrigin =
                branch.OutletConnectorOrigin;

            XYZ oppositeOrigin =
                branch.HeaderConnectorOrigin;

            double axialLength = oppositeOrigin == null
                ? 0.0
                : (oppositeOrigin - outletOrigin)
                    .DotProduct(axis);

            if (axialLength <= GeometryTolerance &&
                oppositeOrigin != null)
            {
                double reverseLength =
                    (oppositeOrigin - outletOrigin)
                        .DotProduct(-axis);

                if (reverseLength > GeometryTolerance)
                {
                    axis = -axis;
                    axialLength = reverseLength;
                }
            }

            if (axialLength <= GeometryTolerance)
            {
                axialLength = Math.Max(
                    GetElementExtent(fitting),
                    branch.BranchDimensions
                        .OutsideDiameter * 2.0);
            }

            if (axialLength <= GeometryTolerance)
            {
                error =
                    "The standalone shaped-branch length is zero or invalid.";

                return false;
            }

            oppositeOrigin =
                outletOrigin +
                (axis * axialLength);

            XYZ helper =
                Math.Abs(axis.Z) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;

            XYZ radialX =
                axis.CrossProduct(helper);

            if (radialX.GetLength() <= GeometryTolerance)
            {
                radialX =
                    axis.CrossProduct(XYZ.BasisY);
            }

            if (radialX.GetLength() <= GeometryTolerance)
            {
                error =
                    "A stable radial frame could not be created for the " +
                    "standalone shaped branch.";

                return false;
            }

            radialX = radialX.Normalize();

            XYZ radialY =
                axis.CrossProduct(radialX);

            if (radialY.GetLength() <= GeometryTolerance)
            {
                error =
                    "The secondary radial direction for the standalone " +
                    "shaped branch is invalid.";

                return false;
            }

            radialY = radialY.Normalize();

            double circumferenceMillimetres =
                2.0 *
                Math.PI *
                outsideRadius *
                FeetToMillimetres;

            int segmentCount =
                (int)Math.Ceiling(
                    circumferenceMillimetres / 2.0);

            segmentCount = Math.Max(
                96,
                Math.Min(
                    192,
                    segmentCount));

            if ((segmentCount % 2) != 0)
                segmentCount++;

            bool outletShouldChamfer =
                outletBore != null &&
                outletBore.ShouldChamfer;

            double rootFace =
                outletBore != null &&
                outletBore.RootFaceMillimetres > 0
                    ? outletBore.RootFaceMillimetres /
                      FeetToMillimetres
                    : ChamferRootFaceMillimetres /
                      FeetToMillimetres;

            double outletRootRadius =
                insideRadius + rootFace;

            double outletBevelDepth = 0.0;

            if (outletShouldChamfer)
            {
                double radialBevel =
                    outsideRadius - outletRootRadius;

                if (radialBevel <= GeometryTolerance)
                {
                    error =
                        "The standalone shaped-branch outlet wall cannot " +
                        "support its configured weld land.";

                    return false;
                }

                outletBevelDepth =
                    radialBevel *
                    Math.Tan(
                        ChamferAngleDegrees *
                        Math.PI / 180.0);
            }

            List<XYZ> outletOuter =
                new List<XYZ>(segmentCount);

            List<XYZ> outletRoot =
                new List<XYZ>(segmentCount);

            List<XYZ> outletInner =
                new List<XYZ>(segmentCount);

            List<XYZ> oppositeOuter =
                new List<XYZ>(segmentCount);

            List<XYZ> oppositeInner =
                new List<XYZ>(segmentCount);

            for (int index = 0;
                 index < segmentCount;
                 index++)
            {
                double angle =
                    2.0 *
                    Math.PI *
                    index /
                    segmentCount;

                XYZ radialDirection =
                    ((radialX * Math.Cos(angle)) +
                     (radialY * Math.Sin(angle)))
                    .Normalize();

                outletOuter.Add(
                    outletOrigin +
                    (radialDirection * outsideRadius) +
                    (axis * outletBevelDepth));

                outletRoot.Add(
                    outletOrigin +
                    (radialDirection * outletRootRadius));

                outletInner.Add(
                    outletOrigin +
                    (radialDirection * insideRadius));

                oppositeOuter.Add(
                    oppositeOrigin +
                    (radialDirection * outsideRadius));

                oppositeInner.Add(
                    oppositeOrigin +
                    (radialDirection * insideRadius));
            }

            TessellatedShapeBuilder builder =
                new TessellatedShapeBuilder();

            builder.OpenConnectedFaceSet(true);

            try
            {
                for (int index = 0;
                     index < segmentCount;
                     index++)
                {
                    int next =
                        (index + 1) % segmentCount;

                    // Outside cylindrical wall.
                    AddProceduralBranchQuad(
                        builder,
                        outletOuter[index],
                        outletOuter[next],
                        oppositeOuter[next],
                        oppositeOuter[index]);

                    // Inside straight-through bore.
                    AddProceduralBranchQuad(
                        builder,
                        outletInner[index],
                        oppositeInner[index],
                        oppositeInner[next],
                        outletInner[next]);

                    // Plain opposite annulus. No header-specific saddle or
                    // saddle bevel is invented when no header exists.
                    AddProceduralBranchQuad(
                        builder,
                        oppositeOuter[index],
                        oppositeOuter[next],
                        oppositeInner[next],
                        oppositeInner[index]);

                    if (outletShouldChamfer)
                    {
                        AddProceduralBranchQuad(
                            builder,
                            outletOuter[index],
                            outletRoot[index],
                            outletRoot[next],
                            outletOuter[next]);

                        AddProceduralBranchQuad(
                            builder,
                            outletRoot[index],
                            outletInner[index],
                            outletInner[next],
                            outletRoot[next]);
                    }
                    else
                    {
                        AddProceduralBranchQuad(
                            builder,
                            outletOuter[index],
                            outletInner[index],
                            outletInner[next],
                            outletOuter[next]);
                    }
                }

                builder.CloseConnectedFaceSet();
                builder.Target =
                    TessellatedShapeBuilderTarget.Solid;
                builder.Fallback =
                    TessellatedShapeBuilderFallback.Abort;
                builder.Build();

                solid = builder
                    .GetBuildResult()
                    .GetGeometricalObjects()
                    .OfType<Solid>()
                    .FirstOrDefault(x =>
                        x != null &&
                        x.Volume > GeometryTolerance);

                if (solid == null)
                {
                    error =
                        "Revit did not return a valid solid for the " +
                        "procedural standalone shaped branch.";

                    return false;
                }

                description =
                    "Standalone shaped branch rebuilt as one watertight " +
                    "plain hollow component with a straight-through bore; " +
                    "no header-specific saddle or saddle bevel generated; " +
                    "perimeter samples " +
                    segmentCount.ToString(
                        CultureInfo.InvariantCulture);

                if (outletShouldChamfer)
                {
                    description +=
                        "; branch outlet chamfer retained";
                }
                else
                {
                    description +=
                        "; branch outlet retained plain";
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The procedural standalone shaped branch could not be " +
                    "generated: " + ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateSingleBodySmoothSetOnShapedBranchGeometry(
                ShapedBranchConnection branch,
                ConnectorBore outletBore,
                double shortCurveTolerance,
                out List<GeometryObject> geometry,
                out int maximumExpectedStepFaceCount,
                out string description,
                out string error)
        {
            geometry = new List<GeometryObject>();
            maximumExpectedStepFaceCount = 0;
            description = null;
            error = null;

            if (branch == null ||
                branch.IsStandaloneComponent ||
                branch.HeaderDimensions == null ||
                branch.BranchDimensions == null ||
                branch.OutletConnectorOrigin == null)
            {
                error =
                    "The smooth SET-ON shaped-branch information is missing.";

                return false;
            }

            if (shortCurveTolerance <= 0)
            {
                error =
                    "Revit's short-curve tolerance is unavailable for the " +
                    "smooth SET-ON branch calculation.";

                return false;
            }

            XYZ surfaceOrigin;
            XYZ inwardAxis;
            XYZ radialInward;
            XYZ headerAxisPoint;

            if (!TryResolveSetOnBranchFrame(
                    branch,
                    out surfaceOrigin,
                    out inwardAxis,
                    out radialInward,
                    out headerAxisPoint,
                    out error))
            {
                return false;
            }

            XYZ branchAxis =
                inwardAxis.Normalize();

            XYZ outletOrigin =
                branch.OutletConnectorOrigin;

            double outletToHeaderDistance =
                (surfaceOrigin - outletOrigin)
                .DotProduct(branchAxis);

            if (outletToHeaderDistance <=
                GeometryTolerance)
            {
                error =
                    "The shaped-branch outlet is not located behind the " +
                    "resolved SET-ON header surface.";

                return false;
            }

            double outsideRadius =
                branch.BranchDimensions.OutsideDiameter / 2.0;

            double insideRadius =
                branch.BranchDimensions.InsideDiameter / 2.0;

            double rootFace =
                ChamferRootFaceMillimetres /
                FeetToMillimetres;

            double saddleRootRadius =
                insideRadius + rootFace;

            double radialBevel =
                outsideRadius - saddleRootRadius;

            if (outsideRadius <= GeometryTolerance ||
                insideRadius <= GeometryTolerance ||
                radialBevel <= GeometryTolerance)
            {
                error =
                    "The shaped-branch OD/ID cannot support the configured " +
                    ChamferRootFaceMillimetres.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm SET-ON weld land.";

                return false;
            }

            double angleRadians =
                ChamferAngleDegrees *
                Math.PI / 180.0;

            double saddleBevelDepth =
                radialBevel *
                Math.Tan(angleRadians);

            if (saddleBevelDepth <=
                GeometryTolerance)
            {
                error =
                    "The calculated SET-ON saddle-bevel depth is invalid.";

                return false;
            }

            XYZ headerAxis =
                branch.HeaderAxisDirection.Normalize();

            XYZ radialX =
                headerAxis -
                (branchAxis *
                 headerAxis.DotProduct(branchAxis));

            if (radialX.GetLength() <=
                GeometryTolerance)
            {
                XYZ helper =
                    Math.Abs(branchAxis.Z) < 0.90
                        ? XYZ.BasisZ
                        : XYZ.BasisX;

                radialX =
                    branchAxis.CrossProduct(helper);
            }

            if (radialX.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "A stable radial frame could not be created for the " +
                    "smooth BRep SET-ON shaped branch.";

                return false;
            }

            radialX = radialX.Normalize();

            XYZ radialY =
                branchAxis.CrossProduct(radialX);

            if (radialY.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The secondary radial direction for the hybrid SET-ON " +
                    "shaped branch is invalid.";

                return false;
            }

            radialY = radialY.Normalize();

            double circumferenceMillimetres =
                2.0 *
                Math.PI *
                outsideRadius *
                FeetToMillimetres;

            int segmentCount =
                (int)Math.Ceiling(
                    circumferenceMillimetres / 2.0);

            const int smoothSaddleSampleMultiple = 16;

            segmentCount = Math.Max(
                128,
                Math.Min(
                    256,
                    segmentCount));

            segmentCount =
                ((segmentCount +
                  smoothSaddleSampleMultiple -
                  1) /
                 smoothSaddleSampleMultiple) *
                smoothSaddleSampleMultiple;

            segmentCount = Math.Min(
                256,
                segmentCount);

            bool outletShouldChamfer =
                outletBore != null &&
                outletBore.ShouldChamfer;

            double outletRootFace =
                outletBore != null &&
                outletBore.RootFaceMillimetres > 0
                    ? outletBore.RootFaceMillimetres /
                      FeetToMillimetres
                    : rootFace;

            double outletRootRadius =
                insideRadius + outletRootFace;

            double outletBevelDepth = 0.0;

            if (outletShouldChamfer)
            {
                double outletRadialBevel =
                    outsideRadius -
                    outletRootRadius;

                if (outletRadialBevel <=
                    GeometryTolerance)
                {
                    error =
                        "The shaped-branch outlet wall cannot support its " +
                        "configured weld land.";

                    return false;
                }

                outletBevelDepth =
                    outletRadialBevel *
                    Math.Tan(angleRadians);
            }

            List<XYZ> saddleOuter =
                new List<XYZ>(segmentCount);

            List<XYZ> saddleRoot =
                new List<XYZ>(segmentCount);

            List<XYZ> saddleInner =
                new List<XYZ>(segmentCount);

            for (int index = 0;
                 index < segmentCount;
                 index++)
            {
                double angle =
                    2.0 *
                    Math.PI *
                    index /
                    segmentCount;

                XYZ radialDirection =
                    ((radialX * Math.Cos(angle)) +
                     (radialY * Math.Sin(angle)))
                    .Normalize();

                XYZ saddleRootContactPoint;
                XYZ saddleInnerContactPoint;

                if (!TryResolveSetOnSaddlePoint(
                        branch,
                        surfaceOrigin,
                        branchAxis,
                        radialDirection,
                        saddleRootRadius,
                        out saddleRootContactPoint) ||
                    !TryResolveSetOnSaddlePoint(
                        branch,
                        surfaceOrigin,
                        branchAxis,
                        radialDirection,
                        insideRadius,
                        out saddleInnerContactPoint))
                {
                    error =
                        "The exact branch-root/header and branch-ID/header " +
                        "intersections could not be resolved around the " +
                        "complete SET-ON saddle.";

                    return false;
                }

                // Keep the ID and 1 mm land against the header. Set the OD
                // backward so the 30-degree weld preparation is visible from
                // outside the branch.
                XYZ outerSaddlePoint =
                    saddleRootContactPoint +
                    (radialDirection *
                     (outsideRadius -
                      saddleRootRadius)) -
                    (branchAxis *
                     saddleBevelDepth);

                saddleOuter.Add(
                    outerSaddlePoint);

                saddleRoot.Add(
                    saddleRootContactPoint);

                saddleInner.Add(
                    saddleInnerContactPoint);
            }

            double nearestSaddleDistance =
                saddleOuter
                    .Concat(saddleRoot)
                    .Concat(saddleInner)
                    .Select(x =>
                        (x - outletOrigin)
                        .DotProduct(branchAxis))
                    .Min();

            double minimumOutletBodyDistance =
                Math.Max(
                    4.0 / FeetToMillimetres,
                    outletBevelDepth +
                    (3.0 / FeetToMillimetres));

            double minimumControlSpacing =
                Math.Max(
                    shortCurveTolerance * 1.35,
                    1.0 / FeetToMillimetres);

            double minimumCollarLength =
                minimumControlSpacing * 3.0;

            double targetCollarLength =
                Math.Max(
                    Math.Max(
                        12.0 / FeetToMillimetres,
                        saddleBevelDepth +
                        (6.0 / FeetToMillimetres)),
                    minimumCollarLength);

            double collarStartDistance =
                nearestSaddleDistance -
                targetCollarLength;

            collarStartDistance = Math.Max(
                minimumOutletBodyDistance,
                collarStartDistance);

            double availableCollarLength =
                nearestSaddleDistance -
                collarStartDistance;

            if (availableCollarLength <=
                minimumCollarLength)
            {
                error =
                    "The shaped branch does not have enough straight length " +
                    "to create tolerance-safe smooth BRep transition curves. " +
                    "Available transition length: " +
                    (availableCollarLength * FeetToMillimetres).ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm; required: more than " +
                    (minimumCollarLength * FeetToMillimetres).ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            double transitionControlSpacing =
                Math.Min(
                    2.0 / FeetToMillimetres,
                    availableCollarLength / 3.0);

            if (transitionControlSpacing <=
                shortCurveTolerance * 1.05)
            {
                error =
                    "The smooth branch transition control spacing is below " +
                    "Revit's short-curve tolerance.";

                return false;
            }

            double blendLength =
                transitionControlSpacing;

            double overlapLength =
                transitionControlSpacing;

            double fullRadiusDistance =
                collarStartDistance +
                blendLength;

            double tubeEndDistance =
                fullRadiusDistance +
                overlapLength;

            XYZ collarStartOrigin =
                outletOrigin +
                (branchAxis *
                 collarStartDistance);

            XYZ fullRadiusOrigin =
                outletOrigin +
                (branchAxis *
                 fullRadiusDistance);

            XYZ tubeEndOrigin =
                outletOrigin +
                (branchAxis *
                 tubeEndDistance);

            List<XYZ> collarStartOuter =
                CreateHybridBranchRing(
                    collarStartOrigin,
                    radialX,
                    radialY,
                    outsideRadius,
                    segmentCount);

            List<XYZ> collarStartInner =
                CreateHybridBranchRing(
                    collarStartOrigin,
                    radialX,
                    radialY,
                    insideRadius,
                    segmentCount);

            List<XYZ> fullRadiusOuter =
                CreateHybridBranchRing(
                    fullRadiusOrigin,
                    radialX,
                    radialY,
                    outsideRadius,
                    segmentCount);

            List<XYZ> fullRadiusInner =
                CreateHybridBranchRing(
                    fullRadiusOrigin,
                    radialX,
                    radialY,
                    insideRadius,
                    segmentCount);

            List<XYZ> tubeEndOuter =
                CreateHybridBranchRing(
                    tubeEndOrigin,
                    radialX,
                    radialY,
                    outsideRadius,
                    segmentCount);

            List<XYZ> tubeEndInner =
                CreateHybridBranchRing(
                    tubeEndOrigin,
                    radialX,
                    radialY,
                    insideRadius,
                    segmentCount);

            XYZ outletOuterOrigin =
                outletOrigin +
                (branchAxis *
                 outletBevelDepth);

            List<XYZ> outletOuter =
                CreateHybridBranchRing(
                    outletOuterOrigin,
                    radialX,
                    radialY,
                    outsideRadius,
                    segmentCount);

            List<XYZ> outletInner =
                CreateHybridBranchRing(
                    outletOrigin,
                    radialX,
                    radialY,
                    insideRadius,
                    segmentCount);

            List<XYZ> outletRoot = null;

            if (outletShouldChamfer)
            {
                outletRoot =
                    CreateHybridBranchRing(
                        outletOrigin,
                        radialX,
                        radialY,
                        outletRootRadius,
                        segmentCount);
            }

            Transform localToWorld =
                Transform.Identity;

            localToWorld.Origin =
                outletOrigin;

            localToWorld.BasisX =
                radialX;

            localToWorld.BasisY =
                radialY;

            localToWorld.BasisZ =
                branchAxis;

            List<XYZ> localOutletOuter =
                ConvertBranchRingToLocal(
                    outletOuter,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localOutletRoot =
                outletRoot == null
                    ? null
                    : ConvertBranchRingToLocal(
                        outletRoot,
                        outletOrigin,
                        radialX,
                        radialY,
                        branchAxis);

            List<XYZ> localOutletInner =
                ConvertBranchRingToLocal(
                    outletInner,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localCollarStartOuter =
                ConvertBranchRingToLocal(
                    collarStartOuter,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localCollarStartInner =
                ConvertBranchRingToLocal(
                    collarStartInner,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localFullRadiusOuter =
                ConvertBranchRingToLocal(
                    fullRadiusOuter,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localFullRadiusInner =
                ConvertBranchRingToLocal(
                    fullRadiusInner,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localTubeEndOuter =
                ConvertBranchRingToLocal(
                    tubeEndOuter,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localTubeEndInner =
                ConvertBranchRingToLocal(
                    tubeEndInner,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localSaddleOuter =
                ConvertBranchRingToLocal(
                    saddleOuter,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localSaddleRoot =
                ConvertBranchRingToLocal(
                    saddleRoot,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            List<XYZ> localSaddleInner =
                ConvertBranchRingToLocal(
                    saddleInner,
                    outletOrigin,
                    radialX,
                    radialY,
                    branchAxis);

            XYZ headerAxisOffset =
                headerAxisPoint -
                outletOrigin;

            XYZ localHeaderAxisPoint =
                new XYZ(
                    headerAxisOffset.DotProduct(
                        radialX),
                    headerAxisOffset.DotProduct(
                        radialY),
                    headerAxisOffset.DotProduct(
                        branchAxis));

            XYZ localHeaderAxisDirection =
                new XYZ(
                    headerAxis.DotProduct(
                        radialX),
                    headerAxis.DotProduct(
                        radialY),
                    headerAxis.DotProduct(
                        branchAxis));

            if (localHeaderAxisDirection.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The SET-ON header axis could not be transformed into " +
                    "the branch-local coordinate frame.";

                return false;
            }

            localHeaderAxisDirection =
                localHeaderAxisDirection.Normalize();

            if (activeBranchDiagnosticProbe != null)
            {
                DiagnosticCaptureBranchGeometryInputs(
                    branch,
                    outletBore,
                    surfaceOrigin,
                    inwardAxis,
                    radialInward,
                    headerAxisPoint,
                    branchAxis,
                    radialX,
                    radialY,
                    headerAxis,
                    outletOrigin,
                    localHeaderAxisPoint,
                    localHeaderAxisDirection,
                    segmentCount,
                    outsideRadius,
                    insideRadius,
                    saddleRootRadius,
                    radialBevel,
                    saddleBevelDepth,
                    outletToHeaderDistance,
                    outletShouldChamfer,
                    outletRootFace,
                    outletRootRadius,
                    outletBevelDepth,
                    shortCurveTolerance,
                    new Dictionary<string, IList<XYZ>>
                    {
                        { "outletOuter", outletOuter },
                        { "outletRoot", outletRoot },
                        { "outletInner", outletInner },
                        { "collarStartOuter", collarStartOuter },
                        { "collarStartInner", collarStartInner },
                        { "fullRadiusOuter", fullRadiusOuter },
                        { "fullRadiusInner", fullRadiusInner },
                        { "tubeEndOuter", tubeEndOuter },
                        { "tubeEndInner", tubeEndInner },
                        { "saddleOuter", saddleOuter },
                        { "saddleRoot", saddleRoot },
                        { "saddleInner", saddleInner }
                    },
                    new Dictionary<string, IList<XYZ>>
                    {
                        { "outletOuter", localOutletOuter },
                        { "outletRoot", localOutletRoot },
                        { "outletInner", localOutletInner },
                        { "collarStartOuter", localCollarStartOuter },
                        { "collarStartInner", localCollarStartInner },
                        { "fullRadiusOuter", localFullRadiusOuter },
                        { "fullRadiusInner", localFullRadiusInner },
                        { "tubeEndOuter", localTubeEndOuter },
                        { "tubeEndInner", localTubeEndInner },
                        { "saddleOuter", localSaddleOuter },
                        { "saddleRoot", localSaddleRoot },
                        { "saddleInner", localSaddleInner }
                    });
            }

            Solid localSingleBody;
            int topologicalPatchCount;
            int surfaceBandCount;
            int expectedStepFaceCount;
            int circumferentialSplineSpanCount;
            bool usedAnalyticStraightCylinders;
            bool usedSeamFreeAnalyticCylinders;
            bool usedContinuousTopSurfaces;
            bool usedMergedSmoothBodyBands;
            bool usedAdaptiveTessellatedFallback;

            if (!TryCreateSingleBodySmoothBRepSetOnBranch(
                    localOutletOuter,
                    localOutletRoot,
                    localOutletInner,
                    localCollarStartOuter,
                    localCollarStartInner,
                    localFullRadiusOuter,
                    localFullRadiusInner,
                    localTubeEndOuter,
                    localTubeEndInner,
                    localSaddleOuter,
                    localSaddleRoot,
                    localSaddleInner,
                    localHeaderAxisPoint,
                    localHeaderAxisDirection,
                    branch.HeaderDimensions
                        .OutsideDiameter / 2.0,
                    outletShouldChamfer,
                    shortCurveTolerance,
                    out localSingleBody,
                    out topologicalPatchCount,
                    out surfaceBandCount,
                    out expectedStepFaceCount,
                    out circumferentialSplineSpanCount,
                    out usedAnalyticStraightCylinders,
                    out usedSeamFreeAnalyticCylinders,
                    out usedContinuousTopSurfaces,
                    out usedMergedSmoothBodyBands,
                    out usedAdaptiveTessellatedFallback,
                    out error))
            {
                return false;
            }

            if (activeBranchDiagnosticProbe != null)
            {
                DiagnosticCaptureTopologyResult(
                    topologicalPatchCount,
                    surfaceBandCount,
                    expectedStepFaceCount,
                    circumferentialSplineSpanCount,
                    usedAnalyticStraightCylinders,
                    usedSeamFreeAnalyticCylinders,
                    usedContinuousTopSurfaces,
                    usedMergedSmoothBodyBands,
                    usedAdaptiveTessellatedFallback);
            }

            Solid singleBody;

            try
            {
                singleBody =
                    SolidUtils.CreateTransformed(
                        localSingleBody,
                        localToWorld);
            }
            catch (Exception ex)
            {
                error =
                    "The local smooth SET-ON branch could not be transformed " +
                    "back into model coordinates: " +
                    ex.Message;

                return false;
            }

            if (singleBody == null ||
                singleBody.Volume <=
                    GeometryTolerance)
            {
                error =
                    "Revit returned an empty single-body SET-ON shaped branch.";

                return false;
            }

            geometry.Add(singleBody);

            maximumExpectedStepFaceCount =
                expectedStepFaceCount;

            description =
                (usedAdaptiveTessellatedFallback
                    ? "Single-body adaptive watertight SET-ON shaped branch fallback; branch OD, "
                    : "Single-body smooth BRep SET-ON shaped branch; branch OD, ") +
                "branch bore, saddle, external " +
                ChamferAngleDegrees.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " degree bevel, and " +
                ChamferRootFaceMillimetres.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm weld land are contained in one watertight solid; " +
                (usedAdaptiveTessellatedFallback
                    ? "adaptive tessellation was used only because one or more physical feature edges are below Revit's BRep short-curve tolerance; "
                    : "BRep generated in branch-local coordinates with tolerance-safe boundary curves; ") +
                surfaceBandCount.ToString(
                    CultureInfo.InvariantCulture) +
                " circumferential surface bands; " +
                (usedAdaptiveTessellatedFallback
                    ? "adaptive triangulated faces preserve the resolved fabrication profile"
                    : usedContinuousTopSurfaces
                    ? (usedSeamFreeAnalyticCylinders
                        ? "the complete OD, bevel, weld land, and bore each use one continuous face"
                        : "the bevel and weld land each use one continuous face while the OD and bore use two analytic half-faces each")
                    : usedSeamFreeAnalyticCylinders
                        ? "the two complete cylindrical wall bands use one face each and the remaining bands use " +
                          topologicalPatchCount.ToString(
                              CultureInfo.InvariantCulture) +
                          " patches each"
                        : "each band uses " +
                          topologicalPatchCount.ToString(
                              CultureInfo.InvariantCulture) +
                          " topological faces") +
                ", for an expected STEP face count of " +
                expectedStepFaceCount.ToString(
                    CultureInfo.InvariantCulture) +
                (usedAdaptiveTessellatedFallback
                    ? "; adaptive tessellation stays inside the configured geometric deviation budget; "
                    : "; patched faces contain " +
                      circumferentialSplineSpanCount.ToString(
                          CultureInfo.InvariantCulture) +
                      " internal smooth C2 cubic B-spline spans; ") +
                (usedAdaptiveTessellatedFallback
                    ? "the compact BRep path remains preferred for every branch where Revit's curve tolerance allows it; "
                    : usedContinuousTopSurfaces
                    ? "the external saddle bevel uses one ruled face and the weld land uses one header-cylinder face; "
                    : usedSeamFreeAnalyticCylinders
                        ? "the complete OD and bore walls use one seam-free analytic cylindrical face each; "
                        : usedMergedSmoothBodyBands
                        ? "the straight OD/ID portions were C2-merged with their adjacent transitions; "
                        : usedAnalyticStraightCylinders
                            ? "the OD/ID portions use split analytic cylindrical surfaces; "
                            : "the straight OD/ID portions use compact smooth NURBS surfaces; ") +
                "header opening retained plain; saddle source samples " +
                segmentCount.ToString(
                    CultureInfo.InvariantCulture);

            if (outletShouldChamfer)
            {
                description +=
                    "; branch outlet chamfer retained in the same solid";
            }
            else
            {
                description +=
                    "; branch outlet retained plain in the same solid";
            }

            return true;
        }

        private static List<XYZ>
            CreateHybridBranchRing(
                XYZ origin,
                XYZ radialX,
                XYZ radialY,
                double radius,
                int segmentCount)
        {
            List<XYZ> ring =
                new List<XYZ>(segmentCount);

            for (int index = 0;
                 index < segmentCount;
                 index++)
            {
                double angle =
                    2.0 *
                    Math.PI *
                    index /
                    segmentCount;

                XYZ radialDirection =
                    ((radialX * Math.Cos(angle)) +
                     (radialY * Math.Sin(angle)))
                    .Normalize();

                ring.Add(
                    origin +
                    (radialDirection * radius));
            }

            return ring;
        }

        private static List<XYZ>
            ConvertBranchRingToLocal(
                IList<XYZ> worldRing,
                XYZ localOrigin,
                XYZ localX,
                XYZ localY,
                XYZ localZ)
        {
            if (worldRing == null)
                return null;

            List<XYZ> localRing =
                new List<XYZ>(worldRing.Count);

            foreach (XYZ worldPoint in worldRing)
            {
                XYZ delta =
                    worldPoint - localOrigin;

                localRing.Add(
                    new XYZ(
                        delta.DotProduct(localX),
                        delta.DotProduct(localY),
                        delta.DotProduct(localZ)));
            }

            return localRing;
        }

        private sealed class SmoothBRepPatchLayout
        {
            public int SplineSpanCount { get; set; }
            public int PatchStartOffset { get; set; }
            public double MinimumClearance { get; set; }
            public double MaximumDeviation { get; set; }
        }

        private static List<XYZ>
            CreateLinearBezierControlRing(
                IList<XYZ> startRing,
                IList<XYZ> endRing,
                double fraction)
        {
            if (startRing == null ||
                endRing == null ||
                startRing.Count == 0 ||
                endRing.Count != startRing.Count ||
                fraction < 0.0 ||
                fraction > 1.0)
            {
                throw new ArgumentException(
                    "The linear Bezier control-ring inputs are invalid.");
            }

            List<XYZ> result =
                new List<XYZ>(startRing.Count);

            for (int index = 0;
                 index < startRing.Count;
                 index++)
            {
                result.Add(
                    startRing[index] +
                    ((endRing[index] - startRing[index]) *
                     fraction));
            }

            return result;
        }

        private static bool
            TryCreateMergedC2CubicBandControlRings(
                IList<XYZ> firstStart,
                IList<XYZ> firstControlOne,
                IList<XYZ> firstControlTwo,
                IList<XYZ> firstEnd,
                IList<XYZ> secondStart,
                IList<XYZ> secondControlOne,
                IList<XYZ> secondControlTwo,
                IList<XYZ> secondEnd,
                double shortCurveTolerance,
                string context,
                out List<IList<XYZ>> mergedControlRings,
                out IList<double> knotsV,
                out string error)
        {
            mergedControlRings = null;
            knotsV = null;
            error = null;

            int sampleCount =
                firstStart == null
                    ? 0
                    : firstStart.Count;

            IList<XYZ>[] rings =
            {
                firstStart,
                firstControlOne,
                firstControlTwo,
                firstEnd,
                secondStart,
                secondControlOne,
                secondControlTwo,
                secondEnd
            };

            if (sampleCount < 8 ||
                shortCurveTolerance <= 0 ||
                rings.Any(x =>
                    x == null ||
                    x.Count != sampleCount))
            {
                error =
                    "The C2 merged surface-band inputs for " +
                    context +
                    " are incomplete.";

                return false;
            }

            double compatibilityTolerance =
                Math.Max(
                    GeometryTolerance * 100.0,
                    0.001 / FeetToMillimetres);

            double firstTangentMagnitude = 0.0;
            double secondTangentMagnitude = 0.0;

            for (int index = 0;
                 index < sampleCount;
                 index++)
            {
                if (firstEnd[index].DistanceTo(
                        secondStart[index]) >
                    compatibilityTolerance)
                {
                    error =
                        "The two source surfaces for " +
                        context +
                        " do not share an exact join ring.";

                    return false;
                }

                XYZ firstTangent =
                    (firstEnd[index] -
                     firstControlTwo[index]) * 3.0;

                XYZ secondTangent =
                    (secondControlOne[index] -
                     secondStart[index]) * 3.0;

                double firstLength =
                    firstTangent.GetLength();

                double secondLength =
                    secondTangent.GetLength();

                if (firstLength <= GeometryTolerance ||
                    secondLength <= GeometryTolerance ||
                    firstTangent.Normalize().DotProduct(
                        secondTangent.Normalize()) < 0.9999)
                {
                    error =
                        "The source surfaces for " +
                        context +
                        " are not tangent-compatible and cannot be merged " +
                        "without changing the fabrication profile.";

                    return false;
                }

                firstTangentMagnitude += firstLength;
                secondTangentMagnitude += secondLength;
            }

            firstTangentMagnitude /= sampleCount;
            secondTangentMagnitude /= sampleCount;

            double joinKnot =
                firstTangentMagnitude /
                (firstTangentMagnitude +
                 secondTangentMagnitude);

            if (joinKnot <= 0.02 ||
                joinKnot >= 0.98)
            {
                error =
                    "The C2 join parameter for " +
                    context +
                    " is too close to the end of the NURBS domain.";

                return false;
            }

            List<XYZ>[] controls =
            {
                new List<XYZ>(sampleCount),
                new List<XYZ>(sampleCount),
                new List<XYZ>(sampleCount),
                new List<XYZ>(sampleCount),
                new List<XYZ>(sampleCount)
            };

            double maximumCompatibilityGap = 0.0;

            for (int index = 0;
                 index < sampleCount;
                 index++)
            {
                XYZ p0 = firstStart[index];
                XYZ p1 = firstControlOne[index];
                XYZ p3 = secondControlTwo[index];
                XYZ p4 = secondEnd[index];

                XYZ p2FromFirst =
                    (firstControlTwo[index] -
                     (p1 * (1.0 - joinKnot))) /
                    joinKnot;

                XYZ p2FromSecond =
                    (secondControlOne[index] -
                     (p3 * joinKnot)) /
                    (1.0 - joinKnot);

                maximumCompatibilityGap =
                    Math.Max(
                        maximumCompatibilityGap,
                        p2FromFirst.DistanceTo(
                            p2FromSecond));

                XYZ p2 =
                    (p2FromFirst +
                     p2FromSecond) /
                    2.0;

                XYZ reconstructedFirstControlTwo =
                    (p1 * (1.0 - joinKnot)) +
                    (p2 * joinKnot);

                XYZ reconstructedSecondControlOne =
                    (p2 * (1.0 - joinKnot)) +
                    (p3 * joinKnot);

                XYZ reconstructedJoin =
                    (reconstructedFirstControlTwo *
                     (1.0 - joinKnot)) +
                    (reconstructedSecondControlOne *
                     joinKnot);

                maximumCompatibilityGap =
                    Math.Max(
                        maximumCompatibilityGap,
                        reconstructedFirstControlTwo.DistanceTo(
                            firstControlTwo[index]));

                maximumCompatibilityGap =
                    Math.Max(
                        maximumCompatibilityGap,
                        reconstructedSecondControlOne.DistanceTo(
                            secondControlOne[index]));

                maximumCompatibilityGap =
                    Math.Max(
                        maximumCompatibilityGap,
                        reconstructedJoin.DistanceTo(
                            firstEnd[index]));

                controls[0].Add(p0);
                controls[1].Add(p1);
                controls[2].Add(p2);
                controls[3].Add(p3);
                controls[4].Add(p4);
            }

            if (maximumCompatibilityGap >
                compatibilityTolerance)
            {
                error =
                    "The smooth source bands for " +
                    context +
                    " are not C2-compatible within tolerance. Maximum " +
                    "control-net mismatch: " +
                    (maximumCompatibilityGap * FeetToMillimetres)
                        .ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            mergedControlRings =
                controls
                    .Cast<IList<XYZ>>()
                    .ToList();

            knotsV =
                new List<double>
                {
                    0.0,
                    0.0,
                    0.0,
                    0.0,
                    joinKnot,
                    1.0,
                    1.0,
                    1.0,
                    1.0
                };

            return true;
        }

        private static bool
            TryCreateSingleBodySmoothBRepSetOnBranch(
                IList<XYZ> outletOuter,
                IList<XYZ> outletRoot,
                IList<XYZ> outletInner,
                IList<XYZ> collarStartOuter,
                IList<XYZ> collarStartInner,
                IList<XYZ> fullRadiusOuter,
                IList<XYZ> fullRadiusInner,
                IList<XYZ> tubeEndOuter,
                IList<XYZ> tubeEndInner,
                IList<XYZ> saddleOuter,
                IList<XYZ> saddleRoot,
                IList<XYZ> saddleInner,
                XYZ headerAxisPoint,
                XYZ headerAxisDirection,
                double headerOutsideRadius,
                bool outletShouldChamfer,
                double shortCurveTolerance,
                out Solid solid,
                out int topologicalPatchCount,
                out int surfaceBandCount,
                out int expectedStepFaceCount,
                out int circumferentialSplineSpanCount,
                out bool usedAnalyticStraightCylinders,
                out bool usedSeamFreeAnalyticCylinders,
                out bool usedContinuousTopSurfaces,
                out bool usedMergedSmoothBodyBands,
                out bool usedAdaptiveTessellatedFallback,
                out string error)
        {
            solid = null;
            topologicalPatchCount = 0;
            surfaceBandCount = 0;
            expectedStepFaceCount = 0;
            circumferentialSplineSpanCount = 0;
            usedAnalyticStraightCylinders = false;
            usedSeamFreeAnalyticCylinders = false;
            usedContinuousTopSurfaces = false;
            usedMergedSmoothBodyBands = false;
            usedAdaptiveTessellatedFallback = false;
            error = null;

            if (shortCurveTolerance <= 0)
            {
                error =
                    "Revit's short-curve tolerance is unavailable for the " +
                    "single-body BRep builder.";

                return false;
            }

            int sampleCount =
                outletOuter == null
                    ? 0
                    : outletOuter.Count;

            if (sampleCount < 32 ||
                outletInner == null ||
                collarStartOuter == null ||
                collarStartInner == null ||
                fullRadiusOuter == null ||
                fullRadiusInner == null ||
                tubeEndOuter == null ||
                tubeEndInner == null ||
                saddleOuter == null ||
                saddleRoot == null ||
                saddleInner == null ||
                outletInner.Count != sampleCount ||
                collarStartOuter.Count != sampleCount ||
                collarStartInner.Count != sampleCount ||
                fullRadiusOuter.Count != sampleCount ||
                fullRadiusInner.Count != sampleCount ||
                tubeEndOuter.Count != sampleCount ||
                tubeEndInner.Count != sampleCount ||
                saddleOuter.Count != sampleCount ||
                saddleRoot.Count != sampleCount ||
                saddleInner.Count != sampleCount ||
                (outletShouldChamfer &&
                 (outletRoot == null ||
                  outletRoot.Count != sampleCount)))
            {
                error =
                    "The single-body smooth SET-ON perimeter rings are " +
                    "incomplete.";

                return false;
            }

            const int minimumTopologicalPatchCount = 2;
            const int fallbackTopologicalPatchCount = 4;
            const int preferredSplineSpanCount = 8;
            const int fallbackSplineSpanCount = 16;

            if ((sampleCount % fallbackSplineSpanCount) != 0)
            {
                error =
                    "The single-body SET-ON sample count must be divisible " +
                    "by the maximum smooth BRep span count.";

                return false;
            }

            List<IList<XYZ>> ringSamples =
                new List<IList<XYZ>>
                {
                    outletOuter,
                    collarStartOuter,
                    saddleOuter,
                    saddleRoot,
                    saddleInner,
                    collarStartInner,
                    outletInner
                };

            int outletRootRingIndex = -1;

            if (outletShouldChamfer)
            {
                outletRootRingIndex =
                    ringSamples.Count;

                ringSamples.Add(
                    outletRoot);
            }

            List<string> attemptErrors =
                new List<string>();

            // Manufacturing-first topology. The complete outside wall and bore
            // are exact cylinders whose upper trimming curves happen to be
            // saddle-shaped. Keep those walls analytic instead of approximating
            // them with NURBS. Revit is first asked to represent each cylinder
            // as one face bounded only by its two circumferential loops. If that
            // periodic-face topology is rejected, retry with two analytic half
            // cylinders before falling back to the compact all-NURBS layouts.
            List<IList<XYZ>[]> manufacturingBandRows =
                new List<IList<XYZ>[]>
                {
                    new[]
                    {
                        outletOuter,
                        saddleOuter
                    },
                    new[]
                    {
                        saddleOuter,
                        saddleRoot
                    },
                    new[]
                    {
                        saddleRoot,
                        saddleInner
                    },
                    new[]
                    {
                        saddleInner,
                        outletInner
                    }
                };

            List<int> manufacturingLowerRingIndexes =
                new List<int>
                {
                    0,
                    2,
                    3,
                    4
                };

            List<int> manufacturingUpperRingIndexes =
                new List<int>
                {
                    2,
                    3,
                    4,
                    6
                };

            List<int> manufacturingDegreeV =
                new List<int>
                {
                    1,
                    1,
                    1,
                    1
                };

            List<IList<double>> manufacturingKnotsV =
                Enumerable
                    .Repeat<IList<double>>(
                        null,
                        manufacturingBandRows.Count)
                    .ToList();

            if (outletShouldChamfer)
            {
                manufacturingBandRows.Add(
                    new[]
                    {
                        outletInner,
                        outletRoot
                    });

                manufacturingLowerRingIndexes.Add(6);
                manufacturingUpperRingIndexes.Add(
                    outletRootRingIndex);
                manufacturingDegreeV.Add(1);
                manufacturingKnotsV.Add(null);

                manufacturingBandRows.Add(
                    new[]
                    {
                        outletRoot,
                        outletOuter
                    });

                manufacturingLowerRingIndexes.Add(
                    outletRootRingIndex);
                manufacturingUpperRingIndexes.Add(0);
                manufacturingDegreeV.Add(1);
                manufacturingKnotsV.Add(null);
            }

            // Only the bevel, weld-land, and optional outlet-chamfer bands
            // have longitudinal patch seams. Exclude the two complete analytic
            // cylinders from seam-clearance scoring; otherwise a short axial
            // distance on a wall with no seam could incorrectly disqualify the
            // seam-free topology before BRepBuilder gets a chance to validate it.
            List<IList<XYZ>[]> manufacturingPatchedBandRows =
                manufacturingBandRows
                    .Where((rows, index) =>
                        index != 0 &&
                        index != 3)
                    .ToList();

            List<int> manufacturingPatchedDegreeV =
                manufacturingDegreeV
                    .Where((degree, index) =>
                        index != 0 &&
                        index != 3)
                    .ToList();

            List<SmoothBRepPatchLayout> manufacturingLayouts;
            string manufacturingLayoutError;

            if (TryResolveToleranceSafeBRepPatchLayouts(
                    manufacturingPatchedBandRows,
                    manufacturingPatchedDegreeV,
                    ringSamples,
                    sampleCount,
                    shortCurveTolerance,
                    minimumTopologicalPatchCount,
                    preferredSplineSpanCount,
                    fallbackSplineSpanCount,
                    "manufacturing analytic-cylinder layout",
                    out manufacturingLayouts,
                    out manufacturingLayoutError))
            {
                foreach (SmoothBRepPatchLayout layout in
                         manufacturingLayouts)
                {
                    // First try the CAM-oriented topology: one continuous
                    // ruled face for the external saddle bevel and one
                    // continuous analytic header-cylinder face for the weld
                    // land. These two faces retain their real shared edge, but
                    // no longer contain the artificial opposite-side seams
                    // produced by the two-patch NURBS representation.
                    if (!outletShouldChamfer &&
                        headerAxisPoint != null &&
                        headerAxisDirection != null &&
                        headerAxisDirection.GetLength() >
                            GeometryTolerance &&
                        headerOutsideRadius >
                            GeometryTolerance)
                    {
                        string continuousTopError;

                        if (TryBuildContinuousTopManufacturingBRepSetOnBranch(
                                ringSamples,
                                sampleCount,
                                layout.SplineSpanCount,
                                layout.PatchStartOffset,
                                shortCurveTolerance,
                                headerAxisPoint,
                                headerAxisDirection,
                                headerOutsideRadius,
                                true,
                                out solid,
                                out continuousTopError))
                        {
                            topologicalPatchCount = 1;
                            surfaceBandCount = 4;
                            expectedStepFaceCount = 5;
                            circumferentialSplineSpanCount =
                                layout.SplineSpanCount;
                            usedAnalyticStraightCylinders = true;
                            usedSeamFreeAnalyticCylinders = true;
                            usedContinuousTopSurfaces = true;
                            usedMergedSmoothBodyBands = false;
                            return true;
                        }

                        attemptErrors.Add(
                            "continuous-top seam-free cylinder layout, spans " +
                            layout.SplineSpanCount.ToString(
                                CultureInfo.InvariantCulture) +
                            ", offset " +
                            layout.PatchStartOffset.ToString(
                                CultureInfo.InvariantCulture) +
                            ": " +
                            continuousTopError);

                        if (TryBuildContinuousTopManufacturingBRepSetOnBranch(
                                ringSamples,
                                sampleCount,
                                layout.SplineSpanCount,
                                layout.PatchStartOffset,
                                shortCurveTolerance,
                                headerAxisPoint,
                                headerAxisDirection,
                                headerOutsideRadius,
                                false,
                                out solid,
                                out continuousTopError))
                        {
                            topologicalPatchCount = 2;
                            surfaceBandCount = 4;
                            expectedStepFaceCount = 7;
                            circumferentialSplineSpanCount =
                                layout.SplineSpanCount;
                            usedAnalyticStraightCylinders = true;
                            usedSeamFreeAnalyticCylinders = false;
                            usedContinuousTopSurfaces = true;
                            usedMergedSmoothBodyBands = false;
                            return true;
                        }

                        attemptErrors.Add(
                            "continuous-top split cylinder layout, spans " +
                            layout.SplineSpanCount.ToString(
                                CultureInfo.InvariantCulture) +
                            ", offset " +
                            layout.PatchStartOffset.ToString(
                                CultureInfo.InvariantCulture) +
                            ": " +
                            continuousTopError);
                    }

                    string seamFreeCylinderError;

                    if (TryBuildSingleBodySmoothBRepSetOnBranch(
                            ringSamples,
                            manufacturingBandRows,
                            manufacturingLowerRingIndexes,
                            manufacturingUpperRingIndexes,
                            manufacturingDegreeV,
                            manufacturingKnotsV,
                            outletShouldChamfer,
                            sampleCount,
                            minimumTopologicalPatchCount,
                            layout.SplineSpanCount,
                            layout.PatchStartOffset,
                            shortCurveTolerance,
                            true,
                            0,
                            3,
                            true,
                            out solid,
                            out seamFreeCylinderError))
                    {
                        topologicalPatchCount =
                            minimumTopologicalPatchCount;

                        surfaceBandCount =
                            manufacturingBandRows.Count;

                        int analyticCylinderBandCount = 2;
                        int patchedBandCount =
                            surfaceBandCount -
                            analyticCylinderBandCount;

                        expectedStepFaceCount =
                            (patchedBandCount *
                             topologicalPatchCount) +
                            analyticCylinderBandCount +
                            (outletShouldChamfer
                                ? 0
                                : 1);

                        circumferentialSplineSpanCount =
                            layout.SplineSpanCount;

                        usedAnalyticStraightCylinders = true;
                        usedSeamFreeAnalyticCylinders = true;
                        usedMergedSmoothBodyBands = false;
                        return true;
                    }

                    attemptErrors.Add(
                        "seam-free analytic-cylinder layout, spans " +
                        layout.SplineSpanCount.ToString(
                            CultureInfo.InvariantCulture) +
                        ", offset " +
                        layout.PatchStartOffset.ToString(
                            CultureInfo.InvariantCulture) +
                        ": " +
                        seamFreeCylinderError);

                    string splitCylinderError;

                    if (TryBuildSingleBodySmoothBRepSetOnBranch(
                            ringSamples,
                            manufacturingBandRows,
                            manufacturingLowerRingIndexes,
                            manufacturingUpperRingIndexes,
                            manufacturingDegreeV,
                            manufacturingKnotsV,
                            outletShouldChamfer,
                            sampleCount,
                            minimumTopologicalPatchCount,
                            layout.SplineSpanCount,
                            layout.PatchStartOffset,
                            shortCurveTolerance,
                            true,
                            0,
                            3,
                            false,
                            out solid,
                            out splitCylinderError))
                    {
                        topologicalPatchCount =
                            minimumTopologicalPatchCount;

                        surfaceBandCount =
                            manufacturingBandRows.Count;

                        expectedStepFaceCount =
                            (surfaceBandCount *
                             topologicalPatchCount) +
                            (outletShouldChamfer
                                ? 0
                                : 1);

                        circumferentialSplineSpanCount =
                            layout.SplineSpanCount;

                        usedAnalyticStraightCylinders = true;
                        usedSeamFreeAnalyticCylinders = false;
                        usedMergedSmoothBodyBands = false;
                        return true;
                    }

                    attemptErrors.Add(
                        "split analytic-cylinder layout, spans " +
                        layout.SplineSpanCount.ToString(
                            CultureInfo.InvariantCulture) +
                        ", offset " +
                        layout.PatchStartOffset.ToString(
                            CultureInfo.InvariantCulture) +
                        ": " +
                        splitCylinderError);
                }
            }
            else
            {
                attemptErrors.Add(
                    "manufacturing analytic-cylinder layout: " +
                    manufacturingLayoutError);
            }

            // Lowest-face all-NURBS fallback. The straight cylindrical portions and the
            // adjacent cubic transition portions are exactly C2-compatible.
            // Representing each pair as one two-span cubic NURBS removes two
            // complete circumferential edge rings without changing the profile.
            List<XYZ> outerLineControlOne =
                CreateLinearBezierControlRing(
                    outletOuter,
                    collarStartOuter,
                    1.0 / 3.0);

            List<XYZ> outerLineControlTwo =
                CreateLinearBezierControlRing(
                    outletOuter,
                    collarStartOuter,
                    2.0 / 3.0);

            List<IList<XYZ>> mergedOuterControls;
            IList<double> mergedOuterKnotsV;
            string mergedOuterError;

            bool mergedOuterSucceeded =
                TryCreateMergedC2CubicBandControlRings(
                    outletOuter,
                    outerLineControlOne,
                    outerLineControlTwo,
                    collarStartOuter,
                    collarStartOuter,
                    fullRadiusOuter,
                    tubeEndOuter,
                    saddleOuter,
                    shortCurveTolerance,
                    "branch outside wall and saddle transition",
                    out mergedOuterControls,
                    out mergedOuterKnotsV,
                    out mergedOuterError);

            List<XYZ> innerLineControlOne =
                CreateLinearBezierControlRing(
                    collarStartInner,
                    outletInner,
                    1.0 / 3.0);

            List<XYZ> innerLineControlTwo =
                CreateLinearBezierControlRing(
                    collarStartInner,
                    outletInner,
                    2.0 / 3.0);

            List<IList<XYZ>> mergedInnerControls;
            IList<double> mergedInnerKnotsV;
            string mergedInnerError;

            bool mergedInnerSucceeded =
                TryCreateMergedC2CubicBandControlRings(
                    saddleInner,
                    tubeEndInner,
                    fullRadiusInner,
                    collarStartInner,
                    collarStartInner,
                    innerLineControlOne,
                    innerLineControlTwo,
                    outletInner,
                    shortCurveTolerance,
                    "branch saddle-bore transition and straight bore",
                    out mergedInnerControls,
                    out mergedInnerKnotsV,
                    out mergedInnerError);

            if (mergedOuterSucceeded &&
                mergedInnerSucceeded)
            {
                List<IList<XYZ>[]> mergedBandRows =
                    new List<IList<XYZ>[]>
                    {
                        mergedOuterControls.ToArray(),
                        new[]
                        {
                            saddleOuter,
                            saddleRoot
                        },
                        new[]
                        {
                            saddleRoot,
                            saddleInner
                        },
                        mergedInnerControls.ToArray()
                    };

                List<int> mergedLowerRingIndexes =
                    new List<int>
                    {
                        0,
                        2,
                        3,
                        4
                    };

                List<int> mergedUpperRingIndexes =
                    new List<int>
                    {
                        2,
                        3,
                        4,
                        6
                    };

                List<int> mergedDegreeV =
                    new List<int>
                    {
                        3,
                        1,
                        1,
                        3
                    };

                List<IList<double>> mergedKnotsV =
                    new List<IList<double>>
                    {
                        mergedOuterKnotsV,
                        null,
                        null,
                        mergedInnerKnotsV
                    };

                if (outletShouldChamfer)
                {
                    mergedBandRows.Add(
                        new[]
                        {
                            outletInner,
                            outletRoot
                        });

                    mergedLowerRingIndexes.Add(6);
                    mergedUpperRingIndexes.Add(
                        outletRootRingIndex);
                    mergedDegreeV.Add(1);
                    mergedKnotsV.Add(null);

                    mergedBandRows.Add(
                        new[]
                        {
                            outletRoot,
                            outletOuter
                        });

                    mergedLowerRingIndexes.Add(
                        outletRootRingIndex);
                    mergedUpperRingIndexes.Add(0);
                    mergedDegreeV.Add(1);
                    mergedKnotsV.Add(null);
                }

                List<SmoothBRepPatchLayout> mergedLayouts;
                string mergedLayoutError;

                if (TryResolveToleranceSafeBRepPatchLayouts(
                        mergedBandRows,
                        mergedDegreeV,
                        ringSamples,
                        sampleCount,
                        shortCurveTolerance,
                        minimumTopologicalPatchCount,
                        preferredSplineSpanCount,
                        fallbackSplineSpanCount,
                        "minimal merged 2-patch layout",
                        out mergedLayouts,
                        out mergedLayoutError))
                {
                    foreach (SmoothBRepPatchLayout layout in
                             mergedLayouts)
                    {
                        string mergedBuildError;

                        if (TryBuildSingleBodySmoothBRepSetOnBranch(
                                ringSamples,
                                mergedBandRows,
                                mergedLowerRingIndexes,
                                mergedUpperRingIndexes,
                                mergedDegreeV,
                                mergedKnotsV,
                                outletShouldChamfer,
                                sampleCount,
                                minimumTopologicalPatchCount,
                                layout.SplineSpanCount,
                                layout.PatchStartOffset,
                                shortCurveTolerance,
                                false,
                                -1,
                                -1,
                                false,
                                out solid,
                                out mergedBuildError))
                        {
                            topologicalPatchCount =
                                minimumTopologicalPatchCount;

                            surfaceBandCount =
                                mergedBandRows.Count;

                            expectedStepFaceCount =
                                (surfaceBandCount *
                                 topologicalPatchCount) +
                                (outletShouldChamfer
                                    ? 0
                                    : 1);

                            circumferentialSplineSpanCount =
                                layout.SplineSpanCount;

                            usedAnalyticStraightCylinders = false;
                            usedSeamFreeAnalyticCylinders = false;
                            usedMergedSmoothBodyBands = true;
                            return true;
                        }

                        attemptErrors.Add(
                            "minimal merged 2-patch layout, spans " +
                            layout.SplineSpanCount.ToString(
                                CultureInfo.InvariantCulture) +
                            ", offset " +
                            layout.PatchStartOffset.ToString(
                                CultureInfo.InvariantCulture) +
                            ": " +
                            mergedBuildError);
                    }
                }
                else
                {
                    attemptErrors.Add(
                        "minimal merged 2-patch layout: " +
                        mergedLayoutError);
                }
            }
            else
            {
                if (!mergedOuterSucceeded)
                {
                    attemptErrors.Add(
                        "outside smooth-band merge: " +
                        mergedOuterError);
                }

                if (!mergedInnerSucceeded)
                {
                    attemptErrors.Add(
                        "inside smooth-band merge: " +
                        mergedInnerError);
                }
            }

            // Proven topology fallback. This preserves separate straight and
            // transition bands, but now exhausts tolerance-safe seam rotations
            // around the entire circumference before increasing face count.
            List<IList<XYZ>[]> bandRows =
                new List<IList<XYZ>[]>
                {
                    new[]
                    {
                        outletOuter,
                        collarStartOuter
                    },
                    new[]
                    {
                        collarStartOuter,
                        fullRadiusOuter,
                        tubeEndOuter,
                        saddleOuter
                    },
                    new[]
                    {
                        saddleOuter,
                        saddleRoot
                    },
                    new[]
                    {
                        saddleRoot,
                        saddleInner
                    },
                    new[]
                    {
                        saddleInner,
                        tubeEndInner,
                        fullRadiusInner,
                        collarStartInner
                    },
                    new[]
                    {
                        collarStartInner,
                        outletInner
                    }
                };

            List<int> lowerRingIndexes =
                new List<int>
                {
                    0,
                    1,
                    2,
                    3,
                    4,
                    5
                };

            List<int> upperRingIndexes =
                new List<int>
                {
                    1,
                    2,
                    3,
                    4,
                    5,
                    6
                };

            List<int> degreeV =
                new List<int>
                {
                    1,
                    3,
                    1,
                    1,
                    3,
                    1
                };

            List<IList<double>> knotsVByBand =
                Enumerable
                    .Repeat<IList<double>>(
                        null,
                        bandRows.Count)
                    .ToList();

            if (outletShouldChamfer)
            {
                bandRows.Add(
                    new[]
                    {
                        outletInner,
                        outletRoot
                    });

                lowerRingIndexes.Add(6);
                upperRingIndexes.Add(
                    outletRootRingIndex);
                degreeV.Add(1);
                knotsVByBand.Add(null);

                bandRows.Add(
                    new[]
                    {
                        outletRoot,
                        outletOuter
                    });

                lowerRingIndexes.Add(
                    outletRootRingIndex);
                upperRingIndexes.Add(0);
                degreeV.Add(1);
                knotsVByBand.Add(null);
            }

            int[] candidateTopologicalPatchCounts =
            {
                minimumTopologicalPatchCount,
                fallbackTopologicalPatchCount
            };

            foreach (int currentPatchCount in
                     candidateTopologicalPatchCounts)
            {
                List<SmoothBRepPatchLayout> layouts;
                string patchLayoutError;

                if (!TryResolveToleranceSafeBRepPatchLayouts(
                        bandRows,
                        degreeV,
                        ringSamples,
                        sampleCount,
                        shortCurveTolerance,
                        currentPatchCount,
                        preferredSplineSpanCount,
                        fallbackSplineSpanCount,
                        currentPatchCount.ToString(
                            CultureInfo.InvariantCulture) +
                        "-patch layout",
                        out layouts,
                        out patchLayoutError))
                {
                    attemptErrors.Add(
                        currentPatchCount.ToString(
                            CultureInfo.InvariantCulture) +
                        "-patch layout: " +
                        patchLayoutError);

                    continue;
                }

                if (currentPatchCount ==
                    minimumTopologicalPatchCount)
                {
                    foreach (SmoothBRepPatchLayout layout in
                             layouts)
                    {
                        string analyticError;

                        if (TryBuildSingleBodySmoothBRepSetOnBranch(
                                ringSamples,
                                bandRows,
                                lowerRingIndexes,
                                upperRingIndexes,
                                degreeV,
                                knotsVByBand,
                                outletShouldChamfer,
                                sampleCount,
                                currentPatchCount,
                                layout.SplineSpanCount,
                                layout.PatchStartOffset,
                                shortCurveTolerance,
                                true,
                                0,
                                5,
                                false,
                                out solid,
                                out analyticError))
                        {
                            topologicalPatchCount =
                                currentPatchCount;

                            surfaceBandCount =
                                bandRows.Count;

                            expectedStepFaceCount =
                                (surfaceBandCount *
                                 topologicalPatchCount) +
                                (outletShouldChamfer
                                    ? 0
                                    : 1);

                            circumferentialSplineSpanCount =
                                layout.SplineSpanCount;

                            usedAnalyticStraightCylinders = true;
                            usedSeamFreeAnalyticCylinders = false;
                            usedMergedSmoothBodyBands = false;
                            return true;
                        }

                        attemptErrors.Add(
                            "2-patch analytic-cylinder layout, spans " +
                            layout.SplineSpanCount.ToString(
                                CultureInfo.InvariantCulture) +
                            ", offset " +
                            layout.PatchStartOffset.ToString(
                                CultureInfo.InvariantCulture) +
                            ": " +
                            analyticError);
                    }
                }

                foreach (SmoothBRepPatchLayout layout in
                         layouts)
                {
                    string smoothNurbsError;

                    if (TryBuildSingleBodySmoothBRepSetOnBranch(
                            ringSamples,
                            bandRows,
                            lowerRingIndexes,
                            upperRingIndexes,
                            degreeV,
                            knotsVByBand,
                            outletShouldChamfer,
                            sampleCount,
                            currentPatchCount,
                            layout.SplineSpanCount,
                            layout.PatchStartOffset,
                            shortCurveTolerance,
                            false,
                            -1,
                            -1,
                            false,
                            out solid,
                            out smoothNurbsError))
                    {
                        topologicalPatchCount =
                            currentPatchCount;

                        surfaceBandCount =
                            bandRows.Count;

                        expectedStepFaceCount =
                            (surfaceBandCount *
                             topologicalPatchCount) +
                            (outletShouldChamfer
                                ? 0
                                : 1);

                        circumferentialSplineSpanCount =
                            layout.SplineSpanCount;

                        usedAnalyticStraightCylinders = false;
                        usedSeamFreeAnalyticCylinders = false;
                        usedMergedSmoothBodyBands = false;
                        return true;
                    }

                    attemptErrors.Add(
                        currentPatchCount.ToString(
                            CultureInfo.InvariantCulture) +
                        "-patch smooth one-body NURBS layout, spans " +
                        layout.SplineSpanCount.ToString(
                            CultureInfo.InvariantCulture) +
                        ", offset " +
                        layout.PatchStartOffset.ToString(
                            CultureInfo.InvariantCulture) +
                        ": " +
                        smoothNurbsError);
                }
            }

            // Final geometry-preserving fallback. Some legitimate branch/weld-land
            // combinations contain a physical band whose radial/axial seam is
            // shorter than Application.ShortCurveTolerance. No amount of seam
            // rotation or additional BRep patches can make that real edge longer;
            // the compact BRep topology is therefore mathematically impossible in
            // Revit for that instance. Do not fail the whole fabrication export.
            // Build the same resolved profile as a watertight adaptive tessellated
            // solid instead. This path is deliberately last so normal branches keep
            // the low-face analytic/NURBS topology.
            int tessellatedFaceCount;
            int tessellatedMaximumVSubdivisions;
            double tessellatedMaximumDeviation;
            string tessellatedError;

            if (TryBuildAdaptiveTessellatedSetOnBranch(
                    bandRows,
                    degreeV,
                    ringSamples,
                    outletShouldChamfer,
                    sampleCount,
                    shortCurveTolerance,
                    out solid,
                    out tessellatedFaceCount,
                    out tessellatedMaximumVSubdivisions,
                    out tessellatedMaximumDeviation,
                    out tessellatedError))
            {
                DiagnosticCaptureAdaptiveFallback(
                    true,
                    true,
                    tessellatedFaceCount,
                    tessellatedMaximumVSubdivisions,
                    tessellatedMaximumDeviation,
                    null);
                topologicalPatchCount = 0;
                surfaceBandCount = bandRows.Count;
                expectedStepFaceCount =
                    Math.Max(
                        1,
                        tessellatedFaceCount);
                circumferentialSplineSpanCount = 0;
                usedAnalyticStraightCylinders = false;
                usedSeamFreeAnalyticCylinders = false;
                usedContinuousTopSurfaces = false;
                usedMergedSmoothBodyBands = false;
                usedAdaptiveTessellatedFallback = true;
                return true;
            }

            DiagnosticCaptureAdaptiveFallback(
                true,
                false,
                tessellatedFaceCount,
                tessellatedMaximumVSubdivisions,
                tessellatedMaximumDeviation,
                tessellatedError);

            attemptErrors.Add(
                "adaptive watertight tessellated fallback: " +
                tessellatedError);

            error =
                "The compact single-body SET-ON branch could not be built, " +
                "and the adaptive geometry-preserving fallback also failed. " +
                string.Join(
                    " ",
                    attemptErrors);

            return false;
        }


        private static bool
            TryBuildAdaptiveTessellatedSetOnBranch(
                IList<IList<XYZ>[]> bandRows,
                IList<int> degreeV,
                IList<IList<XYZ>> ringSamples,
                bool outletShouldChamfer,
                int sampleCount,
                double shortCurveTolerance,
                out Solid solid,
                out int generatedFaceCount,
                out int maximumVSubdivisions,
                out double maximumObservedDeviation,
                out string error)
        {
            solid = null;
            generatedFaceCount = 0;
            maximumVSubdivisions = 0;
            maximumObservedDeviation = 0.0;
            error = null;

            if (bandRows == null ||
                degreeV == null ||
                ringSamples == null ||
                bandRows.Count == 0 ||
                bandRows.Count != degreeV.Count ||
                ringSamples.Count <= 6 ||
                ringSamples[0] == null ||
                ringSamples[6] == null ||
                sampleCount < 16 ||
                ringSamples[0].Count != sampleCount ||
                ringSamples[6].Count != sampleCount)
            {
                error =
                    "The adaptive tessellated SET-ON branch inputs are incomplete.";
                return false;
            }

            for (int bandIndex = 0;
                 bandIndex < bandRows.Count;
                 bandIndex++)
            {
                IList<XYZ>[] rows =
                    bandRows[bandIndex];

                if (rows == null ||
                    rows.Length < 2 ||
                    rows.Any(x =>
                        x == null ||
                        x.Count != sampleCount) ||
                    (degreeV[bandIndex] != 1 &&
                     degreeV[bandIndex] != 3) ||
                    (degreeV[bandIndex] == 1 &&
                     rows.Length != 2) ||
                    (degreeV[bandIndex] == 3 &&
                     rows.Length != 4))
                {
                    error =
                        "Surface band " +
                        bandIndex.ToString(
                            CultureInfo.InvariantCulture) +
                        " cannot be represented by the adaptive tessellated fallback.";
                    return false;
                }
            }

            // Keep the fallback comfortably inside the same 0.1 mm geometric
            // deviation budget used by the compact BRep selector. Most of the
            // circumferential accuracy is already present in the 128-256 source
            // samples; this tolerance only controls subdivision through cubic
            // transition bands.
            double targetDeviation =
                Math.Max(
                    GeometryTolerance * 100.0,
                    Math.Min(
                        SmoothBRepMaximumDeviationMillimetres * 0.25,
                        0.025) /
                    FeetToMillimetres);

            try
            {
                TessellatedShapeBuilder builder =
                    new TessellatedShapeBuilder();

                builder.OpenConnectedFaceSet(true);

                int theoreticalTriangleCount = 0;

                for (int bandIndex = 0;
                     bandIndex < bandRows.Count;
                     bandIndex++)
                {
                    IList<XYZ>[] rows =
                        bandRows[bandIndex];

                    int subdivisionCount;
                    double bandDeviation;

                    if (!TryResolveAdaptiveTessellationSubdivisions(
                            rows,
                            degreeV[bandIndex],
                            sampleCount,
                            targetDeviation,
                            out subdivisionCount,
                            out bandDeviation))
                    {
                        error =
                            "Surface band " +
                            bandIndex.ToString(
                                CultureInfo.InvariantCulture) +
                            " could not meet the adaptive tessellation deviation limit.";
                        return false;
                    }

                    maximumVSubdivisions =
                        Math.Max(
                            maximumVSubdivisions,
                            subdivisionCount);

                    maximumObservedDeviation =
                        Math.Max(
                            maximumObservedDeviation,
                            bandDeviation);

                    IList<XYZ> previousRing =
                        EvaluateAdaptiveTessellatedBandRing(
                            rows,
                            degreeV[bandIndex],
                            0.0,
                            sampleCount);

                    for (int subdivisionIndex = 0;
                         subdivisionIndex < subdivisionCount;
                         subdivisionIndex++)
                    {
                        double parameter =
                            (double)(subdivisionIndex + 1) /
                            subdivisionCount;

                        IList<XYZ> nextRing =
                            EvaluateAdaptiveTessellatedBandRing(
                                rows,
                                degreeV[bandIndex],
                                parameter,
                                sampleCount);

                        AddAdaptiveTessellatedRingStrip(
                            builder,
                            previousRing,
                            nextRing,
                            sampleCount);

                        theoreticalTriangleCount +=
                            sampleCount * 2;

                        previousRing = nextRing;
                    }
                }

                if (!outletShouldChamfer)
                {
                    // The plain outlet is an annulus. Its ordering matches the
                    // orientation of the proven procedural branch builder.
                    AddAdaptiveTessellatedRingStrip(
                        builder,
                        ringSamples[6],
                        ringSamples[0],
                        sampleCount);

                    theoreticalTriangleCount +=
                        sampleCount * 2;
                }

                builder.CloseConnectedFaceSet();
                builder.Target =
                    TessellatedShapeBuilderTarget.Solid;
                builder.Fallback =
                    TessellatedShapeBuilderFallback.Abort;
                builder.Build();

                solid =
                    builder
                        .GetBuildResult()
                        .GetGeometricalObjects()
                        .OfType<Solid>()
                        .Where(x =>
                            x != null &&
                            x.Volume > GeometryTolerance)
                        .OrderByDescending(x => x.Volume)
                        .FirstOrDefault();

                if (solid == null ||
                    solid.Faces == null ||
                    solid.Faces.Size <= 0)
                {
                    error =
                        "Revit did not return a valid watertight solid from the adaptive tessellated SET-ON branch.";
                    return false;
                }

                // Use Revit's actual resulting face count rather than a static
                // estimate. This keeps STEP topology validation correct if Revit
                // combines or subdivides any tessellated faces internally.
                generatedFaceCount =
                    Math.Max(
                        solid.Faces.Size,
                        theoreticalTriangleCount);

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The adaptive tessellated SET-ON branch could not be generated: " +
                    ex.Message;
                return false;
            }
        }

        private static bool
            TryResolveAdaptiveTessellationSubdivisions(
                IList<XYZ>[] rows,
                int degree,
                int sampleCount,
                double targetDeviation,
                out int subdivisionCount,
                out double maximumDeviation)
        {
            subdivisionCount = 0;
            maximumDeviation = double.MaxValue;

            if (rows == null ||
                sampleCount <= 0 ||
                targetDeviation <= 0 ||
                (degree != 1 && degree != 3))
            {
                return false;
            }

            if (degree == 1)
            {
                subdivisionCount = 1;
                maximumDeviation = 0.0;
                return true;
            }

            const int maximumSubdivisionCount = 64;

            for (int candidate = 1;
                 candidate <= maximumSubdivisionCount;
                 candidate *= 2)
            {
                double candidateMaximumDeviation = 0.0;

                for (int sampleIndex = 0;
                     sampleIndex < sampleCount;
                     sampleIndex++)
                {
                    for (int segmentIndex = 0;
                         segmentIndex < candidate;
                         segmentIndex++)
                    {
                        double t0 =
                            (double)segmentIndex /
                            candidate;

                        double t1 =
                            (double)(segmentIndex + 1) /
                            candidate;

                        XYZ p0 =
                            EvaluateAdaptiveTessellatedBandPoint(
                                rows,
                                degree,
                                sampleIndex,
                                t0);

                        XYZ p1 =
                            EvaluateAdaptiveTessellatedBandPoint(
                                rows,
                                degree,
                                sampleIndex,
                                t1);

                        // Check several interior points instead of only the
                        // midpoint. An asymmetric cubic can have its largest
                        // chord deviation away from t=0.5.
                        double[] fractions =
                        {
                            0.25,
                            0.50,
                            0.75
                        };

                        foreach (double fraction in fractions)
                        {
                            double parameter =
                                t0 +
                                ((t1 - t0) * fraction);

                            XYZ actualPoint =
                                EvaluateAdaptiveTessellatedBandPoint(
                                    rows,
                                    degree,
                                    sampleIndex,
                                    parameter);

                            XYZ chordPoint =
                                p0 +
                                ((p1 - p0) * fraction);

                            candidateMaximumDeviation =
                                Math.Max(
                                    candidateMaximumDeviation,
                                    actualPoint.DistanceTo(
                                        chordPoint));
                        }
                    }
                }

                if (candidateMaximumDeviation <=
                    targetDeviation)
                {
                    subdivisionCount = candidate;
                    maximumDeviation =
                        candidateMaximumDeviation;
                    return true;
                }
            }

            return false;
        }

        private static IList<XYZ>
            EvaluateAdaptiveTessellatedBandRing(
                IList<XYZ>[] rows,
                int degree,
                double parameter,
                int sampleCount)
        {
            // Preserve shared boundary vertices exactly. This prevents tiny
            // numerical cracks between neighboring bands.
            if (parameter <= 0.0)
                return rows[0];

            if (parameter >= 1.0)
                return rows[rows.Length - 1];

            List<XYZ> ring =
                new List<XYZ>(sampleCount);

            for (int sampleIndex = 0;
                 sampleIndex < sampleCount;
                 sampleIndex++)
            {
                ring.Add(
                    EvaluateAdaptiveTessellatedBandPoint(
                        rows,
                        degree,
                        sampleIndex,
                        parameter));
            }

            return ring;
        }

        private static XYZ
            EvaluateAdaptiveTessellatedBandPoint(
                IList<XYZ>[] rows,
                int degree,
                int sampleIndex,
                double parameter)
        {
            double t =
                Math.Max(
                    0.0,
                    Math.Min(
                        1.0,
                        parameter));

            if (degree == 1)
            {
                return
                    rows[0][sampleIndex] +
                    ((rows[1][sampleIndex] -
                      rows[0][sampleIndex]) * t);
            }

            double oneMinusT =
                1.0 - t;

            double b0 =
                oneMinusT * oneMinusT * oneMinusT;
            double b1 =
                3.0 * oneMinusT * oneMinusT * t;
            double b2 =
                3.0 * oneMinusT * t * t;
            double b3 =
                t * t * t;

            return
                (rows[0][sampleIndex] * b0) +
                (rows[1][sampleIndex] * b1) +
                (rows[2][sampleIndex] * b2) +
                (rows[3][sampleIndex] * b3);
        }

        private static void
            AddAdaptiveTessellatedRingStrip(
                TessellatedShapeBuilder builder,
                IList<XYZ> firstRing,
                IList<XYZ> secondRing,
                int sampleCount)
        {
            for (int index = 0;
                 index < sampleCount;
                 index++)
            {
                int next =
                    (index + 1) %
                    sampleCount;

                AddProceduralBranchQuad(
                    builder,
                    firstRing[index],
                    firstRing[next],
                    secondRing[next],
                    secondRing[index]);
            }
        }


        private static bool
            TryBuildContinuousTopManufacturingBRepSetOnBranch(
                IList<IList<XYZ>> ringSamples,
                int sampleCount,
                int splineSpanCount,
                int patchStartOffset,
                double shortCurveTolerance,
                XYZ headerAxisPoint,
                XYZ headerAxisDirection,
                double headerOutsideRadius,
                bool useSeamFreeSingleFaceCylinders,
                out Solid solid,
                out string error)
        {
            solid = null;
            error = null;

            // The most likely surface orientations are tried first. The
            // external bevel follows the generated ring order, while the
            // concave weld land is the reverse side of the header cylinder.
            // A small guarded orientation search is cheaper and safer than
            // forcing one orientation across every Revit/fabrication family.
            bool[,] orientationCandidates =
            {
                { false, true },
                { true, false }
            };

            List<string> attemptErrors =
                new List<string>();

            for (int candidateIndex = 0;
                 candidateIndex <
                    orientationCandidates.GetLength(0);
                 candidateIndex++)
            {
                bool reverseBevel =
                    orientationCandidates[
                        candidateIndex,
                        0];

                bool reverseWeldLand =
                    orientationCandidates[
                        candidateIndex,
                        1];

                string attemptError;

                if (TryBuildContinuousTopManufacturingBRepSetOnBranchCore(
                        ringSamples,
                        sampleCount,
                        splineSpanCount,
                        patchStartOffset,
                        shortCurveTolerance,
                        headerAxisPoint,
                        headerAxisDirection,
                        headerOutsideRadius,
                        useSeamFreeSingleFaceCylinders,
                        reverseBevel,
                        reverseWeldLand,
                        out solid,
                        out attemptError))
                {
                    return true;
                }

                attemptErrors.Add(
                    "bevel reversed=" +
                    reverseBevel.ToString() +
                    ", weld land reversed=" +
                    reverseWeldLand.ToString() +
                    ": " +
                    attemptError);

                // Surface reversal can only repair a closed-shell orientation
                // failure reported at Finish(). Ring construction, support-
                // surface, tolerance, and analytic-cylinder failures are
                // orientation-independent, so do not repeat the complete BRep
                // build for those cases.
                if (string.IsNullOrWhiteSpace(
                        attemptError) ||
                    attemptError.IndexOf(
                        "Revit rejected the five-region",
                        StringComparison.OrdinalIgnoreCase) < 0)
                {
                    break;
                }
            }

            error =
                "The continuous-top BRep topology was rejected. " +
                string.Join(
                    " ",
                    attemptErrors);

            return false;
        }

        private static bool
            TryBuildContinuousTopManufacturingBRepSetOnBranchCore(
                IList<IList<XYZ>> ringSamples,
                int sampleCount,
                int splineSpanCount,
                int patchStartOffset,
                double shortCurveTolerance,
                XYZ headerAxisPoint,
                XYZ headerAxisDirection,
                double headerOutsideRadius,
                bool useSeamFreeSingleFaceCylinders,
                bool reverseBevel,
                bool reverseWeldLand,
                out Solid solid,
                out string error)
        {
            solid = null;
            error = null;

            const int outletOuterRingIndex = 0;
            const int saddleOuterRingIndex = 2;
            const int saddleRootRingIndex = 3;
            const int saddleInnerRingIndex = 4;
            const int outletInnerRingIndex = 6;
            const int edgePatchCount = 2;

            if (ringSamples == null ||
                ringSamples.Count <= outletInnerRingIndex ||
                sampleCount < 32 ||
                splineSpanCount < 4 ||
                (splineSpanCount % 2) != 0 ||
                (sampleCount % splineSpanCount) != 0 ||
                shortCurveTolerance <= 0 ||
                headerAxisPoint == null ||
                headerAxisDirection == null ||
                headerAxisDirection.GetLength() <=
                    GeometryTolerance ||
                headerOutsideRadius <=
                    GeometryTolerance)
            {
                error =
                    "The continuous-top manufacturing BRep inputs are " +
                    "incomplete.";

                return false;
            }

            int[] requiredRingIndexes =
            {
                outletOuterRingIndex,
                saddleOuterRingIndex,
                saddleRootRingIndex,
                saddleInnerRingIndex,
                outletInnerRingIndex
            };

            foreach (int ringIndex in
                     requiredRingIndexes)
            {
                if (ringSamples[ringIndex] == null ||
                    ringSamples[ringIndex].Count !=
                        sampleCount)
                {
                    error =
                        "Continuous-top ring " +
                        ringIndex.ToString(
                            CultureInfo.InvariantCulture) +
                        " is incomplete.";

                    return false;
                }
            }

            try
            {
                BRepBuilder builder =
                    new BRepBuilder(
                        BRepType.Solid);

                Curve[] fullRingCurves =
                    new Curve[
                        ringSamples.Count];

                Curve[,] ringCurves =
                    new Curve[
                        ringSamples.Count,
                        edgePatchCount];

                BRepBuilderGeometryId[,]
                    ringEdgeIds =
                        new BRepBuilderGeometryId[
                            ringSamples.Count,
                            edgePatchCount];

                foreach (int ringIndex in
                         requiredRingIndexes)
                {
                    Curve fullRingCurve;
                    Curve firstHalfCurve;
                    Curve secondHalfCurve;
                    string ringError;

                    if (!TryCreateContinuousPeriodicBRepRing(
                            ringSamples[ringIndex],
                            sampleCount,
                            splineSpanCount,
                            patchStartOffset,
                            shortCurveTolerance,
                            "continuous-top ring " +
                            ringIndex.ToString(
                                CultureInfo.InvariantCulture),
                            out fullRingCurve,
                            out firstHalfCurve,
                            out secondHalfCurve,
                            out ringError))
                    {
                        error = ringError;
                        return false;
                    }

                    fullRingCurves[ringIndex] =
                        fullRingCurve;

                    ringCurves[ringIndex, 0] =
                        firstHalfCurve;

                    ringCurves[ringIndex, 1] =
                        secondHalfCurve;

                    ringEdgeIds[ringIndex, 0] =
                        builder.AddEdge(
                            BRepBuilderEdgeGeometry.Create(
                                firstHalfCurve));

                    ringEdgeIds[ringIndex, 1] =
                        builder.AddEdge(
                            BRepBuilderEdgeGeometry.Create(
                                secondHalfCurve));
                }

                Surface bevelSurface;
                string bevelSurfaceError;

                // Revit 2025+ allows HermiteSurface as a BRepBuilder support
                // surface. Use a periodic Hermite strip first so the bevel is
                // represented by one cyclic face instead of two clamped NURBS
                // patches. Older Revit versions reject HermiteSurface here and
                // automatically continue to the ruled-surface/fallback paths.
                if (!TryCreatePeriodicHermiteStripSurface(
                        ringSamples[saddleOuterRingIndex],
                        ringSamples[saddleRootRingIndex],
                        sampleCount,
                        patchStartOffset,
                        out bevelSurface,
                        out bevelSurfaceError))
                {
                    try
                    {
                        bevelSurface =
                            RuledSurface.Create(
                                fullRingCurves[
                                    saddleOuterRingIndex],
                                fullRingCurves[
                                    saddleRootRingIndex]);
                    }
                    catch (Exception ruledException)
                    {
                        bevelSurface = null;
                        bevelSurfaceError +=
                            " Ruled-surface fallback failed: " +
                            ruledException.Message;
                    }
                }

                if (bevelSurface == null ||
                    !BRepBuilder.IsPermittedSurfaceType(
                        bevelSurface))
                {
                    error =
                        "Revit did not create a permitted periodic support " +
                        "surface for the continuous external saddle bevel. " +
                        bevelSurfaceError;

                    return false;
                }

                string faceError;

                if (!TryAddContinuousTwoLoopFace(
                        builder,
                        BRepBuilderSurfaceGeometry.Create(
                            bevelSurface,
                            null),
                        ringEdgeIds,
                        ringCurves,
                        saddleOuterRingIndex,
                        saddleRootRingIndex,
                        reverseBevel,
                        shortCurveTolerance,
                        "continuous external saddle bevel",
                        out faceError))
                {
                    error = faceError;
                    return false;
                }

                CylindricalSurface headerCylinder;
                string headerCylinderError;

                if (!TryCreateContinuousHeaderCylinderSurface(
                        headerAxisPoint,
                        headerAxisDirection,
                        headerOutsideRadius,
                        ringSamples[saddleRootRingIndex],
                        ringSamples[saddleInnerRingIndex],
                        fullRingCurves[saddleRootRingIndex],
                        fullRingCurves[saddleInnerRingIndex],
                        out headerCylinder,
                        out headerCylinderError))
                {
                    error = headerCylinderError;
                    return false;
                }

                if (!TryAddContinuousTwoLoopFace(
                        builder,
                        BRepBuilderSurfaceGeometry.Create(
                            headerCylinder,
                            null),
                        ringEdgeIds,
                        ringCurves,
                        saddleRootRingIndex,
                        saddleInnerRingIndex,
                        reverseWeldLand,
                        shortCurveTolerance,
                        "continuous header-cylinder weld land",
                        out faceError))
                {
                    error = faceError;
                    return false;
                }

                string cylinderError;

                if (!TryAddAnalyticCylindricalBranchBand(
                        builder,
                        ringEdgeIds,
                        ringCurves,
                        ringSamples[outletOuterRingIndex],
                        ringSamples[saddleOuterRingIndex],
                        ringSamples[outletOuterRingIndex],
                        outletOuterRingIndex,
                        saddleOuterRingIndex,
                        false,
                        edgePatchCount,
                        patchStartOffset,
                        shortCurveTolerance,
                        useSeamFreeSingleFaceCylinders,
                        "complete branch outside wall with continuous top",
                        out cylinderError))
                {
                    error = cylinderError;
                    return false;
                }

                if (!TryAddAnalyticCylindricalBranchBand(
                        builder,
                        ringEdgeIds,
                        ringCurves,
                        ringSamples[saddleInnerRingIndex],
                        ringSamples[outletInnerRingIndex],
                        ringSamples[outletInnerRingIndex],
                        saddleInnerRingIndex,
                        outletInnerRingIndex,
                        true,
                        edgePatchCount,
                        patchStartOffset,
                        shortCurveTolerance,
                        useSeamFreeSingleFaceCylinders,
                        "complete branch bore wall with continuous top",
                        out cylinderError))
                {
                    error = cylinderError;
                    return false;
                }

                if (!TryAddPlainOutletAnnularFace(
                        builder,
                        ringSamples,
                        ringEdgeIds,
                        ringCurves,
                        outletOuterRingIndex,
                        outletInnerRingIndex,
                        edgePatchCount,
                        shortCurveTolerance,
                        out faceError))
                {
                    error = faceError;
                    return false;
                }

                builder.Finish();

                if (!builder.IsResultAvailable())
                {
                    error =
                        "Revit rejected the five-region continuous-top " +
                        "single-body BRep.";

                    return false;
                }

                solid =
                    builder.GetResult();

                if (solid == null ||
                    solid.Volume <=
                        GeometryTolerance)
                {
                    error =
                        "Revit returned an empty continuous-top SET-ON " +
                        "branch solid.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The continuous-top SET-ON branch could not be " +
                    "generated: " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateContinuousPeriodicBRepRing(
                IList<XYZ> samples,
                int sampleCount,
                int splineSpanCount,
                int startSample,
                double shortCurveTolerance,
                string context,
                out Curve fullCurve,
                out Curve firstHalfCurve,
                out Curve secondHalfCurve,
                out string error)
        {
            fullCurve = null;
            firstHalfCurve = null;
            secondHalfCurve = null;
            error = null;

            if (samples == null ||
                samples.Count != sampleCount ||
                sampleCount < 8 ||
                splineSpanCount < 4 ||
                (splineSpanCount % 2) != 0 ||
                (sampleCount % splineSpanCount) != 0 ||
                shortCurveTolerance <= 0)
            {
                error =
                    "The continuous periodic ring inputs for " +
                    context +
                    " are incomplete.";

                return false;
            }

            try
            {
                int normalizedStart =
                    ((startSample % sampleCount) +
                     sampleCount) %
                    sampleCount;

                List<XYZ> rotatedSamples =
                    new List<XYZ>(
                        sampleCount);

                for (int index = 0;
                     index < sampleCount;
                     index++)
                {
                    rotatedSamples.Add(
                        samples[
                            (normalizedStart + index) %
                            sampleCount]);
                }

                // Use a genuinely periodic Revit curve as the ruled-surface
                // generator. A merely closed clamped NURBS still has a
                // non-periodic parameter seam; that seam can force Revit or a
                // downstream STEP translator to split the face. The periodic
                // Hermite spline has no geometric end seam and interpolates
                // every resolved saddle sample.
                fullCurve =
                    HermiteSpline.Create(
                        rotatedSamples,
                        true);

                if (fullCurve == null ||
                    !fullCurve.IsBound ||
                    !fullCurve.IsClosed ||
                    !fullCurve.IsCyclic ||
                    fullCurve.Length <=
                        shortCurveTolerance * 2.1)
                {
                    error =
                        "The periodic generator curve for " +
                        context +
                        " is invalid, non-cyclic, or too short.";

                    return false;
                }

                double startParameter =
                    fullCurve.GetEndParameter(0);

                double endParameter =
                    fullCurve.GetEndParameter(1);

                XYZ oppositeSample =
                    samples[
                        (normalizedStart +
                         (sampleCount / 2)) %
                        sampleCount];

                IntersectionResult oppositeProjection =
                    fullCurve.Project(
                        oppositeSample);

                double maximumProjectionDistance =
                    Math.Max(
                        GeometryTolerance * 100.0,
                        SmoothBRepMaximumDeviationMillimetres /
                        FeetToMillimetres);

                if (oppositeProjection == null ||
                    oppositeProjection.Distance >
                        maximumProjectionDistance)
                {
                    error =
                        "The opposite split point for " +
                        context +
                        " could not be resolved on its periodic curve.";

                    return false;
                }

                double middleParameter =
                    oppositeProjection.Parameter;

                if (middleParameter <=
                        startParameter ||
                    middleParameter >=
                        endParameter)
                {
                    error =
                        "The opposite split parameter for " +
                        context +
                        " is outside the bounded periodic curve.";

                    return false;
                }

                firstHalfCurve =
                    fullCurve.Clone();

                firstHalfCurve.MakeBound(
                    startParameter,
                    middleParameter);

                secondHalfCurve =
                    fullCurve.Clone();

                secondHalfCurve.MakeBound(
                    middleParameter,
                    endParameter);

                double minimumAcceptedLength =
                    shortCurveTolerance *
                    1.05;

                if (firstHalfCurve.Length <=
                        minimumAcceptedLength ||
                    secondHalfCurve.Length <=
                        minimumAcceptedLength)
                {
                    error =
                        "A half-edge of " +
                        context +
                        " is below Revit's short-curve tolerance.";

                    return false;
                }

                double maximumConnectionGap =
                    Math.Max(
                        firstHalfCurve.GetEndPoint(1)
                            .DistanceTo(
                                secondHalfCurve.GetEndPoint(0)),
                        secondHalfCurve.GetEndPoint(1)
                            .DistanceTo(
                                firstHalfCurve.GetEndPoint(0)));

                double maximumAcceptedGap =
                    Math.Max(
                        GeometryTolerance * 100.0,
                        shortCurveTolerance * 1.0e-5);

                if (maximumConnectionGap >
                    maximumAcceptedGap)
                {
                    error =
                        "The two topological half-edges for " +
                        context +
                        " do not close. Maximum gap: " +
                        (maximumConnectionGap * FeetToMillimetres)
                            .ToString(
                                "0.######",
                                CultureInfo.InvariantCulture) +
                        " mm.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The continuous periodic generator for " +
                    context +
                    " could not be created: " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryCreatePeriodicHermiteStripSurface(
                IList<XYZ> firstRing,
                IList<XYZ> secondRing,
                int sampleCount,
                int startSample,
                out Surface surface,
                out string error)
        {
            surface = null;
            error = null;

            if (firstRing == null ||
                secondRing == null ||
                sampleCount < 8 ||
                firstRing.Count != sampleCount ||
                secondRing.Count != sampleCount)
            {
                error =
                    "The periodic Hermite strip inputs are incomplete.";

                return false;
            }

            try
            {
                int normalizedStart =
                    ((startSample % sampleCount) +
                     sampleCount) %
                    sampleCount;

                // HermiteSurface.Create describes a periodic point net as
                // one fewer supplied row in the periodic direction. Therefore
                // nU is uniqueSampleCount + 1 while the list contains exactly
                // uniqueSampleCount rows. Points are U-major, then V.
                List<XYZ> pointNet =
                    new List<XYZ>(
                        sampleCount * 2);

                for (int index = 0;
                     index < sampleCount;
                     index++)
                {
                    int sampleIndex =
                        (normalizedStart + index) %
                        sampleCount;

                    pointNet.Add(
                        firstRing[sampleIndex]);

                    pointNet.Add(
                        secondRing[sampleIndex]);
                }

                HermiteSurface hermiteSurface =
                    HermiteSurface.Create(
                        sampleCount + 1,
                        2,
                        pointNet,
                        true,
                        false);

                if (hermiteSurface == null)
                {
                    error =
                        "Revit returned no periodic Hermite support surface.";

                    return false;
                }

                if (!BRepBuilder.IsPermittedSurfaceType(
                        hermiteSurface))
                {
                    error =
                        "This Revit version does not permit HermiteSurface " +
                        "as BRepBuilder face support geometry.";

                    hermiteSurface.Dispose();
                    return false;
                }

                surface = hermiteSurface;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The periodic Hermite strip could not be created: " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryCreateContinuousHeaderCylinderSurface(
                XYZ headerAxisPoint,
                XYZ headerAxisDirection,
                double headerOutsideRadius,
                IList<XYZ> saddleRoot,
                IList<XYZ> saddleInner,
                Curve saddleRootCurve,
                Curve saddleInnerCurve,
                out CylindricalSurface cylinder,
                out string error)
        {
            cylinder = null;
            error = null;

            if (headerAxisPoint == null ||
                headerAxisDirection == null ||
                headerAxisDirection.GetLength() <=
                    GeometryTolerance ||
                headerOutsideRadius <=
                    GeometryTolerance ||
                saddleRoot == null ||
                saddleInner == null ||
                saddleRoot.Count < 16 ||
                saddleInner.Count !=
                    saddleRoot.Count ||
                saddleRootCurve == null ||
                saddleInnerCurve == null)
            {
                error =
                    "The analytic header-cylinder support for the weld " +
                    "land is incomplete.";

                return false;
            }

            XYZ axisDirection =
                headerAxisDirection.Normalize();

            // Place the analytic cylinder's parametric seam on the side
            // opposite the branch opening. The previous implementation used
            // saddleRoot[0], which put the cylinder seam directly through the
            // trimming loops and caused BRepBuilder to reject the intended
            // one-face weld-land topology.
            XYZ openingCenter =
                new XYZ(
                    saddleRoot
                        .Concat(saddleInner)
                        .Average(point => point.X),
                    saddleRoot
                        .Concat(saddleInner)
                        .Average(point => point.Y),
                    saddleRoot
                        .Concat(saddleInner)
                        .Average(point => point.Z));

            XYZ openingOffset =
                openingCenter -
                headerAxisPoint;

            XYZ openingRadial =
                openingOffset -
                (axisDirection *
                 openingOffset.DotProduct(
                     axisDirection));

            XYZ radialVector =
                openingRadial.GetLength() >
                    GeometryTolerance
                    ? openingRadial * -1.0
                    : (saddleRoot[0] -
                       headerAxisPoint);

            radialVector -=
                axisDirection *
                radialVector.DotProduct(
                    axisDirection);

            if (radialVector.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The header-cylinder radial frame for the continuous " +
                    "weld land is invalid.";

                return false;
            }

            XYZ radialX =
                radialVector.Normalize();

            XYZ radialY =
                axisDirection.CrossProduct(
                    radialX);

            if (radialY.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The header-cylinder secondary frame direction for " +
                    "the continuous weld land is invalid.";

                return false;
            }

            radialY =
                radialY.Normalize();

            double maximumRadialDeviation =
                saddleRoot
                    .Concat(saddleInner)
                    .Select(point =>
                    {
                        XYZ offset =
                            point -
                            headerAxisPoint;

                        XYZ radial =
                            offset -
                            (axisDirection *
                             offset.DotProduct(
                                 axisDirection));

                        return Math.Abs(
                            radial.GetLength() -
                            headerOutsideRadius);
                    })
                    .Max();

            // Also sample the actual continuous trimming curves. Their raw
            // interpolation points lie on the header cylinder, but this check
            // catches a spline fit whose between-point deviation is too high
            // for an analytic cylindrical face.
            foreach (Curve curve in
                     new[]
                     {
                         saddleRootCurve,
                         saddleInnerCurve
                     })
            {
                double startParameter =
                    curve.GetEndParameter(0);

                double endParameter =
                    curve.GetEndParameter(1);

                const int validationSampleCount = 64;

                for (int index = 0;
                     index <= validationSampleCount;
                     index++)
                {
                    double parameter =
                        startParameter +
                        ((endParameter - startParameter) *
                         index /
                         validationSampleCount);

                    XYZ point =
                        curve.Evaluate(
                            parameter,
                            false);

                    XYZ offset =
                        point -
                        headerAxisPoint;

                    XYZ radial =
                        offset -
                        (axisDirection *
                         offset.DotProduct(
                             axisDirection));

                    maximumRadialDeviation =
                        Math.Max(
                            maximumRadialDeviation,
                            Math.Abs(
                                radial.GetLength() -
                                headerOutsideRadius));
                }
            }

            double maximumAcceptedRadialDeviation =
                Math.Max(
                    GeometryTolerance * 100.0,
                    SmoothBRepMaximumDeviationMillimetres /
                    FeetToMillimetres);

            if (maximumRadialDeviation >
                maximumAcceptedRadialDeviation)
            {
                error =
                    "The continuous weld-land boundary is not on the " +
                    "header cylinder within the configured BRep tolerance. " +
                    "Maximum radial deviation: " +
                    (maximumRadialDeviation * FeetToMillimetres)
                        .ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            Frame frame =
                new Frame(
                    headerAxisPoint,
                    radialX,
                    radialY,
                    axisDirection);

            cylinder =
                CylindricalSurface.Create(
                    frame,
                    headerOutsideRadius);

            if (cylinder == null ||
                !BRepBuilder.IsPermittedSurfaceType(
                    cylinder))
            {
                error =
                    "Revit did not create a permitted analytic header " +
                    "cylinder for the continuous weld land.";

                cylinder = null;
                return false;
            }

            return true;
        }

        private static bool
            TryAddContinuousTwoLoopFace(
                BRepBuilder builder,
                BRepBuilderSurfaceGeometry surfaceGeometry,
                BRepBuilderGeometryId[,] ringEdgeIds,
                Curve[,] ringCurves,
                int firstRingIndex,
                int secondRingIndex,
                bool reverseSurface,
                double shortCurveTolerance,
                string context,
                out string error)
        {
            error = null;

            if (builder == null ||
                surfaceGeometry == null ||
                ringEdgeIds == null ||
                ringCurves == null)
            {
                error =
                    "The continuous two-loop face for " +
                    context +
                    " is incomplete.";

                return false;
            }

            BRepBuilderGeometryId faceId =
                builder.AddFace(
                    surfaceGeometry,
                    reverseSurface);

            BRepBuilderGeometryId firstLoopId =
                builder.AddLoop(
                    faceId);

            if (!TryAddConnectedBRepLoop(
                    builder,
                    firstLoopId,
                    new[]
                    {
                        ringEdgeIds[firstRingIndex, 0],
                        ringEdgeIds[firstRingIndex, 1]
                    },
                    new[]
                    {
                        ringCurves[firstRingIndex, 0],
                        ringCurves[firstRingIndex, 1]
                    },
                    new[]
                    {
                        false,
                        false
                    },
                    shortCurveTolerance,
                    context + " first circumferential loop",
                    out error))
            {
                return false;
            }

            builder.FinishLoop(
                firstLoopId);

            BRepBuilderGeometryId secondLoopId =
                builder.AddLoop(
                    faceId);

            if (!TryAddConnectedBRepLoop(
                    builder,
                    secondLoopId,
                    new[]
                    {
                        ringEdgeIds[secondRingIndex, 1],
                        ringEdgeIds[secondRingIndex, 0]
                    },
                    new[]
                    {
                        ringCurves[secondRingIndex, 1],
                        ringCurves[secondRingIndex, 0]
                    },
                    new[]
                    {
                        true,
                        true
                    },
                    shortCurveTolerance,
                    context + " second circumferential loop",
                    out error))
            {
                return false;
            }

            builder.FinishLoop(
                secondLoopId);

            builder.FinishFace(
                faceId);

            return true;
        }

        private static bool
            TryAddPlainOutletAnnularFace(
                BRepBuilder builder,
                IList<IList<XYZ>> ringSamples,
                BRepBuilderGeometryId[,] ringEdgeIds,
                Curve[,] ringCurves,
                int outerRingIndex,
                int innerRingIndex,
                int edgePatchCount,
                double shortCurveTolerance,
                out string error)
        {
            error = null;

            XYZ outletFaceOrigin =
                AverageRingPoint(
                    ringSamples[outerRingIndex]);

            XYZ outletFaceNormal =
                ResolveRingPlaneNormal(
                    ringSamples[outerRingIndex],
                    outletFaceOrigin);

            if (outletFaceNormal == null ||
                outletFaceNormal.GetLength() <=
                    GeometryTolerance)
            {
                error =
                    "The continuous-top branch outlet plane could not be " +
                    "resolved.";

                return false;
            }

            Plane outletPlane =
                Plane.CreateByNormalAndOrigin(
                    outletFaceNormal,
                    outletFaceOrigin);

            BRepBuilderGeometryId outletFaceId =
                builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(
                        outletPlane,
                        null),
                    false);

            BRepBuilderGeometryId outerLoopId =
                builder.AddLoop(
                    outletFaceId);

            List<BRepBuilderGeometryId> outerEdgeIds =
                new List<BRepBuilderGeometryId>();

            List<Curve> outerCurves =
                new List<Curve>();

            List<bool> outerFlags =
                new List<bool>();

            for (int patchIndex =
                     edgePatchCount - 1;
                 patchIndex >= 0;
                 patchIndex--)
            {
                outerEdgeIds.Add(
                    ringEdgeIds[
                        outerRingIndex,
                        patchIndex]);

                outerCurves.Add(
                    ringCurves[
                        outerRingIndex,
                        patchIndex]);

                outerFlags.Add(true);
            }

            string loopError;

            if (!TryAddConnectedBRepLoop(
                    builder,
                    outerLoopId,
                    outerEdgeIds,
                    outerCurves,
                    outerFlags,
                    shortCurveTolerance,
                    "continuous-top plain outlet outer loop",
                    out loopError))
            {
                error = loopError;
                return false;
            }

            builder.FinishLoop(
                outerLoopId);

            BRepBuilderGeometryId innerLoopId =
                builder.AddLoop(
                    outletFaceId);

            List<BRepBuilderGeometryId> innerEdgeIds =
                new List<BRepBuilderGeometryId>();

            List<Curve> innerCurves =
                new List<Curve>();

            List<bool> innerFlags =
                new List<bool>();

            for (int patchIndex = 0;
                 patchIndex < edgePatchCount;
                 patchIndex++)
            {
                innerEdgeIds.Add(
                    ringEdgeIds[
                        innerRingIndex,
                        patchIndex]);

                innerCurves.Add(
                    ringCurves[
                        innerRingIndex,
                        patchIndex]);

                innerFlags.Add(false);
            }

            if (!TryAddConnectedBRepLoop(
                    builder,
                    innerLoopId,
                    innerEdgeIds,
                    innerCurves,
                    innerFlags,
                    shortCurveTolerance,
                    "continuous-top plain outlet inner loop",
                    out loopError))
            {
                error = loopError;
                return false;
            }

            builder.FinishLoop(
                innerLoopId);

            builder.FinishFace(
                outletFaceId);

            return true;
        }


        private static bool
            TryBuildSingleBodySmoothBRepSetOnBranch(
                IList<IList<XYZ>> ringSamples,
                IList<IList<XYZ>[]> bandRows,
                IList<int> lowerRingIndexes,
                IList<int> upperRingIndexes,
                IList<int> degreeV,
                IList<IList<double>> knotsVByBand,
                bool outletShouldChamfer,
                int sampleCount,
                int patchCount,
                int splineSpanCount,
                int patchStartOffset,
                double shortCurveTolerance,
                bool useAnalyticStraightCylinders,
                int outerAnalyticCylinderBandIndex,
                int innerAnalyticCylinderBandIndex,
                bool useSeamFreeSingleFaceCylinders,
                out Solid solid,
                out string error)
        {
            solid = null;
            error = null;

            if (ringSamples == null ||
                bandRows == null ||
                lowerRingIndexes == null ||
                upperRingIndexes == null ||
                degreeV == null ||
                knotsVByBand == null ||
                bandRows.Count == 0 ||
                bandRows.Count != lowerRingIndexes.Count ||
                bandRows.Count != upperRingIndexes.Count ||
                bandRows.Count != degreeV.Count ||
                bandRows.Count != knotsVByBand.Count ||
                (patchCount != 2 && patchCount != 4) ||
                splineSpanCount < patchCount ||
                (splineSpanCount % patchCount) != 0 ||
                sampleCount <= 0 ||
                (sampleCount % splineSpanCount) != 0 ||
                (sampleCount % patchCount) != 0 ||
                (useAnalyticStraightCylinders &&
                 (patchCount != 2 ||
                  outerAnalyticCylinderBandIndex < 0 ||
                  innerAnalyticCylinderBandIndex < 0 ||
                  outerAnalyticCylinderBandIndex >= bandRows.Count ||
                  innerAnalyticCylinderBandIndex >= bandRows.Count ||
                  outerAnalyticCylinderBandIndex ==
                    innerAnalyticCylinderBandIndex)) ||
                (!useAnalyticStraightCylinders &&
                 (outerAnalyticCylinderBandIndex != -1 ||
                  innerAnalyticCylinderBandIndex != -1 ||
                  useSeamFreeSingleFaceCylinders)))
            {
                error =
                    "The compact smooth BRep builder inputs are incomplete.";

                return false;
            }

            for (int bandIndex = 0;
                 bandIndex < bandRows.Count;
                 bandIndex++)
            {
                IList<XYZ>[] rows =
                    bandRows[bandIndex];

                if (rows == null ||
                    rows.Length < 2 ||
                    degreeV[bandIndex] < 1 ||
                    rows.Length < degreeV[bandIndex] + 1 ||
                    rows.Any(x =>
                        x == null ||
                        x.Count != sampleCount))
                {
                    error =
                        "Surface band " +
                        bandIndex.ToString(
                            CultureInfo.InvariantCulture) +
                        " has an invalid control net.";

                    return false;
                }

                IList<double> customKnots =
                    knotsVByBand[bandIndex];

                if (customKnots != null &&
                    customKnots.Count !=
                        rows.Length +
                        degreeV[bandIndex] +
                        1)
                {
                    error =
                        "Surface band " +
                        bandIndex.ToString(
                            CultureInfo.InvariantCulture) +
                        " has an invalid custom V knot vector.";

                    return false;
                }
            }

            int spansPerPatch =
                splineSpanCount /
                patchCount;

            int samplesPerSpan =
                sampleCount /
                splineSpanCount;

            int samplesPerPatch =
                sampleCount /
                patchCount;

            IList<double> knotsU =
                CreateClampedCubicSplineKnots(
                    spansPerPatch);

            try
            {
                BRepBuilder builder =
                    new BRepBuilder(
                        BRepType.Solid);

                BRepBuilderGeometryId[,]
                    ringEdgeIds =
                        new BRepBuilderGeometryId[
                            ringSamples.Count,
                            patchCount];

                Curve[,] ringCurves =
                    new Curve[
                        ringSamples.Count,
                        patchCount];

                IList<XYZ>[,] ringControlPoints =
                    new IList<XYZ>[
                        ringSamples.Count,
                        patchCount];

                HashSet<int> requiredRingIndexes =
                    new HashSet<int>(
                        lowerRingIndexes
                            .Concat(upperRingIndexes));

                // Every topological ring edge and every surface boundary uses
                // the same exact control-point list. Non-topological guide
                // rings used only by a merged NURBS band are intentionally not
                // added as BRep edges, so they cannot become STEP face seams.
                for (int ringIndex = 0;
                     ringIndex < ringSamples.Count;
                     ringIndex++)
                {
                    if (!requiredRingIndexes.Contains(
                            ringIndex))
                    {
                        continue;
                    }

                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        int startSample =
                            (patchStartOffset +
                             (patchIndex *
                              samplesPerPatch)) %
                            sampleCount;

                        int endSample =
                            (patchStartOffset +
                             (((patchIndex + 1) %
                               patchCount) *
                              samplesPerPatch)) %
                            sampleCount;

                        IList<XYZ> controls =
                            CreatePeriodicInterpolatingCubicSplineControls(
                                ringSamples[ringIndex],
                                startSample,
                                samplesPerSpan,
                                spansPerPatch);

                        // Clamped cubic splines interpolate their first and
                        // last controls. Force those controls to the shared raw
                        // sample vertices so adjacent patches are topologically
                        // identical, not merely within a numeric tolerance.
                        controls[0] =
                            ringSamples[ringIndex][startSample];

                        controls[
                            controls.Count - 1] =
                                ringSamples[ringIndex][endSample];

                        Curve ringCurve;
                        string ringError;

                        if (!TryCreateToleranceSafeBRepBoundaryCurve(
                                3,
                                knotsU,
                                controls,
                                shortCurveTolerance,
                                "circumferential ring " +
                                ringIndex.ToString(
                                    CultureInfo.InvariantCulture) +
                                ", topological patch " +
                                patchIndex.ToString(
                                    CultureInfo.InvariantCulture),
                                out ringCurve,
                                out ringError))
                        {
                            error = ringError;
                            return false;
                        }

                        ringCurves[
                            ringIndex,
                            patchIndex] =
                                ringCurve;

                        ringControlPoints[
                            ringIndex,
                            patchIndex] =
                                controls;

                        ringEdgeIds[
                            ringIndex,
                            patchIndex] =
                                builder.AddEdge(
                                    BRepBuilderEdgeGeometry
                                        .Create(
                                            ringCurve));
                    }

                    double maximumClosureGap = 0.0;

                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        int nextPatch =
                            (patchIndex + 1) %
                            patchCount;

                        maximumClosureGap =
                            Math.Max(
                                maximumClosureGap,
                                ringCurves[
                                    ringIndex,
                                    patchIndex]
                                    .GetEndPoint(1)
                                    .DistanceTo(
                                        ringCurves[
                                            ringIndex,
                                            nextPatch]
                                            .GetEndPoint(0)));
                    }

                    if (maximumClosureGap >
                        Math.Max(
                            GeometryTolerance * 100.0,
                            shortCurveTolerance * 1.0e-5))
                    {
                        error =
                            "The circumferential ring " +
                            ringIndex.ToString(
                                CultureInfo.InvariantCulture) +
                            " did not close after Revit evaluated its " +
                            patchCount.ToString(
                                CultureInfo.InvariantCulture) +
                            " smooth topological patches. Maximum endpoint " +
                            "gap: " +
                            (maximumClosureGap * FeetToMillimetres)
                                .ToString(
                                    "0.######",
                                    CultureInfo.InvariantCulture) +
                            " mm.";

                        return false;
                    }
                }

                List<int> nurbsBandIndexes =
                    Enumerable.Range(
                            0,
                            bandRows.Count)
                        .Where(x =>
                            !useAnalyticStraightCylinders ||
                            (x != outerAnalyticCylinderBandIndex &&
                             x != innerAnalyticCylinderBandIndex))
                        .ToList();

                // Cache the exact control net used by every face before any
                // seam edge is created. Each seam curve is then made directly
                // from one boundary column of that same control net. This is
                // the critical DN200 x DN80 fix.
                List<IList<XYZ>>[,] bandPatchRowControls =
                    new List<IList<XYZ>>[
                        bandRows.Count,
                        patchCount];

                foreach (int bandIndex in
                         nurbsBandIndexes)
                {
                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        int startSample =
                            (patchStartOffset +
                             (patchIndex *
                              samplesPerPatch)) %
                            sampleCount;

                        int endSample =
                            (patchStartOffset +
                             (((patchIndex + 1) %
                               patchCount) *
                              samplesPerPatch)) %
                            sampleCount;

                        List<IList<XYZ>> rowControls =
                            new List<IList<XYZ>>();

                        for (int rowIndex = 0;
                             rowIndex <
                                bandRows[bandIndex].Length;
                             rowIndex++)
                        {
                            IList<XYZ> controls;

                            if (rowIndex == 0)
                            {
                                controls =
                                    ringControlPoints[
                                        lowerRingIndexes[
                                            bandIndex],
                                        patchIndex];
                            }
                            else if (rowIndex ==
                                     bandRows[bandIndex].Length - 1)
                            {
                                controls =
                                    ringControlPoints[
                                        upperRingIndexes[
                                            bandIndex],
                                        patchIndex];
                            }
                            else
                            {
                                controls =
                                    CreatePeriodicInterpolatingCubicSplineControls(
                                        bandRows[bandIndex][
                                            rowIndex],
                                        startSample,
                                        samplesPerSpan,
                                        spansPerPatch);

                                controls[0] =
                                    bandRows[bandIndex][
                                        rowIndex][startSample];

                                controls[
                                    controls.Count - 1] =
                                        bandRows[bandIndex][
                                            rowIndex][endSample];
                            }

                            rowControls.Add(
                                controls);
                        }

                        bandPatchRowControls[
                            bandIndex,
                            patchIndex] =
                                rowControls;
                    }

                    // Prove that each adjacent face uses the same exact seam
                    // control column before submitting anything to BRepBuilder.
                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        int nextPatch =
                            (patchIndex + 1) %
                            patchCount;

                        List<IList<XYZ>> currentRows =
                            bandPatchRowControls[
                                bandIndex,
                                patchIndex];

                        List<IList<XYZ>> nextRows =
                            bandPatchRowControls[
                                bandIndex,
                                nextPatch];

                        for (int rowIndex = 0;
                             rowIndex < currentRows.Count;
                             rowIndex++)
                        {
                            XYZ currentEnd =
                                currentRows[rowIndex][
                                    currentRows[rowIndex].Count - 1];

                            XYZ nextStart =
                                nextRows[rowIndex][0];

                            double sharedCornerGap =
                                currentEnd.DistanceTo(
                                    nextStart);

                            if (sharedCornerGap >
                                Math.Max(
                                    GeometryTolerance * 100.0,
                                    shortCurveTolerance * 1.0e-6))
                            {
                                error =
                                    "The exact control net for surface band " +
                                    bandIndex.ToString(
                                        CultureInfo.InvariantCulture) +
                                    ", topological seam " +
                                    nextPatch.ToString(
                                        CultureInfo.InvariantCulture) +
                                    " is discontinuous. Gap: " +
                                    (sharedCornerGap * FeetToMillimetres)
                                        .ToString(
                                            "0.######",
                                            CultureInfo.InvariantCulture) +
                                    " mm.";

                                return false;
                            }
                        }
                    }
                }

                Dictionary<int, BRepBuilderGeometryId[]>
                    seamEdgeIdsByBand =
                        new Dictionary<int, BRepBuilderGeometryId[]>();

                Dictionary<int, Curve[]>
                    seamCurvesByBand =
                        new Dictionary<int, Curve[]>();

                foreach (int bandIndex in
                         nurbsBandIndexes)
                {
                    IList<double> seamKnots =
                        knotsVByBand[bandIndex] ??
                        CreateClampedBezierKnots(
                            degreeV[bandIndex]);

                    BRepBuilderGeometryId[] seamIds =
                        new BRepBuilderGeometryId[
                            patchCount];

                    Curve[] seamCurves =
                        new Curve[
                            patchCount];

                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        List<IList<XYZ>> rowControls =
                            bandPatchRowControls[
                                bandIndex,
                                patchIndex];

                        List<XYZ> seamControls =
                            rowControls
                                .Select(x => x[0])
                                .ToList();

                        Curve seamCurve;
                        string seamError;

                        if (!TryCreateToleranceSafeBRepBoundaryCurve(
                                degreeV[bandIndex],
                                seamKnots,
                                seamControls,
                                shortCurveTolerance,
                                "surface band " +
                                bandIndex.ToString(
                                    CultureInfo.InvariantCulture) +
                                ", topological patch seam " +
                                patchIndex.ToString(
                                    CultureInfo.InvariantCulture),
                                out seamCurve,
                                out seamError))
                        {
                            error = seamError;
                            return false;
                        }

                        seamCurves[patchIndex] =
                            seamCurve;

                        seamIds[patchIndex] =
                            builder.AddEdge(
                                BRepBuilderEdgeGeometry
                                    .Create(
                                        seamCurve));
                    }

                    seamEdgeIdsByBand[bandIndex] =
                        seamIds;

                    seamCurvesByBand[bandIndex] =
                        seamCurves;
                }

                foreach (int bandIndex in
                         nurbsBandIndexes)
                {
                    IList<double> knotsV =
                        knotsVByBand[bandIndex] ??
                        CreateClampedBezierKnots(
                            degreeV[bandIndex]);

                    BRepBuilderGeometryId[] seamIds =
                        seamEdgeIdsByBand[bandIndex];

                    Curve[] seamCurves =
                        seamCurvesByBand[bandIndex];

                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        int nextPatch =
                            (patchIndex + 1) %
                            patchCount;

                        List<IList<XYZ>> rowControls =
                            bandPatchRowControls[
                                bandIndex,
                                patchIndex];

                        int controlsU =
                            rowControls[0].Count;

                        List<XYZ> surfaceControls =
                            new List<XYZ>(
                                controlsU *
                                rowControls.Count);

                        for (int u = 0;
                             u < controlsU;
                             u++)
                        {
                            for (int v = 0;
                                 v < rowControls.Count;
                                 v++)
                            {
                                surfaceControls.Add(
                                    rowControls[v][u]);
                            }
                        }

                        BRepBuilderSurfaceGeometry surface =
                            BRepBuilderSurfaceGeometry
                                .CreateNURBSSurface(
                                    3,
                                    degreeV[bandIndex],
                                    knotsU,
                                    knotsV,
                                    surfaceControls,
                                    false,
                                    null);

                        BRepBuilderGeometryId faceId =
                            builder.AddFace(
                                surface,
                                false);

                        BRepBuilderGeometryId loopId =
                            builder.AddLoop(faceId);

                        string loopError;

                        if (!TryAddConnectedBRepLoop(
                                builder,
                                loopId,
                                new[]
                                {
                                    ringEdgeIds[
                                        lowerRingIndexes[
                                            bandIndex],
                                        patchIndex],
                                    seamIds[nextPatch],
                                    ringEdgeIds[
                                        upperRingIndexes[
                                            bandIndex],
                                        patchIndex],
                                    seamIds[patchIndex]
                                },
                                new[]
                                {
                                    ringCurves[
                                        lowerRingIndexes[
                                            bandIndex],
                                        patchIndex],
                                    seamCurves[nextPatch],
                                    ringCurves[
                                        upperRingIndexes[
                                            bandIndex],
                                        patchIndex],
                                    seamCurves[patchIndex]
                                },
                                new[]
                                {
                                    false,
                                    false,
                                    true,
                                    true
                                },
                                shortCurveTolerance,
                                "surface band " +
                                bandIndex.ToString(
                                    CultureInfo.InvariantCulture) +
                                ", topological patch " +
                                patchIndex.ToString(
                                    CultureInfo.InvariantCulture),
                                out loopError))
                        {
                            error = loopError;
                            return false;
                        }

                        builder.FinishLoop(loopId);
                        builder.FinishFace(faceId);
                    }
                }

                if (useAnalyticStraightCylinders)
                {
                    string cylinderError;

                    IList<XYZ>[] outerCylinderRows =
                        bandRows[
                            outerAnalyticCylinderBandIndex];

                    if (!TryAddAnalyticCylindricalBranchBand(
                            builder,
                            ringEdgeIds,
                            ringCurves,
                            outerCylinderRows[0],
                            outerCylinderRows[
                                outerCylinderRows.Length - 1],
                            outerCylinderRows[0],
                            lowerRingIndexes[
                                outerAnalyticCylinderBandIndex],
                            upperRingIndexes[
                                outerAnalyticCylinderBandIndex],
                            false,
                            patchCount,
                            patchStartOffset,
                            shortCurveTolerance,
                            useSeamFreeSingleFaceCylinders,
                            "complete branch outside wall",
                            out cylinderError))
                    {
                        error = cylinderError;
                        return false;
                    }

                    IList<XYZ>[] innerCylinderRows =
                        bandRows[
                            innerAnalyticCylinderBandIndex];

                    if (!TryAddAnalyticCylindricalBranchBand(
                            builder,
                            ringEdgeIds,
                            ringCurves,
                            innerCylinderRows[0],
                            innerCylinderRows[
                                innerCylinderRows.Length - 1],
                            innerCylinderRows[
                                innerCylinderRows.Length - 1],
                            lowerRingIndexes[
                                innerAnalyticCylinderBandIndex],
                            upperRingIndexes[
                                innerAnalyticCylinderBandIndex],
                            true,
                            patchCount,
                            patchStartOffset,
                            shortCurveTolerance,
                            useSeamFreeSingleFaceCylinders,
                            "complete branch bore wall",
                            out cylinderError))
                    {
                        error = cylinderError;
                        return false;
                    }
                }

                if (!outletShouldChamfer)
                {
                    XYZ outletFaceOrigin =
                        AverageRingPoint(
                            ringSamples[0]);

                    XYZ outletFaceNormal =
                        ResolveRingPlaneNormal(
                            ringSamples[0],
                            outletFaceOrigin);

                    if (outletFaceNormal == null ||
                        outletFaceNormal.GetLength() <=
                            GeometryTolerance)
                    {
                        error =
                            "The single-body branch outlet plane could not " +
                            "be resolved.";

                        return false;
                    }

                    Plane outletPlane =
                        Plane.CreateByNormalAndOrigin(
                            outletFaceNormal,
                            outletFaceOrigin);

                    BRepBuilderGeometryId outletFaceId =
                        builder.AddFace(
                            BRepBuilderSurfaceGeometry
                                .Create(
                                    outletPlane,
                                    null),
                            false);

                    BRepBuilderGeometryId outerLoopId =
                        builder.AddLoop(
                            outletFaceId);

                    List<BRepBuilderGeometryId> outerEdgeIds =
                        new List<BRepBuilderGeometryId>();

                    List<Curve> outerCurves =
                        new List<Curve>();

                    List<bool> outerFlags =
                        new List<bool>();

                    for (int patchIndex =
                             patchCount - 1;
                         patchIndex >= 0;
                         patchIndex--)
                    {
                        outerEdgeIds.Add(
                            ringEdgeIds[0, patchIndex]);

                        outerCurves.Add(
                            ringCurves[0, patchIndex]);

                        outerFlags.Add(true);
                    }

                    string outletLoopError;

                    if (!TryAddConnectedBRepLoop(
                            builder,
                            outerLoopId,
                            outerEdgeIds,
                            outerCurves,
                            outerFlags,
                            shortCurveTolerance,
                            "plain outlet outer loop",
                            out outletLoopError))
                    {
                        error = outletLoopError;
                        return false;
                    }

                    builder.FinishLoop(
                        outerLoopId);

                    BRepBuilderGeometryId innerLoopId =
                        builder.AddLoop(
                            outletFaceId);

                    List<BRepBuilderGeometryId> innerEdgeIds =
                        new List<BRepBuilderGeometryId>();

                    List<Curve> innerCurves =
                        new List<Curve>();

                    List<bool> innerFlags =
                        new List<bool>();

                    for (int patchIndex = 0;
                         patchIndex < patchCount;
                         patchIndex++)
                    {
                        innerEdgeIds.Add(
                            ringEdgeIds[6, patchIndex]);

                        innerCurves.Add(
                            ringCurves[6, patchIndex]);

                        innerFlags.Add(false);
                    }

                    if (!TryAddConnectedBRepLoop(
                            builder,
                            innerLoopId,
                            innerEdgeIds,
                            innerCurves,
                            innerFlags,
                            shortCurveTolerance,
                            "plain outlet inner loop",
                            out outletLoopError))
                    {
                        error = outletLoopError;
                        return false;
                    }

                    builder.FinishLoop(
                        innerLoopId);

                    builder.FinishFace(
                        outletFaceId);
                }

                builder.Finish();

                if (!builder.IsResultAvailable())
                {
                    error =
                        "Revit rejected the compact single-body smooth " +
                        "BRep SET-ON shaped branch.";

                    return false;
                }

                solid =
                    builder.GetResult();

                if (solid == null ||
                    solid.Volume <=
                        GeometryTolerance)
                {
                    error =
                        "Revit returned an empty compact single-body " +
                        "smooth BRep SET-ON shaped branch.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The compact single-body smooth BRep SET-ON shaped " +
                    "branch could not be generated: " +
                    ex.Message;

                return false;
            }
        }


        private static bool
            TryAddAnalyticCylindricalBranchBand(
                BRepBuilder builder,
                BRepBuilderGeometryId[,] ringEdgeIds,
                Curve[,] ringCurves,
                IList<XYZ> firstRing,
                IList<XYZ> secondRing,
                IList<XYZ> planarReferenceRing,
                int firstRingIndex,
                int secondRingIndex,
                bool reverseSurface,
                int patchCount,
                int patchStartOffset,
                double shortCurveTolerance,
                bool useSeamFreeSingleFace,
                string context,
                out string error)
        {
            error = null;

            if (builder == null ||
                ringEdgeIds == null ||
                ringCurves == null ||
                firstRing == null ||
                secondRing == null ||
                planarReferenceRing == null ||
                firstRing.Count < 16 ||
                secondRing.Count != firstRing.Count ||
                planarReferenceRing.Count != firstRing.Count ||
                patchCount != 2)
            {
                error =
                    "The analytic cylindrical support for " +
                    context +
                    " is incomplete.";

                return false;
            }

            int sampleCount =
                firstRing.Count;

            int samplesPerPatch =
                sampleCount /
                patchCount;

            int firstSeamSample =
                ((patchStartOffset % sampleCount) +
                 sampleCount) %
                sampleCount;

            int oppositeSeamSample =
                (firstSeamSample +
                 samplesPerPatch) %
                sampleCount;

            XYZ axisOrigin =
                AverageRingPoint(
                    planarReferenceRing);

            if (axisOrigin == null)
            {
                error =
                    "The analytic cylinder axis for " +
                    context +
                    " could not be resolved.";

                return false;
            }

            XYZ axisDirection =
                ResolveRingPlaneNormal(
                    planarReferenceRing,
                    axisOrigin);

            if (axisDirection == null ||
                axisDirection.GetLength() <=
                    GeometryTolerance)
            {
                error =
                    "The analytic cylinder orientation for " +
                    context +
                    " could not be resolved.";

                return false;
            }

            axisDirection =
                axisDirection.Normalize();

            XYZ radialVector =
                planarReferenceRing[firstSeamSample] -
                axisOrigin;

            radialVector -=
                axisDirection *
                radialVector.DotProduct(
                    axisDirection);

            if (radialVector.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The analytic cylinder radius direction for " +
                    context +
                    " is invalid.";

                return false;
            }

            XYZ radialX =
                radialVector.Normalize();

            XYZ radialY =
                axisDirection.CrossProduct(
                    radialX);

            if (radialY.GetLength() <=
                GeometryTolerance)
            {
                error =
                    "The analytic cylinder secondary direction for " +
                    context +
                    " is invalid.";

                return false;
            }

            radialY = radialY.Normalize();

            double radius =
                planarReferenceRing
                    .Average(point =>
                    {
                        XYZ fromAxisOrigin =
                            point - axisOrigin;

                        XYZ radial =
                            fromAxisOrigin -
                            (axisDirection *
                             fromAxisOrigin.DotProduct(
                                 axisDirection));

                        return radial.GetLength();
                    });

            double maximumRadialDeviation =
                firstRing
                    .Concat(secondRing)
                    .Select(point =>
                    {
                        XYZ fromAxisOrigin =
                            point - axisOrigin;

                        XYZ radial =
                            fromAxisOrigin -
                            (axisDirection *
                             fromAxisOrigin.DotProduct(
                                 axisDirection));

                        return Math.Abs(
                            radial.GetLength() -
                            radius);
                    })
                    .Max();

            double maximumAcceptedRadialDeviation =
                Math.Max(
                    GeometryTolerance * 100.0,
                    SmoothBRepMaximumDeviationMillimetres /
                    FeetToMillimetres);

            if (radius <= GeometryTolerance ||
                maximumRadialDeviation >
                    maximumAcceptedRadialDeviation)
            {
                error =
                    "The complete trimmed wall for " +
                    context +
                    " is not cylindrical within the configured smooth " +
                    "BRep tolerance. Maximum radial deviation: " +
                    (maximumRadialDeviation * FeetToMillimetres)
                        .ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            Frame frame =
                new Frame(
                    axisOrigin,
                    radialX,
                    radialY,
                    axisDirection);

            CylindricalSurface cylinder =
                CylindricalSurface.Create(
                    frame,
                    radius);

            if (useSeamFreeSingleFace)
            {
                return TryAddSeamFreeAnalyticCylindricalFace(
                    builder,
                    cylinder,
                    ringEdgeIds,
                    ringCurves,
                    firstRingIndex,
                    secondRingIndex,
                    reverseSurface,
                    shortCurveTolerance,
                    context,
                    out error);
            }

            XYZ firstSeamStart =
                ringCurves[
                    firstRingIndex,
                    0].GetEndPoint(0);

            XYZ firstSeamEnd =
                ringCurves[
                    secondRingIndex,
                    0].GetEndPoint(0);

            XYZ oppositeSeamStart =
                ringCurves[
                    firstRingIndex,
                    1].GetEndPoint(0);

            XYZ oppositeSeamEnd =
                ringCurves[
                    secondRingIndex,
                    1].GetEndPoint(0);

            double minimumAcceptedLength =
                shortCurveTolerance *
                1.05;

            if (firstSeamStart.DistanceTo(
                    firstSeamEnd) <=
                    minimumAcceptedLength ||
                oppositeSeamStart.DistanceTo(
                    oppositeSeamEnd) <=
                    minimumAcceptedLength)
            {
                error =
                    "The analytic cylinder seam for " +
                    context +
                    " is below Revit's short-curve tolerance.";

                return false;
            }

            Curve firstSeamCurve =
                Line.CreateBound(
                    firstSeamStart,
                    firstSeamEnd);

            Curve oppositeSeamCurve =
                Line.CreateBound(
                    oppositeSeamStart,
                    oppositeSeamEnd);

            BRepBuilderGeometryId firstSeamEdgeId =
                builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(
                        firstSeamCurve));

            BRepBuilderGeometryId oppositeSeamEdgeId =
                builder.AddEdge(
                    BRepBuilderEdgeGeometry.Create(
                        oppositeSeamCurve));

            BRepBuilderGeometryId firstFaceId =
                builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(
                        cylinder,
                        null),
                    reverseSurface);

            BRepBuilderGeometryId secondFaceId =
                builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(
                        cylinder,
                        null),
                    reverseSurface);

            AddAnalyticCylindricalHalfFace(
                builder,
                firstFaceId,
                ringEdgeIds,
                ringCurves,
                firstRingIndex,
                secondRingIndex,
                0,
                firstSeamEdgeId,
                oppositeSeamEdgeId,
                firstSeamCurve,
                oppositeSeamCurve,
                shortCurveTolerance,
                context + " first half",
                out error);

            if (!string.IsNullOrWhiteSpace(error))
            {
                return false;
            }

            AddAnalyticCylindricalHalfFace(
                builder,
                secondFaceId,
                ringEdgeIds,
                ringCurves,
                firstRingIndex,
                secondRingIndex,
                1,
                oppositeSeamEdgeId,
                firstSeamEdgeId,
                oppositeSeamCurve,
                firstSeamCurve,
                shortCurveTolerance,
                context + " second half",
                out error);

            return string.IsNullOrWhiteSpace(error);
        }

        private static bool
            TryAddSeamFreeAnalyticCylindricalFace(
                BRepBuilder builder,
                CylindricalSurface cylinder,
                BRepBuilderGeometryId[,] ringEdgeIds,
                Curve[,] ringCurves,
                int firstRingIndex,
                int secondRingIndex,
                bool reverseSurface,
                double shortCurveTolerance,
                string context,
                out string error)
        {
            error = null;

            if (builder == null ||
                cylinder == null ||
                ringEdgeIds == null ||
                ringCurves == null)
            {
                error =
                    "The seam-free analytic cylindrical face for " +
                    context +
                    " is incomplete.";

                return false;
            }

            BRepBuilderGeometryId faceId =
                builder.AddFace(
                    BRepBuilderSurfaceGeometry.Create(
                        cylinder,
                        null),
                    reverseSurface);

            BRepBuilderGeometryId firstLoopId =
                builder.AddLoop(
                    faceId);

            if (!TryAddConnectedBRepLoop(
                    builder,
                    firstLoopId,
                    new[]
                    {
                        ringEdgeIds[firstRingIndex, 0],
                        ringEdgeIds[firstRingIndex, 1]
                    },
                    new[]
                    {
                        ringCurves[firstRingIndex, 0],
                        ringCurves[firstRingIndex, 1]
                    },
                    new[]
                    {
                        false,
                        false
                    },
                    shortCurveTolerance,
                    context + " first circumferential loop",
                    out error))
            {
                return false;
            }

            builder.FinishLoop(
                firstLoopId);

            BRepBuilderGeometryId secondLoopId =
                builder.AddLoop(
                    faceId);

            if (!TryAddConnectedBRepLoop(
                    builder,
                    secondLoopId,
                    new[]
                    {
                        ringEdgeIds[secondRingIndex, 1],
                        ringEdgeIds[secondRingIndex, 0]
                    },
                    new[]
                    {
                        ringCurves[secondRingIndex, 1],
                        ringCurves[secondRingIndex, 0]
                    },
                    new[]
                    {
                        true,
                        true
                    },
                    shortCurveTolerance,
                    context + " second circumferential loop",
                    out error))
            {
                return false;
            }

            builder.FinishLoop(
                secondLoopId);

            builder.FinishFace(
                faceId);

            return true;
        }

        private static bool
            AddAnalyticCylindricalHalfFace(
                BRepBuilder builder,
                BRepBuilderGeometryId faceId,
                BRepBuilderGeometryId[,] ringEdgeIds,
                Curve[,] ringCurves,
                int firstRingIndex,
                int secondRingIndex,
                int patchIndex,
                BRepBuilderGeometryId startSeamEdgeId,
                BRepBuilderGeometryId endSeamEdgeId,
                Curve startSeamCurve,
                Curve endSeamCurve,
                double shortCurveTolerance,
                string context,
                out string error)
        {
            error = null;
            BRepBuilderGeometryId loopId =
                builder.AddLoop(
                    faceId);

            if (!TryAddConnectedBRepLoop(
                    builder,
                    loopId,
                    new[]
                    {
                        ringEdgeIds[
                            firstRingIndex,
                            patchIndex],
                        endSeamEdgeId,
                        ringEdgeIds[
                            secondRingIndex,
                            patchIndex],
                        startSeamEdgeId
                    },
                    new[]
                    {
                        ringCurves[
                            firstRingIndex,
                            patchIndex],
                        endSeamCurve,
                        ringCurves[
                            secondRingIndex,
                            patchIndex],
                        startSeamCurve
                    },
                    new[]
                    {
                        false,
                        false,
                        true,
                        true
                    },
                    shortCurveTolerance,
                    context,
                    out error))
            {
                return false;
            }

            builder.FinishLoop(
                loopId);

            builder.FinishFace(
                faceId);

            return true;
        }

        private static bool
            TryAddConnectedBRepLoop(
                BRepBuilder builder,
                BRepBuilderGeometryId loopId,
                IList<BRepBuilderGeometryId> edgeIds,
                IList<Curve> edgeCurves,
                IList<bool> preferredFlippedFlags,
                double shortCurveTolerance,
                string context,
                out string error)
        {
            error = null;

            if (builder == null ||
                edgeIds == null ||
                edgeCurves == null ||
                preferredFlippedFlags == null ||
                edgeIds.Count < 2 ||
                edgeIds.Count != edgeCurves.Count ||
                edgeIds.Count != preferredFlippedFlags.Count ||
                edgeCurves.Any(x => x == null))
            {
                error =
                    "The BRep loop inputs for " +
                    context +
                    " are incomplete.";

                return false;
            }

            int edgeCount =
                edgeIds.Count;

            bool[] bestFlags = null;
            double bestMaximumGap =
                double.MaxValue;
            int bestPreferenceChanges =
                int.MaxValue;

            int combinationCount =
                1 << edgeCount;

            for (int mask = 0;
                 mask < combinationCount;
                 mask++)
            {
                bool[] flags =
                    new bool[edgeCount];

                double maximumGap = 0.0;
                int preferenceChanges = 0;

                for (int index = 0;
                     index < edgeCount;
                     index++)
                {
                    flags[index] =
                        (mask & (1 << index)) != 0;

                    if (flags[index] !=
                        preferredFlippedFlags[index])
                    {
                        preferenceChanges++;
                    }
                }

                for (int index = 0;
                     index < edgeCount;
                     index++)
                {
                    int nextIndex =
                        (index + 1) %
                        edgeCount;

                    XYZ currentEnd =
                        edgeCurves[index]
                            .GetEndPoint(
                                flags[index]
                                    ? 0
                                    : 1);

                    XYZ nextStart =
                        edgeCurves[nextIndex]
                            .GetEndPoint(
                                flags[nextIndex]
                                    ? 1
                                    : 0);

                    maximumGap =
                        Math.Max(
                            maximumGap,
                            currentEnd.DistanceTo(
                                nextStart));
                }

                if (maximumGap + GeometryTolerance <
                        bestMaximumGap ||
                    (Math.Abs(
                         maximumGap -
                         bestMaximumGap) <=
                         GeometryTolerance &&
                     preferenceChanges <
                        bestPreferenceChanges))
                {
                    bestFlags = flags;
                    bestMaximumGap = maximumGap;
                    bestPreferenceChanges =
                        preferenceChanges;
                }
            }

            double maximumAcceptedGap =
                Math.Max(
                    GeometryTolerance * 100.0,
                    shortCurveTolerance * 1.0e-5);

            if (bestFlags == null ||
                bestMaximumGap >
                    maximumAcceptedGap)
            {
                error =
                    "The BRep loop for " +
                    context +
                    " could not be connected before it was submitted to " +
                    "Revit. Maximum endpoint gap: " +
                    (bestMaximumGap * FeetToMillimetres)
                        .ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            try
            {
                for (int index = 0;
                     index < edgeCount;
                     index++)
                {
                    builder.AddCoEdge(
                        loopId,
                        edgeIds[index],
                        bestFlags[index]);
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "Revit rejected the connected BRep loop for " +
                    context +
                    ": " +
                    ex.Message;

                return false;
            }
        }

        private static bool
            TryResolveToleranceSafeBRepPatchLayouts(
                IList<IList<XYZ>[]> bandRows,
                IList<int> degreeV,
                IList<IList<XYZ>> ringSamples,
                int sampleCount,
                double shortCurveTolerance,
                int topologicalPatchCount,
                int preferredSplineSpanCount,
                int fallbackSplineSpanCount,
                string diagnosticContext,
                out List<SmoothBRepPatchLayout> layouts,
                out string error)
        {
            layouts =
                new List<SmoothBRepPatchLayout>();

            error = null;

            FabricationBRepLayoutProbe diagnosticProbe =
                DiagnosticBeginLayoutProbe(
                    diagnosticContext,
                    topologicalPatchCount,
                    sampleCount,
                    preferredSplineSpanCount,
                    fallbackSplineSpanCount,
                    shortCurveTolerance);

            if (diagnosticProbe != null)
            {
                diagnosticProbe.BandDegrees =
                    degreeV == null
                        ? new List<int>()
                        : degreeV.ToList();
                diagnosticProbe.BandRowCounts =
                    bandRows == null
                        ? new List<int>()
                        : bandRows
                            .Select(x => x == null ? 0 : x.Length)
                            .ToList();
            }

            if (bandRows == null ||
                degreeV == null ||
                ringSamples == null ||
                bandRows.Count == 0 ||
                bandRows.Count != degreeV.Count ||
                ringSamples.Count == 0 ||
                sampleCount < 32 ||
                shortCurveTolerance <= 0 ||
                (topologicalPatchCount != 2 &&
                 topologicalPatchCount != 4) ||
                (sampleCount % topologicalPatchCount) != 0)
            {
                error =
                    "The tolerance-safe compact BRep layout inputs are " +
                    "incomplete.";

                if (diagnosticProbe != null)
                {
                    diagnosticProbe.Error = error;
                    diagnosticProbe.Succeeded = false;
                }

                return false;
            }

            List<int> candidateSplineSpanCounts =
                new List<int>();

            if (preferredSplineSpanCount > 0)
            {
                candidateSplineSpanCounts.Add(
                    preferredSplineSpanCount);
            }

            if (fallbackSplineSpanCount > 0 &&
                !candidateSplineSpanCounts.Contains(
                    fallbackSplineSpanCount))
            {
                candidateSplineSpanCounts.Add(
                    fallbackSplineSpanCount);
            }

            double minimumAcceptedLength =
                shortCurveTolerance *
                1.05;

            double maximumAcceptedDeviation =
                SmoothBRepMaximumDeviationMillimetres /
                FeetToMillimetres;

            double bestObservedClearance =
                double.MinValue;

            string bestObservedClearanceContext = null;

            double bestObservedDeviation =
                double.MaxValue;

            string bestObservedDeviationContext = null;

            List<IList<XYZ>> deviationRings =
                new List<IList<XYZ>>();

            foreach (IList<XYZ> ring in
                     ringSamples)
            {
                if (ring != null &&
                    !deviationRings.Any(x =>
                        object.ReferenceEquals(
                            x,
                            ring)))
                {
                    deviationRings.Add(ring);
                }
            }

            foreach (IList<XYZ>[] rows in
                     bandRows)
            {
                if (rows == null)
                    continue;

                foreach (IList<XYZ> row in rows)
                {
                    if (row != null &&
                        !deviationRings.Any(x =>
                            object.ReferenceEquals(
                                x,
                                row)))
                    {
                        deviationRings.Add(row);
                    }
                }
            }

            const int maximumBuildLayoutsPerSpanCount = 3;

            foreach (int currentSplineSpanCount in
                     candidateSplineSpanCounts)
            {
                if (currentSplineSpanCount <
                        topologicalPatchCount ||
                    (currentSplineSpanCount %
                     topologicalPatchCount) != 0 ||
                    (sampleCount %
                     currentSplineSpanCount) != 0)
                {
                    continue;
                }

                int samplesPerSpan =
                    sampleCount /
                    currentSplineSpanCount;

                int samplesPerPatch =
                    sampleCount /
                    topologicalPatchCount;

                int spansPerPatch =
                    currentSplineSpanCount /
                    topologicalPatchCount;

                List<SmoothBRepPatchLayout>
                    clearanceCandidates =
                        new List<SmoothBRepPatchLayout>();

                // A previous implementation searched only one spline span.
                // That misses most possible seam rotations. Search the full
                // unique patch interval so a two-face layout is genuinely
                // exhausted before the four-face fallback is considered.
                for (int offset = 0;
                     offset < samplesPerPatch;
                     offset++)
                {
                    bool layoutIsValid = true;

                    double minimumClearance =
                        double.MaxValue;

                    string minimumContext = null;

                    FabricationBRepWitnessProbe minimumWitness = null;

                    for (int bandIndex = 0;
                         bandIndex < bandRows.Count &&
                         layoutIsValid;
                         bandIndex++)
                    {
                        IList<XYZ>[] rows =
                            bandRows[bandIndex];

                        if (rows == null ||
                            rows.Length < 2 ||
                            rows.Any(x =>
                                x == null ||
                                x.Count != sampleCount))
                        {
                            layoutIsValid = false;
                            minimumContext =
                                "surface band " +
                                bandIndex.ToString(
                                    CultureInfo.InvariantCulture) +
                                " has incomplete rows";

                            minimumWitness =
                                new FabricationBRepWitnessProbe
                                {
                                    Kind = "incomplete-band",
                                    BandIndex = bandIndex,
                                    PatchIndex = -1,
                                    SampleIndex = -1,
                                    FirstRowIndex = -1,
                                    SecondRowIndex = -1
                                };

                            break;
                        }

                        for (int patchIndex = 0;
                             patchIndex <
                                topologicalPatchCount;
                             patchIndex++)
                        {
                            int sampleIndex =
                                (offset +
                                 (patchIndex *
                                  samplesPerPatch)) %
                                sampleCount;

                            XYZ first =
                                rows[0][sampleIndex];

                            XYZ last =
                                rows[
                                    rows.Length - 1]
                                    [sampleIndex];

                            double chordLength =
                                first.DistanceTo(last);

                            double chordClearance =
                                chordLength -
                                minimumAcceptedLength;

                            if (chordClearance <
                                minimumClearance)
                            {
                                minimumClearance =
                                    chordClearance;

                                minimumContext =
                                    "surface band " +
                                    bandIndex.ToString(
                                        CultureInfo.InvariantCulture) +
                                    ", compact patch seam " +
                                    patchIndex.ToString(
                                        CultureInfo.InvariantCulture) +
                                    ", sample " +
                                    sampleIndex.ToString(
                                        CultureInfo.InvariantCulture);

                                minimumWitness =
                                    new FabricationBRepWitnessProbe
                                    {
                                        Kind = "band-chord",
                                        BandIndex = bandIndex,
                                        PatchIndex = patchIndex,
                                        SampleIndex = sampleIndex,
                                        FirstRowIndex = 0,
                                        SecondRowIndex = rows.Length - 1,
                                        FirstPoint = ToPointProbe(first),
                                        SecondPoint = ToPointProbe(last),
                                        DistanceMillimetres =
                                            chordLength * FeetToMillimetres,
                                        RequiredMillimetres =
                                            minimumAcceptedLength * FeetToMillimetres,
                                        ClearanceMillimetres =
                                            chordClearance * FeetToMillimetres
                                    };
                            }

                            if (chordClearance <= 0)
                            {
                                layoutIsValid = false;
                                break;
                            }

                            if (degreeV[bandIndex] <= 1)
                                continue;

                            for (int rowIndex = 1;
                                 rowIndex < rows.Length;
                                 rowIndex++)
                            {
                                double spacing =
                                    rows[rowIndex - 1]
                                        [sampleIndex]
                                        .DistanceTo(
                                            rows[rowIndex]
                                                [sampleIndex]);

                                double spacingClearance =
                                    spacing -
                                    shortCurveTolerance;

                                if (spacingClearance <
                                    minimumClearance)
                                {
                                    minimumClearance =
                                        spacingClearance;

                                    minimumContext =
                                        "surface band " +
                                        bandIndex.ToString(
                                            CultureInfo.InvariantCulture) +
                                        ", compact patch seam " +
                                        patchIndex.ToString(
                                            CultureInfo.InvariantCulture) +
                                        ", control spacing " +
                                        (rowIndex - 1).ToString(
                                            CultureInfo.InvariantCulture) +
                                        "-" +
                                        rowIndex.ToString(
                                            CultureInfo.InvariantCulture);

                                    minimumWitness =
                                        new FabricationBRepWitnessProbe
                                        {
                                            Kind = "control-spacing",
                                            BandIndex = bandIndex,
                                            PatchIndex = patchIndex,
                                            SampleIndex = sampleIndex,
                                            FirstRowIndex = rowIndex - 1,
                                            SecondRowIndex = rowIndex,
                                            FirstPoint = ToPointProbe(
                                                rows[rowIndex - 1][sampleIndex]),
                                            SecondPoint = ToPointProbe(
                                                rows[rowIndex][sampleIndex]),
                                            DistanceMillimetres =
                                                spacing * FeetToMillimetres,
                                            RequiredMillimetres =
                                                shortCurveTolerance * FeetToMillimetres,
                                            ClearanceMillimetres =
                                                spacingClearance * FeetToMillimetres
                                        };
                                }

                                if (spacingClearance <= 0)
                                {
                                    layoutIsValid = false;
                                    break;
                                }
                            }

                            if (!layoutIsValid)
                                break;
                        }
                    }

                    if (diagnosticProbe != null)
                    {
                        diagnosticProbe.Offsets.Add(
                            new FabricationBRepOffsetProbe
                            {
                                SplineSpanCount = currentSplineSpanCount,
                                Offset = offset,
                                PassedClearance = layoutIsValid,
                                MinimumClearanceMillimetres =
                                    minimumClearance == double.MaxValue
                                        ? (double?)null
                                        : minimumClearance * FeetToMillimetres,
                                MinimumContext = minimumContext,
                                Witness = minimumWitness
                            });
                    }

                    if (minimumClearance >
                        bestObservedClearance)
                    {
                        bestObservedClearance =
                            minimumClearance;

                        bestObservedClearanceContext =
                            minimumContext;
                    }

                    if (!layoutIsValid)
                        continue;

                    clearanceCandidates.Add(
                        new SmoothBRepPatchLayout
                        {
                            SplineSpanCount =
                                currentSplineSpanCount,
                            PatchStartOffset = offset,
                            MinimumClearance =
                                minimumClearance,
                            MaximumDeviation =
                                double.MaxValue
                        });
                }

                List<SmoothBRepPatchLayout>
                    deviationCandidates =
                        clearanceCandidates
                            .OrderByDescending(x =>
                                x.MinimumClearance)
                            .ToList();

                int acceptedLayoutsForSpanCount = 0;

                foreach (SmoothBRepPatchLayout candidate in
                         deviationCandidates)
                {
                    bool layoutIsValid = true;
                    double maximumDeviation = 0.0;
                    string maximumDeviationContext = null;

                    for (int ringIndex = 0;
                         ringIndex < deviationRings.Count;
                         ringIndex++)
                    {
                        IList<XYZ> ring =
                            deviationRings[ringIndex];

                        if (ring == null ||
                            ring.Count != sampleCount)
                        {
                            layoutIsValid = false;
                            maximumDeviationContext =
                                "circumferential control ring " +
                                ringIndex.ToString(
                                    CultureInfo.InvariantCulture) +
                                " is incomplete";

                            break;
                        }

                        for (int patchIndex = 0;
                             patchIndex <
                                topologicalPatchCount;
                             patchIndex++)
                        {
                            int startSample =
                                (candidate.PatchStartOffset +
                                 (patchIndex *
                                  samplesPerPatch)) %
                                sampleCount;

                            double deviation =
                                MeasurePeriodicInterpolatingCubicSplineDeviation(
                                    ring,
                                    startSample,
                                    samplesPerSpan,
                                    spansPerPatch);

                            if (deviation >
                                maximumDeviation)
                            {
                                maximumDeviation =
                                    deviation;

                                maximumDeviationContext =
                                    "circumferential control ring " +
                                    ringIndex.ToString(
                                        CultureInfo.InvariantCulture) +
                                    ", compact patch " +
                                    patchIndex.ToString(
                                        CultureInfo.InvariantCulture);
                            }
                        }
                    }

                    if (maximumDeviation <
                        bestObservedDeviation)
                    {
                        bestObservedDeviation =
                            maximumDeviation;

                        bestObservedDeviationContext =
                            maximumDeviationContext;
                    }

                    if (!layoutIsValid ||
                        maximumDeviation >
                            maximumAcceptedDeviation)
                    {
                        continue;
                    }

                    candidate.MaximumDeviation =
                        maximumDeviation;

                    layouts.Add(candidate);

                    if (diagnosticProbe != null)
                    {
                        diagnosticProbe.AcceptedLayouts.Add(
                            new FabricationAcceptedLayoutProbe
                            {
                                SplineSpanCount = candidate.SplineSpanCount,
                                PatchStartOffset = candidate.PatchStartOffset,
                                MinimumClearanceMillimetres =
                                    candidate.MinimumClearance * FeetToMillimetres,
                                MaximumDeviationMillimetres =
                                    candidate.MaximumDeviation * FeetToMillimetres
                            });
                    }

                    acceptedLayoutsForSpanCount++;

                    if (acceptedLayoutsForSpanCount >=
                        maximumBuildLayoutsPerSpanCount)
                    {
                        break;
                    }
                }
            }

            layouts =
                layouts
                    .OrderBy(x =>
                        x.SplineSpanCount)
                    .ThenByDescending(x =>
                        x.MinimumClearance)
                    .ThenBy(x =>
                        x.MaximumDeviation)
                    .ToList();

            if (layouts.Count > 0)
            {
                if (diagnosticProbe != null)
                {
                    diagnosticProbe.BestObservedClearanceMillimetres =
                        bestObservedClearance == double.MinValue
                            ? (double?)null
                            : bestObservedClearance * FeetToMillimetres;
                    diagnosticProbe.BestObservedClearanceContext =
                        bestObservedClearanceContext;
                    diagnosticProbe.BestObservedDeviationMillimetres =
                        bestObservedDeviation == double.MaxValue
                            ? (double?)null
                            : bestObservedDeviation * FeetToMillimetres;
                    diagnosticProbe.BestObservedDeviationContext =
                        bestObservedDeviationContext;
                    diagnosticProbe.Succeeded = true;
                }

                return true;
            }

            error =
                "No tolerance-safe compact circumferential BRep layout " +
                "could be found for the shaped branch. Best remaining " +
                "seam clearance: " +
                (bestObservedClearance *
                 FeetToMillimetres).ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm" +
                (string.IsNullOrWhiteSpace(
                    bestObservedClearanceContext)
                    ? "."
                    : " at " +
                      bestObservedClearanceContext +
                      ".") +
                " Best geometric deviation: " +
                (bestObservedDeviation *
                 FeetToMillimetres).ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm" +
                (string.IsNullOrWhiteSpace(
                    bestObservedDeviationContext)
                    ? "."
                    : " at " +
                      bestObservedDeviationContext +
                      ".") +
                " Required deviation: no more than " +
                SmoothBRepMaximumDeviationMillimetres.ToString(
                    "0.###",
                    CultureInfo.InvariantCulture) +
                " mm. The selected branch dimensions or weld-land " +
                "geometry cannot be represented safely without changing " +
                "the fabrication profile.";

            if (diagnosticProbe != null)
            {
                diagnosticProbe.BestObservedClearanceMillimetres =
                    bestObservedClearance == double.MinValue
                        ? (double?)null
                        : bestObservedClearance * FeetToMillimetres;
                diagnosticProbe.BestObservedClearanceContext =
                    bestObservedClearanceContext;
                diagnosticProbe.BestObservedDeviationMillimetres =
                    bestObservedDeviation == double.MaxValue
                        ? (double?)null
                        : bestObservedDeviation * FeetToMillimetres;
                diagnosticProbe.BestObservedDeviationContext =
                    bestObservedDeviationContext;
                diagnosticProbe.Error = error;
                diagnosticProbe.Succeeded = false;
            }

            return false;
        }


        private static IList<double>
            CreateClampedCubicSplineKnots(
                int spanCount)
        {
            if (spanCount < 2)
            {
                throw new ArgumentOutOfRangeException(
                    "spanCount");
            }

            List<double> knots =
                new List<double>();

            // Revit accepts a clamped cubic NURBS with simple interior
            // knots. For degree three, an interior multiplicity greater than
            // one is rejected because Revit requires at least C2 continuity.
            for (int index = 0;
                 index < 4;
                 index++)
            {
                knots.Add(0.0);
            }

            for (int spanIndex = 1;
                 spanIndex < spanCount;
                 spanIndex++)
            {
                knots.Add(
                    (double)spanIndex /
                    spanCount);
            }

            for (int index = 0;
                 index < 4;
                 index++)
            {
                knots.Add(1.0);
            }

            return knots;
        }

        private static IList<XYZ>
            CreatePeriodicInterpolatingCubicSplineControls(
                IList<XYZ> samples,
                int startIndex,
                int samplesPerSpan,
                int spanCount)
        {
            if (samples == null ||
                samples.Count < 8 ||
                samplesPerSpan < 1 ||
                spanCount < 2 ||
                (samplesPerSpan * spanCount) >=
                    samples.Count)
            {
                throw new ArgumentException(
                    "The periodic interpolating cubic B-spline inputs are " +
                    "invalid.");
            }

            int sampleCount =
                samples.Count;

            int normalizedStart =
                ((startIndex % sampleCount) +
                 sampleCount) %
                sampleCount;

            int sampleAdvance =
                samplesPerSpan *
                spanCount;

            int normalizedEnd =
                (normalizedStart +
                 sampleAdvance) %
                sampleCount;

            double stepAngle =
                2.0 *
                Math.PI /
                sampleCount;

            double patchAngle =
                sampleAdvance *
                stepAngle;

            XYZ startDerivative =
                (samples[
                     (normalizedStart + 1) %
                     sampleCount] -
                 samples[
                     (normalizedStart - 1 +
                      sampleCount) %
                     sampleCount]) /
                (2.0 * stepAngle);

            XYZ endDerivative =
                (samples[
                     (normalizedEnd + 1) %
                     sampleCount] -
                 samples[
                     (normalizedEnd - 1 +
                      sampleCount) %
                     sampleCount]) /
                (2.0 * stepAngle);

            // The spline parameter runs from zero to one across this patch.
            // Convert the angular derivatives to that normalized parameter.
            startDerivative *=
                patchAngle;

            endDerivative *=
                patchAngle;

            int controlCount =
                spanCount + 3;

            XYZ[] controls =
                new XYZ[controlCount];

            controls[0] =
                samples[normalizedStart];

            controls[1] =
                controls[0] +
                (startDerivative /
                 (3.0 * spanCount));

            controls[controlCount - 1] =
                samples[normalizedEnd];

            controls[controlCount - 2] =
                controls[controlCount - 1] -
                (endDerivative /
                 (3.0 * spanCount));

            int unknownControlCount =
                spanCount - 1;

            if (unknownControlCount > 0)
            {
                IList<double> knots =
                    CreateClampedCubicSplineKnots(
                        spanCount);

                double[,] coefficientMatrix =
                    new double[
                        unknownControlCount,
                        unknownControlCount];

                XYZ[] rightHandSide =
                    new XYZ[
                        unknownControlCount];

                for (int dataIndex = 1;
                     dataIndex < spanCount;
                     dataIndex++)
                {
                    int equationIndex =
                        dataIndex - 1;

                    double parameter =
                        (double)dataIndex /
                        spanCount;

                    double[] basisValues =
                        EvaluateAllBSplineBasisValues(
                            3,
                            knots,
                            controlCount,
                            parameter);

                    XYZ targetPoint =
                        samples[
                            (normalizedStart +
                             (dataIndex *
                              samplesPerSpan)) %
                            sampleCount];

                    XYZ knownContribution =
                        (controls[0] *
                         basisValues[0]) +
                        (controls[1] *
                         basisValues[1]) +
                        (controls[
                             controlCount - 2] *
                         basisValues[
                             controlCount - 2]) +
                        (controls[
                             controlCount - 1] *
                         basisValues[
                             controlCount - 1]);

                    rightHandSide[equationIndex] =
                        targetPoint -
                        knownContribution;

                    for (int unknownIndex = 0;
                         unknownIndex <
                            unknownControlCount;
                         unknownIndex++)
                    {
                        int controlIndex =
                            unknownIndex + 2;

                        coefficientMatrix[
                            equationIndex,
                            unknownIndex] =
                                basisValues[
                                    controlIndex];
                    }
                }

                IList<XYZ> solvedControls =
                    SolveLinearSystem(
                        coefficientMatrix,
                        rightHandSide);

                for (int unknownIndex = 0;
                     unknownIndex <
                        unknownControlCount;
                     unknownIndex++)
                {
                    controls[
                        unknownIndex + 2] =
                            solvedControls[
                                unknownIndex];
                }
            }

            return controls;
        }

        private static double
            MeasurePeriodicInterpolatingCubicSplineDeviation(
                IList<XYZ> samples,
                int startIndex,
                int samplesPerSpan,
                int spanCount)
        {
            try
            {
                IList<XYZ> controls =
                    CreatePeriodicInterpolatingCubicSplineControls(
                        samples,
                        startIndex,
                        samplesPerSpan,
                        spanCount);

                IList<double> knots =
                    CreateClampedCubicSplineKnots(
                        spanCount);

                double maximumDeviation = 0.0;

                const int checksPerSpan = 8;

                for (int spanIndex = 0;
                     spanIndex < spanCount;
                     spanIndex++)
                {
                    for (int checkIndex = 0;
                         checkIndex <= checksPerSpan;
                         checkIndex++)
                    {
                        double localParameter =
                            (double)checkIndex /
                            checksPerSpan;

                        double splineParameter =
                            (spanIndex +
                             localParameter) /
                            spanCount;

                        XYZ splinePoint =
                            EvaluateBSplinePoint(
                                3,
                                knots,
                                controls,
                                splineParameter);

                        double samplePosition =
                            startIndex +
                            ((spanIndex +
                              localParameter) *
                             samplesPerSpan);

                        XYZ referencePoint =
                            InterpolatePeriodicSamples(
                                samples,
                                samplePosition);

                        maximumDeviation =
                            Math.Max(
                                maximumDeviation,
                                splinePoint.DistanceTo(
                                    referencePoint));
                    }
                }

                return maximumDeviation;
            }
            catch
            {
                return double.MaxValue;
            }
        }

        private static IList<XYZ>
            SolveLinearSystem(
                double[,] coefficientMatrix,
                IList<XYZ> rightHandSide)
        {
            if (coefficientMatrix == null ||
                rightHandSide == null)
            {
                throw new ArgumentNullException(
                    "coefficientMatrix");
            }

            int size =
                rightHandSide.Count;

            if (coefficientMatrix.GetLength(0) != size ||
                coefficientMatrix.GetLength(1) != size)
            {
                throw new ArgumentException(
                    "The spline interpolation system is not square.");
            }

            double[,] matrix =
                (double[,])coefficientMatrix.Clone();

            XYZ[] values =
                rightHandSide.ToArray();

            const double pivotTolerance = 1.0e-12;

            for (int pivotIndex = 0;
                 pivotIndex < size;
                 pivotIndex++)
            {
                int pivotRow =
                    pivotIndex;

                double pivotMagnitude =
                    Math.Abs(
                        matrix[
                            pivotRow,
                            pivotIndex]);

                for (int row = pivotIndex + 1;
                     row < size;
                     row++)
                {
                    double candidateMagnitude =
                        Math.Abs(
                            matrix[
                                row,
                                pivotIndex]);

                    if (candidateMagnitude >
                        pivotMagnitude)
                    {
                        pivotMagnitude =
                            candidateMagnitude;

                        pivotRow =
                            row;
                    }
                }

                if (pivotMagnitude <=
                    pivotTolerance)
                {
                    throw new InvalidOperationException(
                        "The cubic B-spline interpolation matrix is " +
                        "singular.");
                }

                if (pivotRow != pivotIndex)
                {
                    for (int column = 0;
                         column < size;
                         column++)
                    {
                        double temporary =
                            matrix[
                                pivotIndex,
                                column];

                        matrix[
                            pivotIndex,
                            column] =
                                matrix[
                                    pivotRow,
                                    column];

                        matrix[
                            pivotRow,
                            column] =
                                temporary;
                    }

                    XYZ temporaryValue =
                        values[pivotIndex];

                    values[pivotIndex] =
                        values[pivotRow];

                    values[pivotRow] =
                        temporaryValue;
                }

                double pivot =
                    matrix[
                        pivotIndex,
                        pivotIndex];

                for (int column = pivotIndex;
                     column < size;
                     column++)
                {
                    matrix[
                        pivotIndex,
                        column] /=
                            pivot;
                }

                values[pivotIndex] /=
                    pivot;

                for (int row = 0;
                     row < size;
                     row++)
                {
                    if (row == pivotIndex)
                        continue;

                    double factor =
                        matrix[
                            row,
                            pivotIndex];

                    if (Math.Abs(factor) <=
                        pivotTolerance)
                    {
                        continue;
                    }

                    for (int column = pivotIndex;
                         column < size;
                         column++)
                    {
                        matrix[
                            row,
                            column] -=
                                factor *
                                matrix[
                                    pivotIndex,
                                    column];
                    }

                    values[row] -=
                        values[pivotIndex] *
                        factor;
                }
            }

            return values;
        }

        private static XYZ
            EvaluateBSplinePoint(
                int degree,
                IList<double> knots,
                IList<XYZ> controls,
                double parameter)
        {
            if (knots == null ||
                controls == null ||
                degree < 1 ||
                controls.Count <= degree ||
                knots.Count !=
                    controls.Count +
                    degree + 1)
            {
                throw new ArgumentException(
                    "The B-spline evaluation inputs are invalid.");
            }

            double[] basisValues =
                EvaluateAllBSplineBasisValues(
                    degree,
                    knots,
                    controls.Count,
                    parameter);

            XYZ point =
                new XYZ(
                    0.0,
                    0.0,
                    0.0);

            for (int controlIndex = 0;
                 controlIndex < controls.Count;
                 controlIndex++)
            {
                point +=
                    controls[controlIndex] *
                    basisValues[controlIndex];
            }

            return point;
        }

        private static double[]
            EvaluateAllBSplineBasisValues(
                int degree,
                IList<double> knots,
                int controlCount,
                double parameter)
        {
            if (knots == null ||
                degree < 1 ||
                controlCount <= degree ||
                knots.Count !=
                    controlCount +
                    degree + 1)
            {
                throw new ArgumentException(
                    "The B-spline basis inputs are invalid.");
            }

            int span =
                FindBSplineKnotSpan(
                    degree,
                    knots,
                    controlCount,
                    parameter);

            double firstParameter =
                knots[degree];

            double lastParameter =
                knots[controlCount];

            double clampedParameter =
                Math.Max(
                    firstParameter,
                    Math.Min(
                        lastParameter,
                        parameter));

            double[] localBasis =
                new double[degree + 1];

            double[] left =
                new double[degree + 1];

            double[] right =
                new double[degree + 1];

            localBasis[0] = 1.0;

            for (int basisDegree = 1;
                 basisDegree <= degree;
                 basisDegree++)
            {
                left[basisDegree] =
                    clampedParameter -
                    knots[
                        span + 1 -
                        basisDegree];

                right[basisDegree] =
                    knots[
                        span +
                        basisDegree] -
                    clampedParameter;

                double saved = 0.0;

                for (int basisIndex = 0;
                     basisIndex < basisDegree;
                     basisIndex++)
                {
                    double denominator =
                        right[basisIndex + 1] +
                        left[
                            basisDegree -
                            basisIndex];

                    double temporary =
                        Math.Abs(denominator) <=
                            1.0e-14
                            ? 0.0
                            : localBasis[basisIndex] /
                              denominator;

                    localBasis[basisIndex] =
                        saved +
                        (right[basisIndex + 1] *
                         temporary);

                    saved =
                        left[
                            basisDegree -
                            basisIndex] *
                        temporary;
                }

                localBasis[basisDegree] =
                    saved;
            }

            double[] allBasis =
                new double[controlCount];

            int firstControlIndex =
                span - degree;

            for (int localIndex = 0;
                 localIndex <= degree;
                 localIndex++)
            {
                allBasis[
                    firstControlIndex +
                    localIndex] =
                        localBasis[localIndex];
            }

            return allBasis;
        }

        private static int
            FindBSplineKnotSpan(
                int degree,
                IList<double> knots,
                int controlCount,
                double parameter)
        {
            int lastControlIndex =
                controlCount - 1;

            if (parameter >=
                knots[controlCount])
            {
                return lastControlIndex;
            }

            if (parameter <=
                knots[degree])
            {
                return degree;
            }

            int low = degree;
            int high = controlCount;
            int middle =
                (low + high) /
                2;

            while (parameter < knots[middle] ||
                   parameter >= knots[middle + 1])
            {
                if (parameter < knots[middle])
                {
                    high = middle;
                }
                else
                {
                    low = middle;
                }

                middle =
                    (low + high) /
                    2;
            }

            return middle;
        }

        private static XYZ
            InterpolatePeriodicSamples(
                IList<XYZ> samples,
                double samplePosition)
        {
            int count =
                samples.Count;

            double normalized =
                samplePosition %
                count;

            if (normalized < 0)
                normalized += count;

            int firstIndex =
                (int)Math.Floor(
                    normalized) %
                count;

            int secondIndex =
                (firstIndex + 1) %
                count;

            double fraction =
                normalized -
                Math.Floor(
                    normalized);

            return
                samples[firstIndex] +
                ((samples[secondIndex] -
                  samples[firstIndex]) *
                 fraction);
        }

        private static bool
            TryCreateToleranceSafeBRepBoundaryCurve(
                int degree,
                IList<double> knots,
                IList<XYZ> controlPoints,
                double shortCurveTolerance,
                string context,
                out Curve curve,
                out string error)
        {
            curve = null;
            error = null;

            if (controlPoints == null ||
                controlPoints.Count < 2)
            {
                error =
                    "The smooth BRep boundary for " +
                    context +
                    " does not contain enough control points.";

                return false;
            }

            XYZ startPoint =
                controlPoints[0];

            XYZ endPoint =
                controlPoints[
                    controlPoints.Count - 1];

            double chordLength =
                startPoint.DistanceTo(endPoint);

            double minimumAcceptedLength =
                shortCurveTolerance * 1.05;

            if (chordLength <=
                minimumAcceptedLength)
            {
                error =
                    "The smooth BRep boundary for " +
                    context +
                    " is shorter than Revit's curve tolerance. Length: " +
                    (chordLength * FeetToMillimetres).ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm; required: more than " +
                    (minimumAcceptedLength * FeetToMillimetres).ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm.";

                return false;
            }

            try
            {
                if (degree == 1)
                {
                    curve =
                        Line.CreateBound(
                            startPoint,
                            endPoint);
                }
                else
                {
                    for (int index = 1;
                         index < controlPoints.Count;
                         index++)
                    {
                        double controlSpacing =
                            controlPoints[index - 1]
                                .DistanceTo(
                                    controlPoints[index]);

                        if (controlSpacing <=
                            shortCurveTolerance)
                        {
                            error =
                                "The smooth BRep control spacing for " +
                                context +
                                " is below Revit's curve tolerance. " +
                                "Control spacing: " +
                                (controlSpacing * FeetToMillimetres)
                                    .ToString(
                                        "0.###",
                                        CultureInfo.InvariantCulture) +
                                " mm.";

                            return false;
                        }
                    }

                    curve =
                        NurbSpline.CreateCurve(
                            degree,
                            knots,
                            controlPoints);

                    // Revit is allowed to simplify an explicitly supplied
                    // NURBS boundary to an Arc or Line. On large circular
                    // outlet rings that simplification can move the returned
                    // curve endpoints by a few thousandths of a millimetre,
                    // even though the clamped NURBS controls use the exact
                    // shared loop vertices. Rebuild only the simplified
                    // primitive with the intended endpoints so adjacent BRep
                    // edges remain exactly connected. Non-simplified NURBS
                    // boundaries are left untouched.
                    curve =
                        ReanchorSimplifiedBRepBoundaryCurve(
                            curve,
                            startPoint,
                            endPoint,
                            shortCurveTolerance);
                }

                Curve orientedCurve;
                string orientationError;

                if (!TryOrientBRepBoundaryCurve(
                        curve,
                        startPoint,
                        endPoint,
                        shortCurveTolerance,
                        context,
                        out orientedCurve,
                        out orientationError))
                {
                    error = orientationError;
                    curve = null;
                    return false;
                }

                curve = orientedCurve;
                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The smooth BRep boundary for " +
                    context +
                    " could not be generated: " +
                    ex.Message;

                return false;
            }
        }

        private static Curve
            ReanchorSimplifiedBRepBoundaryCurve(
                Curve sourceCurve,
                XYZ expectedStart,
                XYZ expectedEnd,
                double shortCurveTolerance)
        {
            if (sourceCurve == null ||
                expectedStart == null ||
                expectedEnd == null)
            {
                return sourceCurve;
            }

            try
            {
                // NurbSpline.CreateCurve may return a simpler Line. Recreate
                // it directly from the shared BRep vertices so no fitting
                // drift remains at either end.
                if (sourceCurve is Line)
                {
                    if (expectedStart.DistanceTo(expectedEnd) >
                        shortCurveTolerance * 1.05)
                    {
                        return
                            Line.CreateBound(
                                expectedStart,
                                expectedEnd);
                    }

                    return sourceCurve;
                }

                Arc sourceArc =
                    sourceCurve as Arc;

                if (sourceArc == null ||
                    !sourceCurve.IsBound ||
                    sourceCurve.IsClosed)
                {
                    return sourceCurve;
                }

                XYZ pointOnArc =
                    sourceCurve.Evaluate(
                        0.5,
                        true);

                if (pointOnArc == null ||
                    expectedStart.DistanceTo(expectedEnd) <=
                        shortCurveTolerance * 1.05 ||
                    pointOnArc.DistanceTo(expectedStart) <=
                        GeometryTolerance ||
                    pointOnArc.DistanceTo(expectedEnd) <=
                        GeometryTolerance)
                {
                    return sourceCurve;
                }

                // Arc.Create(end0, end1, pointOnArc) preserves the supplied
                // endpoints exactly. This is important for the outlet rings:
                // their neighboring seam curves already terminate at these
                // same raw sample vertices.
                Curve anchoredArc =
                    Arc.Create(
                        expectedStart,
                        expectedEnd,
                        pointOnArc);

                if (anchoredArc == null ||
                    anchoredArc.Length <=
                        shortCurveTolerance * 1.05)
                {
                    return sourceCurve;
                }

                return anchoredArc;
            }
            catch
            {
                // Keep the original curve and let the existing orientation
                // and endpoint validation produce the detailed failure.
                return sourceCurve;
            }
        }

        private static bool
            TryOrientBRepBoundaryCurve(
                Curve sourceCurve,
                XYZ expectedStart,
                XYZ expectedEnd,
                double shortCurveTolerance,
                string context,
                out Curve orientedCurve,
                out string error)
        {
            orientedCurve = null;
            error = null;

            if (sourceCurve == null ||
                expectedStart == null ||
                expectedEnd == null)
            {
                error =
                    "The smooth BRep boundary orientation for " +
                    context +
                    " could not be resolved because its curve or " +
                    "endpoints are missing.";

                return false;
            }

            try
            {
                XYZ actualStart =
                    sourceCurve.GetEndPoint(0);

                XYZ actualEnd =
                    sourceCurve.GetEndPoint(1);

                double forwardMismatch =
                    actualStart.DistanceTo(expectedStart) +
                    actualEnd.DistanceTo(expectedEnd);

                double reverseMismatch =
                    actualStart.DistanceTo(expectedEnd) +
                    actualEnd.DistanceTo(expectedStart);

                // NurbSpline.CreateCurve is permitted to simplify the result
                // to a line or arc. A simplified curve can have a canonical
                // orientation that is opposite to the control-point order.
                // BRepBuilder co-edge flags are based on the actual curve
                // orientation, so normalize every boundary before the edge is
                // added to the builder.
                if (reverseMismatch + GeometryTolerance <
                    forwardMismatch)
                {
                    orientedCurve =
                        sourceCurve.CreateReversed();
                }
                else
                {
                    orientedCurve = sourceCurve;
                }

                XYZ orientedStart =
                    orientedCurve.GetEndPoint(0);

                XYZ orientedEnd =
                    orientedCurve.GetEndPoint(1);

                double startGap =
                    orientedStart.DistanceTo(expectedStart);

                double endGap =
                    orientedEnd.DistanceTo(expectedEnd);

                double endpointTolerance =
                    Math.Max(
                        GeometryTolerance * 100.0,
                        shortCurveTolerance * 1.0e-4);

                if (startGap > endpointTolerance ||
                    endGap > endpointTolerance)
                {
                    error =
                        "The smooth BRep boundary for " +
                        context +
                        " does not terminate at its intended loop " +
                        "vertices. Start gap: " +
                        (startGap * FeetToMillimetres).ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                        " mm; end gap: " +
                        (endGap * FeetToMillimetres).ToString(
                            "0.######",
                            CultureInfo.InvariantCulture) +
                        " mm.";

                    orientedCurve = null;
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The smooth BRep boundary orientation for " +
                    context +
                    " could not be normalized: " +
                    ex.Message;

                orientedCurve = null;
                return false;
            }
        }

        private static IList<double>
            CreateClampedBezierKnots(
                int degree)
        {
            if (degree < 1)
            {
                throw new ArgumentOutOfRangeException(
                    "degree");
            }

            List<double> knots =
                new List<double>();

            for (int index = 0;
                 index <= degree;
                 index++)
            {
                knots.Add(0.0);
            }

            for (int index = 0;
                 index <= degree;
                 index++)
            {
                knots.Add(1.0);
            }

            return knots;
        }

        private static IList<XYZ>
            CreatePeriodicCubicBezierControls(
                IList<XYZ> samples,
                int startIndex,
                int endIndex)
        {
            if (samples == null ||
                samples.Count < 8)
            {
                throw new ArgumentException(
                    "At least eight periodic samples are required.",
                    "samples");
            }

            int count =
                samples.Count;

            int normalizedStart =
                ((startIndex % count) + count) %
                count;

            int normalizedEnd =
                ((endIndex % count) + count) %
                count;

            double stepAngle =
                2.0 *
                Math.PI /
                count;

            int forwardSamples =
                normalizedEnd -
                normalizedStart;

            if (forwardSamples <= 0)
                forwardSamples += count;

            double patchAngle =
                forwardSamples *
                stepAngle;

            XYZ startPoint =
                samples[normalizedStart];

            XYZ endPoint =
                samples[normalizedEnd];

            XYZ startDerivative =
                (samples[
                     (normalizedStart + 1) %
                     count] -
                 samples[
                     (normalizedStart - 1 +
                      count) %
                     count]) /
                (2.0 * stepAngle);

            XYZ endDerivative =
                (samples[
                     (normalizedEnd + 1) %
                     count] -
                 samples[
                     (normalizedEnd - 1 +
                      count) %
                     count]) /
                (2.0 * stepAngle);

            return new List<XYZ>
            {
                startPoint,
                startPoint +
                    (startDerivative *
                     (patchAngle / 3.0)),
                endPoint -
                    (endDerivative *
                     (patchAngle / 3.0)),
                endPoint
            };
        }

        private static XYZ
            AverageRingPoint(
                IList<XYZ> ring)
        {
            if (ring == null ||
                ring.Count == 0)
            {
                return null;
            }

            return new XYZ(
                ring.Average(x => x.X),
                ring.Average(x => x.Y),
                ring.Average(x => x.Z));
        }

        private static XYZ
            ResolveRingPlaneNormal(
                IList<XYZ> ring,
                XYZ origin)
        {
            if (ring == null ||
                ring.Count < 4 ||
                origin == null)
            {
                return null;
            }

            XYZ first =
                ring[0] - origin;

            XYZ second =
                ring[ring.Count / 4] -
                origin;

            XYZ normal =
                first.CrossProduct(second);

            if (normal.GetLength() <=
                GeometryTolerance)
            {
                return null;
            }

            // The overlap face must point toward the branch outlet. The ring
            // samples increase around the branch axis, whose cross product
            // points toward the header, so reverse it here.
            return -normal.Normalize();
        }

        private static bool
            TryResolveSetOnSaddlePoint(
                ShapedBranchConnection branch,
                XYZ surfaceOrigin,
                XYZ inwardAxis,
                XYZ radialDirection,
                double branchRadius,
                out XYZ saddlePoint)
        {
            saddlePoint = null;

            if (branch == null ||
                branch.HeaderDimensions == null ||
                branch.HeaderAxisStart == null ||
                branch.HeaderAxisDirection == null ||
                surfaceOrigin == null ||
                inwardAxis == null ||
                radialDirection == null ||
                branchRadius <=
                GeometryTolerance)
            {
                return false;
            }

            XYZ lineOrigin =
                surfaceOrigin +
                (radialDirection.Normalize() *
                 branchRadius);

            double nearDistance;
            double farDistance;

            if (!TryGetLineCylinderIntersections(
                    lineOrigin,
                    inwardAxis.Normalize(),
                    branch.HeaderAxisStart,
                    branch.HeaderAxisDirection.Normalize(),
                    branch.HeaderDimensions
                        .OutsideDiameter / 2.0,
                    out nearDistance,
                    out farDistance))
            {
                return false;
            }

            double surfaceDistance;

            if (!TryGetIntersectionAtOrAfter(
                    nearDistance,
                    farDistance,
                    -0.50 /
                    FeetToMillimetres,
                    out surfaceDistance))
            {
                return false;
            }

            saddlePoint =
                lineOrigin +
                (inwardAxis.Normalize() *
                 surfaceDistance);

            return true;
        }

        private static void
            AddProceduralBranchQuad(
                TessellatedShapeBuilder builder,
                XYZ first,
                XYZ second,
                XYZ third,
                XYZ fourth)
        {
            builder.AddFace(
                new TessellatedFace(
                    new List<XYZ>
                    {
                        first,
                        second,
                        third
                    },
                    ElementId.InvalidElementId));

            builder.AddFace(
                new TessellatedFace(
                    new List<XYZ>
                    {
                        first,
                        third,
                        fourth
                    },
                    ElementId.InvalidElementId));
        }

        private static bool
            TryCreateSetOnHeaderEnvelopeTrimCutter(
                ShapedBranchConnection branch,
                out Solid cutter,
                out string error)
        {
            cutter = null;
            error = null;

            if (branch == null ||
                branch.HeaderDimensions == null ||
                branch.HeaderAxisStart == null ||
                branch.HeaderAxisDirection == null ||
                branch.HeaderAxisLength <= GeometryTolerance)
            {
                error =
                    "The SET-ON header envelope information is missing.";

                return false;
            }

            XYZ headerAxis =
                branch.HeaderAxisDirection.Normalize();

            // Extend beyond both pipe ends so the trim remains valid when the
            // branch is close to an end connector or when Revit Boolean
            // tolerances slightly enlarge the fitting solid.
            double axialExtension = Math.Max(
                10.0 / FeetToMillimetres,
                branch.BranchDimensions == null
                    ? 10.0 / FeetToMillimetres
                    : branch.BranchDimensions.OutsideDiameter);

            // This overlap is only a Boolean tolerance. It removes coincident
            // or nearly coincident branch material at the pipe's outer face
            // without introducing a fabrication clearance.
            double radialBooleanOverlap =
                0.05 / FeetToMillimetres;

            XYZ cutterStart =
                branch.HeaderAxisStart -
                (headerAxis * axialExtension);

            double cutterLength =
                branch.HeaderAxisLength +
                (2.0 * axialExtension);

            double cutterRadius =
                (branch.HeaderDimensions.OutsideDiameter / 2.0) +
                radialBooleanOverlap;

            try
            {
                cutter = CreateCylinder(
                    cutterStart,
                    headerAxis,
                    cutterLength,
                    cutterRadius);

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty SET-ON header-envelope " +
                        "trim cutter.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The SET-ON branch could not be trimmed flush to the " +
                    "header outer surface: " + ex.Message;

                return false;
            }
        }

        private static bool TryResolveSetOnOpeningAxialDepth(
            ShapedBranchConnection branch,
            XYZ surfaceOrigin,
            XYZ inwardAxis,
            double cutterRadius,
            out double requiredDepth,
            out string error)
        {
            requiredDepth = 0.0;
            error = null;

            if (branch == null ||
                branch.HeaderDimensions == null ||
                branch.HeaderAxisStart == null ||
                branch.HeaderAxisDirection == null ||
                surfaceOrigin == null ||
                inwardAxis == null ||
                inwardAxis.GetLength() <= GeometryTolerance ||
                cutterRadius <= GeometryTolerance)
            {
                error =
                    "The SET-ON opening depth information is missing.";

                return false;
            }

            double headerInsideRadius =
                branch.HeaderDimensions.InsideDiameter / 2.0;

            if (headerInsideRadius <= GeometryTolerance)
            {
                error =
                    "The SET-ON header inside radius is zero or invalid.";

                return false;
            }

            XYZ axis = inwardAxis.Normalize();
            XYZ headerAxis =
                branch.HeaderAxisDirection.Normalize();

            // Build a stable basis in the branch-cutter cross-section plane.
            // Prefer the projected header axis so the probe pattern explicitly
            // checks both the header axial and circumferential directions.
            XYZ firstProbeAxis =
                headerAxis -
                (axis * headerAxis.DotProduct(axis));

            if (firstProbeAxis.GetLength() <= GeometryTolerance)
            {
                XYZ helper = Math.Abs(axis.Z) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;

                firstProbeAxis =
                    axis.CrossProduct(helper);
            }

            if (firstProbeAxis.GetLength() <= GeometryTolerance)
            {
                error =
                    "The SET-ON opening probe frame could not be created.";

                return false;
            }

            firstProbeAxis = firstProbeAxis.Normalize();

            XYZ secondProbeAxis =
                axis.CrossProduct(firstProbeAxis);

            if (secondProbeAxis.GetLength() <= GeometryTolerance)
            {
                error =
                    "The SET-ON opening secondary probe axis is invalid.";

                return false;
            }

            secondProbeAxis = secondProbeAxis.Normalize();

            // Probe the complete cylindrical cutter cross-section. The old
            // radial clipping slab sliced through the cutter itself; on some
            // header/branch sizes that turned the intended ellipse into a
            // narrow slot. The axial-depth method below keeps the cutter's
            // complete circular cross-section and only shortens its length.
            double[] ringFactors =
            {
                0.0,
                0.35,
                0.70,
                0.90,
                0.99
            };

            const int angularProbeCount = 32;

            double maximumNearInnerDepth = 0.0;
            double minimumFarInnerDepth = double.MaxValue;
            int validProbeCount = 0;

            foreach (double ringFactor in ringFactors)
            {
                int probeCount = ringFactor <= GeometryTolerance
                    ? 1
                    : angularProbeCount;

                for (int probeIndex = 0;
                     probeIndex < probeCount;
                     probeIndex++)
                {
                    double angle = probeCount == 1
                        ? 0.0
                        : (2.0 * Math.PI * probeIndex) /
                          probeCount;

                    XYZ offset =
                        (firstProbeAxis *
                         (cutterRadius * ringFactor *
                          Math.Cos(angle))) +
                        (secondProbeAxis *
                         (cutterRadius * ringFactor *
                          Math.Sin(angle)));

                    XYZ probeOrigin = surfaceOrigin + offset;

                    double nearDistance;
                    double farDistance;

                    if (!TryGetLineCylinderIntersections(
                            probeOrigin,
                            axis,
                            branch.HeaderAxisStart,
                            headerAxis,
                            headerInsideRadius,
                            out nearDistance,
                            out farDistance))
                    {
                        error =
                            "The full SET-ON branch opening does not reach " +
                            "the header bore at every point. Verify the " +
                            "branch size, angle, and attachment position.";

                        return false;
                    }

                    if (farDistance <= GeometryTolerance)
                    {
                        error =
                            "The SET-ON opening probe points away from the " +
                            "header bore. Verify the branch connector " +
                            "direction.";

                        return false;
                    }

                    double nearInnerDepth =
                        nearDistance > GeometryTolerance
                            ? nearDistance
                            : 0.0;

                    if (farDistance <=
                        nearInnerDepth + GeometryTolerance)
                    {
                        error =
                            "The SET-ON branch opening has no safe depth " +
                            "between the near and opposite header walls.";

                        return false;
                    }

                    maximumNearInnerDepth = Math.Max(
                        maximumNearInnerDepth,
                        nearInnerDepth);

                    minimumFarInnerDepth = Math.Min(
                        minimumFarInnerDepth,
                        farDistance);

                    validProbeCount++;
                }
            }

            if (validProbeCount == 0 ||
                minimumFarInnerDepth == double.MaxValue)
            {
                error =
                    "The SET-ON opening depth could not be resolved.";

                return false;
            }

            // Continue slightly beyond the deepest near-side inner-surface
            // intersection so the entire ellipse opens cleanly into the bore.
            double boreOverlap = Math.Max(
                1.0 / FeetToMillimetres,
                branch.HeaderDimensions.WallThickness * 0.20);

            double desiredDepth =
                maximumNearInnerDepth + boreOverlap;

            // Keep a positive gap before the earliest opposite inner surface
            // across the complete cutter disk. This makes the opposite wall
            // unreachable without slicing the cutter cross-section.
            double oppositeWallSafety = Math.Max(
                2.0 / FeetToMillimetres,
                branch.HeaderDimensions.WallThickness * 0.25);

            double maximumSafeDepth =
                minimumFarInnerDepth - oppositeWallSafety;

            if (desiredDepth >=
                maximumSafeDepth - GeometryTolerance)
            {
                error =
                    "The SET-ON opening cannot fully clear the near-side " +
                    "wall while preserving the opposite wall. Verify the " +
                    "branch-to-header size and angle.";

                return false;
            }

            requiredDepth = desiredDepth;
            return true;
        }

        private static bool TryCreateSetOnHeaderOpeningCutter(
            ShapedBranchConnection branch,
            out Solid cutter,
            out string error)
        {
            cutter = null;
            error = null;

            if (branch == null ||
                branch.HeaderDimensions == null ||
                branch.BranchDimensions == null)
            {
                error =
                    "The SET-ON shaped-branch geometry information is missing.";

                return false;
            }

            XYZ surfaceOrigin;
            XYZ inwardAxis;
            XYZ radialInward;
            XYZ headerAxisPoint;

            if (!TryResolveSetOnBranchFrame(
                    branch,
                    out surfaceOrigin,
                    out inwardAxis,
                    out radialInward,
                    out headerAxisPoint,
                    out error))
            {
                return false;
            }

            double openingDiameter =
                branch.BranchDimensions.InsideDiameter +
                (ShapedBranchHoleClearanceMillimetres /
                 FeetToMillimetres);

            if (openingDiameter <= GeometryTolerance)
            {
                error =
                    "The SET-ON header opening diameter is zero or invalid.";

                return false;
            }

            // A very small radial Boolean overlap prevents coincident faces
            // from leaving a hairline membrane. It is not fabrication
            // clearance and changes the diameter by only 0.10 mm overall.
            double radialBooleanOverlap =
                0.05 / FeetToMillimetres;

            double cutterRadius =
                (openingDiameter / 2.0) +
                radialBooleanOverlap;

            double requiredInwardDepth;

            if (!TryResolveSetOnOpeningAxialDepth(
                    branch,
                    surfaceOrigin,
                    inwardAxis,
                    cutterRadius,
                    out requiredInwardDepth,
                    out error))
            {
                return false;
            }

            double outsideExtension = Math.Max(
                10.0 / FeetToMillimetres,
                branch.BranchDimensions.OutsideDiameter);

            XYZ cutterStart =
                surfaceOrigin -
                (inwardAxis * outsideExtension);

            double cutterLength =
                outsideExtension +
                requiredInwardDepth;

            if (cutterLength <= GeometryTolerance)
            {
                error =
                    "The SET-ON header opening cutter length is invalid.";

                return false;
            }

            try
            {
                // Keep the full circular cutter cross-section. Truncating only
                // along the branch axis produces the correct circle/ellipse on
                // the curved header surface instead of the slit produced by a
                // radial clipping slab.
                cutter = CreateCylinder(
                    cutterStart,
                    inwardAxis,
                    cutterLength,
                    cutterRadius);

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty SET-ON header opening " +
                        "cutter.";

                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The SET-ON header opening cylinder could not be " +
                    "created: " + ex.Message;

                return false;
            }
        }

        private static bool TryCreateSetOnHeaderOpeningCutter(
            SideCouplingConnection coupling,
            out Solid cutter,
            out string error)
        {
            cutter = null;
            error = null;

            if (coupling == null)
            {
                error =
                    "The tap-half coupling opening information is missing.";

                return false;
            }

            // Reuse the proven SET-ON opening geometry. A tap-half coupling
            // follows the same header-wall cut rule as a shaped branch, but
            // both coupling interfaces remain plain-ended.
            ShapedBranchConnection opening =
                new ShapedBranchConnection
                {
                    FittingId = coupling.FittingId,
                    FittingName = coupling.FittingName,
                    HeaderPipeId = coupling.HeaderPipeId,
                    BranchPipeId = coupling.OutletPipeId,
                    HeaderDimensions = coupling.HeaderDimensions,
                    BranchDimensions = coupling.OutletDimensions,
                    HeaderConnectorOrigin =
                        coupling.HeaderConnectorOrigin,
                    HeaderInwardDirection =
                        coupling.HeaderInwardDirection,
                    HeaderAxisStart = coupling.HeaderAxisStart,
                    HeaderAxisDirection = coupling.HeaderAxisDirection,
                    HeaderAxisLength = coupling.HeaderAxisLength
                };

            bool succeeded = TryCreateSetOnHeaderOpeningCutter(
                opening,
                out cutter,
                out error);

            if (!succeeded && !string.IsNullOrWhiteSpace(error))
            {
                error = error
                    .Replace("shaped-branch", "tap-half coupling")
                    .Replace("Shaped-branch", "Tap-half coupling")
                    .Replace("SET-ON shaped branch", "tap-half coupling")
                    .Replace("SET-ON header", "tap-half coupling header");
            }

            return succeeded;
        }

        private static bool TryGetLineCylinderIntersections(
            XYZ lineOrigin,
            XYZ lineDirection,
            XYZ cylinderAxisOrigin,
            XYZ cylinderAxisDirection,
            double cylinderRadius,
            out double nearDistance,
            out double farDistance)
        {
            nearDistance = 0.0;
            farDistance = 0.0;

            if (lineOrigin == null ||
                lineDirection == null ||
                cylinderAxisOrigin == null ||
                cylinderAxisDirection == null ||
                cylinderRadius <= GeometryTolerance)
            {
                return false;
            }

            XYZ direction =
                lineDirection.Normalize();

            XYZ axis =
                cylinderAxisDirection.Normalize();

            XYZ offset =
                lineOrigin - cylinderAxisOrigin;

            XYZ perpendicularDirection =
                direction -
                (axis * direction.DotProduct(axis));

            XYZ perpendicularOffset =
                offset -
                (axis * offset.DotProduct(axis));

            double a =
                perpendicularDirection.DotProduct(
                    perpendicularDirection);

            if (a <= GeometryTolerance)
                return false;

            double b =
                2.0 *
                perpendicularOffset.DotProduct(
                    perpendicularDirection);

            double c =
                perpendicularOffset.DotProduct(
                    perpendicularOffset) -
                (cylinderRadius * cylinderRadius);

            double discriminant =
                (b * b) - (4.0 * a * c);

            if (discriminant < -GeometryTolerance)
                return false;

            discriminant =
                Math.Max(0.0, discriminant);

            double squareRoot =
                Math.Sqrt(discriminant);

            double first =
                (-b - squareRoot) / (2.0 * a);

            double second =
                (-b + squareRoot) / (2.0 * a);

            nearDistance =
                Math.Min(first, second);

            farDistance =
                Math.Max(first, second);

            return true;
        }

        private static bool TryGetIntersectionAtOrAfter(
            double first,
            double second,
            double minimum,
            out double intersection)
        {
            intersection = 0.0;

            bool firstValid =
                first >= minimum - GeometryTolerance;

            bool secondValid =
                second >= minimum - GeometryTolerance;

            if (!firstValid && !secondValid)
                return false;

            if (firstValid && secondValid)
            {
                intersection =
                    Math.Min(first, second);

                return true;
            }

            intersection =
                firstValid
                    ? first
                    : second;

            return true;
        }
    }
}
