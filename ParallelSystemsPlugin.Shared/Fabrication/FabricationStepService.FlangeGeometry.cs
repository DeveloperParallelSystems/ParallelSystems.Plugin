using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Models.Configs;
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

            FlangeDimensionConfiguration configuration;

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
                    configuration.NumberOfHoles,
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
                    configuration.PitchCircleDiameterMm,
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
                    configuration.HoleDiameter,
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
                    configuration.OutsideDiameterMm,
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

        private static bool TryResolveFlangeConfiguration(
            Document doc,
            Element flange,
            IList<ConnectorBore> bores,
            out FlangeDimensionConfiguration configuration,
            out string error)
        {
            configuration = null;
            error = null;

            string classification =
                BuildFlangeConfigurationText(doc, flange);
            string normalized =
                NormalizeClassificationText(classification);
            string compact = normalized.Replace(
                " ",
                string.Empty);
            string padded = " " + normalized + " ";

            string standard;

            if (compact.Contains("AS2129"))
                standard = "AS 2129";
            else if (compact.Contains("AS4087"))
                standard = "AS 4087";
            else if (compact.Contains("ANSI") &&
                     compact.Contains("B165"))
                standard = "ANSI B16.5";
            else if (compact.Contains("ISO7005") ||
                     padded.Contains(" DIN "))
                standard = "ISO 7005 (DIN)";
            else
            {
                error =
                    "The flange standard could not be resolved. Include an " +
                    "explicit standard such as AS 2129, AS 4087, ISO 7005 " +
                    "or ANSI B16.5 in the flange type, description, lookup " +
                    "table name, or Standard parameter.";
                return false;
            }

            string classOrTable =
                ResolveFlangeClassOrTable(
                    standard,
                    padded,
                    compact);

            if (string.IsNullOrWhiteSpace(classOrTable))
            {
                error =
                    "The flange class/table could not be resolved for " +
                    standard +
                    ". Include the class or table in the flange type, " +
                    "description, lookup table name, or Class/Table parameter.";
                return false;
            }

            List<double> nominalSizes = (bores ??
                    new List<ConnectorBore>())
                .Where(x =>
                    x != null &&
                    !x.IsSynthetic &&
                    x.NominalDiameter > GeometryTolerance)
                .Select(x =>
                    x.NominalDiameter * FeetToMillimetres)
                .ToList();

            if (nominalSizes.Count == 0)
            {
                error =
                    "The flange nominal size could not be resolved from its " +
                    "piping connectors.";
                return false;
            }

            if (nominalSizes.Max() - nominalSizes.Min() > 1.0)
            {
                error =
                    "The flange connectors report different nominal sizes, " +
                    "so one bolt configuration cannot be selected safely.";
                return false;
            }

            double nominalMillimetres =
                nominalSizes.Average();

            List<FlangeDimensionConfiguration> matches =
                FlangeDimensionConfigurationCatalog.All
                    .Where(x =>
                        string.Equals(
                            x.Standard,
                            standard,
                            StringComparison.Ordinal) &&
                        string.Equals(
                            x.ClassOrTable,
                            classOrTable,
                            StringComparison.OrdinalIgnoreCase) &&
                        Math.Abs(
                            x.NominalSizeMm -
                            nominalMillimetres) <= 0.5)
                    .ToList();

            if (matches.Count != 1)
            {
                error =
                    "No unique flange catalog row was found for " +
                    standard + " " + classOrTable + ", nominal " +
                    nominalMillimetres.ToString(
                        "0.###",
                        CultureInfo.InvariantCulture) +
                    " mm.";
                return false;
            }

            configuration = matches[0];
            return true;
        }

        private static string BuildFlangeConfigurationText(
            Document doc,
            Element flange)
        {
            StringBuilder text = new StringBuilder();
            text.Append(
                BuildElementClassificationText(doc, flange));

            Element type = GetElementTypeCached(doc, flange);
            string[] parameterNames =
            {
                "Standard",
                "Flange Standard",
                "Class",
                "Pressure Class",
                "Table",
                "Class / Table",
                "Lookup Table Name",
                "Type Name",
                "Description",
                "Description BOM"
            };

            foreach (string parameterName in parameterNames)
            {
                string instanceValue =
                    GetParameterText(flange, parameterName);
                string typeValue =
                    GetParameterText(type, parameterName);

                if (!string.IsNullOrWhiteSpace(instanceValue))
                {
                    text.Append(' ');
                    text.Append(instanceValue);
                }

                if (!string.IsNullOrWhiteSpace(typeValue))
                {
                    text.Append(' ');
                    text.Append(typeValue);
                }
            }

            return text.ToString();
        }

        private static string ResolveFlangeClassOrTable(
            string standard,
            string paddedClassification,
            string compactClassification)
        {
            if (standard == "AS 2129")
            {
                string[] tables =
                {
                    "A", "D", "E", "F", "G", "H"
                };

                foreach (string table in tables)
                {
                    if (paddedClassification.Contains(
                            " TABLE " + table + " "))
                    {
                        return "Table " + table;
                    }
                }

                return null;
            }

            if (standard == "AS 4087" ||
                standard == "ISO 7005 (DIN)")
            {
                string[] pressureClasses =
                    standard == "AS 4087"
                        ? new[] { "PN14", "PN16", "PN21", "PN35" }
                        : new[]
                        {
                            "PN6", "PN10", "PN16",
                            "PN20", "PN25", "PN40"
                        };

                foreach (string pressureClass in
                         pressureClasses)
                {
                    if (paddedClassification.Contains(
                            " " + pressureClass + " ") ||
                        compactClassification.Contains(
                            pressureClass))
                    {
                        return pressureClass;
                    }
                }

                return null;
            }

            if (standard == "ANSI B16.5")
            {
                if (compactClassification.Contains("125150"))
                    return "125/150";

                string[] classes =
                {
                    "600", "300", "150"
                };

                foreach (string flangeClass in classes)
                {
                    if (paddedClassification.Contains(
                            " CLASS " + flangeClass + " ") ||
                        paddedClassification.Contains(
                            " " + flangeClass + " "))
                    {
                        return flangeClass;
                    }
                }
            }

            return null;
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
            FlangeDimensionConfiguration configuration)
        {
            return configuration.Standard + " " +
                   configuration.ClassOrTable + ", nominal " +
                   configuration.NominalSizeMm.ToString(
                       CultureInfo.InvariantCulture) +
                   " mm";
        }
    }
}
