// Created by Jhay
using System;
using Autodesk.Revit.UI;
using ParallelSystemPlugin.UI;

namespace ParallelSystemsPlugin.ProjectLaunch
{
    internal sealed class ProjectSetupExternalEventHandler : IExternalEventHandler
    {
        private readonly object _sync = new object();
        private ProjectSetupRequest _request;
        public event Action<ProjectSetupSnapshot> Completed;

        public bool TryQueue(ProjectSetupRequest request)
        {
            lock (_sync)
            {
                if (_request != null) return false;
                _request = request;
                return true;
            }
        }

        public void CancelPending()
        {
            lock (_sync) { _request = null; }
        }

        public void Execute(UIApplication app)
        {
            ProjectSetupRequest request;
            lock (_sync) { request = _request; _request = null; }
            if (request == null) return;
            try
            {
                ProjectSetupSnapshot snapshot = ProjectSetupService.Execute(app, request);
                Completed?.Invoke(snapshot);
            }
            catch (Exception ex)
            {
                AppDialog.Error(app, "Project Launch", "The requested Project Setup action could not be completed.\n\n" + ex.Message);
                try { Completed?.Invoke(ProjectSetupService.Scan(app, null)); } catch { }
            }
        }

        public string GetName() => "Parallel Systems - Project Setup";
    }
}
