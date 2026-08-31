using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

using ParallelSystemPlugin.UI; // AppDialog + ProgressWindow
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Helpers;

using ParallelSystemsPlugin.Compatibility;
namespace ParallelSystemsPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class PublishBomCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                if (!App.IsUserAuthorized)
                {
                    AppDialog.Warn(
                        "Access Denied",
                        "Your account is not authorized to use this function.");

                    return Result.Cancelled;
                }

                var uiapp = commandData.Application;
                var uidoc = uiapp.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;

                if (doc == null)
                {
                    AppDialog.Warn(uiapp, "Publish BOM", "No active document.");
                    return Result.Failed;
                }

                var appCfg = Configs.AppConfig.CurrentConfig;
                if (appCfg == null)
                {
                    AppDialog.Warn(uiapp, "Publish BOM", "Application configuration not found.");
                    return Result.Failed;
                }

                return Run(uiapp, doc, appCfg);
            }
            catch (Exception ex)
            {
                message = ex.ToString();
                return Result.Failed;
            }
        }

        /// <summary>
        /// Callable from ExportBomCommand (main procurement runner).
        /// CSV is produced first. Only CSV-included sheets are eligible for PDF export when enabled.
        /// </summary>
        public static Result Run(UIApplication uiapp, Document doc, ApplicationConfig appCfg)
        {
            if (doc == null) return Result.Failed;

            if (appCfg == null || appCfg.Procurement == null)
            {
                AppDialog.Warn(uiapp, "Publish BOM",
                    "Procurement configuration not found. Please open Configurations and save Publish details.");
                return Result.Failed;
            }

            var p = appCfg.Procurement;

            string publishRoot = (p.PublishSite ?? "").Trim();
            string baseFileName = (p.PublishFileName ?? "").Trim();

            if (string.IsNullOrWhiteSpace(publishRoot))
            {
                AppDialog.Warn(uiapp, "Publish BOM",
                    "Publish Site is empty. Please set it in Configurations > Procurement > Publish details.");
                return Result.Failed;
            }

            if (!Directory.Exists(publishRoot))
                Directory.CreateDirectory(publishRoot);

            if (string.IsNullOrWhiteSpace(baseFileName))
            {
                AppDialog.Warn(uiapp, "Publish BOM",
                    "File Name is empty. Please set it in Configurations > Procurement > Publish details.");
                return Result.Failed;
            }

            // Ensure .csv
            if (baseFileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                baseFileName = baseFileName.Substring(0, baseFileName.Length - 4);

            string csvPath = Path.Combine(publishRoot, baseFileName + ".csv");

            // ---- Build CSV rows FIRST ----
            var csvBuild = BuildDrawingRegisterCsv(doc);

            // If nothing qualifies (eg revision number blank), do not proceed to PDF export.
            if (csvBuild.Rows.Count == 0)
            {
                WriteCsvUtf8Bom(csvPath, csvBuild.Headers, csvBuild.Rows); // still writes headers only (optional)
                AppDialog.Warn(uiapp, "Publish BOM",
                    "No rows were generated.\n\nAll sheets were skipped because they have no Sheet Number or no Revision number.");
                return Result.Succeeded;
            }

            // Write CSV first (requirement)
            WriteCsvUtf8Bom(csvPath, csvBuild.Headers, csvBuild.Rows);

            // ---- PDF export (only sheets included in CSV) ----
            if (p.ExportPdf && !RevitApiCompatibility.SupportsNativePdfExport)
            {
                AppDialog.Warn(uiapp, "Publish BOM",
                    "CSV created successfully.\n\n" +
                    "Revit 2021 does not expose the native PDF export API used by this command, " +
                    "so this run was completed as CSV-only.\n\nCSV:\n" + csvPath);
                return Result.Succeeded;
            }

            if (p.ExportPdf)
            {
                var allowedSheets = GetSheetsByNumber(doc, csvBuild.IncludedSheetNumbers);

                // Don’t do anything if checksum says up to date
                if (!HasPdfChanges(publishRoot, csvBuild.RevSnapshot))
                {
                    AppDialog.Info(uiapp, "Publish BOM",
                        "CSV created.\n\nNo PDF changes detected (already up to date).\n\nCSV:\n" +
                        csvPath + "\n\nRows: " + csvBuild.Rows.Count);
                    return Result.Succeeded;
                }

                // Ask user how to export
                var mode = PromptPdfExportMode(uiapp, doc);
                if (mode == PdfExportMode.Cancel)
                {
                    AppDialog.Info(uiapp, "Publish BOM",
                        "CSV created.\n\nPDF export cancelled.\n\nCSV:\n" +
                        csvPath + "\n\nRows: " + csvBuild.Rows.Count);
                    return Result.Succeeded;
                }

                if (mode == PdfExportMode.InSession)
                {
                    // Use your existing in-session export (with progress)
                    var res = ExportPdfsWithChecksum(uiapp, doc, publishRoot, allowedSheets, csvBuild.RevSnapshot);
                    if (res != Result.Succeeded) return res;

                    AppDialog.Info(uiapp, "Publish BOM",
                        "Publish completed.\n\nCSV:\n" +
                        csvPath + "\n\nRows: " + csvBuild.Rows.Count);
                    return Result.Succeeded;
                }

                // Worker mode
                bool forceLocalCopy = IsCloudModel(doc);
                StartBackgroundPdfExport(uiapp, doc, publishRoot, allowedSheets, csvBuild.RevSnapshot, forceLocalCopy);

                AppDialog.Info(uiapp, "Publish BOM",
                    "CSV created.\n\nBackground PDF export started (minimized worker Revit).\n" +
                    "You may continue working while PDFs are generated.\n\nCSV:\n" +
                    csvPath + "\n\nRows: " + csvBuild.Rows.Count);
                return Result.Succeeded;
            }

            // CSV-only publish
            AppDialog.Info(uiapp, "Publish BOM",
                "Publish completed (CSV only).\n\nCSV:\n" + csvPath + "\n\nRows: " + csvBuild.Rows.Count);
            return Result.Succeeded;
        }

        private static void StartBackgroundPdfExport(
            UIApplication uiapp,
            Document doc,
            string publishRoot,
            IList<ViewSheet> allowedSheets,
            Dictionary<string, SheetChecksum> revSnapshot,
            bool forceLocalCopy)
        {
            string jobId = Guid.NewGuid().ToString("N");
            string jobDir = ParallelSystemsPlugin.Helpers.BackgroundPublishJob.CreateJobFolder(jobId);

            string jobPath = Path.Combine(jobDir, "job.json");
            string progressPath = Path.Combine(jobDir, "progress.txt");
            string cancelPath = Path.Combine(jobDir, "cancel.flag");

            // Default: worker opens the same model path (works for local/non-ACC)
            string modelPathForWorker = doc.PathName;
            bool deleteModelAfter = false;

            // For ACC/BIM360: create a local temp RVT
            if (forceLocalCopy)
            {
                // Note: This is a full model SaveAs. No “sheet-only” shortcut exists.
                string tempModelPath = Path.Combine(jobDir, "TEMP_WORKER_MODEL.rvt");

                var sao = new SaveAsOptions();
                sao.OverwriteExistingFile = true;

                // This may take time depending on model size
                BackgroundPublishJob.WriteProgress(progressPath, 0, Math.Max(1, allowedSheets.Count), "Preparing local copy (ACC)…");
                doc.SaveAs(tempModelPath, sao);

                modelPathForWorker = tempModelPath;
                deleteModelAfter = true; // ensure it gets deleted after export
            }

            var job = new ParallelSystemsPlugin.Helpers.BackgroundPublishJob
            {
                JobId = jobId,
                ModelPath = modelPathForWorker,
                PublishRoot = publishRoot,
                JobDirectory = jobDir,
                DeleteModelOnFinish = deleteModelAfter,
                DeleteJobDirectoryOnFinish = true
            };

            job.AllowedSheetNumbers = allowedSheets
                .Select(s => (s.SheetNumber ?? "").Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (revSnapshot != null)
            {
                foreach (var kv in revSnapshot)
                {
                    if (kv.Value == null) continue;
                    job.RevisionSnapshot[kv.Key] = new ParallelSystemsPlugin.Helpers.BackgroundPublishJob.RevSnap
                    {
                        RevisionNumber = (kv.Value.RevisionNumber ?? "").Trim(),
                        RevisionDate = (kv.Value.RevisionDate ?? "").Trim()
                    };
                }
            }

            ParallelSystemsPlugin.Helpers.BackgroundPublishJob.WriteJson(jobPath, job);
            ParallelSystemsPlugin.Helpers.BackgroundPublishJob.WriteProgress(progressPath, 0, Math.Max(1, job.AllowedSheetNumbers.Count), "Queued…");

            string revitExe = ParallelSystemsPlugin.Helpers.BackgroundPublishRunner.GetCurrentRevitExecutablePath();
            if (string.IsNullOrWhiteSpace(revitExe) || !File.Exists(revitExe))
                throw new InvalidOperationException("Unable to locate Revit.exe.");

            var psi = new ProcessStartInfo(revitExe);
            psi.UseShellExecute = false;

            // ✅ Minimized worker
            psi.WindowStyle = ProcessWindowStyle.Minimized;
            psi.CreateNoWindow = false;

            psi.EnvironmentVariables["PARALLEL_PUBLISH_JOB"] = "1";
            psi.EnvironmentVariables["PARALLEL_PUBLISH_JOB_PATH"] = jobPath;

            // Optional: pass model path as arg (helps worker open it)
            if (!string.IsNullOrWhiteSpace(modelPathForWorker) && File.Exists(modelPathForWorker))
                psi.Arguments = "\"" + modelPathForWorker + "\"";

            Process.Start(psi);

            ShowBackgroundProgressWindow(uiapp, progressPath, cancelPath);
        }



        // =====================================================================================
        // Background export + checksum pre-check (MAIN Revit)
        // =====================================================================================

        private static bool HasPdfChanges(string publishRoot, Dictionary<string, SheetChecksum> currentSnapshot)
        {
            var old = ReadChecksum(publishRoot);

            if (old.Count == 0) return true; // first run

            foreach (var kv in currentSnapshot)
            {
                if (!old.TryGetValue(kv.Key, out var prev) || prev == null)
                    return true;

                if (!StringEquals(prev.RevisionNumber, kv.Value.RevisionNumber) ||
                    !StringEquals(prev.RevisionDate, kv.Value.RevisionDate))
                    return true;
            }

            return false;
        }

        private static void StartBackgroundPdfExport(
            UIApplication uiapp,
            Document doc,
            string publishRoot,
            IList<ViewSheet> allowedSheets,
            Dictionary<string, SheetChecksum> revSnapshot)
        {
            string jobId = Guid.NewGuid().ToString("N");
            string jobDir = BackgroundPublishJob.CreateJobFolder(jobId);

            string jobPath = Path.Combine(jobDir, "job.json");
            string progressPath = Path.Combine(jobDir, "progress.txt");
            string cancelPath = Path.Combine(jobDir, "cancel.flag");

            var job = new BackgroundPublishJob
            {
                JobId = jobId,
                ModelPath = doc.PathName,
                PublishRoot = publishRoot,
                AllowedSheetNumbers = (allowedSheets ?? new List<ViewSheet>())
                    .Select(s => (s.SheetNumber ?? "").Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            };

            if (revSnapshot != null)
            {
                foreach (var kv in revSnapshot)
                {
                    if (kv.Value == null) continue;
                    job.RevisionSnapshot[kv.Key] = new BackgroundPublishJob.RevSnap
                    {
                        RevisionNumber = (kv.Value.RevisionNumber ?? "").Trim(),
                        RevisionDate = (kv.Value.RevisionDate ?? "").Trim()
                    };
                }
            }

            BackgroundPublishJob.WriteJson(jobPath, job);
            BackgroundPublishJob.WriteProgress(progressPath, 0, job.AllowedSheetNumbers.Count, "Queued…");

            // Launch a worker Revit.exe
            string revitExe = BackgroundPublishRunner.GetCurrentRevitExecutablePath();
            if (string.IsNullOrWhiteSpace(revitExe) || !File.Exists(revitExe))
                throw new InvalidOperationException("Unable to locate Revit.exe.");

            var psi = new ProcessStartInfo(revitExe);
            psi.UseShellExecute = false;
            psi.EnvironmentVariables["PARALLEL_PUBLISH_JOB"] = "1";
            psi.EnvironmentVariables["PARALLEL_PUBLISH_JOB_PATH"] = jobPath;

            if (!string.IsNullOrWhiteSpace(doc.PathName) && File.Exists(doc.PathName))
                psi.Arguments = "\"" + doc.PathName + "\"";

            Process.Start(psi);

            // Show modeless progress in main Revit
            ShowBackgroundProgressWindow(uiapp, progressPath, cancelPath);
        }

        private static void ShowBackgroundProgressWindow(UIApplication uiapp, string progressPath, string cancelPath)
        {
            var win = new ProgressWindow();

            var hwnd = Process.GetCurrentProcess().MainWindowHandle;
            try { new WindowInteropHelper(win).Owner = hwnd; } catch { }

            win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            win.Initialize(1, "Background PDF Export", "Starting…");
            win.Show();

            var timer = new DispatcherTimer();
            timer.Interval = TimeSpan.FromMilliseconds(500);
            timer.Tick += (s, e) =>
            {
                // cancel request
                if (win.IsCanceled)
                {
                    try { File.WriteAllText(cancelPath, "1"); } catch { }
                }

                if (BackgroundPublishJob.TryReadProgress(progressPath, out var flag, out var done, out var total, out var msg))
                {
                    if (total <= 0) total = 1;
                    win.Initialize(total, "Background PDF Export", msg);
                    win.UpdateSmart(done, total, msg, true);

                    if (string.Equals(flag, "DONE", StringComparison.OrdinalIgnoreCase))
                    {
                        timer.Stop();
                        win.Done("PDF export completed.", total, "Background Export Done");
                        AppDialog.Info(uiapp, "Publish BOM", "Background PDF export completed.");
                    }
                    else if (string.Equals(flag, "ERROR", StringComparison.OrdinalIgnoreCase))
                    {
                        timer.Stop();
                        win.Canceled("Background Export Error", done, msg);
                        AppDialog.Warn(uiapp, "Publish BOM", "Background export failed:\n" + msg);
                    }
                }
            };
            timer.Start();
        }

        // =====================================================================================
        // Background worker entry points (called from BackgroundPublishRunner)
        // =====================================================================================

        internal static List<ViewSheet> GetSheetsByNumber_Internal(Document doc, List<string> sheetNumbers)
        {
            var set = new HashSet<string>(sheetNumbers ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return GetSheetsByNumber(doc, set);
        }

        internal static void ExportPdfsWithChecksum_Background(
            Document doc,
            string publishRoot,
            IList<ViewSheet> allowedSheets,
            Dictionary<string, BackgroundPublishJob.RevSnap> snapshot,
            string progressPath,
            string cancelPath)
        {
            var currentSnapshot = new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);

            if (snapshot != null)
            {
                foreach (var kv in snapshot)
                {
                    currentSnapshot[kv.Key] = new SheetChecksum
                    {
                        SheetNumber = kv.Key,
                        RevisionNumber = (kv.Value?.RevisionNumber ?? "").Trim(),
                        RevisionDate = (kv.Value?.RevisionDate ?? "").Trim(),
                        VersionFolder = ""
                    };
                }
            }

            ExportPdfsWithChecksum_BackgroundImpl(doc, publishRoot, allowedSheets, currentSnapshot, progressPath, cancelPath);
        }


        // =====================================================================================
        // Background PDF export core (NO UI) - optimized batching + progress file + cancel
        // =====================================================================================

        private static void ExportPdfsWithChecksum_BackgroundImpl(
    Document doc,
    string publishRoot,
    IList<ViewSheet> allowedSheets,
    Dictionary<string, SheetChecksum> currentSnapshot,
    string progressPath,
    string cancelPath)
        {
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (string.IsNullOrWhiteSpace(publishRoot))
            {
                WriteProgressFile(progressPath, "ERROR", 0, 0, "Publish root is empty.");
                return;
            }

            publishRoot = publishRoot.Trim();
            if (!Directory.Exists(publishRoot)) Directory.CreateDirectory(publishRoot);

            if (allowedSheets == null) allowedSheets = new List<ViewSheet>();

            // If nothing eligible, still write checksum (empty subset) and exit
            if (allowedSheets.Count == 0)
            {
                if (currentSnapshot != null)
                    WriteChecksum(publishRoot, currentSnapshot.Values.OrderBy(x => x.SheetNumber).ToList());

                WriteProgressFile(progressPath, "DONE", 0, 0, "No eligible sheets for PDF export.");
                return;
            }

            // Load old checksum for crash-resume
            var old = ReadChecksum(publishRoot);

            // Build current snapshot dict for allowed subset (already filtered by CSV)
            var current = new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);
            foreach (var sh in allowedSheets)
            {
                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;

                if (currentSnapshot != null && currentSnapshot.TryGetValue(sNo, out var snap) && snap != null)
                {
                    current[sNo] = new SheetChecksum
                    {
                        SheetNumber = sNo,
                        RevisionNumber = (snap.RevisionNumber ?? "").Trim(),
                        RevisionDate = (snap.RevisionDate ?? "").Trim(),
                        VersionFolder = ""
                    };
                }
            }

            if (current.Count == 0)
            {
                WriteChecksum(publishRoot, new List<SheetChecksum>());
                WriteProgressFile(progressPath, "DONE", 0, 0, "No snapshot rows for eligible sheets.");
                return;
            }

            string pdfRoot = EnsureDir(Path.Combine(publishRoot, "PDF"));

            bool firstRun = old.Count == 0;

            // Diff list
            var toExport = new List<ViewSheet>();
            bool hasRevChange = false;

            foreach (var sh in allowedSheets)
            {
                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;
                if (!current.TryGetValue(sNo, out var cur) || cur == null) continue;

                if (firstRun)
                {
                    toExport.Add(sh);
                    continue;
                }

                if (!old.TryGetValue(sNo, out var prev) || prev == null)
                {
                    toExport.Add(sh);
                    continue;
                }

                bool changed = !StringEquals(prev.RevisionNumber, cur.RevisionNumber) ||
                               !StringEquals(prev.RevisionDate, cur.RevisionDate);

                if (changed)
                {
                    hasRevChange = true;
                    toExport.Add(sh);
                }
            }

            if (toExport.Count == 0)
            {
                // Align checksum to current allowed subset, preserving VersionFolder when possible
                foreach (var kv in current)
                {
                    if (old.TryGetValue(kv.Key, out var prev) && prev != null && !string.IsNullOrWhiteSpace(prev.VersionFolder))
                        kv.Value.VersionFolder = prev.VersionFolder;
                }

                WriteChecksum(publishRoot, current.Values.OrderBy(x => x.SheetNumber).ToList());
                WriteProgressFile(progressPath, "DONE", 0, 0, "No PDF changes detected. Checksum updated.");
                return;
            }

            // Decide version folder (same rule as in-session)
            string newVersion;
            if (firstRun || hasRevChange)
            {
                newVersion = GetNextVersionFolderName(pdfRoot);
            }
            else
            {
                newVersion = TryGetMaxVersionFolder(old);
                if (string.IsNullOrWhiteSpace(newVersion))
                    newVersion = GetNextVersionFolderName(pdfRoot);
            }

            // Assign VersionFolder
            foreach (var kv in current)
            {
                var key = kv.Key;
                var cur = kv.Value;

                if (!firstRun && old.TryGetValue(key, out var prev) && prev != null &&
                    StringEquals(prev.RevisionNumber, cur.RevisionNumber) &&
                    StringEquals(prev.RevisionDate, cur.RevisionDate) &&
                    !string.IsNullOrWhiteSpace(prev.VersionFolder))
                {
                    cur.VersionFolder = prev.VersionFolder;
                }
                else
                {
                    cur.VersionFolder = newVersion;
                }
            }

            // Create folders
            string vRoot = EnsureDir(Path.Combine(pdfRoot, newVersion));
            string vPdf = EnsureDir(Path.Combine(vRoot, "pdf"));
            EnsureDir(Path.Combine(vRoot, "preview"));

            WriteProgressFile(progressPath, "PROG", 0, toExport.Count, "Starting PDF export…");

            // Per-sheet checksum update callback
            Action<string> onSheetExported = (sheetNo) =>
            {
                sheetNo = (sheetNo ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sheetNo)) return;

                if (current.TryGetValue(sheetNo, out var snap) && snap != null)
                {
                    old[sheetNo] = snap;
                    WriteChecksum(publishRoot, old.Values.OrderBy(x => x.SheetNumber).ToList());
                }
            };

            bool cancelled = BatchExportPdf_Background(doc, toExport, vPdf, progressPath, cancelPath, onSheetExported);

            // Final merge for allowed subset
            foreach (var kv in current)
                old[kv.Key] = kv.Value;

            WriteChecksum(publishRoot, old.Values.OrderBy(x => x.SheetNumber).ToList());

            if (cancelled)
            {
                WriteProgressFile(progressPath, "DONE", 0, toExport.Count, "Cancelled. Checksum updated.");
                return;
            }

            WriteProgressFile(progressPath, "DONE", toExport.Count, toExport.Count, "PDF export completed. Checksum updated.");
        }

        private static bool BatchExportPdf_Background(
    Document doc,
    IList<ViewSheet> sheets,
    string outDir,
    string progressPath,
    string cancelPath,
    Action<string> onSheetExported)
        {
            if (doc == null || sheets == null || sheets.Count == 0) return false;

            EnsureDir(outDir);

            const int BATCH_SIZE = 100; // tune if needed

            var sheetInfos = sheets
                .Select(sh => new
                {
                    Sheet = sh,
                    SheetNo = (sh.SheetNumber ?? "").Trim(),
                    SafeNo = SanitizeFileName((sh.SheetNumber ?? "").Trim()),
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.SheetNo))
                .ToList();

            
            int done = 0;
            int total = sheetInfos.Count;

            for (int start = 0; start < sheetInfos.Count; start += BATCH_SIZE)
            {
                if (IsCancelRequested(cancelPath))
                    return true;

                var batch = sheetInfos.Skip(start).Take(BATCH_SIZE).ToList();
                if (batch.Count == 0) continue;

                var viewIds = batch.Select(x => x.Sheet.Id).ToList();

                // Pre-existing PDFs
                var beforeNames = new HashSet<string>(
                    Directory.GetFiles(outDir, "*.pdf").Select(Path.GetFileName),
                    StringComparer.OrdinalIgnoreCase);

                try
                {
                    RevitApiCompatibility.ExportPdf(doc, outDir, viewIds);
                }
                catch
                {
                    // continue best-effort
                }

                if (IsCancelRequested(cancelPath))
                    return true;

                // New PDFs after export
                var after = Directory.GetFiles(outDir, "*.pdf")
                    .Select(p => new FileInfo(p))
                    .OrderByDescending(fi => fi.LastWriteTime)
                    .ToList();

                var remaining = after.Where(fi => !beforeNames.Contains(fi.Name)).ToList();

                foreach (var x in batch)
                {
                    if (IsCancelRequested(cancelPath))
                        return true;

                    string desired = Path.Combine(outDir, x.SafeNo + ".pdf");

                    FileInfo match = remaining.FirstOrDefault(fi =>
                    {
                        string n = Path.GetFileNameWithoutExtension(fi.Name) ?? "";
                        return n.StartsWith(x.SafeNo, StringComparison.OrdinalIgnoreCase);
                    });

                    if (match == null)
                    {
                        match = remaining.FirstOrDefault(fi =>
                        {
                            string n = Path.GetFileNameWithoutExtension(fi.Name) ?? "";
                            return n.IndexOf(x.SafeNo, StringComparison.OrdinalIgnoreCase) >= 0;
                        });
                    }

                    if (match == null && remaining.Count > 0)
                        match = remaining[0];

                    if (match != null)
                    {
                        try
                        {
                            remaining.Remove(match);
                            try { if (File.Exists(desired)) File.Delete(desired); } catch { }
                            File.Move(match.FullName, desired);
                        }
                        catch { }

                        // ✅ Only mark as exported if expected PDF exists
                        try
                        {
                            if (File.Exists(desired))
                                onSheetExported?.Invoke(x.SheetNo);
                        }
                        catch { }
                    }
                }

                done += batch.Count;
                WriteProgressFile(progressPath, "PROG", done, total, "Exported " + done + " / " + total);
            }

            return false;
        }

        private static bool IsCancelRequested(string cancelPath)
        {
            try { return !string.IsNullOrWhiteSpace(cancelPath) && File.Exists(cancelPath); }
            catch { return false; }
        }

        // Progress format: FLAG|done|total|message
        private static void WriteProgressFile(string progressPath, string flag, int done, int total, string message)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(progressPath)) return;
                if (total < 0) total = 0;
                if (done < 0) done = 0;

                File.WriteAllText(progressPath,
                    (flag ?? "PROG") + "|" + done.ToString(CultureInfo.InvariantCulture) + "|" +
                    total.ToString(CultureInfo.InvariantCulture) + "|" + (message ?? ""));
            }
            catch { }
        }

        // =====================================================================================
        // CSV builder (filters: must have SheetNumber AND Revision number)
        // =====================================================================================

        private sealed class CsvBuild
        {
            public string[] Headers;
            public List<string[]> Rows = new List<string[]>();
            public HashSet<string> IncludedSheetNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Snapshot used for checksum/versioning consistency
            public Dictionary<string, SheetChecksum> RevSnapshot = new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);
        }

        private static CsvBuild BuildDrawingRegisterCsv(Document doc)
        {
            var result = new CsvBuild();

            // ---- Project Information (single values for all rows) ----
            var projInfo = doc.ProjectInformation;
            string projectName = GetParamText(projInfo, "Project Name");
            string projectNumber = GetParamText(projInfo, "Project Number");
            string clientName = GetParamText(projInfo, "Client Name");

            // Collect all sheets once
            var sheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .WhereElementIsNotElementType()
                .Cast<ViewSheet>()
                .ToList();

            // Collect all titleblocks once and map by sheet id (OwnerViewId)
            var titleblocks = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .ToElements();

            var tbBySheetId = new Dictionary<long, Element>();
            foreach (var tb in titleblocks)
            {
                try
                {
                    var sid = tb.OwnerViewId;
                    if (sid != null && !RevitApiCompatibility.IsInvalidElementId(sid))
                    {
                        if (!tbBySheetId.ContainsKey(RevitApiCompatibility.GetElementIdValue(sid)))
                            tbBySheetId[RevitApiCompatibility.GetElementIdValue(sid)] = tb;
                    }
                }
                catch { }
            }

            result.Headers = new[]
            {
                "Project Name",
                "Project Number",
                "Client Name",
                "Drawing Category",
                "Drawing Sub-Category",
                "Vic_Zone",
                "System Type",           // from Vic_System_PT
                "Sheet Number",
                "Sheet Name",
                "System abbreviation",   // from Vic_System_PT
                "Level",                 // from sheet param 'Titleblock Level'
                "Revision number",
                "Revision Date",
                "revision description"
            };

            foreach (var sh in sheets)
            {
                try { if (sh.IsPlaceholder) continue; } catch { }

                tbBySheetId.TryGetValue(RevitApiCompatibility.GetElementIdValue(sh.Id), out var tb);

                string sheetNumber = SafeStr(sh.SheetNumber).Trim();
                if (string.IsNullOrWhiteSpace(sheetNumber) || sheetNumber == "0")
                    continue;

                // Revision info (as displayed on sheet)
                var rev = GetLatestRevisionInfo(doc, sh);
                string revNumber = (rev.RevNumber ?? "").Trim();

                // Requirement: If no revision number, do NOT include in CSV
                if (string.IsNullOrWhiteSpace(revNumber))
                    continue;

                string drawingCat = GetSheetField(sh, tb, "Drawing Category");
                string drawingSub = GetSheetField(sh, tb, "Drawing Sub-Category");
                string vicZone = GetSheetField(sh, tb, "Vic_Zone");

                string vicSystem = GetSheetField(sh, tb, "Vic_System_PT");
                string systemType = vicSystem;
                string systemAbbr = vicSystem;

                string level = GetParamText(sh, "Titleblock Level");
                string sheetName = SafeStr(sh.Name);

                result.Rows.Add(new[]
                {
                    projectName,
                    projectNumber,
                    clientName,
                    drawingCat,
                    drawingSub,
                    vicZone,
                    systemType,
                    sheetNumber,
                    sheetName,
                    systemAbbr,
                    level,
                    revNumber,
                    (rev.RevDate ?? "").Trim(),
                    (rev.RevDesc ?? "").Trim()
                });

                result.IncludedSheetNumbers.Add(sheetNumber);

                // Snapshot for checksum/version plan (VersionFolder is decided later)
                result.RevSnapshot[sheetNumber] = new SheetChecksum
                {
                    SheetNumber = sheetNumber,
                    RevisionNumber = revNumber,
                    RevisionDate = (rev.RevDate ?? "").Trim(),
                    VersionFolder = string.Empty
                };
            }

            return result;
        }

        private static List<ViewSheet> GetSheetsByNumber(Document doc, HashSet<string> sheetNumbers)
        {
            var list = new List<ViewSheet>();
            if (doc == null || sheetNumbers == null || sheetNumbers.Count == 0) return list;

            var all = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .WhereElementIsNotElementType()
                .Cast<ViewSheet>()
                .ToList();

            foreach (var sh in all)
            {
                try { if (sh.IsPlaceholder) continue; } catch { }

                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;

                if (sheetNumbers.Contains(sNo))
                    list.Add(sh);
            }

            return list;
        }

        // =====================================================================================
        // PDF Export + Checksum + PDF\V# versioning
        // - Only processes sheets passed in (CSV-included subset).
        // =====================================================================================

        private const string CHECKSUM_FILE = "DrawingRegisterCheckSum.csv";

        private sealed class SheetChecksum
        {
            public string SheetNumber;
            public string RevisionNumber;
            public string RevisionDate;
            public string VersionFolder;
        }


        private static Result ExportPdfsWithChecksum(
            UIApplication uiapp,
            Document doc,
            string publishRoot,
            IList<ViewSheet> allowedSheets,
            Dictionary<string, SheetChecksum> currentSnapshot)
        {
            if (doc == null) return Result.Failed;
            if (string.IsNullOrWhiteSpace(publishRoot)) return Result.Succeeded;

            publishRoot = publishRoot.Trim();
            if (!Directory.Exists(publishRoot))
                Directory.CreateDirectory(publishRoot);

            if (allowedSheets == null || allowedSheets.Count == 0)
            {
                AppDialog.Warn(uiapp, "Publish BOM", "Export PDF is enabled, but there are no sheets eligible for export (CSV filter removed all sheets).");
                return Result.Succeeded;
            }

            // Ensure checksum file is visible immediately (even before the first successful export callback).
            // This keeps crash-resume diagnostics simple: you will always see the file in publishRoot.
            try
            {
                string chkPath = GetChecksumPath(publishRoot);
                if (!File.Exists(chkPath))
                {
                    File.WriteAllText(chkPath, "SheetNumber,RevisionNumber,RevisionDate,VersionFolder" + Environment.NewLine);
                }
            }
            catch { /* best-effort */ }

            // Load old checksum (persisted state). This enables crash-resume.
            var old = ReadChecksum(publishRoot);

            // Build current snapshot dictionary for the allowed subset
            var current = new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);
            foreach (var sh in allowedSheets)
            {
                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;

                if (currentSnapshot != null && currentSnapshot.TryGetValue(sNo, out var snap) && snap != null)
                {
                    current[sNo] = new SheetChecksum
                    {
                        SheetNumber = sNo,
                        RevisionNumber = (snap.RevisionNumber ?? "").Trim(),
                        RevisionDate = (snap.RevisionDate ?? "").Trim(),
                        VersionFolder = ""
                    };
                }
                else
                {
                    // Fallback (rare): compute latest revision info from the sheet
                    var rev = GetLatestRevisionInfo(doc, sh);
                    current[sNo] = new SheetChecksum
                    {
                        SheetNumber = sNo,
                        RevisionNumber = (rev.RevNumber ?? "").Trim(),
                        RevisionDate = (rev.RevDate ?? "").Trim(),
                        VersionFolder = ""
                    };
                }
            }

            if (current.Count == 0)
                return Result.Succeeded;

            string pdfRoot = EnsureDir(Path.Combine(publishRoot, "PDF"));

            bool firstRun = old.Count == 0;

            // Categorize export work to preserve your rule:
            // - New version folder contains ONLY updated sheets (revision change).
            // - Crash-resume / missing PDFs continue in the existing version folder(s).
            var exportChanged = new List<ViewSheet>(); // revision changed -> export to new version
            var exportMissingRow = new List<ViewSheet>(); // missing checksum row (new sheet or crash before writing row) -> export to resume version
            var exportMissingFileByFolder = new Dictionary<string, List<ViewSheet>>(StringComparer.OrdinalIgnoreCase); // unchanged rev but PDF missing -> export back to that folder

            bool hasRevChange = false;

            int eligible = 0;
            int alreadyOk = 0;

            // Determine the "resume" version (latest V#) for cases where we need a folder but don't have one.
            // Only used when there is no revision change.
            string resumeVersion = null;
            if (!firstRun)
            {
                resumeVersion = TryGetMaxVersionFolder(old);
                if (string.IsNullOrWhiteSpace(resumeVersion))
                    resumeVersion = GetNextVersionFolderName(pdfRoot); // best-effort fallback
            }

            foreach (var sh in allowedSheets)
            {
                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;
                if (!current.TryGetValue(sNo, out var cur) || cur == null) continue;

                eligible++;

                if (firstRun)
                {
                    exportMissingRow.Add(sh); // first run exports everything into V1 (handled below)
                    continue;
                }

                if (!old.TryGetValue(sNo, out var prev) || prev == null)
                {
                    // No checksum row - either new sheet or crash before row was written.
                    exportMissingRow.Add(sh);
                    continue;
                }

                bool changed = !StringEquals(prev.RevisionNumber, cur.RevisionNumber) ||
                               !StringEquals(prev.RevisionDate, cur.RevisionDate);

                if (changed)
                {
                    hasRevChange = true;
                    exportChanged.Add(sh);
                    continue;
                }

                // Same revision - ensure expected PDF exists in the previously recorded folder.
                string prevFolder = (prev.VersionFolder ?? "").Trim();
                bool missingFile = string.IsNullOrWhiteSpace(prevFolder) || !ExpectedPdfExists(pdfRoot, prevFolder, sNo);

                if (missingFile)
                {

                    // If we don't know the previous folder, continue in the resume version (latest).
                    string folder = string.IsNullOrWhiteSpace(prevFolder) ? resumeVersion : prevFolder;

                    if (!exportMissingFileByFolder.TryGetValue(folder, out var list))
                    {
                        list = new List<ViewSheet>();
                        exportMissingFileByFolder[folder] = list;
                    }
                    list.Add(sh);
                    continue;
                }

                alreadyOk++;
            }

            // Nothing to export
            if (!firstRun && exportChanged.Count == 0 && exportMissingRow.Count == 0 && exportMissingFileByFolder.Count == 0)
            {
                // Keep checksum aligned to current subset (preserve VersionFolder from old).
                foreach (var kv in current)
                {
                    if (old.TryGetValue(kv.Key, out var prev) && prev != null && !string.IsNullOrWhiteSpace(prev.VersionFolder))
                        kv.Value.VersionFolder = prev.VersionFolder;
                }

                WriteChecksum(publishRoot, current.Values.OrderBy(x => x.SheetNumber).ToList());
                return Result.Succeeded;
            }

            // Decide folders:
            // - First run: everything goes to a new version folder (V1).
            // - Revision changes: changed sheets go to a NEW version folder (Vnext).
            // - Missing-only: continue on existing folders (from checksum) or resumeVersion when unknown.
            string newVersion = null;
            if (firstRun)
            {
                newVersion = GetNextVersionFolderName(pdfRoot);
                resumeVersion = newVersion; // first run's "resume" is the same folder
            }
            else
            {
                // Only create a new version if there is an actual revision change.
                if (hasRevChange)
                    newVersion = GetNextVersionFolderName(pdfRoot);
            }

            // Assign VersionFolder for checksum snapshots:
            // 1) default: keep old folder if unchanged, else set per export category
            foreach (var kv in current)
            {
                string key = kv.Key;
                var cur = kv.Value;

                if (!firstRun && old.TryGetValue(key, out var prev) && prev != null &&
                    StringEquals(prev.RevisionNumber, cur.RevisionNumber) &&
                    StringEquals(prev.RevisionDate, cur.RevisionDate) &&
                    !string.IsNullOrWhiteSpace(prev.VersionFolder))
                {
                    cur.VersionFolder = prev.VersionFolder;
                }
                else
                {
                    // new sheet OR changed revision OR first run
                    // new sheets without revision change export into resumeVersion
                    if (!firstRun && !hasRevChange)
                        cur.VersionFolder = resumeVersion;
                    else
                        cur.VersionFolder = newVersion ?? resumeVersion;
                }
            }

            // For missing-file exports, force VersionFolder to the folder we are actually exporting into (prev or resume)
            foreach (var kv in exportMissingFileByFolder)
            {
                string folder = kv.Key;
                foreach (var sh in kv.Value)
                {
                    string sNo = (sh.SheetNumber ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(sNo)) continue;
                    if (current.TryGetValue(sNo, out var snap) && snap != null)
                        snap.VersionFolder = folder;
                }
            }

            // For missing-row exports (new/crash), export into resumeVersion unless firstRun exports into newVersion
            string missingRowFolder = firstRun ? newVersion : resumeVersion;
            foreach (var sh in exportMissingRow)
            {
                string sNo = (sh.SheetNumber ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sNo)) continue;
                if (current.TryGetValue(sNo, out var snap) && snap != null)
                    snap.VersionFolder = missingRowFolder;
            }

            // For changed exports, export into newVersion (only updated sheets in next version)
            if (hasRevChange && !string.IsNullOrWhiteSpace(newVersion))
            {
                foreach (var sh in exportChanged)
                {
                    string sNo = (sh.SheetNumber ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(sNo)) continue;
                    if (current.TryGetValue(sNo, out var snap) && snap != null)
                        snap.VersionFolder = newVersion;
                }
            }

            // Crash-resume: update checksum immediately after each successful sheet export.
            Action<string> onSheetExported = (sheetNo) =>
            {
                sheetNo = (sheetNo ?? "").Trim();
                if (string.IsNullOrWhiteSpace(sheetNo)) return;

                if (current.TryGetValue(sheetNo, out var snap) && snap != null)
                {
                    old[sheetNo] = snap;
                    WriteChecksum(publishRoot, old.Values.OrderBy(x => x.SheetNumber).ToList());
                }
            };

            // 1) Export missing PDFs back into their recorded folders (or resume folder when unknown)
            foreach (var kv in exportMissingFileByFolder.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                if (kv.Value == null || kv.Value.Count == 0) continue;

                string folder = kv.Key;
                string vRoot = EnsureDir(Path.Combine(pdfRoot, folder));
                string vPdf = EnsureDir(Path.Combine(vRoot, "pdf"));
                EnsureDir(Path.Combine(vRoot, "preview"));

                var res = ExportPdfWithProgress(uiapp, doc, kv.Value, vPdf, onSheetExported);
                if (res != Result.Succeeded) return res;
            }

            // 2) Export missing checksum rows into resume folder (first run uses newVersion)
            if (exportMissingRow.Count > 0)
            {
                string folder = missingRowFolder;
                string vRoot = EnsureDir(Path.Combine(pdfRoot, folder));
                string vPdf = EnsureDir(Path.Combine(vRoot, "pdf"));
                EnsureDir(Path.Combine(vRoot, "preview"));

                var res = ExportPdfWithProgress(uiapp, doc, exportMissingRow, vPdf, onSheetExported);
                if (res != Result.Succeeded) return res;
            }

            // 3) Export revision-changed sheets into new version folder (ONLY updated PDFs)
            if (exportChanged.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(newVersion))
                    newVersion = GetNextVersionFolderName(pdfRoot);

                string vRoot = EnsureDir(Path.Combine(pdfRoot, newVersion));
                string vPdf = EnsureDir(Path.Combine(vRoot, "pdf"));
                EnsureDir(Path.Combine(vRoot, "preview"));

                var res = ExportPdfWithProgress(uiapp, doc, exportChanged, vPdf, onSheetExported);
                if (res != Result.Succeeded) return res;
            }

            // Final write: ensure checksum contains the latest for every current key
            foreach (var kv in current)
                old[kv.Key] = kv.Value;

            WriteChecksum(publishRoot, old.Values.OrderBy(x => x.SheetNumber).ToList());
            return Result.Succeeded;
        }



        private static Result ExportPdfWithProgress(
            UIApplication uiapp,
            Document doc,
            IList<ViewSheet> sheets,
            string outDir,
            Action<string> onSheetExported)
        {
            if (doc == null || sheets == null || sheets.Count == 0) return Result.Succeeded;

            EnsureDir(outDir);

            // Batch export reduces the number of doc.Export(...) calls.
            // We still expect PDFs to appear one-by-one on disk (Revit exports sequentially),
            // but the export engine initialization happens only once per batch.
            const int BATCH_SIZE = 100; // lower this on low-RAM machines if needed

            var sheetInfos = sheets
                .Select(sh => new
                {
                    Sheet = sh,
                    SheetNo = (sh.SheetNumber ?? "").Trim(),
                    SafeNo = SanitizeFileName((sh.SheetNumber ?? "").Trim()),
                })
                .Where(x => !string.IsNullOrWhiteSpace(x.SheetNo))
                .ToList();

            int total = sheetInfos.Count;
            if (total == 0) return Result.Succeeded;

            var win = new ProgressWindow();

            try
            {
                var hwnd = Process.GetCurrentProcess().MainWindowHandle;
                try { new WindowInteropHelper(win).Owner = hwnd; } catch { }

                win.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                win.Initialize(total, "Exporting PDFs…", "Preparing…");
                win.Show();

                win.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                win.Dispatcher.Invoke(() => { }, DispatcherPriority.Background);

                
                int done = 0;

                for (int start = 0; start < sheetInfos.Count; start += BATCH_SIZE)
                {
                    if (win.IsCanceled)
                        break;

                    var batch = sheetInfos.Skip(start).Take(BATCH_SIZE).ToList();
                    if (batch.Count == 0) continue;

                    int batchStart = start + 1;
                    int batchEnd = start + batch.Count;

                    win.UpdateSmart(done, total, "Exporting batch… " + batchStart + " - " + batchEnd + " / " + total);

                    // Export each batch into a clean temp folder to avoid ambiguous "new file" matching
                    // inside a growing output directory. This makes checksum updates reliable.
                    string tempDir = Path.Combine(outDir, "_tmp_pdf_" + Guid.NewGuid().ToString("N"));
                    EnsureDir(tempDir);

                    try
                    {
                        var viewIds = batch.Select(x => x.Sheet.Id).ToList();

                        try
                        {
                            RevitApiCompatibility.ExportPdf(doc, tempDir, viewIds);
                        }
                        catch
                        {
                            // best-effort; we'll still try to move whatever got produced
                        }

                        if (win.IsCanceled)
                            break;

                        // Get produced PDFs for this batch only
                        var produced = Directory.GetFiles(tempDir, "*.pdf")
                            .Select(p => new FileInfo(p))
                            .OrderBy(fi => fi.LastWriteTimeUtc)
                            .ThenBy(fi => fi.Name, StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        // Map by export order (batch order). Revit exports sequentially.
                        int mapCount = Math.Min(batch.Count, produced.Count);

                        for (int i = 0; i < mapCount; i++)
                        {
                            if (win.IsCanceled)
                                break;

                            var item = batch[i];
                            var src = produced[i];

                            string desired = Path.Combine(outDir, item.SafeNo + ".pdf");

                            try
                            {
                                try { if (File.Exists(desired)) File.Delete(desired); } catch { }
                                File.Move(src.FullName, desired);
                            }
                            catch
                            {
                                // If move fails, skip checksum update for this sheet (crash-resume safety)
                            }

                            // Only mark exported if the expected file exists in the final location
                            if (File.Exists(desired))
                            {
                                try { onSheetExported?.Invoke(item.SheetNo); } catch { }
                            }

                            done++;
                            win.UpdateSmart(done, total, "Exported " + done + " / " + total + " (" + item.SheetNo + ")");
                        }

                        // If produced count doesn't match batch count, show progress but don't mark missing ones.
                        // Those sheets will be picked up again in the next run via "missing PDF" detection.
                        if (produced.Count != batch.Count)
                        {
                            // Count remaining sheets as not exported (no done increment here)
                        }
                    }
                    finally
                    {
                        // Clean temp folder (best-effort)
                        try { Directory.Delete(tempDir, true); } catch { }
                    }
                }

                if (win.IsCanceled)
                {
                    win.Canceled("Export Cancelled", done, "PDF export was cancelled.");
                    return Result.Cancelled;
                }

                win.UpdateSmart(total, total, "Finalizing…", true);
                win.Done("PDF export completed", total, "Export Completed");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                try { if (win.IsVisible) win.Close(); } catch { }
                try { AppDialog.Warn(uiapp, "Publish BOM", ex.ToString()); } catch { }
                return Result.Failed;
            }
            finally
            {
                try { if (win.IsVisible) win.Close(); } catch { }
            }
        }


        private static Dictionary<string, SheetChecksum> ReadChecksum(string publishRoot)
        {
            var dict = new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);
            var path = GetChecksumPath(publishRoot);

            if (!File.Exists(path))
                return dict;

            try
            {
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    var line = lines[i];
                    if (i == 0) continue; // header
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var cols = SplitCsvLine(line);
                    if (cols.Count < 4) continue;

                    string sNo = (cols[0] ?? string.Empty).Trim();
                    if (string.IsNullOrEmpty(sNo)) continue;

                    dict[sNo] = new SheetChecksum
                    {
                        SheetNumber = sNo,
                        RevisionNumber = (cols[1] ?? string.Empty).Trim(),
                        RevisionDate = (cols[2] ?? string.Empty).Trim(),
                        VersionFolder = (cols[3] ?? string.Empty).Trim(),
                    };
                }
            }
            catch
            {
                // malformed checksum -> treat as first run
                return new Dictionary<string, SheetChecksum>(StringComparer.OrdinalIgnoreCase);
            }

            return dict;
        }

        private static string GetChecksumPath(string publishRoot)
        {
            throw new NotImplementedException();
        }

        private static void WriteChecksum(string publishRoot, List<SheetChecksum> rows)
        {
            var path = GetChecksumPath(publishRoot);
            var tmp = path + ".tmp";

            // Atomic-ish: write temp then replace/move.
            using (var sw = new StreamWriter(tmp, false, new UTF8Encoding(true)))
            {
                sw.WriteLine("Sheet Number,Revision Number,Revision Date,VersionFolder");

                if (rows != null)
                {
                    foreach (var r in rows)
                    {
                        if (r == null) continue;
                        sw.WriteLine(string.Join(",",
                            Csv(r.SheetNumber),
                            Csv(r.RevisionNumber),
                            Csv(r.RevisionDate),
                            Csv(r.VersionFolder)
                        ));
                    }
                }
            }

            try
            {
                if (File.Exists(path))
                    File.Delete(path);

                File.Move(tmp, path);
            }
            catch
            {
                // fallback best-effort
                try { File.Copy(tmp, path, true); } catch { }
                try { File.Delete(tmp); } catch { }
            }
        }

        private static string Csv(string s)
        {
            s = s ?? string.Empty;
            if (s.Contains(",") || s.Contains("\"") || s.Contains("\n") || s.Contains("\r"))
            {
                s = s.Replace("\"", "\"\"");
                return "\"" + s + "\"";
            }
            return s;
        }

        private static List<string> SplitCsvLine(string line)
        {
            var result = new List<string>();
            if (line == null) return result;

            var sb = new StringBuilder();
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            sb.Append('"');
                            i++;
                        }
                        else
                        {
                            inQuotes = false;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }
                else
                {
                    if (c == '"')
                        inQuotes = true;
                    else if (c == ',')
                    {
                        result.Add(sb.ToString());
                        sb.Clear();
                    }
                    else
                        sb.Append(c);
                }
            }

            result.Add(sb.ToString());
            return result;
        }

        // =====================================================================================
        // Versioning helpers
        // =====================================================================================

        private static bool ExpectedPdfExists(string pdfRoot, string versionFolder, string sheetNo)
        {
            if (string.IsNullOrWhiteSpace(pdfRoot)) return false;
            if (string.IsNullOrWhiteSpace(versionFolder)) return false;
            if (string.IsNullOrWhiteSpace(sheetNo)) return false;

            try
            {
                string safeNo = SanitizeFileName(sheetNo.Trim());
                string p = Path.Combine(pdfRoot, versionFolder.Trim(), "pdf", safeNo + ".pdf");
                return File.Exists(p);
            }
            catch
            {
                return false;
            }
        }



        private static string TryGetMaxVersionFolder(Dictionary<string, SheetChecksum> old)
        {
            if (old == null || old.Count == 0) return null;

            int max = 0;
            string best = null;

            foreach (var kv in old)
            {
                var v = kv.Value?.VersionFolder;
                if (string.IsNullOrWhiteSpace(v)) continue;

                v = v.Trim();
                if (!v.StartsWith("V", StringComparison.OrdinalIgnoreCase)) continue;

                if (int.TryParse(v.Substring(1), out int n))
                {
                    if (n > max)
                    {
                        max = n;
                        best = "V" + n;
                    }
                }
            }

            return best;
        }

        private static string GetNextVersionFolderName(string pdfRoot)
        {
            int max = 0;
            if (Directory.Exists(pdfRoot))
            {
                foreach (var dir in Directory.GetDirectories(pdfRoot))
                {
                    var name = Path.GetFileName(dir);
                    if (string.IsNullOrWhiteSpace(name)) continue;

                    if ((name[0] == 'V' || name[0] == 'v') && name.Length > 1)
                    {
                        int n;
                        if (int.TryParse(name.Substring(1), out n))
                            if (n > max) max = n;
                    }
                }
            }
            return "V" + (max + 1);
        }

        private static string EnsureDir(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
            return path;
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "SHEET";
            var invalid = Path.GetInvalidFileNameChars();
            var s = name.Trim();
            foreach (var ch in invalid) s = s.Replace(ch, '_');
            return s.Trim().TrimEnd('.');
        }

        private static bool StringEquals(string a, string b)
        {
            return string.Equals((a ?? "").Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
        }

        // =====================================================================================
        // Existing CSV helpers (kept; GetLatestRevisionInfo is your reliable method)
        // =====================================================================================

        private static string SafeStr(object x)
        {
            try { return x == null ? "" : x.ToString(); }
            catch { return ""; }
        }

        private static string GetParamText(Element elem, string paramName)
        {
            if (elem == null) return "";
            try
            {
                var p = elem.LookupParameter(paramName);
                if (p != null && p.HasValue)
                    return (p.AsString() ?? p.AsValueString() ?? "").Trim();
            }
            catch { }
            return "";
        }

        private static string GetSheetField(ViewSheet sheet, Element titleblock, string paramName)
        {
            var v = GetParamText(sheet, paramName);
            if (!string.IsNullOrWhiteSpace(v)) return v;
            return GetParamText(titleblock, paramName);
        }

        private static (string RevNumber, string RevDate, string RevDesc) GetLatestRevisionInfo(Document doc, ViewSheet sheet)
        {
            ICollection<ElementId> revIds;
            try { revIds = sheet.GetAllRevisionIds(); }
            catch { revIds = new List<ElementId>(); }

            Revision latest = null;
            int latestSeq = -1;
            ElementId latestId = null;

            foreach (var rid in revIds)
            {
                try
                {
                    var rev = doc.GetElement(rid) as Revision;
                    if (rev == null) continue;

                    int seq = rev.SequenceNumber;
                    if (seq > latestSeq)
                    {
                        latestSeq = seq;
                        latest = rev;
                        latestId = rid;
                    }
                }
                catch { }
            }

            if (latest == null || latestId == null)
                return ("", "", "");

            string revNumber = "";
            try { revNumber = sheet.GetRevisionNumberOnSheet(latestId) ?? ""; }
            catch { try { revNumber = SafeStr(latest.RevisionNumber); } catch { } }

            string revDate = "";
            string revDesc = "";

            try { revDate = SafeStr(latest.RevisionDate); } catch { }
            try { revDesc = SafeStr(latest.Description); } catch { }

            return (revNumber != null ? revNumber.Trim() : "", revDate != null ? revDate.Trim() : "", revDesc != null ? revDesc.Trim() : "");
        }

        private static void WriteCsvUtf8Bom(string path, string[] headers, List<string[]> rows)
        {
            var utf8Bom = new UTF8Encoding(true);

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read))
            using (var sw = new StreamWriter(fs, utf8Bom))
            {
                sw.NewLine = "\r\n";
                sw.WriteLine(string.Join(",", headers.Select(CsvEscape)));

                foreach (var r in rows)
                    sw.WriteLine(string.Join(",", r.Select(x => CsvEscape(SafeStr(x)))));
            }
        }

        private static string CsvEscape(string s)
        {
            if (s == null) return "";
            bool mustQuote = s.Contains(",") || s.Contains("\"") || s.Contains("\r") || s.Contains("\n");
            if (s.Contains("\"")) s = s.Replace("\"", "\"\"");
            return mustQuote ? ("\"" + s + "\"") : s;
        }

        private enum PdfExportMode
        {
            Cancel = 0,
            InSession = 1,
            Worker = 2
        }

        private static bool IsCloudModel(Document doc)
        {
            // Works across multiple Revit versions
            try
            {
                var prop = typeof(Document).GetProperty("IsModelInCloud");
                if (prop != null && prop.PropertyType == typeof(bool))
                    return (bool)prop.GetValue(doc, null);
            }
            catch { }
            return false;
        }

        private static PdfExportMode PromptPdfExportMode(UIApplication uiapp, Document doc)
        {
            string content =
                IsCloudModel(doc)
                    ? "This model is from ACC/BIM 360.\n\n" +
                      "• In-session export is the most reliable.\n" +
                      "• Worker export will SaveAs a temporary local RVT first, then export in a minimized Revit window."
                    : "• In-session export runs in your current Revit session.\n" +
                      "• Worker export runs in a separate minimized Revit process.";

            int selectedOption = AppDialog.Choose(
                uiapp,
                "Publish BOM - PDF Export",
                "PDFs need to be generated. Choose how to export:",
                content,
                new[]
                {
                    "Option 1: Export in current session (fastest start, blocks Revit)",
                    "Option 2: Export using worker (background, minimized Revit)"
                },
                defaultOptionIndex: 0);

            if (selectedOption == 0)
                return PdfExportMode.InSession;

            if (selectedOption == 1)
                return PdfExportMode.Worker;

            return PdfExportMode.Cancel;
        }

    }
}
