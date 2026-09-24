using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

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
        public string PlacementFingerprint { get; private set; }
        public string ParameterFingerprint { get; private set; }

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
                    StringComparison.Ordinal) &&
                familyInstance.Symbol.Name.StartsWith("PS_ASM_ID_", StringComparison.Ordinal);

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
                    : member.Location.GetType().Name,
                ParameterFingerprint = CaptureParameters(member)
            };

            var point = member.Location as LocationPoint;
            if (point != null)
            {
                SetRelativeStart(evidence, point.Point, assemblyOrigin);
                evidence.PlacementFingerprint = CapturePlacement(member, assemblyOrigin);
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
                evidence.PlacementFingerprint = CapturePlacement(member, assemblyOrigin);
                return evidence;
            }

            if (familyInstance != null)
                SetRelativeStart(evidence, familyInstance.GetTransform().Origin, assemblyOrigin);

            evidence.PlacementFingerprint = CapturePlacement(member, assemblyOrigin);
            return evidence;
        }

        private static string CapturePlacement(Element member, XYZ assemblyOrigin)
        {
            var text = new StringBuilder();
            var familyInstance = member as FamilyInstance;
            if (familyInstance != null)
            {
                Transform transform = familyInstance.GetTransform();
                AppendVector(text, "O", transform.Origin - assemblyOrigin);
                AppendVector(text, "X", transform.BasisX);
                AppendVector(text, "Y", transform.BasisY);
                AppendVector(text, "Z", transform.BasisZ);
                text.Append("M=").Append(familyInstance.Mirrored).Append(';');
            }

            var curve = member.Location as LocationCurve;
            if (curve?.Curve != null)
            {
                int index = 0;
                foreach (XYZ point in curve.Curve.Tessellate())
                    AppendVector(text, "C" + index++, point - assemblyOrigin);
            }

            BoundingBoxXYZ bounds = member.get_BoundingBox(null);
            if (bounds != null)
            {
                AppendVector(text, "B0", bounds.Min - assemblyOrigin);
                AppendVector(text, "B1", bounds.Max - assemblyOrigin);
            }

            return text.ToString();
        }

        private static string CaptureParameters(Element member)
        {
            long assemblyNameId = (long)BuiltInParameter.ASSEMBLY_NAME;
            long elementIdParameter = (long)BuiltInParameter.ID_PARAM;
            var values = new List<string>();
            foreach (Parameter parameter in member.Parameters.Cast<Parameter>())
            {
                long parameterId = RevitApiCompatibility.GetElementIdValue(parameter.Id);
                if (parameterId == assemblyNameId || parameterId == elementIdParameter)
                    continue;

                string value;
                switch (parameter.StorageType)
                {
                    case StorageType.Double:
                        value = Format(parameter.AsDouble());
                        break;
                    case StorageType.Integer:
                        value = parameter.AsInteger().ToString(CultureInfo.InvariantCulture);
                        break;
                    case StorageType.String:
                        value = parameter.AsString() ?? string.Empty;
                        break;
                    case StorageType.ElementId:
                        value = NormalizeElementReference(member.Document, parameter.AsElementId());
                        break;
                    default:
                        continue;
                }

                values.Add(parameterId.ToString(CultureInfo.InvariantCulture) + "=" + value);
            }

            values.Sort(StringComparer.Ordinal);
            return string.Join("|", values);
        }

        private static string NormalizeElementReference(Document document, ElementId id)
        {
            long value = RevitApiCompatibility.GetElementIdValue(id);
            if (value <= 0)
                return value.ToString(CultureInfo.InvariantCulture);

            Element referenced = document.GetElement(id);
            if (referenced == null)
                return "missing:" + value.ToString(CultureInfo.InvariantCulture);

            long categoryId = referenced.Category == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(referenced.Category.Id);
            long typeId = RevitApiCompatibility.GetElementIdValue(referenced.GetTypeId());
            return referenced.GetType().FullName + ":" + categoryId + ":" + typeId;
        }

        private static void AppendVector(StringBuilder text, string label, XYZ vector)
        {
            text.Append(label).Append('=')
                .Append(Format(vector.X)).Append(',')
                .Append(Format(vector.Y)).Append(',')
                .Append(Format(vector.Z)).Append(';');
        }

        private static string Format(double value)
        {
            return Math.Round(value, 6).ToString("G17", CultureInfo.InvariantCulture);
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

    }
}
