using Autodesk.Revit.DB;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ParallelSystemsPlugin.Fabrication
{
    internal sealed class FabricationSelection
    {
        // Elements explicitly selected by the user and therefore included in
        // the generated STEP file.
        public IList<ElementId> SourceElementIds { get; set; } =
            new List<ElementId>();

        // Read-only model context used to calculate fabrication geometry. A
        // prompted or automatically resolved header pipe belongs here, not in
        // SourceElementIds, so it can shape a SET-ON branch without being
        // exported.
        public IList<ElementId> CalculationContextElementIds { get; set; } =
            new List<ElementId>();

        // Records a header pipe explicitly picked for a particular shaped
        // branch. Explicit choices are never replaced by automatic searching.
        public IDictionary<ElementId, ElementId>
            ExplicitHeaderPipeIdsByBranch { get; set; } =
                new Dictionary<ElementId, ElementId>();

        public string SuggestedFileName { get; set; }
    }

    internal enum FabricationIssueSeverity
    {
        Information,
        Warning,
        Blocking
    }

    internal sealed class FabricationIssue
    {
        public FabricationIssueSeverity Severity { get; set; }
        public ElementId ElementId { get; set; }
        public string ElementName { get; set; }
        public string Message { get; set; }
    }

    internal sealed class PipeDimensions
    {
        public double NominalDiameter { get; set; }
        public double OutsideDiameter { get; set; }
        public double InsideDiameter { get; set; }
        public double WallThickness { get; set; }

        public string SourceDescription { get; set; }
    }

    internal sealed class FabricationElementGeometry
    {
        public ElementId SourceElementId { get; set; }
        public string SourceUniqueId { get; set; }
        public string SourceName { get; set; }
        public string CategoryName { get; set; }
        public IList<GeometryObject> Geometry { get; set; } =
            new List<GeometryObject>();

        public PipeDimensions PipeDimensions { get; set; }

        // Marks a generated item whose staged STEP must remain one compact
        // closed SET-ON branch body. This is explicit metadata rather than a
        // status-string convention so topology validation cannot silently stop
        // running when user-facing wording changes.
        public bool RequiresCompactSetOnTopology { get; set; }

        // Maximum STEP ADVANCED_FACE count expected from the compact branch
        // BRep. Zero means no component-specific face-count gate applies.
        public int MaximumExpectedStepFaceCount { get; set; }

        public string Status { get; set; }
        public string Notes { get; set; }
    }

    internal sealed class FabricationStepResult
    {
        public bool Succeeded { get; set; }
        public string StepFilePath { get; set; }
        public string FabricationViewName { get; set; }
        public int SourceElementCount { get; set; }
        public int GeneratedElementCount { get; set; }
        public IList<FabricationIssue> Issues { get; set; } =
            new List<FabricationIssue>();

        public string BuildUserMessage()
        {
            int blocking = Issues.Count(x =>
                x.Severity == FabricationIssueSeverity.Blocking);

            int warnings = Issues.Count(x =>
                x.Severity == FabricationIssueSeverity.Warning);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine(
                "Source elements: " +
                SourceElementCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(
                "Generated fabrication elements: " +
                GeneratedElementCount.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(
                "Warnings: " +
                warnings.ToString(CultureInfo.InvariantCulture));
            sb.AppendLine(
                "Blocking issues: " +
                blocking.ToString(CultureInfo.InvariantCulture));

            if (!string.IsNullOrWhiteSpace(StepFilePath))
            {
                sb.AppendLine();
                sb.AppendLine("STEP:");
                sb.AppendLine(StepFilePath);
            }

            if (!string.IsNullOrWhiteSpace(FabricationViewName))
            {
                sb.AppendLine();
                sb.AppendLine("Revit inspection view:");
                sb.AppendLine(FabricationViewName);
            }

            return sb.ToString().Trim();
        }

        public string BuildDetailedMessage()
        {
            if (Issues == null || Issues.Count == 0)
                return "No validation issues were reported.";

            StringBuilder sb = new StringBuilder();

            foreach (FabricationIssue issue in Issues
                         .OrderByDescending(x => x.Severity)
                         .Take(100))
            {
                sb.Append('[');
                sb.Append(issue.Severity.ToString().ToUpperInvariant());
                sb.Append("] ");

                if (!string.IsNullOrWhiteSpace(issue.ElementName))
                {
                    sb.Append(issue.ElementName);
                    sb.Append(": ");
                }

                sb.AppendLine(issue.Message ?? string.Empty);
            }

            if (Issues.Count > 100)
            {
                sb.AppendLine();
                sb.AppendLine(
                    "Only the first 100 validation issues are shown in this dialog.");
            }

            return sb.ToString().Trim();
        }
    }
}
