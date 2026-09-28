using Autodesk.Revit.DB;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: read-only preflight evidence for the identity-family contract.
    internal sealed class AssemblyIdentityFamilyAvailability
    {
        public AssemblyIdentityFamilyAvailability(
            string assetPath,
            bool familyExists,
            long? familyId,
            long? baseSymbolId,
            long? categoryId,
            string categoryName,
            string placementType,
            bool isAvailable,
            string issue)
        {
            AssetPath = assetPath;
            FamilyExists = familyExists;
            FamilyId = familyId;
            BaseSymbolId = baseSymbolId;
            CategoryId = categoryId;
            CategoryName = categoryName;
            PlacementType = placementType;
            IsAvailable = isAvailable;
            Issue = issue;
        }

        public string AssetPath { get; }
        public bool FamilyExists { get; }
        public long? FamilyId { get; }
        public long? BaseSymbolId { get; }
        public long? CategoryId { get; }
        public string CategoryName { get; }
        public string PlacementType { get; }
        public bool IsAvailable { get; }
        public string Issue { get; }
    }

    internal sealed class AssemblyIdentityFamilyResolution
    {
        public Family Family { get; set; }
        public FamilySymbol BaseSymbol { get; set; }
        public string AssetPath { get; set; }
        public bool WasLoaded { get; set; }
        public long FamilyId { get; set; }
        public long CategoryId { get; set; }
        public string CategoryName { get; set; }
        public string PlacementType { get; set; }
        public TransactionStatus? LoadTransactionStatus { get; set; }
    }

    internal sealed class AssemblyIdentityMarkerPlacement
    {
        public FamilyInstance Marker { get; set; }
        public FamilySymbol Symbol { get; set; }
        public AssemblyIdentityMarkerEvidence Evidence { get; set; }
    }

    internal sealed class AssemblyIdentityMarkerEvidence
    {
        public long MarkerId { get; set; }
        public long SymbolId { get; set; }
        public string SymbolName { get; set; }
        public long LevelId { get; set; }
        public string LevelName { get; set; }
        public double LevelElevation { get; set; }
        public double Offset { get; set; }
        public double RequestedX { get; set; }
        public double RequestedY { get; set; }
        public double RequestedZ { get; set; }
        public double ResultX { get; set; }
        public double ResultY { get; set; }
        public double ResultZ { get; set; }
    }
}
