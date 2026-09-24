using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal sealed class AssemblyEvidence
    {
        private AssemblyEvidence(
            long instanceId,
            long typeId,
            string typeName,
            long namingCategoryId,
            XYZ origin,
            IReadOnlyList<AssemblyMemberEvidence> members)
        {
            InstanceId = instanceId;
            TypeId = typeId;
            TypeName = typeName;
            NamingCategoryId = namingCategoryId;
            OriginX = origin.X;
            OriginY = origin.Y;
            OriginZ = origin.Z;
            Members = members;
        }

        public long InstanceId { get; }
        public long TypeId { get; }
        public string TypeName { get; }
        public long NamingCategoryId { get; }
        public double OriginX { get; }
        public double OriginY { get; }
        public double OriginZ { get; }
        public IReadOnlyList<AssemblyMemberEvidence> Members { get; }
        public IReadOnlyList<AssemblyMemberEvidence> ProductionMembers =>
            Members.Where(member => !member.IsIdentityMarker).ToList();
        public IReadOnlyList<AssemblyMemberEvidence> InternalMembers =>
            Members.Where(member => member.IsIdentityMarker).ToList();

        public static AssemblyEvidence Capture(Document document, AssemblyInstance assembly)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (assembly == null || !assembly.IsValidObject)
                throw new ArgumentException("Assembly instance is unavailable.", nameof(assembly));

            XYZ origin = assembly.GetTransform().Origin;
            var members = new List<AssemblyMemberEvidence>();
            foreach (ElementId memberId in assembly.GetMemberIds())
            {
                Element member = document.GetElement(memberId);
                if (member == null)
                {
                    throw new InvalidOperationException(
                        "Assembly evidence could not resolve member id " +
                        RevitApiCompatibility.GetElementIdValue(memberId) + ".");
                }

                members.Add(AssemblyMemberEvidence.Capture(member, origin));
            }

            return new AssemblyEvidence(
                RevitApiCompatibility.GetElementIdValue(assembly.Id),
                RevitApiCompatibility.GetElementIdValue(assembly.GetTypeId()),
                assembly.AssemblyTypeName ?? string.Empty,
                RevitApiCompatibility.GetElementIdValue(assembly.NamingCategoryId),
                origin,
                members.OrderBy(member => member.MemberId).ToList());
        }
    }

    internal sealed class AssemblyMemberEvidence
    {
        private AssemblyMemberEvidence()
        {
        }

        public long MemberId { get; private set; }
        public long OwnerAssemblyId { get; private set; }
        public long CategoryId { get; private set; }
        public string CategoryName { get; private set; }
        public long TypeId { get; private set; }
        public string ElementTypeName { get; private set; }
        public bool IsIdentityMarker { get; private set; }
        public string LocationKind { get; private set; }
        public double? RelativeX { get; private set; }
        public double? RelativeY { get; private set; }
        public double? RelativeZ { get; private set; }
        public double? RelativeEndX { get; private set; }
        public double? RelativeEndY { get; private set; }
        public double? RelativeEndZ { get; private set; }

        public static AssemblyMemberEvidence Capture(Element member, XYZ assemblyOrigin)
        {
            Category category = member.Category;
            ElementId typeId = member.GetTypeId();
            ElementType elementType = RevitApiCompatibility.IsInvalidElementId(typeId)
                ? null
                : member.Document.GetElement(typeId) as ElementType;
            var familyInstance = member as FamilyInstance;
            bool isMarker = familyInstance != null &&
                familyInstance.Symbol != null &&
                familyInstance.Symbol.Family != null &&
                string.Equals(
                    familyInstance.Symbol.Family.Name,
                    AssemblyIdentityFamilyService.FamilyName,
                    StringComparison.Ordinal);

            var evidence = new AssemblyMemberEvidence
            {
                MemberId = RevitApiCompatibility.GetElementIdValue(member.Id),
                OwnerAssemblyId = RevitApiCompatibility.GetElementIdValue(member.AssemblyInstanceId),
                CategoryId = category == null
                    ? -1L
                    : RevitApiCompatibility.GetElementIdValue(category.Id),
                CategoryName = category == null ? "<none>" : category.Name,
                TypeId = RevitApiCompatibility.GetElementIdValue(typeId),
                ElementTypeName = elementType == null ? string.Empty : elementType.Name,
                IsIdentityMarker = isMarker,
                LocationKind = member.Location == null
                    ? "None"
                    : member.Location.GetType().Name
            };

            var point = member.Location as LocationPoint;
            if (point != null)
            {
                SetRelativeStart(evidence, point.Point, assemblyOrigin);
                return evidence;
            }

            var curve = member.Location as LocationCurve;
            if (curve?.Curve != null)
            {
                SetRelativeStart(evidence, curve.Curve.GetEndPoint(0), assemblyOrigin);
                XYZ end = curve.Curve.GetEndPoint(1) - assemblyOrigin;
                evidence.RelativeEndX = end.X;
                evidence.RelativeEndY = end.Y;
                evidence.RelativeEndZ = end.Z;
                return evidence;
            }

            if (familyInstance != null)
                SetRelativeStart(evidence, familyInstance.GetTransform().Origin, assemblyOrigin);

            return evidence;
        }

        private static void SetRelativeStart(
            AssemblyMemberEvidence evidence,
            XYZ point,
            XYZ assemblyOrigin)
        {
            XYZ relative = point - assemblyOrigin;
            evidence.RelativeX = relative.X;
            evidence.RelativeY = relative.Y;
            evidence.RelativeZ = relative.Z;
        }
    }

    internal sealed class AssemblyDuplicationInvariant
    {
        public AssemblyDuplicationInvariant(string name, bool passed, string details)
        {
            Name = name;
            Passed = passed;
            Details = details;
        }

        public string Name { get; }
        public bool Passed { get; }
        public string Details { get; }
    }

    internal sealed class AssemblyTransactionStage
    {
        public string Name { get; set; }
        public TransactionStatus Status { get; set; }
    }

    internal sealed class AssemblyContaminationObservation
    {
        public string Surface { get; set; }
        public string Result { get; set; }
    }

    internal sealed class AssemblyDuplicationDiagnosticResult
    {
        public bool Succeeded { get; set; }
        public string Summary { get; set; }
        public string Details { get; set; }
        public string ReportPath { get; set; }
        public AssemblyEvidence SourceBefore { get; set; }
        public AssemblyEvidence SourceAfter { get; set; }
        public AssemblyEvidence Target500After { get; set; }
        public AssemblyEvidence Target501After { get; set; }
        public AssemblyIdentityMarkerEvidence Target500Marker { get; set; }
        public AssemblyIdentityMarkerEvidence Target501Marker { get; set; }
        public AssemblyIdentityFamilyResolution FamilyResolution { get; set; }
        public bool RenameProbePassed { get; set; }
        public string RenameProbeDetails { get; set; }
        public IList<AssemblyTransactionStage> TransactionStages { get; } =
            new List<AssemblyTransactionStage>();
        public IList<AssemblyContaminationObservation> ContaminationObservations { get; } =
            new List<AssemblyContaminationObservation>();
        public IList<AssemblyDuplicationInvariant> Invariants { get; } =
            new List<AssemblyDuplicationInvariant>();

        public AssemblyEvidence TargetAfter
        {
            get => Target500After;
            set => Target500After = value;
        }
    }
}
