using System;
using System.Diagnostics;
using System.IO;
using System.Linq;

using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

using ParallelSystemsPlugin.Commands;

namespace ParallelSystemsPlugin.Helpers
{
    internal static class BackgroundPublishRunner
    {
        private static bool _started;

        internal static string GetCurrentRevitExecutablePath()
        {
            try
            {
                using (Process process = Process.GetCurrentProcess())
                {
                    return process.MainModule?.FileName;
                }
            }
            catch
            {
                return null;
            }
        }

        internal static void Register(UIControlledApplication uiApp)
        {
            // Only attach if this Revit instance is a worker
            string isWorker = Environment.GetEnvironmentVariable("PARALLEL_PUBLISH_JOB");
            if (!string.Equals(isWorker, "1", StringComparison.OrdinalIgnoreCase))
                return;

            // ✅ Idling is on UIControlledApplication (not ControlledApplication)
            uiApp.Idling += OnIdling;
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            if (_started) return;
            _started = true;

            // In UIControlledApplication.Idling, sender is UIApplication
            var uiapp = sender as UIApplication;
            if (uiapp == null) return;

            string jobPath = Environment.GetEnvironmentVariable("PARALLEL_PUBLISH_JOB_PATH");
            if (string.IsNullOrWhiteSpace(jobPath) || !File.Exists(jobPath))
                return;

            try
            {
                RunWorker(uiapp, jobPath);
            }
            catch
            {
                // Keep worker stable
            }

            // ✅ No Quit() in your API level -> do nothing.
            // Worker Revit will remain open; export status is still written to progress.txt.
        }

        private static void RunWorker(UIApplication uiapp, string jobPath)
        {
            var app = uiapp.Application;

            var job = BackgroundPublishJob.ReadJson(jobPath);
            if (job == null) return;

            string jobDir = Path.GetDirectoryName(jobPath);
            string progressPath = Path.Combine(jobDir, "progress.txt");
            string cancelPath = Path.Combine(jobDir, "cancel.flag");

            BackgroundPublishJob.WriteProgress(progressPath, 0, 1, "Starting…");

            Document doc = null;

            try
            {
                // Try already-open doc (if Revit was launched with model path)
                doc = app.Documents
                    .Cast<Document>()
                    .FirstOrDefault(d =>
                    {
                        try { return string.Equals(d.PathName, job.ModelPath, StringComparison.OrdinalIgnoreCase); }
                        catch { return false; }
                    });

                if (doc == null)
                {
                    if (string.IsNullOrWhiteSpace(job.ModelPath))
                        throw new InvalidOperationException("ModelPath missing.");

                    // ✅ Always use ModelPath (avoids string overload mismatch)
                    ModelPath mp = ModelPathUtils.ConvertUserVisiblePathToModelPath(job.ModelPath);

                    var openOpts = new OpenOptions();
                    doc = app.OpenDocumentFile(mp, openOpts);
                }

                if (doc == null)
                    throw new InvalidOperationException("Failed to open document.");

                var allowed = PublishBomCommand.GetSheetsByNumber_Internal(doc, job.AllowedSheetNumbers);

                PublishBomCommand.ExportPdfsWithChecksum_Background(
                    doc,
                    job.PublishRoot,
                    allowed,
                    job.RevisionSnapshot,
                    progressPath,
                    cancelPath
                );

                BackgroundPublishJob.WriteProgress(progressPath, 1, 1, "Completed.", isDone: true);
            }
            catch (Exception ex)
            {
                BackgroundPublishJob.WriteProgress(progressPath, 0, 1, ex.Message, isError: true);
            }
            finally
            {
                try { if (doc != null) doc.Close(false); } catch { }

                // Delete temp RVT if requested
                if (job != null && job.DeleteModelOnFinish)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(job.ModelPath) && File.Exists(job.ModelPath))
                            File.Delete(job.ModelPath);
                    }
                    catch { }
                }

                // Delete the whole job folder (job.json, progress.txt, cancel.flag, temp model)
                if (job != null && job.DeleteJobDirectoryOnFinish)
                {
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(job.JobDirectory) && Directory.Exists(job.JobDirectory))
                            Directory.Delete(job.JobDirectory, true);
                    }
                    catch { }
                }
            }

        }
    }
}
