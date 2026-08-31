using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static partial class FabricationStepService
    {
        // Revit API geometry and parameter access is single-threaded and can be
        // expensive when the same fitting is classified, measured, and walked
        // through several fabrication rules. Keep a cache only for the current
        // Generate call. It is cleared before the command returns and therefore
        // never survives a document change or a later export.
        [ThreadStatic]
        private static FabricationRunCache _activeFabricationRunCache;

        private static IDisposable BeginFabricationRunCache(
            Document document)
        {
            FabricationRunCache previous =
                _activeFabricationRunCache;

            _activeFabricationRunCache =
                new FabricationRunCache(document);

            return new FabricationRunCacheScope(previous);
        }

        private static bool IsFabricationRunCacheActive(
            Document document)
        {
            return
                _activeFabricationRunCache != null &&
                document != null &&
                ReferenceEquals(
                    _activeFabricationRunCache.Document,
                    document);
        }

        private static long GetCacheElementId(Element element)
        {
            return element == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(
                    element.Id);
        }

        private static Element GetElementTypeCached(
            Document document,
            Element element)
        {
            if (document == null || element == null)
                return null;

            ElementId typeId = element.GetTypeId();
            long typeKey =
                RevitApiCompatibility.GetElementIdValue(typeId);

            if (!IsFabricationRunCacheActive(document) ||
                typeKey < 0)
            {
                return document.GetElement(typeId);
            }

            Element cached;

            if (_activeFabricationRunCache.ElementTypes.TryGetValue(
                    typeKey,
                    out cached))
            {
                _activeFabricationRunCache.CacheHits++;
                return cached;
            }

            _activeFabricationRunCache.CacheMisses++;
            cached = document.GetElement(typeId);
            _activeFabricationRunCache.ElementTypes[typeKey] = cached;
            return cached;
        }

        private static bool TryGetCachedClassificationText(
            Document document,
            Element element,
            out string value)
        {
            value = null;

            if (!IsFabricationRunCacheActive(document) ||
                element == null)
            {
                return false;
            }

            long key = GetCacheElementId(element);

            if (_activeFabricationRunCache.ClassificationText.TryGetValue(
                    key,
                    out value))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheClassificationText(
            Document document,
            Element element,
            string value)
        {
            if (!IsFabricationRunCacheActive(document) ||
                element == null)
            {
                return;
            }

            _activeFabricationRunCache.ClassificationText[
                GetCacheElementId(element)] = value ?? string.Empty;
        }

        private static bool TryGetCachedClassificationResult(
            Element element,
            FabricationClassificationCacheKind kind,
            out bool value)
        {
            value = false;

            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return false;
            }

            FabricationClassificationCacheKey key =
                new FabricationClassificationCacheKey(
                    GetCacheElementId(element),
                    kind);

            if (_activeFabricationRunCache.ClassificationResults.TryGetValue(
                    key,
                    out value))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static bool CacheClassificationResult(
            Element element,
            FabricationClassificationCacheKind kind,
            bool value)
        {
            if (element != null &&
                IsFabricationRunCacheActive(element.Document))
            {
                _activeFabricationRunCache.ClassificationResults[
                    new FabricationClassificationCacheKey(
                        GetCacheElementId(element),
                        kind)] = value;
            }

            return value;
        }

        private static bool TryGetCachedElementSolids(
            Element element,
            out List<Solid> solids)
        {
            solids = null;

            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return false;
            }

            if (_activeFabricationRunCache.ElementSolids.TryGetValue(
                    GetCacheElementId(element),
                    out solids))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheElementSolids(
            Element element,
            List<Solid> solids)
        {
            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return;
            }

            long key = GetCacheElementId(element);

            if (!_activeFabricationRunCache.ElementSolids.ContainsKey(key))
            {
                const int maximumCachedSolidElements = 64;

                while (_activeFabricationRunCache.ElementSolids.Count >=
                       maximumCachedSolidElements &&
                       _activeFabricationRunCache.ElementSolidOrder.Count > 0)
                {
                    long oldest =
                        _activeFabricationRunCache.ElementSolidOrder.Dequeue();

                    _activeFabricationRunCache.ElementSolids.Remove(oldest);
                }

                _activeFabricationRunCache.ElementSolidOrder.Enqueue(key);
            }

            _activeFabricationRunCache.ElementSolids[key] =
                solids ?? new List<Solid>();
        }

        private static bool TryGetCachedElementExtent(
            Element element,
            out double extent)
        {
            extent = 0.0;

            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return false;
            }

            if (_activeFabricationRunCache.ElementExtents.TryGetValue(
                    GetCacheElementId(element),
                    out extent))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheElementExtent(
            Element element,
            double extent)
        {
            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return;
            }

            _activeFabricationRunCache.ElementExtents[
                GetCacheElementId(element)] = extent;
        }

        private static bool TryGetCachedElementCenter(
            Element element,
            out XYZ center)
        {
            center = null;

            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return false;
            }

            if (_activeFabricationRunCache.ElementCenters.TryGetValue(
                    GetCacheElementId(element),
                    out center))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheElementCenter(
            Element element,
            XYZ center)
        {
            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return;
            }

            _activeFabricationRunCache.ElementCenters[
                GetCacheElementId(element)] = center;
        }

        private static BoundingBoxXYZ GetElementBoundingBoxCached(
            Element element)
        {
            if (element == null)
                return null;

            if (!IsFabricationRunCacheActive(element.Document))
                return element.get_BoundingBox(null);

            long key = GetCacheElementId(element);
            BoundingBoxXYZ cached;

            if (_activeFabricationRunCache.ElementBoundingBoxes.TryGetValue(
                    key,
                    out cached))
            {
                _activeFabricationRunCache.CacheHits++;
                return cached;
            }

            _activeFabricationRunCache.CacheMisses++;
            cached = element.get_BoundingBox(null);
            _activeFabricationRunCache.ElementBoundingBoxes[key] = cached;
            return cached;
        }

        private static bool TryGetCachedDisplayName(
            Element element,
            out string value)
        {
            value = null;

            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return false;
            }

            if (_activeFabricationRunCache.DisplayNames.TryGetValue(
                    GetCacheElementId(element),
                    out value))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheDisplayName(
            Element element,
            string value)
        {
            if (element == null ||
                !IsFabricationRunCacheActive(element.Document))
            {
                return;
            }

            _activeFabricationRunCache.DisplayNames[
                GetCacheElementId(element)] = value ?? string.Empty;
        }

        private static bool TryGetStraightPipeAxis(
            Pipe pipe,
            out XYZ start,
            out XYZ end,
            out XYZ direction,
            out double length)
        {
            start = null;
            end = null;
            direction = null;
            length = 0.0;

            if (pipe == null)
                return false;

            long key = GetCacheElementId(pipe);
            StraightPipeAxisCacheEntry cached;

            if (IsFabricationRunCacheActive(pipe.Document) &&
                _activeFabricationRunCache.StraightPipeAxes.TryGetValue(
                    key,
                    out cached))
            {
                _activeFabricationRunCache.CacheHits++;
                start = cached.Start;
                end = cached.End;
                direction = cached.Direction;
                length = cached.Length;
                return cached.Succeeded;
            }

            if (IsFabricationRunCacheActive(pipe.Document))
                _activeFabricationRunCache.CacheMisses++;

            LocationCurve location =
                pipe.Location as LocationCurve;

            Line line =
                location?.Curve as Line;

            bool succeeded = line != null;

            if (succeeded)
            {
                start = line.GetEndPoint(0);
                end = line.GetEndPoint(1);

                XYZ vector = end - start;
                length = vector.GetLength();

                if (length <= GeometryTolerance)
                {
                    succeeded = false;
                    direction = null;
                }
                else
                {
                    direction = vector.Normalize();
                }
            }

            if (IsFabricationRunCacheActive(pipe.Document))
            {
                _activeFabricationRunCache.StraightPipeAxes[key] =
                    new StraightPipeAxisCacheEntry
                    {
                        Succeeded = succeeded,
                        Start = start,
                        End = end,
                        Direction = direction,
                        Length = length
                    };
            }

            return succeeded;
        }

        private static bool TryGetCachedPipeDimensions(
            Document document,
            Pipe pipe,
            out bool succeeded,
            out PipeDimensions dimensions,
            out string error)
        {
            succeeded = false;
            dimensions = null;
            error = null;

            if (pipe == null ||
                !IsFabricationRunCacheActive(document))
            {
                return false;
            }

            PipeDimensionCacheEntry cached;

            if (_activeFabricationRunCache.PipeDimensions.TryGetValue(
                    GetCacheElementId(pipe),
                    out cached))
            {
                _activeFabricationRunCache.CacheHits++;
                succeeded = cached.Succeeded;
                dimensions = cached.Dimensions;
                error = cached.Error;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CachePipeDimensions(
            Document document,
            Pipe pipe,
            bool succeeded,
            PipeDimensions dimensions,
            string error)
        {
            if (pipe == null ||
                !IsFabricationRunCacheActive(document))
            {
                return;
            }

            _activeFabricationRunCache.PipeDimensions[
                GetCacheElementId(pipe)] =
                new PipeDimensionCacheEntry
                {
                    Succeeded = succeeded,
                    Dimensions = dimensions,
                    Error = error
                };
        }

        private static bool TryGetCachedConnectedElement(
            Element owner,
            Connector connector,
            out Element connectedElement,
            out ConnectorLookupCacheKey key)
        {
            connectedElement = null;
            key = default(ConnectorLookupCacheKey);

            if (owner == null ||
                connector == null ||
                !IsFabricationRunCacheActive(owner.Document) ||
                !TryBuildConnectorLookupCacheKey(
                    owner,
                    connector,
                    out key))
            {
                return false;
            }

            if (_activeFabricationRunCache.ConnectedElements.TryGetValue(
                    key,
                    out connectedElement))
            {
                _activeFabricationRunCache.CacheHits++;
                return true;
            }

            _activeFabricationRunCache.CacheMisses++;
            return false;
        }

        private static void CacheConnectedElement(
            Element owner,
            ConnectorLookupCacheKey key,
            Element connectedElement)
        {
            if (owner == null ||
                !IsFabricationRunCacheActive(owner.Document) ||
                !key.IsValid)
            {
                return;
            }

            _activeFabricationRunCache.ConnectedElements[key] =
                connectedElement;
        }

        private static bool TryBuildConnectorLookupCacheKey(
            Element owner,
            Connector connector,
            out ConnectorLookupCacheKey key)
        {
            key = default(ConnectorLookupCacheKey);

            try
            {
                XYZ origin = connector.Origin;
                XYZ direction = connector.CoordinateSystem?.BasisZ;

                if (origin == null)
                    return false;

                if (direction == null ||
                    direction.GetLength() <= GeometryTolerance)
                {
                    direction = XYZ.Zero;
                }
                else
                {
                    direction = direction.Normalize();
                }

                double radius = 0.0;

                if (connector.Shape == ConnectorProfileType.Round)
                    radius = connector.Radius;

                long referenceHash = 17L;

                List<long> referenceOwnerIds =
                    new List<long>();

                foreach (Connector reference in connector.AllRefs)
                {
                    Element referenceOwner =
                        reference?.Owner;

                    if (referenceOwner == null ||
                        referenceOwner.Id.Equals(owner.Id))
                    {
                        continue;
                    }

                    referenceOwnerIds.Add(
                        GetCacheElementId(referenceOwner));
                }

                foreach (long referenceOwnerId in
                         referenceOwnerIds
                             .Distinct()
                             .OrderBy(x => x))
                {
                    unchecked
                    {
                        referenceHash =
                            (referenceHash * 397L) ^
                            referenceOwnerId;
                    }
                }

                key = new ConnectorLookupCacheKey(
                    GetCacheElementId(owner),
                    QuantizeConnectorValue(origin.X),
                    QuantizeConnectorValue(origin.Y),
                    QuantizeConnectorValue(origin.Z),
                    QuantizeConnectorValue(direction.X),
                    QuantizeConnectorValue(direction.Y),
                    QuantizeConnectorValue(direction.Z),
                    QuantizeConnectorValue(radius),
                    (int)connector.ConnectorType,
                    referenceHash);

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static long QuantizeConnectorValue(double value)
        {
            // 1e-6 ft is approximately 0.0003 mm. It is fine enough to keep
            // distinct physical connectors separate while remaining stable
            // across repeated Revit connector wrappers in one command.
            return checked((long)Math.Round(value * 1000000.0));
        }

        private enum FabricationClassificationCacheKind
        {
            Flange,
            BlindFlange,
            ShapedBranch,
            SideCoupling,
            CopperTube,
            CopperCapillaryReducer,
            ConcentricReducer,
            Reducer,
            IgnoredConnection
        }

        private struct FabricationClassificationCacheKey :
            IEquatable<FabricationClassificationCacheKey>
        {
            public FabricationClassificationCacheKey(
                long elementId,
                FabricationClassificationCacheKind kind)
            {
                ElementId = elementId;
                Kind = kind;
            }

            private long ElementId { get; }
            private FabricationClassificationCacheKind Kind { get; }

            public bool Equals(
                FabricationClassificationCacheKey other)
            {
                return
                    ElementId == other.ElementId &&
                    Kind == other.Kind;
            }

            public override bool Equals(object obj)
            {
                return
                    obj is FabricationClassificationCacheKey &&
                    Equals((FabricationClassificationCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return
                        (ElementId.GetHashCode() * 397) ^
                        (int)Kind;
                }
            }
        }

        private struct ConnectorLookupCacheKey :
            IEquatable<ConnectorLookupCacheKey>
        {
            public ConnectorLookupCacheKey(
                long ownerId,
                long originX,
                long originY,
                long originZ,
                long directionX,
                long directionY,
                long directionZ,
                long radius,
                int connectorType,
                long referenceHash)
            {
                OwnerId = ownerId;
                OriginX = originX;
                OriginY = originY;
                OriginZ = originZ;
                DirectionX = directionX;
                DirectionY = directionY;
                DirectionZ = directionZ;
                Radius = radius;
                ConnectorType = connectorType;
                ReferenceHash = referenceHash;
            }

            private long OwnerId { get; }
            private long OriginX { get; }
            private long OriginY { get; }
            private long OriginZ { get; }
            private long DirectionX { get; }
            private long DirectionY { get; }
            private long DirectionZ { get; }
            private long Radius { get; }
            private int ConnectorType { get; }
            private long ReferenceHash { get; }

            public bool IsValid
            {
                get { return OwnerId > 0; }
            }

            public bool Equals(ConnectorLookupCacheKey other)
            {
                return
                    OwnerId == other.OwnerId &&
                    OriginX == other.OriginX &&
                    OriginY == other.OriginY &&
                    OriginZ == other.OriginZ &&
                    DirectionX == other.DirectionX &&
                    DirectionY == other.DirectionY &&
                    DirectionZ == other.DirectionZ &&
                    Radius == other.Radius &&
                    ConnectorType == other.ConnectorType &&
                    ReferenceHash == other.ReferenceHash;
            }

            public override bool Equals(object obj)
            {
                return
                    obj is ConnectorLookupCacheKey &&
                    Equals((ConnectorLookupCacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = OwnerId.GetHashCode();
                    hashCode = (hashCode * 397) ^ OriginX.GetHashCode();
                    hashCode = (hashCode * 397) ^ OriginY.GetHashCode();
                    hashCode = (hashCode * 397) ^ OriginZ.GetHashCode();
                    hashCode = (hashCode * 397) ^ DirectionX.GetHashCode();
                    hashCode = (hashCode * 397) ^ DirectionY.GetHashCode();
                    hashCode = (hashCode * 397) ^ DirectionZ.GetHashCode();
                    hashCode = (hashCode * 397) ^ Radius.GetHashCode();
                    hashCode = (hashCode * 397) ^ ConnectorType;
                    hashCode = (hashCode * 397) ^
                        ReferenceHash.GetHashCode();
                    return hashCode;
                }
            }
        }

        private sealed class StraightPipeAxisCacheEntry
        {
            public bool Succeeded { get; set; }
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
            public XYZ Direction { get; set; }
            public double Length { get; set; }
        }

        private sealed class PipeDimensionCacheEntry
        {
            public bool Succeeded { get; set; }
            public PipeDimensions Dimensions { get; set; }
            public string Error { get; set; }
        }

        private sealed class FabricationRunCache
        {
            public FabricationRunCache(Document document)
            {
                Document = document;
            }

            public Document Document { get; }

            public Dictionary<long, Element> ElementTypes { get; } =
                new Dictionary<long, Element>();

            public Dictionary<long, string> ClassificationText { get; } =
                new Dictionary<long, string>();

            public Dictionary<FabricationClassificationCacheKey, bool>
                ClassificationResults { get; } =
                    new Dictionary<
                        FabricationClassificationCacheKey,
                        bool>();

            public Dictionary<long, List<Solid>> ElementSolids { get; } =
                new Dictionary<long, List<Solid>>();

            public Queue<long> ElementSolidOrder { get; } =
                new Queue<long>();

            public Dictionary<long, double> ElementExtents { get; } =
                new Dictionary<long, double>();

            public Dictionary<long, XYZ> ElementCenters { get; } =
                new Dictionary<long, XYZ>();

            public Dictionary<long, BoundingBoxXYZ>
                ElementBoundingBoxes { get; } =
                    new Dictionary<long, BoundingBoxXYZ>();

            public Dictionary<long, string> DisplayNames { get; } =
                new Dictionary<long, string>();

            public Dictionary<long, PipeDimensionCacheEntry>
                PipeDimensions { get; } =
                    new Dictionary<long, PipeDimensionCacheEntry>();

            public Dictionary<long, StraightPipeAxisCacheEntry>
                StraightPipeAxes { get; } =
                    new Dictionary<long, StraightPipeAxisCacheEntry>();

            public Dictionary<ConnectorLookupCacheKey, Element>
                ConnectedElements { get; } =
                    new Dictionary<ConnectorLookupCacheKey, Element>();

            public int CacheHits { get; set; }
            public int CacheMisses { get; set; }
        }

        private sealed class FabricationRunCacheScope : IDisposable
        {
            private readonly FabricationRunCache _previous;
            private bool _disposed;

            public FabricationRunCacheScope(
                FabricationRunCache previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;

                FabricationRunCache completed =
                    _activeFabricationRunCache;

                _activeFabricationRunCache = _previous;

                if (completed != null)
                {
                    Debug.WriteLine(
                        "Fabrication STEP run cache: " +
                        completed.CacheHits +
                        " hits, " +
                        completed.CacheMisses +
                        " misses.");
                }
            }
        }
    }
}
