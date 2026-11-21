using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    // All methods run in the bridge ExternalEvent. No Dynamo runtime is started or
    // shut down here: the user opens Dynamo in Revit, which owns its dependencies.
    internal sealed class DynamoLiveSession
    {
        private readonly Func<object> _getModel;
        private readonly Dictionary<string, JObject> _runs = new Dictionary<string, JObject>();
        private object _openedWorkspace;
        private DynamoGraphFile _openedFile;
        internal DynamoLiveSession(Func<object> getModel = null) { _getModel = getModel ?? DynamoReflection.Model; }

        internal JObject Status()
        {
            var model = _getModel();
            if (model == null) return new JObject { ["available"] = false,
                ["message"] = "Open Manage > Dynamo in this Revit session, then try again." };
            var workspace = DynamoReflection.Get(model, "CurrentWorkspace");
            return new JObject
            {
                ["available"] = true, ["assemblyVersion"] = model.GetType().Assembly.GetName().Version.ToString(),
                ["workspace"] = workspace == null ? null : DescribeWorkspace(workspace),
                ["hasPendingTasks"] = (bool)DynamoReflection.Get(DynamoReflection.Get(model, "Scheduler"), "HasPendingTasks")
            };
        }

        internal JObject Open(JObject args)
        {
            var file = DynamoGraphFile.Read((string)args["path"]);
            file.RequireHash((string)args["expectedSha256"]);
            var model = RequireModel();
            RequireIdle(model);
            foreach (var existing in DynamoReflection.Items(DynamoReflection.Get(model, "Workspaces")))
                if ((bool)DynamoReflection.Get(existing, "HasUnsavedChanges"))
                    throw new InvalidOperationException("Save or close unsaved Dynamo workspaces before opening a graph through the bridge.");
            _openedWorkspace = null;
            _openedFile = null;
            // Hold a read-only sharing lock until Dynamo has read exactly the inspected file.
            using (var locked = System.IO.File.Open(file.Path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.Read))
            {
                DynamoGraphFile.Read(file.Path).RequireHash(file.Sha256);
                DynamoReflection.Call(model, "OpenFileFromPath", file.Path, true);
            }
            var workspace = DynamoReflection.Get(model, "CurrentWorkspace");
            if (!string.Equals(DynamoReflection.Text(DynamoReflection.Get(workspace, "FileName")), file.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Dynamo did not open the requested file.");
            RequireManual(workspace);
            _openedWorkspace = workspace;
            _openedFile = file;
            var result = DescribeWorkspace(workspace);
            result["sha256"] = file.Sha256;
            result["executed"] = false;
            return result;
        }

        internal JObject Workspace()
        {
            var model = RequireModel();
            RequireIdle(model);
            var workspace = DynamoReflection.Get(model, "CurrentWorkspace");
            var result = DescribeWorkspace(workspace);
            result["nodes"] = Nodes(workspace, false);
            result["openedThroughBridge"] = ReferenceEquals(workspace, _openedWorkspace);
            return result;
        }

        internal JObject Result(string runId)
        {
            if (runId == null || !_runs.TryGetValue(runId, out var result))
                throw new ArgumentException("Unknown runId for this bridge ON cycle.");
            return (JObject)result.DeepClone();
        }

        internal JObject Run(JObject args, string documentId, string activeViewId)
        {
            string runId = (string)args["runId"];
            if (!Guid.TryParse(runId, out _)) throw new ArgumentException("runId must be a caller-generated GUID. Reuse it only to retrieve this same run.");
            if (_runs.TryGetValue(runId, out var previous))
            {
                if ((string)previous["documentId"] != documentId || (string)previous["viewId"] != (string)args["viewId"] ||
                    (string)previous["sha256"] != (string)args["expectedSha256"] || (string)previous["workspaceId"] != (string)args["workspaceId"])
                    throw new ArgumentException("runId was already used for a different graph or context.");
                return (JObject)previous.DeepClone();
            }
            if (_runs.Count >= 32) throw new InvalidOperationException("This bridge cycle already holds 32 runs. Export results, then toggle OFF/ON to start a new cycle.");
            var model = RequireModel();
            RequireIdle(model);
            var workspace = DynamoReflection.Get(model, "CurrentWorkspace");
            RequireManual(workspace);
            if (!ReferenceEquals(workspace, _openedWorkspace) || _openedFile == null)
                throw new InvalidOperationException("Open the inspected graph with dynamo.open before running it.");
            if (DynamoReflection.Text(DynamoReflection.Get(workspace, "Guid")) != (string)args["workspaceId"])
                throw new InvalidOperationException("workspaceId does not match the active Dynamo workspace.");
            if (activeViewId != (string)args["viewId"])
                throw new InvalidOperationException("The active Revit view changed or viewId is missing. Query session.info again.");
            if ((bool)DynamoReflection.Get(workspace, "HasUnsavedChanges"))
                throw new InvalidOperationException("The graph has unsaved edits. Save, inspect and reopen it before running.");
            _openedFile.RequireHash((string)args["expectedSha256"]);
            DynamoGraphFile.Read(_openedFile.Path).RequireHash(_openedFile.Sha256);
            var settings = DynamoReflection.Get(workspace, "RunSettings");
            if (!(bool)DynamoReflection.Get(settings, "RunEnabled") || (bool?)DynamoReflection.Optional(settings, "ForceBlockRun") == true)
                throw new InvalidOperationException("Dynamo has disabled execution. Resolve its trust, dependency or run-state prompt in Dynamo.");
            var scheduler = DynamoReflection.Get(model, "Scheduler");
            var mode = scheduler.GetType().GetProperty("ProcessMode");
            if (mode?.SetMethod == null) throw new NotSupportedException("This Dynamo scheduler does not expose synchronous execution.");
            var oldMode = mode.GetValue(scheduler);
            var syncMode = Enum.Parse(mode.PropertyType, "Synchronous");
            var completedEvent = workspace.GetType().GetEvent("EvaluationCompleted");
            if (completedEvent == null) throw new NotSupportedException("Dynamo evaluation events are unavailable.");
            var completion = new Completion();
            var handler = Delegate.CreateDelegate(completedEvent.EventHandlerType, completion,
                typeof(Completion).GetMethod(nameof(Completion.OnCompleted)));
            var result = new JObject
            {
                ["runId"] = runId, ["status"] = "running", ["documentId"] = documentId, ["viewId"] = activeViewId,
                ["workspaceId"] = (string)args["workspaceId"], ["path"] = _openedFile.Path, ["sha256"] = _openedFile.Sha256,
                ["startedUtc"] = DateTime.UtcNow.ToString("O")
            };
            _runs.Add(runId, result); // Record before execution: an error must not cause duplicate application on retry.
            var watch = Stopwatch.StartNew();
            completedEvent.AddEventHandler(workspace, handler);
            try
            {
                // Revit's normal Dynamo scheduler runs on Idling. Synchronous mode
                // keeps this run in the same validated Revit document/view context.
                // Restore the user's scheduler in finally; never wrap script-owned
                // transactions in an outer Revit transaction.
                mode.SetValue(scheduler, syncMode);
                DynamoReflection.Call(model, "ForceRun");
                result["evaluationCompleted"] = completion.Seen;
                result["evaluationSucceeded"] = completion.Succeeded;
                result["evaluationTookPlace"] = completion.TookPlace;
                result["error"] = completion.Error;
                result["status"] = !completion.Seen ? "outcome_unknown" : completion.Succeeded ? "completed" : "evaluation_failed";
                if (completion.Seen)
                {
                    result["nodes"] = Nodes(workspace, true);
                    if (completion.Succeeded && ((JArray)result["nodes"]).OfType<JObject>().Any(n =>
                        new[] { "Error", "Warning", "PersistentWarning", "AstBuildBroken" }.Contains((string)n["state"])))
                        result["status"] = "completed_with_node_issues";
                }
                result["note"] = "Evaluation completion is not proof of script success or rollback. Inspect node outputs and the model before another run.";
            }
            catch (Exception ex)
            {
                result["status"] = "outcome_unknown";
                result["error"] = ex.Message;
                result["note"] = "The graph may have modified the model. Inspect it before retrying with a new runId.";
            }
            finally
            {
                try { mode.SetValue(scheduler, oldMode); }
                finally
                {
                    completedEvent.RemoveEventHandler(workspace, handler);
                    result["finishedUtc"] = DateTime.UtcNow.ToString("O");
                    result["elapsedMilliseconds"] = watch.ElapsedMilliseconds;
                }
            }
            return (JObject)result.DeepClone();
        }

        private object RequireModel() => _getModel() ?? throw new InvalidOperationException("Open Manage > Dynamo in Revit first.");
        private static void RequireIdle(object model)
        {
            if ((bool)DynamoReflection.Get(DynamoReflection.Get(model, "Scheduler"), "HasPendingTasks"))
                throw new InvalidOperationException("Dynamo has pending tasks. Wait for it to become idle.");
        }
        private static void RequireManual(object workspace)
        {
            if (DynamoReflection.Text(DynamoReflection.Get(DynamoReflection.Get(workspace, "RunSettings"), "RunType")) != "Manual")
                throw new InvalidOperationException("Only a home workspace in Manual mode can run through the bridge.");
        }
        private static JObject DescribeWorkspace(object workspace) => new JObject
        {
            ["workspaceId"] = DynamoReflection.Text(DynamoReflection.Get(workspace, "Guid")),
            ["name"] = DynamoReflection.Text(DynamoReflection.Get(workspace, "Name")),
            ["path"] = DynamoReflection.Text(DynamoReflection.Get(workspace, "FileName")),
            ["hasUnsavedChanges"] = (bool)DynamoReflection.Get(workspace, "HasUnsavedChanges"),
            ["runType"] = DynamoReflection.Text(DynamoReflection.Optional(DynamoReflection.Optional(workspace, "RunSettings"), "RunType")),
            ["nodeCount"] = DynamoReflection.Items(DynamoReflection.Get(workspace, "Nodes")).Length
        };

        private static JArray Nodes(object workspace, bool values)
        {
            var nodes = DynamoReflection.Items(DynamoReflection.Get(workspace, "Nodes"));
            var writer = new DynamoValueWriter();
            var result = new JArray();
            foreach (var node in nodes.Take(200))
            {
                var entry = new JObject
                {
                    ["nodeId"] = DynamoReflection.Text(DynamoReflection.Get(node, "GUID")),
                    ["name"] = DynamoReflection.Text(DynamoReflection.Get(node, "Name")),
                    ["type"] = node.GetType().FullName,
                    ["state"] = DynamoReflection.Text(DynamoReflection.Get(node, "State")),
                    ["messages"] = new JArray(DynamoReflection.Items(DynamoReflection.Optional(node, "NodeInfos"))
                        .Take(20).Select(info => writer.Write(DynamoReflection.Optional(info, "Message") ?? info.ToString())))
                };
                if (values)
                {
                    try
                    {
                        var ports = DynamoReflection.Items(DynamoReflection.Get(node, "OutPorts"));
                        var outputs = new JArray();
                        for (int port = 0; port < Math.Min(ports.Length, 8); port++)
                            outputs.Add(writer.Write(DynamoReflection.Call(node, "GetValue", port,
                                DynamoReflection.Get(workspace, "EngineController"))));
                        entry["outputs"] = outputs;
                        entry["outputsTruncated"] = ports.Length > 8 || writer.Truncated;
                    }
                    catch (Exception ex) { entry["outputError"] = ex.Message; }
                }
                result.Add(entry);
            }
            if (nodes.Length > 200) result.Add(new JObject { ["truncated"] = true, ["totalNodes"] = nodes.Length });
            return result;
        }

        private sealed class Completion
        {
            internal bool Seen, Succeeded, TookPlace;
            internal string Error;
            public void OnCompleted(object sender, EventArgs args)
            {
                Seen = true;
                Succeeded = (bool)DynamoReflection.Get(args, "EvaluationSucceeded");
                TookPlace = (bool)DynamoReflection.Get(args, "EvaluationTookPlace");
                // Dynamo throws when Error is queried after a successful evaluation.
                Error = Succeeded ? null : (DynamoReflection.Optional(args, "Error") as Exception)?.Message;
            }
        }
    }
}
