using System.Reflection;
using System.Reflection.Emit;
using ParallelSystemsPlugin.Classes;
using ParallelSystemsPlugin.Models.Configs;
using ParallelSystemsPlugin.Reports.Procurement;

internal static class PdfConflictTests
{
    public static void ExcelCutListEntryDoesNotInitializePdf()
    {
        // Reproduce the unsigned assembly identity reported for ProSheets without
        // loading, changing or redistributing any files belonging to that add-in.
        AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("PdfSharp"), AssemblyBuilderAccess.Run);
        string revitInstallDir = typeof(PdfConflictTests).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == "RevitInstallDir").Value;
        Assembly.LoadFrom(Path.Combine(revitInstallDir, "RevitAPI.dll"));

        MethodInfo generate = typeof(BomCutListReport).GetMethod("Generate");
        try
        {
            generate.Invoke(null, new object[] { null, new ProcurementConfig { ExportReportsToExcel = true } });
            throw new InvalidOperationException("Cut list must reject a missing document.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is ArgumentNullException argument && argument.ParamName == "doc")
        {
            // The entry point must reach document validation without touching PDF.
        }
        catch (TargetInvocationException ex)
        {
            throw new InvalidOperationException("Excel cut list was blocked before document validation: " + ex.InnerException?.Message, ex);
        }

        AssertPdfUntouched();
        Console.WriteLine("PASS: Excel cut-list entry bypasses PDF initialization with an unsigned PdfSharp assembly loaded.");
    }

    public static void AssertPdfUntouched()
    {
        foreach (string fieldName in new[] { "_initialized", "_resolverRegistered" })
        {
            var field = typeof(PdfRuntime).GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic);
            if ((bool)field.GetValue(null))
                throw new InvalidOperationException("Excel export touched the PDF runtime: " + fieldName);
        }

        if (AppDomain.CurrentDomain.GetAssemblies().Any(a => !a.IsDynamic
            && (a.GetName().Name.StartsWith("PdfSharp", StringComparison.OrdinalIgnoreCase)
                || a.GetName().Name.StartsWith("MigraDoc", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidOperationException("Excel export loaded PDF rendering assemblies.");
    }

    public static void PdfExportStillChecksConflicts()
    {
        Type pieceType = typeof(BomCutListReport).GetNestedType("PipePiece", BindingFlags.NonPublic);
        object pieces = Activator.CreateInstance(typeof(List<>).MakeGenericType(pieceType));
        MethodInfo export = typeof(BomCutListReport).GetMethod("ExportReport", BindingFlags.NonPublic | BindingFlags.Static);
        try
        {
            export.Invoke(null, new object[]
            {
                new ProcurementConfig { ExportReportsToExcel = false },
                pieces, 6000d, 2d, 0d, "", "pdf-conflict-regression"
            });
            throw new InvalidOperationException("PDF export bypassed its dependency validation.");
        }
        catch (TargetInvocationException ex) when (ex.InnerException is FileLoadException
            && ex.InnerException.Message.Contains("conflicting unsigned or incorrectly signed"))
        {
            Console.WriteLine("PASS: PDF export retains its dependency-conflict checks.");
        }
    }
}
