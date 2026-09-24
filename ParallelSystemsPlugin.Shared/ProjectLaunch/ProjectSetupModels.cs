// Created by Jhay
using System;
using System.Collections.Generic;

namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal enum ProjectSetupStatusKind
    {
        Pass,
        Warning,
        Blocking,
        Pending,
        NotApplicable
    }

    internal enum ProjectSetupAutomationLevel
    {
        Automatic,
        RevitNativeHandoff,
        ManualRevitHandoff,
        Unsupported
    }

    internal sealed class ProjectSetupStatus
    {
        public string Name { get; set; }
        public ProjectSetupStatusKind Kind { get; set; }
        public string Message { get; set; }
        public string Label => Kind == ProjectSetupStatusKind.NotApplicable
            ? "NOT APPLICABLE"
            : Kind.ToString().ToUpperInvariant();
    }

    internal sealed class ProjectLinkState
    {
        public string Name { get; set; }
        public bool IsLoaded { get; set; }
        public bool IsRequired { get; set; }
    }

    internal sealed class ProjectSetupState
    {
        public int SchemaVersion { get; set; } = 1;
        public string ProjectName { get; set; }
        public string ProjectNumber { get; set; }
        public string ProjectManager { get; set; }
        public string ProjectOverseer { get; set; }
        public string ClientNamingConvention { get; set; }
        public bool NamingConventionConfirmed { get; set; }
        public string GridLevelWorksetName { get; set; } = "Shared Levels and Grids";
        public string InitialModelWorksetName { get; set; } = "Workset1";
        public List<string> AdditionalWorksetNames { get; set; } = new List<string>();
        public List<string> RequiredLinkNames { get; set; } = new List<string>();
        public bool NoLinksRequired { get; set; }
        public string CoordinationSourceLinkName { get; set; }
        public bool CoordinationReviewed { get; set; }
        public bool CopyMonitorNotRequired { get; set; }
        public string CopyMonitorNotRequiredReason { get; set; }
        public bool SetupCompleted { get; set; }
        public string CompletedBy { get; set; }
        public string CompletedUtc { get; set; }
        public string CompletedPluginVersion { get; set; }

        public ProjectSetupState Clone()
        {
            return new ProjectSetupState
            {
                SchemaVersion = SchemaVersion,
                ProjectName = ProjectName,
                ProjectNumber = ProjectNumber,
                ProjectManager = ProjectManager,
                ProjectOverseer = ProjectOverseer,
                ClientNamingConvention = ClientNamingConvention,
                NamingConventionConfirmed = NamingConventionConfirmed,
                GridLevelWorksetName = GridLevelWorksetName,
                InitialModelWorksetName = InitialModelWorksetName,
                AdditionalWorksetNames = new List<string>(AdditionalWorksetNames ?? new List<string>()),
                RequiredLinkNames = new List<string>(RequiredLinkNames ?? new List<string>()),
                NoLinksRequired = NoLinksRequired,
                CoordinationSourceLinkName = CoordinationSourceLinkName,
                CoordinationReviewed = CoordinationReviewed,
                CopyMonitorNotRequired = CopyMonitorNotRequired,
                CopyMonitorNotRequiredReason = CopyMonitorNotRequiredReason,
                SetupCompleted = SetupCompleted,
                CompletedBy = CompletedBy,
                CompletedUtc = CompletedUtc,
                CompletedPluginVersion = CompletedPluginVersion
            };
        }
    }

    internal sealed class ProjectSetupSnapshot
    {
        public ProjectSetupState State { get; set; } = new ProjectSetupState();
        public string DocumentKey { get; set; }
        public string DocumentTitle { get; set; } = "No active project";
        public string RevitVersion { get; set; }
        public bool IsSuitableProject { get; set; }
        public string SuitabilityMessage { get; set; }
        public bool IsModelInCloud { get; set; }
        public bool IsWorkshared { get; set; }
        public List<string> ExistingWorksets { get; set; } = new List<string>();
        public List<ProjectLinkState> Links { get; set; } = new List<ProjectLinkState>();
        public int MonitoredGridCount { get; set; }
        public int MonitoredLevelCount { get; set; }
        public List<ProjectSetupStatus> Statuses { get; set; } = new List<ProjectSetupStatus>();
        public ProjectSetupAutomationLevel SaveAsCloudAutomation { get; set; }
        public ProjectSetupAutomationLevel ManageLinksAutomation { get; set; }
        public ProjectSetupAutomationLevel CopyMonitorAutomation { get; set; }
        public bool HasBlockingIssues => Statuses.Exists(x => x.Kind == ProjectSetupStatusKind.Blocking);
    }
}
