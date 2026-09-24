using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal static class AssemblyIdentityFamilyService
    {
        internal const string FamilyName = "PS_AssemblyIdentity";
        internal const string BaseSymbolName = "PS_ASSEMBLY_IDENTITY_BASE";
        internal const string RelativeAssetPath = @"Families\PS_AssemblyIdentity.rfa";
        private const double PlacementTolerance = 1e-6;

        public static AssemblyIdentityFamilyResolution ResolveOrLoad(
            Document document,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
                throw new ArgumentException("The add-in assembly directory is required.", nameof(assemblyDirectory));

            string assetPath = Path.Combine(assemblyDirectory, RelativeAssetPath);
            Family family = FindFamily(document);
            bool wasLoaded = false;
            TransactionStatus? loadStatus = null;

            if (family == null)
            {
                if (!File.Exists(assetPath))
                {
                    throw new FileNotFoundException(
                        "The deployed assembly identity family is missing.",
                        assetPath);
                }

                using (var transaction = new Transaction(document, "Load Assembly Identity Family"))
                {
                    TransactionStatus startStatus = transaction.Start();
                    if (startStatus != TransactionStatus.Started)
                        throw new InvalidOperationException("Identity family load transaction did not start: " + startStatus + ".");

                    Family loadedFamily;
                    if (!document.LoadFamily(assetPath, out loadedFamily) || loadedFamily == null)
                        throw new InvalidOperationException("Revit did not load the assembly identity family.");

                    document.Regenerate();
                    loadStatus = transaction.Commit();
                    if (loadStatus != TransactionStatus.Committed)
                        throw new InvalidOperationException("Identity family load transaction did not commit: " + loadStatus + ".");
                }

                wasLoaded = true;
                family = FindFamily(document);
            }

            if (family == null)
                throw new InvalidOperationException("The assembly identity family is unavailable after loading.");

            Category category = family.FamilyCategory;
            long expectedCategoryId = RevitApiCompatibility.GetElementIdValue(
                RevitApiCompatibility.CreateElementId((long)BuiltInCategory.OST_GenericModel));
            long actualCategoryId = category == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(category.Id);
            if (actualCategoryId != expectedCategoryId)
            {
                throw new InvalidOperationException(
                    "A family named '" + FamilyName + "' already exists but is not a Generic Model. " +
                    "User content will not be overwritten.");
            }

            FamilySymbol baseSymbol = family.GetFamilySymbolIds()
                .Select(id => document.GetElement(id) as FamilySymbol)
                .FirstOrDefault(symbol => symbol != null &&
                    string.Equals(symbol.Name, BaseSymbolName, StringComparison.Ordinal));
            if (baseSymbol == null)
            {
                throw new InvalidOperationException(
                    "A family named '" + FamilyName + "' already exists without the required '" +
                    BaseSymbolName + "' type. User content will not be overwritten.");
            }

            if (family.FamilyPlacementType != FamilyPlacementType.OneLevelBased)
            {
                throw new InvalidOperationException(
                    "The assembly identity family placement type is unsupported: " +
                    family.FamilyPlacementType + ".");
            }

            return new AssemblyIdentityFamilyResolution
            {
                Family = family,
                BaseSymbol = baseSymbol,
                AssetPath = assetPath,
                WasLoaded = wasLoaded,
                FamilyId = RevitApiCompatibility.GetElementIdValue(family.Id),
                CategoryId = actualCategoryId,
                CategoryName = category.Name,
                PlacementType = family.FamilyPlacementType.ToString(),
                LoadTransactionStatus = loadStatus
            };
        }

        public static AssemblyIdentityMarkerPlacement CreateMarker(
            Document document,
            FamilySymbol baseSymbol,
            string targetName,
            AssemblyInstance source,
            IReadOnlyCollection<ElementId> sourceMemberIds)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (!document.IsModifiable)
                throw new InvalidOperationException("Marker creation requires an open transaction.");
            if (baseSymbol == null || !baseSymbol.IsValidObject)
                throw new ArgumentException("The base identity symbol is unavailable.", nameof(baseSymbol));
            if (source == null || !source.IsValidObject)
                throw new ArgumentException("The source assembly is unavailable.", nameof(source));
            if (sourceMemberIds == null)
                throw new ArgumentNullException(nameof(sourceMemberIds));

            string symbolName = CreateUnusedSymbolName(document, targetName);
            FamilySymbol symbol = baseSymbol.Duplicate(symbolName) as FamilySymbol;
            if (symbol == null)
                throw new InvalidOperationException("Identity symbol duplication failed.");

            if (!symbol.IsActive)
                symbol.Activate();
            document.Regenerate();

            XYZ origin = source.GetTransform().Origin;
            Level level = SelectLevel(document, source, sourceMemberIds, origin);
            XYZ levelPoint = new XYZ(origin.X, origin.Y, level.Elevation);
            FamilyInstance marker = document.Create.NewFamilyInstance(
                levelPoint,
                symbol,
                level,
                StructuralType.NonStructural);
            if (marker == null)
                throw new InvalidOperationException("Revit did not create the assembly identity marker.");

            double requestedOffset = origin.Z - level.Elevation;
            Parameter offset = marker.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            if (offset != null && !offset.IsReadOnly)
                offset.Set(requestedOffset);

            document.Regenerate();
            var location = marker.Location as LocationPoint;
            if (location == null)
                throw new InvalidOperationException("The identity marker does not have a point location.");

            XYZ point = location.Point;
            if (!PointsMatch(origin, point))
            {
                throw new InvalidOperationException(
                    "The identity marker was not placed at the source assembly origin. " +
                    "Requested " + FormatPoint(origin) + ", actual " + FormatPoint(point) + ".");
            }

            double appliedOffset = offset == null ? requestedOffset : offset.AsDouble();
            return new AssemblyIdentityMarkerPlacement
            {
                Marker = marker,
                Symbol = symbol,
                Evidence = new AssemblyIdentityMarkerEvidence
                {
                    MarkerId = RevitApiCompatibility.GetElementIdValue(marker.Id),
                    SymbolId = RevitApiCompatibility.GetElementIdValue(symbol.Id),
                    SymbolName = symbol.Name,
                    LevelId = RevitApiCompatibility.GetElementIdValue(level.Id),
                    LevelName = level.Name,
                    LevelElevation = level.Elevation,
                    Offset = appliedOffset,
                    RequestedX = origin.X,
                    RequestedY = origin.Y,
                    RequestedZ = origin.Z,
                    ResultX = point.X,
                    ResultY = point.Y,
                    ResultZ = point.Z
                }
            };
        }

        private static Family FindFamily(Document document)
        {
            return new FilteredElementCollector(document)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .FirstOrDefault(item => string.Equals(item.Name, FamilyName, StringComparison.Ordinal));
        }

        private static string CreateUnusedSymbolName(Document document, string targetName)
        {
            var existingNames = new HashSet<string>(
                new FilteredElementCollector(document)
                    .WhereElementIsElementType()
                    .Select(element => element.Name),
                StringComparer.OrdinalIgnoreCase);

            for (int attempt = 0; attempt < 20; attempt++)
            {
                string candidate = AssemblyIdentityMarkerName.Create(targetName, Guid.NewGuid());
                if (!existingNames.Contains(candidate))
                    return candidate;
            }

            throw new InvalidOperationException("A unique identity marker type name could not be generated.");
        }

        private static Level SelectLevel(
            Document document,
            AssemblyInstance source,
            IEnumerable<ElementId> sourceMemberIds,
            XYZ origin)
        {
            Level sourceLevel = ResolveLevel(document, source.LevelId);
            if (sourceLevel != null)
                return sourceLevel;

            var memberLevels = sourceMemberIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .Select(element => ResolveLevel(document, element.LevelId))
                .Where(level => level != null)
                .GroupBy(level => RevitApiCompatibility.GetElementIdValue(level.Id))
                .Select(group => new { Level = group.First(), Count = group.Count() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => Math.Abs(item.Level.Elevation - origin.Z))
                .ThenBy(item => RevitApiCompatibility.GetElementIdValue(item.Level.Id))
                .Select(item => item.Level)
                .FirstOrDefault();
            if (memberLevels != null)
                return memberLevels;

            Level closest = new FilteredElementCollector(document)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(level => Math.Abs(level.Elevation - origin.Z))
                .ThenBy(level => RevitApiCompatibility.GetElementIdValue(level.Id))
                .FirstOrDefault();
            if (closest == null)
                throw new InvalidOperationException("The document has no level available for identity marker placement.");

            return closest;
        }

        private static Level ResolveLevel(Document document, ElementId levelId)
        {
            if (RevitApiCompatibility.IsInvalidElementId(levelId))
                return null;
            return document.GetElement(levelId) as Level;
        }

        private static bool PointsMatch(XYZ left, XYZ right)
        {
            return Math.Abs(left.X - right.X) <= PlacementTolerance &&
                   Math.Abs(left.Y - right.Y) <= PlacementTolerance &&
                   Math.Abs(left.Z - right.Z) <= PlacementTolerance;
        }

        private static string FormatPoint(XYZ point)
        {
            return "(" + point.X + ", " + point.Y + ", " + point.Z + ")";
        }
    }
}
