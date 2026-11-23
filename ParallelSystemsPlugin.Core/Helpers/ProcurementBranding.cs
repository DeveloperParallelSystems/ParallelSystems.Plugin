using System;
using System.IO;

namespace ParallelSystemsPlugin.Helpers
{
    public static class ProcurementBranding
    {
        private static readonly object LogoLock = new object();

        public static string GetDefaultCompanyLogoPath()
        {
            // Reports need a real file for Excel image embedding and PDF rendering.
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Parallel Systems", "Images", "ParallelSystems.png");
            lock (LogoLock)
            {
                try
                {
                    if (!File.Exists(path))
                    {
                        using (var resource = typeof(ProcurementBranding).Assembly.GetManifestResourceStream(
                            "ParallelSystemsPlugin.Procurement.CompanyLogo.png"))
                        {
                            if (resource == null) return "";
                            Directory.CreateDirectory(Path.GetDirectoryName(path));
                            using (var output = File.Create(path))
                                resource.CopyTo(output);
                        }
                    }
                    return path;
                }
                catch (IOException) { return ""; }
                catch (UnauthorizedAccessException) { return ""; }
            }
        }
    }
}
