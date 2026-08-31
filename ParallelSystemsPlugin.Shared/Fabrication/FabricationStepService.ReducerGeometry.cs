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
        private static FabricationElementGeometry
            BuildProceduralConcentricReducerGeometry(
                Document doc,
                Element element,
                IList<ConnectorBore> bores,
                IList<FabricationIssue> issues)
        {
            if (bores == null || bores.Count != 2)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "A concentric reducer requires exactly two round " +
                        "connectors with resolved pipe dimensions."
                });

                return null;
            }

            List<ConnectorBore> ordered = bores
                .OrderByDescending(x => x.OutsideDiameter)
                .ToList();

            ConnectorBore largeEnd = ordered[0];
            ConnectorBore smallEnd = ordered[1];

            if (!IsValidReducerEnd(largeEnd) ||
                !IsValidReducerEnd(smallEnd))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The concentric reducer end dimensions are invalid. " +
                        "Both ends require actual OD and ID values from the " +
                        "connected pipes, with ID smaller than OD."
                });

                return null;
            }

            if (Math.Abs(
                    largeEnd.OutsideDiameter -
                    smallEnd.OutsideDiameter) <=
                Math.Max(
                    DiameterTolerance,
                    largeEnd.OutsideDiameter * 0.001))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The selected fitting is classified as a concentric " +
                        "reducer, but the two resolved outside diameters are " +
                        "equal or nearly equal."
                });

                return null;
            }

            XYZ largeOrigin =
                largeEnd.OriginalConnectorOrigin ?? largeEnd.Origin;

            XYZ smallOrigin =
                smallEnd.OriginalConnectorOrigin ?? smallEnd.Origin;

            if (largeOrigin == null || smallOrigin == null)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The concentric reducer connector origins could not " +
                        "be resolved."
                });

                return null;
            }

            XYZ axisVector = smallOrigin - largeOrigin;
            double reducerLength = axisVector.GetLength();

            if (reducerLength <= GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The concentric reducer connector planes have zero " +
                        "or invalid separation."
                });

                return null;
            }

            XYZ axis = axisVector.Normalize();
            double shortCurveTolerance =
                doc.Application.ShortCurveTolerance;

            double rootFace =
                ChamferRootFaceMillimetres /
                FeetToMillimetres;

            double largeInsideRadius =
                largeEnd.InsideDiameter / 2.0;

            double smallInsideRadius =
                smallEnd.InsideDiameter / 2.0;

            double largeLandingRadius =
                largeInsideRadius + rootFace;

            double smallLandingRadius =
                smallInsideRadius + rootFace;

            double largeOutsideRadius =
                largeEnd.OutsideDiameter / 2.0;

            double smallOutsideRadius =
                smallEnd.OutsideDiameter / 2.0;

            if (IsCopperCapillaryReducerLike(
                    doc,
                    element))
            {
                Solid plainCopperReducer;

                try
                {
                    plainCopperReducer =
                        CreatePlainConcentricReducerLoftShell(
                            largeOrigin,
                            smallOrigin,
                            axis,
                            reducerLength,
                            largeOutsideRadius,
                            smallOutsideRadius,
                            largeInsideRadius,
                            smallInsideRadius,
                            shortCurveTolerance);
                }
                catch (Exception ex)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName =
                            GetElementDisplayName(element),
                        Message =
                            "The plain-end copper capillary " +
                            "concentric reducer could not be generated: " +
                            ex.Message
                    });

                    return null;
                }

                if (plainCopperReducer == null ||
                    plainCopperReducer.Volume <=
                    GeometryTolerance)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity =
                            FabricationIssueSeverity.Blocking,
                        ElementId = element.Id,
                        ElementName =
                            GetElementDisplayName(element),
                        Message =
                            "Revit generated an empty plain-end " +
                            "copper capillary reducer solid."
                    });

                    return null;
                }

                return new FabricationElementGeometry
                {
                    SourceElementId = element.Id,
                    SourceUniqueId = element.UniqueId,
                    SourceName =
                        GetElementDisplayName(element),
                    CategoryName =
                        element.Category?.Name ??
                        "Pipe Fitting",
                    Geometry =
                        new List<GeometryObject>
                        {
                            plainCopperReducer
                        },
                    Status =
                        "Procedural copper capillary concentric " +
                        "reducer; plain ends 2; chamfered ends 0",
                    Notes =
                        "Copper capillary reducer rebuilt as an " +
                        "outer conical loft minus an extended inner " +
                        "conical loft; both ends plain; no butt-weld " +
                        "chamfer; avoids sub-tolerance wall-profile " +
                        "segments"
                };
            }

            if (largeLandingRadius >=
                    largeOutsideRadius - GeometryTolerance ||
                smallLandingRadius >=
                    smallOutsideRadius - GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The concentric reducer cannot retain the required " +
                        "1 mm root face because the resolved wall thickness " +
                        "is too small."
                });

                return null;
            }

            double outerSlope =
                (smallOutsideRadius - largeOutsideRadius) /
                reducerLength;

            double innerSlope =
                (smallInsideRadius - largeInsideRadius) /
                reducerLength;

            double angleRadians =
                ChamferAngleDegrees *
                Math.PI /
                180.0;

            double tangent = Math.Tan(angleRadians);

            if (tangent <= GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The configured reducer chamfer angle is invalid."
                });

                return null;
            }

            // The chamfer is measured from the end face. In the radial/axial
            // profile, radial growth per unit axial depth is cot(angle).
            double chamferRadialPerAxial = 1.0 / tangent;

            // Intersect each chamfer line with the reducer's original outer
            // conical taper. This is the key difference from the previous
            // implementation: no cylindrical tangent/straight section is
            // inserted at either reducer end.
            double largeDenominator =
                chamferRadialPerAxial - outerSlope;

            double smallDenominator =
                chamferRadialPerAxial + outerSlope;

            if (largeDenominator <= GeometryTolerance ||
                smallDenominator <= GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The reducer taper is too steep for a 30 degree " +
                        "end chamfer to intersect both ends safely."
                });

                return null;
            }

            double largeChamferDepth =
                (largeOutsideRadius - largeLandingRadius) /
                largeDenominator;

            double smallChamferDepth =
                (smallOutsideRadius - smallLandingRadius) /
                smallDenominator;

            double largeChamferStation =
                largeChamferDepth;

            double smallChamferStation =
                reducerLength - smallChamferDepth;

            double minimumTransitionLength = Math.Max(
                shortCurveTolerance * 1.50,
                1.0 / FeetToMillimetres);

            if (largeChamferDepth <= GeometryTolerance ||
                smallChamferDepth <= GeometryTolerance ||
                smallChamferStation - largeChamferStation <=
                    minimumTransitionLength)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The concentric reducer is too short for two " +
                        "taper-preserving 30 degree chamfers with 1 mm " +
                        "root faces. Resolved length: " +
                        FormatMillimetres(reducerLength) + "."
                });

                return null;
            }

            double largeChamferOuterRadius =
                largeOutsideRadius +
                (outerSlope * largeChamferStation);

            double smallChamferOuterRadius =
                largeOutsideRadius +
                (outerSlope * smallChamferStation);

            double largeInnerRadiusAtChamfer =
                largeInsideRadius +
                (innerSlope * largeChamferStation);

            double smallInnerRadiusAtChamfer =
                largeInsideRadius +
                (innerSlope * smallChamferStation);

            if (largeChamferOuterRadius <=
                    largeInnerRadiusAtChamfer + GeometryTolerance ||
                smallChamferOuterRadius <=
                    smallInnerRadiusAtChamfer + GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The taper-preserving reducer chamfer would cross " +
                        "the internal bore. Verify the reducer length and " +
                        "connected-pipe OD/ID values."
                });

                return null;
            }

            Solid reducer;

            try
            {
                // Revolve one piecewise-linear shell. The outer reducer taper
                // continues directly into each chamfer intersection; there is
                // no forced straight/cylindrical material at either end.
                reducer = CreateTaperPreservingConcentricReducerSolid(
                    largeOrigin,
                    axis,
                    reducerLength,
                    largeInsideRadius,
                    smallInsideRadius,
                    largeLandingRadius,
                    smallLandingRadius,
                    largeChamferStation,
                    smallChamferStation,
                    largeChamferOuterRadius,
                    smallChamferOuterRadius,
                    shortCurveTolerance);
            }
            catch (Exception ex)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The taper-preserving procedural concentric reducer " +
                        "could not be generated: " + ex.Message
                });

                return null;
            }

            if (reducer == null ||
                reducer.Volume <= GeometryTolerance)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "Revit generated an empty taper-preserving " +
                        "concentric reducer solid."
                });

                return null;
            }

            return new FabricationElementGeometry
            {
                SourceElementId = element.Id,
                SourceUniqueId = element.UniqueId,
                SourceName = GetElementDisplayName(element),
                CategoryName =
                    element.Category?.Name ?? "Pipe Fitting",
                Geometry =
                    new List<GeometryObject> { reducer },
                Status =
                    "Procedural concentric reducer; taper-preserving chamfered ends 2; plain ends 0",
                Notes =
                    "Reducer rebuilt as one revolved piecewise-linear shell " +
                    "from connected-pipe OD/ID values; each 30 degree " +
                    "chamfer intersects the original conical taper directly; " +
                    "1 mm root faces; no cylindrical end tangents, smoothing " +
                    "loft, or post-build chamfer Boolean"
            };
        }

        private static Solid
            CreatePlainConcentricReducerLoftShell(
                XYZ largeOrigin,
                XYZ smallOrigin,
                XYZ axisDirection,
                double reducerLength,
                double largeOutsideRadius,
                double smallOutsideRadius,
                double largeInsideRadius,
                double smallInsideRadius,
                double shortCurveTolerance)
        {
            if (largeOrigin == null)
                throw new ArgumentNullException(nameof(largeOrigin));

            if (smallOrigin == null)
                throw new ArgumentNullException(nameof(smallOrigin));

            if (axisDirection == null ||
                axisDirection.GetLength() <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "Reducer axis direction is invalid.",
                    nameof(axisDirection));
            }

            if (reducerLength <= GeometryTolerance)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(reducerLength));
            }

            if (largeOutsideRadius <=
                    largeInsideRadius + GeometryTolerance ||
                smallOutsideRadius <=
                    smallInsideRadius + GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "Copper reducer OD/ID values do not define " +
                    "a positive wall thickness.");
            }

            XYZ axis = axisDirection.Normalize();

            CurveLoop largeOuterLoop = CreateCircleLoop(
                largeOrigin,
                axis,
                largeOutsideRadius);

            CurveLoop smallOuterLoop = CreateCircleLoop(
                smallOrigin,
                axis,
                smallOutsideRadius);

            Solid outer =
                GeometryCreationUtilities.CreateLoftGeometry(
                    new List<CurveLoop>
                    {
                        largeOuterLoop,
                        smallOuterLoop
                    },
                    new SolidOptions(
                        ElementId.InvalidElementId,
                        ElementId.InvalidElementId));

            if (outer == null ||
                outer.Volume <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "Revit generated an empty outer copper " +
                    "reducer loft.");
            }

            // Extend the bore beyond both end planes so the subtraction does
            // not leave coincident end caps or a thin internal diaphragm.
            double boreExtension = Math.Max(
                shortCurveTolerance * 1.50,
                1.0 / FeetToMillimetres);

            double insideSlope =
                (smallInsideRadius -
                 largeInsideRadius) /
                reducerLength;

            double extendedLargeInsideRadius =
                largeInsideRadius -
                (insideSlope * boreExtension);

            double extendedSmallInsideRadius =
                smallInsideRadius +
                (insideSlope * boreExtension);

            if (extendedLargeInsideRadius <=
                    GeometryTolerance ||
                extendedSmallInsideRadius <=
                    GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The extended copper reducer bore radius " +
                    "became zero or negative.");
            }

            XYZ extendedLargeOrigin =
                largeOrigin -
                (axis * boreExtension);

            XYZ extendedSmallOrigin =
                smallOrigin +
                (axis * boreExtension);

            CurveLoop largeInnerLoop = CreateCircleLoop(
                extendedLargeOrigin,
                axis,
                extendedLargeInsideRadius);

            CurveLoop smallInnerLoop = CreateCircleLoop(
                extendedSmallOrigin,
                axis,
                extendedSmallInsideRadius);

            Solid inner =
                GeometryCreationUtilities.CreateLoftGeometry(
                    new List<CurveLoop>
                    {
                        largeInnerLoop,
                        smallInnerLoop
                    },
                    new SolidOptions(
                        ElementId.InvalidElementId,
                        ElementId.InvalidElementId));

            if (inner == null ||
                inner.Volume <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "Revit generated an empty inner copper " +
                    "reducer loft.");
            }

            Solid shell =
                BooleanOperationsUtils.ExecuteBooleanOperation(
                    outer,
                    inner,
                    BooleanOperationsType.Difference);

            if (shell == null ||
                shell.Volume <= GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The copper reducer hollowing operation " +
                    "produced an empty solid.");
            }

            if (shell.Volume >=
                outer.Volume - GeometryTolerance)
            {
                throw new InvalidOperationException(
                    "The copper reducer bore did not remove " +
                    "measurable material.");
            }

            return shell;
        }

        private static Solid
            CreateTaperPreservingConcentricReducerSolid(
                XYZ largeOrigin,
                XYZ axisDirection,
                double reducerLength,
                double largeInsideRadius,
                double smallInsideRadius,
                double largeLandingRadius,
                double smallLandingRadius,
                double largeChamferStation,
                double smallChamferStation,
                double largeChamferOuterRadius,
                double smallChamferOuterRadius,
                double shortCurveTolerance)
        {
            if (largeOrigin == null)
                throw new ArgumentNullException(nameof(largeOrigin));

            if (axisDirection == null ||
                axisDirection.GetLength() <= GeometryTolerance)
            {
                throw new ArgumentException(
                    "Reducer axis direction is invalid.",
                    nameof(axisDirection));
            }

            XYZ axis = axisDirection.Normalize();
            XYZ helper = Math.Abs(axis.Z) < 0.90
                ? XYZ.BasisZ
                : XYZ.BasisX;

            XYZ radial =
                axis.CrossProduct(helper).Normalize();

            XYZ tangential =
                axis.CrossProduct(radial).Normalize();

            List<XYZ> profilePoints = new List<XYZ>
            {
                // Large-end bore and 1 mm root face.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    0.0,
                    largeInsideRadius),

                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    0.0,
                    largeLandingRadius),

                // Large-end 30 degree chamfer intersects the original taper.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    largeChamferStation,
                    largeChamferOuterRadius),

                // Original conical outer reducer transition remains continuous.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    smallChamferStation,
                    smallChamferOuterRadius),

                // Small-end 30 degree chamfer returns to the root face.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    reducerLength,
                    smallLandingRadius),

                // Small-end root face and bore.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    reducerLength,
                    smallInsideRadius),

                // Original conical internal bore; no straight end segment.
                ReducerProfilePoint(
                    largeOrigin,
                    axis,
                    radial,
                    0.0,
                    largeInsideRadius)
            };

            CurveLoop profile =
                CreateClosedLinearProfileLoop(
                    profilePoints,
                    shortCurveTolerance);

            Frame frame = new Frame(
                largeOrigin,
                radial,
                tangential,
                axis);

            return GeometryCreationUtilities.CreateRevolvedGeometry(
                frame,
                new List<CurveLoop> { profile },
                0.0,
                2.0 * Math.PI,
                new SolidOptions(
                    ElementId.InvalidElementId,
                    ElementId.InvalidElementId));
        }

        private static XYZ ReducerProfilePoint(
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

        private static CurveLoop CreateClosedLinearProfileLoop(
            IList<XYZ> points,
            double shortCurveTolerance)
        {
            if (points == null || points.Count < 3)
            {
                throw new InvalidOperationException(
                    "At least three points are required for a closed " +
                    "reducer profile.");
            }

            double minimumLength = Math.Max(
                shortCurveTolerance * 1.01,
                GeometryTolerance);

            List<XYZ> cleanedPoints = new List<XYZ>();

            foreach (XYZ point in points)
            {
                if (point == null)
                {
                    throw new InvalidOperationException(
                        "The reducer profile contains a null point.");
                }

                if (cleanedPoints.Count == 0 ||
                    cleanedPoints[cleanedPoints.Count - 1]
                        .DistanceTo(point) > GeometryTolerance)
                {
                    cleanedPoints.Add(point);
                }
            }

            if (cleanedPoints.Count > 1 &&
                cleanedPoints[0].DistanceTo(
                    cleanedPoints[cleanedPoints.Count - 1]) <=
                GeometryTolerance)
            {
                cleanedPoints.RemoveAt(cleanedPoints.Count - 1);
            }

            if (cleanedPoints.Count < 3)
            {
                throw new InvalidOperationException(
                    "The reducer profile collapsed to fewer than three " +
                    "unique points.");
            }

            CurveLoop loop = new CurveLoop();

            for (int index = 0;
                 index < cleanedPoints.Count;
                 index++)
            {
                XYZ start = cleanedPoints[index];
                XYZ end = cleanedPoints[
                    (index + 1) % cleanedPoints.Count];

                double segmentLength = start.DistanceTo(end);

                if (segmentLength <= minimumLength)
                {
                    throw new InvalidOperationException(
                        "A reducer profile segment is shorter than " +
                        "Revit's short-curve tolerance. Segment length: " +
                        FormatMillimetres(segmentLength) + ".");
                }

                loop.Append(Line.CreateBound(start, end));
            }

            return loop;
        }

        private static bool IsValidReducerEnd(
            ConnectorBore bore)
        {
            return bore != null &&
                   bore.OutsideDiameter > GeometryTolerance &&
                   bore.InsideDiameter > GeometryTolerance &&
                   bore.InsideDiameter < bore.OutsideDiameter &&
                   bore.WallThickness > GeometryTolerance;
        }
    }
}
