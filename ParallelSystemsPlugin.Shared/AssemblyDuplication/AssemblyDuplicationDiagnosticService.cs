using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal static class AssemblyDuplicationDiagnosticService
    {
        private const double CoordinateTolerance = 1e-6;

        public static AssemblyDuplicationDiagnosticResult Run(
            Document document,
            AssemblyInstance source,
            string target500Name,
            string target501Name,
            string assemblyDirectory)
        {
            ValidateArguments(document, source, target500Name, target501Name, assemblyDirectory);
            RejectNameConflicts(document, target500Name, target501Name);

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

                    CreatedAssemblyTarget target500 = CreateTarget(
                        document,
                        source,
                        sourceProductionIds,
                        result.FamilyResolution.BaseSymbol,
                        target500Name,
                        "Target 500",
                        result);
                    CreatedAssemblyTarget target501 = CreateTarget(
                        document,
                        source,
                        sourceProductionIds,
                        result.FamilyResolution.BaseSymbol,
                        target501Name,
                        "Target 501",
                        result);

                    RequirePairwiseTypes(source, target500.Assembly, target501.Assembly);
                    RunRenameProbe(
                        document,
                        source,
                        target500.Assembly,
                        target501.Assembly,
                        target500Name,
                        target501Name,
                        result);

                    CaptureAndValidate(
                        document,
                        source,
                        target500,
                        target501,
                        target500Name,
                        target501Name,
                        result);

                    RunTransaction(document, result, "Regenerate Assembly Identity POC", () =>
                    {
                        document.Regenerate();
                    });

                    CaptureAndValidate(
                        document,
                        source,
                        target500,
                        target501,
                        target500Name,
                        target501Name,
                        result);
                    CaptureContaminationObservations(document, target500, target501, result);

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

        private static CreatedAssemblyTarget CreateTarget(
            Document document,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionMemberIds,
            FamilySymbol baseMarkerSymbol,
            string targetName,
            string stagePrefix,
            AssemblyDuplicationDiagnosticResult result)
        {
            AssemblyInstance target = null;
            AssemblyIdentityMarkerPlacement marker = null;
            List<ElementId> copiedProductionIds = null;

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
                if (allCopiedIds.Count == 0)
                    throw new InvalidOperationException(stagePrefix + " returned no copied elements.");

                copiedProductionIds = SelectCopiedProductionIds(
                    document,
                    source,
                    sourceProductionMemberIds,
                    allCopiedIds);
                RemoveUnmatchedCopyResults(document, copiedProductionIds, allCopiedIds, stagePrefix);

                foreach (ElementId copiedId in copiedProductionIds)
                {
                    Element copiedElement = document.GetElement(copiedId);
                    if (copiedElement == null)
                        throw new InvalidOperationException(stagePrefix + " lost a requested production copy.");
                    if (!RevitApiCompatibility.IsInvalidElementId(copiedElement.AssemblyInstanceId))
                        throw new InvalidOperationException(stagePrefix + " copied element already belongs to an assembly.");
                }

                marker = AssemblyIdentityFamilyService.CreateMarker(
                    document,
                    baseMarkerSymbol,
                    targetName,
                    source,
                    sourceProductionMemberIds);

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

                target = AssemblyInstance.Create(document, combinedIds, source.NamingCategoryId);
                if (target == null)
                    throw new InvalidOperationException(stagePrefix + " assembly creation returned null.");
                document.Regenerate();
            });

            if (target == null || !target.IsValidObject)
                throw new InvalidOperationException(stagePrefix + " target is unavailable after commit.");
            if (RevitApiCompatibility.GetElementIdValue(target.GetTypeId()) ==
                RevitApiCompatibility.GetElementIdValue(source.GetTypeId()))
            {
                throw new InvalidOperationException(
                    stagePrefix + " still shares the source AssemblyType despite its unique identity marker.");
            }

            RunTransaction(document, result, stagePrefix + " visible name", () =>
            {
                target.AssemblyTypeName = targetName;
                document.Regenerate();
            });

            target = document.GetElement(target.Id) as AssemblyInstance;
            if (target == null || !target.IsValidObject)
                throw new InvalidOperationException(stagePrefix + " target is unavailable after naming.");

            return new CreatedAssemblyTarget
            {
                Assembly = target,
                CopiedProductionIds = copiedProductionIds,
                Marker = marker
            };
        }

        private static List<ElementId> SelectCopiedProductionIds(
            Document document,
            AssemblyInstance source,
            IReadOnlyList<ElementId> sourceProductionIds,
            IReadOnlyCollection<ElementId> copiedIds)
        {
            XYZ sourceOrigin = source.GetTransform().Origin;
            var available = copiedIds
                .Select(id => document.GetElement(id))
                .Where(element => element != null)
                .Select(element => new CandidateCopy
                {
                    Id = element.Id,
                    Evidence = AssemblyMemberEvidence.Capture(element, sourceOrigin)
                })
                .OrderBy(candidate => RevitApiCompatibility.GetElementIdValue(candidate.Id))
                .ToList();
            var selected = new List<ElementId>();

            foreach (ElementId sourceId in sourceProductionIds)
            {
                Element sourceElement = document.GetElement(sourceId);
                if (sourceElement == null)
                    throw new InvalidOperationException("A source production member is unavailable while matching copies.");

                AssemblyMemberEvidence sourceEvidence = AssemblyMemberEvidence.Capture(sourceElement, sourceOrigin);
                CandidateCopy match = available.FirstOrDefault(candidate =>
                    MemberShapeMatches(sourceEvidence, candidate.Evidence));
                if (match == null)
                {
                    throw new InvalidOperationException(
                        "Revit did not return a production copy matching source member " +
                        RevitApiCompatibility.GetElementIdValue(sourceId) +
                        " (" + sourceEvidence.CategoryName + ", type " + sourceEvidence.TypeId + ").");
                }

                selected.Add(match.Id);
                available.Remove(match);
            }

            return selected;
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

        private static void RunRenameProbe(
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

        private static void CaptureAndValidate(
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

            AddChecks(result, ValidateSource(result.SourceBefore, result.SourceAfter));
            AddChecks(result, ValidateTarget(
                result.SourceBefore,
                result.Target500After,
                target500.CopiedProductionIds,
                target500.Marker.Evidence.MarkerId,
                target500Name,
                "Target 500"));
            AddChecks(result, ValidateTarget(
                result.SourceBefore,
                result.Target501After,
                target501.CopiedProductionIds,
                target501.Marker.Evidence.MarkerId,
                target501Name,
                "Target 501"));

            long sourceType = result.SourceAfter.TypeId;
            long target500Type = result.Target500After.TypeId;
            long target501Type = result.Target501After.TypeId;
            Add(result, "Pairwise type independence",
                sourceType != target500Type && sourceType != target501Type && target500Type != target501Type,
                "Source " + sourceType + ", target 500 " + target500Type + ", target 501 " + target501Type + ".");
        }

        private static IEnumerable<AssemblyDuplicationInvariant> ValidateSource(
            AssemblyEvidence before,
            AssemblyEvidence after)
        {
            yield return Check("Source instance unchanged", before.InstanceId == after.InstanceId, "Source instance id remains unchanged.");
            yield return Check("Source type unchanged", before.TypeId == after.TypeId, "Source type id remains unchanged.");
            yield return Check("Source name unchanged", string.Equals(before.TypeName, after.TypeName, StringComparison.Ordinal), "Source type name remains unchanged.");
            yield return Check("Source members unchanged", MembersEqual(before, after), "Source member ids and owners remain unchanged.");
        }

        private static IEnumerable<AssemblyDuplicationInvariant> ValidateTarget(
            AssemblyEvidence source,
            AssemblyEvidence target,
            IReadOnlyCollection<ElementId> copiedProductionIds,
            long markerId,
            string expectedName,
            string label)
        {
            var expectedIds = new HashSet<long>(copiedProductionIds.Select(RevitApiCompatibility.GetElementIdValue));
            var actualProductionIds = new HashSet<long>(target.ProductionMembers.Select(member => member.MemberId));

            yield return Check(label + " name", string.Equals(target.TypeName, expectedName, StringComparison.Ordinal), "Visible assembly name matches the request.");
            yield return Check(label + " production count", source.ProductionMembers.Count == target.ProductionMembers.Count, "Production-member count matches the source.");
            yield return Check(label + " marker count", target.InternalMembers.Count == 1, "Exactly one internal identity marker belongs to the target.");
            yield return Check(label + " marker id", target.InternalMembers.Count == 1 && target.InternalMembers[0].MemberId == markerId, "The expected identity marker is the target's internal member.");
            yield return Check(label + " copied production ids", expectedIds.SetEquals(actualProductionIds), "Target production ids match the selected requested copies.");
            yield return Check(label + " production evidence", MemberEvidenceSetsMatch(source.ProductionMembers, target.ProductionMembers), "Production category, type, and relative location match the source.");
            yield return Check(label + " ownership", target.Members.All(member => member.OwnerAssemblyId == target.InstanceId), "Every target member belongs to the target assembly.");
        }

        private static void RequirePairwiseTypes(
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

        private static void CaptureContaminationObservations(
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

        private static void RunTransaction(
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

        private static void ValidateArguments(
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

        private static void RejectNameConflicts(Document document, params string[] names)
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
            IReadOnlyCollection<AssemblyMemberEvidence> actual)
        {
            var remaining = actual.ToList();
            foreach (AssemblyMemberEvidence member in expected)
            {
                AssemblyMemberEvidence match = remaining.FirstOrDefault(candidate => MemberShapeMatches(member, candidate));
                if (match == null)
                    return false;
                remaining.Remove(match);
            }

            return remaining.Count == 0;
        }

        private static bool MemberShapeMatches(AssemblyMemberEvidence left, AssemblyMemberEvidence right)
        {
            if (left.CategoryId != right.CategoryId || left.TypeId != right.TypeId ||
                !string.Equals(left.LocationKind, right.LocationKind, StringComparison.Ordinal))
                return false;

            if (!string.Equals(left.PlacementFingerprint, right.PlacementFingerprint, StringComparison.Ordinal) ||
                !string.Equals(left.ParameterFingerprint, right.ParameterFingerprint, StringComparison.Ordinal))
                return false;

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

        private static bool CoordinatesMatch(double? left, double? right)
        {
            if (!left.HasValue || !right.HasValue)
                return left.HasValue == right.HasValue;
            return Math.Abs(left.Value - right.Value) <= CoordinateTolerance;
        }

        private static void AddChecks(
            AssemblyDuplicationDiagnosticResult result,
            IEnumerable<AssemblyDuplicationInvariant> checks)
        {
            foreach (AssemblyDuplicationInvariant check in checks)
                result.Invariants.Add(check);
        }

        private static void Add(
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

        private sealed class CreatedAssemblyTarget
        {
            public AssemblyInstance Assembly { get; set; }
            public List<ElementId> CopiedProductionIds { get; set; }
            public AssemblyIdentityMarkerPlacement Marker { get; set; }
        }

        private sealed class CandidateCopy
        {
            public ElementId Id { get; set; }
            public AssemblyMemberEvidence Evidence { get; set; }
        }
    }
}
