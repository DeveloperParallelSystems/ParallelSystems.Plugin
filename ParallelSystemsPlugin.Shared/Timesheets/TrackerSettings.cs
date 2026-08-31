using Newtonsoft.Json;
using System;
using System.IO;

namespace ParallelSystemsPlugin.Timesheets
{
    internal sealed class TrackerSettings
    {
        public bool Enabled { get; set; } = true;
        public string ApiBaseUrl { get; set; } = "http://localhost:5185";
        public string TrackerApiKey { get; set; } = "TzuOp6FOUBaRuRtHX8/krK3ztrxY/OmSIowsJMdnso/rcXvWtdaQEP5Ee86FQcjx";
        public int SamplingIntervalSeconds { get; set; } = 5;
        public int CheckpointIntervalSeconds { get; set; } = 60;
        public int ActiveInputThresholdSeconds { get; set; } = 90;
        public int EngagedGraceSeconds { get; set; } = 300;
        public int MaxElementsInspectedPerChange { get; set; } = 250;
        public int MaxPendingMessages { get; set; } = 5000;
        public string CompanyName { get; set; } = "Parallel Systems";

        public static string ProgramDataFolder
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(root, "Parallel Systems", "Timesheet");
            }
        }

        public static string SettingsPath => Path.Combine(ProgramDataFolder, "tracker.settings.json");

        public static TrackerSettings Load()
        {
            TrackerSettings settings = null;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    settings = JsonConvert.DeserializeObject<TrackerSettings>(File.ReadAllText(SettingsPath));
                }
            }
            catch
            {
                // Invalid settings must never prevent the rest of the Revit plugin from loading.
            }

            settings = settings ?? new TrackerSettings();

            var url = Environment.GetEnvironmentVariable("PARALLEL_TIMESHEET_API_URL");
            if (!string.IsNullOrWhiteSpace(url)) settings.ApiBaseUrl = url;

            var key = Environment.GetEnvironmentVariable("PARALLEL_TIMESHEET_API_KEY");
            if (!string.IsNullOrWhiteSpace(key)) settings.TrackerApiKey = key;

            settings.SamplingIntervalSeconds = Clamp(settings.SamplingIntervalSeconds, 2, 60);
            settings.CheckpointIntervalSeconds = Clamp(settings.CheckpointIntervalSeconds, 15, 600);
            settings.ActiveInputThresholdSeconds = Clamp(settings.ActiveInputThresholdSeconds, 15, 600);
            settings.EngagedGraceSeconds = Clamp(settings.EngagedGraceSeconds, settings.ActiveInputThresholdSeconds, 1800);
            settings.MaxElementsInspectedPerChange = Clamp(settings.MaxElementsInspectedPerChange, 25, 2000);
            settings.MaxPendingMessages = Clamp(settings.MaxPendingMessages, 100, 50000);
            settings.ApiBaseUrl = (settings.ApiBaseUrl ?? string.Empty).Trim().TrimEnd('/');

            return settings;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }
}
