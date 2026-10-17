#nullable enable
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Runtime.Serialization.Json;

namespace ParallelSystems.ProductSupport
{
    // Also compiled by the plugin (.NET Framework). The installed launcher remains
    // responsible for signature, package, minimum-version and shutdown validation.
    internal static class LegacyUpdaterBootstrap
    {
        internal static void Start() { _ = Task.Run(RunAsync); }

        private static async Task RunAsync()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var data = Path.Combine(local, "Parallel Systems", "Updater");
            var root = Path.Combine(local, "Programs", "Parallel Systems", "Updater");
            var helper = Path.Combine(root, "ParallelSystems.Launcher.exe");
            try
            {
                if (!File.Exists(helper) || !File.Exists(Path.Combine(root, "current.txt")) || !NeedsBootstrap(root)) return;
                Directory.CreateDirectory(data);
                // Only one product/process may bootstrap at a time.
                using (var gate = new FileStream(Path.Combine(data, "bootstrap.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    if (!Enabled(data, root)) return;
                    var status = Path.Combine(data, "product-updates.xml");
                    using (var check = Launch(helper, "launch-updater --check updater --background")) { }
                    var deadline = DateTime.UtcNow.AddMinutes(5);
                    while (DateTime.UtcNow < deadline)
                    {
                        await Task.Delay(2000).ConfigureAwait(false);
                        if (!Enabled(data, root)) return;
                        if (!File.Exists(status)) continue;
                        var updates = XDocument.Parse(Read(status)).Root;
                        long reported;
                        if (updates == null || !long.TryParse((string?)updates.Attribute("reported"), out reported) ||
                            reported < DateTime.UtcNow.AddMinutes(-2).Ticks || reported > DateTime.UtcNow.AddMinutes(1).Ticks) continue;
                        var offer = updates.Elements("update").FirstOrDefault(e => (string?)e.Attribute("product") == "updater" && (string?)e.Attribute("state") == "Ready");
                        if (offer == null) continue;
                        var version = (string?)offer.Attribute("version");
                        if (!IsPostBootstrapVersion(version)) continue;
                        var staging = Path.Combine(data, "staging");
                        if (!Directory.Exists(staging)) continue;
                        foreach (var directory in Directory.GetDirectories(staging))
                        {
                            Guid operation;
                            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out operation)) continue;
                            var journalFile = Path.Combine(directory, "preparation-journal.json");
                            if (!File.Exists(journalFile) || File.Exists(Path.Combine(data, "pending", operation.ToString("N") + ".json"))) continue;
                            var channel = Value(ParseJson(ReadSettings(data, root)), "channel");
                            if (channel == "") channel = "stable";
                            if (!IsBootstrapCandidate(Value(ParseJson(Read(journalFile)), "payload"), version!, channel)) continue;
                            // Persist an attempt before invoking an older launcher; don't loop on failures.
                            var marker = Path.Combine(data, "bootstrap-" + version + ".attempted");
                            if (File.Exists(marker)) return;
                            if (!Enabled(data, root)) return;
                            using (var attempt = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
                            using (var install = Launch(helper, "activate " + operation.ToString("N") + " updater " + version + " - " + channel))
                            {
                                while (!install.HasExited) await Task.Delay(500).ConfigureAwait(false);
                                File.AppendAllText(marker, "Launcher exit code: " + install.ExitCode);
                            }
                            // Activation can close the old Updater even if a later step fails.
                            using (var restart = Launch(helper, "launch-updater --background")) { }
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                try { File.AppendAllText(Path.Combine(data, "bootstrap.log"), DateTime.UtcNow.ToString("O") + " " + ex.Message + Environment.NewLine); }
                catch { /* Product startup remains independent of updates. */ }
            }
        }

        internal static bool AutoInstallEnabled(string json)
        {
            var setting = ParseJson(json).Element("autoInstallUpdates");
            if (setting == null) return true;
            if ((string?)setting.Attribute("type") != "boolean") throw new InvalidDataException("Invalid autoInstallUpdates setting.");
            return setting.Value == "true";
        }

        private static bool Enabled(string data, string root)
        {
            return NeedsBootstrap(root) && AutoInstallEnabled(ReadSettings(data, root));
        }

        internal static bool IsLegacyVersion(string? version)
        {
            var parsed = ParseVersion(version);
            return parsed != null && parsed == new Version(1, 0, 0, 0);
        }

        internal static bool IsPostBootstrapVersion(string? version)
        {
            var parsed = ParseVersion(version);
            return parsed != null && parsed > new Version(1, 0, 0, 0);
        }

        private static Version? ParseVersion(string? version)
        {
            Version? parsed;
            if (!Version.TryParse(version, out parsed) || parsed == null || parsed.Build < 0) return null;
            return new Version(parsed.Major, parsed.Minor, parsed.Build, Math.Max(0, parsed.Revision));
        }

        private static bool NeedsBootstrap(string root)
        {
            // Inspect the active app, not the stable launcher or a stale status heartbeat.
            var assembly = Path.Combine(ActiveDirectory(root), "ParallelSystems.Updater.dll");
            return File.Exists(assembly) && IsLegacyVersion(FileVersionInfo.GetVersionInfo(assembly).FileVersion);
        }

        internal static bool IsBootstrapCandidate(string json, string version, string channel)
        {
            var journal = ParseJson(json);
            var target = journal.Element("target");
            return IsPostBootstrapVersion(version) && Value(journal, "phase") == "Prepared" && target != null &&
                Value(target, "productId") == "updater" && Value(target, "version") == version &&
                Value(target, "architecture") == "win-x64" && Value(target, "revitYear") == "" &&
                (channel == "stable" || channel == "preview") && Value(target, "channel") == channel &&
                Value(journal.Element("release"), "deliveryType") == "package";
        }

        private static string ReadSettings(string data, string root)
        {
            var settings = Path.Combine(data, "updater.user.json");
            if (!File.Exists(settings))
                settings = Path.Combine(ActiveDirectory(root), "updater.settings.json");
            return File.Exists(settings) ? Read(settings) : "{}";
        }

        private static string ActiveDirectory(string root)
        {
            var parts = Read(Path.Combine(root, "current.txt")).Trim().Split('/');
            if (parts.Length != 2 || parts[0] != "versions" || parts[1] == ".." || parts[1] == "." || parts[1] == "" ||
                parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("Invalid updater pointer.");
            return Path.Combine(root, "versions", parts[1]);
        }
        private static string Value(XElement? element, string name) { return element == null ? "" : (string?)element.Element(name) ?? ""; }
        private static XElement ParseJson(string json)
        {
            using (var reader = JsonReaderWriterFactory.CreateJsonReader(Encoding.UTF8.GetBytes(json), XmlDictionaryReaderQuotas.Max))
                return XElement.Load(reader);
        }
        private static string Read(string file)
        {
            using (var stream = File.OpenRead(file))
            {
                if (stream.Length > 128 * 1024) throw new InvalidDataException("Updater metadata is too large.");
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }
        private static Process Launch(string helper, string arguments)
        {
            var start = new ProcessStartInfo(helper, arguments) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(helper) };
            start.EnvironmentVariables["PARALLELSYSTEMS_LAUNCHER_DIAGNOSTIC_ID"] = Guid.NewGuid().ToString("N");
            return Process.Start(start) ?? throw new IOException("The updater launcher did not start.");
        }
    }
}
