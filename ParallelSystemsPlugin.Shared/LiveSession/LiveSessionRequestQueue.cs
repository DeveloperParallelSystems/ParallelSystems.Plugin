using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    // A single pending operation. Expired work is removed before it can touch Revit.
    internal sealed class LiveSessionRequestQueue : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<bool> _raise;
        private readonly int _timeoutMilliseconds;
        private TaskCompletionSource<JObject> _completion;
        private JObject _request;
        private Stopwatch _pendingAge;
        private bool _stopped;

        public LiveSessionRequestQueue(Func<bool> raise, int timeoutMilliseconds = 15000)
        { _raise = raise; _timeoutMilliseconds = timeoutMilliseconds; }

        public async Task<JObject> Submit(JObject request)
        {
            TaskCompletionSource<JObject> completion;
            lock (_gate)
            {
                if (_stopped) return LiveSessionServer.Error("offline", "Live Session is OFF.");
                if (_completion != null) return LiveSessionServer.Error("busy", "Another request is pending.");
                completion = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
                _completion = completion;
                _request = request;
                _pendingAge = Stopwatch.StartNew();
                try
                {
                    if (!_raise()) Finish(LiveSessionServer.Error("busy", "Revit could not accept the request."));
                }
                catch (Exception)
                { Finish(LiveSessionServer.Error("unavailable", "Revit could not schedule the request.")); }
            }
            await Task.WhenAny(completion.Task, Task.Delay(_timeoutMilliseconds)).ConfigureAwait(false);
            lock (_gate)
            {
                if (ReferenceEquals(_completion, completion))
                    Finish(LiveSessionServer.Error("timeout", "Revit was busy. The pending operation was cancelled."));
            }
            return await completion.Task.ConfigureAwait(false);
        }

        public void Execute(Func<JObject, JObject> execute)
        {
            lock (_gate)
            {
                if (_stopped || _completion == null) return;
                if (_pendingAge.ElapsedMilliseconds >= _timeoutMilliseconds)
                {
                    Finish(LiveSessionServer.Error("timeout", "Revit was busy. The pending operation was cancelled."));
                    return;
                }
                try { Finish(execute(_request)); }
                catch (Exception ex) { Finish(LiveSessionServer.Error("command_failed", ex.Message)); }
            }
        }

        private void Finish(JObject response)
        {
            _completion?.TrySetResult(response);
            _completion = null;
            _request = null;
            _pendingAge = null;
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _stopped = true;
                Finish(LiveSessionServer.Error("offline", "Live Session was turned OFF."));
            }
        }
    }
}
