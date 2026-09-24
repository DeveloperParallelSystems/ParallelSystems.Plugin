// Created by Jhay
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;
using ParallelSystemsPlugin.Compatibility;

namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal static class ProjectSetupService
    {
        private static readonly char[] InvalidWorksetCharacters = { '\\', ':', '{', '}', '[', ']', '|', ';', '<', '>', '?', '`', '~' };

        public static ProjectSetupSnapshot Execute(UIApplication app, ProjectSetupRequest request)
        {
            request = request ?? new ProjectSetupRequest { Type = ProjectSetupRequestType.Refresh };
            UIDocument uiDocument = app?.ActiveUIDocument;
            Document document = uiDocument?.Document;
            ProjectSetupSnapshot before = Scan(app, request.State);

            if (request.Type == ProjectSetupRequestType.Refresh || request.Type == ProjectSetupRequestType.Validate)
                return before;
            if (!before.IsSuitableProject)
                throw new InvalidOperationException(before.SuitabilityMessage);

            ProjectSetupState state = request.State?.Clone() ?? before.State.Clone();
            Normalize(state);

            switch (request.Type)
            {
                case ProjectSetupRequestType.SaveProjectInformation:
                    SaveProfile(document, state);
                    break;
                case ProjectSetupRequestType.EnableWorksharingAndCreateWorksets:
                    SaveProfile(document, state);
                    EnableWorksharing(app, document, state);
                    CreateMissingWorksets(document, state);
                    break;
                case ProjectSetupRequestType.CreateMissingWorksets:
                    SaveProfile(document, state);
                    CreateMissingWorksets(document, state);
                    break;
                case ProjectSetupRequestType.SaveAsCloudModel:
                    SaveProfile(document, state);
                    OpenSaveAsCloudModel(app);
                    break;
                case ProjectSetupRequestType.EnableCloudWorksharing:
                    SaveProfile(document, state);
                    EnableCloudWorksharing(app, document);
                    break;
                case ProjectSetupRequestType.OpenManageLinks:
                    SaveProfile(document, state);
                    PostManageLinks(app);
                    break;
                case ProjectSetupRequestType.OpenCopyMonitor:
                    SaveProfile(document, state);
                    OpenCopyMonitor(app);
                    break;
                case ProjectSetupRequestType.CompleteSetup:
                    SaveProfile(document, state);
                    ProjectSetupSnapshot validation = Scan(app, null);
                    if (validation.HasBlockingIssues)
                        throw new InvalidOperationException("Project Setup still contains blocking validation issues.");
                    state = validation.State;
                    state.SetupCompleted = true;
                    state.CompletedBy = app.Application.Username ?? "";
                    state.CompletedUtc = DateTime.UtcNow.ToString("O");
                    state.CompletedPluginVersion = GetPluginVersion();
                    using (var tx = new Transaction(document, "Complete Parallel Systems Project Setup"))
                    {
                        tx.Start(); ProjectSetupStorage.Save(document, state); tx.Commit();
                    }
                    AppDialog.Success(app, "Project Setup Complete", "01.1 Project Setup has been completed and its audit record was saved.");
                    break;
            }

            return Scan(app, null);
        }

        public static ProjectSetupSnapshot Scan(UIApplication app, ProjectSetupState unsavedState)
        {
            var snapshot = new ProjectSetupSnapshot
            {
                RevitVersion = app?.Application?.VersionNumber ?? "",
                SaveAsCloudAutomation = RevitApiCompatibility.SaveAsCloudModelAutomation,
                ManageLinksAutomation = RevitApiCompatibility.ManageLinksAutomation,
                CopyMonitorAutomation = RevitApiCompatibility.CopyMonitorAutomation
            };

            Document document = app?.ActiveUIDocument?.Document;
            if (document == null)
            {
                snapshot.SuitabilityMessage = "No active Revit project is open.";
                snapshot.Statuses = ProjectSetupValidator.Validate(snapshot);
                return snapshot;
            }

            snapshot.DocumentTitle = document.Title;
            snapshot.DocumentKey = document.Title + "|" + document.PathName;
            snapshot.IsModelInCloud = document.IsModelInCloud;
            snapshot.IsWorkshared = document.IsWorkshared;
            snapshot.SuitabilityMessage = GetSuitabilityMessage(document);
            snapshot.IsSuitableProject = snapshot.SuitabilityMessage == null;

            ProjectSetupState persisted = ProjectSetupStorage.Load(document);
            ReadProjectInformation(document, persisted);
            snapshot.State = unsavedState?.Clone() ?? persisted;
            Normalize(snapshot.State);

            if (document.IsWorkshared)
            {
                snapshot.ExistingWorksets = new FilteredWorksetCollector(document)
                    .OfKind(WorksetKind.UserWorkset).Select(x => x.Name).OrderBy(x => x).ToList();
            }

            snapshot.Links = ScanLinks(document, snapshot.State);
            ScanMonitoring(document, snapshot, snapshot.State.CoordinationSourceLinkName);
            snapshot.Statuses = ProjectSetupValidator.Validate(snapshot);
            return snapshot;
        }

        private static string GetSuitabilityMessage(Document document)
        {
            if (document.IsFamilyDocument) return "Family documents cannot use Project Setup.";
            if (document.IsLinked) return "A linked document cannot be modified by Project Setup.";
            if (document.IsReadOnly) return "The active project is read-only.";
            return null;
        }

        private static void ReadProjectInformation(Document document, ProjectSetupState state)
        {
            ProjectInfo info = document.ProjectInformation;
            state.ProjectName = ReadParameter(info.get_Parameter(BuiltInParameter.PROJECT_NAME));
            state.ProjectNumber = ReadParameter(info.get_Parameter(BuiltInParameter.PROJECT_NUMBER));
            string manager = ReadNamedParameter(info, "Project Manager");
            string overseer = ReadNamedParameter(info, "Project Overseer");
            if (!string.IsNullOrWhiteSpace(manager)) state.ProjectManager = manager;
            if (!string.IsNullOrWhiteSpace(overseer)) state.ProjectOverseer = overseer;
        }

        private static void SaveProfile(Document document, ProjectSetupState state)
        {
            using (var tx = new Transaction(document, "Save Parallel Systems Project Setup"))
            {
                tx.Start();
                SetParameter(document.ProjectInformation.get_Parameter(BuiltInParameter.PROJECT_NAME), state.ProjectName, "Project Name");
                SetParameter(document.ProjectInformation.get_Parameter(BuiltInParameter.PROJECT_NUMBER), state.ProjectNumber, "Project Number");
                TrySetNamedParameter(document.ProjectInformation, "Project Manager", state.ProjectManager);
                TrySetNamedParameter(document.ProjectInformation, "Project Overseer", state.ProjectOverseer);
                ProjectSetupStorage.Save(document, state);
                tx.Commit();
            }
        }

        private static void EnableWorksharing(UIApplication app, Document document, ProjectSetupState state)
        {
            if (document.IsWorkshared) return;
            ValidateInitialWorksetNames(state);
            int choice = AppDialog.Choose(app, "Enable Revit Worksharing?", "Enable Worksharing",
                "This project is not currently workshared. Parallel Systems Project Setup will enable Revit worksharing using the configured initial worksets.\n\nRevit will clear the current Undo history when worksharing is enabled.",
                new[] { "Enable Worksharing" });
            bool confirmed = choice == 0;
            if (!confirmed) return;
            if (!document.CanEnableWorksharing()) throw new InvalidOperationException("Revit cannot enable worksharing for the active project in its current state.");
            document.EnableWorksharing(state.GridLevelWorksetName, state.InitialModelWorksetName);
        }

        private static void CreateMissingWorksets(Document document, ProjectSetupState state)
        {
            if (!document.IsWorkshared) throw new InvalidOperationException("Enable worksharing before creating additional worksets.");
            List<string> names = state.AdditionalWorksetNames.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            foreach (string name in names) ValidateWorksetName(name);
            using (var tx = new Transaction(document, "Create Parallel Systems Worksets"))
            {
                tx.Start();
                foreach (string name in names)
                    if (WorksetTable.IsWorksetNameUnique(document, name)) Workset.Create(document, name);
                tx.Commit();
            }
        }

        private static void EnableCloudWorksharing(UIApplication app, Document document)
        {
            if (!document.IsModelInCloud || document.IsWorkshared) return;
            if (!AppDialog.Confirm(app, "Enable Cloud Worksharing?", "This cloud model is not workshared. Enable Revit Cloud Worksharing now?", true)) return;
            if (!document.CanEnableCloudWorksharing()) throw new InvalidOperationException("Revit cannot enable cloud worksharing in the current state.");
            document.EnableCloudWorksharing();
        }

        private static void OpenSaveAsCloudModel(UIApplication app)
        {
            if (RevitApiCompatibility.SaveAsCloudModelAutomation == ProjectSetupAutomationLevel.ManualRevitHandoff)
            {
                AppDialog.Info(app, "Manual Revit Action Required", "Revit 2021 requires this step to be opened manually.\n\nIn Revit select:\n\nFile > Save As > Cloud Model\n\nChoose the required Autodesk cloud project/folder, complete the save, then return to Project Launch and select Refresh.");
                return;
            }
            string error;
            if (!RevitApiCompatibility.TryPostSaveAsCloudModel(app, out error)) throw new InvalidOperationException(error);
        }

        private static void PostManageLinks(UIApplication app)
        {
            string error;
            if (!RevitApiCompatibility.TryPostManageLinks(app, out error)) throw new InvalidOperationException(error);
        }

        private static void OpenCopyMonitor(UIApplication app)
        {
            if (RevitApiCompatibility.CopyMonitorAutomation == ProjectSetupAutomationLevel.ManualRevitHandoff)
            {
                AppDialog.Info(app, "Manual Revit Action Required", "Revit 2021 requires this workflow to be opened manually.\n\nIn Revit select:\n\nCollaborate > Copy/Monitor > Select Link\n\nComplete the required Grid / Level coordination, return to Project Launch and select Recheck.");
                return;
            }
            string error;
            if (!RevitApiCompatibility.TryPostCopyMonitorSelectLink(app, out error)) throw new InvalidOperationException(error);
        }

        private static List<ProjectLinkState> ScanLinks(Document document, ProjectSetupState state)
        {
            var required = new HashSet<string>(state.RequiredLinkNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return new FilteredElementCollector(document).OfClass(typeof(RevitLinkType)).Cast<RevitLinkType>()
                .Select(x => new ProjectLinkState { Name = x.Name, IsLoaded = RevitLinkType.IsLoaded(document, x.Id), IsRequired = required.Contains(x.Name) })
                .OrderBy(x => x.Name).ToList();
        }

        private static void ScanMonitoring(Document document, ProjectSetupSnapshot snapshot, string sourceName)
        {
            var sourceInstanceIds = new HashSet<long>();
            foreach (RevitLinkInstance instance in new FilteredElementCollector(document).OfClass(typeof(RevitLinkInstance)).Cast<RevitLinkInstance>())
            {
                Element type = document.GetElement(instance.GetTypeId());
                if (string.IsNullOrWhiteSpace(sourceName) || string.Equals(type?.Name, sourceName, StringComparison.OrdinalIgnoreCase))
                    sourceInstanceIds.Add(RevitApiCompatibility.GetElementIdValue(instance.Id));
            }
            snapshot.MonitoredGridCount = CountMonitored<Grid>(document, sourceInstanceIds);
            snapshot.MonitoredLevelCount = CountMonitored<Level>(document, sourceInstanceIds);
        }

        private static int CountMonitored<T>(Document document, HashSet<long> sourceInstanceIds) where T : Element
        {
            return new FilteredElementCollector(document).OfClass(typeof(T)).Cast<T>().Count(element =>
            {
                IList<ElementId> ids = element.GetMonitoredLinkElementIds();
                return ids != null && ids.Any(id => sourceInstanceIds.Count == 0 || sourceInstanceIds.Contains(RevitApiCompatibility.GetElementIdValue(id)));
            });
        }

        private static void ValidateInitialWorksetNames(ProjectSetupState state)
        {
            ValidateWorksetName(state.GridLevelWorksetName); ValidateWorksetName(state.InitialModelWorksetName);
            if (string.Equals(state.GridLevelWorksetName, state.InitialModelWorksetName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The two initial workset names must be different.");
            foreach (string additionalName in state.AdditionalWorksetNames)
            {
                if (string.Equals(additionalName, state.GridLevelWorksetName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(additionalName, state.InitialModelWorksetName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Additional workset names must be different from both initial workset names.");
                }
            }
        }

        private static void ValidateWorksetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Workset names cannot be blank.");
            if (name.IndexOfAny(InvalidWorksetCharacters) >= 0) throw new InvalidOperationException("Workset name '" + name + "' contains a character Revit does not allow.");
        }

        private static void Normalize(ProjectSetupState state)
        {
            state.ProjectName = Trim(state.ProjectName); state.ProjectNumber = Trim(state.ProjectNumber);
            state.ProjectManager = Trim(state.ProjectManager); state.ProjectOverseer = Trim(state.ProjectOverseer);
            state.ClientNamingConvention = Trim(state.ClientNamingConvention);
            state.GridLevelWorksetName = Trim(state.GridLevelWorksetName); state.InitialModelWorksetName = Trim(state.InitialModelWorksetName);
            state.AdditionalWorksetNames = (state.AdditionalWorksetNames ?? new List<string>()).Select(Trim).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.RequiredLinkNames = (state.RequiredLinkNames ?? new List<string>()).Select(Trim).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            state.CoordinationSourceLinkName = Trim(state.CoordinationSourceLinkName);
            state.CopyMonitorNotRequiredReason = Trim(state.CopyMonitorNotRequiredReason);
        }

        private static string Trim(string value) => (value ?? "").Trim();
        private static string ReadParameter(Parameter parameter) => parameter?.AsString() ?? "";
        private static string ReadNamedParameter(ProjectInfo info, string name)
        {
            Parameter parameter = info.Parameters.Cast<Parameter>().FirstOrDefault(x => string.Equals(x.Definition?.Name, name, StringComparison.OrdinalIgnoreCase) && !x.IsReadOnly);
            return ReadParameter(parameter);
        }
        private static void TrySetNamedParameter(ProjectInfo info, string name, string value)
        {
            Parameter parameter = info.Parameters.Cast<Parameter>().FirstOrDefault(x => string.Equals(x.Definition?.Name, name, StringComparison.OrdinalIgnoreCase) && !x.IsReadOnly && x.StorageType == StorageType.String);
            if (parameter != null) parameter.Set(value ?? "");
        }
        private static void SetParameter(Parameter parameter, string value, string label)
        {
            if (parameter == null || parameter.IsReadOnly || parameter.StorageType != StorageType.String) throw new InvalidOperationException(label + " is not writable in this project.");
            parameter.Set(value ?? "");
        }
        private static string GetPluginVersion()
        {
            Assembly assembly = typeof(ProjectSetupService).Assembly;
            var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            return attribute?.InformationalVersion ?? assembly.GetName().Version?.ToString() ?? "";
        }
    }
}
