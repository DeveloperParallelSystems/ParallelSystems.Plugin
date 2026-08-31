using System;
using System.Collections.Generic;

namespace ParallelSystemsPlugin.Timesheets
{
    /// <summary>
    /// Plugin-owned representation of the checkpoint JSON sent to the timesheet API.
    /// Keep its serialized property names compatible with the server contract.
    /// </summary>
    internal sealed class TrackerCheckpointRequest
    {
        public int SchemaVersion { get; set; } = 3;
        public Guid MessageId { get; set; } = Guid.NewGuid();
        public Guid SessionId { get; set; }
        public long Sequence { get; set; }
        public string EventType { get; set; }
        public DateTime OccurredAtUtc { get; set; }
        public string ClientWorkDate { get; set; }
        public DateTime SessionStartedAtUtc { get; set; }
        public DateTime? SessionEndedAtUtc { get; set; }

        public string InstallationId { get; set; }
        public string MachineName { get; set; }
        public string WindowsUserName { get; set; }
        public string RevitUserName { get; set; }
        public string RevitVersion { get; set; }
        public string PluginVersion { get; set; }

        public string ProjectKey { get; set; }
        public string ProjectName { get; set; }
        public string ProjectNumber { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPathHash { get; set; }
        public string CloudProjectId { get; set; }
        public string CloudModelId { get; set; }

        public long? ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public string SheetNumber { get; set; }
        public string ViewTemplateName { get; set; }
        public string ViewDiscipline { get; set; }
        public string ViewSubDiscipline { get; set; }

        public int MeasuredActiveSeconds { get; set; }
        public int EngagedSeconds { get; set; }
        public int ForegroundSeconds { get; set; }
        public int InactiveSeconds { get; set; }
        public bool IsClosed { get; set; }

        public string DetectedArea { get; set; }
        public string DetectedLevel { get; set; }
        public string DetectedZone { get; set; }
        public string DetectedSystem { get; set; }
        public string DetectedActivity { get; set; }
        public string DetectedScope { get; set; }
        public string DetectedStatus { get; set; }

        public Dictionary<string, int> CategoryCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> LevelCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SystemCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> AreaCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> ZoneCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> WorksetCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> TransactionNameCounts { get; set; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public int CreatedElementCount { get; set; }
        public int ModifiedElementCount { get; set; }
        public int DeletedElementCount { get; set; }
        public int UninspectedElementCount { get; set; }
    }
}
