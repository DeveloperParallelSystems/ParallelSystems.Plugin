using Autodesk.Revit.DB;
using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace ParallelSystemsPlugin.AssemblyDuplication
{
    internal static class AssemblyDuplicationDiagnosticReport
    {
        public static string Write(Document document, AssemblyDuplicationDiagnosticResult result, Exception exception = null)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));
            if (result == null)
                throw new ArgumentNullException(nameof(result));

            string folder = Path.Combine(Path.GetTempPath(), "ParallelSystems", "AssemblyDuplicationDiagnostic");
            Directory.CreateDirectory(folder);
            string path = Path.Combine(
                folder,
                "AssemblyDuplication-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".txt");

            var text = new StringBuilder();
            text.AppendLine("Parallel Systems Assembly Duplication Diagnostic");
            text.AppendLine("UTC: " + DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            text.AppendLine("Revit: " + document.Application.VersionNumber);
            text.AppendLine("Document: " + document.Title);
            text.AppendLine("Succeeded: " + result.Succeeded);
            text.AppendLine("Summary: " + (result.Summary ?? string.Empty));
            text.AppendLine("Details: " + (result.Details ?? string.Empty));
            text.AppendLine();

            AppendFamilyResolution(text, result.FamilyResolution);
            AppendEvidence(text, "SOURCE BEFORE", result.SourceBefore);
            AppendEvidence(text, "SOURCE AFTER", result.SourceAfter);
            AppendEvidence(text, "TARGET 500 AFTER", result.Target500After);
            AppendEvidence(text, "TARGET 501 AFTER", result.Target501After);
            AppendMarker(text, "MARKER 500", result.Target500Marker);
            AppendMarker(text, "MARKER 501", result.Target501Marker);

            text.AppendLine("PAIRWISE TYPE IDS");
            text.AppendLine("Source: " + FormatTypeId(result.SourceAfter ?? result.SourceBefore));
            text.AppendLine("Target 500: " + FormatTypeId(result.Target500After));
            text.AppendLine("Target 501: " + FormatTypeId(result.Target501After));
            text.AppendLine();

            text.AppendLine("RENAME PROBE");
            text.AppendLine((result.RenameProbePassed ? "PASS" : "FAIL") + " | " + (result.RenameProbeDetails ?? "not run"));
            text.AppendLine();

            text.AppendLine("TRANSACTION STAGES");
            if (result.TransactionStages.Count == 0)
                text.AppendLine("<not captured>");
            foreach (AssemblyTransactionStage stage in result.TransactionStages)
                text.AppendLine(stage.Status + " | " + stage.Name);
            text.AppendLine();

            text.AppendLine("CONTAMINATION OBSERVATIONS");
            if (result.ContaminationObservations.Count == 0)
                text.AppendLine("<not captured>");
            foreach (AssemblyContaminationObservation observation in result.ContaminationObservations)
                text.AppendLine(observation.Surface + " | " + observation.Result);
            text.AppendLine();

            text.AppendLine("INVARIANTS");
            foreach (AssemblyDuplicationInvariant invariant in result.Invariants)
            {
                text.AppendLine((invariant.Passed ? "PASS" : "FAIL") + " | " + invariant.Name + " | " + invariant.Details);
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

        private static void AppendFamilyResolution(StringBuilder text, AssemblyIdentityFamilyResolution resolution)
        {
            text.AppendLine("FAMILY RESOLUTION");
            if (resolution == null)
            {
                text.AppendLine("<not captured>");
                text.AppendLine();
                return;
            }

            text.AppendLine("AssetPath: " + resolution.AssetPath);
            text.AppendLine("WasLoaded: " + resolution.WasLoaded);
            text.AppendLine("LoadTransactionStatus: " + (resolution.LoadTransactionStatus?.ToString() ?? "not required"));
            text.AppendLine("FamilyId: " + resolution.FamilyId);
            text.AppendLine("FamilyName: " + AssemblyIdentityFamilyService.FamilyName);
            text.AppendLine("Category: " + resolution.CategoryName + " (" + resolution.CategoryId + ")");
            text.AppendLine("FamilyPlacementType: " + resolution.PlacementType);
            text.AppendLine();
        }

        private static void AppendEvidence(StringBuilder text, string heading, AssemblyEvidence evidence)
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
            text.AppendLine("NamingCategoryId: " + evidence.NamingCategoryId);
            text.AppendLine("Origin: " + FormatPoint(evidence.OriginX, evidence.OriginY, evidence.OriginZ));
            text.AppendLine("ProductionMemberCount: " + evidence.ProductionMembers.Count);
            text.AppendLine("InternalMemberCount: " + evidence.InternalMembers.Count);
            text.AppendLine("TotalMemberCount: " + evidence.Members.Count);
            foreach (AssemblyMemberEvidence member in evidence.Members)
            {
                text.AppendLine(
                    "Member: " + member.MemberId +
                    " | Owner: " + member.OwnerAssemblyId +
                    " | Category: " + member.CategoryName + " (" + member.CategoryId + ")" +
                    " | Type: " + member.ElementTypeName + " (" + member.TypeId + ")" +
                    " | IdentityMarker: " + member.IsIdentityMarker +
                    " | Location: " + FormatMemberLocation(member) +
                    " | PlacementFingerprint: " + member.PlacementFingerprint +
                    " | ParameterFingerprint: " + member.ParameterFingerprint);
            }
            text.AppendLine();
        }

        private static void AppendMarker(StringBuilder text, string heading, AssemblyIdentityMarkerEvidence marker)
        {
            text.AppendLine(heading);
            if (marker == null)
            {
                text.AppendLine("<not captured>");
                text.AppendLine();
                return;
            }

            text.AppendLine("MarkerId: " + marker.MarkerId);
            text.AppendLine("SymbolId: " + marker.SymbolId);
            text.AppendLine("SymbolName: " + marker.SymbolName);
            text.AppendLine("Level: " + marker.LevelName + " (" + marker.LevelId + ")");
            text.AppendLine("LevelElevation: " + FormatNumber(marker.LevelElevation));
            text.AppendLine("Offset: " + FormatNumber(marker.Offset));
            text.AppendLine("RequestedOrigin: " + FormatPoint(marker.RequestedX, marker.RequestedY, marker.RequestedZ));
            text.AppendLine("ResultPoint: " + FormatPoint(marker.ResultX, marker.ResultY, marker.ResultZ));
            text.AppendLine();
        }

        private static string FormatMemberLocation(AssemblyMemberEvidence member)
        {
            if (!member.RelativeX.HasValue)
                return member.LocationKind + " (not point/curve based)";

            string start = FormatPoint(member.RelativeX.Value, member.RelativeY.Value, member.RelativeZ.Value);
            if (!member.RelativeEndX.HasValue)
                return member.LocationKind + " " + start;

            return member.LocationKind + " " + start + " -> " +
                FormatPoint(member.RelativeEndX.Value, member.RelativeEndY.Value, member.RelativeEndZ.Value);
        }

        private static string FormatTypeId(AssemblyEvidence evidence)
        {
            return evidence == null ? "<not captured>" : evidence.TypeId.ToString(CultureInfo.InvariantCulture);
        }

        private static string FormatPoint(double x, double y, double z)
        {
            return "(" + FormatNumber(x) + ", " + FormatNumber(y) + ", " + FormatNumber(z) + ")";
        }

        private static string FormatNumber(double value)
        {
            return value.ToString("G17", CultureInfo.InvariantCulture);
        }
    }
}
