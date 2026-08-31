using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

#if REVIT_MODERN_DOTNET
using System.Runtime.Loader;
#endif

namespace ParallelSystemsPlugin.Classes
{
    /// <summary>
    /// Keeps PDFsharp/MigraDoc isolated to the DLL set deployed beside this add-in.
    ///
    /// Revit loads many add-ins in the same process. If another add-in loads an
    /// unsigned or different PDFsharp/MigraDoc assembly first, MigraDoc can fail
    /// during static initialization with errors such as:
    /// "A strongly-named assembly is required" or "Method not found".
    ///
    /// This class is intentionally called only when a PDF/BOM report is executed,
    /// not when the ribbon loads.
    /// </summary>
    public static class PdfRuntime
    {
        private const string ExpectedPdfSharpPublicKeyToken = "f94615aa0424f9eb";

        private static readonly object Sync = new object();
        private static bool _initialized;
        private static bool _resolverRegistered;

#if REVIT_LEGACY_DOTNET
        private static readonly string[] RequiredPdfFiles =
        {
            "PdfSharp-gdi.dll",
            "MigraDoc.DocumentObjectModel-gdi.dll",
            "MigraDoc.Rendering-gdi.dll"
        };

        private static readonly string[] PreferredLoadOrder =
        {
            "PdfSharp-gdi.dll",
            "PdfSharp.Charting-gdi.dll",
            "MigraDoc.DocumentObjectModel-gdi.dll",
            "MigraDoc.Rendering-gdi.dll",
            "MigraDoc.RtfRendering-gdi.dll"
        };
#else
        private static readonly string[] RequiredPdfFiles =
        {
            "PdfSharp.System.dll",
            "PdfSharp-gdi.dll",
            "MigraDoc.DocumentObjectModel.dll",
            "MigraDoc.Rendering-gdi.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll"
        };

        private static readonly string[] PreferredLoadOrder =
        {
            "Microsoft.Extensions.DependencyInjection.Abstractions.dll",
            "Microsoft.Extensions.Logging.Abstractions.dll",
            "Microsoft.Bcl.AsyncInterfaces.dll",
            "System.Runtime.CompilerServices.Unsafe.dll",
            "System.Buffers.dll",
            "System.Numerics.Vectors.dll",
            "System.Memory.dll",
            "System.Threading.Tasks.Extensions.dll",
            "System.Security.Cryptography.Pkcs.dll",
            "PdfSharp.System.dll",
            "PdfSharp.Shared.dll",
            "PdfSharp.Cryptography.dll",
            "PdfSharp.WPFonts.dll",
            "PdfSharp-gdi.dll",
            "MigraDoc.DocumentObjectModel.dll",
            "MigraDoc.Rendering-gdi.dll",
            "MigraDoc.RtfRendering-gdi.dll"
        };
#endif

        public static void EnsureInitialized()
        {
            if (_initialized)
                return;

            lock (Sync)
            {
                if (_initialized)
                    return;

                string pluginFolder = GetPluginFolder();

                RegisterAssemblyResolver(pluginFolder);
                ValidateLocalPdfAssemblies(pluginFolder);
                PreloadLocalPdfAssemblies(pluginFolder);
#if REVIT_MODERN_DOTNET
                ValidateNoConflictingPdfAssembliesAlreadyLoaded(pluginFolder);
#endif

                _initialized = true;
            }
        }

        public static bool IsSupportedImagePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return false;

            string extension = Path.GetExtension(path);
            return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetPluginFolder()
        {
            string location = typeof(PdfRuntime).Assembly.Location;
            string folder = Path.GetDirectoryName(location);

            if (string.IsNullOrWhiteSpace(folder))
                throw new InvalidOperationException("Unable to determine the ParallelSystemsPlugin add-in folder.");

            return folder;
        }

        private static void RegisterAssemblyResolver(string pluginFolder)
        {
            if (_resolverRegistered)
                return;

#if REVIT_MODERN_DOTNET
            AssemblyLoadContext.Default.Resolving += (context, assemblyName) => ResolveFromPluginFolder(pluginFolder, assemblyName);
#else
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) => ResolveFromPluginFolder(pluginFolder, new AssemblyName(args.Name));
#endif
            _resolverRegistered = true;
        }

        private static Assembly ResolveFromPluginFolder(string pluginFolder, AssemblyName requestedName)
        {
            if (requestedName == null || string.IsNullOrWhiteSpace(requestedName.Name))
                return null;

            if (!IsPdfRelatedAssemblyName(requestedName.Name) && !IsKnownPdfSupportAssemblyName(requestedName.Name))
                return null;

            Assembly alreadyLoaded = FindLoadedAssembly(requestedName.Name);
            if (alreadyLoaded != null)
                return alreadyLoaded;

            string dllPath = Path.Combine(pluginFolder, requestedName.Name + ".dll");
            if (!File.Exists(dllPath))
                return null;

#if REVIT_MODERN_DOTNET
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(dllPath);
#else
            return Assembly.LoadFrom(dllPath);
#endif
        }

        private static void ValidateLocalPdfAssemblies(string pluginFolder)
        {
            foreach (string file in RequiredPdfFiles)
            {
                string path = Path.Combine(pluginFolder, file);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        "A required PDF export dependency was not deployed beside the add-in. " +
                        "Deploy the full output folder, not only the main ParallelSystemsPlugin DLL. Missing file: " + file,
                        path);
                }
            }

#if REVIT_MODERN_DOTNET
            foreach (string file in Directory.GetFiles(pluginFolder, "*.dll")
                         .Where(path => IsPdfRelatedAssemblyName(Path.GetFileNameWithoutExtension(path))))
            {
                AssemblyName name = GetAssemblyNameSafe(file);
                string token = GetPublicKeyToken(name);

                if (!string.Equals(token, ExpectedPdfSharpPublicKeyToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileLoadException(
                        "The deployed PDFsharp/MigraDoc DLL set is invalid. " +
                        "Expected public key token '" + ExpectedPdfSharpPublicKeyToken +
                        "' but found '" + token + "'. File: " + file + ". " +
                        "Delete the Revit add-in deployment folder, clean bin/obj, restore from NuGet, rebuild, and deploy the full output folder.");
                }
            }
#endif
        }

        private static void PreloadLocalPdfAssemblies(string pluginFolder)
        {
            foreach (string file in PreferredLoadOrder)
            {
                string path = Path.Combine(pluginFolder, file);
                if (!File.Exists(path))
                    continue;

                AssemblyName localName = GetAssemblyNameSafe(path);
                Assembly loaded = FindLoadedAssembly(localName.Name);

                if (loaded != null)
                    continue;

#if REVIT_MODERN_DOTNET
                AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
#else
                Assembly.LoadFrom(path);
#endif
            }
        }

        private static void ValidateNoConflictingPdfAssembliesAlreadyLoaded(string pluginFolder)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                AssemblyName name = assembly.GetName();
                if (!IsPdfRelatedAssemblyName(name.Name))
                    continue;

                string location = GetAssemblyLocation(assembly);
                string token = GetPublicKeyToken(name);

                if (!string.Equals(token, ExpectedPdfSharpPublicKeyToken, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FileLoadException(
                        "A conflicting unsigned or incorrectly signed PDFsharp/MigraDoc assembly is already loaded in Revit. " +
                        "Assembly: " + name.Name + ". Token: '" + token + "'. Loaded from: " + location + ". " +
                        "Close Revit, remove stale PDFsharp/MigraDoc DLLs from other add-in folders if needed, then restart Revit.");
                }

                if (!IsSameOrChildPath(pluginFolder, location))
                {
                    throw new FileLoadException(
                        "A PDFsharp/MigraDoc assembly was loaded from outside the ParallelSystemsPlugin folder before BOM export started. " +
                        "Assembly: " + name.Name + ". Loaded from: " + location + ". Expected folder: " + pluginFolder + ". " +
                        "Revit cannot safely mix PDFsharp/MigraDoc DLLs from different add-ins. Restart Revit after deploying the full plugin folder.");
                }
            }
        }

        private static Assembly FindLoadedAssembly(string simpleName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in assemblies)
            {
                AssemblyName name = assembly.GetName();
                if (string.Equals(name.Name, simpleName, StringComparison.OrdinalIgnoreCase))
                    return assembly;
            }

            return null;
        }

        private static AssemblyName GetAssemblyNameSafe(string path)
        {
            try
            {
                return AssemblyName.GetAssemblyName(path);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Unable to read assembly identity from DLL: " + path, ex);
            }
        }

        private static string GetAssemblyLocation(Assembly assembly)
        {
            try
            {
                return string.IsNullOrWhiteSpace(assembly.Location)
                    ? "<unknown location>"
                    : assembly.Location;
            }
            catch
            {
                return "<unknown location>";
            }
        }

        private static bool IsPdfRelatedAssemblyName(string simpleName)
        {
            if (string.IsNullOrWhiteSpace(simpleName))
                return false;

            return simpleName.StartsWith("PdfSharp", StringComparison.OrdinalIgnoreCase) ||
                   simpleName.StartsWith("MigraDoc", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownPdfSupportAssemblyName(string simpleName)
        {
            if (string.IsNullOrWhiteSpace(simpleName))
                return false;

            switch (simpleName)
            {
                case "Microsoft.Extensions.DependencyInjection.Abstractions":
                case "Microsoft.Extensions.Logging.Abstractions":
                case "Microsoft.Bcl.AsyncInterfaces":
                case "System.Runtime.CompilerServices.Unsafe":
                case "System.Buffers":
                case "System.Numerics.Vectors":
                case "System.Memory":
                case "System.Threading.Tasks.Extensions":
                case "System.Security.Cryptography.Pkcs":
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsSameOrChildPath(string parentFolder, string candidatePath)
        {
            if (string.IsNullOrWhiteSpace(candidatePath) || candidatePath.StartsWith("<", StringComparison.Ordinal))
                return true;

            try
            {
                string parent = Path.GetFullPath(parentFolder)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string candidate = Path.GetFullPath(candidatePath);

                return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static string GetPublicKeyToken(AssemblyName assemblyName)
        {
            byte[] token = assemblyName.GetPublicKeyToken();
            if (token == null || token.Length == 0)
                return "<none>";

            return BitConverter
                .ToString(token)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }
    }
}
