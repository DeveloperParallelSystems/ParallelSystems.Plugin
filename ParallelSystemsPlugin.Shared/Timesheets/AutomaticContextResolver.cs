using Autodesk.Revit.DB;
using ParallelSystemsPlugin.Compatibility;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ParallelSystemsPlugin.Timesheets
{
    internal sealed class AutomaticContextResolver
    {
        private static readonly string[] AreaParameterNames =
        {
            "Area", "Building", "Building Area", "BIM Area", "PS Area", "PS_Area"
        };

        private static readonly string[] ZoneParameterNames =
        {
            "Zone", "MEP Zone", "BIM Zone", "PS Zone", "PS_Zone"
        };

        private static readonly string[] SystemParameterNames =
        {
            "System Name", "System Type", "System Classification", "MEP System", "Service Name"
        };

        private static readonly string[] StatusParameterNames =
        {
            "Status", "BIM Status", "Model Status", "PS Status", "PS_Status"
        };

        public ProjectContext ResolveProject(Document document)
        {
            if (document == null) return new ProjectContext();

            var projectInfo = document.ProjectInformation;
            var path = Safe(() => document.PathName);
            var title = Safe(() => document.Title);
            var projectName = Safe(() => projectInfo?.Name);
            var projectNumber = Safe(() => projectInfo?.Number);
            string cloudProjectId = null;
            string cloudModelId = null;

            TryResolveCloudIds(document, out cloudProjectId, out cloudModelId);

            var cloudIdentity = !string.IsNullOrWhiteSpace(cloudProjectId) || !string.IsNullOrWhiteSpace(cloudModelId)
                ? (cloudProjectId ?? string.Empty) + ":" + (cloudModelId ?? string.Empty)
                : null;

            var identitySource = FirstNotBlank(
                cloudIdentity,
                path,
                projectNumber + ":" + projectName + ":" + title,
                title);

            return new ProjectContext
            {
                ProjectKey = Hash(identitySource),
                ProjectName = FirstNotBlank(projectName, Path.GetFileNameWithoutExtension(title), title, "Unnamed Revit Project"),
                ProjectNumber = projectNumber,
                DocumentTitle = title,
                DocumentPathHash = string.IsNullOrWhiteSpace(path) ? null : Hash(path),
                CloudProjectId = cloudProjectId,
                CloudModelId = cloudModelId,
                RevitUserName = Safe(() => document.Application.Username)
            };
        }

        public ViewContext ResolveView(Document document, View view, EvidenceAccumulator evidence)
        {
            if (view == null) return new ViewContext();

            var viewName = Safe(() => view.Name);
            var level = FirstNotBlank(
                ResolveGeneratedLevel(view),
                evidence?.DominantLevel,
                ReadFirstParameter(view, new[] { "Associated Level", "Level" }),
                InferLevelFromName(viewName));

            var area = FirstNotBlank(
                evidence?.DominantArea,
                ReadFirstParameter(view, AreaParameterNames),
                ReadFirstParameter(document?.ProjectInformation, AreaParameterNames),
                InferAreaFromName(viewName));

            var zone = FirstNotBlank(
                evidence?.DominantZone,
                ReadFirstParameter(view, ZoneParameterNames),
                InferZoneFromName(viewName));

            var system = FirstNotBlank(
                evidence?.DominantSystem,
                ReadFirstParameter(view, SystemParameterNames));

            var status = FirstNotBlank(
                ReadFirstParameter(view, StatusParameterNames),
                ReadFirstParameter(document?.ProjectInformation, StatusParameterNames));

            var viewTemplateName = ResolveViewTemplateName(document, view);
            var viewDiscipline = ReadFirstParameter(view, new[] { "Discipline", "View Discipline" });
            var viewSubDiscipline = ReadFirstParameter(view, new[] { "Sub-Discipline", "Sub Discipline" });
            var scope = ResolveScope(view, evidence);
            var activity = ResolveActivity(view, evidence);

            return new ViewContext
            {
                ViewId = RevitApiCompatibility.GetElementIdValue(view.Id),
                ViewName = viewName,
                ViewType = Safe(() => view.ViewType.ToString()),
                SheetNumber = view is ViewSheet sheet ? Safe(() => sheet.SheetNumber) : null,
                ViewTemplateName = viewTemplateName,
                ViewDiscipline = viewDiscipline,
                ViewSubDiscipline = viewSubDiscipline,
                Area = area,
                Level = level,
                Zone = zone,
                System = system,
                Status = status,
                Scope = scope,
                Activity = activity
            };
        }

        public ElementEvidence ResolveElement(Document document, Element element)
        {
            if (element == null) return new ElementEvidence();

            var category = Safe(() => element.Category?.Name);
            var level = ResolveElementLevel(document, element);
            var area = ReadFirstParameter(element, AreaParameterNames);
            var zone = ReadFirstParameter(element, ZoneParameterNames);
            var system = ReadFirstParameter(element, SystemParameterNames);
            var workset = ResolveWorksetName(document, element);

            return new ElementEvidence
            {
                Category = category,
                Level = level,
                Area = area,
                Zone = zone,
                System = system,
                Workset = workset
            };
        }

        private static string ResolveViewTemplateName(Document document, View view)
        {
            if (document == null || view == null) return null;
            try
            {
                var id = view.ViewTemplateId;
                if (id == null || id == ElementId.InvalidElementId) return null;
                return document.GetElement(id)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveWorksetName(Document document, Element element)
        {
            if (document == null || element == null) return null;
            try
            {
                return document.GetWorksetTable()?.GetWorkset(element.WorksetId)?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveElementLevel(Document document, Element element)
        {
            try
            {
                var property = element.GetType().GetProperty("LevelId", BindingFlags.Instance | BindingFlags.Public);
                var id = property?.GetValue(element, null) as ElementId;
                if (id != null && id != ElementId.InvalidElementId)
                {
                    return document?.GetElement(id)?.Name;
                }
            }
            catch { }

            return ReadFirstParameter(element, new[] { "Level", "Reference Level", "Schedule Level", "Base Level" });
        }

        private static string ResolveGeneratedLevel(View view)
        {
            try
            {
                var property = view.GetType().GetProperty("GenLevel", BindingFlags.Instance | BindingFlags.Public);
                var level = property?.GetValue(view, null) as Level;
                return level?.Name;
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveScope(View view, EvidenceAccumulator evidence)
        {
            var category = evidence?.DominantCategory;
            if (!string.IsNullOrWhiteSpace(category))
            {
                var normalized = category.ToUpperInvariant();
                if (normalized.Contains("PIPE") || normalized.Contains("PLUMBING")) return "Pipe";
                if (normalized.Contains("DUCT") || normalized.Contains("AIR TERMINAL")) return "Duct";
                if (normalized.Contains("MECHANICAL EQUIPMENT")) return "Mechanical Equipment";
                if (normalized.Contains("STRUCTURAL FRAMING") || normalized.Contains("FRAME")) return "Frames";
                if (normalized.Contains("SHEET")) return "Sheets";
            }

            if (view != null && view.ViewType == ViewType.DrawingSheet) return "Sheets";
            return null;
        }

        private static string ResolveActivity(View view, EvidenceAccumulator evidence)
        {
            if (evidence != null && evidence.HasModelChanges)
            {
                if (view != null && view.ViewType == ViewType.DrawingSheet) return "Coordination Sheets";
                return "Modeling";
            }

            return "Review";
        }

        private static string ReadFirstParameter(Element element, IEnumerable<string> names)
        {
            if (element == null || names == null) return null;

            foreach (var name in names)
            {
                try
                {
                    var parameter = element.LookupParameter(name);
                    var value = ReadParameter(parameter);
                    if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
                }
                catch { }
            }

            return null;
        }

        private static string ReadParameter(Parameter parameter)
        {
            if (parameter == null || !parameter.HasValue) return null;

            try
            {
                if (parameter.StorageType == StorageType.String) return parameter.AsString();
                return parameter.AsValueString();
            }
            catch
            {
                return null;
            }
        }

        private static string InferAreaFromName(string value)
        {
            return Match(value, @"\b(?:BUILDING|BLDG|AREA)[\s_\-]*[A-Z0-9]+\b");
        }

        private static string InferLevelFromName(string value)
        {
            return Match(value, @"\b(?:LEVEL|LVL)[\s_\-]*[A-Z0-9]+\b");
        }

        private static string InferZoneFromName(string value)
        {
            return Match(value, @"\bZONE[\s_\-]*[A-Z0-9]+\b");
        }

        private static string Match(string value, string pattern)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            return match.Success ? Regex.Replace(match.Value, "[_-]+", " ").Trim() : null;
        }

        private static string FirstNotBlank(params string[] values)
        {
            return values?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim();
        }

        private static string Hash(string value)
        {
            value = value ?? "unknown";
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder(bytes.Length * 2);
                foreach (var item in bytes) builder.Append(item.ToString("x2"));
                return builder.ToString();
            }
        }

        private static string Safe(Func<string> action)
        {
            try { return action(); }
            catch { return null; }
        }

        private static void TryResolveCloudIds(Document document, out string projectId, out string modelId)
        {
            projectId = null;
            modelId = null;

            try
            {
                var method = document.GetType().GetMethod("GetCloudModelPath", Type.EmptyTypes);
                var modelPath = method?.Invoke(document, null);
                if (modelPath == null) return;

                var projectMethod = modelPath.GetType().GetMethod("GetProjectGUID", Type.EmptyTypes);
                var modelMethod = modelPath.GetType().GetMethod("GetModelGUID", Type.EmptyTypes);
                projectId = projectMethod?.Invoke(modelPath, null)?.ToString();
                modelId = modelMethod?.Invoke(modelPath, null)?.ToString();
            }
            catch
            {
                // Local, detached, and older models legitimately have no cloud identifiers.
            }
        }
    }

    internal sealed class ProjectContext
    {
        public string ProjectKey { get; set; }
        public string ProjectName { get; set; }
        public string ProjectNumber { get; set; }
        public string DocumentTitle { get; set; }
        public string DocumentPathHash { get; set; }
        public string CloudProjectId { get; set; }
        public string CloudModelId { get; set; }
        public string RevitUserName { get; set; }
    }

    internal sealed class ViewContext
    {
        public long? ViewId { get; set; }
        public string ViewName { get; set; }
        public string ViewType { get; set; }
        public string SheetNumber { get; set; }
        public string ViewTemplateName { get; set; }
        public string ViewDiscipline { get; set; }
        public string ViewSubDiscipline { get; set; }
        public string Area { get; set; }
        public string Level { get; set; }
        public string Zone { get; set; }
        public string System { get; set; }
        public string Activity { get; set; }
        public string Scope { get; set; }
        public string Status { get; set; }
    }

    internal sealed class ElementEvidence
    {
        public string Category { get; set; }
        public string Level { get; set; }
        public string Area { get; set; }
        public string Zone { get; set; }
        public string System { get; set; }
        public string Workset { get; set; }
    }
}
