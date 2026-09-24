using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: isolated Phase 2 proof of independent assembly identity.
    internal static class AssemblyDuplicationDiagnosticService
    {
        public static AssemblyDuplicationDiagnosticResult Run(
            Document document,
            AssemblyInstance source,
            string targetName)
        {
            return CopyCreateAndVerify(document, source, targetName);
        }

        private static AssemblyDuplicationDiagnosticResult CopyCreateAndVerify(
            Document document,
            AssemblyInstance source,
            string targetName)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (source == null || !source.IsValidObject)
                throw new ArgumentException("Source assembly is unavailable.", nameof(source));
            if (string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("Target assembly name is required.", nameof(targetName));

            bool nameExists = new FilteredElementCollector(document)
                .OfClass(typeof(AssemblyInstance))
                .Cast<AssemblyInstance>()
                .Any(assembly => string.Equals(
                    assembly.AssemblyTypeName,
                    targetName,
                    StringComparison.OrdinalIgnoreCase));

            if (nameExists)
                throw new InvalidOperationException(
                    $"Assembly name '{targetName}' already exists.");

            var result = new AssemblyDuplicationDiagnosticResult
            {
                SourceBefore = AssemblyEvidence.Capture(document, source)
            };

            Exception failure = null;
            using (var group = new TransactionGroup(
                       document,
                       "Assembly Independence Diagnostic"))
            {
                group.Start();
                try
                {
                    List<ElementId> sourceMemberIds = source.GetMemberIds().ToList();
                    if (sourceMemberIds.Count == 0)
                        throw new InvalidOperationException("The source assembly has no members.");

                    ICollection<ElementId> copiedIds;
                    using (var copyTransaction = new Transaction(
                               document,
                               "Copy Assembly Members for Diagnostic"))
                    {
                        copyTransaction.Start();
                        using (var options = new CopyPasteOptions())
                        {
                            copiedIds = ElementTransformUtils.CopyElements(
                                document,
                                sourceMemberIds,
                                document,
                                Transform.Identity,
                                options);
                        }

                        document.Regenerate();
                        copyTransaction.Commit();
                    }

                    List<ElementId> copiedMemberIds = copiedIds?.ToList() ??
                                                      new List<ElementId>();
                    if (copiedMemberIds.Count == 0)
                        throw new InvalidOperationException("Revit returned no copied member elements.");
                    if (copiedMemberIds.Count != sourceMemberIds.Count)
                    {
                        throw new InvalidOperationException(
                            "The copy returned a different number of elements than the source " +
                            "assembly. The diagnostic will not guess which dependency elements " +
                            "belong in the new assembly.");
                    }

                    foreach (ElementId copiedId in copiedMemberIds)
                    {
                        Element copiedElement = document.GetElement(copiedId);
                        if (copiedElement == null)
                            throw new InvalidOperationException("A copied element no longer exists.");
                        if (!RevitApiCompatibility.IsInvalidElementId(
                                copiedElement.AssemblyInstanceId))
                        {
                            throw new InvalidOperationException(
                                "A copied element already belongs to an assembly. " +
                                "The diagnostic cannot safely create a new independent assembly.");
                        }
                    }

                    if (!AssemblyInstance.AreElementsValidForAssembly(
                            document,
                            copiedMemberIds,
                            ElementId.InvalidElementId))
                    {
                        throw new InvalidOperationException(
                            "The copied elements are not valid members for a new assembly.");
                    }

                    List<long> expectedCopiedMemberIds = copiedMemberIds
                        .Select(RevitApiCompatibility.GetElementIdValue)
                        .ToList();

                    AssemblyInstance target;
                    using (var createTransaction = new Transaction(
                               document,
                               "Create Independent Assembly Diagnostic"))
                    {
                        createTransaction.Start();
                        target = AssemblyInstance.Create(
                            document,
                            copiedMemberIds,
                            source.NamingCategoryId);
                        document.Regenerate();
                        createTransaction.Commit();
                    }

                    if (target == null || !target.IsValidObject)
                        throw new InvalidOperationException("Revit did not create the target assembly.");

                    long sourceTypeId = RevitApiCompatibility.GetElementIdValue(
                        source.GetTypeId());
                    long targetTypeId = RevitApiCompatibility.GetElementIdValue(
                        target.GetTypeId());

                    // Changed by Jhay: never rename while Revit still shares the source type.
                    if (sourceTypeId == targetTypeId)
                    {
                        throw new InvalidOperationException(
                            "Revit assigned the source AssemblyType to the new assembly. " +
                            "The operation was rolled back before either type was renamed.");
                    }

                    using (var renameTransaction = new Transaction(
                               document,
                               "Rename Independent Assembly Diagnostic"))
                    {
                        renameTransaction.Start();
                        target.AssemblyTypeName = targetName;
                        document.Regenerate();
                        renameTransaction.Commit();
                    }

                    result.SourceAfter = AssemblyEvidence.Capture(document, source);
                    result.TargetAfter = AssemblyEvidence.Capture(document, target);
                    AddChecks(result, ValidateIndependence(
                        result.SourceBefore,
                        result.SourceAfter,
                        result.TargetAfter,
                        expectedCopiedMemberIds,
                        targetName));

                    using (var verificationTransaction = new Transaction(
                               document,
                               "Regenerate Assembly Diagnostic"))
                    {
                        verificationTransaction.Start();
                        document.Regenerate();
                        verificationTransaction.Commit();
                    }

                    AssemblyEvidence sourceAfterRegeneration =
                        AssemblyEvidence.Capture(document, source);
                    AssemblyEvidence targetAfterRegeneration =
                        AssemblyEvidence.Capture(document, target);

                    AddChecks(result, ValidateIndependence(
                        result.SourceBefore,
                        sourceAfterRegeneration,
                        targetAfterRegeneration,
                        expectedCopiedMemberIds,
                        targetName));

                    AssemblyDuplicationInvariant failed = result.Invariants
                        .FirstOrDefault(check => !check.Passed);
                    if (failed != null)
                    {
                        throw new InvalidOperationException(
                            "Independence validation failed: " + failed.Name + ". " + failed.Details);
                    }

                    result.SourceAfter = sourceAfterRegeneration;
                    result.TargetAfter = targetAfterRegeneration;
                    result.Succeeded = true;
                    result.Summary = "The target assembly was created with an independent type.";
                    result.Details = "All source-protection and target-ownership checks passed twice.";
                    group.Assimilate();
                }
                catch (Exception ex)
                {
                    failure = ex;
                    result.Succeeded = false;
                    result.Summary = "The diagnostic was rolled back.";
                    result.Details = ex.Message;

                    if (group.GetStatus() == TransactionStatus.Started)
                        group.RollBack();
                }
            }

            try
            {
                result.ReportPath = AssemblyDuplicationDiagnosticReport.Write(
                    document,
                    result,
                    failure);
            }
            catch (Exception reportException)
            {
                result.ReportPath = string.Empty;
                result.Details += Environment.NewLine +
                                  "The diagnostic report could not be written: " +
                                  reportException.Message;
            }

            return result;
        }

        private static void AddChecks(
            AssemblyDuplicationDiagnosticResult result,
            IEnumerable<AssemblyDuplicationInvariant> checks)
        {
            foreach (AssemblyDuplicationInvariant check in checks)
                result.Invariants.Add(check);
        }

        private static IList<AssemblyDuplicationInvariant> ValidateIndependence(
            AssemblyEvidence sourceBefore,
            AssemblyEvidence sourceAfter,
            AssemblyEvidence targetAfter,
            IReadOnlyCollection<long> expectedCopiedMemberIds,
            string targetName)
        {
            var checks = new List<AssemblyDuplicationInvariant>();
            Add(checks, "Source exists", sourceAfter != null, "Source evidence was captured after duplication.");
            Add(checks, "Target exists", targetAfter != null, "Target evidence was captured after duplication.");

            if (sourceAfter == null || targetAfter == null)
                return checks;

            Add(checks, "Source instance unchanged", sourceBefore.InstanceId == sourceAfter.InstanceId, "Source instance id remains unchanged.");
            Add(checks, "Source type unchanged", sourceBefore.TypeId == sourceAfter.TypeId, "Source type id remains unchanged.");
            Add(checks, "Source name unchanged", string.Equals(sourceBefore.TypeName, sourceAfter.TypeName, StringComparison.Ordinal), "Source type name remains unchanged.");
            Add(checks, "Source members unchanged", MembersEqual(sourceBefore, sourceAfter), "Source member ids and owners remain unchanged.");
            Add(checks, "Independent instance", sourceBefore.InstanceId != targetAfter.InstanceId, "Target instance id differs from source.");
            Add(checks, "Independent type", sourceBefore.TypeId != targetAfter.TypeId, "Target type id differs from source.");
            Add(checks, "Target name", string.Equals(targetName, targetAfter.TypeName, StringComparison.Ordinal), "Target type name matches the requested diagnostic name.");
            Add(checks, "Member count", sourceBefore.Members.Count == targetAfter.Members.Count, "Target member count matches source.");
            Add(
                checks,
                "Exact copied membership",
                AssemblyMemberSetComparer.Matches(
                    expectedCopiedMemberIds,
                    targetAfter.Members.Select(x => x.MemberId).ToList()),
                "The target member ids exactly match every copied element id.");
            Add(checks, "Target member ownership", targetAfter.Members.All(x => x.OwnerAssemblyId == targetAfter.InstanceId), "Every target member belongs to the target assembly.");
            return checks;
        }

        private static bool MembersEqual(AssemblyEvidence left, AssemblyEvidence right)
        {
            return left.Members.Select(x => x.MemberId + ":" + x.OwnerAssemblyId)
                .SequenceEqual(right.Members.Select(x => x.MemberId + ":" + x.OwnerAssemblyId));
        }

        private static void Add(
            ICollection<AssemblyDuplicationInvariant> checks,
            string name,
            bool passed,
            string details)
        {
            checks.Add(new AssemblyDuplicationInvariant(name, passed, details));
        }
    }
}
