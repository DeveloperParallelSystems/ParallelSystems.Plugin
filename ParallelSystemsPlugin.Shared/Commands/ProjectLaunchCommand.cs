// Created by Jhay
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using ParallelSystemsPlugin;
using ParallelSystemsPlugin.ProjectLaunch;
using ParallelSystemPlugin.UI;

namespace ParallelSystemPlugin.Commands
{
    [Transaction(TransactionMode.Manual)]
    public sealed class ProjectLaunchCommand : IExternalCommand
    {
        private static ProjectLaunchWindow _window;
        private static ExternalEvent _externalEvent;
        private static ProjectSetupExternalEventHandler _handler;

        internal static void RefreshForActiveDocumentChange()
        {
            if (_window != null && _window.IsLoaded)
                _window.RefreshProject(true);
        }

        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            if (!App.IsUserAuthorized)
            {
                AppDialog.Warn("Access Denied", "Your account is not authorized to use this function.");
                return Result.Cancelled;
            }

            if (_window != null && _window.IsLoaded)
            {
                _window.Activate();
                _window.RefreshProject();
                return Result.Succeeded;
            }

            _handler = new ProjectSetupExternalEventHandler();
            _externalEvent = ExternalEvent.Create(_handler);
            _window = new ProjectLaunchWindow(_handler, _externalEvent);
            _window.Closed += (sender, args) =>
            {
                _handler = null;
                _externalEvent?.Dispose();
                _externalEvent = null;
                _window = null;
            };
            _window.ShowModeless(data.Application.MainWindowHandle);
            _window.RefreshProject();
            return Result.Succeeded;
        }
    }
}
