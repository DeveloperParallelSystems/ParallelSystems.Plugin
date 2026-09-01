using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace ParallelSystemsPlugin.Timesheets
{
    /// <summary>
    /// Silent, automatic Revit timesheet tracker.
    /// It records compact session checkpoints and never uploads individual element IDs.
    /// </summary>
    internal sealed class TimesheetTracker : IDisposable
    {
        private readonly TrackerSettings _settings;
        private readonly AutomaticContextResolver _resolver;
        private readonly LocalOutboxClient _outbox;
        private readonly Stopwatch _clock;
        private readonly string _installationId;
        private readonly string _pluginVersion;

        private Document _document;
        private View _view;
        private ProjectContext _project;
        private ViewContext _viewContext;
        private EvidenceAccumulator _evidence;
        private Guid _sessionId;
        private DateTime _sessionStartedUtc;
        private long _sequence;
        private double _lastSampleSeconds;
        private double _lastCheckpointSeconds;
        private int _measuredActiveSeconds;
        private int _engagedSeconds;
        private int _foregroundSeconds;
        private int _inactiveSeconds;
        private bool _sessionOpen;
        private bool _disposed;

        private TimesheetTracker(TrackerSettings settings)
        {
            _settings = settings;
            _resolver = new AutomaticContextResolver();
            _outbox = new LocalOutboxClient(settings);
            _installationId = _outbox.InstallationId;
            _pluginVersion = GetPluginVersion();
            _clock = Stopwatch.StartNew();
            _lastSampleSeconds = _clock.Elapsed.TotalSeconds;
            _lastCheckpointSeconds = _lastSampleSeconds;
            _outbox.RequestFlush();
        }

        private static string GetPluginVersion()
        {
            try
            {
                Assembly assembly = typeof(TimesheetTracker).Assembly;
                var attribute = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                string value = attribute?.InformationalVersion;

                if (string.IsNullOrWhiteSpace(value))
                    value = assembly.GetName().Version?.ToString();

                if (string.IsNullOrWhiteSpace(value))
                    return "1.17.8";

                int suffix = value.IndexOf('+');
                if (suffix >= 0)
                    value = value.Substring(0, suffix);

                string[] parts = value.Split('.');
                if (parts.Length == 4 && parts[3] == "0")
                    value = string.Join(".", parts, 0, 3);

                return value;
            }
            catch
            {
                return "1.17.8";
            }
        }

        public static TimesheetTracker TryCreate()
        {
            try
            {
                var settings = TrackerSettings.Load();
                return settings.Enabled ? new TimesheetTracker(settings) : null;
            }
            catch
            {
                // Timesheet startup failure must never prevent the existing plugin tools from loading.
                return null;
            }
        }

        public void OnDocumentOpened(Document document)
        {
            if (_disposed || document == null) return;
            TryStartOrSwitch(document, SafeActiveView(document));
        }

        public void OnViewActivated(Document document, View view)
        {
            if (_disposed || document == null || view == null) return;
            TryStartOrSwitch(document, view);
        }

        public void OnDocumentSaved(Document document)
        {
            if (_disposed || document == null) return;
            EnsureSession(document, SafeActiveView(document));
            QueueCheckpoint("Checkpoint", false);
        }

        public void OnDocumentClosed()
        {
            if (_disposed) return;
            CloseCurrentSession();
        }

        public void OnDocumentChanged(DocumentChangedEventArgs args)
        {
            if (_disposed || args == null) return;

            Document document;
            try { document = args.GetDocument(); }
            catch { return; }
            if (document == null) return;

            // DocumentChanged can also fire for a background document. Do not let a
            // background transaction switch the employee's active tracking context.
            if (!_sessionOpen || _document == null || !ReferenceEquals(_document, document)) return;

            try
            {
                try
                {
                    foreach (var transactionName in args.GetTransactionNames() ?? new List<string>())
                        _evidence.AddTransactionName(transactionName);
                }
                catch
                {
                    // Transaction names are useful evidence but are not required for tracking.
                }

                var added = args.GetAddedElementIds()?.ToList() ?? new List<ElementId>();
                var modified = args.GetModifiedElementIds()?.ToList() ?? new List<ElementId>();
                var deleted = args.GetDeletedElementIds()?.ToList() ?? new List<ElementId>();

                foreach (var id in added) _evidence.AddCreated();
                foreach (var id in modified) _evidence.AddModified();
                foreach (var id in deleted) _evidence.AddDeleted();

                var inspect = added.Concat(modified)
                    .Take(_settings.MaxElementsInspectedPerChange)
                    .ToList();
                var totalInspectable = added.Count + modified.Count;
                _evidence.AddUninspected(totalInspectable - inspect.Count);

                foreach (var id in inspect)
                {
                    Element element = null;
                    try { element = document.GetElement(id); }
                    catch { }
                    var item = _resolver.ResolveElement(document, element);
                    _evidence.AddCategory(item.Category);
                    _evidence.AddLevel(item.Level);
                    _evidence.AddSystem(item.System);
                    _evidence.AddArea(item.Area);
                    _evidence.AddZone(item.Zone);
                    _evidence.AddWorkset(item.Workset);
                }
            }
            catch
            {
                // DocumentChanged is performance-sensitive. Evidence is best-effort only.
            }
        }

        public void OnIdling(UIApplication application)
        {
            if (_disposed || application == null) return;

            var now = _clock.Elapsed.TotalSeconds;
            var elapsed = now - _lastSampleSeconds;
            if (elapsed < _settings.SamplingIntervalSeconds) return;
            _lastSampleSeconds = now;

            Document document = null;
            View view = null;
            try
            {
                document = application.ActiveUIDocument?.Document;
                view = application.ActiveUIDocument?.ActiveView;
            }
            catch { }

            if (document == null || view == null)
            {
                CloseCurrentSession();
                return;
            }

            EnsureSession(document, view);
            if (!_sessionOpen) return;

            // Cap a single sample to avoid counting machine sleep or a suspended Revit process as work.
            var seconds = (int)Math.Round(Math.Min(Math.Max(elapsed, 0), 300));
            if (seconds <= 0) return;

            var foreground = WindowsActivityDetector.IsRevitForeground();
            var idleSeconds = WindowsActivityDetector.GetSystemIdleTime().TotalSeconds;

            if (foreground) _foregroundSeconds += seconds;

            if (foreground && idleSeconds <= _settings.ActiveInputThresholdSeconds)
            {
                _measuredActiveSeconds += seconds;
                _engagedSeconds += seconds;
            }
            else if (foreground && idleSeconds <= _settings.EngagedGraceSeconds)
            {
                // Reading, thinking, regeneration, and short pauses remain attributable to the Revit work block.
                _engagedSeconds += seconds;
            }
            else
            {
                // Screen lock, another foreground application, or extended inactivity.
                _inactiveSeconds += seconds;
            }

            if (now - _lastCheckpointSeconds >= _settings.CheckpointIntervalSeconds)
            {
                QueueCheckpoint("Checkpoint", false);
                _lastCheckpointSeconds = now;
            }
        }

        private void TryStartOrSwitch(Document document, View view)
        {
            if (document == null || view == null) return;

            var project = _resolver.ResolveProject(document);
            var currentViewId = SafeViewId(view);
            var changed = !_sessionOpen ||
                          !string.Equals(_project?.ProjectKey, project.ProjectKey, StringComparison.OrdinalIgnoreCase) ||
                          SafeViewId(_view) != currentViewId;

            if (changed)
            {
                CloseCurrentSession();
                StartSession(document, view, project);
            }
            else
            {
                _document = document;
                _view = view;
            }
        }

        private void EnsureSession(Document document, View view)
        {
            if (document == null || view == null) return;
            TryStartOrSwitch(document, view);
        }

        private void StartSession(Document document, View view, ProjectContext project)
        {
            try
            {
                _document = document;
                _view = view;
                _project = project ?? _resolver.ResolveProject(document);
                _evidence = new EvidenceAccumulator();
                _viewContext = _resolver.ResolveView(document, view, _evidence);
                _sessionId = Guid.NewGuid();
                _sessionStartedUtc = DateTime.UtcNow;
                _sequence = 0;
                _measuredActiveSeconds = 0;
                _engagedSeconds = 0;
                _foregroundSeconds = 0;
                _inactiveSeconds = 0;
                _sessionOpen = true;
                _lastCheckpointSeconds = _clock.Elapsed.TotalSeconds;
                // Do not create a zero-duration database row merely because a view was opened.
                // The first meaningful checkpoint is sent after activity, a save, a timed
                // checkpoint, or when the view/session closes.
            }
            catch
            {
                ResetSession();
            }
        }

        private void CloseCurrentSession()
        {
            if (!_sessionOpen) return;
            if (HasMeaningfulSessionData()) QueueCheckpoint("Stop", true);
            ResetSession();
        }

        private bool HasMeaningfulSessionData()
        {
            return _measuredActiveSeconds > 0 ||
                   _engagedSeconds > 0 ||
                   _foregroundSeconds > 0 ||
                   (_evidence != null && _evidence.HasModelChanges);
        }

        private void QueueCheckpoint(string eventType, bool close)
        {
            if (!_sessionOpen || _project == null || _viewContext == null || !HasMeaningfulSessionData()) return;

            try
            {
                if (!close && _document != null && _view != null)
                {
                    _viewContext = _resolver.ResolveView(_document, _view, _evidence);
                }

                var evidence = _evidence?.Clone() ?? new EvidenceAccumulator();
                var checkpoint = new TrackerCheckpointRequest
                {
                    SchemaVersion = 3,
                    MessageId = Guid.NewGuid(),
                    SessionId = _sessionId,
                    Sequence = ++_sequence,
                    EventType = eventType,
                    OccurredAtUtc = DateTime.UtcNow,
                    ClientWorkDate = DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    SessionStartedAtUtc = _sessionStartedUtc,
                    SessionEndedAtUtc = close ? DateTime.UtcNow : (DateTime?)null,
                    InstallationId = _installationId,
                    MachineName = Environment.MachineName,
                    WindowsUserName = Environment.UserName,
                    RevitUserName = _project.RevitUserName,
                    RevitVersion = SafeRevitVersion(),
                    PluginVersion = _pluginVersion,
                    ProjectKey = _project.ProjectKey,
                    ProjectName = _project.ProjectName,
                    ProjectNumber = _project.ProjectNumber,
                    DocumentTitle = _project.DocumentTitle,
                    DocumentPathHash = _project.DocumentPathHash,
                    CloudProjectId = _project.CloudProjectId,
                    CloudModelId = _project.CloudModelId,
                    ViewId = _viewContext.ViewId,
                    ViewName = _viewContext.ViewName,
                    ViewType = _viewContext.ViewType,
                    SheetNumber = _viewContext.SheetNumber,
                    ViewTemplateName = _viewContext.ViewTemplateName,
                    ViewDiscipline = _viewContext.ViewDiscipline,
                    ViewSubDiscipline = _viewContext.ViewSubDiscipline,
                    MeasuredActiveSeconds = _measuredActiveSeconds,
                    EngagedSeconds = _engagedSeconds,
                    ForegroundSeconds = _foregroundSeconds,
                    InactiveSeconds = _inactiveSeconds,
                    IsClosed = close,
                    DetectedArea = _viewContext.Area,
                    DetectedLevel = _viewContext.Level,
                    DetectedZone = _viewContext.Zone,
                    DetectedSystem = _viewContext.System,
                    DetectedActivity = _viewContext.Activity,
                    DetectedScope = _viewContext.Scope,
                    DetectedStatus = _viewContext.Status,
                    CategoryCounts = TopCounts(evidence.CategoryCounts),
                    LevelCounts = TopCounts(evidence.LevelCounts),
                    SystemCounts = TopCounts(evidence.SystemCounts),
                    AreaCounts = TopCounts(evidence.AreaCounts),
                    ZoneCounts = TopCounts(evidence.ZoneCounts),
                    WorksetCounts = TopCounts(evidence.WorksetCounts),
                    TransactionNameCounts = TopCounts(evidence.TransactionNameCounts),
                    CreatedElementCount = evidence.CreatedElementCount,
                    ModifiedElementCount = evidence.ModifiedElementCount,
                    DeletedElementCount = evidence.DeletedElementCount,
                    UninspectedElementCount = evidence.UninspectedElementCount
                };

                _outbox.Queue(checkpoint);
            }
            catch
            {
                // A telemetry failure never propagates into a Revit command or event.
            }
        }

        private static Dictionary<string, int> TopCounts(
            IDictionary<string, int> values,
            int limit = 25)
        {
            if (values == null || values.Count == 0)
                return new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            return values
                .OrderByDescending(x => x.Value)
                .ThenBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Take(limit)
                .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        }

        private string SafeRevitVersion()
        {
            try { return _document?.Application?.VersionNumber; }
            catch { return null; }
        }

        private static View SafeActiveView(Document document)
        {
            try { return document?.ActiveView; }
            catch { return null; }
        }

        private static long? SafeViewId(View view)
        {
            if (view == null) return null;
            try { return ParallelSystemsPlugin.Compatibility.RevitApiCompatibility.GetElementIdValue(view.Id); }
            catch { return null; }
        }

        private void ResetSession()
        {
            _document = null;
            _view = null;
            _project = null;
            _viewContext = null;
            _evidence = null;
            _sessionOpen = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            try { CloseCurrentSession(); } catch { }
            _disposed = true;
            try { _outbox?.RequestFlush(); } catch { }
            try { _outbox?.Dispose(); } catch { }
            try { _clock?.Stop(); } catch { }
        }
    }
}
