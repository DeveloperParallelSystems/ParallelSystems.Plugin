using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: shared, transaction-group-neutral execution of the validated duplication core.
    internal sealed partial class AssemblyDuplicationEngine
    {
        public AssemblyDuplicationEngineResult Duplicate(AssemblyDuplicationEngineRequest request)
        {
            ValidateRequest(request);

            AssemblyInstance source = request.Source;
            var working = new AssemblyDuplicationDiagnosticResult
            {
                SourceBefore = AssemblyEvidence.Capture(request.Document, source)
            };
            AppendDestinationPlanEvidence(working, request.DestinationPlan);
            string stage = "initialize duplication";

            try
            {
                stage = request.StagePrefix + " copy, marker, and assembly creation";
                CreatedAssemblyTarget target = CreateTarget(
                        request.Document,
                        source,
                        request.SourceProductionMemberIds,
                        request.IdentityMarkerBaseSymbol,
                        request.TargetName,
                        request.StagePrefix,
                        working,
                        request.DestinationPlan);

                stage = "Regenerate destination-level target";
                RunTransaction(
                    request.Document,
                    working,
                    stage,
                    request.Document.Regenerate);

                stage = "capture post-movement evidence";
                working.SourceAfter = AssemblyEvidence.Capture(request.Document, source);
                working.Target500After = AssemblyEvidence.Capture(request.Document, target.Assembly);
                working.Target500Marker = target.Marker.Evidence;

                stage = "validate pipe insulation relationships";
                AssemblyDestinationLevelService.ValidatePipeInsulationRelationships(
                    request.Document,
                    request.SourceProductionMemberIds,
                    target.CopiedProductionIds,
                    target.Assembly,
                    request.DestinationPlan,
                    working.DestinationLevelObservations);

                stage = "validate production evidence";
                working.ProductionEvidenceObservations.Clear();
                // Changed by Jhay: preserve strict failure while identifying any Revit-added members.
                AppendUnexpectedTargetMemberDiagnostics(
                    request.Document,
                    target,
                    working.ProductionEvidenceObservations);
                AddChecks(
                    working,
                    ValidateSource(
                        working.SourceBefore,
                        working.SourceAfter));
                AddChecks(
                    working,
                    ValidateTarget(
                        working.SourceBefore,
                        working.Target500After,
                        target.CopiedProductionIds,
                        target.Marker.Evidence.MarkerId,
                        request.TargetName,
                        request.StagePrefix,
                        working,
                        target.Provenance,
                        true,
                        request.DestinationPlan.DeltaZ));

                long sourceType = working.SourceAfter.TypeId;
                long targetType = working.Target500After.TypeId;
                Add(
                    working,
                    "Independent target type",
                    sourceType != targetType,
                    "Source " + sourceType + ", target " + targetType + ".");

                double expectedTargetOffset = request.DestinationPlan.TargetTransform.Origin.Z -
                    request.DestinationPlan.DestinationLevel.ProjectElevation;
                Add(
                    working,
                    "Target level and assembly offset",
                    Math.Abs(expectedTargetOffset - request.DestinationPlan.SourceAssemblyOffset) <=
                        CoordinateTolerance &&
                    target.Marker.Evidence.LevelId == RevitApiCompatibility.GetElementIdValue(
                        request.DestinationPlan.DestinationLevel.Id) &&
                    Math.Abs(target.Marker.Evidence.Offset - request.DestinationPlan.SourceAssemblyOffset) <=
                        CoordinateTolerance,
                    "Destination " + request.DestinationPlan.DestinationLevel.Name +
                    ", source offset " + FormatCoordinate(
                        request.DestinationPlan.SourceAssemblyOffset) +
                    ", target offset " + FormatCoordinate(
                        target.Marker.Evidence.Offset) + ".");

                // Changed by Jhay: preserve the locked aggregate-validation and rollback contract.
                stage = "aggregate validation";
                foreach (AssemblyDuplicationInvariant invariant in working.Invariants)
                {
                    request.DestinationPlan.Validation.Check(
                        invariant.Passed,
                        RevitApiCompatibility.GetElementIdValue(source.Id),
                        RevitApiCompatibility.GetElementIdValue(target.Assembly.Id),
                        "assembly invariant: " + invariant.Name,
                        invariant.Details,
                        working.DestinationLevelObservations);
                }
                request.DestinationPlan.Validation.AppendSummary(working.DestinationLevelObservations);
                request.DestinationPlan.Validation.ThrowIfFailed();

                return new AssemblyDuplicationEngineResult
                {
                    TargetAssembly = target.Assembly,
                    CopiedProductionMemberIds = target.CopiedProductionIds.AsReadOnly(),
                    SourceToTargetProductionMemberIds = new System.Collections.ObjectModel.ReadOnlyDictionary<long, long>(
                        request.SourceProductionMemberIds
                            .Select((sourceId, index) => new
                            {
                                Source = RevitApiCompatibility.GetElementIdValue(sourceId),
                                Target = RevitApiCompatibility.GetElementIdValue(
                                    target.CopiedProductionIds[index])
                            })
                            .ToDictionary(item => item.Source, item => item.Target)),
                    Marker = target.Marker.Evidence,
                    SourceBefore = working.SourceBefore,
                    SourceAfter = working.SourceAfter,
                    TargetAfter = working.Target500After,
                    Evidence = CaptureEvidence(working)
                };
            }
            catch (Exception exception)
            {
                throw new AssemblyDuplicationEngineException(
                    RevitApiCompatibility.GetElementIdValue(source.Id),
                    source.AssemblyTypeName,
                    request.TargetName,
                    stage,
                    CaptureEvidence(working),
                    exception);
            }
        }

        internal static void AppendDestinationPlanEvidence(
            AssemblyDuplicationDiagnosticResult result,
            AssemblyDestinationLevelPlan plan)
        {
            result.DestinationLevelObservations.Add(
                "Source level: " + plan.SourceLevel.Name + " (" +
                FormatCoordinate(plan.SourceLevel.ProjectElevation) + ")");
            result.DestinationLevelObservations.Add(
                "Destination level: " + plan.DestinationLevel.Name + " (" +
                FormatCoordinate(plan.DestinationLevel.ProjectElevation) + ")");
            result.DestinationLevelObservations.Add(
                "Source assembly offset: " +
                FormatCoordinate(plan.SourceAssemblyOffset) +
                " | deltaZ: " + FormatCoordinate(plan.DeltaZ));
            foreach (string observation in plan.Observations)
                result.DestinationLevelObservations.Add(observation);
        }

        private static void ValidateRequest(AssemblyDuplicationEngineRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));
            if (request.Document == null)
                throw new ArgumentException("Document is unavailable.", nameof(request));
            if (request.Document.IsModifiable)
                throw new InvalidOperationException("The duplication engine must be called between its short phase transactions.");
            if (request.Source == null || !request.Source.IsValidObject)
                throw new ArgumentException("Source assembly is unavailable.", nameof(request));
            if (request.SourceProductionMemberIds == null || request.SourceProductionMemberIds.Count == 0)
                throw new ArgumentException("The source assembly has no production members.", nameof(request));
            foreach (ElementId memberId in request.SourceProductionMemberIds)
            {
                Element member = memberId == null ? null : request.Document.GetElement(memberId);
                if (member == null || !member.IsValidObject)
                {
                    throw new ArgumentException(
                        "A source production member is unavailable.",
                        nameof(request));
                }
            }
            if (string.IsNullOrWhiteSpace(request.TargetName))
                throw new ArgumentException("Target assembly name is required.", nameof(request));
            if (request.DestinationPlan == null)
                throw new ArgumentException("Destination-level plan is required.", nameof(request));
            if (request.IdentityMarkerBaseSymbol == null || !request.IdentityMarkerBaseSymbol.IsValidObject)
                throw new ArgumentException("Identity marker base symbol is unavailable.", nameof(request));
            if (string.IsNullOrWhiteSpace(request.StagePrefix))
                throw new ArgumentException("Stage prefix is required.", nameof(request));
        }

        internal static AssemblyDuplicationExecutionEvidence CaptureEvidence(
            AssemblyDuplicationDiagnosticResult result)
        {
            var evidence = new AssemblyDuplicationExecutionEvidence();
            Copy(result.TransactionStages, evidence.TransactionStages);
            Copy(result.CopyMatchingObservations, evidence.CopyMatchingObservations);
            Copy(result.TransformAlignmentObservations, evidence.TransformAlignmentObservations);
            Copy(result.DestinationLevelObservations, evidence.DestinationLevelObservations);
            Copy(result.ProductionEvidenceObservations, evidence.ProductionEvidenceObservations);
            Copy(result.Invariants, evidence.Invariants);
            return evidence;
        }

        private static void Copy<T>(IEnumerable<T> source, IList<T> destination)
        {
            foreach (T item in source)
                destination.Add(item);
        }
    }

    // Created by Jhay: the 93-check Revit diagnostic remains an adapter over the shared engine.
    internal static class AssemblyDuplicationDiagnosticAdapter
    {
        public static AssemblyDuplicationDiagnosticResult RunSingleToLevel(
            Document document,
            AssemblyInstance source,
            string targetName,
            Level destinationLevel,
            string assemblyDirectory)
        {
            ValidateArguments(document, source, targetName, destinationLevel, assemblyDirectory);
            AssemblyDuplicationEngine.RejectNameConflicts(document, targetName);

            List<ElementId> sourceProductionIds = source.GetMemberIds().ToList();
            if (sourceProductionIds.Count == 0)
                throw new InvalidOperationException("The source assembly has no production members.");

            AssemblyDestinationLevelPlan plan = AssemblyDestinationLevelService.CreatePlan(
                document,
                source,
                sourceProductionIds,
                destinationLevel);
            var result = new AssemblyDuplicationDiagnosticResult
            {
                SourceBefore = AssemblyEvidence.Capture(document, source)
            };
            AssemblyDuplicationEngine.AppendDestinationPlanEvidence(result, plan);

            Exception failure = null;
            using (var group = new TransactionGroup(document, "Assembly Destination Level POC"))
            {
                TransactionStatus start = group.Start();
                result.TransactionStages.Add(new AssemblyTransactionStage
                {
                    Name = "Transaction group start",
                    Status = start
                });
                if (start != TransactionStatus.Started)
                    throw new InvalidOperationException(
                        "Assembly destination-level transaction group did not start: " + start + ".");

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

                    var engine = new AssemblyDuplicationEngine();
                    AssemblyDuplicationEngineResult engineResult = engine.Duplicate(
                        new AssemblyDuplicationEngineRequest(
                            document,
                            source,
                            sourceProductionIds,
                            targetName,
                            plan,
                            result.FamilyResolution.BaseSymbol,
                            "Target 500"));
                    ApplyEngineResult(result, engineResult);

                    result.Succeeded = true;
                    result.Summary = "One independent assembly was duplicated to " +
                        plan.DestinationLevel.Name + ".";
                    result.Details = "Production geometry moved by the exact level delta and retained " +
                        "its source-relative placement and offsets.";

                    TransactionStatus assimilated = group.Assimilate();
                    result.TransactionStages.Add(new AssemblyTransactionStage
                    {
                        Name = "Transaction group assimilate",
                        Status = assimilated
                    });
                    if (assimilated != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Assembly destination-level transaction group did not assimilate: " +
                            assimilated + ".");
                    }
                }
                catch (Exception exception)
                {
                    failure = exception;
                    var engineFailure = exception as AssemblyDuplicationEngineException;
                    if (engineFailure != null)
                        ApplyEvidence(result, engineFailure.Evidence);

                    result.Succeeded = false;
                    result.Details = exception.Message;
                    TransactionStatus status = group.GetStatus();
                    if (status == TransactionStatus.Started)
                    {
                        status = group.RollBack();
                        result.TransactionStages.Add(new AssemblyTransactionStage
                        {
                            Name = "Transaction group rollback",
                            Status = status
                        });
                    }

                    result.Summary = status == TransactionStatus.RolledBack
                        ? "The destination-level duplicate was rolled back."
                        : "The destination-level duplicate failed; rollback was not confirmed (group status " +
                          status + ").";
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

        private static void ApplyEngineResult(
            AssemblyDuplicationDiagnosticResult result,
            AssemblyDuplicationEngineResult engineResult)
        {
            result.SourceBefore = engineResult.SourceBefore;
            result.SourceAfter = engineResult.SourceAfter;
            result.Target500After = engineResult.TargetAfter;
            result.Target500Marker = engineResult.Marker;
            ApplyEvidence(result, engineResult.Evidence);
        }

        private static void ApplyEvidence(
            AssemblyDuplicationDiagnosticResult result,
            AssemblyDuplicationExecutionEvidence evidence)
        {
            foreach (AssemblyTransactionStage stage in evidence.TransactionStages)
                result.TransactionStages.Add(stage);
            Replace(result.CopyMatchingObservations, evidence.CopyMatchingObservations);
            Replace(result.TransformAlignmentObservations, evidence.TransformAlignmentObservations);
            Replace(result.DestinationLevelObservations, evidence.DestinationLevelObservations);
            Replace(result.ProductionEvidenceObservations, evidence.ProductionEvidenceObservations);
            Replace(result.Invariants, evidence.Invariants);
        }

        private static void Replace<T>(IList<T> destination, IEnumerable<T> source)
        {
            destination.Clear();
            foreach (T item in source)
                destination.Add(item);
        }

        private static void ValidateArguments(
            Document document,
            AssemblyInstance source,
            string targetName,
            Level destinationLevel,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (source == null || !source.IsValidObject)
                throw new ArgumentException("Source assembly is unavailable.", nameof(source));
            if (string.IsNullOrWhiteSpace(targetName))
                throw new ArgumentException("Target assembly name is required.", nameof(targetName));
            if (destinationLevel == null || !destinationLevel.IsValidObject)
                throw new ArgumentException("Destination level is unavailable.", nameof(destinationLevel));
            if (string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                throw new ArgumentException(
                    "The add-in assembly directory is required.",
                    nameof(assemblyDirectory));
            }
        }
    }
}
