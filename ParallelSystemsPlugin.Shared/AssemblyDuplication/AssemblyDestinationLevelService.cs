using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: bounded destination-level support for the single-assembly POC.
    internal sealed class AssemblyDestinationLevelPlan
    {
        public Level SourceLevel { get; set; }
        public Level DestinationLevel { get; set; }
        public double DeltaZ { get; set; }
        public double SourceAssemblyOffset { get; set; }
        public Transform TargetTransform { get; set; }
        // Changed by Jhay: retain typed insulation-host evidence for post-copy validation.
        public IList<PipeInsulationHostPlan> PipeInsulations { get; } =
            new List<PipeInsulationHostPlan>();
        public IList<string> Observations { get; } = new List<string>();
        // Changed by Jhay: aggregate strict destination validations before one rollback-triggering failure.
        public DestinationValidationCollector Validation { get; } =
            new DestinationValidationCollector();
    }

    internal sealed class DestinationValidationCollector
    {
        private readonly List<string> failures = new List<string>();

        public int PassCount { get; private set; }
        public IReadOnlyList<string> Failures => failures;

        public void Check(
            bool passed,
            long sourceId,
            long targetId,
            string check,
            string details,
            IList<string> observations)
        {
            string line = sourceId + " -> " + targetId + " | " + check + " | " + details;
            if (passed)
            {
                PassCount++;
                observations.Add("VALIDATION PASS | " + line);
            }
            else
            {
                failures.Add(line);
                observations.Add("VALIDATION FAIL | " + line);
            }
        }

        public void AppendSummary(IList<string> observations)
        {
            observations.Add("VALIDATION SUMMARY");
            observations.Add("PASS: " + PassCount);
            observations.Add("FAIL: " + failures.Count);
            foreach (string failure in failures)
                observations.Add("FAIL | " + failure);
        }

        public void ThrowIfFailed()
        {
            if (failures.Count > 0)
            {
                throw new InvalidOperationException(
                    "Destination-level validation collected " + failures.Count +
                    " failure(s). See VALIDATION SUMMARY for every source -> target failure.");
            }
        }
    }

    internal sealed class PipeInsulationHostPlan
    {
        public long SourceInsulationId { get; set; }
        public long SourceHostId { get; set; }
        public long SourceTypeId { get; set; }
        public double SourceThickness { get; set; }
        public long DirectReferenceLevelId { get; set; }
        public long ElementLevelId { get; set; }
        public long EffectiveLevelId { get; set; }
    }

    internal static class AssemblyDestinationLevelService
    {
        private const double Tolerance = 1e-6;

        public static AssemblyDestinationLevelPlan CreatePlan(
            Document document,
            AssemblyInstance source,
            IReadOnlyCollection<ElementId> sourceMemberIds,
            Level destinationLevel)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (source == null || !source.IsValidObject)
                throw new ArgumentException("The source assembly is unavailable.", nameof(source));
            if (destinationLevel == null || !destinationLevel.IsValidObject)
                throw new ArgumentException("The destination level is unavailable.", nameof(destinationLevel));

            var plan = new AssemblyDestinationLevelPlan();
            var sourceMemberValues = new HashSet<long>(sourceMemberIds.Select(Id));
            var resolved = new List<Level>();
            foreach (ElementId id in sourceMemberIds)
            {
                Element member = document.GetElement(id);
                if (member == null)
                    throw new InvalidOperationException("Source production member " + Id(id) + " is unavailable.");

                EnsureSupported(member);
                // Changed by Jhay: capture the source fitting API/parameter evidence before behavior changes.
                if (IsPipeFitting(member))
                    AppendLevelParameterDiagnostics(document, member, "Source", plan.Observations);
                Level level = ResolveReferenceLevel(
                    document,
                    member,
                    sourceMemberValues,
                    plan);
                if (level == null)
                {
                    throw new InvalidOperationException(
                        "Production member " + Id(member.Id) + " (" + CategoryName(member) +
                        ") has no safely resolvable reference level.");
                }

                resolved.Add(level);
            }

            List<Level> distinct = resolved
                .GroupBy(level => Id(level.Id))
                .Select(group => group.First())
                .ToList();
            if (distinct.Count != 1)
            {
                throw new InvalidOperationException(
                    "The selected assembly uses multiple production reference levels (" +
                    string.Join(", ", distinct.Select(level => level.Name)) +
                    "). This bounded test requires one unambiguous source reference level.");
            }

            Level sourceLevel = distinct[0];
            double deltaZ = destinationLevel.ProjectElevation - sourceLevel.ProjectElevation;
            Transform sourceTransform = source.GetTransform();
            Transform targetTransform = Transform.CreateTranslation(new XYZ(0, 0, deltaZ))
                .Multiply(sourceTransform);

            plan.SourceLevel = sourceLevel;
            plan.DestinationLevel = destinationLevel;
            plan.DeltaZ = deltaZ;
            plan.SourceAssemblyOffset = sourceTransform.Origin.Z - sourceLevel.ProjectElevation;
            plan.TargetTransform = targetTransform;
            return plan;
        }

        public static void MoveAndReassociate(
            Document document,
            IReadOnlyList<ElementId> sourceIds,
            IReadOnlyList<ElementId> targetIds,
            AssemblyDestinationLevelPlan plan,
            IList<string> observations)
        {
            if (sourceIds.Count != targetIds.Count)
                throw new InvalidOperationException("Source/copy production-member counts differ before destination-level movement.");

            var sourceEvidence = new Dictionary<long, WorldEvidence>();
            var sourceMemberValues = new HashSet<long>(sourceIds.Select(Id));
            var targetMemberValues = new HashSet<long>(targetIds.Select(Id));
            var sourceConnections = new Dictionary<long, ConnectorEvidence>();
            var networkDrivenTargetIds = new HashSet<long>();
            var targetBySource = new Dictionary<long, ElementId>();
            for (int index = 0; index < sourceIds.Count; index++)
                targetBySource[Id(sourceIds[index])] = targetIds[index];

            var insulationEvidenceByTarget = new Dictionary<long, PipeInsulationMoveEvidence>();
            for (int index = 0; index < sourceIds.Count; index++)
            {
                Element source = document.GetElement(sourceIds[index]);
                Element target = document.GetElement(targetIds[index]);
                EnsureSupported(target);
                sourceEvidence[Id(target.Id)] = WorldEvidence.Capture(source);
                sourceConnections[Id(source.Id)] = ConnectorEvidence.Capture(source, sourceMemberValues);
                if (IsPipeFitting(target))
                {
                    AppendLevelParameterDiagnostics(document, target, "Copied before move", observations);
                    AppendFittingStageEvidence(document, target, "before any level changes", observations);
                    if (!CanSetReferenceLevel(target))
                        networkDrivenTargetIds.Add(Id(target.Id));
                }
            }

            foreach (PipeInsulationHostPlan insulationPlan in plan.PipeInsulations)
            {
                var sourceInsulation = document.GetElement(
                    RevitApiCompatibility.CreateElementId(insulationPlan.SourceInsulationId)) as PipeInsulation;
                var targetInsulation = document.GetElement(
                    targetBySource[insulationPlan.SourceInsulationId]) as PipeInsulation;
                Element sourceHost = document.GetElement(
                    RevitApiCompatibility.CreateElementId(insulationPlan.SourceHostId));
                Element targetHost = document.GetElement(targetBySource[insulationPlan.SourceHostId]);
                PipeInsulationMoveEvidence evidence = PipeInsulationMoveEvidence.Capture(
                    document,
                    sourceInsulation,
                    targetInsulation,
                    sourceHost,
                    targetHost);
                insulationEvidenceByTarget[Id(targetInsulation.Id)] = evidence;
                evidence.AppendStage(document, targetInsulation, targetHost, "before any level changes", plan.DeltaZ, observations);
            }

            XYZ translation = new XYZ(0, 0, plan.DeltaZ);
            // Changed by Jhay: reassociate writable members before the one common physical translation.
            for (int index = 0; index < targetIds.Count; index++)
            {
                Element target = document.GetElement(targetIds[index]);
                if (target is PipeInsulation)
                    continue;

                bool referenceLevelWasSet = TrySetReferenceLevel(
                    target,
                    plan.DestinationLevel,
                    observations);
                document.Regenerate();

                Level appliedLevel = ResolvePrimaryReferenceLevel(document, target);
                if (referenceLevelWasSet &&
                    (appliedLevel == null || Id(appliedLevel.Id) != Id(plan.DestinationLevel.Id)))
                {
                    throw new InvalidOperationException(
                        "Production member " + Id(target.Id) + " did not accept destination level '" +
                        plan.DestinationLevel.Name + "'.");
                }

                if (!referenceLevelWasSet && !IsPipeFitting(target))
                    throw new InvalidOperationException("Only a copied Pipe Fitting may use network-driven destination placement.");

                observations.Add(
                    "LEVEL REASSOCIATION | member " + Id(sourceIds[index]) + " -> " + Id(target.Id) +
                    " | " + CategoryName(target) + " | " +
                    (referenceLevelWasSet
                        ? "writable reference level set to " + plan.DestinationLevel.Name
                        : "no writable direct level; connector-driven association retained") +
                    " | exposed level " + (appliedLevel == null ? "<unavailable>" : appliedLevel.Name));

                LogAllPipeFittingStages(
                    document,
                    targetIds,
                    "after reassociating copied member " + Id(target.Id),
                    observations);
            }

            LogAllPipeFittingStages(document, targetIds, "before common network translation", observations);
            LogAllPipeInsulationStages(
                document,
                plan,
                targetBySource,
                insulationEvidenceByTarget,
                "before common network translation",
                observations);
            ElementTransformUtils.MoveElements(document, targetIds.ToList(), translation);
            LogAllPipeFittingStages(
                document,
                targetIds,
                "after common network translation, before explicit regenerate",
                observations);
            LogAllPipeInsulationStages(
                document,
                plan,
                targetBySource,
                insulationEvidenceByTarget,
                "after common network translation, before explicit regenerate",
                observations);
            document.Regenerate();
            LogAllPipeFittingStages(document, targetIds, "after common network translation and regenerate", observations);
            LogAllPipeInsulationStages(
                document,
                plan,
                targetBySource,
                insulationEvidenceByTarget,
                "after common network translation and regenerate",
                observations);

            // Changed by Jhay: insulation remains the one copy made by CopyElements and follows its copied host.
            foreach (PipeInsulationHostPlan insulationPlan in plan.PipeInsulations)
            {
                ElementId targetInsulationId = targetBySource[insulationPlan.SourceInsulationId];
                ElementId expectedTargetHostId = targetBySource[insulationPlan.SourceHostId];
                var targetInsulation = document.GetElement(targetInsulationId) as PipeInsulation;
                if (targetInsulation == null)
                    throw new InvalidOperationException("The copied Pipe Insulation is unavailable after movement.");
                plan.Validation.Check(
                    Id(targetInsulation.HostElementId) == Id(expectedTargetHostId),
                    insulationPlan.SourceInsulationId,
                    Id(targetInsulation.Id),
                    "copied insulation host",
                    "actual " + Id(targetInsulation.HostElementId) +
                    ", expected copied host " + Id(expectedTargetHostId),
                    observations);

                Element targetHost = document.GetElement(expectedTargetHostId);
                if (targetHost == null)
                    throw new InvalidOperationException("The expected copied Pipe Insulation host is unavailable after movement.");
                Level effectiveLevel = ResolvePrimaryReferenceLevel(document, targetHost);
                plan.Validation.Check(
                    effectiveLevel != null && Id(effectiveLevel.Id) == Id(plan.DestinationLevel.Id),
                    insulationPlan.SourceInsulationId,
                    Id(targetInsulation.Id),
                    "effective host destination level",
                    effectiveLevel == null
                        ? "copied host exposes no effective level"
                        : "actual '" + effectiveLevel.Name + "', expected '" + plan.DestinationLevel.Name + "'",
                    observations);

                // Changed by Jhay: raw PipeInsulation.LocationCurve is diagnostic-only; validate host and bounds in world space.
                PipeInsulationMoveEvidence moveEvidence = insulationEvidenceByTarget[Id(targetInsulation.Id)];
                moveEvidence.ValidateFinal(
                    document,
                    targetInsulation,
                    targetHost,
                    translation,
                    plan.DestinationLevel,
                    plan.Validation,
                    observations);

                observations.Add(
                    "Pipe Insulation " + insulationPlan.SourceInsulationId + " -> " +
                    Id(targetInsulation.Id) + " | copied host " + insulationPlan.SourceHostId + " -> " +
                    Id(expectedTargetHostId) + " | effective destination level " +
                    (effectiveLevel == null ? "<unavailable>" : effectiveLevel.Name) +
                    " | expected world delta (0, 0, " + Format(plan.DeltaZ) + ")");
            }

            foreach (ElementId targetId in targetIds)
            {
                Element target = document.GetElement(targetId);
                if (IsPipeFitting(target))
                    AppendLevelParameterDiagnostics(document, target, "Copied final", observations);
            }

            // Changed by Jhay: validate the final regenerated state after every network member was processed.
            document.Regenerate();
            for (int index = 0; index < sourceIds.Count; index++)
            {
                Element source = document.GetElement(sourceIds[index]);
                Element target = document.GetElement(targetIds[index]);
                if (source == null || target == null)
                    throw new InvalidOperationException("A production member is unavailable during final destination-level validation.");
                long sourceId = Id(source.Id);
                long targetId = Id(target.Id);
                bool categoryTypeMatch = Id(source.Category.Id) == Id(target.Category.Id) &&
                    Id(source.GetTypeId()) == Id(target.GetTypeId());
                plan.Validation.Check(
                    categoryTypeMatch,
                    sourceId,
                    targetId,
                    "category/type",
                    "source category/type " + Id(source.Category.Id) + "/" + Id(source.GetTypeId()) +
                    ", target " + Id(target.Category.Id) + "/" + Id(target.GetTypeId()),
                    observations);

                WorldEvidence expected = WorldEvidence.Capture(source).Translated(translation);
                WorldEvidence actual = WorldEvidence.Capture(target);
                if (!(target is PipeInsulation))
                {
                    plan.Validation.Check(
                        expected.Matches(actual),
                        sourceId,
                        targetId,
                        "exact world XYZ delta",
                        "expected " + expected.Describe() + " | actual " + actual.Describe(),
                        observations);
                }

                List<string> connectivityFailures = GetMappedConnectivityFailures(
                    source,
                    target,
                    sourceConnections[Id(source.Id)],
                    sourceIds,
                    targetIds,
                    targetMemberValues);
                foreach (string connectivityFailure in connectivityFailures)
                {
                    plan.Validation.Check(
                        false,
                        sourceId,
                        targetId,
                        "connector topology",
                        connectivityFailure,
                        observations);
                }
                if (connectivityFailures.Count == 0)
                {
                    plan.Validation.Check(true, sourceId, targetId, "connector topology", "mapped connector graph preserved", observations);
                }

                if (!(target is PipeInsulation))
                    ValidateFinalOffset(source, target, plan, observations);

                if (!(target is PipeInsulation))
                {
                    Level effectiveLevel = ResolvePrimaryReferenceLevel(document, target);
                    bool levelPassed = effectiveLevel == null
                        ? networkDrivenTargetIds.Contains(targetId)
                        : Id(effectiveLevel.Id) == Id(plan.DestinationLevel.Id);
                    plan.Validation.Check(
                        levelPassed,
                        sourceId,
                        targetId,
                        "effective destination level",
                        effectiveLevel == null
                            ? "unavailable; network-driven=" + networkDrivenTargetIds.Contains(targetId)
                            : "actual '" + effectiveLevel.Name + "', expected '" + plan.DestinationLevel.Name + "'",
                        observations);
                }
            }

            // Changed by Jhay: do not create the marker/assembly when any pre-assembly validation failed.
            if (plan.Validation.Failures.Count > 0)
            {
                plan.Validation.AppendSummary(observations);
                plan.Validation.ThrowIfFailed();
            }
        }

        // Changed by Jhay: verify source and copied insulation identity/hosting after target assembly creation.
        public static void ValidatePipeInsulationRelationships(
            Document document,
            IReadOnlyList<ElementId> sourceIds,
            IReadOnlyList<ElementId> targetIds,
            AssemblyInstance targetAssembly,
            AssemblyDestinationLevelPlan plan,
            IList<string> observations)
        {
            var targetBySource = new Dictionary<long, ElementId>();
            for (int index = 0; index < sourceIds.Count; index++)
                targetBySource[Id(sourceIds[index])] = targetIds[index];

            foreach (PipeInsulationHostPlan insulationPlan in plan.PipeInsulations)
            {
                var source = document.GetElement(
                    RevitApiCompatibility.CreateElementId(insulationPlan.SourceInsulationId)) as PipeInsulation;
                var target = document.GetElement(targetBySource[insulationPlan.SourceInsulationId]) as PipeInsulation;
                ElementId expectedTargetHostId = targetBySource[insulationPlan.SourceHostId];
                Element targetHost = document.GetElement(expectedTargetHostId);
                if (source == null || target == null || targetHost == null)
                    throw new InvalidOperationException("Pipe Insulation host validation cannot continue because required elements are unavailable.");
                long sourceId = Id(source.Id);
                long targetId = Id(target.Id);
                plan.Validation.Check(
                    Id(source.HostElementId) == insulationPlan.SourceHostId,
                    sourceId,
                    targetId,
                    "source insulation host unchanged",
                    "actual " + Id(source.HostElementId) + ", expected " + insulationPlan.SourceHostId,
                    observations);
                plan.Validation.Check(
                    Id(target.HostElementId) == Id(expectedTargetHostId),
                    sourceId,
                    targetId,
                    "copied insulation host",
                    "actual " + Id(target.HostElementId) + ", expected " + Id(expectedTargetHostId),
                    observations);
                plan.Validation.Check(
                    Id(source.GetTypeId()) == insulationPlan.SourceTypeId &&
                    Id(target.GetTypeId()) == insulationPlan.SourceTypeId,
                    sourceId,
                    targetId,
                    "insulation type",
                    "source " + Id(source.GetTypeId()) + ", target " + Id(target.GetTypeId()) +
                    ", expected " + insulationPlan.SourceTypeId,
                    observations);
                plan.Validation.Check(
                    Math.Abs(source.Thickness - insulationPlan.SourceThickness) <= Tolerance &&
                    Math.Abs(target.Thickness - insulationPlan.SourceThickness) <= Tolerance,
                    sourceId,
                    targetId,
                    "insulation thickness",
                    "source " + Format(source.Thickness) + ", target " + Format(target.Thickness) +
                    ", expected " + Format(insulationPlan.SourceThickness),
                    observations);
                plan.Validation.Check(
                    Id(targetHost.AssemblyInstanceId) == Id(targetAssembly.Id),
                    sourceId,
                    targetId,
                    "copied host target ownership",
                    "host assembly " + Id(targetHost.AssemblyInstanceId) +
                    ", target assembly " + Id(targetAssembly.Id),
                    observations);

                Level effectiveLevel = ResolvePrimaryReferenceLevel(document, targetHost);
                plan.Validation.Check(
                    effectiveLevel != null && Id(effectiveLevel.Id) == Id(plan.DestinationLevel.Id),
                    sourceId,
                    targetId,
                    "copied host destination level",
                    effectiveLevel == null
                        ? "unavailable"
                        : "actual '" + effectiveLevel.Name + "', expected '" + plan.DestinationLevel.Name + "'",
                    observations);

                observations.Add(
                    "Pipe Insulation validation | source " + insulationPlan.SourceInsulationId +
                    " host " + insulationPlan.SourceHostId + " unchanged | target " + Id(target.Id) +
                    " host " + Id(expectedTargetHostId) + " | same type/thickness | target ownership/effective level: PASS");
            }
        }

        private static void EnsureSupported(Element element)
        {
            if (element is Pipe || element is PipeInsulation)
                return;

            var family = element as FamilyInstance;
            if (family != null && element.Category != null &&
                Id(element.Category.Id) == (long)BuiltInCategory.OST_PipeFitting &&
                family.Host == null)
                return;

            throw new InvalidOperationException(
                "Production member " + Id(element.Id) + " (" + CategoryName(element) +
                ", " + element.GetType().Name +
                ") is outside the safely supported destination-level categories for this test.");
        }

        private static Level ResolveReferenceLevel(
            Document document,
            Element element,
            ISet<long> sourceMemberIds,
            AssemblyDestinationLevelPlan plan)
        {
            var insulation = element as PipeInsulation;
            if (insulation != null)
            {
                ElementId hostId = insulation.HostElementId;
                if (RevitApiCompatibility.IsInvalidElementId(hostId))
                    throw new InvalidOperationException("Pipe Insulation " + Id(insulation.Id) + " has an invalid HostElementId.");

                Element host = document.GetElement(hostId);
                if (host == null)
                    throw new InvalidOperationException("Pipe Insulation " + Id(insulation.Id) + " host " + Id(hostId) + " is unavailable.");
                if (!sourceMemberIds.Contains(Id(hostId)))
                {
                    throw new InvalidOperationException(
                        "Pipe Insulation " + Id(insulation.Id) + " host " + Id(hostId) +
                        " is not one of the selected source production members.");
                }
                EnsureSupportedInsulationHost(host);
                Level hostLevel = ResolvePrimaryReferenceLevel(document, host);
                if (hostLevel == null)
                {
                    throw new InvalidOperationException(
                        "Pipe Insulation " + Id(insulation.Id) + " host " + Id(hostId) +
                        " has no safely resolvable reference level.");
                }

                Level directLevel = insulation.ReferenceLevel;
                plan.PipeInsulations.Add(new PipeInsulationHostPlan
                {
                    SourceInsulationId = Id(insulation.Id),
                    SourceHostId = Id(hostId),
                    SourceTypeId = Id(insulation.GetTypeId()),
                    SourceThickness = insulation.Thickness,
                    DirectReferenceLevelId = directLevel == null ? -1L : Id(directLevel.Id),
                    ElementLevelId = RevitApiCompatibility.IsInvalidElementId(insulation.LevelId)
                        ? -1L
                        : Id(insulation.LevelId),
                    EffectiveLevelId = Id(hostLevel.Id)
                });
                plan.Observations.Add(
                    "Source Pipe Insulation " + Id(insulation.Id) + " | HostElementId " + Id(hostId) +
                    " | host " + CategoryName(host) + " / " + host.GetType().Name +
                    " | direct ReferenceLevel " + (directLevel == null ? "<null>" : directLevel.Name) +
                    " | LevelId " + (RevitApiCompatibility.IsInvalidElementId(insulation.LevelId)
                        ? "<invalid>"
                        : Id(insulation.LevelId).ToString(CultureInfo.InvariantCulture)) +
                    " | effective host level " + hostLevel.Name + ".");
                return hostLevel;
            }

            return ResolvePrimaryReferenceLevel(document, element);
        }

        private static Level ResolvePrimaryReferenceLevel(Document document, Element element)
        {
            if (element == null || element is PipeInsulation)
                return null;

            var curve = element as MEPCurve;
            if (curve != null)
                return curve.ReferenceLevel;

            foreach (BuiltInParameter id in LevelParameters())
            {
                Parameter parameter = element.get_Parameter(id);
                if (parameter == null || parameter.StorageType != StorageType.ElementId)
                    continue;
                Level level = document.GetElement(parameter.AsElementId()) as Level;
                if (level != null)
                    return level;
            }

            return document.GetElement(element.LevelId) as Level;
        }

        private static bool TrySetReferenceLevel(
            Element element,
            Level destination,
            IList<string> observations)
        {
            if (element is PipeInsulation)
                throw new InvalidOperationException("Pipe Insulation level association must be resolved through its host.");

            var curve = element as MEPCurve;
            if (curve != null)
            {
                curve.ReferenceLevel = destination;
                return true;
            }

            foreach (BuiltInParameter id in LevelParameters())
            {
                Parameter parameter = element.get_Parameter(id);
                if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.ElementId)
                    continue;
                if (parameter.Set(destination.Id))
                    return true;
            }

            if (IsPipeFitting(element))
            {
                observations.Add(
                    "Pipe fitting " + Id(element.Id) +
                    " exposes no writable supported reference-level parameter; " +
                    "it will retain its copied connector topology and use the common network translation.");
                return false;
            }

            throw new InvalidOperationException(
                "Production member " + Id(element.Id) + " has no writable supported reference-level parameter.");
        }

        private static bool CanSetReferenceLevel(Element element)
        {
            if (element is MEPCurve)
                return true;
            return LevelParameters().Any(id =>
            {
                Parameter parameter = element.get_Parameter(id);
                return parameter != null && !parameter.IsReadOnly &&
                    parameter.StorageType == StorageType.ElementId;
            });
        }

        private static IEnumerable<BuiltInParameter> LevelParameters()
        {
            yield return BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM;
            yield return BuiltInParameter.FAMILY_LEVEL_PARAM;
            yield return BuiltInParameter.SCHEDULE_LEVEL_PARAM;
        }

        // Changed by Jhay: log every level/elevation/offset parameter and FamilyInstance context.
        private static void AppendLevelParameterDiagnostics(
            Document document,
            Element element,
            string phase,
            IList<string> observations)
        {
            var family = element as FamilyInstance;
            string host = family?.Host == null
                ? "<none>"
                : Id(family.Host.Id) + " " + CategoryName(family.Host) + " / " + family.Host.GetType().Name;
            string placement = family?.Symbol?.Family == null
                ? "<not a FamilyInstance>"
                : family.Symbol.Family.FamilyPlacementType.ToString();
            observations.Add(
                "LEVEL PARAMETER DIAGNOSTICS | " + phase + " | element " + Id(element.Id) +
                " | " + CategoryName(element) + " / " + element.GetType().Name +
                " | LevelId " + DescribeElementId(document, element.LevelId) +
                " | FamilyInstance host " + host + " | family placement " + placement + ".");

            List<Parameter> parameters = element.Parameters
                .Cast<Parameter>()
                .Where(IsLevelRelatedParameter)
                .OrderBy(parameter => Id(parameter.Id))
                .ToList();
            if (parameters.Count == 0)
            {
                observations.Add("  <no level/elevation/offset parameters exposed>");
                return;
            }

            foreach (Parameter parameter in parameters)
            {
                long parameterId = Id(parameter.Id);
                observations.Add(
                    "  Name='" + parameter.Definition.Name + "'" +
                    " | Id=" + parameterId +
                    " | BuiltInParameter=" + DescribeBuiltInParameter(parameterId) +
                    " | Value=" + DescribeParameterValue(document, parameter) +
                    " | StorageType=" + parameter.StorageType +
                    " | IsReadOnly=" + parameter.IsReadOnly);
            }
        }

        private static bool IsLevelRelatedParameter(Parameter parameter)
        {
            string name = parameter?.Definition?.Name ?? string.Empty;
            if (name.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("elevation", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("offset", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            long id = Id(parameter.Id);
            return LevelParameters().Any(item => id == (long)item) ||
                id == (long)BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM ||
                id == (long)BuiltInParameter.INSTANCE_ELEVATION_PARAM;
        }

        private static string DescribeBuiltInParameter(long parameterId)
        {
            if (parameterId < int.MinValue || parameterId > int.MaxValue)
                return "<not built-in>";
            string name = Enum.GetName(typeof(BuiltInParameter), (int)parameterId);
            return string.IsNullOrEmpty(name) ? "<not built-in>" : name;
        }

        private static string DescribeParameterValue(Document document, Parameter parameter)
        {
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.Double:
                        return Format(parameter.AsDouble()) + " (display '" + (parameter.AsValueString() ?? string.Empty) + "')";
                    case StorageType.Integer:
                        return parameter.AsInteger().ToString(CultureInfo.InvariantCulture) +
                            " (display '" + (parameter.AsValueString() ?? string.Empty) + "')";
                    case StorageType.String:
                        return "'" + (parameter.AsString() ?? string.Empty) + "'";
                    case StorageType.ElementId:
                        return DescribeElementId(document, parameter.AsElementId());
                    default:
                        return "<none>";
                }
            }
            catch (Exception exception)
            {
                return "<unavailable: " + exception.Message + ">";
            }
        }

        private static string DescribeElementId(Document document, ElementId id)
        {
            if (RevitApiCompatibility.IsInvalidElementId(id))
                return "<invalid>";
            Element referenced = document.GetElement(id);
            return Id(id) + (referenced == null ? string.Empty : " ('" + referenced.Name + "')");
        }

        private static bool IsPipeFitting(Element element)
        {
            return element is FamilyInstance && element.Category != null &&
                Id(element.Category.Id) == (long)BuiltInCategory.OST_PipeFitting;
        }

        // Changed by Jhay: expose the exact fitting XYZ/level/offset at each sequencing boundary.
        private static void LogAllPipeFittingStages(
            Document document,
            IEnumerable<ElementId> targetIds,
            string stage,
            IList<string> observations)
        {
            foreach (ElementId id in targetIds)
            {
                Element element = document.GetElement(id);
                if (IsPipeFitting(element))
                    AppendFittingStageEvidence(document, element, stage, observations);
            }
        }

        private static void AppendFittingStageEvidence(
            Document document,
            Element fitting,
            string stage,
            IList<string> observations)
        {
            Level exposedLevel = ResolvePrimaryReferenceLevel(document, fitting);
            double? offset = ReadOffset(fitting);
            observations.Add(
                "FITTING STAGE | " + stage + " | element " + Id(fitting.Id) +
                " | world " + WorldEvidence.Capture(fitting).Describe() +
                " | LevelId " + DescribeElementId(document, fitting.LevelId) +
                " | exposed/effective level " +
                (exposedLevel == null ? "<unavailable>" : Id(exposedLevel.Id) + " ('" + exposedLevel.Name + "')") +
                " | offset " + (offset.HasValue ? Format(offset.Value) : "<unavailable>"));
        }

        // Changed by Jhay: record insulation host/bounds evidence at each movement boundary.
        private static void LogAllPipeInsulationStages(
            Document document,
            AssemblyDestinationLevelPlan plan,
            IReadOnlyDictionary<long, ElementId> targetBySource,
            IReadOnlyDictionary<long, PipeInsulationMoveEvidence> evidenceByTarget,
            string stage,
            IList<string> observations)
        {
            foreach (PipeInsulationHostPlan insulationPlan in plan.PipeInsulations)
            {
                var targetInsulation = document.GetElement(
                    targetBySource[insulationPlan.SourceInsulationId]) as PipeInsulation;
                Element targetHost = document.GetElement(targetBySource[insulationPlan.SourceHostId]);
                if (targetInsulation == null || targetHost == null)
                    throw new InvalidOperationException("Pipe Insulation stage diagnostics could not resolve the copied relationship.");
                evidenceByTarget[Id(targetInsulation.Id)].AppendStage(
                    document,
                    targetInsulation,
                    targetHost,
                    stage,
                    plan.DeltaZ,
                    observations);
            }
        }

        // Changed by Jhay: require the copied MEP network to retain the exact mapped connector graph.
        private static List<string> GetMappedConnectivityFailures(
            Element source,
            Element target,
            ConnectorEvidence sourceEvidence,
            IReadOnlyList<ElementId> sourceIds,
            IReadOnlyList<ElementId> targetIds,
            ISet<long> targetMemberIds)
        {
            var failures = new List<string>();
            ConnectorEvidence targetEvidence = ConnectorEvidence.Capture(target, targetMemberIds);
            if (sourceEvidence.ConnectorCount != targetEvidence.ConnectorCount)
            {
                failures.Add(
                    "connector count changed from " + sourceEvidence.ConnectorCount +
                    " to " + targetEvidence.ConnectorCount);
            }

            var expectedTargetOwners = new List<long>();
            foreach (long sourceOwnerId in sourceEvidence.ConnectedOwnerIds)
            {
                int index = -1;
                for (int candidate = 0; candidate < sourceIds.Count; candidate++)
                {
                    if (Id(sourceIds[candidate]) == sourceOwnerId)
                    {
                        index = candidate;
                        break;
                    }
                }
                if (index < 0)
                {
                    failures.Add("source connector evidence contains unmapped production member " + sourceOwnerId);
                    continue;
                }
                expectedTargetOwners.Add(Id(targetIds[index]));
            }
            expectedTargetOwners.Sort();

            if (!expectedTargetOwners.SequenceEqual(targetEvidence.ConnectedOwnerIds))
            {
                failures.Add(
                    "mapped production connector relationships differ; expected [" +
                    string.Join(",", expectedTargetOwners) + "], actual [" +
                    string.Join(",", targetEvidence.ConnectedOwnerIds) + "]");
            }

            return failures;
        }

        private static void EnsureSupportedInsulationHost(Element host)
        {
            if (host is Pipe)
                return;
            if (host.Category != null)
            {
                long categoryId = Id(host.Category.Id);
                if (categoryId == (long)BuiltInCategory.OST_PipeFitting ||
                    categoryId == (long)BuiltInCategory.OST_PipeAccessory)
                    return;
            }

            throw new InvalidOperationException(
                "Pipe Insulation host " + Id(host.Id) + " (" + CategoryName(host) + ", " +
                host.GetType().Name + ") is not a supported pipe, pipe fitting, or pipe accessory.");
        }

        private static double? ReadOffset(Element element)
        {
            var insulation = element as PipeInsulation;
            if (insulation != null)
                return insulation.LevelOffset;

            var curve = element as MEPCurve;
            if (curve != null)
                return curve.LevelOffset;

            Parameter offset = FindOffsetParameter(element);
            return offset != null && offset.StorageType == StorageType.Double
                ? (double?)offset.AsDouble()
                : null;
        }

        // Changed by Jhay: read-only fitting offsets are diagnostic metadata; world Z relative to level is authoritative.
        private static void ValidateFinalOffset(
            Element source,
            Element target,
            AssemblyDestinationLevelPlan plan,
            IList<string> observations)
        {
            long sourceId = Id(source.Id);
            long targetId = Id(target.Id);
            Parameter sourceParameter = FindOffsetParameter(source);
            Parameter targetParameter = FindOffsetParameter(target);
            bool useGeometricOffset = IsPipeFitting(target) &&
                (targetParameter == null || targetParameter.IsReadOnly);

            if (useGeometricOffset)
            {
                double sourceWorldZ = GetAuthoritativeWorldZ(source);
                double targetWorldZ = GetAuthoritativeWorldZ(target);
                double sourceGeometricOffset = sourceWorldZ - plan.SourceLevel.ProjectElevation;
                double targetGeometricOffset = targetWorldZ - plan.DestinationLevel.ProjectElevation;
                double? sourceExposed = ReadOffset(source);
                double? targetExposed = ReadOffset(target);
                plan.Validation.Check(
                    Math.Abs(sourceGeometricOffset - targetGeometricOffset) <= Tolerance,
                    sourceId,
                    targetId,
                    "geometric offset for read-only fitting",
                    "source world Z " + Format(sourceWorldZ) + " - source level " +
                    Format(plan.SourceLevel.ProjectElevation) + " = " + Format(sourceGeometricOffset) +
                    "; target world Z " + Format(targetWorldZ) + " - destination level " +
                    Format(plan.DestinationLevel.ProjectElevation) + " = " + Format(targetGeometricOffset) +
                    "; exposed source/target values " +
                    (sourceExposed.HasValue ? Format(sourceExposed.Value) : "<unavailable>") + "/" +
                    (targetExposed.HasValue ? Format(targetExposed.Value) : "<unavailable>") +
                    " are diagnostic-only because target IsReadOnly=" +
                    (targetParameter == null ? "<no parameter>" : targetParameter.IsReadOnly.ToString()),
                    observations);
                return;
            }

            double? sourceOffset = ReadOffset(source);
            double? targetOffset = ReadOffset(target);
            bool passed = !sourceOffset.HasValue ||
                (targetOffset.HasValue && Math.Abs(sourceOffset.Value - targetOffset.Value) <= Tolerance);
            plan.Validation.Check(
                passed,
                sourceId,
                targetId,
                "writable/reliable exposed offset",
                "source " + (sourceOffset.HasValue ? Format(sourceOffset.Value) : "<unavailable>") +
                ", target " + (targetOffset.HasValue ? Format(targetOffset.Value) : "<unavailable>") +
                ", target parameter read-only " +
                (targetParameter == null ? "<typed API or unavailable>" : targetParameter.IsReadOnly.ToString()),
                observations);
        }

        private static Parameter FindOffsetParameter(Element element)
        {
            Parameter elevation = element.get_Parameter(BuiltInParameter.INSTANCE_ELEVATION_PARAM);
            if (elevation != null && elevation.StorageType == StorageType.Double)
                return elevation;
            Parameter freeHostOffset = element.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
            return freeHostOffset != null && freeHostOffset.StorageType == StorageType.Double
                ? freeHostOffset
                : null;
        }

        private static double GetAuthoritativeWorldZ(Element element)
        {
            var family = element as FamilyInstance;
            if (family != null)
                return family.GetTransform().Origin.Z;
            var point = element.Location as LocationPoint;
            if (point != null)
                return point.Point.Z;
            var curve = element.Location as LocationCurve;
            if (curve?.Curve != null)
                return (curve.Curve.GetEndPoint(0).Z + curve.Curve.GetEndPoint(1).Z) * 0.5;
            BoundingBoxXYZ bounds = element.get_BoundingBox(null);
            if (bounds != null)
                return (bounds.Min.Z + bounds.Max.Z) * 0.5;
            throw new InvalidOperationException(
                "Production member " + Id(element.Id) + " has no authoritative world Z for offset validation.");
        }

        private static long Id(ElementId id)
        {
            return RevitApiCompatibility.GetElementIdValue(id);
        }

        private static string CategoryName(Element element)
        {
            return element.Category == null ? "<no category>" : element.Category.Name;
        }

        private static string Format(double value)
        {
            return Math.Round(value, 9).ToString("G17", CultureInfo.InvariantCulture);
        }

        private sealed class WorldEvidence
        {
            public XYZ Start { get; private set; }
            public XYZ End { get; private set; }
            public XYZ BoundsMin { get; private set; }
            public XYZ BoundsMax { get; private set; }
            public Transform FamilyTransform { get; private set; }

            public static WorldEvidence Capture(Element element)
            {
                var evidence = new WorldEvidence();
                var point = element.Location as LocationPoint;
                var curve = element.Location as LocationCurve;
                if (point != null)
                    evidence.Start = point.Point;
                else if (curve != null && curve.Curve != null)
                {
                    evidence.Start = curve.Curve.GetEndPoint(0);
                    evidence.End = curve.Curve.GetEndPoint(1);
                }

                var family = element as FamilyInstance;
                if (family != null)
                {
                    evidence.FamilyTransform = family.GetTransform();
                    if (evidence.Start == null)
                        evidence.Start = evidence.FamilyTransform.Origin;
                }

                BoundingBoxXYZ bounds = element.get_BoundingBox(null);
                if (bounds != null)
                {
                    evidence.BoundsMin = bounds.Min;
                    evidence.BoundsMax = bounds.Max;
                }

                return evidence;
            }

            public WorldEvidence Translated(XYZ vector)
            {
                return new WorldEvidence
                {
                    Start = Add(Start, vector),
                    End = Add(End, vector),
                    BoundsMin = Add(BoundsMin, vector),
                    BoundsMax = Add(BoundsMax, vector),
                    FamilyTransform = Translate(FamilyTransform, vector)
                };
            }

            public bool Matches(WorldEvidence other)
            {
                return SameUnordered(Start, End, other.Start, other.End) &&
                    Same(BoundsMin, other.BoundsMin) && Same(BoundsMax, other.BoundsMax) &&
                    SameTransform(FamilyTransform, other.FamilyTransform);
            }

            public string Describe()
            {
                var parts = new List<string>();
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
                }
                if (BoundsMin != null)
                    parts.Add("BoundsMin=" + FormatPoint(BoundsMin));
                if (BoundsMax != null)
                    parts.Add("BoundsMax=" + FormatPoint(BoundsMax));
                return parts.Count == 0 ? "<unavailable>" : string.Join(", ", parts);
            }

            private static bool SameUnordered(XYZ a0, XYZ a1, XYZ b0, XYZ b1)
            {
                if (a1 == null && b1 == null)
                    return Same(a0, b0);
                return (Same(a0, b0) && Same(a1, b1)) || (Same(a0, b1) && Same(a1, b0));
            }

            private static XYZ Add(XYZ point, XYZ vector)
            {
                return point == null ? null : point + vector;
            }

            private static Transform Translate(Transform transform, XYZ vector)
            {
                return transform == null
                    ? null
                    : Transform.CreateTranslation(vector).Multiply(transform);
            }

            private static bool SameTransform(Transform left, Transform right)
            {
                if (left == null || right == null)
                    return left == null && right == null;
                return Same(left.Origin, right.Origin) &&
                    Same(left.BasisX, right.BasisX) &&
                    Same(left.BasisY, right.BasisY) &&
                    Same(left.BasisZ, right.BasisZ);
            }

            private static string FormatPoint(XYZ point)
            {
                return "(" + Format(point.X) + "," + Format(point.Y) + "," + Format(point.Z) + ")";
            }

            private static bool Same(XYZ left, XYZ right)
            {
                if (left == null || right == null)
                    return left == null && right == null;
                return left.DistanceTo(right) <= Tolerance;
            }
        }

        // Changed by Jhay: authoritative insulation movement evidence uses host world geometry and model-world bounds.
        private sealed class PipeInsulationMoveEvidence
        {
            private long SourceInsulationId { get; set; }
            private long TargetInsulationId { get; set; }
            private long SourceHostId { get; set; }
            private long TargetHostId { get; set; }
            private WorldEvidence SourceHostWorld { get; set; }
            private WorldEvidence TargetHostBefore { get; set; }
            private InsulationGeometryEvidence SourceInsulation { get; set; }
            private InsulationGeometryEvidence TargetInsulationBefore { get; set; }
            private string SourceHostLevel { get; set; }
            private string TargetHostLevelBefore { get; set; }

            public static PipeInsulationMoveEvidence Capture(
                Document document,
                PipeInsulation sourceInsulation,
                PipeInsulation targetInsulation,
                Element sourceHost,
                Element targetHost)
            {
                if (sourceInsulation == null || targetInsulation == null || sourceHost == null || targetHost == null)
                    throw new InvalidOperationException("Pipe Insulation movement evidence could not resolve all source/copy elements.");

                return new PipeInsulationMoveEvidence
                {
                    SourceInsulationId = Id(sourceInsulation.Id),
                    TargetInsulationId = Id(targetInsulation.Id),
                    SourceHostId = Id(sourceHost.Id),
                    TargetHostId = Id(targetHost.Id),
                    SourceHostWorld = WorldEvidence.Capture(sourceHost),
                    TargetHostBefore = WorldEvidence.Capture(targetHost),
                    SourceInsulation = InsulationGeometryEvidence.Capture(sourceInsulation),
                    TargetInsulationBefore = InsulationGeometryEvidence.Capture(targetInsulation),
                    SourceHostLevel = DescribeEffectiveLevel(document, sourceHost),
                    TargetHostLevelBefore = DescribeEffectiveLevel(document, targetHost)
                };
            }

            public void AppendStage(
                Document document,
                PipeInsulation targetInsulation,
                Element targetHost,
                string stage,
                double deltaZ,
                IList<string> observations)
            {
                InsulationGeometryEvidence targetGeometry = InsulationGeometryEvidence.Capture(targetInsulation);
                observations.Add(
                    "PIPE INSULATION STAGE | " + stage +
                    " | source insulation " + SourceInsulationId +
                    " | copied insulation " + TargetInsulationId +
                    " | source host " + SourceHostId +
                    " | copied host " + TargetHostId +
                    " | expected deltaZ " + Format(deltaZ));
                observations.Add(
                    "  Source host world geometry: " + SourceHostWorld.Describe() +
                    " | effective host level " + SourceHostLevel);
                observations.Add(
                    "  Copied host world geometry: " + WorldEvidence.Capture(targetHost).Describe() +
                    " | effective host level " + DescribeEffectiveLevel(document, targetHost));
                observations.Add("  Source insulation: " + SourceInsulation.Describe());
                observations.Add("  Copied insulation: " + targetGeometry.Describe());
            }

            public void ValidateFinal(
                Document document,
                PipeInsulation targetInsulation,
                Element targetHost,
                XYZ translation,
                Level destinationLevel,
                DestinationValidationCollector validation,
                IList<string> observations)
            {
                WorldEvidence targetHostAfter = WorldEvidence.Capture(targetHost);
                InsulationGeometryEvidence targetInsulationAfter =
                    InsulationGeometryEvidence.Capture(targetInsulation);

                validation.Check(
                    SourceHostWorld.Translated(translation).Matches(targetHostAfter),
                    SourceInsulationId,
                    TargetInsulationId,
                    "insulation source/copy host world delta",
                    "expected " + SourceHostWorld.Translated(translation).Describe() +
                    " | actual " + targetHostAfter.Describe(),
                    observations);
                validation.Check(
                    TargetHostBefore.Translated(translation).Matches(targetHostAfter),
                    SourceInsulationId,
                    TargetInsulationId,
                    "insulation copied host before/after delta",
                    "expected " + TargetHostBefore.Translated(translation).Describe() +
                    " | actual " + targetHostAfter.Describe(),
                    observations);
                validation.Check(
                    SourceInsulation.TranslatedBoundsMatch(targetInsulationAfter, translation),
                    SourceInsulationId,
                    TargetInsulationId,
                    "insulation source/copy model-world bounds delta",
                    "source " + SourceInsulation.Describe() + " | target " + targetInsulationAfter.Describe(),
                    observations);
                validation.Check(
                    TargetInsulationBefore.TranslatedBoundsMatch(targetInsulationAfter, translation),
                    SourceInsulationId,
                    TargetInsulationId,
                    "insulation copied bounds before/after delta",
                    "before " + TargetInsulationBefore.Describe() + " | after " + targetInsulationAfter.Describe(),
                    observations);

                Level effectiveLevel = ResolvePrimaryReferenceLevel(document, targetHost);
                validation.Check(
                    effectiveLevel != null && Id(effectiveLevel.Id) == Id(destinationLevel.Id),
                    SourceInsulationId,
                    TargetInsulationId,
                    "insulation effective host destination level",
                    effectiveLevel == null
                        ? "unavailable"
                        : "actual '" + effectiveLevel.Name + "', expected '" + destinationLevel.Name + "'",
                    observations);

                observations.Add(
                    "PIPE INSULATION FINAL VALIDATION | " + SourceInsulationId + " -> " +
                    TargetInsulationId + " | host " + SourceHostId + " -> " + TargetHostId +
                    " | source/copy host world delta: PASS | copied host before/after delta: PASS" +
                    " | source/copy insulation bounds delta: PASS | copied bounds before/after delta: PASS" +
                    " | effective host level " + (effectiveLevel == null ? "<unavailable>" : effectiveLevel.Name) +
                    " | raw LocationCurve excluded from authoritative validation");
            }

            private static string DescribeEffectiveLevel(Document document, Element host)
            {
                Level level = ResolvePrimaryReferenceLevel(document, host);
                return level == null
                    ? "<unavailable>"
                    : Id(level.Id) + " ('" + level.Name + "')";
            }
        }

        private sealed class InsulationGeometryEvidence
        {
            private XYZ BoundsMin { get; set; }
            private XYZ BoundsMax { get; set; }
            private XYZ BoundsCenter { get; set; }
            private XYZ RawLocationCurveStart { get; set; }
            private XYZ RawLocationCurveEnd { get; set; }

            public static InsulationGeometryEvidence Capture(PipeInsulation insulation)
            {
                BoundingBoxXYZ bounds = insulation.get_BoundingBox(null);
                if (bounds == null)
                    throw new InvalidOperationException("Pipe Insulation " + Id(insulation.Id) + " has no model bounding box.");

                var evidence = new InsulationGeometryEvidence
                {
                    BoundsMin = bounds.Min,
                    BoundsMax = bounds.Max,
                    BoundsCenter = (bounds.Min + bounds.Max) * 0.5
                };
                var location = insulation.Location as LocationCurve;
                if (location?.Curve != null)
                {
                    evidence.RawLocationCurveStart = location.Curve.GetEndPoint(0);
                    evidence.RawLocationCurveEnd = location.Curve.GetEndPoint(1);
                }
                return evidence;
            }

            public bool TranslatedBoundsMatch(InsulationGeometryEvidence actual, XYZ translation)
            {
                return Same(BoundsMin + translation, actual.BoundsMin) &&
                    Same(BoundsMax + translation, actual.BoundsMax) &&
                    Same(BoundsCenter + translation, actual.BoundsCenter);
            }

            public string Describe()
            {
                return "model-world BoundsMin=" + Point(BoundsMin) +
                    ", BoundsMax=" + Point(BoundsMax) +
                    ", BoundsCenter=" + Point(BoundsCenter) +
                    ", raw/API LocationCurveStart=" + Point(RawLocationCurveStart) +
                    ", raw/API LocationCurveEnd=" + Point(RawLocationCurveEnd);
            }

            private static bool Same(XYZ left, XYZ right)
            {
                return left != null && right != null && left.DistanceTo(right) <= Tolerance;
            }

            private static string Point(XYZ point)
            {
                return point == null
                    ? "<unavailable>"
                    : "(" + Format(point.X) + "," + Format(point.Y) + "," + Format(point.Z) + ")";
            }
        }

        // Changed by Jhay: bounded connector evidence for source-to-copy topology validation.
        private sealed class ConnectorEvidence
        {
            public int ConnectorCount { get; private set; }
            public IReadOnlyList<long> ConnectedOwnerIds { get; private set; }

            public static ConnectorEvidence Capture(Element element, ISet<long> productionMemberIds)
            {
                ConnectorManager manager = GetConnectorManager(element);
                if (manager == null)
                {
                    return new ConnectorEvidence
                    {
                        ConnectorCount = 0,
                        ConnectedOwnerIds = new List<long>()
                    };
                }

                var connectedOwners = new List<long>();
                int connectorCount = 0;
                foreach (Connector connector in manager.Connectors)
                {
                    connectorCount++;
                    foreach (Connector reference in connector.AllRefs)
                    {
                        Element owner = reference.Owner;
                        if (owner == null || Id(owner.Id) == Id(element.Id) ||
                            !productionMemberIds.Contains(Id(owner.Id)))
                            continue;
                        connectedOwners.Add(Id(owner.Id));
                    }
                }
                connectedOwners.Sort();

                return new ConnectorEvidence
                {
                    ConnectorCount = connectorCount,
                    ConnectedOwnerIds = connectedOwners
                };
            }

            private static ConnectorManager GetConnectorManager(Element element)
            {
                var curve = element as MEPCurve;
                if (curve != null)
                    return curve.ConnectorManager;
                return (element as FamilyInstance)?.MEPModel?.ConnectorManager;
            }
        }
    }
}
