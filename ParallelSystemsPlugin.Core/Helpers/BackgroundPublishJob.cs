using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace ParallelSystemsPlugin.Helpers
{
    public sealed class BackgroundPublishJob
    {
        public string JobId { get; set; }
        public string ModelPath { get; set; }
        public string PublishRoot { get; set; }
        public string JobDirectory { get; set; }
        public bool DeleteModelOnFinish { get; set; }
        public bool DeleteJobDirectoryOnFinish { get; set; } = true;


        // SheetNumbers that passed your CSV filter
        public List<string> AllowedSheetNumbers { get; set; } = new List<string>();

        // Optional: pass snapshot if you want strict consistency
        public Dictionary<string, RevSnap> RevisionSnapshot { get; set; } = new Dictionary<string, RevSnap>(StringComparer.OrdinalIgnoreCase);

        public sealed class RevSnap
        {
            public string RevisionNumber { get; set; }
            public string RevisionDate { get; set; }
        }

        public static string CreateJobFolder(string jobId)
        {
            string root = Path.Combine(Path.GetTempPath(), "ParallelSystems", "PublishJobs", jobId);
            Directory.CreateDirectory(root);
            return root;
        }

        public static void WriteJson(string path, BackgroundPublishJob job)
        {
            File.WriteAllText(path, JsonConvert.SerializeObject(job, Formatting.Indented));
        }

        public static BackgroundPublishJob ReadJson(string path)
        {
            return JsonConvert.DeserializeObject<BackgroundPublishJob>(File.ReadAllText(path));
        }

        public static void WriteProgress(string progressPath, int done, int total, string message, bool isDone = false, bool isError = false)
        {
            // Simple, robust text format: DONE|ERROR|done|total|message
            string flag = isError ? "ERROR" : (isDone ? "DONE" : "PROG");
            File.WriteAllText(progressPath, $"{flag}|{done}|{total}|{message ?? ""}");
        }

        public static bool TryReadProgress(string progressPath, out string flag, out int done, out int total, out string message)
        {
            flag = ""; done = 0; total = 0; message = "";
            try
            {
                if (!File.Exists(progressPath)) return false;
                var parts = (File.ReadAllText(progressPath) ?? "").Split(new[] { '|' }, 4);
                if (parts.Length < 4) return false;
                flag = parts[0];
                int.TryParse(parts[1], out done);
                int.TryParse(parts[2], out total);
                message = parts[3];
                return true;
            }
            catch { return false; }
        }

    }
}
