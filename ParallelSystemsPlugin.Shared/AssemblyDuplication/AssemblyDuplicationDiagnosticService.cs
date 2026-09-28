using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal static class AssemblyDuplicationDiagnosticService
    {
        public static AssemblyDuplicationDiagnosticResult Run(
            Document document,
            AssemblyInstance source,
            string target500Name,
            string target501Name,
            string assemblyDirectory)
        {
            AssemblyDuplicationEngine.ValidateArguments(
                document,
                source,
                target500Name,
                target501Name,
                assemblyDirectory);
            AssemblyDuplicationEngine.RejectNameConflicts(document, target500Name, target501Name);

            var result = new AssemblyDuplicationDiagnosticResult
            {
                SourceBefore = AssemblyEvidence.Capture(document, source)
            };

            Exception failure = null;
            using (var group = new TransactionGroup(document, "Assembly Identity Marker POC"))
            {
                TransactionStatus groupStart = group.Start();
                result.TransactionStages.Add(new AssemblyTransactionStage
                {
                    Name = "Transaction group start",
                    Status = groupStart
                });

                if (groupStart != TransactionStatus.Started)
                    throw new InvalidOperationException("Assembly identity transaction group did not start: " + groupStart + ".");

                try
                {
                    result.FamilyResolution = AssemblyIdentityFamilyService.ResolveOrLoad(
                        document,
                        assemblyDirectory);
                    if (result.FamilyResolution.LoadTransactionStatus.HasValue)
                    {
                        result.TransactionStages.Add(new AssemblyTransactionStage
                        {
                            Name = "Load Assembly Identity Family",
                            Status = result.FamilyResolution.LoadTransactionStatus.Value
                        });
                    }

                    List<ElementId> sourceProductionIds = source.GetMemberIds().ToList();
                    if (sourceProductionIds.Count == 0)
                        throw new InvalidOperationException("The source assembly has no production members.");

                    AssemblyDuplicationEngine.CreatedAssemblyTarget target500 =
                        AssemblyDuplicationEngine.CreateTarget(
                        document,
                        source,
                        sourceProductionIds,
                        result.FamilyResolution.BaseSymbol,
                        target500Name,
                        "Target 500",
                        result);
                    AssemblyDuplicationEngine.CreatedAssemblyTarget target501 =
                        AssemblyDuplicationEngine.CreateTarget(
                        document,
                        source,
                        sourceProductionIds,
                        result.FamilyResolution.BaseSymbol,
                        target501Name,
                        "Target 501",
                        result);

                    AssemblyDuplicationEngine.RequirePairwiseTypes(
                        source,
                        target500.Assembly,
                        target501.Assembly);
                    AssemblyDuplicationEngine.RunRenameProbe(
                        document,
                        source,
                        target500.Assembly,
                        target501.Assembly,
                        target500Name,
                        target501Name,
                        result);

                    AssemblyDuplicationEngine.CaptureAndValidate(
                        document,
                        source,
                        target500,
                        target501,
                        target500Name,
                        target501Name,
                        result);

                    AssemblyDuplicationEngine.RunTransaction(
                        document,
                        result,
                        "Regenerate Assembly Identity POC",
                        () =>
                    {
                        document.Regenerate();
                    });

                    AssemblyDuplicationEngine.CaptureAndValidate(
                        document,
                        source,
                        target500,
                        target501,
                        target500Name,
                        target501Name,
                        result);
                    AssemblyDuplicationEngine.CaptureContaminationObservations(
                        document,
                        target500,
                        target501,
                        result);

                    AssemblyDuplicationInvariant failed = result.Invariants
                        .FirstOrDefault(check => !check.Passed);
                    if (failed != null)
                    {
                        throw new InvalidOperationException(
                            "Identity marker validation failed: " + failed.Name + ". " + failed.Details);
                    }

                    result.Succeeded = true;
                    result.Summary = "Two assemblies were created with pairwise-independent types.";
                    result.Details = "Production members, identity markers, ownership, names, and type independence passed twice.";

                    TransactionStatus assimilateStatus = group.Assimilate();
                    result.TransactionStages.Add(new AssemblyTransactionStage
                    {
                        Name = "Transaction group assimilate",
                        Status = assimilateStatus
                    });
                    if (assimilateStatus != TransactionStatus.Committed)
                        throw new InvalidOperationException("Assembly identity transaction group did not assimilate: " + assimilateStatus + ".");
                }
                catch (Exception exception)
                {
                    failure = exception;
                    result.Succeeded = false;
                    result.Details = exception.Message;

                    TransactionStatus groupStatus = group.GetStatus();
                    if (groupStatus == TransactionStatus.Started)
                    {
                        try
                        {
                            groupStatus = group.RollBack();
                            result.TransactionStages.Add(new AssemblyTransactionStage
                            {
                                Name = "Transaction group rollback",
                                Status = groupStatus
                            });
                        }
                        catch (Exception rollbackException)
                        {
                            result.Details += Environment.NewLine +
                                "Transaction group rollback could not be confirmed: " +
                                rollbackException.Message;
                            groupStatus = group.GetStatus();
                        }
                    }

                    result.Summary = groupStatus == TransactionStatus.RolledBack
                        ? "The identity marker POC was rolled back."
                        : "The identity marker POC failed; rollback was not confirmed (group status " +
                          groupStatus + "). Close the model without saving and inspect the report.";
                }
            }

            try
            {
                result.ReportPath = AssemblyDuplicationDiagnosticReport.Write(document, result, failure);
            }
            catch (Exception reportException)
            {
                result.ReportPath = string.Empty;
                result.Details += Environment.NewLine +
                    "The diagnostic report could not be written: " + reportException.Message;
            }

            return result;
        }

        // Changed by Jhay: one-target destination-level test using the proven duplication path.
        public static AssemblyDuplicationDiagnosticResult RunSingleToLevel(
            Document document,
            AssemblyInstance source,
            string targetName,
            Level destinationLevel,
            string assemblyDirectory)
        {
            // Changed by Jhay: retain this diagnostic as a regression adapter over the shared engine.
            return AssemblyDuplicationDiagnosticAdapter.RunSingleToLevel(
                document,
                source,
                targetName,
                destinationLevel,
                assemblyDirectory);
        }

    }

    // Changed by Jhay: validated copy, alignment, evidence, and validation operations are engine-owned.
    internal sealed partial class AssemblyDuplicationEngine
    {
        internal const double CoordinateTolerance = 1e-6;


        internal static CreatedAssemblyTarget CreateTarget(
            Document document,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionMemberIds,
            FamilySymbol baseMarkerSymbol,
            string targetName,
            string stagePrefix,
            AssemblyDuplicationDiagnosticResult result,
            AssemblyDestinationLevelPlan destinationPlan = null)
        {
            AssemblyInstance target = null;
            AssemblyIdentityMarkerPlacement marker = null;
            List<ElementId> copiedProductionIds = null;
            var provenance = new TargetMemberProvenance();

            RunTransaction(document, result, stagePrefix + " copy, marker, and assembly creation", () =>
            {
                ICollection<ElementId> copiedIds;
                using (var options = new CopyPasteOptions())
                {
                    copiedIds = ElementTransformUtils.CopyElements(
                        document,
                        sourceProductionMemberIds.ToList(),
                        document,
                        Transform.Identity,
                        options);
                }

                document.Regenerate();
                List<ElementId> allCopiedIds = copiedIds?.ToList() ?? new List<ElementId>();
                provenance.CopyReturnedIds.UnionWith(
                    allCopiedIds.Select(RevitApiCompatibility.GetElementIdValue));
                if (allCopiedIds.Count == 0)
                    throw new InvalidOperationException(stagePrefix + " returned no copied elements.");

                copiedProductionIds = SelectCopiedProductionIds(
                    document,
                    source,
                    sourceProductionMemberIds,
                    allCopiedIds,
                    stagePrefix,
                    result);
                provenance.SelectedProductionIds.UnionWith(
                    copiedProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
                RemoveUnmatchedCopyResults(document, copiedProductionIds, allCopiedIds, stagePrefix);

                if (destinationPlan != null)
                {
                    AssemblyDestinationLevelService.MoveAndReassociate(
                        document,
                        sourceProductionMemberIds,
                        copiedProductionIds,
                        destinationPlan,
                        result.DestinationLevelObservations);
                }

                foreach (ElementId copiedId in copiedProductionIds)
                {
                    Element copiedElement = document.GetElement(copiedId);
                    if (copiedElement == null)
                        throw new InvalidOperationException(stagePrefix + " lost a requested production copy.");
                    if (!RevitApiCompatibility.IsInvalidElementId(copiedElement.AssemblyInstanceId))
                        throw new InvalidOperationException(stagePrefix + " copied element already belongs to an assembly.");
                }

                marker = destinationPlan == null
                    ? AssemblyIdentityFamilyService.CreateMarker(
                        document,
                        baseMarkerSymbol,
                        targetName,
                        source,
                        sourceProductionMemberIds)
                    : AssemblyIdentityFamilyService.CreateMarker(
                        document,
                        baseMarkerSymbol,
                        targetName,
                        destinationPlan.DestinationLevel,
                        destinationPlan.TargetTransform.Origin);
                provenance.MarkerRelatedIds.Add(
                    RevitApiCompatibility.GetElementIdValue(marker.Marker.Id));
                provenance.MarkerRelatedIds.UnionWith(
                    marker.Marker.GetSubComponentIds().Select(
                        RevitApiCompatibility.GetElementIdValue));
                provenance.MarkerRelatedIds.UnionWith(
                    marker.Marker.GetDependentElements(null).Select(
                        RevitApiCompatibility.GetElementIdValue));

                var combinedIds = new List<ElementId>(copiedProductionIds) { marker.Marker.Id };
                if (!AssemblyInstance.IsValidNamingCategory(
                        document,
                        source.NamingCategoryId,
                        combinedIds))
                    throw new InvalidOperationException(stagePrefix + " naming category is not valid for the selected members.");
                if (!AssemblyInstance.AreElementsValidForAssembly(
                        document,
                        combinedIds,
                        ElementId.InvalidElementId))
                    throw new InvalidOperationException(stagePrefix + " members are not valid for a new assembly.");

                // Changed by Jhay: snapshot fitting-hosted insulation immediately before assembly creation.
                CaptureHostedPipeInsulationRelationshipsBeforeAssemblyCreate(
                    document,
                    source,
                    sourceProductionMemberIds,
                    copiedProductionIds,
                    provenance);

                target = AssemblyInstance.Create(document, combinedIds, source.NamingCategoryId);
                if (target == null)
                    throw new InvalidOperationException(stagePrefix + " assembly creation returned null.");
                document.Regenerate();
                // Changed by Jhay: prove which fitting-hosted insulation Revit adds during assembly creation.
                CaptureHostedPipeInsulationRelationshipsAfterAssemblyCreate(
                    document,
                    destinationPlan == null ? 0.0 : destinationPlan.DeltaZ,
                    target.Id,
                    copiedProductionIds,
                    provenance);
                provenance.MembersImmediatelyAfterAssemblyCreate.UnionWith(
                    target.GetMemberIds().Select(RevitApiCompatibility.GetElementIdValue));
                AppendTargetMembershipPhaseObservation(
                    document,
                    target,
                    combinedIds,
                    stagePrefix + " immediately after AssemblyInstance.Create",
                    result.CopyMatchingObservations);
            });

            if (target == null || !target.IsValidObject)
                throw new InvalidOperationException(stagePrefix + " target is unavailable after commit.");
            if (RevitApiCompatibility.GetElementIdValue(target.GetTypeId()) ==
                RevitApiCompatibility.GetElementIdValue(source.GetTypeId()))
            {
                throw new InvalidOperationException(
                    stagePrefix + " still shares the source AssemblyType despite its unique identity marker.");
            }

            AlignTargetTransform(
                document,
                source,
                target,
                copiedProductionIds,
                marker.Marker,
                stagePrefix,
                result,
                destinationPlan == null ? source.GetTransform() : destinationPlan.TargetTransform);

            RunTransaction(document, result, stagePrefix + " visible name", () =>
            {
                target.AssemblyTypeName = targetName;
                document.Regenerate();
            });

            target = document.GetElement(target.Id) as AssemblyInstance;
            if (target == null || !target.IsValidObject)
                throw new InvalidOperationException(stagePrefix + " target is unavailable after naming.");

            // Changed by Jhay: revalidate dependency world geometry and ownership in the final target state.
            CaptureHostedPipeInsulationRelationshipsAfterAssemblyCreate(
                document,
                destinationPlan == null ? 0.0 : destinationPlan.DeltaZ,
                target.Id,
                copiedProductionIds,
                provenance);

            return new CreatedAssemblyTarget
            {
                Assembly = target,
                CopiedProductionIds = copiedProductionIds,
                Marker = marker,
                Provenance = provenance
            };
        }

        // Changed by Jhay: record exactly when Revit first exposes members beyond the requested set.
        private static void AppendTargetMembershipPhaseObservation(
            Document document,
            AssemblyInstance target,
            IEnumerable<ElementId> requestedIds,
            string phase,
            IList<string> observations)
        {
            var requested = new HashSet<long>(
                requestedIds.Select(RevitApiCompatibility.GetElementIdValue));
            List<ElementId> unexpected = target.GetMemberIds()
                .Where(id => !requested.Contains(RevitApiCompatibility.GetElementIdValue(id)))
                .ToList();
            observations.Add(
                phase + " | unexpected target member count=" + unexpected.Count +
                (unexpected.Count == 0
                    ? " | <none>"
                    : " | " + string.Join(", ", unexpected.Select(id =>
                        DescribeUnexpectedTargetMember(document, id, null)))));
        }

        // Changed by Jhay: focused provenance evidence for target members not requested by the engine.
        internal static void AppendUnexpectedTargetMemberDiagnostics(
            Document document,
            CreatedAssemblyTarget target,
            IList<string> observations)
        {
            var expected = new HashSet<long>(
                target.CopiedProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
            expected.Add(target.Marker.Evidence.MarkerId);
            List<ElementId> unexpected = target.Assembly.GetMemberIds()
                .Where(id => !expected.Contains(RevitApiCompatibility.GetElementIdValue(id)))
                .OrderBy(RevitApiCompatibility.GetElementIdValue)
                .ToList();

            observations.Add("UNEXPECTED TARGET MEMBER DIAGNOSTICS");
            observations.Add(
                "Requested copied production IDs: " +
                string.Join(", ", target.CopiedProductionIds.Select(
                    RevitApiCompatibility.GetElementIdValue)));
            observations.Add("Requested marker ID: " + target.Marker.Evidence.MarkerId);
            observations.Add(
                "Unexpected final target member count: " + unexpected.Count);
            // Changed by Jhay: keep source/copy/generated host evidence beside strict unexpected-member failures.
            AppendHostedPipeInsulationRelationshipDiagnostics(
                target.Provenance.HostedPipeInsulationRelationships,
                new HashSet<long>(unexpected.Select(
                    RevitApiCompatibility.GetElementIdValue)),
                observations);
            if (unexpected.Count == 0)
            {
                observations.Add("Unexpected final target members: <none>");
                return;
            }

            foreach (ElementId unexpectedId in unexpected)
            {
                long value = RevitApiCompatibility.GetElementIdValue(unexpectedId);
                string phase;
                if (target.Provenance.SelectedProductionIds.Contains(value))
                    phase = "selected requested production copy";
                else if (target.Provenance.CopyReturnedIds.Contains(value))
                    phase = "returned by CopyElements but not selected as requested production";
                else if (target.Provenance.MarkerRelatedIds.Contains(value))
                    phase = "created/exposed through the identity-marker relationship";
                else if (target.Provenance.HostedPipeInsulationRelationships.Any(
                    relationship => relationship.CopiedInsulationsBefore.Any(
                        insulation => insulation.Id == value)))
                {
                    phase = "existed as a copied host dependency before AssemblyInstance.Create; " +
                        "observed as a target member immediately after AssemblyInstance.Create";
                }
                else if (target.Provenance.MembersImmediatelyAfterAssemblyCreate.Contains(value))
                    phase = "first observed immediately after AssemblyInstance.Create";
                else
                    phase = "first observed after assembly creation during alignment/naming/regeneration";

                HostDependentDependencyCandidateValidation dependency =
                    target.Provenance.HostedPipeInsulationRelationships
                        .SelectMany(relationship => relationship.ValidatedDependencies)
                        .FirstOrDefault(item => item.TargetDependencyId == value && item.Passed);
                string semanticClassification = dependency == null
                    ? null
                    : "ValidatedHostDependentProductionDependency" +
                      " | SourceDependencyId=" + dependency.SourceDependencyId +
                      " | HostMapping=" + dependency.SourceHostId + "->" +
                      dependency.TargetHostId;

                observations.Add(DescribeUnexpectedTargetMember(
                    document,
                    unexpectedId,
                    phase,
                    semanticClassification));
            }
        }

        // Changed by Jhay: capture source and copied fitting insulation state without changing classification.
        private static void CaptureHostedPipeInsulationRelationshipsBeforeAssemblyCreate(
            Document document,
            AssemblyInstance sourceAssembly,
            IReadOnlyList<ElementId> sourceProductionIds,
            IReadOnlyList<ElementId> copiedProductionIds,
            TargetMemberProvenance provenance)
        {
            var sourceAssemblyMembers = new HashSet<long>(
                sourceAssembly.GetMemberIds().Select(RevitApiCompatibility.GetElementIdValue));

            for (int index = 0; index < sourceProductionIds.Count; index++)
            {
                Element sourceHost = document.GetElement(sourceProductionIds[index]);
                Element copiedHost = document.GetElement(copiedProductionIds[index]);
                if (sourceHost == null || copiedHost == null)
                    continue;

                List<HostedPipeInsulationEvidence> sourceInsulations =
                    CaptureHostedPipeInsulations(
                        document,
                        sourceHost.Id,
                        sourceAssemblyMembers,
                        out string sourceInsulationError);
                List<HostedPipeInsulationEvidence> copiedInsulations =
                    CaptureHostedPipeInsulations(
                        document,
                        copiedHost.Id,
                        null,
                        out string copiedInsulationError);
                if (sourceInsulations.Count == 0 && copiedInsulations.Count == 0 &&
                    string.IsNullOrEmpty(sourceInsulationError) &&
                    string.IsNullOrEmpty(copiedInsulationError))
                    continue;

                var evidence = new HostedPipeInsulationRelationshipEvidence
                {
                    SourceHostId = RevitApiCompatibility.GetElementIdValue(sourceHost.Id),
                    CopiedHostId = RevitApiCompatibility.GetElementIdValue(copiedHost.Id),
                    SourceDependentIds = GetDependentIdValues(sourceHost, out string sourceDependentError),
                    SourceDependentError = sourceDependentError,
                    CopiedDependentIdsBefore = GetDependentIdValues(copiedHost, out string copiedDependentError),
                    CopiedDependentErrorBefore = copiedDependentError
                };
                evidence.SourceInsulations.AddRange(sourceInsulations);
                evidence.SourceInsulationError = sourceInsulationError;
                evidence.CopiedInsulationsBefore.AddRange(copiedInsulations);
                evidence.CopiedInsulationErrorBefore = copiedInsulationError;
                provenance.HostedPipeInsulationRelationships.Add(evidence);
            }
        }

        // Changed by Jhay: compare fitting-hosted insulation before/after AssemblyInstance.Create.
        private static void CaptureHostedPipeInsulationRelationshipsAfterAssemblyCreate(
            Document document,
            double deltaZ,
            ElementId targetAssemblyId,
            IReadOnlyCollection<ElementId> copiedProductionIds,
            TargetMemberProvenance provenance)
        {
            long targetAssemblyIdValue = RevitApiCompatibility.GetElementIdValue(targetAssemblyId);
            var primaryIds = new HashSet<long>(
                copiedProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
            foreach (HostedPipeInsulationRelationshipEvidence relationship in
                provenance.HostedPipeInsulationRelationships)
            {
                relationship.CopiedInsulationsAfter.Clear();
                relationship.GeneratedInsulations.Clear();
                relationship.ValidatedDependencies.Clear();
                relationship.UnclassifiedDependencyReasons.Clear();
                Element copiedHost = document.GetElement(
                    RevitApiCompatibility.CreateElementId(relationship.CopiedHostId));
                relationship.CopiedDependentIdsAfter = GetDependentIdValues(
                    copiedHost,
                    out string dependentError);
                relationship.CopiedDependentErrorAfter = dependentError;
                relationship.CopiedInsulationsAfter.AddRange(CaptureHostedPipeInsulations(
                    document,
                    copiedHost.Id,
                    null,
                    out string insulationError));
                relationship.CopiedInsulationErrorAfter = insulationError;

                var beforeIds = new HashSet<long>(
                    relationship.CopiedInsulationsBefore.Select(item => item.Id));
                relationship.GeneratedInsulations.AddRange(
                    relationship.CopiedInsulationsAfter.Where(item => !beforeIds.Contains(item.Id)));

                var usedSourceIds = new HashSet<long>();
                foreach (HostedPipeInsulationEvidence targetDependency in
                    relationship.CopiedInsulationsAfter.Where(item => !primaryIds.Contains(item.Id)))
                {
                    List<HostDependentDependencyCandidateValidation> candidates =
                        relationship.SourceInsulations
                            .Where(source => !usedSourceIds.Contains(source.Id))
                            .Select(source => ValidateHostDependentProductionDependency(
                                relationship,
                                source,
                                targetDependency,
                                targetAssemblyIdValue,
                                deltaZ))
                            .ToList();
                    HostDependentDependencyCandidateValidation validated =
                        candidates.FirstOrDefault(candidate => candidate.Passed);
                    if (validated != null)
                    {
                        usedSourceIds.Add(validated.SourceDependencyId);
                        relationship.ValidatedDependencies.Add(validated);
                        targetDependency.EquivalentExternalSourceInsulationId =
                            validated.SourceDependencyId;
                    }
                    else
                    {
                        relationship.UnclassifiedDependencyReasons[targetDependency.Id] =
                            candidates.Count == 0
                                ? "no hosted source PipeInsulation candidate is available"
                                : string.Join("; ", candidates.Select(candidate =>
                                    "source " + candidate.SourceDependencyId + ": " +
                                    string.Join(", ", candidate.Failures)));
                    }
                }
            }
        }

        private static List<long> GetDependentIdValues(Element element, out string error)
        {
            error = null;
            if (element == null)
            {
                error = "host unavailable";
                return new List<long>();
            }

            try
            {
                return element.GetDependentElements(null)
                    .Select(RevitApiCompatibility.GetElementIdValue)
                    .OrderBy(value => value)
                    .ToList();
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return new List<long>();
            }
        }

        private static List<HostedPipeInsulationEvidence> CaptureHostedPipeInsulations(
            Document document,
            ElementId hostId,
            ISet<long> sourceAssemblyMembers,
            out string error)
        {
            error = null;
            try
            {
                return InsulationLiningBase.GetInsulationIds(document, hostId)
                    .Select(id => document.GetElement(id) as PipeInsulation)
                    .Where(insulation => insulation != null)
                    .Select(insulation => HostedPipeInsulationEvidence.Capture(
                        document,
                        insulation,
                        sourceAssemblyMembers))
                    .OrderBy(item => item.Id)
                    .ToList();
            }
            catch (Exception exception)
            {
                error = exception.Message;
                return new List<HostedPipeInsulationEvidence>();
            }
        }

        // Changed by Jhay: require every semantic relationship before classifying an extra dependency.
        private static HostDependentDependencyCandidateValidation ValidateHostDependentProductionDependency(
            HostedPipeInsulationRelationshipEvidence relationship,
            HostedPipeInsulationEvidence source,
            HostedPipeInsulationEvidence target,
            long targetAssemblyId,
            double deltaZ)
        {
            var validation = new HostDependentDependencyCandidateValidation
            {
                SourceHostId = relationship.SourceHostId,
                TargetHostId = relationship.CopiedHostId,
                SourceDependencyId = source.Id,
                TargetDependencyId = target.Id
            };

            if (source.IsSourceAssemblyMember)
                validation.Failures.Add("source dependency belongs to the source assembly");
            if (source.CategoryId != (long)BuiltInCategory.OST_PipeInsulations)
                validation.Failures.Add("source category is not Pipe Insulations");
            if (target.CategoryId != (long)BuiltInCategory.OST_PipeInsulations)
                validation.Failures.Add("target category is not Pipe Insulations");
            if (source.TypeId != target.TypeId)
                validation.Failures.Add("insulation type differs");
            if (Math.Abs(source.Thickness - target.Thickness) > CoordinateTolerance)
                validation.Failures.Add("insulation thickness differs");
            if (source.HostId != relationship.SourceHostId)
                validation.Failures.Add("source HostElementId is not the mapped source host");
            if (target.HostId != relationship.CopiedHostId)
                validation.Failures.Add("target HostElementId is not the mapped copied host");
            if (target.AssemblyInstanceId != targetAssemblyId)
                validation.Failures.Add("target dependency does not belong to the target assembly");

            if (source.BoundsMin == null || source.BoundsMax == null ||
                target.BoundsMin == null || target.BoundsMax == null)
            {
                validation.Failures.Add("world bounds are unavailable");
            }
            else
            {
                XYZ translation = new XYZ(0.0, 0.0, deltaZ);
                if (source.BoundsMin.Add(translation).DistanceTo(target.BoundsMin) > CoordinateTolerance ||
                    source.BoundsMax.Add(translation).DistanceTo(target.BoundsMax) > CoordinateTolerance)
                {
                    validation.Failures.Add(
                        "target world bounds do not equal source world bounds plus deltaZ " +
                        FormatCoordinate(deltaZ));
                }
            }

            return validation;
        }

        // Changed by Jhay: report evidence only; strict production membership remains unchanged.
        private static void AppendHostedPipeInsulationRelationshipDiagnostics(
            IEnumerable<HostedPipeInsulationRelationshipEvidence> relationships,
            ISet<long> unclassifiedFinalTargetMemberIds,
            IList<string> observations)
        {
            observations.Add("HOST-DEPENDENT PIPE INSULATION RELATIONSHIPS");
            foreach (HostedPipeInsulationRelationshipEvidence relationship in relationships)
            {
                observations.Add(
                    "Source host " + relationship.SourceHostId +
                    " -> copied host " + relationship.CopiedHostId);
                observations.Add(
                    "  Source dependent IDs: " + DescribeIdsAndError(
                        relationship.SourceDependentIds,
                        relationship.SourceDependentError));
                observations.Add(
                    "  Source hosted PipeInsulation: " + DescribeInsulations(
                        relationship.SourceInsulations,
                        relationship.SourceInsulationError));
                observations.Add(
                    "  Copied dependent IDs before AssemblyInstance.Create: " + DescribeIdsAndError(
                        relationship.CopiedDependentIdsBefore,
                        relationship.CopiedDependentErrorBefore));
                observations.Add(
                    "  Copied hosted PipeInsulation before AssemblyInstance.Create: " + DescribeInsulations(
                        relationship.CopiedInsulationsBefore,
                        relationship.CopiedInsulationErrorBefore));
                observations.Add(
                    "  Copied dependent IDs after AssemblyInstance.Create: " + DescribeIdsAndError(
                        relationship.CopiedDependentIdsAfter,
                        relationship.CopiedDependentErrorAfter));
                observations.Add(
                    "  Copied hosted PipeInsulation after AssemblyInstance.Create: " + DescribeInsulations(
                        relationship.CopiedInsulationsAfter,
                        relationship.CopiedInsulationErrorAfter));
                observations.Add(
                    "  Generated during AssemblyInstance.Create: " + DescribeInsulations(
                        relationship.GeneratedInsulations,
                        null));
                foreach (HostedPipeInsulationEvidence generated in relationship.GeneratedInsulations)
                {
                    observations.Add(
                        "  Generated insulation " + generated.Id +
                        " equivalent source insulation outside source assembly: " +
                        (generated.EquivalentExternalSourceInsulationId.HasValue
                            ? generated.EquivalentExternalSourceInsulationId.Value.ToString(
                                CultureInfo.InvariantCulture) +
                              " (same type, thickness, host mapping, and world bounds + deltaZ)"
                            : "<not proven>"));
                }
                foreach (HostDependentDependencyCandidateValidation dependency in
                    relationship.ValidatedDependencies)
                {
                    observations.Add(
                        "  ValidatedHostDependentProductionDependency | " +
                        dependency.SourceDependencyId + " -> " + dependency.TargetDependencyId +
                        " | host " + dependency.SourceHostId + " -> " +
                        dependency.TargetHostId + " | PASS");
                }
                foreach (KeyValuePair<long, string> unclassified in
                    relationship.UnclassifiedDependencyReasons.OrderBy(item => item.Key))
                {
                    bool isUnclassifiedTargetMember =
                        unclassifiedFinalTargetMemberIds.Contains(unclassified.Key);
                    observations.Add(isUnclassifiedTargetMember
                        ? "  Unclassified target hosted dependency " + unclassified.Key +
                          " | FAIL | " + unclassified.Value
                        : "  Hosted dependency " + unclassified.Key +
                          " | INFO | external hosted dependency not part of target assembly | " +
                          unclassified.Value);
                }
            }
            observations.Add(
                "Classification rule: only fully validated host-dependent PipeInsulation is separated from primary production; all other extras remain hard failures.");
        }

        private static string DescribeIdsAndError(IEnumerable<long> ids, string error)
        {
            if (!string.IsNullOrEmpty(error))
                return "<unavailable: " + error + ">";
            List<long> values = ids?.ToList() ?? new List<long>();
            return values.Count == 0 ? "<none>" : string.Join(", ", values);
        }

        private static string DescribeInsulations(
            IEnumerable<HostedPipeInsulationEvidence> insulations,
            string error)
        {
            if (!string.IsNullOrEmpty(error))
                return "<unavailable: " + error + ">";
            List<HostedPipeInsulationEvidence> values = insulations?.ToList() ??
                new List<HostedPipeInsulationEvidence>();
            return values.Count == 0
                ? "<none>"
                : string.Join("; ", values.Select(value => value.Describe()));
        }

        private static string DescribeUnexpectedTargetMember(
            Document document,
            ElementId memberId,
            string phase,
            string semanticClassification = null)
        {
            Element member = document.GetElement(memberId);
            long value = RevitApiCompatibility.GetElementIdValue(memberId);
            if (member == null)
                return "ElementId=" + value + " | unavailable | phase=" + (phase ?? "unknown");

            Category category = member.Category;
            ElementType elementType = document.GetElement(member.GetTypeId()) as ElementType;
            var familyInstance = member as FamilyInstance;
            var insulation = member as PipeInsulation;
            long ownerId = RevitApiCompatibility.GetElementIdValue(member.AssemblyInstanceId);
            long familyHostId = familyInstance?.Host == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(familyInstance.Host.Id);
            long superComponentId = familyInstance?.SuperComponent == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(familyInstance.SuperComponent.Id);
            long insulationHostId = insulation == null
                ? -1L
                : RevitApiCompatibility.GetElementIdValue(insulation.HostElementId);
            string dependents;
            try
            {
                dependents = string.Join(",", member.GetDependentElements(null)
                    .Select(RevitApiCompatibility.GetElementIdValue));
            }
            catch (Exception exception)
            {
                dependents = "<unavailable: " + exception.Message + ">";
            }

            bool isIdentityMarker = familyInstance?.Symbol?.Family != null &&
                string.Equals(
                    familyInstance.Symbol.Family.Name,
                    AssemblyIdentityFamilyService.FamilyName,
                    StringComparison.Ordinal) &&
                familyInstance.Symbol.Name.StartsWith("PS_ASM_ID_", StringComparison.Ordinal);
            string classification = semanticClassification ?? (isIdentityMarker
                ? "Parallel Systems identity marker/internal implementation element"
                : "unclassified unexpected member; strict production validation remains enabled");

            return "ElementId=" + value +
                " | Category=" + (category?.Name ?? "<none>") +
                " (" + (category == null
                    ? -1L
                    : RevitApiCompatibility.GetElementIdValue(category.Id)) + ")" +
                " | RuntimeType=" + member.GetType().FullName +
                " | Family=" + (familyInstance?.Symbol?.Family?.Name ?? "<none>") +
                " | FamilySymbol=" + (familyInstance?.Symbol?.Name ?? "<none>") +
                " | Name=" + (member.Name ?? "<none>") +
                " | ElementType=" + (elementType?.Name ?? "<none>") +
                " (" + RevitApiCompatibility.GetElementIdValue(member.GetTypeId()) + ")" +
                " | AssemblyInstanceId=" + ownerId +
                " | FamilyHostId=" + familyHostId +
                " | InsulationHostId=" + insulationHostId +
                " | SuperComponentId=" + superComponentId +
                " | DependentIds=" + (string.IsNullOrEmpty(dependents) ? "<none>" : dependents) +
                " | IsPSAssemblyIdentity=" + isIdentityMarker +
                " | Phase=" + (phase ?? "unknown") +
                " | Classification=" + classification;
        }

        private static void AlignTargetTransform(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target,
            IReadOnlyCollection<ElementId> productionIds,
            FamilyInstance marker,
            string stagePrefix,
            AssemblyDuplicationDiagnosticResult result,
            Transform expectedTransform)
        {
            Transform sourceTransform = source.GetTransform();
            result.TransformAlignmentObservations.Add(
                stagePrefix + " source transform: " + DescribeTransform(sourceTransform));

            Transform targetTransformBefore = null;
            Transform targetTransformAfter = null;
            Dictionary<long, LooseCopyGeometryEvidence> worldBefore = null;
            Dictionary<long, LooseCopyGeometryEvidence> worldAfter = null;
            RunTransaction(document, result, "Align " + stagePrefix + " assembly transform", () =>
            {
                targetTransformBefore = target.GetTransform();
                worldBefore = CaptureWorldGeometry(document, productionIds);
                result.TransformAlignmentObservations.Add(
                    stagePrefix + " target transform before SetTransform: " +
                    DescribeTransform(targetTransformBefore));
                AppendWorldGeometryObservations(
                    result,
                    stagePrefix + " world production geometry before SetTransform",
                    worldBefore);

                target.SetTransform(expectedTransform);
                document.Regenerate();

                targetTransformAfter = target.GetTransform();
                worldAfter = CaptureWorldGeometry(document, productionIds);
                result.TransformAlignmentObservations.Add(
                    stagePrefix + " target transform after SetTransform: " +
                    DescribeTransform(targetTransformAfter));
                AppendWorldGeometryObservations(
                    result,
                    stagePrefix + " world production geometry after SetTransform",
                    worldAfter);

                var markerLocation = marker.Location as LocationPoint;
                if (markerLocation == null)
                {
                    result.TransformAlignmentObservations.Add(
                        stagePrefix + " marker assembly-relative position: unavailable (no LocationPoint)");
                }
                else
                {
                    XYZ markerLocal = targetTransformAfter.Inverse.OfPoint(markerLocation.Point);
                    result.TransformAlignmentObservations.Add(
                        stagePrefix + " marker assembly-relative position after alignment: " +
                        FormatPoint(markerLocal) + " | approximately origin: " +
                        (markerLocal.DistanceTo(XYZ.Zero) <= CoordinateTolerance));
                }

                double transformDifference = MaximumTransformDifference(
                    expectedTransform,
                    targetTransformAfter);
                if (transformDifference > CoordinateTolerance)
                {
                    throw new InvalidOperationException(
                        stagePrefix + " assembly transform did not match the expected target after SetTransform; " +
                        "maximum origin/basis difference " + FormatCoordinate(transformDifference) + ".");
                }

                foreach (KeyValuePair<long, LooseCopyGeometryEvidence> before in worldBefore)
                {
                    LooseCopyGeometryEvidence after;
                    if (!worldAfter.TryGetValue(before.Key, out after))
                    {
                        throw new InvalidOperationException(
                            stagePrefix + " lost production member " + before.Key +
                            " while aligning the assembly transform.");
                    }

                    CopyGeometryComparison comparison = CompareLooseCopyGeometry(before.Value, after);
                    if (!comparison.Accepted)
                    {
                        throw new InvalidOperationException(
                            stagePrefix + " SetTransform physically changed production member " +
                            before.Key + ": " + comparison.Reason + ".");
                    }
                }
            });

            result.TransformAlignmentObservations.Add(
                stagePrefix + " world production geometry unchanged: PASS");
        }

        private static Dictionary<long, LooseCopyGeometryEvidence> CaptureWorldGeometry(
            Document document,
            IEnumerable<ElementId> elementIds)
        {
            var evidence = new Dictionary<long, LooseCopyGeometryEvidence>();
            foreach (ElementId id in elementIds)
            {
                Element element = document.GetElement(id);
                if (element == null)
                {
                    throw new InvalidOperationException(
                        "Production member " + RevitApiCompatibility.GetElementIdValue(id) +
                        " is unavailable while capturing world geometry.");
                }

                LooseCopyGeometryEvidence captured = LooseCopyGeometryEvidence.Capture(element);
                evidence.Add(captured.Id, captured);
            }
            return evidence;
        }

        private static void AppendWorldGeometryObservations(
            AssemblyDuplicationDiagnosticResult result,
            string heading,
            IReadOnlyDictionary<long, LooseCopyGeometryEvidence> evidence)
        {
            if (evidence == null)
            {
                result.TransformAlignmentObservations.Add(heading + ": <not captured>");
                return;
            }

            foreach (KeyValuePair<long, LooseCopyGeometryEvidence> item in evidence.OrderBy(item => item.Key))
            {
                result.TransformAlignmentObservations.Add(
                    heading + " | " + item.Value.DescribeIdentity() + " | " +
                    item.Value.DescribeWorldGeometry());
            }
        }

        private static string DescribeTransform(Transform transform)
        {
            if (transform == null)
                return "<not captured>";
            return "Origin=" + FormatPoint(transform.Origin) +
                ", BasisX=" + FormatPoint(transform.BasisX) +
                ", BasisY=" + FormatPoint(transform.BasisY) +
                ", BasisZ=" + FormatPoint(transform.BasisZ);
        }

        private static string FormatPoint(XYZ point)
        {
            return "(" + FormatCoordinate(point.X) + "," +
                FormatCoordinate(point.Y) + "," +
                FormatCoordinate(point.Z) + ")";
        }

        private static List<ElementId> SelectCopiedProductionIds(
            Document document,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionIds,
            IReadOnlyCollection<ElementId> copiedIds,
            string stagePrefix,
            AssemblyDuplicationDiagnosticResult result)
        {
            var sourceIdValues = new HashSet<long>(
                sourceProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
            var available = copiedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .Select(element => new CandidateCopy
                {
                    Id = element.Id,
                    Evidence = LooseCopyGeometryEvidence.Capture(element)
                })
                .OrderBy(candidate => RevitApiCompatibility.GetElementIdValue(candidate.Id))
                .ToList();
            result.CopyMatchingObservations.Add(
                stagePrefix + " CopyElements returned " + copiedIds.Count + " id(s): " +
                string.Join(", ", copiedIds.Select(RevitApiCompatibility.GetElementIdValue)));
            result.CopyMatchingObservations.Add(
                stagePrefix + " resolved returned ids: " +
                (available.Count == 0
                    ? "<none>"
                    : string.Join(", ", available.Select(candidate => candidate.Evidence.DescribeIdentity()))));

            var sources = new List<LooseCopyGeometryEvidence>();
            var optionsBySource = new Dictionary<long, List<CopyMatchOption>>();

            foreach (ElementId sourceId in sourceProductionIds)
            {
                Element sourceElement = document.GetElement(sourceId);
                if (sourceElement == null)
                    throw new InvalidOperationException("A source production member is unavailable while matching copies.");

                LooseCopyGeometryEvidence sourceEvidence = LooseCopyGeometryEvidence.Capture(sourceElement);
                sources.Add(sourceEvidence);
                List<CandidateCopy> sameType = available
                    .Where(candidate =>
                        candidate.Evidence.CategoryId == sourceEvidence.CategoryId &&
                        candidate.Evidence.TypeId == sourceEvidence.TypeId)
                    .ToList();
                var options = new List<CopyMatchOption>();
                var candidateResults = new List<string>();
                foreach (CandidateCopy candidate in sameType)
                {
                    long candidateId = RevitApiCompatibility.GetElementIdValue(candidate.Id);
                    CopyGeometryComparison comparison;
                    if (sourceIdValues.Contains(candidateId))
                        comparison = CopyGeometryComparison.Rejected("returned id is not new");
                    else if (candidate.Evidence.IsIdentityMarker)
                        comparison = CopyGeometryComparison.Rejected("candidate is the identity marker");
                    else
                        comparison = CompareLooseCopyGeometry(sourceEvidence, candidate.Evidence);

                    candidateResults.Add(
                        candidateId + " [" + candidate.Evidence.DescribeWorldGeometry() + "] => " +
                        (comparison.Accepted ? "accepted" : "rejected: " + comparison.Reason));
                    if (comparison.Accepted)
                    {
                        options.Add(new CopyMatchOption
                        {
                            Candidate = candidate,
                            Score = comparison.Score
                        });
                    }
                }

                optionsBySource[sourceEvidence.Id] = options
                    .OrderBy(option => option.Score)
                    .ThenBy(option => RevitApiCompatibility.GetElementIdValue(option.Candidate.Id))
                    .ToList();
                result.CopyMatchingObservations.Add(
                    stagePrefix + " source " + sourceEvidence.Id + " " +
                    sourceEvidence.DescribeIdentity() + " world=[" +
                    sourceEvidence.DescribeWorldGeometry() + "] same-category/type candidates: " +
                    (candidateResults.Count == 0 ? "<none>" : string.Join("; ", candidateResults)));
            }

            var assignment = new Dictionary<long, CandidateCopy>();
            List<LooseCopyGeometryEvidence> assignmentOrder = sources
                .OrderBy(item => optionsBySource[item.Id].Count)
                .ThenBy(item => item.Id)
                .ToList();
            if (!TryAssignLooseCopies(
                    assignmentOrder,
                    optionsBySource,
                    0,
                    new HashSet<long>(),
                    assignment))
            {
                result.CopyMatchingObservations.Add(
                    stagePrefix + " unmatched/dependency-created ids (matching failed before classification): " +
                    (available.Count == 0
                        ? "<none>"
                        : string.Join(", ", available.Select(candidate =>
                            candidate.Evidence.DescribeIdentity() + " world=[" +
                            candidate.Evidence.DescribeWorldGeometry() + "]"))));
                LooseCopyGeometryEvidence failedSource = assignmentOrder
                    .FirstOrDefault(item => optionsBySource[item.Id].Count == 0) ??
                    assignmentOrder.First();
                List<CandidateCopy> failedCandidates = available
                    .Where(candidate =>
                        candidate.Evidence.CategoryId == failedSource.CategoryId &&
                        candidate.Evidence.TypeId == failedSource.TypeId)
                    .ToList();
                throw new InvalidOperationException(BuildCopyMatchFailure(
                    failedSource,
                    failedCandidates,
                    available));
            }

            var selected = sourceProductionIds
                .Select(id => assignment[RevitApiCompatibility.GetElementIdValue(id)].Id)
                .ToList();
            var selectedValues = new HashSet<long>(
                selected.Select(RevitApiCompatibility.GetElementIdValue));
            foreach (LooseCopyGeometryEvidence sourceEvidence in sources.OrderBy(item => item.Id))
            {
                CandidateCopy candidate = assignment[sourceEvidence.Id];
                result.CopyMatchingObservations.Add(
                    stagePrefix + " selected source " + sourceEvidence.Id + " -> copied " +
                    RevitApiCompatibility.GetElementIdValue(candidate.Id));
            }

            List<CandidateCopy> unmatched = available
                .Where(candidate => !selectedValues.Contains(
                    RevitApiCompatibility.GetElementIdValue(candidate.Id)))
                .ToList();
            result.CopyMatchingObservations.Add(
                stagePrefix + " unmatched/dependency-created ids (" + unmatched.Count + "): " +
                (unmatched.Count == 0
                    ? "<none>"
                    : string.Join(", ", unmatched.Select(candidate =>
                        candidate.Evidence.DescribeIdentity() + " world=[" +
                        candidate.Evidence.DescribeWorldGeometry() + "]"))));

            return selected;
        }

        private static bool TryAssignLooseCopies(
            IReadOnlyList<LooseCopyGeometryEvidence> sources,
            IReadOnlyDictionary<long, List<CopyMatchOption>> optionsBySource,
            int index,
            ISet<long> usedCandidateIds,
            IDictionary<long, CandidateCopy> assignment)
        {
            if (index == sources.Count)
                return true;

            LooseCopyGeometryEvidence source = sources[index];
            foreach (CopyMatchOption option in optionsBySource[source.Id])
            {
                long candidateId = RevitApiCompatibility.GetElementIdValue(option.Candidate.Id);
                if (!usedCandidateIds.Add(candidateId))
                    continue;

                assignment[source.Id] = option.Candidate;
                if (TryAssignLooseCopies(
                        sources,
                        optionsBySource,
                        index + 1,
                        usedCandidateIds,
                        assignment))
                    return true;

                assignment.Remove(source.Id);
                usedCandidateIds.Remove(candidateId);
            }

            return false;
        }

        private static CopyGeometryComparison CompareLooseCopyGeometry(
            LooseCopyGeometryEvidence source,
            LooseCopyGeometryEvidence candidate)
        {
            if (!string.Equals(source.LocationKind, candidate.LocationKind, StringComparison.Ordinal))
                return CopyGeometryComparison.Rejected(
                    "location kind differs (source " + source.LocationKind +
                    ", candidate " + candidate.LocationKind + ")");

            double score = 0;
            bool hasStableGeometry = false;
            if (source.Start != null || candidate.Start != null)
            {
                if (source.Start == null || candidate.Start == null)
                    return CopyGeometryComparison.Rejected("only one element has a point/curve location");

                if (source.End != null || candidate.End != null)
                {
                    if (source.End == null || candidate.End == null)
                        return CopyGeometryComparison.Rejected("only one element has a curve endpoint");

                    double directStart = source.Start.DistanceTo(candidate.Start);
                    double directEnd = source.End.DistanceTo(candidate.End);
                    double reverseStart = source.Start.DistanceTo(candidate.End);
                    double reverseEnd = source.End.DistanceTo(candidate.Start);
                    double directMaximum = Math.Max(directStart, directEnd);
                    double reverseMaximum = Math.Max(reverseStart, reverseEnd);
                    double bestMaximum = Math.Min(directMaximum, reverseMaximum);
                    if (bestMaximum > CoordinateTolerance)
                    {
                        return CopyGeometryComparison.Rejected(
                            "world curve endpoints differ; best maximum distance " +
                            FormatCoordinate(bestMaximum));
                    }

                    score += directMaximum <= reverseMaximum
                        ? directStart + directEnd
                        : reverseStart + reverseEnd;
                }
                else
                {
                    double pointDistance = source.Start.DistanceTo(candidate.Start);
                    if (pointDistance > CoordinateTolerance)
                    {
                        return CopyGeometryComparison.Rejected(
                            "world point differs by " + FormatCoordinate(pointDistance));
                    }
                    score += pointDistance;
                }

                hasStableGeometry = true;
            }

            if (source.FamilyTransform != null || candidate.FamilyTransform != null)
            {
                if (source.FamilyTransform == null || candidate.FamilyTransform == null)
                    return CopyGeometryComparison.Rejected("only one element has a family transform");
                if (source.Mirrored != candidate.Mirrored)
                    return CopyGeometryComparison.Rejected("family mirrored state differs");

                double transformDifference = MaximumTransformDifference(
                    source.FamilyTransform,
                    candidate.FamilyTransform);
                if (transformDifference > CoordinateTolerance)
                {
                    return CopyGeometryComparison.Rejected(
                        "family world transform differs; maximum distance " +
                        FormatCoordinate(transformDifference));
                }
                score += transformDifference;
                hasStableGeometry = true;
            }

            if (source.BoundsCenter != null || candidate.BoundsCenter != null)
            {
                if (source.BoundsCenter == null || candidate.BoundsCenter == null)
                    return CopyGeometryComparison.Rejected("only one element has a world bounding box");

                double centerDifference = source.BoundsCenter.DistanceTo(candidate.BoundsCenter);
                double extentsDifference = source.BoundsExtents.DistanceTo(candidate.BoundsExtents);
                if (centerDifference > CoordinateTolerance || extentsDifference > CoordinateTolerance)
                {
                    return CopyGeometryComparison.Rejected(
                        "world bounding box differs; center " + FormatCoordinate(centerDifference) +
                        ", extents " + FormatCoordinate(extentsDifference));
                }
                score += centerDifference + extentsDifference;
                hasStableGeometry = true;
            }

            return hasStableGeometry
                ? CopyGeometryComparison.AcceptedMatch(score)
                : CopyGeometryComparison.Rejected("no stable world geometry/location evidence is available");
        }

        private static double MaximumTransformDifference(Transform left, Transform right)
        {
            return new[]
            {
                left.Origin.DistanceTo(right.Origin),
                left.BasisX.DistanceTo(right.BasisX),
                left.BasisY.DistanceTo(right.BasisY),
                left.BasisZ.DistanceTo(right.BasisZ)
            }.Max();
        }

        private static string BuildCopyMatchFailure(
            LooseCopyGeometryEvidence source,
            IReadOnlyCollection<CandidateCopy> sameTypeCandidates,
            IReadOnlyCollection<CandidateCopy> allCandidates)
        {
            var text = new StringBuilder();
            text.Append("Revit did not return a production copy matching source member ")
                .Append(source.Id).Append(" (").Append(source.CategoryName)
                .Append(", type ").Append(source.TypeId).Append("). ")
                .Append("Source world geometry: ").Append(source.DescribeWorldGeometry()).Append(". ")
                .Append("Same category/type candidates: ");
            if (sameTypeCandidates.Count == 0)
                text.Append("<none>");
            else
            {
                text.Append(string.Join("; ", sameTypeCandidates.Select(candidate =>
                {
                    CopyGeometryComparison comparison = CompareLooseCopyGeometry(source, candidate.Evidence);
                    return candidate.Evidence.Id + " [" + candidate.Evidence.DescribeWorldGeometry() +
                        "] => " + (comparison.Accepted ? "geometry accepted but unique assignment failed" : comparison.Reason);
                })));
            }

            text.Append(". CopyElements returned ").Append(allCandidates.Count).Append(" resolved id(s): ")
                .Append(string.Join(", ", allCandidates.Select(candidate => candidate.Evidence.DescribeIdentity())));
            return text.ToString();
        }

        internal static string FormatCoordinate(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }

        private static void RemoveUnmatchedCopyResults(
            Document document,
            IReadOnlyCollection<ElementId> selected,
            IReadOnlyCollection<ElementId> allCopied,
            string stagePrefix)
        {
            var selectedValues = new HashSet<long>(
                selected.Select(RevitApiCompatibility.GetElementIdValue));
            List<ElementId> extras = allCopied
                .Where(id => !selectedValues.Contains(RevitApiCompatibility.GetElementIdValue(id)))
                .ToList();
            if (extras.Count == 0)
                return;

            document.Delete(extras);
            document.Regenerate();
            if (selected.Any(id => document.GetElement(id) == null))
            {
                throw new InvalidOperationException(
                    stagePrefix + " cleanup of automatically copied dependencies also removed a requested production member.");
            }
        }

        internal static void RunRenameProbe(
            Document document,
            AssemblyInstance source,
            AssemblyInstance target500,
            AssemblyInstance target501,
            string target500Name,
            string target501Name,
            AssemblyDuplicationDiagnosticResult result)
        {
            string sourceName = source.AssemblyTypeName;
            string probeName = target500Name + "_PROBE_" +
                Guid.NewGuid().ToString("N").Substring(0, 8).ToUpperInvariant();

            RunTransaction(document, result, "Target 500 rename independence probe", () =>
            {
                target500.AssemblyTypeName = probeName;
                document.Regenerate();
            });

            bool isolated = string.Equals(source.AssemblyTypeName, sourceName, StringComparison.Ordinal) &&
                string.Equals(target501.AssemblyTypeName, target501Name, StringComparison.Ordinal) &&
                string.Equals(target500.AssemblyTypeName, probeName, StringComparison.Ordinal);
            if (!isolated)
                throw new InvalidOperationException("Renaming target 500 changed another assembly name.");

            RunTransaction(document, result, "Restore Target 500 visible name", () =>
            {
                target500.AssemblyTypeName = target500Name;
                document.Regenerate();
            });

            bool restored = string.Equals(source.AssemblyTypeName, sourceName, StringComparison.Ordinal) &&
                string.Equals(target500.AssemblyTypeName, target500Name, StringComparison.Ordinal) &&
                string.Equals(target501.AssemblyTypeName, target501Name, StringComparison.Ordinal);
            if (!restored)
                throw new InvalidOperationException("Assembly names were not restored after the rename probe.");

            result.RenameProbePassed = true;
            result.RenameProbeDetails = "Target 500 renamed and restored without changing the source or target 501.";
        }

        internal static void CaptureAndValidate(
            Document document,
            AssemblyInstance source,
            CreatedAssemblyTarget target500,
            CreatedAssemblyTarget target501,
            string target500Name,
            string target501Name,
            AssemblyDuplicationDiagnosticResult result)
        {
            result.SourceAfter = AssemblyEvidence.Capture(document, source);
            result.Target500After = AssemblyEvidence.Capture(document, target500.Assembly);
            result.Target501After = AssemblyEvidence.Capture(document, target501.Assembly);
            result.Target500Marker = target500.Marker.Evidence;
            result.Target501Marker = target501.Marker.Evidence;
            result.ProductionEvidenceObservations.Clear();

            AddChecks(result, ValidateSource(result.SourceBefore, result.SourceAfter));
            AddChecks(result, ValidateTarget(
                result.SourceBefore,
                result.Target500After,
                target500.CopiedProductionIds,
                target500.Marker.Evidence.MarkerId,
                target500Name,
                "Target 500",
                result,
                target500.Provenance));
            AddChecks(result, ValidateTarget(
                result.SourceBefore,
                result.Target501After,
                target501.CopiedProductionIds,
                target501.Marker.Evidence.MarkerId,
                target501Name,
                "Target 501",
                result,
                target501.Provenance));

            long sourceType = result.SourceAfter.TypeId;
            long target500Type = result.Target500After.TypeId;
            long target501Type = result.Target501After.TypeId;
            Add(result, "Pairwise type independence",
                sourceType != target500Type && sourceType != target501Type && target500Type != target501Type,
                "Source " + sourceType + ", target 500 " + target500Type + ", target 501 " + target501Type + ".");
        }

        internal static IEnumerable<AssemblyDuplicationInvariant> ValidateSource(
            AssemblyEvidence before,
            AssemblyEvidence after)
        {
            yield return Check("Source instance unchanged", before.InstanceId == after.InstanceId, "Source instance id remains unchanged.");
            yield return Check("Source type unchanged", before.TypeId == after.TypeId, "Source type id remains unchanged.");
            yield return Check("Source name unchanged", string.Equals(before.TypeName, after.TypeName, StringComparison.Ordinal), "Source type name remains unchanged.");
            yield return Check("Source members unchanged", MembersEqual(before, after), "Source member ids and owners remain unchanged.");
        }

        internal static IEnumerable<AssemblyDuplicationInvariant> ValidateTarget(
            AssemblyEvidence source,
            AssemblyEvidence target,
            IReadOnlyCollection<ElementId> copiedProductionIds,
            long markerId,
            string expectedName,
            string label,
            AssemblyDuplicationDiagnosticResult result,
            TargetMemberProvenance provenance = null,
            bool destinationLevelMode = false,
            double destinationDeltaZ = 0)
        {
            var expectedIds = new HashSet<long>(copiedProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
            List<HostDependentDependencyCandidateValidation> validatedDependencies = provenance == null
                ? new List<HostDependentDependencyCandidateValidation>()
                : provenance.HostedPipeInsulationRelationships
                    .SelectMany(relationship => relationship.ValidatedDependencies)
                    .Where(dependency => dependency.Passed)
                    .ToList();
            var validatedDependencyIds = new HashSet<long>(
                validatedDependencies.Select(dependency => dependency.TargetDependencyId));
            List<AssemblyMemberEvidence> targetPrimaryMembers = target.ProductionMembers
                .Where(member => expectedIds.Contains(member.MemberId))
                .ToList();
            List<AssemblyMemberEvidence> unclassifiedTargetMembers = target.ProductionMembers
                .Where(member => !expectedIds.Contains(member.MemberId) &&
                    !validatedDependencyIds.Contains(member.MemberId))
                .ToList();
            var actualPrimaryIds = new HashSet<long>(
                targetPrimaryMembers.Select(member => member.MemberId));
            bool noUnclassifiedMembers = unclassifiedTargetMembers.Count == 0;

            // Changed by Jhay: report primary, validated dependencies, and unclassified members separately.
            result.ProductionEvidenceObservations.Add("PRIMARY PRODUCTION");
            result.ProductionEvidenceObservations.Add(
                "Source count: " + source.ProductionMembers.Count);
            result.ProductionEvidenceObservations.Add(
                "Target primary count: " + targetPrimaryMembers.Count);
            result.ProductionEvidenceObservations.Add(
                source.ProductionMembers.Count == targetPrimaryMembers.Count &&
                expectedIds.SetEquals(actualPrimaryIds) && noUnclassifiedMembers
                    ? "PASS"
                    : "FAIL");
            result.ProductionEvidenceObservations.Add(
                "HOST-DEPENDENT PRODUCTION DEPENDENCIES");
            if (validatedDependencies.Count == 0)
            {
                result.ProductionEvidenceObservations.Add("<none>");
            }
            else
            {
                foreach (HostDependentDependencyCandidateValidation dependency in
                    validatedDependencies.OrderBy(item => item.TargetDependencyId))
                {
                    result.ProductionEvidenceObservations.Add(
                        dependency.SourceDependencyId + " -> " + dependency.TargetDependencyId +
                        " | host " + dependency.SourceHostId + " -> " +
                        dependency.TargetHostId + " | PASS");
                }
            }
            result.ProductionEvidenceObservations.Add("UNCLASSIFIED TARGET MEMBERS");
            result.ProductionEvidenceObservations.Add(
                unclassifiedTargetMembers.Count.ToString(CultureInfo.InvariantCulture));
            foreach (AssemblyMemberEvidence unclassified in unclassifiedTargetMembers)
            {
                string reason = FindUnclassifiedDependencyReason(
                    provenance,
                    unclassified.MemberId);
                result.ProductionEvidenceObservations.Add(
                    unclassified.MemberId + " | " + reason);
            }

            yield return Check(label + " name", string.Equals(target.TypeName, expectedName, StringComparison.Ordinal), "Visible assembly name matches the request.");
            yield return Check(
                label + " production count",
                source.ProductionMembers.Count == targetPrimaryMembers.Count && noUnclassifiedMembers,
                "Source primary count " + source.ProductionMembers.Count +
                ", target primary count " + targetPrimaryMembers.Count +
                ", validated dependencies " + validatedDependencies.Count +
                ", unclassified target members " + unclassifiedTargetMembers.Count + ".");
            yield return Check(label + " marker count", target.InternalMembers.Count == 1, "Exactly one internal identity marker belongs to the target.");
            yield return Check(label + " marker id", target.InternalMembers.Count == 1 && target.InternalMembers[0].MemberId == markerId, "The expected identity marker is the target's internal member.");
            yield return Check(
                label + " copied production ids",
                expectedIds.SetEquals(actualPrimaryIds) && noUnclassifiedMembers,
                "Target primary IDs match the requested copies; validated dependencies are separate and no unclassified target member remains.");
            bool primaryProductionEvidenceMatches = MemberEvidenceSetsMatch(
                source.ProductionMembers,
                targetPrimaryMembers,
                label,
                result,
                destinationLevelMode,
                destinationDeltaZ);
            bool dependencyEvidenceMatches = validatedDependencies.All(dependency => dependency.Passed) &&
                validatedDependencies.Select(dependency => dependency.TargetDependencyId).Distinct().Count() ==
                    validatedDependencies.Count &&
                validatedDependencyIds.All(id => target.ProductionMembers.Any(member => member.MemberId == id));
            bool productionEvidenceMatches = primaryProductionEvidenceMatches &&
                dependencyEvidenceMatches && noUnclassifiedMembers;
            yield return Check(
                label + " production evidence",
                productionEvidenceMatches,
                "Primary production evidence and every validated host-dependent dependency pass, with no unclassified target member.");
            yield return Check(label + " ownership", target.Members.All(member => member.OwnerAssemblyId == target.InstanceId), "Every target member belongs to the target assembly.");
        }

        private static string FindUnclassifiedDependencyReason(
            TargetMemberProvenance provenance,
            long memberId)
        {
            if (provenance == null)
                return "no dependency provenance is available";
            foreach (HostedPipeInsulationRelationshipEvidence relationship in
                provenance.HostedPipeInsulationRelationships)
            {
                string reason;
                if (relationship.UnclassifiedDependencyReasons.TryGetValue(memberId, out reason))
                    return reason;
            }
            return "not a validated host-dependent production dependency";
        }

        internal static void RequirePairwiseTypes(
            AssemblyInstance source,
            AssemblyInstance target500,
            AssemblyInstance target501)
        {
            long sourceType = RevitApiCompatibility.GetElementIdValue(source.GetTypeId());
            long target500Type = RevitApiCompatibility.GetElementIdValue(target500.GetTypeId());
            long target501Type = RevitApiCompatibility.GetElementIdValue(target501.GetTypeId());
            if (sourceType == target500Type || sourceType == target501Type || target500Type == target501Type)
            {
                throw new InvalidOperationException(
                    "Assembly types are not pairwise independent. Source " + sourceType +
                    ", target 500 " + target500Type + ", target 501 " + target501Type + ".");
            }
        }

        internal static void CaptureContaminationObservations(
            Document document,
            CreatedAssemblyTarget target500,
            CreatedAssemblyTarget target501,
            AssemblyDuplicationDiagnosticResult result)
        {
            CaptureMarkerGeometry(target500.Marker.Marker, "Target 500 model geometry", result);
            CaptureMarkerGeometry(target501.Marker.Marker, "Target 501 model geometry", result);

            List<View> normalViews = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(view => !view.IsTemplate && view.ViewType != ViewType.Schedule &&
                    RevitApiCompatibility.IsInvalidElementId(view.AssociatedAssemblyInstanceId))
                .Take(1)
                .ToList();
            CaptureViewVisibility(document, normalViews.FirstOrDefault(), target500.Marker.Marker.Id, "Normal project view", result);

            View assemblyView = new FilteredElementCollector(document)
                .OfClass(typeof(View))
                .Cast<View>()
                .FirstOrDefault(view => !view.IsTemplate &&
                    RevitApiCompatibility.GetElementIdValue(view.AssociatedAssemblyInstanceId) ==
                    RevitApiCompatibility.GetElementIdValue(target500.Assembly.Id));
            CaptureViewVisibility(document, assemblyView, target500.Marker.Marker.Id, "Target 500 assembly view", result);

            List<ViewSchedule> schedules = new FilteredElementCollector(document)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(schedule => !schedule.IsTemplate)
                .ToList();
            CaptureScheduleObservation(document, schedules, target500.Marker.Marker.Id, "Available schedules", result);

            result.ContaminationObservations.Add(new AssemblyContaminationObservation
            {
                Surface = "Selection/highlight",
                Result = "manual verification required after the command succeeds"
            });
            result.ContaminationObservations.Add(new AssemblyContaminationObservation
            {
                Surface = "Project Browser family/type entries",
                Result = "manual verification required after the command succeeds"
            });
        }

        private static void CaptureMarkerGeometry(
            FamilyInstance marker,
            string surface,
            AssemblyDuplicationDiagnosticResult result)
        {
            bool hasGeometry = false;
            using (var options = new Options())
            {
                GeometryElement geometry = marker.get_Geometry(options);
                hasGeometry = geometry != null && geometry.Cast<GeometryObject>().Any();
            }

            result.ContaminationObservations.Add(new AssemblyContaminationObservation
            {
                Surface = surface,
                Result = hasGeometry ? "geometry returned" : "no geometry returned"
            });
        }

        private static void CaptureViewVisibility(
            Document document,
            View view,
            ElementId markerId,
            string surface,
            AssemblyDuplicationDiagnosticResult result)
        {
            string observation;
            if (view == null)
            {
                observation = "not available in test model";
            }
            else
            {
                try
                {
                    bool returned = new FilteredElementCollector(document, view.Id)
                        .WhereElementIsNotElementType()
                        .ToElementIds()
                        .Any(id => RevitApiCompatibility.GetElementIdValue(id) ==
                            RevitApiCompatibility.GetElementIdValue(markerId));
                    observation = view.Name + ": marker " + (returned ? "returned" : "not returned") + " by view collector";
                }
                catch (Exception exception)
                {
                    observation = view.Name + ": collector unavailable (" + exception.Message + ")";
                }
            }

            result.ContaminationObservations.Add(new AssemblyContaminationObservation
            {
                Surface = surface,
                Result = observation
            });
        }

        private static void CaptureScheduleObservation(
            Document document,
            IReadOnlyCollection<ViewSchedule> schedules,
            ElementId markerId,
            string surface,
            AssemblyDuplicationDiagnosticResult result)
        {
            if (schedules.Count == 0)
            {
                result.ContaminationObservations.Add(new AssemblyContaminationObservation
                {
                    Surface = surface,
                    Result = "not available in test model"
                });
                return;
            }

            var returnedBy = new List<string>();
            foreach (ViewSchedule schedule in schedules)
            {
                try
                {
                    bool returned = new FilteredElementCollector(document, schedule.Id)
                        .WhereElementIsNotElementType()
                        .ToElementIds()
                        .Any(id => RevitApiCompatibility.GetElementIdValue(id) ==
                            RevitApiCompatibility.GetElementIdValue(markerId));
                    if (returned)
                        returnedBy.Add(schedule.Name);
                }
                catch
                {
                    // Some internal schedules do not support view-scoped collection.
                }
            }

            result.ContaminationObservations.Add(new AssemblyContaminationObservation
            {
                Surface = surface,
                Result = returnedBy.Count == 0
                    ? "marker not returned by available schedule collectors"
                    : "marker returned by: " + string.Join(", ", returnedBy)
            });
        }

        internal static void RunTransaction(
            Document document,
            AssemblyDuplicationDiagnosticResult result,
            string name,
            Action action)
        {
            using (var transaction = new Transaction(document, name))
            {
                TransactionStatus start = transaction.Start();
                if (start != TransactionStatus.Started)
                {
                    result.TransactionStages.Add(new AssemblyTransactionStage { Name = name, Status = start });
                    throw new InvalidOperationException(name + " did not start: " + start + ".");
                }
                AssemblyIdentityFailureHandling.Configure(transaction);

                try
                {
                    action();
                    TransactionStatus commit = transaction.Commit();
                    result.TransactionStages.Add(new AssemblyTransactionStage { Name = name, Status = commit });
                    if (commit != TransactionStatus.Committed)
                        throw new InvalidOperationException(name + " did not commit: " + commit + ".");
                }
                catch
                {
                    if (transaction.GetStatus() == TransactionStatus.Started)
                    {
                        TransactionStatus rollback = transaction.RollBack();
                        result.TransactionStages.Add(new AssemblyTransactionStage { Name = name + " rollback", Status = rollback });
                    }

                    throw;
                }
            }
        }

        internal static void ValidateArguments(
            Document document,
            AssemblyInstance source,
            string target500Name,
            string target501Name,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (source == null || !source.IsValidObject)
                throw new ArgumentException("Source assembly is unavailable.", nameof(source));
            if (string.IsNullOrWhiteSpace(target500Name))
                throw new ArgumentException("Target 500 assembly name is required.", nameof(target500Name));
            if (string.IsNullOrWhiteSpace(target501Name))
                throw new ArgumentException("Target 501 assembly name is required.", nameof(target501Name));
            if (string.Equals(target500Name, target501Name, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Target assembly names must be different.");
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
                throw new ArgumentException("The add-in assembly directory is required.", nameof(assemblyDirectory));
        }

        internal static void RejectNameConflicts(Document document, params string[] names)
        {
            var existing = new HashSet<string>(
                new FilteredElementCollector(document)
                    .OfClass(typeof(AssemblyInstance))
                    .Cast<AssemblyInstance>()
                    .Select(assembly => assembly.AssemblyTypeName),
                StringComparer.OrdinalIgnoreCase);
            string conflict = names.FirstOrDefault(existing.Contains);
            if (conflict != null)
                throw new InvalidOperationException("Assembly name '" + conflict + "' already exists.");
        }

        private static bool MembersEqual(AssemblyEvidence left, AssemblyEvidence right)
        {
            return left.Members.Select(MemberIdentity)
                .SequenceEqual(right.Members.Select(MemberIdentity));
        }

        private static string MemberIdentity(AssemblyMemberEvidence member)
        {
            return member.MemberId + ":" + member.OwnerAssemblyId + ":" +
                member.CategoryId + ":" + member.TypeId + ":" +
                member.PlacementFingerprint + ":" + member.ParameterFingerprint;
        }

        private static bool MemberEvidenceSetsMatch(
            IReadOnlyCollection<AssemblyMemberEvidence> expected,
            IReadOnlyCollection<AssemblyMemberEvidence> actual,
            string label,
            AssemblyDuplicationDiagnosticResult result,
            bool destinationLevelMode = false,
            double destinationDeltaZ = 0)
        {
            var remaining = actual.ToList();
            bool allPassed = true;
            foreach (AssemblyMemberEvidence member in expected)
            {
                AssemblyMemberEvidence match = remaining.FirstOrDefault(candidate =>
                    StructuralGeometryMatches(member, candidate) &&
                    RelativePlacementMatches(member, candidate));
                if (match == null)
                {
                    result.ProductionEvidenceObservations.Add(
                        label + " | " + member.MemberId + " -> <unmatched>");
                    List<AssemblyMemberEvidence> sameType = remaining
                        .Where(candidate =>
                            candidate.CategoryId == member.CategoryId &&
                            candidate.TypeId == member.TypeId)
                        .ToList();
                    if (sameType.Count == 0)
                    {
                        result.ProductionEvidenceObservations.Add(
                            "  Geometry validation: FAIL | no remaining target has CategoryId " +
                            member.CategoryId + " and TypeId " + member.TypeId);
                    }
                    else
                    {
                        foreach (AssemblyMemberEvidence candidate in sameType)
                        {
                            result.ProductionEvidenceObservations.Add(
                                "  Candidate " + candidate.MemberId +
                                " | Geometry validation: " +
                                (StructuralGeometryMatches(member, candidate) ? "PASS" : "FAIL") +
                                " | Placement validation: " +
                                (RelativePlacementMatches(member, candidate) ? "PASS" : "FAIL"));
                        }
                    }
                    result.ProductionEvidenceObservations.Add(
                        "  Meaningful parameter validation: NOT RUN");
                    allPassed = false;
                    continue;
                }

                result.ProductionEvidenceObservations.Add(
                    label + " | " + member.MemberId + " -> " + match.MemberId);
                result.ProductionEvidenceObservations.Add(
                    "  Geometry validation: PASS | CategoryId, TypeId, location kind, orientation, mirrored state, and geometry fingerprint match");
                result.ProductionEvidenceObservations.Add(
                    "  Placement validation: PASS | assembly-relative point/curve coordinates match");

                bool parametersMatch = MeaningfulParametersMatch(
                    member,
                    match,
                    result.ProductionEvidenceObservations,
                    destinationLevelMode,
                    destinationDeltaZ);
                result.ProductionEvidenceObservations.Add(
                    "  Meaningful parameter validation: " + (parametersMatch ? "PASS" : "FAIL"));
                if (!parametersMatch)
                    allPassed = false;

                remaining.Remove(match);
            }

            if (remaining.Count != 0)
            {
                result.ProductionEvidenceObservations.Add(
                    label + " | unmatched target production ids: " +
                    string.Join(", ", remaining.Select(member => member.MemberId)));
                allPassed = false;
            }

            return allPassed;
        }

        private static bool StructuralGeometryMatches(
            AssemblyMemberEvidence left,
            AssemblyMemberEvidence right)
        {
            if (left.CategoryId != right.CategoryId || left.TypeId != right.TypeId ||
                !string.Equals(left.LocationKind, right.LocationKind, StringComparison.Ordinal))
                return false;

            return string.Equals(
                left.PlacementFingerprint,
                right.PlacementFingerprint,
                StringComparison.Ordinal);
        }

        private static bool RelativePlacementMatches(
            AssemblyMemberEvidence left,
            AssemblyMemberEvidence right)
        {
            bool direct = CoordinatesMatch(left.RelativeX, right.RelativeX) &&
                CoordinatesMatch(left.RelativeY, right.RelativeY) &&
                CoordinatesMatch(left.RelativeZ, right.RelativeZ) &&
                CoordinatesMatch(left.RelativeEndX, right.RelativeEndX) &&
                CoordinatesMatch(left.RelativeEndY, right.RelativeEndY) &&
                CoordinatesMatch(left.RelativeEndZ, right.RelativeEndZ);
            if (direct || !left.RelativeEndX.HasValue || !right.RelativeEndX.HasValue)
                return direct;

            return CoordinatesMatch(left.RelativeX, right.RelativeEndX) &&
                CoordinatesMatch(left.RelativeY, right.RelativeEndY) &&
                CoordinatesMatch(left.RelativeZ, right.RelativeEndZ) &&
                CoordinatesMatch(left.RelativeEndX, right.RelativeX) &&
                CoordinatesMatch(left.RelativeEndY, right.RelativeY) &&
                CoordinatesMatch(left.RelativeEndZ, right.RelativeZ);
        }

        private static bool MeaningfulParametersMatch(
            AssemblyMemberEvidence source,
            AssemblyMemberEvidence target,
            IList<string> observations,
            bool destinationLevelMode = false,
            double destinationDeltaZ = 0)
        {
            bool passed = true;
            IEnumerable<long> parameterIds = source.ParameterValues.Keys
                .Concat(target.ParameterValues.Keys)
                .Distinct()
                .OrderBy(id => id);
            foreach (long parameterId in parameterIds)
            {
                string sourceValue;
                string targetValue;
                bool hasSource = source.ParameterValues.TryGetValue(parameterId, out sourceValue);
                bool hasTarget = target.ParameterValues.TryGetValue(parameterId, out targetValue);
                if (hasSource == hasTarget &&
                    string.Equals(sourceValue, targetValue, StringComparison.Ordinal))
                    continue;

                // Changed by Jhay: compare actual Double values before interpreting formatted evidence.
                double sourceDouble;
                double targetDouble;
                if (hasSource && hasTarget &&
                    source.DoubleParameterValues != null &&
                    target.DoubleParameterValues != null &&
                    source.DoubleParameterValues.TryGetValue(parameterId, out sourceDouble) &&
                    target.DoubleParameterValues.TryGetValue(parameterId, out targetDouble) &&
                    Math.Abs(sourceDouble - targetDouble) <= CoordinateTolerance)
                {
                    observations.Add(
                        "  " + parameterId + " | " + sourceValue + " | " + targetValue +
                        " | Equivalent numeric Double values within tolerance (actual source=" +
                        FormatCoordinate(sourceDouble) + ", target=" +
                        FormatCoordinate(targetDouble) + ")");
                    continue;
                }

                string classification = null;
                bool ignored = destinationLevelMode &&
                    parameterId == (long)BuiltInParameter.INSTANCE_ELEVATION_PARAM;
                if (ignored)
                    classification = "Ignored: expected absolute elevation change for destination level";
                // Changed by Jhay: pipe invert elevation is absolute and must move by exactly the level delta.
                else if (destinationLevelMode &&
                    parameterId == (long)BuiltInParameter.RBS_PIPE_INVERT_ELEVATION)
                {
                    ignored = TryClassifyExpectedPipeInvertElevationChange(
                        hasSource,
                        sourceValue,
                        hasTarget,
                        targetValue,
                        destinationDeltaZ,
                        out classification);
                }
                else
                    ignored = TryClassifyIgnoredCopyContextParameter(parameterId, out classification);
                if (!ignored)
                {
                    passed = false;
                    if (string.IsNullOrEmpty(classification))
                    {
                        classification = parameterId > 0
                            ? "Required: project/shared production parameter changed"
                            : "Unknown built-in parameter: requires review; hard failure";
                    }
                }

                observations.Add(
                    "  " + parameterId + " | " +
                    (hasSource ? sourceValue : "<missing>") + " | " +
                    (hasTarget ? targetValue : "<missing>") + " | " +
                    classification);
                // Changed by Jhay: emit the exact Revit metadata supporting each changed-parameter decision.
                observations.Add("    Source metadata: " + DescribeParameterMetadata(source, parameterId));
                observations.Add("    Target metadata: " + DescribeParameterMetadata(target, parameterId));
            }

            return passed;
        }

        private static bool TryClassifyExpectedPipeInvertElevationChange(
            bool hasSource,
            string sourceValue,
            bool hasTarget,
            string targetValue,
            double destinationDeltaZ,
            out string classification)
        {
            double sourceElevation;
            double targetElevation;
            if (!hasSource || !hasTarget ||
                !double.TryParse(sourceValue, NumberStyles.Float, CultureInfo.InvariantCulture, out sourceElevation) ||
                !double.TryParse(targetValue, NumberStyles.Float, CultureInfo.InvariantCulture, out targetElevation))
            {
                classification = "Required RBS_PIPE_INVERT_ELEVATION absolute-elevation validation failed: " +
                    "source and target must both expose numeric values; hard failure";
                return false;
            }

            double actualDelta = targetElevation - sourceElevation;
            if (Math.Abs(actualDelta - destinationDeltaZ) > CoordinateTolerance)
            {
                classification = "Required RBS_PIPE_INVERT_ELEVATION absolute-elevation validation failed: " +
                    "expected target-source=" + FormatCoordinate(destinationDeltaZ) +
                    ", actual=" + FormatCoordinate(actualDelta) + "; hard failure";
                return false;
            }

            classification = "Ignored for production equivalence only after strict validation: " +
                "RBS_PIPE_INVERT_ELEVATION is an absolute geometric elevation and target-source=" +
                FormatCoordinate(actualDelta) + " matches deltaZ=" + FormatCoordinate(destinationDeltaZ);
            return true;
        }

        private static string DescribeParameterMetadata(
            AssemblyMemberEvidence member,
            long parameterId)
        {
            AssemblyParameterMetadata metadata;
            return member.ParameterMetadata != null &&
                member.ParameterMetadata.TryGetValue(parameterId, out metadata)
                ? metadata.Describe()
                : "<parameter not exposed on member>";
        }

        private static bool TryClassifyIgnoredCopyContextParameter(
            long parameterId,
            out string classification)
        {
            if (parameterId == (long)BuiltInParameter.IFC_GUID)
            {
                classification = "Ignored: Revit-generated IFC GUID";
                return true;
            }
            if (parameterId == (long)BuiltInParameter.RBS_SECTION)
            {
                classification = "Ignored: Revit-generated MEP system section identifier";
                return true;
            }
            if (parameterId == (long)BuiltInParameter.RBS_SYSTEM_NAME_PARAM)
            {
                classification = "Ignored: Revit-generated MEP system instance/name context";
                return true;
            }

            classification = null;
            return false;
        }

        private static bool CoordinatesMatch(double? left, double? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue == right.HasValue;
            return Math.Abs(left.Value - right.Value) <= CoordinateTolerance;
        }

        internal static void AddChecks(
            AssemblyDuplicationDiagnosticResult result,
            IEnumerable<AssemblyDuplicationInvariant> checks)
        {
            foreach (AssemblyDuplicationInvariant check in checks)
                result.Invariants.Add(check);
        }

        internal static void Add(
            AssemblyDuplicationDiagnosticResult result,
            string name,
            bool passed,
            string details)
        {
            result.Invariants.Add(Check(name, passed, details));
        }

        private static AssemblyDuplicationInvariant Check(string name, bool passed, string details)
        {
            return new AssemblyDuplicationInvariant(name, passed, details);
        }

        internal sealed class CreatedAssemblyTarget
        {
            public AssemblyInstance Assembly { get; set; }
            public List<ElementId> CopiedProductionIds { get; set; }
            public AssemblyIdentityMarkerPlacement Marker { get; set; }
            public TargetMemberProvenance Provenance { get; set; }
        }

        internal sealed class TargetMemberProvenance
        {
            public HashSet<long> CopyReturnedIds { get; } = new HashSet<long>();
            public HashSet<long> SelectedProductionIds { get; } = new HashSet<long>();
            public HashSet<long> MarkerRelatedIds { get; } = new HashSet<long>();
            public HashSet<long> MembersImmediatelyAfterAssemblyCreate { get; } =
                new HashSet<long>();
            // Changed by Jhay: preserve fitting-hosted insulation snapshots across assembly creation.
            public IList<HostedPipeInsulationRelationshipEvidence> HostedPipeInsulationRelationships { get; } =
                new List<HostedPipeInsulationRelationshipEvidence>();
        }

        internal sealed class HostedPipeInsulationRelationshipEvidence
        {
            public long SourceHostId { get; set; }
            public long CopiedHostId { get; set; }
            public List<long> SourceDependentIds { get; set; } = new List<long>();
            public string SourceDependentError { get; set; }
            public List<HostedPipeInsulationEvidence> SourceInsulations { get; } =
                new List<HostedPipeInsulationEvidence>();
            public string SourceInsulationError { get; set; }
            public List<long> CopiedDependentIdsBefore { get; set; } = new List<long>();
            public string CopiedDependentErrorBefore { get; set; }
            public List<HostedPipeInsulationEvidence> CopiedInsulationsBefore { get; } =
                new List<HostedPipeInsulationEvidence>();
            public string CopiedInsulationErrorBefore { get; set; }
            public List<long> CopiedDependentIdsAfter { get; set; } = new List<long>();
            public string CopiedDependentErrorAfter { get; set; }
            public List<HostedPipeInsulationEvidence> CopiedInsulationsAfter { get; } =
                new List<HostedPipeInsulationEvidence>();
            public string CopiedInsulationErrorAfter { get; set; }
            public List<HostedPipeInsulationEvidence> GeneratedInsulations { get; } =
                new List<HostedPipeInsulationEvidence>();
            // Changed by Jhay: keep validated dependencies distinct from primary and unclassified members.
            public IList<HostDependentDependencyCandidateValidation> ValidatedDependencies { get; } =
                new List<HostDependentDependencyCandidateValidation>();
            public IDictionary<long, string> UnclassifiedDependencyReasons { get; } =
                new Dictionary<long, string>();
        }

        internal sealed class HostedPipeInsulationEvidence
        {
            public long Id { get; private set; }
            public long CategoryId { get; private set; }
            public long TypeId { get; private set; }
            public string TypeName { get; private set; }
            public double Thickness { get; private set; }
            public long HostId { get; private set; }
            public long AssemblyInstanceId { get; private set; }
            public bool IsSourceAssemblyMember { get; private set; }
            public XYZ BoundsMin { get; private set; }
            public XYZ BoundsMax { get; private set; }
            public long? EquivalentExternalSourceInsulationId { get; set; }

            public static HostedPipeInsulationEvidence Capture(
                Document document,
                PipeInsulation insulation,
                ISet<long> sourceAssemblyMembers)
            {
                GetWorldBounds(insulation, out XYZ boundsMin, out XYZ boundsMax);
                ElementType type = document.GetElement(insulation.GetTypeId()) as ElementType;
                long id = RevitApiCompatibility.GetElementIdValue(insulation.Id);
                return new HostedPipeInsulationEvidence
                {
                    Id = id,
                    CategoryId = insulation.Category == null
                        ? -1L
                        : RevitApiCompatibility.GetElementIdValue(insulation.Category.Id),
                    TypeId = RevitApiCompatibility.GetElementIdValue(insulation.GetTypeId()),
                    TypeName = type?.Name ?? "<none>",
                    Thickness = insulation.Thickness,
                    HostId = RevitApiCompatibility.GetElementIdValue(insulation.HostElementId),
                    AssemblyInstanceId = RevitApiCompatibility.GetElementIdValue(
                        insulation.AssemblyInstanceId),
                    IsSourceAssemblyMember = sourceAssemblyMembers != null &&
                        sourceAssemblyMembers.Contains(id),
                    BoundsMin = boundsMin,
                    BoundsMax = boundsMax
                };
            }

            public string Describe()
            {
                return "Id=" + Id +
                    " | CategoryId=" + CategoryId +
                    " | Type=" + TypeName + " (" + TypeId + ")" +
                    " | Thickness=" + FormatCoordinate(Thickness) +
                    " | HostId=" + HostId +
                    " | AssemblyInstanceId=" + AssemblyInstanceId +
                    " | InSourceAssembly=" + IsSourceAssemblyMember +
                    " | WorldBoundsMin=" + (BoundsMin == null ? "<none>" : FormatPoint(BoundsMin)) +
                    " | WorldBoundsMax=" + (BoundsMax == null ? "<none>" : FormatPoint(BoundsMax));
            }

            private static void GetWorldBounds(
                Element element,
                out XYZ worldMin,
                out XYZ worldMax)
            {
                worldMin = null;
                worldMax = null;
                BoundingBoxXYZ bounds = element.get_BoundingBox(null);
                if (bounds == null)
                    return;

                Transform transform = bounds.Transform ?? Transform.Identity;
                var corners = new List<XYZ>();
                foreach (double x in new[] { bounds.Min.X, bounds.Max.X })
                foreach (double y in new[] { bounds.Min.Y, bounds.Max.Y })
                foreach (double z in new[] { bounds.Min.Z, bounds.Max.Z })
                    corners.Add(transform.OfPoint(new XYZ(x, y, z)));

                worldMin = new XYZ(
                    corners.Min(point => point.X),
                    corners.Min(point => point.Y),
                    corners.Min(point => point.Z));
                worldMax = new XYZ(
                    corners.Max(point => point.X),
                    corners.Max(point => point.Y),
                    corners.Max(point => point.Z));
            }
        }

        internal sealed class HostDependentDependencyCandidateValidation
        {
            public long SourceHostId { get; set; }
            public long TargetHostId { get; set; }
            public long SourceDependencyId { get; set; }
            public long TargetDependencyId { get; set; }
            public IList<string> Failures { get; } = new List<string>();
            public bool Passed => Failures.Count == 0;
        }

        private sealed class CandidateCopy
        {
            public ElementId Id { get; set; }
            public LooseCopyGeometryEvidence Evidence { get; set; }
        }

        private sealed class CopyMatchOption
        {
            public CandidateCopy Candidate { get; set; }
            public double Score { get; set; }
        }

        private sealed class CopyGeometryComparison
        {
            public bool Accepted { get; private set; }
            public double Score { get; private set; }
            public string Reason { get; private set; }

            public static CopyGeometryComparison AcceptedMatch(double score)
            {
                return new CopyGeometryComparison
                {
                    Accepted = true,
                    Score = score,
                    Reason = "world geometry matches"
                };
            }

            public static CopyGeometryComparison Rejected(string reason)
            {
                return new CopyGeometryComparison
                {
                    Accepted = false,
                    Score = double.PositiveInfinity,
                    Reason = reason
                };
            }
        }

        private sealed class LooseCopyGeometryEvidence
        {
            public long Id { get; private set; }
            public long CategoryId { get; private set; }
            public string CategoryName { get; private set; }
            public long TypeId { get; private set; }
            public string TypeName { get; private set; }
            public bool IsIdentityMarker { get; private set; }
            public string LocationKind { get; private set; }
            public XYZ Start { get; private set; }
            public XYZ End { get; private set; }
            public Transform FamilyTransform { get; private set; }
            public bool? Mirrored { get; private set; }
            public XYZ BoundsCenter { get; private set; }
            public XYZ BoundsExtents { get; private set; }

            public static LooseCopyGeometryEvidence Capture(Element element)
            {
                Category category = element.Category;
                ElementId typeId = element.GetTypeId();
                ElementType elementType = RevitApiCompatibility.IsInvalidElementId(typeId)
                    ? null
                    : element.Document.GetElement(typeId) as ElementType;
                var familyInstance = element as FamilyInstance;
                var evidence = new LooseCopyGeometryEvidence
                {
                    Id = RevitApiCompatibility.GetElementIdValue(element.Id),
                    CategoryId = category == null
                        ? -1L
                        : RevitApiCompatibility.GetElementIdValue(category.Id),
                    CategoryName = category == null ? "<none>" : category.Name,
                    TypeId = RevitApiCompatibility.GetElementIdValue(typeId),
                    TypeName = elementType == null ? string.Empty : elementType.Name,
                    IsIdentityMarker = familyInstance?.Symbol?.Family != null &&
                        string.Equals(
                            familyInstance.Symbol.Family.Name,
                            AssemblyIdentityFamilyService.FamilyName,
                            StringComparison.Ordinal) &&
                        familyInstance.Symbol.Name.StartsWith("PS_ASM_ID_", StringComparison.Ordinal),
                    LocationKind = element.Location == null
                        ? "None"
                        : element.Location.GetType().Name
                };

                var point = element.Location as LocationPoint;
                if (point != null)
                    evidence.Start = point.Point;

                var curve = element.Location as LocationCurve;
                if (curve?.Curve != null)
                {
                    evidence.Start = curve.Curve.GetEndPoint(0);
                    evidence.End = curve.Curve.GetEndPoint(1);
                }

                if (familyInstance != null)
                {
                    evidence.FamilyTransform = familyInstance.GetTransform();
                    evidence.Mirrored = familyInstance.Mirrored;
                    if (evidence.Start == null)
                        evidence.Start = evidence.FamilyTransform.Origin;
                }

                BoundingBoxXYZ bounds = element.get_BoundingBox(null);
                if (bounds != null)
                {
                    evidence.BoundsCenter = (bounds.Min + bounds.Max) * 0.5;
                    evidence.BoundsExtents = bounds.Max - bounds.Min;
                }

                return evidence;
            }

            public string DescribeIdentity()
            {
                return Id + " " + CategoryName + " (" + CategoryId + ") type " +
                    TypeName + " (" + TypeId + ")";
            }

            public string DescribeWorldGeometry()
            {
                var parts = new List<string> { "LocationKind=" + LocationKind };
                if (Start != null)
                    parts.Add("Start=" + FormatPoint(Start));
                if (End != null)
                    parts.Add("End=" + FormatPoint(End));
                if (FamilyTransform != null)
                {
                    parts.Add("TransformOrigin=" + FormatPoint(FamilyTransform.Origin));
                    parts.Add("BasisX=" + FormatPoint(FamilyTransform.BasisX));
                    parts.Add("BasisY=" + FormatPoint(FamilyTransform.BasisY));
                    parts.Add("BasisZ=" + FormatPoint(FamilyTransform.BasisZ));
                    parts.Add("Mirrored=" + Mirrored);
                }
                if (BoundsCenter != null)
                {
                    parts.Add("BoundsCenter=" + FormatPoint(BoundsCenter));
                    parts.Add("BoundsExtents=" + FormatPoint(BoundsExtents));
                }
                return string.Join(", ", parts);
            }

            private static string FormatPoint(XYZ point)
            {
                return "(" + FormatCoordinate(point.X) + "," +
                    FormatCoordinate(point.Y) + "," +
                    FormatCoordinate(point.Z) + ")";
            }
        }
    }
}
