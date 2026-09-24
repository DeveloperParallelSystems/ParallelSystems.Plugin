// Created by Jhay
using System;
using System.Collections.Generic;
using System.Linq;

namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal static class ProjectSetupValidator
    {
        public static List<ProjectSetupStatus> Validate(ProjectSetupSnapshot snapshot)
        {
            var results = new List<ProjectSetupStatus>();
            ProjectSetupState state = snapshot.State ?? new ProjectSetupState();

            Add(results, "Active Project", snapshot.IsSuitableProject ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                snapshot.IsSuitableProject ? "The active Revit project is available for setup." : snapshot.SuitabilityMessage);

            var missingInfo = new List<string>();
            if (Blank(state.ProjectName)) missingInfo.Add("Project Name");
            if (Blank(state.ProjectNumber)) missingInfo.Add("Project Number");
            if (Blank(state.ProjectManager)) missingInfo.Add("Project Manager");
            if (Blank(state.ProjectOverseer)) missingInfo.Add("Project Overseer");
            Add(results, "Project Information", missingInfo.Count == 0 ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                missingInfo.Count == 0 ? "Required project information is present." : "Missing: " + string.Join(", ", missingInfo) + ".");

            bool naming = !Blank(state.ClientNamingConvention) && state.NamingConventionConfirmed;
            Add(results, "Naming Convention", naming ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                naming ? "The client convention has been recorded and confirmed." : "Enter the client convention and explicitly confirm the model follows it.");

            Add(results, "Worksharing", snapshot.IsWorkshared ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                snapshot.IsWorkshared ? "Revit worksharing is enabled." : "Revit worksharing is not enabled.");

            List<string> requiredWorksets = new[] { state.GridLevelWorksetName, state.InitialModelWorksetName }
                .Concat(state.AdditionalWorksetNames ?? new List<string>()).Where(x => !Blank(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            List<string> missingWorksets = requiredWorksets.Where(x => !snapshot.ExistingWorksets.Contains(x, StringComparer.OrdinalIgnoreCase)).ToList();
            Add(results, "Required Worksets", snapshot.IsWorkshared && missingWorksets.Count == 0 ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                missingWorksets.Count == 0 && snapshot.IsWorkshared ? "All configured worksets exist." : "Missing configured worksets: " + (missingWorksets.Count == 0 ? "worksharing is not enabled" : string.Join(", ", missingWorksets)) + ".");

            bool cloud = snapshot.IsModelInCloud && snapshot.IsWorkshared;
            Add(results, "Cloud Worksharing", cloud ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking,
                cloud ? "Revit Cloud Worksharing is active." : snapshot.IsModelInCloud ? "The cloud model is not workshared." : "This model has not been saved to Autodesk Docs.");

            if (state.NoLinksRequired)
            {
                Add(results, "Required Links", ProjectSetupStatusKind.NotApplicable, "The project explicitly records that no Revit links are required.");
            }
            else
            {
                List<string> required = state.RequiredLinkNames ?? new List<string>();
                List<string> missingLinks = required.Where(name => !snapshot.Links.Any(x => Eq(x.Name, name))).ToList();
                List<string> unloaded = required.Where(name => snapshot.Links.Any(x => Eq(x.Name, name) && !x.IsLoaded)).ToList();
                bool linksPass = required.Count > 0 && missingLinks.Count == 0 && unloaded.Count == 0;
                string message = required.Count == 0 ? "Mark at least one detected Revit link as required, or explicitly select No Links Required."
                    : missingLinks.Count > 0 ? "Required links are missing: " + string.Join(", ", missingLinks) + "."
                    : unloaded.Count > 0 ? "Required links are unloaded: " + string.Join(", ", unloaded) + "."
                    : required.Count + " required Revit link(s) are loaded.";
                Add(results, "Required Links", linksPass ? ProjectSetupStatusKind.Pass : ProjectSetupStatusKind.Blocking, message);
            }

            if (state.CopyMonitorNotRequired)
            {
                bool reason = !Blank(state.CopyMonitorNotRequiredReason);
                Add(results, "Grid / Level Coordination", reason ? ProjectSetupStatusKind.NotApplicable : ProjectSetupStatusKind.Blocking,
                    reason ? "Copy/Monitor is explicitly not required: " + state.CopyMonitorNotRequiredReason : "A reason is required when Copy/Monitor is not required.");
            }
            else
            {
                bool sourceRequired = !state.NoLinksRequired;
                bool sourceValid = !sourceRequired || (!Blank(state.CoordinationSourceLinkName) && snapshot.Links.Any(x => Eq(x.Name, state.CoordinationSourceLinkName) && x.IsLoaded));
                bool evidence = snapshot.MonitoredGridCount > 0 && snapshot.MonitoredLevelCount > 0;
                bool coordinationPass = sourceValid && evidence && state.CoordinationReviewed;
                string message = !sourceValid ? "Select a loaded coordination source link."
                    : !evidence ? "Monitored Grid and Level evidence is required."
                    : !state.CoordinationReviewed ? snapshot.MonitoredGridCount + " monitored Grid(s) and " + snapshot.MonitoredLevelCount + " monitored Level(s) detected; review is not confirmed."
                    : snapshot.MonitoredGridCount + " monitored Grid(s) and " + snapshot.MonitoredLevelCount + " monitored Level(s) detected and reviewed.";
                Add(results, "Grid / Level Coordination", coordinationPass ? ProjectSetupStatusKind.Pass : (evidence ? ProjectSetupStatusKind.Warning : ProjectSetupStatusKind.Blocking), message);
                if (!coordinationPass && evidence && (!sourceValid || !state.CoordinationReviewed))
                    results[results.Count - 1].Kind = ProjectSetupStatusKind.Blocking;
            }

            return results;
        }

        private static bool Blank(string value) => string.IsNullOrWhiteSpace(value);
        private static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static void Add(ICollection<ProjectSetupStatus> results, string name, ProjectSetupStatusKind kind, string message) =>
            results.Add(new ProjectSetupStatus { Name = name, Kind = kind, Message = message });
    }
}
