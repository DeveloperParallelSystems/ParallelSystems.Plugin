using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
    internal sealed class FabricationPreflightIssue
    {
        public string ElementName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class FabricationPreflightResult
    {
        public IList<FabricationPreflightIssue> BlockingIssues { get; } =
            new List<FabricationPreflightIssue>();

        public IList<FabricationPreflightIssue> Warnings { get; } =
            new List<FabricationPreflightIssue>();

        public bool CanProceed => BlockingIssues.Count == 0;
        public bool RequiresConfirmation => Warnings.Count > 0;

        public string BuildBlockingMessage()
        {
            if (BlockingIssues.Count == 0)
                return string.Empty;

            return
                "The selected fabrication elements are not safe to export from " +
                "the current local model. Reload Latest or resolve the listed " +
                "model condition, then run Fabrication STEP again.";
        }

        public string BuildWarningMessage()
        {
            if (Warnings.Count == 0)
                return string.Empty;

            return
                "One or more selected elements are currently owned by another " +
                "user. Fabrication STEP will not edit those source elements, " +
                "but the export will use the geometry currently loaded in your " +
                "local model.";
        }

        public string BuildDetails()
        {
            StringBuilder details = new StringBuilder();

            AppendIssues(details, "BLOCKING", BlockingIssues);
            AppendIssues(details, "WARNING", Warnings);

            return details.Length == 0
                ? "No worksharing issues were detected."
                : details.ToString().Trim();
        }

        private static void AppendIssues(
            StringBuilder builder,
            string label,
            IEnumerable<FabricationPreflightIssue> issues)
        {
            foreach (FabricationPreflightIssue issue in issues.Take(100))
            {
                builder.Append('[');
                builder.Append(label);
                builder.Append("] ");

                if (!string.IsNullOrWhiteSpace(issue.ElementName))
                {
                    builder.Append(issue.ElementName);
                    builder.Append(": ");
                }

                builder.AppendLine(issue.Message ?? string.Empty);
            }
        }
    }
}
