using System;
using System.Diagnostics;
using System.IO;
using System.Xml.Linq;
namespace ParallelSystems.ProductSupport
{
    // Deliberately dependency-free for Revit .NET Framework and modern desktop products.
    internal static class ProductLifecycle
    {
        internal static void Report(string product, int? year, string entry, string state)
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parallel Systems", "Updater", "health");
                    Directory.CreateDirectory(root);
                    var file = Path.Combine(root, product + "-" + process.Id + "-" + process.StartTime.ToUniversalTime().Ticks + ".xml");
                    var document = new XDocument(new XElement("startup", new XAttribute("schema", "1"),
                        new XElement("product", product), new XElement("year", year.HasValue ? year.Value.ToString() : ""),
                        new XElement("entry", Path.GetFullPath(entry)), new XElement("host", process.MainModule == null ? "" : process.MainModule.FileName),
                        new XElement("pid", process.Id), new XElement("started", process.StartTime.ToUniversalTime().Ticks),
                        new XElement("reported", DateTime.UtcNow.Ticks), new XElement("state", state)));
                    var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { document.Save(stream); stream.Flush(true); }
                    if (File.Exists(file)) File.Replace(temporary, file, null); else File.Move(temporary, file);
                }
            }
            catch { /* Health reporting must never prevent ordinary offline product startup. */ }
        }
        internal static void OpenUpdater()
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Parallel Systems", "Updater");
            var launcher = Path.Combine(root, "ParallelSystems.Launcher.exe");
            var legacy = Path.Combine(root, "ParallelSystems.Updater.exe");
            if (File.Exists(launcher)) Process.Start(new ProcessStartInfo(launcher, "launch-updater") { UseShellExecute = false, WorkingDirectory = root });
            else if (File.Exists(legacy)) Process.Start(new ProcessStartInfo(legacy) { UseShellExecute = false, WorkingDirectory = root });
            else throw new FileNotFoundException("Install Parallel Systems Updater before checking for updates.");
        }
    }
}
