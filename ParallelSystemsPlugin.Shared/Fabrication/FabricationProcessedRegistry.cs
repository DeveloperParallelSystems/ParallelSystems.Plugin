using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
    internal static class FabricationProcessedRegistry
    {
        private const string RegistryHeader =
            "# Parallel Systems Fabrication STEP processed sources v1";

        public static HashSet<string> Read(Document doc)
        {
            HashSet<string> result =
                new HashSet<string>(
                    StringComparer.OrdinalIgnoreCase);

            try
            {
                string path = GetRegistryPath(doc);

                if (!File.Exists(path))
                    return result;

                foreach (string line in File.ReadAllLines(path))
                {
                    string value = line?.Trim();

                    if (string.IsNullOrWhiteSpace(value) ||
                        value.StartsWith(
                            "#",
                            StringComparison.Ordinal))
                    {
                        continue;
                    }

                    result.Add(value);
                }
            }
            catch
            {
                // Status storage must never prevent fabrication generation.
            }

            return result;
        }

        public static void MarkProcessed(
            Document doc,
            IEnumerable<ElementId> sourceElementIds)
        {
            if (doc == null || sourceElementIds == null)
                return;

            HashSet<string> values = Read(doc);

            foreach (ElementId elementId in
                     sourceElementIds.Distinct())
            {
                Element element = doc.GetElement(elementId);

                if (!string.IsNullOrWhiteSpace(element?.UniqueId))
                    values.Add(element.UniqueId);
            }

            string path = GetRegistryPath(doc);
            string directory = Path.GetDirectoryName(path);

            Directory.CreateDirectory(directory);

            string temporaryPath =
                path + "." + Guid.NewGuid().ToString("N") + ".tmp";

            try
            {
                IEnumerable<string> lines =
                    new[] { RegistryHeader }
                        .Concat(values.OrderBy(x => x));

                File.WriteAllLines(
                    temporaryPath,
                    lines,
                    new UTF8Encoding(true));

                if (File.Exists(path))
                    File.Replace(temporaryPath, path, null, true);
                else
                    File.Move(temporaryPath, path);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup only.
                }
            }
        }

        private static string GetRegistryPath(Document doc)
        {
            string root = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "Parallel Systems",
                "FabricationStep",
                "Processed");

            return Path.Combine(
                root,
                BuildDocumentKey(doc) + ".txt");
        }

        private static string BuildDocumentKey(Document doc)
        {
            string identity = null;

            try
            {
                if (doc != null && doc.IsWorkshared)
                {
                    ModelPath centralPath =
                        doc.GetWorksharingCentralModelPath();

                    if (centralPath != null)
                    {
                        identity =
                            ModelPathUtils
                                .ConvertModelPathToUserVisiblePath(
                                    centralPath);
                    }
                }
            }
            catch
            {
                // Fall back to the local path/title below.
            }

            if (string.IsNullOrWhiteSpace(identity))
                identity = doc?.PathName;

            if (string.IsNullOrWhiteSpace(identity))
                identity = doc?.Title ?? "Unsaved-Revit-Project";

            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(
                    Encoding.UTF8.GetBytes(
                        identity.Trim().ToUpperInvariant()));

                StringBuilder builder = new StringBuilder(64);

                foreach (byte value in hash)
                    builder.Append(value.ToString("x2"));

                return builder.ToString();
            }
        }
    }
}
