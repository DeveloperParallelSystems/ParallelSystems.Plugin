using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using ParallelSystemsPlugin.LiveSession;

internal static class DynamoTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action, string label)
    {
        try { action(); } catch (InvalidOperationException) { return; } catch (ArgumentException) { return; }
        throw new Exception("Expected rejection: " + label);
    }
    internal static void Run()
    {
        var registry = new LiveDocumentRegistry<FakeDocument>(doc => doc.Valid);
        var original = new FakeDocument { NativeId = 1 };
        string originalId = registry.GetId(original);
        Check(registry.GetId(new FakeDocument { NativeId = 1 }) == originalId, "Fresh wrappers for the same native document must keep the same ID.");
        Check(registry.GetId(new FakeDocument { NativeId = 2 }) != originalId, "Different native documents must have distinct IDs.");
        original.Valid = false;
        Check(registry.GetId(new FakeDocument { NativeId = 1 }) != originalId, "Reopened documents must get new IDs.");
        string folder = Path.Combine(Path.GetTempPath(), "DynamoBridgeTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            string path = Path.Combine(folder, "test.dyn");
            File.WriteAllText(path, "{\"Uuid\":\"" + Guid.NewGuid() + "\",\"Name\":\"Test\",\"Nodes\":[],\"Connectors\":[],\"View\":{\"Dynamo\":{\"RunType\":\"Automatic\"}}}");
            var file = DynamoGraphFile.Read(path);
            Check((bool)file.Describe()["inspectionOnly"], "Inspect must only read the graph.");
            Reject(() => file.RequireHash("old"), "changed graph hash");
            Reject(() => DynamoGraphFile.Read("relative.dyn"), "relative graph path");
            Reject(() => DynamoGraphFile.Read(path + ".txt"), "non-graph path");
            var unavailable = new DynamoLiveSession(() => null);
            Check(!(bool)unavailable.Status()["available"], "Missing Dynamo must be reported without loading a runtime.");
            var model = new FakeModel();
            var bridge = new DynamoLiveSession(() => model);
            var openArgs = new JObject { ["path"] = path, ["expectedSha256"] = file.Sha256 };
            model.CurrentWorkspace.HasUnsavedChanges = true;
            Reject(() => bridge.Open(openArgs), "unsaved workspace");
            model.CurrentWorkspace.HasUnsavedChanges = false;
            model.Scheduler.HasPendingTasks = true;
            Reject(() => bridge.Open(openArgs), "pending Dynamo work");
            model.Scheduler.HasPendingTasks = false;
            var opened = bridge.Open(openArgs);
            Check(model.Runs == 0 && model.CurrentWorkspace.RunSettings.RunType == "Manual", "Opening must force Manual and never execute.");
            var args = new JObject { ["workspaceId"] = opened["workspaceId"], ["expectedSha256"] = file.Sha256,
                ["runId"] = Guid.NewGuid().ToString(), ["viewId"] = "123" };
            Reject(() => bridge.Run(args, "doc", "456"), "changed active view");
            model.CurrentWorkspace.HasUnsavedChanges = true;
            Reject(() => bridge.Run(args, "doc", "123"), "modified graph");
            model.CurrentWorkspace.HasUnsavedChanges = false;
            model.CurrentWorkspace.RunSettings.ForceBlockRun = true;
            Reject(() => bridge.Run(args, "doc", "123"), "Dynamo trust/run block");
            model.CurrentWorkspace.RunSettings.ForceBlockRun = false;
            File.AppendAllText(path, " ");
            Reject(() => bridge.Run(args, "doc", "123"), "file changed after opening");
            file = DynamoGraphFile.Read(path);
            openArgs["expectedSha256"] = file.Sha256;
            opened = bridge.Open(openArgs);
            args["workspaceId"] = opened["workspaceId"];
            args["expectedSha256"] = file.Sha256;
            var result = bridge.Run(args, "doc", "123");
            Check((string)result["status"] == "completed" && model.Runs == 1, "The graph must run exactly once.");
            Check(model.Scheduler.ProcessMode == FakeProcessMode.Asynchronous, "Original scheduler mode must be restored.");
            Check((string)result.SelectToken("nodes[0].outputs[0].status") == "preview_fit_uncommitted", "Structured script output must be preserved.");
            bridge.Run(args, "doc", "123");
            Check(model.Runs == 1, "Retrying a runId must not execute twice.");
            args["viewId"] = "456";
            Reject(() => bridge.Run(args, "doc", "456"), "reused run ID with changed context");
            args["viewId"] = "123";
            args["runId"] = Guid.NewGuid().ToString();
            model.ThrowDuringRun = true;
            result = bridge.Run(args, "doc", "123");
            Check((string)result["status"] == "outcome_unknown", "Exception after starting must not imply rollback.");
            Check(model.Scheduler.ProcessMode == FakeProcessMode.Asynchronous, "Exception must restore scheduler mode.");
            Check((string)bridge.Result((string)args["runId"])["status"] == "outcome_unknown", "Failed run must remain retrievable.");
            bridge.Run(args, "doc", "123");
            Check(model.Runs == 2, "Retrying a failed run must not execute twice.");
            model.ThrowDuringRun = false;
            model.SignalCompletion = false;
            args["runId"] = Guid.NewGuid().ToString();
            result = bridge.Run(args, "doc", "123");
            Check((string)result["status"] == "outcome_unknown" && result["nodes"] == null,
                "Missing completion must never expose old node values as this run's output.");

            var writer = new DynamoValueWriter();
            var cycle = new ArrayList(); cycle.Add(cycle);
            writer.Write(cycle);
            Check(writer.Truncated, "Cyclic/overdeep outputs must be bounded.");
            writer = new DynamoValueWriter();
            writer.Write(new string('x', 40000));
            Check(writer.Truncated, "Large text outputs must be visibly truncated.");
            Check(model.CurrentWorkspace.Subscribers == 0, "Evaluation event handlers must be removed.");
        }
        finally { Directory.Delete(folder, true); }
        Console.WriteLine("PASS: Dynamo inspection, manual open, hash/context guards, synchronous run, structured outputs, idempotency, failure records, bounded values, scheduler/event cleanup.");
    }

    public enum FakeProcessMode { Synchronous, Asynchronous }
    private sealed class FakeDocument
    {
        internal int NativeId;
        internal bool Valid = true;
        public override bool Equals(object other) => other is FakeDocument doc && doc.NativeId == NativeId;
        public override int GetHashCode() => NativeId;
    }
    public sealed class FakeScheduler
    {
        public bool HasPendingTasks { get; set; }
        public FakeProcessMode ProcessMode { get; set; } = FakeProcessMode.Asynchronous;
    }
    public sealed class FakeSettings
    {
        public string RunType { get; set; } = "Manual";
        public bool RunEnabled { get; set; } = true;
        public bool ForceBlockRun { get; set; }
    }
    public sealed class FakeCompletionArgs : EventArgs
    {
        public bool EvaluationSucceeded => true;
        public bool EvaluationTookPlace => true;
        public Exception Error => null;
    }
    public sealed class FakeWorkspace
    {
        public Guid Guid { get; } = Guid.NewGuid();
        public string Name => "Test";
        public string FileName { get; set; } = "";
        public bool HasUnsavedChanges { get; set; }
        public FakeSettings RunSettings { get; } = new FakeSettings();
        public object EngineController { get; } = new object();
        public FakeNode[] Nodes { get; } = { new FakeNode() };
        public event EventHandler<FakeCompletionArgs> EvaluationCompleted;
        public int Subscribers => EvaluationCompleted?.GetInvocationList().Length ?? 0;
        public void Complete() => EvaluationCompleted?.Invoke(this, new FakeCompletionArgs());
    }
    public sealed class FakeNode
    {
        public Guid GUID { get; } = Guid.NewGuid();
        public string Name => "Python Script";
        public string State => "Active";
        public object[] OutPorts { get; } = { new object() };
        public object[] NodeInfos => new object[0];
        public object GetValue(int port, object engine) => new Dictionary<string, object> { ["status"] = "preview_fit_uncommitted", ["committed"] = false };
    }
    public sealed class FakeModel
    {
        public FakeScheduler Scheduler { get; } = new FakeScheduler();
        public FakeWorkspace CurrentWorkspace { get; private set; } = new FakeWorkspace();
        public FakeWorkspace[] Workspaces => new[] { CurrentWorkspace };
        public int Runs { get; private set; }
        public bool ThrowDuringRun { get; set; }
        public bool SignalCompletion { get; set; } = true;
        public void OpenFileFromPath(string path, bool forceManual) => CurrentWorkspace = new FakeWorkspace { FileName = path };
        public void ForceRun()
        {
            Check(Scheduler.ProcessMode == FakeProcessMode.Synchronous, "Must run synchronously inside Revit's API context.");
            Runs++;
            if (ThrowDuringRun) throw new InvalidOperationException("test failure after execution started");
            if (SignalCompletion) CurrentWorkspace.Complete();
        }
    }
}
