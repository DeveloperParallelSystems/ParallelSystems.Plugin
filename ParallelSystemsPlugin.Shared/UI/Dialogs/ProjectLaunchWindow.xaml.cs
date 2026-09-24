// Created by Jhay
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin.ProjectLaunch;

namespace ParallelSystemPlugin.UI
{
    public partial class ProjectLaunchWindow : Window
    {
        private readonly ProjectSetupExternalEventHandler _handler;
        private readonly ExternalEvent _externalEvent;
        private ProjectSetupSnapshot _snapshot = new ProjectSetupSnapshot();

        internal ProjectLaunchWindow(ProjectSetupExternalEventHandler handler, ExternalEvent externalEvent)
        {
            InitializeComponent();
            _handler = handler;
            _externalEvent = externalEvent;
            _handler.Completed += ApplySnapshot;
            Icon = AppDialog.LoadWindowIcon();
            Closed += (sender, args) => _handler.Completed -= ApplySnapshot;
        }

        public void ShowModeless(IntPtr owner)
        {
            if (owner != IntPtr.Zero) new WindowInteropHelper(this) { Owner = owner };
            Show(); Activate();
        }

        public void RefreshProject(bool quiet = false) => Queue(ProjectSetupRequestType.Refresh, false, quiet);

        private void Queue(ProjectSetupRequestType type, bool capture = true, bool quiet = false)
        {
            ProjectSetupState state = capture ? CaptureState() : null;
            if (!_handler.TryQueue(new ProjectSetupRequest { Type = type, State = state }))
            {
                if (!quiet) AppDialog.Warn("Project Launch", "Another Project Setup action is already queued.");
                return;
            }
            try
            {
                ExternalEventRequest result = _externalEvent.Raise();
                if (result != ExternalEventRequest.Accepted)
                {
                    _handler.CancelPending();
                    AppDialog.Warn("Project Launch", "Revit could not queue the requested action. Try again when the current command finishes.");
                }
            }
            catch (Exception ex)
            {
                _handler.CancelPending();
                AppDialog.Error("Project Launch", "Unable to queue the requested action.\n\n" + ex.Message);
            }
        }

        private ProjectSetupState CaptureState()
        {
            PART_Links.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Cell, true);
            PART_Links.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);
            ProjectSetupState state = _snapshot.State?.Clone() ?? new ProjectSetupState();
            state.ProjectName = PART_ProjectName.Text; state.ProjectNumber = PART_ProjectNumber.Text;
            state.ProjectManager = PART_ProjectManager.Text; state.ProjectOverseer = PART_ProjectOverseer.Text;
            state.ClientNamingConvention = PART_NamingConvention.Text; state.NamingConventionConfirmed = PART_NamingConfirmed.IsChecked == true;
            state.GridLevelWorksetName = PART_GridWorkset.Text; state.InitialModelWorksetName = PART_InitialWorkset.Text;
            state.AdditionalWorksetNames = PART_AdditionalWorksets.Items.Cast<string>().ToList();
            state.NoLinksRequired = PART_NoLinksRequired.IsChecked == true;
            state.RequiredLinkNames = state.NoLinksRequired ? new List<string>() : PART_Links.Items.Cast<ProjectLinkState>().Where(x => x.IsRequired).Select(x => x.Name).ToList();
            state.CoordinationSourceLinkName = PART_CoordinationSource.SelectedValue as string;
            state.CoordinationReviewed = PART_CoordinationReviewed.IsChecked == true;
            state.CopyMonitorNotRequired = PART_CopyMonitorNotRequired.IsChecked == true;
            state.CopyMonitorNotRequiredReason = PART_CopyMonitorReason.Text;
            return state;
        }

        private void ApplySnapshot(ProjectSetupSnapshot snapshot)
        {
            if (snapshot == null || !IsLoaded) return;
            _snapshot = snapshot;
            ProjectSetupState state = snapshot.State ?? new ProjectSetupState();
            PART_ProjectSummary.Text = "Project: " + snapshot.DocumentTitle + "   |   Revit " + snapshot.RevitVersion;
            PART_ActiveProject.Text = snapshot.IsSuitableProject ? "PASS — Revit project detected" : "BLOCKING — " + snapshot.SuitabilityMessage;
            PART_ActiveState.Text = "Model: " + snapshot.DocumentTitle + "   |   Type: " + (snapshot.IsModelInCloud ? "Cloud" : "Local") + "   |   Workshared: " + (snapshot.IsWorkshared ? "Yes" : "No");
            PART_ProjectName.Text = state.ProjectName ?? ""; PART_ProjectNumber.Text = state.ProjectNumber ?? "";
            PART_ProjectManager.Text = state.ProjectManager ?? ""; PART_ProjectOverseer.Text = state.ProjectOverseer ?? "";
            PART_NamingConvention.Text = state.ClientNamingConvention ?? ""; PART_NamingConfirmed.IsChecked = state.NamingConventionConfirmed;
            PART_GridWorkset.Text = state.GridLevelWorksetName ?? ""; PART_InitialWorkset.Text = state.InitialModelWorksetName ?? "";
            PART_AdditionalWorksets.ItemsSource = null; PART_AdditionalWorksets.ItemsSource = state.AdditionalWorksetNames ?? new List<string>();
            PART_CloudStatus.Text = snapshot.IsModelInCloud && snapshot.IsWorkshared ? "PASS — Revit Cloud Worksharing is active."
                : snapshot.IsModelInCloud ? "BLOCKING — This cloud model is not workshared." : "BLOCKING — This model has not been saved to Autodesk Docs.";
            PART_SaveAsCloud.Content = snapshot.SaveAsCloudAutomation == ProjectSetupAutomationLevel.ManualRevitHandoff ? "Manual Handoff: Save As Cloud Model" : "Save As Cloud Model";
            PART_EnableCloudWorksharing.Visibility = snapshot.IsModelInCloud && !snapshot.IsWorkshared ? Visibility.Visible : Visibility.Collapsed;
            PART_Links.ItemsSource = snapshot.Links;
            PART_NoLinksRequired.IsChecked = state.NoLinksRequired;
            PART_CoordinationSource.ItemsSource = snapshot.Links.Where(x => x.IsLoaded).ToList();
            PART_CoordinationSource.SelectedValue = state.CoordinationSourceLinkName;
            PART_MonitoringCounts.Text = "Monitored Grids: " + snapshot.MonitoredGridCount + "   |   Monitored Levels: " + snapshot.MonitoredLevelCount;
            PART_CopyMonitor.Content = snapshot.CopyMonitorAutomation == ProjectSetupAutomationLevel.ManualRevitHandoff ? "Manual Handoff: Copy / Monitor" : "Open Copy / Monitor";
            PART_CoordinationReviewed.IsChecked = state.CoordinationReviewed;
            PART_CopyMonitorNotRequired.IsChecked = state.CopyMonitorNotRequired;
            PART_CopyMonitorReason.Text = state.CopyMonitorNotRequiredReason ?? "";
            PART_Statuses.ItemsSource = snapshot.Statuses;
            PART_CompletionAudit.Text = state.SetupCompleted
                ? "Historical completion: " + state.CompletedBy + " at " + state.CompletedUtc + " using plugin " + state.CompletedPluginVersion + ". Current compliance is shown above."
                : "01.1 has not yet been completed.";
            PART_Complete.IsEnabled = snapshot.IsSuitableProject && !snapshot.HasBlockingIssues;
            PART_EnableWorksharing.IsEnabled = snapshot.IsSuitableProject && !snapshot.IsWorkshared;
            PART_SaveAsCloud.IsEnabled = snapshot.IsSuitableProject && !snapshot.IsModelInCloud;
        }

        private void OnRefreshClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.Refresh, false);
        private void OnSaveClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.SaveProjectInformation);
        private void OnValidateClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.Validate, false);
        private void OnEnableWorksharingClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.EnableWorksharingAndCreateWorksets);
        private void OnCreateWorksetsClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.CreateMissingWorksets);
        private void OnSaveAsCloudClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.SaveAsCloudModel);
        private void OnEnableCloudWorksharingClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.EnableCloudWorksharing);
        private void OnManageLinksClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.OpenManageLinks);
        private void OnCopyMonitorClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.OpenCopyMonitor);
        private void OnCompleteClick(object sender, RoutedEventArgs e) => Queue(ProjectSetupRequestType.CompleteSetup);

        private void OnAddWorksetClick(object sender, RoutedEventArgs e)
        {
            string name = (PART_NewWorkset.Text ?? "").Trim();
            if (name.Length == 0) return;
            var names = PART_AdditionalWorksets.Items.Cast<string>().ToList();
            if (!names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
            PART_AdditionalWorksets.ItemsSource = names; PART_NewWorkset.Clear();
        }

        private void OnRemoveWorksetClick(object sender, RoutedEventArgs e)
        {
            string selected = PART_AdditionalWorksets.SelectedItem as string;
            if (selected == null) return;
            var names = PART_AdditionalWorksets.Items.Cast<string>().Where(x => !string.Equals(x, selected, StringComparison.OrdinalIgnoreCase)).ToList();
            PART_AdditionalWorksets.ItemsSource = names;
        }
    }
}
