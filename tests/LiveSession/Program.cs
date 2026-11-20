using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ParallelSystemsPlugin.LiveSession;

internal static class Program
{
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); }

    private static async Task Main()
    {
        DynamoTests.Run();
        int executions = 0;
        using (var queue = new LiveSessionRequestQueue(() => true, 50))
        {
            var expired = await queue.Submit(new JObject());
            Check((string)expired["error"]["code"] == "timeout", "Busy Revit must time out.");
            queue.Execute(_ => { executions++; return new JObject(); });
            Check(executions == 0, "Expired work must never execute later.");
            var pending = queue.Submit(new JObject());
            var busy = await queue.Submit(new JObject());
            Check((string)busy["error"]["code"] == "busy", "Only one pending operation is allowed.");
            queue.Execute(_ => new JObject { ["ok"] = true });
            Check((bool)(await pending)["ok"], "Successful dispatch must return its result.");
            pending = queue.Submit(new JObject());
            queue.Dispose();
            Check((string)(await pending)["error"]["code"] == "offline", "OFF cancels pending work.");
            queue.Execute(_ => { executions++; return new JObject(); });
            Check(executions == 0, "Disabled work must never execute.");
        }
        using (var queue = new LiveSessionRequestQueue(() => false))
            Check((string)(await queue.Submit(new JObject()))["error"]["code"] == "busy", "Rejected external events must fail promptly.");
        using (var queue = new LiveSessionRequestQueue(() => true))
        {
            var pending = queue.Submit(new JObject());
            queue.Execute(_ => throw new InvalidOperationException("test"));
            Check((string)(await pending)["error"]["code"] == "command_failed", "Command failures must be returned.");
        }
        using (var queue = new LiveSessionRequestQueue(() => true, 100))
        using (var entered = new ManualResetEventSlim())
        {
            var pending = queue.Submit(new JObject());
            var execution = Task.Run(() => queue.Execute(_ =>
            {
                entered.Set();
                Thread.Sleep(200);
                return new JObject { ["ok"] = true };
            }));
            Check(entered.Wait(2000), "Execution must start.");
            Check((bool)(await pending)["ok"], "A running operation must return its outcome, not a cancellation.");
            await execution;
        }

        string directory = Path.Combine(Path.GetTempPath(), "ParallelSystems-LiveSessionTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            int dispatched = 0;
            using (var server = new LiveSessionServer(_ =>
            {
                dispatched++;
                return Task.FromResult(new JObject { ["ok"] = true, ["result"] = "connected" });
            }, directory))
            {
                server.Start("test");
                var session = JObject.Parse(File.ReadAllText(server.DiscoveryPath));
                int port = (int)session["port"];
                string token = (string)session["token"];
                Check((string)session["host"] == "127.0.0.1", "Server must bind to loopback.");
                var unauthorized = await Call(port, new JObject { ["token"] = "wrong" }.ToString(Formatting.None));
                Check((string)unauthorized["error"]["code"] == "unauthorized" && dispatched == 0, "Bad tokens must not reach Revit.");
                var result = await Call(port, new JObject { ["token"] = token, ["command"] = "session.info" }.ToString(Formatting.None));
                Check((bool)result["ok"] && dispatched == 1, "Authenticated client must connect.");
                Check((string)(await Call(port, "not json"))["error"]["code"] == "invalid_request", "Malformed JSON must fail.");
                Check((string)(await Call(port, "{\"token\":{}}"))["error"]["code"] == "invalid_request", "Non-string tokens must fail.");
                Check((string)(await Call(port, "{} {}"))["error"]["code"] == "invalid_request", "Multiple JSON objects must fail.");
                Check((string)(await Call(port, new string(' ', 65537)))["error"]["code"] == "invalid_request", "Oversize requests must fail.");
                var afterError = await Call(port, new JObject { ["token"] = token }.ToString(Formatting.None));
                Check((bool)afterError["ok"], "Server must survive malformed clients.");
                using (var stalled = new TcpClient())
                {
                    await stalled.ConnectAsync("127.0.0.1", port);
                    await Task.Delay(30);
                    server.Dispose();
                    Check(!File.Exists(server.DiscoveryPath), "OFF removes discovery credentials.");
                    using (var probe = new TcpClient())
                    {
                        bool refused = false;
                        try { await probe.ConnectAsync("127.0.0.1", port); }
                        catch (SocketException) { refused = true; }
                        Check(refused, "OFF must close the listener.");
                    }
                }
                using (var restarted = new LiveSessionServer(_ => Task.FromResult(new JObject { ["ok"] = true }), directory))
                {
                    restarted.Start("test");
                    var next = JObject.Parse(File.ReadAllText(restarted.DiscoveryPath));
                    Check((string)next["token"] != token, "Re-enabling must rotate the token.");
                    var oldToken = await Call((int)next["port"], new JObject { ["token"] = token }.ToString(Formatting.None));
                    Check((string)oldToken["error"]["code"] == "unauthorized", "Old credentials must fail after re-enabling.");
                }
            }
        }
        finally { Directory.Delete(directory, true); }
        Console.WriteLine("PASS: authentication, roundtrip, malformed/oversize requests, recovery, timeout cancellation, queue bounds, event rejection, errors, shutdown.");
    }

    private static async Task<JObject> Call(int port, string request)
    {
        using (var client = new TcpClient())
        {
            await client.ConnectAsync("127.0.0.1", port);
            var stream = client.GetStream();
            byte[] bytes = Encoding.UTF8.GetBytes(request + "\n");
            await stream.WriteAsync(bytes, 0, bytes.Length);
            using (var reader = new StreamReader(stream))
                return JObject.Parse(await reader.ReadLineAsync());
        }
    }
}
