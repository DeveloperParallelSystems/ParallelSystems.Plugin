using Newtonsoft.Json;
using System;
using System.IO;

namespace ParallelSystemsPlugin.Timesheets
{
    internal sealed class TrackerSettings
    {
        public bool Enabled { get; set; } = true;
        public string ApiBaseUrl { get; set; } = "";
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
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                return Path.Combine(root, "Parallel Systems", "Timesheet");
            }
        }

        public static string SettingsPath => Path.Combine(ProgramDataFolder, "tracker.settings.json");
        private static string LegacySettingsPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Parallel Systems", "Timesheet", "tracker.settings.json");

        public static TrackerSettings Load()
        {
            TrackerSettings settings = null;

            try
            {
                if (File.Exists(SettingsPath))
                {
                    settings = JsonConvert.DeserializeObject<TrackerSettings>(File.ReadAllText(SettingsPath));
                }
                else if (File.Exists(LegacySettingsPath))
                {
                    settings = JsonConvert.DeserializeObject<TrackerSettings>(File.ReadAllText(LegacySettingsPath))
                        ?? new TrackerSettings();
                    Directory.CreateDirectory(ProgramDataFolder);
                    File.WriteAllText(
                        SettingsPath,
                        JsonConvert.SerializeObject(settings, Formatting.Indented));
                }
                else
                {
                    settings = new TrackerSettings();
                    Directory.CreateDirectory(ProgramDataFolder);
                    File.WriteAllText(
                        SettingsPath,
                        JsonConvert.SerializeObject(settings, Formatting.Indented));
                }
            }
            catch
            {
                // Invalid settings must never prevent the rest of the Revit plugin from loading.
            }

            settings = settings ?? new TrackerSettings();

            var url = Environment.GetEnvironmentVariable("PARALLEL_TIMESHEET_API_URL");
            if (!string.IsNullOrWhiteSpace(url)) settings.ApiBaseUrl = url;

            settings.SamplingIntervalSeconds = Clamp(settings.SamplingIntervalSeconds, 2, 60);
            settings.CheckpointIntervalSeconds = Clamp(settings.CheckpointIntervalSeconds, 15, 600);
            settings.ActiveInputThresholdSeconds = Clamp(settings.ActiveInputThresholdSeconds, 15, 600);
            settings.EngagedGraceSeconds = Clamp(settings.EngagedGraceSeconds, settings.ActiveInputThresholdSeconds, 1800);
            settings.MaxElementsInspectedPerChange = Clamp(settings.MaxElementsInspectedPerChange, 25, 2000);
            settings.MaxPendingMessages = Clamp(settings.MaxPendingMessages, 100, 50000);
            settings.ApiBaseUrl = CommonServerSettings.Resolve(CommonServerSettings.Load(settings.ApiBaseUrl));

            return settings;
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }

// Keep this resolver equivalent across the independently deployed desktop products.
internal static class CommonServerSettings
{
    internal const string DefaultHost = "app.parallelsystems.com.au";
    internal static string SettingsPath => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData),
        "Parallel Systems", "Shared", "common.settings.json");

    internal static bool IsValid(string value)
    {
        System.Uri uri;
        return !string.IsNullOrWhiteSpace(value) &&
            System.Uri.TryCreate(value.Contains("://") ? value.Trim() : "https://" + value.Trim(), System.UriKind.Absolute, out uri) &&
            (uri.Scheme == "https" || uri.Scheme == "http") &&
            !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo) &&
            string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
    }

    internal static string Load(string local, string path = null)
    {
        if (IsValid(local)) return local.Trim().TrimEnd('/');
        path = path ?? SettingsPath;
        try
        {
            var shared = Newtonsoft.Json.Linq.JObject.Parse(System.IO.File.ReadAllText(path)).GetValue("ApiBaseUrl", System.StringComparison.OrdinalIgnoreCase)?.ToObject<string>();
            if (IsValid(shared)) return shared.Trim().TrimEnd('/');
        }
        catch (System.Exception ex) when (ex is System.IO.IOException || ex is System.UnauthorizedAccessException || ex is Newtonsoft.Json.JsonException || ex is System.InvalidOperationException) { }
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            // Replace unusable shared settings with the default.
            using (var stream = new System.IO.StreamWriter(new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write, System.IO.FileShare.Read)))
                    stream.Write("{\n  \"ApiBaseUrl\": \"" + DefaultHost + "\"\n}");
        }
        catch (System.IO.IOException) { }
        catch (System.UnauthorizedAccessException) { }
        return DefaultHost;
    }

    internal static string Resolve(string value)
    {
        if (!IsValid(value)) throw new System.ArgumentException("Enter a server host or HTTP(S) URL without credentials, query, or fragment.");
        value = value.Trim().TrimEnd('/');
        if (value.Contains("://")) return value;
        return ResolveAsync(value).GetAwaiter().GetResult();
    }

    private static async System.Threading.Tasks.Task<string> ResolveAsync(string host)
    {
        using (var client = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false }))
        {
            client.Timeout = System.TimeSpan.FromSeconds(2);
            foreach (var scheme in new[] { "https://", "http://" })
            {
                try
                {
                    using (var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Head, scheme + host + "/"))
                    using (var response = await client.SendAsync(request, System.Net.Http.HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
                        return scheme + host; // Any HTTP status proves that the endpoint is reachable.
                }
                catch (System.Net.Http.HttpRequestException) { }
                catch (System.Threading.Tasks.TaskCanceledException) { }
            }
        }
        // An offline startup must not erase the configured host.
        return "https://" + host;
    }
}

}
