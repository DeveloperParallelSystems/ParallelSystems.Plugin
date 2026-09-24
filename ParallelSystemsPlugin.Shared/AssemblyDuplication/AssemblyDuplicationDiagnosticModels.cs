using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: immutable evidence used to prove source/target independence.
    internal sealed class AssemblyEvidence
    {
        private AssemblyEvidence(
            long instanceId,
            long typeId,
            string typeName,
            IReadOnlyList<AssemblyMemberEvidence> members)
        {
            InstanceId = instanceId;
            TypeId = typeId;
            TypeName = typeName;
            Members = members;
        }

        public long InstanceId { get; }

        public long TypeId { get; }

        public string TypeName { get; }

        public IReadOnlyList<AssemblyMemberEvidence> Members { get; }

        public static AssemblyEvidence Capture(
            Document document,
            AssemblyInstance assembly)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (assembly == null || !assembly.IsValidObject)
                throw new ArgumentException("Assembly instance is unavailable.", nameof(assembly));

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

                members.Add(new AssemblyMemberEvidence(
                    RevitApiCompatibility.GetElementIdValue(member.Id),
                    RevitApiCompatibility.GetElementIdValue(member.AssemblyInstanceId)));
            }

            members = members.OrderBy(member => member.MemberId).ToList();

            return new AssemblyEvidence(
                RevitApiCompatibility.GetElementIdValue(assembly.Id),
                RevitApiCompatibility.GetElementIdValue(assembly.GetTypeId()),
                assembly.AssemblyTypeName ?? string.Empty,
                members);
        }
    }

    internal sealed class AssemblyMemberEvidence
    {
        public AssemblyMemberEvidence(long memberId, long ownerAssemblyId)
        {
            MemberId = memberId;
            OwnerAssemblyId = ownerAssemblyId;
        }

        public long MemberId { get; }

        public long OwnerAssemblyId { get; }
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

    internal sealed class AssemblyDuplicationDiagnosticResult
    {
        public bool Succeeded { get; set; }

        public string Summary { get; set; }

        public string Details { get; set; }

        public string ReportPath { get; set; }

        public AssemblyEvidence SourceBefore { get; set; }

        public AssemblyEvidence SourceAfter { get; set; }

        public AssemblyEvidence TargetAfter { get; set; }

        public IList<AssemblyDuplicationInvariant> Invariants { get; } =
            new List<AssemblyDuplicationInvariant>();
    }
}
