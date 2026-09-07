using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        private enum ModuleSupportSolidDisposition
        {
            Include,
            OmitWood,
            Ambiguous
        }

        private static FabricationElementGeometry
            BuildModuleSupportGeometry(
                Document doc,
                Element element,
                IList<FabricationIssue> issues)
        {
            if (doc == null || element == null)
                return null;

            List<GeometryObject> retainedGeometry =
                new List<GeometryObject>();

            int omittedWoodSolidCount = 0;
            int ambiguousSolidCount = 0;
            int omittedUpperClampSolidCount = 0;

            try
            {
                bool elementContainsWoodMaterial =
                    ElementContainsWoodMaterial(doc, element);

                Options options = new Options
                {
                    ComputeReferences = false,
                    IncludeNonVisibleObjects = false,
                    DetailLevel = ViewDetailLevel.Fine
                };

                GeometryElement geometry =
                    element.get_Geometry(options);

                CollectModuleSupportSolids(
                    doc,
                    geometry,
                    Transform.Identity,
                    elementContainsWoodMaterial,
                    retainedGeometry,
                    ref omittedWoodSolidCount,
                    ref ambiguousSolidCount);
            }
            catch (Exception ex)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The module support geometry could not be read: " +
                        ex.Message
                });

                return null;
            }

            if (ambiguousSolidCount > 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        ambiguousSolidCount.ToString() +
                        " support solid(s) have ambiguous Wood/non-Wood " +
                        "material assignments. The Module STEP was blocked " +
                        "instead of guessing which geometry to omit."
                });

                return null;
            }

            string upperClampError;

            if (!TryOmitUpperKshClampHalf(
                    element,
                    retainedGeometry,
                    out omittedUpperClampSolidCount,
                    out upperClampError))
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message = upperClampError
                });

                return null;
            }

            if (retainedGeometry.Count == 0)
            {
                if (omittedWoodSolidCount > 0)
                {
                    issues.Add(new FabricationIssue
                    {
                        Severity = FabricationIssueSeverity.Information,
                        ElementId = element.Id,
                        ElementName = GetElementDisplayName(element),
                        Message =
                            "The entire support component was omitted because " +
                            "all of its positive-volume solids are Wood."
                    });

                    return null;
                }

                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Blocking,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "No non-Wood closed support solids remained for the " +
                        "Module STEP export."
                });

                return null;
            }

            if (omittedWoodSolidCount > 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Information,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        omittedWoodSolidCount.ToString() +
                        " Wood support solid(s) were omitted while retaining " +
                        "the permitted metal bracket/clamp geometry."
                });
            }

            if (omittedUpperClampSolidCount > 0)
            {
                issues.Add(new FabricationIssue
                {
                    Severity = FabricationIssueSeverity.Information,
                    ElementId = element.Id,
                    ElementName = GetElementDisplayName(element),
                    Message =
                        "The upper metal half of KSH_FM_Clamp_DB was " +
                        "omitted while the lower metal half was retained."
                });
            }

            return new FabricationElementGeometry
            {
                SourceElementId = element.Id,
                SourceUniqueId = element.UniqueId,
                SourceName = GetElementDisplayName(element),
                CategoryName = element.Category?.Name,
                Geometry = retainedGeometry,
                Status =
                    "Module support geometry; retained solids " +
                    retainedGeometry.Count.ToString() +
                    "; Wood solids omitted " +
                    omittedWoodSolidCount.ToString() +
                    "; upper clamp solids omitted " +
                    omittedUpperClampSolidCount.ToString(),
                Notes =
                    "Original Fine-detail support solids retained. " +
                    "Wood-classified solids omitted by face material." +
                    (omittedUpperClampSolidCount > 0
                        ? " KSH_FM_Clamp_DB upper half omitted by " +
                          "transformed project-Z centroid."
                        : string.Empty)
            };
        }

        private static void CollectModuleSupportSolids(
            Document doc,
            GeometryElement geometry,
            Transform transform,
            bool elementContainsWoodMaterial,
            ICollection<GeometryObject> retainedGeometry,
            ref int omittedWoodSolidCount,
            ref int ambiguousSolidCount)
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
                    ModuleSupportSolidDisposition disposition =
                        ClassifyModuleSupportSolid(
                            doc,
                            solid,
                            elementContainsWoodMaterial);

                    if (disposition ==
                        ModuleSupportSolidDisposition.OmitWood)
                    {
                        omittedWoodSolidCount++;
                        continue;
                    }

                    if (disposition ==
                        ModuleSupportSolidDisposition.Ambiguous)
                    {
                        ambiguousSolidCount++;
                        continue;
                    }

                    Solid transformed = transform == null
                        ? solid
                        : SolidUtils.CreateTransformed(
                            solid,
                            transform);

                    retainedGeometry.Add(transformed);
                    continue;
                }

                GeometryInstance instance =
                    geometryObject as GeometryInstance;

                if (instance == null)
                    continue;

                Transform combined =
                    (transform ?? Transform.Identity)
                        .Multiply(instance.Transform);

                CollectModuleSupportSolids(
                    doc,
                    instance.GetSymbolGeometry(),
                    combined,
                    elementContainsWoodMaterial,
                    retainedGeometry,
                    ref omittedWoodSolidCount,
                    ref ambiguousSolidCount);
            }
        }

        private static bool TryOmitUpperKshClampHalf(
            Element element,
            IList<GeometryObject> retainedGeometry,
            out int omittedSolidCount,
            out string error)
        {
            omittedSolidCount = 0;
            error = null;

            if (!IsKshFmClampDb(element))
                return true;

            List<Solid> nonWoodSolids = retainedGeometry
                .OfType<Solid>()
                .Where(x =>
                    x != null &&
                    x.Volume > GeometryTolerance &&
                    x.Faces.Size > 0)
                .ToList();

            if (nonWoodSolids.Count != 2)
            {
                error =
                    "KSH_FM_Clamp_DB must contain exactly two resolved " +
                    "non-Wood clamp-half solids after Wood removal, but " +
                    nonWoodSolids.Count.ToString() +
                    " were found. The Module STEP was blocked instead of " +
                    "risking removal of the lower clamp half.";

                return false;
            }

            double firstElevation;
            double secondElevation;

            if (!TryGetSolidCentroidElevation(
                    nonWoodSolids[0],
                    out firstElevation) ||
                !TryGetSolidCentroidElevation(
                    nonWoodSolids[1],
                    out secondElevation))
            {
                error =
                    "The transformed project-Z centres of the two " +
                    "KSH_FM_Clamp_DB metal halves could not be resolved. " +
                    "The Module STEP was blocked instead of guessing which " +
                    "half is upper.";

                return false;
            }

            double minimumVerticalSeparation =
                1.0 / FeetToMillimetres;

            if (Math.Abs(firstElevation - secondElevation) <=
                minimumVerticalSeparation)
            {
                error =
                    "The two KSH_FM_Clamp_DB metal halves do not have a " +
                    "clear upper/lower separation in project Z. The Module " +
                    "STEP was blocked instead of guessing which half to " +
                    "remove.";

                return false;
            }

            Solid upperSolid = firstElevation > secondElevation
                ? nonWoodSolids[0]
                : nonWoodSolids[1];

            if (!retainedGeometry.Remove(upperSolid))
            {
                error =
                    "The identified upper KSH_FM_Clamp_DB solid could not " +
                    "be removed from the Module STEP geometry.";

                return false;
            }

            omittedSolidCount = 1;
            return true;
        }

        private static bool IsKshFmClampDb(Element element)
        {
            FamilyInstance familyInstance = element as FamilyInstance;
            string familyName = familyInstance?.Symbol?.FamilyName;

            return string.Equals(
                NormalizeClassificationText(familyName),
                "KSH FM CLAMP DB",
                StringComparison.Ordinal);
        }

        private static bool TryGetSolidCentroidElevation(
            Solid solid,
            out double elevation)
        {
            elevation = 0.0;

            if (solid == null)
                return false;

            try
            {
                XYZ centroid = solid.ComputeCentroid();

                if (centroid == null ||
                    double.IsNaN(centroid.Z) ||
                    double.IsInfinity(centroid.Z))
                {
                    return false;
                }

                elevation = centroid.Z;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static ModuleSupportSolidDisposition
            ClassifyModuleSupportSolid(
                Document doc,
                Solid solid,
                bool elementContainsWoodMaterial)
        {
            bool hasWoodMaterial = false;
            bool hasNonWoodMaterial = false;
            bool hasUnresolvedMaterial = false;

            foreach (Face face in solid.Faces)
            {
                ElementId materialId;

                try
                {
                    materialId = face.MaterialElementId;
                }
                catch
                {
                    hasUnresolvedMaterial = true;
                    continue;
                }

                if (materialId == null ||
                    materialId.Equals(ElementId.InvalidElementId))
                {
                    hasUnresolvedMaterial = true;
                    continue;
                }

                Material material =
                    doc.GetElement(materialId) as Material;

                if (material == null ||
                    string.IsNullOrWhiteSpace(material.Name))
                {
                    hasUnresolvedMaterial = true;
                    continue;
                }

                if (IsWoodMaterialName(material.Name))
                    hasWoodMaterial = true;
                else
                    hasNonWoodMaterial = true;
            }

            if (hasWoodMaterial && hasNonWoodMaterial)
                return ModuleSupportSolidDisposition.Ambiguous;

            if (elementContainsWoodMaterial &&
                hasUnresolvedMaterial &&
                !hasWoodMaterial)
            {
                // A mixed-material family such as KSH_FM_Clamp_DB must never
                // use solid size/order as a proxy for material identity.
                return ModuleSupportSolidDisposition.Ambiguous;
            }

            if (hasWoodMaterial)
            {
                return hasUnresolvedMaterial
                    ? ModuleSupportSolidDisposition.Ambiguous
                    : ModuleSupportSolidDisposition.OmitWood;
            }

            return ModuleSupportSolidDisposition.Include;
        }

        private static bool ElementContainsWoodMaterial(
            Document doc,
            Element element)
        {
            if (doc == null || element == null)
                return false;

            try
            {
                return element
                    .GetMaterialIds(false)
                    .Select(doc.GetElement)
                    .OfType<Material>()
                    .Any(x => IsWoodMaterialName(x.Name));
            }
            catch
            {
                // If the element-level material inventory is unavailable,
                // per-face material classification remains authoritative.
                return false;
            }
        }

        private static bool IsWoodMaterialName(string name)
        {
            string normalized =
                NormalizeClassificationText(name);

            return normalized == "WOOD" ||
                   normalized.StartsWith(
                       "WOOD ",
                       StringComparison.Ordinal) ||
                   normalized.EndsWith(
                       " WOOD",
                       StringComparison.Ordinal) ||
                   normalized.StartsWith(
                       "TIMBER ",
                       StringComparison.Ordinal) ||
                   normalized.EndsWith(
                       " TIMBER",
                       StringComparison.Ordinal) ||
                   normalized.Contains(" TIMBER ") ||
                   normalized == "TIMBER";
        }
    }
}
