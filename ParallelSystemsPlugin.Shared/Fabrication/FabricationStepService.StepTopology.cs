using System;
using System.Globalization;
using System.IO;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        private sealed class FabricationStepTopologySummary
        {
            public int ManifoldSolidBRepCount { get; set; }
            public int ClosedShellCount { get; set; }
            public int OpenShellCount { get; set; }
            public int AdvancedFaceCount { get; set; }
            public int CylindricalSurfaceCount { get; set; }
            public int BSplineSurfaceCount { get; set; }

            public string BuildMessage()
            {
                return
                    "STEP topology: manifold solids " +
                    ManifoldSolidBRepCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ", closed shells " +
                    ClosedShellCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ", open shells " +
                    OpenShellCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ", advanced faces " +
                    AdvancedFaceCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ", cylindrical surfaces " +
                    CylindricalSurfaceCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ", B-spline surfaces " +
                    BSplineSurfaceCount.ToString(
                        CultureInfo.InvariantCulture) +
                    ".";
            }
        }

        private static bool TryValidateStagedStepTopology(
            string stepPath,
            int maximumExpectedCompactSetOnFaceCount,
            out FabricationStepTopologySummary summary,
            out string error)
        {
            summary = null;
            error = null;

            if (string.IsNullOrWhiteSpace(stepPath) ||
                !File.Exists(stepPath) ||
                new FileInfo(stepPath).Length == 0)
            {
                error =
                    "The staged STEP file is missing or empty during " +
                    "topology validation.";

                return false;
            }

            FabricationStepTopologySummary parsed =
                new FabricationStepTopologySummary();

            try
            {
                using (StreamReader reader =
                       new StreamReader(stepPath))
                {
                    string line;

                    while ((line = reader.ReadLine()) != null)
                    {
                        parsed.ManifoldSolidBRepCount +=
                            CountStepEntityType(
                                line,
                                "MANIFOLD_SOLID_BREP");

                        parsed.ClosedShellCount +=
                            CountStepEntityType(
                                line,
                                "CLOSED_SHELL");

                        parsed.OpenShellCount +=
                            CountStepEntityType(
                                line,
                                "OPEN_SHELL");

                        parsed.AdvancedFaceCount +=
                            CountStepEntityType(
                                line,
                                "ADVANCED_FACE");

                        parsed.CylindricalSurfaceCount +=
                            CountStepEntityType(
                                line,
                                "CYLINDRICAL_SURFACE");

                        parsed.BSplineSurfaceCount +=
                            CountStepEntityType(
                                line,
                                "B_SPLINE_SURFACE_WITH_KNOTS");
                    }
                }
            }
            catch (Exception ex)
            {
                error =
                    "The staged STEP topology could not be inspected: " +
                    ex.Message;

                return false;
            }

            summary = parsed;

            if (parsed.ManifoldSolidBRepCount <= 0 ||
                parsed.ClosedShellCount <= 0)
            {
                error =
                    "The exported STEP does not contain a verified closed " +
                    "manifold solid. " +
                    parsed.BuildMessage();

                return false;
            }

            if (parsed.OpenShellCount > 0)
            {
                error =
                    "The exported STEP contains one or more open shells. " +
                    "The file was not saved for fabrication. " +
                    parsed.BuildMessage();

                return false;
            }

            if (maximumExpectedCompactSetOnFaceCount > 0)
            {
                if (parsed.ManifoldSolidBRepCount != 1 ||
                    parsed.ClosedShellCount != 1)
                {
                    error =
                        "The selected SET-ON branch did not export as one " +
                        "closed solid body. " +
                        parsed.BuildMessage();

                    return false;
                }

                if (parsed.AdvancedFaceCount <= 0 ||
                    parsed.AdvancedFaceCount >
                        maximumExpectedCompactSetOnFaceCount)
                {
                    error =
                        "The selected SET-ON branch exported with " +
                        parsed.AdvancedFaceCount.ToString(
                            CultureInfo.InvariantCulture) +
                        " STEP faces. The compact smooth topology allows no " +
                        "more than " +
                        maximumExpectedCompactSetOnFaceCount.ToString(
                            CultureInfo.InvariantCulture) +
                        ". The file was blocked so segmented topology is not " +
                        "released for fabrication.";

                    return false;
                }
            }

            return true;
        }

        private static int CountStepEntityType(
            string line,
            string entityType)
        {
            if (string.IsNullOrEmpty(line) ||
                string.IsNullOrEmpty(entityType))
            {
                return 0;
            }

            int count = 0;
            int searchIndex = 0;

            while (searchIndex < line.Length)
            {
                int equalsIndex =
                    line.IndexOf(
                        '=',
                        searchIndex);

                if (equalsIndex < 0)
                    break;

                int entityIndex =
                    equalsIndex + 1;

                while (entityIndex < line.Length &&
                       char.IsWhiteSpace(
                           line[entityIndex]))
                {
                    entityIndex++;
                }

                if (entityIndex + entityType.Length <=
                        line.Length &&
                    string.Compare(
                        line,
                        entityIndex,
                        entityType,
                        0,
                        entityType.Length,
                        StringComparison.OrdinalIgnoreCase) == 0)
                {
                    int openingParenthesisIndex =
                        entityIndex +
                        entityType.Length;

                    while (openingParenthesisIndex < line.Length &&
                           char.IsWhiteSpace(
                               line[openingParenthesisIndex]))
                    {
                        openingParenthesisIndex++;
                    }

                    if (openingParenthesisIndex < line.Length &&
                        line[openingParenthesisIndex] == '(')
                    {
                        count++;
                    }
                }

                searchIndex =
                    equalsIndex + 1;
            }

            return count;
        }
    }
}
