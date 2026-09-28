using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: executes one immutable batch atomically through the locked engine.
    internal static class AssemblyBatchDuplicationService
    {
        public static AssemblyBatchResult Execute(
            Document document,
            AssemblyBatchPlan confirmedPlan,
            string assemblyDirectory)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (confirmedPlan == null)
                throw new ArgumentNullException(nameof(confirmedPlan));

            var result = new AssemblyBatchResult
            {
                Plan = confirmedPlan
            };
            foreach (AssemblyBatchPlanItem item in confirmedPlan.Items)
            {
                result.Items.Add(new AssemblyBatchItemResult
                {
                    PlanItem = item,
                    Status = AssemblyBatchItemStatus.Planned
                });
            }

            Exception failure = null;
            AssemblyBatchRevalidationResult revalidation =
                AssemblyBatchPreflightService.Revalidate(
                    document,
                    confirmedPlan,
                    assemblyDirectory);
            foreach (AssemblyBatchPreflightIssue issue in revalidation.Issues)
                result.RevalidationIssues.Add(issue);

            if (!revalidation.IsValid)
            {
                result.Status = AssemblyBatchStatus.StalePlan;
                result.FailedStage = "confirmed-plan revalidation";
                result.FailureMessage = string.Join(
                    Environment.NewLine,
                    revalidation.Issues.Select(issue => issue.Message));
                SetNotStarted(result.Items);
                WriteReport(document, result, null);
                return result;
            }

            int currentIndex = -1;
            string currentStage = "start batch transaction group";
            using (var group = new TransactionGroup(document, "Duplicate Assemblies Batch"))
            {
                try
                {
                    TransactionStatus start = group.Start();
                    result.GroupStartStatus = start;
                    if (start != TransactionStatus.Started)
                    {
                        throw new InvalidOperationException(
                            "Duplicate Assemblies Batch transaction group did not start: " + start + ".");
                    }

                    currentStage = "resolve assembly identity family";
                    result.FamilyResolution = AssemblyIdentityFamilyService.ResolveOrLoad(
                        document,
                        assemblyDirectory);

                    var engine = new AssemblyDuplicationEngine();
                    var engineResults = new List<AssemblyDuplicationEngineResult>();
                    var documentationResults = new List<AssemblyDocumentationResult>();
                    for (currentIndex = 0; currentIndex < revalidation.ExecutionItems.Count; currentIndex++)
                    {
                        AssemblyBatchExecutionItem execution =
                            revalidation.ExecutionItems[currentIndex];
                        AssemblyBatchItemResult itemResult = result.Items[currentIndex];
                        currentStage = "duplicate " + execution.ConfirmedItem.SourceAssemblyName;
                        try
                        {
                            AssemblyDuplicationEngineResult engineResult = engine.Duplicate(
                                new AssemblyDuplicationEngineRequest(
                                    document,
                                    execution.Source,
                                    execution.SourceProductionMemberIds,
                                    execution.ConfirmedItem.TargetAssemblyName,
                                    execution.DestinationPlan,
                                    result.FamilyResolution.BaseSymbol,
                                    "Target " + execution.ConfirmedItem.AssignedNumber));
                            engineResults.Add(engineResult);
                            itemResult.TargetInstanceId = Id(engineResult.TargetAssembly.Id);
                            itemResult.TargetTypeId = Id(engineResult.TargetAssembly.GetTypeId());
                            itemResult.Evidence = engineResult.Evidence;
                            currentStage = "duplicate documentation for " +
                                execution.ConfirmedItem.SourceAssemblyName;
                            AssemblyDocumentationResult documentationResult =
                                AssemblyDocumentationDuplicationService.Duplicate(
                                    document,
                                    execution.Source,
                                    engineResult.TargetAssembly,
                                    execution.ConfirmedItem.Documentation,
                                    engineResult.SourceToTargetProductionMemberIds);
                            documentationResults.Add(documentationResult);
                            itemResult.Status = AssemblyBatchItemStatus.Succeeded;
                            itemResult.Documentation = documentationResult;
                        }
                        catch (AssemblyDuplicationEngineException engineException)
                        {
                            itemResult.Status = AssemblyBatchItemStatus.Failed;
                            itemResult.FailedStage = engineException.Stage;
                            itemResult.FailureMessage = engineException.Message;
                            itemResult.Evidence = engineException.Evidence;
                            throw;
                        }
                        catch (AssemblyDocumentationException documentationException)
                        {
                            itemResult.Status = AssemblyBatchItemStatus.Failed;
                            itemResult.FailedStage = documentationException.Stage;
                            itemResult.FailureMessage = documentationException.Message;
                            itemResult.Documentation = new AssemblyDocumentationResult(
                                new Dictionary<long, long>(),
                                null,
                                documentationException.Evidence);
                            throw;
                        }
                    }

                    currentStage = "validate complete batch";
                    ValidateCompleteBatch(
                        document,
                        confirmedPlan,
                        engineResults,
                        documentationResults);

                    currentStage = "assimilate batch transaction group";
                    TransactionStatus assimilated = group.Assimilate();
                    result.GroupFinalStatus = assimilated;
                    if (assimilated != TransactionStatus.Committed)
                    {
                        throw new InvalidOperationException(
                            "Duplicate Assemblies Batch transaction group did not assimilate: " +
                            assimilated + ".");
                    }

                    result.Status = AssemblyBatchStatus.Succeeded;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    result.Status = AssemblyBatchStatus.RolledBack;
                    var engineFailure = exception as AssemblyDuplicationEngineException;
                    var documentationFailure = exception as AssemblyDocumentationException;
                    result.FailedStage = engineFailure?.Stage ??
                        documentationFailure?.Stage ?? currentStage;
                    result.FailureMessage = exception.Message;

                    TransactionStatus status = group.GetStatus();
                    if (status == TransactionStatus.Started)
                    {
                        try
                        {
                            status = group.RollBack();
                        }
                        catch (Exception rollbackException)
                        {
                            result.FailureMessage += Environment.NewLine +
                                "Batch rollback could not be confirmed: " + rollbackException.Message;
                            status = group.GetStatus();
                        }
                    }
                    result.GroupFinalStatus = status;
                    MarkRollbackStatuses(result.Items, currentIndex);
                }
            }

            WriteReport(document, result, failure);
            return result;
        }

        private static void ValidateCompleteBatch(
            Document document,
            AssemblyBatchPlan plan,
            IReadOnlyCollection<AssemblyDuplicationEngineResult> engineResults,
            IReadOnlyCollection<AssemblyDocumentationResult> documentationResults)
        {
            if (engineResults.Count != plan.Items.Count)
                throw new InvalidOperationException("The completed target count does not match the confirmed batch plan.");
            if (documentationResults.Count != plan.Items.Count)
                throw new InvalidOperationException("The completed documentation count does not match the confirmed batch plan.");

            var instanceIds = new HashSet<long>();
            var typeIds = new HashSet<long>();
            for (int index = 0; index < plan.Items.Count; index++)
            {
                AssemblyBatchPlanItem item = plan.Items[index];
                AssemblyDuplicationEngineResult engineResult = engineResults.ElementAt(index);
                AssemblyInstance target = engineResult.TargetAssembly;
                if (target == null || !target.IsValidObject)
                    throw new InvalidOperationException("Target '" + item.TargetAssemblyName + "' is unavailable before commit.");
                if (!instanceIds.Add(Id(target.Id)))
                    throw new InvalidOperationException("The batch produced a duplicate target AssemblyInstance id.");
                if (!typeIds.Add(Id(target.GetTypeId())))
                    throw new InvalidOperationException("The batch produced a shared target AssemblyType id.");
                if (!string.Equals(target.AssemblyTypeName, item.TargetAssemblyName, StringComparison.Ordinal))
                    throw new InvalidOperationException("Target assembly name changed before batch commit.");

                AssemblyInstance source = document.GetElement(
                    RevitApiCompatibility.CreateElementId(item.SourceAssemblyId)) as AssemblyInstance;
                if (source == null || !source.IsValidObject)
                    throw new InvalidOperationException("Source assembly became unavailable before batch commit.");
            }
        }

        private static void MarkRollbackStatuses(
            IList<AssemblyBatchItemResult> items,
            int currentIndex)
        {
            for (int index = 0; index < items.Count; index++)
            {
                AssemblyBatchItemResult item = items[index];
                if (index < currentIndex && item.Status == AssemblyBatchItemStatus.Succeeded)
                    item.Status = AssemblyBatchItemStatus.RolledBack;
                else if (index == currentIndex && item.Status == AssemblyBatchItemStatus.Planned)
                    item.Status = AssemblyBatchItemStatus.Failed;
                else if (index > currentIndex && item.Status == AssemblyBatchItemStatus.Planned)
                    item.Status = AssemblyBatchItemStatus.NotStarted;
                else if (currentIndex < 0 && item.Status == AssemblyBatchItemStatus.Planned)
                    item.Status = AssemblyBatchItemStatus.NotStarted;
            }
        }

        private static void SetNotStarted(IEnumerable<AssemblyBatchItemResult> items)
        {
            foreach (AssemblyBatchItemResult item in items)
                item.Status = AssemblyBatchItemStatus.NotStarted;
        }

        private static void WriteReport(
            Document document,
            AssemblyBatchResult result,
            Exception failure)
        {
            try
            {
                result.ReportPath = AssemblyBatchReport.Write(document, result, failure);
            }
            catch (Exception reportException)
            {
                result.ReportPath = string.Empty;
                result.FailureMessage = (result.FailureMessage ?? string.Empty) +
                    Environment.NewLine + "The batch report could not be written: " +
                    reportException.Message;
            }
        }

        private static long Id(ElementId id) => RevitApiCompatibility.GetElementIdValue(id);
    }
}
