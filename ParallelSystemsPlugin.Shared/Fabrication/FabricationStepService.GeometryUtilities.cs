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
        private static Solid CreateCylinder(
            XYZ start,
            XYZ direction,
            double length,
            double radius)
        {
            if (length <= GeometryTolerance)
                throw new ArgumentOutOfRangeException(nameof(length));

            if (radius <= GeometryTolerance)
                throw new ArgumentOutOfRangeException(nameof(radius));

            XYZ normalizedDirection = direction.Normalize();
            CurveLoop profile = CreateCircleLoop(
                start,
                normalizedDirection,
                radius);

            return GeometryCreationUtilities.CreateExtrusionGeometry(
                new List<CurveLoop> { profile },
                normalizedDirection,
                length);
        }

        private static CurveLoop CreateCircleLoop(
            XYZ center,
            XYZ normal,
            double radius)
        {
            XYZ normalizedNormal = normal.Normalize();
            XYZ helper = Math.Abs(normalizedNormal.Z) < 0.90
                ? XYZ.BasisZ
                : XYZ.BasisX;

            XYZ xAxis =
                normalizedNormal.CrossProduct(helper).Normalize();
            XYZ yAxis =
                normalizedNormal.CrossProduct(xAxis).Normalize();

            Arc firstHalf = Arc.Create(
                center,
                radius,
                0.0,
                Math.PI,
                xAxis,
                yAxis);

            Arc secondHalf = Arc.Create(
                center,
                radius,
                Math.PI,
                2.0 * Math.PI,
                xAxis,
                yAxis);

            CurveLoop loop = new CurveLoop();
            loop.Append(firstHalf);
            loop.Append(secondHalf);
            return loop;
        }

        private static List<Solid> GetElementSolids(Element element)
        {
            List<Solid> cachedSolids;

            if (TryGetCachedElementSolids(
                    element,
                    out cachedSolids))
            {
                return cachedSolids;
            }

            Options options = new Options
            {
                ComputeReferences = false,
                IncludeNonVisibleObjects = false,
                DetailLevel = ViewDetailLevel.Fine
            };

            GeometryElement geometry = element.get_Geometry(options);
            List<Solid> solids = new List<Solid>();

            CollectSolids(
                geometry,
                Transform.Identity,
                solids);

            CacheElementSolids(
                element,
                solids);

            return solids;
        }

        private static void CollectSolids(
            GeometryElement geometry,
            Transform transform,
            ICollection<Solid> solids)
        {
            if (geometry == null)
                return;

            foreach (GeometryObject geometryObject in geometry)
            {
                Solid solid = geometryObject as Solid;
                if (solid != null &&
                    solid.Volume > GeometryTolerance &&
                    solid.Faces.Size > 0)
                {
                    Solid transformed = transform == null
                        ? solid
                        : SolidUtils.CreateTransformed(
                            solid,
                            transform);

                    solids.Add(transformed);
                    continue;
                }

                GeometryInstance instance =
                    geometryObject as GeometryInstance;

                if (instance != null)
                {
                    Transform combined = transform.Multiply(
                        instance.Transform);

                    CollectSolids(
                        instance.GetSymbolGeometry(),
                        combined,
                        solids);
                }
            }
        }

        private static double GetElementExtent(Element element)
        {
            double cachedExtent;

            if (TryGetCachedElementExtent(
                    element,
                    out cachedExtent))
            {
                return cachedExtent;
            }

            BoundingBoxXYZ box =
                GetElementBoundingBoxCached(element);

            double extent;

            if (box == null)
            {
                extent = 10.0;
            }
            else
            {
                Transform transform =
                    box.Transform ?? Transform.Identity;

                XYZ minimum = transform.OfPoint(box.Min);
                XYZ maximum = transform.OfPoint(box.Max);
                double diagonal = minimum.DistanceTo(maximum);

                extent = Math.Max(diagonal * 2.5, 1.0);
            }

            CacheElementExtent(
                element,
                extent);

            return extent;
        }

        private static double Clamp(
            double value,
            double minimum,
            double maximum)
        {
            return Math.Max(minimum, Math.Min(maximum, value));
        }
    }
}
