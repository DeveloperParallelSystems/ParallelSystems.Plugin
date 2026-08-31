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
        private static bool TryCreateChamferCutter(
            EndPreparation preparation,
            double shortCurveTolerance,
            out Solid cutter,
            out string error)
        {
            cutter = null;
            error = null;

            double rootFaceMillimetres =
                preparation.RootFaceMillimetres > 0
                    ? preparation.RootFaceMillimetres
                    : ChamferRootFaceMillimetres;

            double rootFace =
                rootFaceMillimetres / FeetToMillimetres;

            double outsideRadius = preparation.OutsideDiameter / 2.0;
            double insideRadius = preparation.InsideDiameter / 2.0;
            double wallThickness = outsideRadius - insideRadius;

            if (outsideRadius <= GeometryTolerance ||
                insideRadius <= GeometryTolerance ||
                insideRadius >= outsideRadius)
            {
                error =
                    "The chamfer dimensions are invalid at " +
                    preparation.ConnectionLabel + ". Resolved OD " +
                    FormatMillimetres(preparation.OutsideDiameter) +
                    " and ID " +
                    FormatMillimetres(preparation.InsideDiameter) + ".";
                return false;
            }

            if (wallThickness <= rootFace + GeometryTolerance)
            {
                error =
                    "The 30 degree chamfer cannot be created at " +
                    preparation.ConnectionLabel +
                    " because the resolved wall thickness " +
                    FormatMillimetres(wallThickness) +
                    " is not greater than the required " +
                    rootFaceMillimetres.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm root face.";
                return false;
            }

            double radialBevel = wallThickness - rootFace;
            double angleRadians =
                ChamferAngleDegrees * Math.PI / 180.0;

            // The client angle is measured from the end face. Therefore the
            // axial bevel depth is radial bevel x tan(angle).
            double axialDepth = radialBevel * Math.Tan(angleRadians);
            double epsilon =
                BooleanExtensionMillimetres / FeetToMillimetres;

            if (axialDepth <= GeometryTolerance)
            {
                error =
                    "The calculated chamfer depth is invalid at " +
                    preparation.ConnectionLabel + ".";
                return false;
            }

            XYZ outward = preparation.OutwardDirection.Normalize();
            XYZ inward = -outward;

            XYZ helper = Math.Abs(inward.Z) < 0.90
                ? XYZ.BasisZ
                : XYZ.BasisX;

            XYZ radial = inward.CrossProduct(helper).Normalize();
            XYZ tangential = inward.CrossProduct(radial).Normalize();

            double radialPerAxial = radialBevel / axialDepth;
            double startAxial = -epsilon;

            // Every bounded curve used by Revit must be longer than the
            // application's ShortCurveTolerance. The previous profile used a
            // fixed 0.25 mm radial extension. At a 30 degree bevel that left
            // one profile edge at roughly 0.57 mm, which is below Revit's
            // tolerance in common installations and caused Line.CreateBound
            // to fail before the revolved cutter was created.
            double minimumProfileEdge = Math.Max(
                shortCurveTolerance * 1.50,
                1.0 / FeetToMillimetres);

            double naturalEndAxial = axialDepth + epsilon;
            double endAxial = Math.Max(
                naturalEndAxial,
                startAxial + minimumProfileEdge);

            // Continue the exact 30 degree bevel line outside the physical
            // pipe/fitting envelope. Extending the profile outside the source
            // solid does not change the finished bevel, but it prevents short
            // or coincident profile edges during solid creation and Boolean
            // subtraction.
            double innerStartRadius =
                (insideRadius + rootFace) -
                (radialPerAxial * epsilon);

            double innerEndRadius =
                innerStartRadius +
                (radialPerAxial * (endAxial - startAxial));

            double outerProfileRadius =
                innerEndRadius + minimumProfileEdge;

            if (innerStartRadius <= insideRadius ||
                innerEndRadius <= outsideRadius ||
                innerEndRadius >= outerProfileRadius)
            {
                error =
                    "The extended chamfer profile is invalid at " +
                    preparation.ConnectionLabel + ".";
                return false;
            }

            XYZ first = preparation.Origin +
                (radial * innerStartRadius) +
                (inward * startAxial);

            XYZ second = preparation.Origin +
                (radial * outerProfileRadius) +
                (inward * startAxial);

            XYZ third = preparation.Origin +
                (radial * outerProfileRadius) +
                (inward * endAxial);

            XYZ fourth = preparation.Origin +
                (radial * innerEndRadius) +
                (inward * endAxial);

            double requiredCurveLength = Math.Max(
                shortCurveTolerance * 1.05,
                GeometryTolerance);

            double firstEdgeLength = first.DistanceTo(second);
            double secondEdgeLength = second.DistanceTo(third);
            double thirdEdgeLength = third.DistanceTo(fourth);
            double fourthEdgeLength = fourth.DistanceTo(first);

            if (firstEdgeLength <= requiredCurveLength ||
                secondEdgeLength <= requiredCurveLength ||
                thirdEdgeLength <= requiredCurveLength ||
                fourthEdgeLength <= requiredCurveLength)
            {
                error =
                    "The calculated 30 degree chamfer profile is smaller than " +
                    "Revit's short-curve tolerance at " +
                    preparation.ConnectionLabel +
                    ". Resolved wall thickness " +
                    FormatMillimetres(wallThickness) +
                    ", root face " +
                    rootFaceMillimetres.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm.";
                return false;
            }

            try
            {
                CurveLoop profile = new CurveLoop();
                profile.Append(Line.CreateBound(first, second));
                profile.Append(Line.CreateBound(second, third));
                profile.Append(Line.CreateBound(third, fourth));
                profile.Append(Line.CreateBound(fourth, first));

                Frame frame = new Frame(
                    preparation.Origin,
                    radial,
                    tangential,
                    inward);

                cutter = GeometryCreationUtilities.CreateRevolvedGeometry(
                    frame,
                    new List<CurveLoop> { profile },
                    0.0,
                    2.0 * Math.PI,
                    new SolidOptions(
                        ElementId.InvalidElementId,
                        ElementId.InvalidElementId));

                if (cutter == null ||
                    cutter.Volume <= GeometryTolerance)
                {
                    error =
                        "Revit generated an empty chamfer cutter at " +
                        preparation.ConnectionLabel + ".";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error =
                    "The 30 degree chamfer cutter could not be generated at " +
                    preparation.ConnectionLabel + ": " + ex.Message;
                return false;
            }
        }

        private static bool TryApplyVerifiedChamfer(
            IList<Solid> sourceSolids,
            EndPreparation preparation,
            XYZ originalConnectorOrigin,
            bool usePhysicalFaceSearch,
            double elementExtent,
            double shortCurveTolerance,
            out List<Solid> resultSolids,
            out string note,
            out string error)
        {
            resultSolids = sourceSolids?.ToList() ??
                new List<Solid>();
            note = null;
            error = null;

            if (preparation == null)
            {
                error = "The chamfer preparation data is missing.";
                return false;
            }

            List<Tuple<XYZ, string>> initialOrigins =
                new List<Tuple<XYZ, string>>();

            AddChamferOriginCandidate(
                initialOrigins,
                preparation.Origin,
                "resolved fitting face");

            AddChamferOriginCandidate(
                initialOrigins,
                originalConnectorOrigin,
                "connector origin");

            foreach (Tuple<XYZ, string> candidate in initialOrigins)
            {
                EndPreparation candidatePreparation =
                    CloneEndPreparation(
                        preparation,
                        candidate.Item1,
                        preparation.OutwardDirection);

                string directionNote;
                string candidateError;

                if (TrySubtractChamferInEitherDirection(
                        sourceSolids,
                        candidatePreparation,
                        shortCurveTolerance,
                        out resultSolids,
                        out directionNote,
                        out candidateError))
                {
                    note =
                        "Chamfer applied from " +
                        candidate.Item2;

                    if (!string.IsNullOrWhiteSpace(directionNote))
                        note += "; " + directionNote;

                    return true;
                }

                if (!string.IsNullOrWhiteSpace(candidateError))
                    error = candidateError;
            }

            if (usePhysicalFaceSearch &&
                originalConnectorOrigin != null &&
                preparation.OutwardDirection != null &&
                preparation.OutwardDirection.GetLength() >
                GeometryTolerance)
            {
                XYZ outward =
                    preparation.OutwardDirection.Normalize();

                double step =
                    ShapedBranchFaceSearchStepMillimetres /
                    FeetToMillimetres;

                double maximumSearch = Math.Min(
                    ShapedBranchMaximumFaceSearchMillimetres /
                    FeetToMillimetres,
                    Math.Max(
                        10.0 / FeetToMillimetres,
                        Math.Min(
                            elementExtent * 0.25,
                            preparation.OutsideDiameter)));

                int stepCount = Math.Max(
                    1,
                    (int)Math.Ceiling(maximumSearch / step));

                for (int index = 1;
                     index <= stepCount;
                     index++)
                {
                    double offset = index * step;

                    // The physical fitting face is normally inward from a
                    // connector placed on a weld-gap/reference plane. Test
                    // inward first, then outward as a defensive fallback.
                    XYZ[] candidateOrigins =
                    {
                        originalConnectorOrigin -
                        (outward * offset),

                        originalConnectorOrigin +
                        (outward * offset)
                    };

                    foreach (XYZ candidateOrigin in
                             candidateOrigins)
                    {
                        EndPreparation candidatePreparation =
                            CloneEndPreparation(
                                preparation,
                                candidateOrigin,
                                outward);

                        string directionNote;
                        string candidateError;

                        if (!TrySubtractChamferInEitherDirection(
                                sourceSolids,
                                candidatePreparation,
                                shortCurveTolerance,
                                out resultSolids,
                                out directionNote,
                                out candidateError))
                        {
                            if (!string.IsNullOrWhiteSpace(
                                    candidateError))
                            {
                                error = candidateError;
                            }

                            continue;
                        }

                        double signedOffset =
                            (candidateOrigin -
                             originalConnectorOrigin)
                            .DotProduct(outward);

                        note =
                            "Physical fitting end face recovered " +
                            FormatMillimetres(
                                Math.Abs(signedOffset)) +
                            (signedOffset < 0
                                ? " inward from connector"
                                : " outward from connector");

                        if (!string.IsNullOrWhiteSpace(
                                directionNote))
                        {
                            note += "; " + directionNote;
                        }

                        return true;
                    }
                }
            }

            error =
                "The required 30 degree chamfer did not remove material at " +
                preparation.ConnectionLabel +
                ". The connector direction was checked in both directions";

            if (usePhysicalFaceSearch)
            {
                error +=
                    ", and the physical fitting end face was searched " +
                    "around the connector/reference plane";
            }

            error +=
                ". Verify that the family contains physical end material " +
                "with OD/ID matching the connected pipe.";

            return false;
        }

        private static void AddChamferOriginCandidate(
            ICollection<Tuple<XYZ, string>> candidates,
            XYZ origin,
            string description)
        {
            if (candidates == null || origin == null)
                return;

            if (candidates.Any(x =>
                    x.Item1 != null &&
                    x.Item1.DistanceTo(origin) <=
                    GeometryTolerance))
            {
                return;
            }

            candidates.Add(
                Tuple.Create(origin, description));
        }

        private static EndPreparation CloneEndPreparation(
            EndPreparation source,
            XYZ origin,
            XYZ outwardDirection)
        {
            return new EndPreparation
            {
                Origin = origin,
                OutwardDirection = outwardDirection,
                OutsideDiameter = source.OutsideDiameter,
                InsideDiameter = source.InsideDiameter,
                WallThickness = source.WallThickness,
                RootFaceMillimetres = source.RootFaceMillimetres,
                ShouldChamfer = source.ShouldChamfer,
                ConnectionLabel = source.ConnectionLabel,
                Description = source.Description
            };
        }

        private static bool TrySubtractChamferInEitherDirection(
            IList<Solid> sourceSolids,
            EndPreparation preparation,
            double shortCurveTolerance,
            out List<Solid> resultSolids,
            out string note,
            out string error)
        {
            resultSolids = sourceSolids?.ToList() ??
                new List<Solid>();
            note = null;
            error = null;

            bool primaryRemoved = false;
            double primaryRemovedVolume = 0.0;
            List<Solid> primaryResult = resultSolids;

            Solid primaryCutter;
            string primaryError;

            if (TryCreateChamferCutter(
                    preparation,
                    shortCurveTolerance,
                    out primaryCutter,
                    out primaryError))
            {
                primaryResult =
                    SubtractCutterFromSolids(
                        sourceSolids,
                        primaryCutter,
                        out primaryRemoved,
                        out primaryRemovedVolume);
            }

            EndPreparation reversedPreparation =
                CloneEndPreparation(
                    preparation,
                    preparation.Origin,
                    -preparation.OutwardDirection);

            bool reversedRemoved = false;
            double reversedRemovedVolume = 0.0;
            List<Solid> reversedResult = resultSolids;

            Solid reversedCutter;
            string reversedError;

            if (TryCreateChamferCutter(
                    reversedPreparation,
                    shortCurveTolerance,
                    out reversedCutter,
                    out reversedError))
            {
                reversedResult =
                    SubtractCutterFromSolids(
                        sourceSolids,
                        reversedCutter,
                        out reversedRemoved,
                        out reversedRemovedVolume);
            }

            if (!primaryRemoved && !reversedRemoved)
            {
                error =
                    !string.IsNullOrWhiteSpace(primaryError)
                        ? primaryError
                        : reversedError;

                return false;
            }

            bool useReversed =
                reversedRemoved &&
                (!primaryRemoved ||
                 reversedRemovedVolume >
                 primaryRemovedVolume * 1.05);

            resultSolids =
                useReversed
                    ? reversedResult
                    : primaryResult;

            if (useReversed)
            {
                note =
                    "connector direction automatically corrected";
            }

            return true;
        }

        private static bool TryResolvePhysicalEndFaceOrigin(
            IList<Solid> sourceSolids,
            XYZ connectorOrigin,
            XYZ outwardDirection,
            double outsideDiameter,
            double insideDiameter,
            double elementExtent,
            out XYZ physicalOrigin,
            out double signedOffset)
        {
            physicalOrigin = connectorOrigin;
            signedOffset = 0.0;

            if (sourceSolids == null ||
                sourceSolids.Count == 0 ||
                connectorOrigin == null ||
                outwardDirection == null ||
                outwardDirection.GetLength() <= GeometryTolerance ||
                outsideDiameter <= GeometryTolerance ||
                insideDiameter <= GeometryTolerance ||
                insideDiameter >= outsideDiameter)
            {
                return false;
            }

            XYZ outward = outwardDirection.Normalize();

            XYZ helper =
                Math.Abs(outward.Z) < 0.90
                    ? XYZ.BasisZ
                    : XYZ.BasisX;

            XYZ radialX =
                outward.CrossProduct(helper).Normalize();

            XYZ radialY =
                outward.CrossProduct(radialX).Normalize();

            double probeRadius =
                (outsideDiameter + insideDiameter) / 4.0;

            double searchDistance = Math.Max(
                elementExtent,
                Math.Max(
                    outsideDiameter * 4.0,
                    50.0 / FeetToMillimetres));

            List<double> probeOffsets =
                new List<double>();

            const int probeCount = 8;

            for (int probeIndex = 0;
                 probeIndex < probeCount;
                 probeIndex++)
            {
                double angle =
                    (2.0 * Math.PI * probeIndex) /
                    probeCount;

                XYZ radialOffset =
                    (radialX * (Math.Cos(angle) * probeRadius)) +
                    (radialY * (Math.Sin(angle) * probeRadius));

                XYZ lineStart =
                    connectorOrigin +
                    (outward * searchDistance) +
                    radialOffset;

                XYZ lineEnd =
                    connectorOrigin -
                    (outward * searchDistance) +
                    radialOffset;

                Line probeLine;

                try
                {
                    probeLine =
                        Line.CreateBound(
                            lineStart,
                            lineEnd);
                }
                catch
                {
                    continue;
                }

                List<double> boundaryOffsets =
                    new List<double>();

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
                                       probeLine,
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

                                    for (int endIndex = 0;
                                         endIndex < 2;
                                         endIndex++)
                                    {
                                        XYZ point =
                                            segment.GetEndPoint(
                                                endIndex);

                                        double offset =
                                            (point -
                                             connectorOrigin)
                                            .DotProduct(outward);

                                        if (Math.Abs(offset) <=
                                            searchDistance +
                                            GeometryTolerance)
                                        {
                                            boundaryOffsets.Add(
                                                offset);
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch
                    {
                        // A malformed or open family solid should not prevent
                        // the remaining valid solids/probes from being tested.
                    }
                }

                if (boundaryOffsets.Count == 0)
                    continue;

                double nearestBoundary =
                    boundaryOffsets
                        .OrderBy(x => Math.Abs(x))
                        .First();

                probeOffsets.Add(nearestBoundary);
            }

            if (probeOffsets.Count == 0)
                return false;

            List<double> orderedOffsets =
                probeOffsets
                    .OrderBy(x => x)
                    .ToList();

            signedOffset =
                orderedOffsets[
                    orderedOffsets.Count / 2];

            if (Math.Abs(signedOffset) >
                searchDistance * 0.90)
            {
                return false;
            }

            physicalOrigin =
                connectorOrigin +
                (outward * signedOffset);

            return true;
        }

        private sealed class EndPreparation
        {
            public XYZ Origin { get; set; }
            public XYZ OutwardDirection { get; set; }
            public double OutsideDiameter { get; set; }
            public double InsideDiameter { get; set; }
            public double WallThickness { get; set; }
            public double RootFaceMillimetres { get; set; }
            public bool ShouldChamfer { get; set; }
            public string ConnectionLabel { get; set; }
            public string Description { get; set; }
        }
    }
}
