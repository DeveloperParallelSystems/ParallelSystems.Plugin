using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class ShowTimesheetCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try
            {
                EventWaitHandle activation;
                if (EventWaitHandle.TryOpenExisting(@"Local\ParallelSystems.DesktopNotifier.ShowMain", out activation))
                {
                    using (activation) activation.Set();
                    return Result.Succeeded;
                }
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var root = Path.Combine(local, "Programs", "Parallel Systems", "Desktop Notifier");
                var launcher = Path.Combine(root, "ParallelSystems.Launcher.exe");
                if (File.Exists(launcher))
                {
                    Start(launcher, "launch --show-main");
                    return Result.Succeeded;
                }
                // Support earlier standalone and suite installations as well.
                var candidates = new[]
                {
                    Path.Combine(root, "ParallelSystems.DesktopNotifier.exe"),
                    Path.Combine(local, "Programs", "Parallel Systems", "Suite", "DesktopNotifier", "ParallelSystems.DesktopNotifier.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Parallel Systems", "Revit Plugin", "DesktopNotifier", "ParallelSystems.DesktopNotifier.exe"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Parallel Systems", "Desktop Notifier", "ParallelSystems.DesktopNotifier.exe")
                };
                foreach (var file in candidates)
                    if (File.Exists(file)) { Start(file, "--show-main"); return Result.Succeeded; }
                TaskDialog.Show("Show Timesheet", "Desktop Notifier is not installed. Install it using the Parallel Systems suite, then try again.");
                return Result.Cancelled;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Show Timesheet", "Desktop Notifier could not be opened.\n\n" + ex.Message);
                return Result.Cancelled;
            }
        }
        private static void Start(string file, string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(file, arguments)
            { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(file) }))
            {
                if (process == null) throw new IOException("Desktop Notifier could not be started.");
            }
        }
    }
}
