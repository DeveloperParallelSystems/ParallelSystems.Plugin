using Autodesk.Revit.DB;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    // Created by Jhay: persistent technical evidence for the manual Revit POC.
    internal static class AssemblyDuplicationDiagnosticReport
    {
        public static string Write(
            Document document,
            AssemblyDuplicationDiagnosticResult result,
            Exception exception = null)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            string folder = Path.Combine(
                Path.GetTempPath(),
                "ParallelSystems",
                "AssemblyDuplicationDiagnostic");

            Directory.CreateDirectory(folder);

            string path = Path.Combine(
                folder,
                "AssemblyDuplication-" +
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) +
                ".txt");

            var text = new StringBuilder();
            text.AppendLine("Parallel Systems Assembly Duplication Diagnostic");
            text.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            text.AppendLine("Revit: " + document.Application.VersionNumber);
            text.AppendLine("Document: " + document.Title);
            text.AppendLine("Succeeded: " + result.Succeeded);
            text.AppendLine();
            AppendEvidence(text, "SOURCE BEFORE", result.SourceBefore);
            AppendEvidence(text, "SOURCE AFTER", result.SourceAfter);
            AppendEvidence(text, "TARGET AFTER", result.TargetAfter);
            text.AppendLine("INVARIANTS");
            foreach (AssemblyDuplicationInvariant invariant in result.Invariants)
            {
                text.AppendLine(
                    (invariant.Passed ? "PASS" : "FAIL") +
                    " | " + invariant.Name +
                    " | " + invariant.Details);
            }

            if (exception != null)
            {
                text.AppendLine();
                text.AppendLine("EXCEPTION");
                text.AppendLine(exception.ToString());
            }

            File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
            return path;
        }

        private static void AppendEvidence(
            StringBuilder text,
            string heading,
            AssemblyEvidence evidence)
        {
            text.AppendLine(heading);
            if (evidence == null)
            {
                text.AppendLine("<not captured>");
                text.AppendLine();
                return;
            }

            text.AppendLine("InstanceId: " + evidence.InstanceId);
            text.AppendLine("TypeId: " + evidence.TypeId);
            text.AppendLine("TypeName: " + evidence.TypeName);
            text.AppendLine("MemberCount: " + evidence.Members.Count);
            text.AppendLine(
                "Members: " +
                string.Join(
                    ", ",
                    evidence.Members.Select(member =>
                        member.MemberId + "->" + member.OwnerAssemblyId)));
            text.AppendLine();
        }
    }
}
