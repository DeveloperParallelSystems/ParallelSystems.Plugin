using System;
using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace ParallelSystemsPlugin.LiveSession
{
    internal sealed class LiveSessionBridge : IExternalEventHandler, IDisposable
    {
        private static LiveSessionBridge _current;
        private static PushButton _button;
        private ExternalEvent _event;
        private LiveSessionRequestQueue _queue;
        private LiveSessionServer _server;
        private readonly LiveSessionCommands _commands = new LiveSessionCommands();

        internal static bool IsEnabled => _current != null;

        internal static void AddRibbonButton(RibbonPanel panel)
        {
            var data = new PushButtonData("PS_LiveSession", "Live Session\nOFF",
                typeof(App).Assembly.Location, typeof(ToggleLiveSessionCommand).FullName)
            {
                AvailabilityClassName = typeof(LiveSessionAvailability).FullName,
                ToolTip = "Turn local API access to this Revit session ON or OFF. Starts OFF each time Revit opens."
            };
            _button = (PushButton)panel.AddItem(data);
        }

        internal static void Toggle(UIApplication app)
        {
            if (IsEnabled) { Stop(); return; }
            if (!App.IsUserAuthorized) throw new InvalidOperationException("Reconnect to authorize ParallelSystems first.");
            var bridge = new LiveSessionBridge();
            try
            {
                bridge._event = ExternalEvent.Create(bridge);
                bridge._queue = new LiveSessionRequestQueue(() =>
                {
                    var result = bridge._event.Raise();
                    return result == ExternalEventRequest.Accepted || result == ExternalEventRequest.Pending;
                });
                bridge._server = new LiveSessionServer(bridge._queue.Submit, Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ParallelSystems", "LiveSessions"));
                bridge._server.Start(app.Application.VersionNumber);
                _current = bridge;
                UpdateButton();
                TaskDialog.Show("ParallelSystems Live Session", "Live Session is ON.\n\n" +
                    "Local clients can inspect this session, control selection and views, edit text parameters, and run Dynamo graphs.\n\n" +
                    "Connection file:\n" + bridge._server.DiscoveryPath +
                    "\n\nClick Live Session again to turn access OFF.");
            }
            catch { bridge.Dispose(); _current = null; UpdateButton(); throw; }
        }

        internal static void Stop()
        {
            var bridge = _current;
            _current = null;
            bridge?.Dispose();
            UpdateButton();
        }

        private static void UpdateButton()
        {
            if (_button != null) _button.ItemText = "Live Session\n" + (IsEnabled ? "ON" : "OFF");
        }

        public void Execute(UIApplication app) => _queue.Execute(request =>
            App.IsUserAuthorized ? _commands.Execute(app, request) :
                LiveSessionServer.Error("unauthorized", "ParallelSystems authorization is no longer active."));

        public string GetName() => "ParallelSystems Live Session";

        public void Dispose()
        {
            _queue?.Dispose();
            _server?.Dispose();
            _event?.Dispose();
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ToggleLiveSessionCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData data, ref string message, ElementSet elements)
        {
            try { LiveSessionBridge.Toggle(data.Application); return Result.Succeeded; }
            catch (Exception ex) { message = ex.Message; return Result.Failed; }
        }
    }

    public sealed class LiveSessionAvailability : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) =>
            LiveSessionBridge.IsEnabled || App.IsUserAuthorized;
    }
}
