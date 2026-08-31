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
        private static bool IsFlangeLike(
            Document doc,
            Element element)
        {
            if (element == null)
                return false;

            FamilyInstance familyInstance = element as FamilyInstance;
            Autodesk.Revit.DB.Mechanical.MechanicalFitting mechanicalFitting =
                familyInstance?.MEPModel as
                    Autodesk.Revit.DB.Mechanical.MechanicalFitting;

            if (mechanicalFitting != null &&
                mechanicalFitting.PartType == PartType.PipeFlange)
            {
                return true;
            }

            string classification = NormalizeClassificationText(
                BuildElementClassificationText(doc, element));

            return classification.Contains("FLANGE") ||
                   classification.Contains("FLANGED") ||
                   classification.Contains("WELD NECK") ||
                   classification.Contains("SLIP ON") ||
                   classification.Contains("LAP JOINT") ||
                   classification.Contains("BLIND FLANGE") ||
                   classification.Contains(" SOFF ") ||
                   classification.Contains(" SOW ");
        }

        private static bool IsBlindFlangeLike(
            Document doc,
            Element element)
        {
            if (element == null)
                return false;

            string classification = NormalizeClassificationText(
                BuildElementClassificationText(doc, element));

            return classification.Contains("BLIND FLANGE") ||
                   classification.Contains("BLINDFLANGE");
        }

        private static bool IsShapedBranchLike(
            Document doc,
            Element element)
        {
            if (element == null || element is Pipe)
                return false;

            string classification =
                NormalizeClassificationText(
                    BuildElementClassificationText(
                        doc,
                        element));

            // Keep this intentionally narrow. A generic "BRANCH" test would
            // misclassify tees, crosses, and unrelated branch metadata.
            return classification.Contains("SHAPED BRANCH");
        }

        private static bool IsSideCouplingLike(
            Document doc,
            Element element)
        {
            if (element == null || element is Pipe)
                return false;

            string classification =
                NormalizeClassificationText(
                    BuildElementClassificationText(
                        doc,
                        element));

            // Keep this narrow so ordinary inline couplings are not treated
            // as side outlets. The tested Revit family is named
            // "Tap-Half Coupling".
            return classification.Contains("TAP HALF COUPLING") ||
                   classification.Contains("TAPHALF COUPLING");
        }

        private static bool IsCopperTubeFamilyLike(
            Document doc,
            Element element)
        {
            if (element == null)
                return false;

            string classification =
                NormalizeClassificationText(
                    BuildElementClassificationText(
                        doc,
                        element));

            return
                classification.Contains("COPPER") ||
                classification.Contains("KEMBLA") ||
                classification.Contains("COPPERMATE") ||
                classification.Contains("CAPILLARY");
        }

        private static bool
            IsCopperCapillaryReducerLike(
                Document doc,
                Element element)
        {
            if (element == null ||
                element is Pipe ||
                !IsConcentricReducerLike(
                    doc,
                    element))
            {
                return false;
            }

            string classification =
                NormalizeClassificationText(
                    BuildElementClassificationText(
                        doc,
                        element));

            // Keep this rule narrow. A copper butt-weld reducer, if one is
            // introduced later, must not automatically inherit capillary
            // plain-end behavior.
            return
                classification.Contains("CAPILLARY") ||
                classification.Contains("KEMBLA") ||
                classification.Contains("COPPERMATE");
        }

        private static bool IsConcentricReducerLike(
            Document doc,
            Element element)
        {
            if (element == null || element is Pipe)
                return false;

            string classification = NormalizeClassificationText(
                BuildElementClassificationText(doc, element));

            if (classification.Contains("ECC REDUCER") ||
                classification.Contains("ECCENTRIC REDUCER") ||
                classification.Contains("REDUCER ECCENTRIC") ||
                classification.Contains("REDUCERECCENTRIC") ||
                classification.Contains("ECCENTRICREDUCER"))
            {
                return false;
            }

            return
                classification.Contains("CON REDUCER") ||
                classification.Contains("CONCENTRIC REDUCER") ||
                classification.Contains("REDUCER CONCENTRIC") ||
                classification.Contains("REDUCERCONCENTRIC") ||
                classification.Contains("CONCENTRICREDUCER");
        }

        private static bool IsReducerLike(
            Document doc,
            Element element)
        {
            if (element == null || element is Pipe)
                return false;

            FamilyInstance familyInstance = element as FamilyInstance;
            Autodesk.Revit.DB.Mechanical.MechanicalFitting mechanicalFitting =
                familyInstance?.MEPModel as
                    Autodesk.Revit.DB.Mechanical.MechanicalFitting;

            if (mechanicalFitting != null)
            {
                string partType = NormalizeClassificationText(
                    mechanicalFitting.PartType.ToString());

                if (partType == "TRANSITION" ||
                    partType == "REDUCER" ||
                    partType == "REDUCTION")
                {
                    return true;
                }
            }

            string classification = NormalizeClassificationText(
                BuildElementClassificationText(doc, element));

            string padded = " " + classification + " ";

            return
                classification.Contains("CON REDUCER") ||
                classification.Contains("ECC REDUCER") ||
                classification.Contains("ECCENTRIC REDUCER") ||
                classification.Contains("CONCENTRIC REDUCER") ||
                padded.Contains(" REDUCER ") ||
                padded.Contains(" REDUCTION ");
        }

        private static bool IsIgnoredConnectionElement(
            Document doc,
            Element element)
        {
            if (element == null || element is Pipe)
                return false;

            bool cachedResult;

            if (TryGetCachedClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    out cachedResult))
            {
                return cachedResult;
            }

            FamilyInstance familyInstance = element as FamilyInstance;
            Autodesk.Revit.DB.Mechanical.MechanicalFitting mechanicalFitting =
                familyInstance?.MEPModel as
                    Autodesk.Revit.DB.Mechanical.MechanicalFitting;

            if (mechanicalFitting != null &&
                IsIgnoredConnectionClassificationValue(
                    mechanicalFitting.PartType.ToString()))
            {
                return CacheClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    true);
            }

            Element type = GetElementTypeCached(doc, element);
            string[] connectionMarkerParameters =
            {
                "Part Type",
                "#PCF_OBJECT_TYPE",
                "PCF_OBJECT_TYPE",
                "Component Type"
            };

            foreach (string parameterName in connectionMarkerParameters)
            {
                if (IsIgnoredConnectionClassificationValue(
                        GetParameterText(element, parameterName)) ||
                    IsIgnoredConnectionClassificationValue(
                        GetParameterText(type, parameterName)))
                {
                    return CacheClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    true);
                }
            }

            string[] connectorClassificationParameters =
            {
                "Connector Type",
                "Connection Type"
            };

            foreach (string parameterName in
                     connectorClassificationParameters)
            {
                string instanceValue = NormalizeClassificationText(
                    GetParameterText(element, parameterName));
                string typeValue = NormalizeClassificationText(
                    GetParameterText(type, parameterName));

                if (instanceValue == "NON CONNECTOR" ||
                    instanceValue == "NONCONNECTOR" ||
                    typeValue == "NON CONNECTOR" ||
                    typeValue == "NONCONNECTOR")
                {
                    return CacheClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    true);
                }
            }

            string classification = NormalizeClassificationText(
                BuildElementClassificationText(doc, element));

            if (classification.Contains("NON CONNECTOR") ||
                classification.Contains("NONCONNECTOR"))
            {
                return CacheClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    true);
            }

            // These identify connection-marker families rather than actual
            // pipe/fitting geometry. "WELD NECK" is intentionally not listed;
            // it is a real flange and must remain in the export.
            if (classification.Contains("WELD GAP") ||
                classification.Contains("FILLET WELD") ||
                classification.Contains("FIELD WELD") ||
                classification.Contains("SHOP WELD") ||
                classification.Contains("TACK WELD"))
            {
                return CacheClassificationResult(
                    element,
                    FabricationClassificationCacheKind.IgnoredConnection,
                    true);
            }

            bool containsStandaloneWeld =
                classification == "WELD" ||
                classification.StartsWith("WELD ", StringComparison.Ordinal) ||
                classification.EndsWith(" WELD", StringComparison.Ordinal) ||
                classification.Contains(" BUTTWELD ") ||
                classification.StartsWith("BUTTWELD ", StringComparison.Ordinal) ||
                classification.EndsWith(" BUTTWELD", StringComparison.Ordinal);

            return CacheClassificationResult(
                element,
                FabricationClassificationCacheKind.IgnoredConnection,
                containsStandaloneWeld &&
                !ContainsFabricationComponentKeyword(classification));
        }

        private static bool IsIgnoredConnectionClassificationValue(
            string value)
        {
            string normalized = NormalizeClassificationText(value);

            return normalized == "WELD" ||
                   normalized == "BUTT WELD" ||
                   normalized == "BUTTWELD" ||
                   normalized == "FILLET WELD" ||
                   normalized == "SOCKET WELD" ||
                   normalized == "WELD GAP" ||
                   normalized == "NON CONNECTOR" ||
                   normalized == "NONCONNECTOR";
        }

        private static bool ContainsFabricationComponentKeyword(
            string classification)
        {
            string padded = " " + (classification ?? string.Empty) + " ";

            string[] keywords =
            {
                " ELBOW ",
                " TEE ",
                " REDUCER ",
                " REDUCTION ",
                " FLANGE ",
                " COUPLING ",
                " SOCKET ",
                " CAP ",
                " VALVE ",
                " BRANCH ",
                " OLET ",
                " ADAPTER ",
                " UNION ",
                " CROSS ",
                " TRANSITION ",
                " FERRULE "
            };

            return keywords.Any(padded.Contains);
        }

        private static string BuildElementClassificationText(
            Document doc,
            Element element)
        {
            if (element == null)
                return string.Empty;

            string cachedText;

            if (TryGetCachedClassificationText(
                    doc,
                    element,
                    out cachedText))
            {
                return cachedText;
            }

            StringBuilder text = new StringBuilder();
            text.Append(' ');
            text.Append(GetElementDisplayName(element));
            text.Append(' ');
            text.Append(element.Name);

            FamilyInstance familyInstance = element as FamilyInstance;
            Autodesk.Revit.DB.Mechanical.MechanicalFitting mechanicalFitting =
                familyInstance?.MEPModel as
                    Autodesk.Revit.DB.Mechanical.MechanicalFitting;

            if (mechanicalFitting != null)
            {
                text.Append(' ');
                text.Append(mechanicalFitting.PartType.ToString());
            }

            Element type = GetElementTypeCached(doc, element);
            if (type != null)
            {
                text.Append(' ');
                text.Append(type.Name);
            }

            string[] parameterNames =
            {
                "Part Type",
                "Connector Type",
                "Connection Type",
                "Description",
                "#PCF_OBJECT_TYPE",
                "PCF_OBJECT_TYPE",
                "Component Type",
                "END_TYPE_1",
                "END_TYPE_2",
                "END_TYPE_3"
            };

            foreach (string parameterName in parameterNames)
            {
                AppendParameterText(text, element, parameterName);

                if (type != null)
                    AppendParameterText(text, type, parameterName);
            }

            string result = text.ToString();

            CacheClassificationText(
                doc,
                element,
                result);

            return result;
        }

        private static string NormalizeClassificationText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            StringBuilder normalized = new StringBuilder(value.Length);
            bool previousWasSpace = true;

            foreach (char character in value.ToUpperInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    normalized.Append(character);
                    previousWasSpace = false;
                }
                else if (!previousWasSpace)
                {
                    normalized.Append(' ');
                    previousWasSpace = true;
                }
            }

            return normalized.ToString().Trim();
        }

        private static string GetParameterText(
            Element element,
            string parameterName)
        {
            Parameter parameter = element?.LookupParameter(parameterName);
            if (parameter == null)
                return string.Empty;

            return parameter.StorageType == StorageType.String
                ? parameter.AsString() ?? string.Empty
                : parameter.AsValueString() ?? string.Empty;
        }

        private static void AppendParameterText(
            StringBuilder text,
            Element element,
            string parameterName)
        {
            Parameter parameter = element?.LookupParameter(parameterName);
            if (parameter == null)
                return;

            string value = parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();

            if (string.IsNullOrWhiteSpace(value))
                return;

            text.Append(' ');
            text.Append(value);
        }
    }
}
