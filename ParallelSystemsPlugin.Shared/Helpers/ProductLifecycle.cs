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
        internal static void EnsureUpdaterBackground()
        {
            try { StartUpdater("--background"); } catch { /* Offline/product startup is independent of the updater. */ }
        }
        internal static void CheckForUpdates(string product = "desktop-notifier", int? year = null)
        {
            if(product != "desktop-notifier" && product != "plugin") throw new ArgumentException("Unknown product");
            if(product == "plugin" && (!year.HasValue || year<2021 || year>2026)) throw new ArgumentException("Invalid Revit year");
            StartUpdater("--check "+product+(year.HasValue?" "+year.Value:"")+" --background");
        }
        internal static void OpenUpdater(string product = "desktop-notifier", int? year = null)
        {
            if(product != "desktop-notifier" && product != "updater" && product != "plugin") throw new ArgumentException("Unknown product");
            if(product == "plugin" && (!year.HasValue || year<2021 || year>2026)) throw new ArgumentException("Invalid Revit year");
            StartUpdater("--review "+product+(year.HasValue?" "+year.Value:""));
        }
        internal static string UpdateLabel(string product,int? year,string statusFile="") { return ReadUpdateLabel(product,year,statusFile,false); }
        internal static string UpdateActionLabel(string product,int? year,string statusFile="") { return ReadUpdateLabel(product,year,statusFile,true); }
        private static string ReadUpdateLabel(string product,int? year,string statusFile,bool action)
        {
            try
            {
                var file=string.IsNullOrEmpty(statusFile)?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Parallel Systems","Updater","product-updates.xml"):statusFile;
                if(!File.Exists(file)||new FileInfo(file).Length>128*1024) return action ? "" : "Updates";
                using(var reader=System.Xml.XmlReader.Create(file,new System.Xml.XmlReaderSettings{DtdProcessing=System.Xml.DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=128*1024}))
                {
                    var doc=XDocument.Load(reader); long reported;
                    if(doc.Root==null || Attribute(doc.Root,"schema")!="1" || !long.TryParse(Attribute(doc.Root,"reported"),out reported) || reported<=0 || reported>DateTime.UtcNow.Ticks || DateTime.UtcNow.Ticks-reported>TimeSpan.FromMinutes(2).Ticks) return action ? "" : "Updates";
                    foreach(var item in doc.Root.Elements("update"))
                    {
                        if(Attribute(item,"product")!=product || Attribute(item,"year")!=(year.HasValue?year.Value.ToString():"")) continue;
                        var state=Attribute(item,"state");var version=Attribute(item,"version");
                        if(action)
                        {
                            if(string.IsNullOrWhiteSpace(version)) return "";
                            if(state=="Ready"||state=="Approved"||state=="Postponed") return "Install v"+version;
                            if(state=="Downloading") return "Downloading v"+version;
                            if(state=="Available"||state=="Failed"||state=="Verifying") return "Download v"+version;
                            if(state=="Applying") return "Installing v"+version;
                            if(state=="NeedsReview") return "Review v"+version;
                            if(state=="UpdaterRequired") return "Update Updater first";
                            return "";
                        }
                        if(state=="Ready"||state=="Approved"||state=="Postponed") return "Ready to install "+version;
                        if(state=="Downloading"||state=="Verifying") return Attribute(item,"detail")??"Downloading update";
                        if(state=="Applying") return "Installing update";
                        if(state=="NeedsReview") return "Update needs review";
                        if(state=="UpdaterRequired") return "Update the Updater first";
                        if(state=="Available"||state=="Failed") return "New update "+version;
                    }
                }
            }
            catch { }
            return action ? "" : "Updates";
        }
        internal static string StartupUpdateVersion(int year, DateTime requestedAt, string statusFile = "")
        {
            try
            {
                var file = string.IsNullOrEmpty(statusFile) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Parallel Systems", "Updater", "product-updates.xml") : statusFile;
                if (!File.Exists(file) || new FileInfo(file).Length > 128 * 1024) return null;
                using (var reader = System.Xml.XmlReader.Create(file, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 128 * 1024 }))
                {
                    var doc = XDocument.Load(reader);
                    if (doc.Root == null || Attribute(doc.Root, "schema") != "1") return null;
                    foreach (var item in doc.Root.Elements("update"))
                    {
                        long checkedAt;
                        var state = Attribute(item, "state");
                        if (Attribute(item, "product") == "plugin" && Attribute(item, "year") == year.ToString() &&
                            long.TryParse(Attribute(item, "checked"), out checkedAt) && checkedAt >= requestedAt.Ticks && checkedAt <= DateTime.UtcNow.Ticks &&
                            (state == "Available" || state == "Ready" || state == "Downloading" || state == "Verifying"))
                            return Attribute(item, "version");
                    }
                }
            }
            catch { }
            return null;
        }

        internal static void InstallStartupUpdate(int year, string version, bool unattended = false)
        {
            using (var process = Process.GetCurrentProcess())
            using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
            using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "ParallelSystems.Updater.Startup." + identity.User.Value, System.IO.Pipes.PipeDirection.InOut))
            {
                pipe.Connect(5000);
                var request = System.Text.Encoding.UTF8.GetBytes(year + "|" + process.Id + "|" + process.StartTime.ToUniversalTime().Ticks + "|" + version + (unattended ? "|unattended" : ""));
                if (request.Length > 256) throw new InvalidOperationException("Invalid startup update request.");
                var buffer = new byte[256];
                Array.Copy(request, buffer, request.Length);
                using (var timeout = new System.Threading.Timer(_ => pipe.Dispose(), null, 10000, System.Threading.Timeout.Infinite))
                {
                    pipe.Write(buffer, 0, buffer.Length);
                    if (pipe.ReadByte() != 1) throw new IOException("The Updater did not accept the request.");
                }
            }
        }

        private static string Attribute(XElement element,string name) { var value=element.Attribute(name); return value==null?"":value.Value; }
        private static void StartUpdater(string arguments)
        {
            var root=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Programs","Parallel Systems","Updater");
            var launcher=Path.Combine(root,"ParallelSystems.Launcher.exe");var legacy=Path.Combine(root,"ParallelSystems.Updater.exe");
            var file=File.Exists(launcher)?launcher:legacy;
            if(!File.Exists(file)) throw new FileNotFoundException("Install Parallel Systems Updater before checking for updates.");
            Process.Start(new ProcessStartInfo(file,(file==launcher?"launch-updater ":"")+arguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=root});
        }
    }
}
