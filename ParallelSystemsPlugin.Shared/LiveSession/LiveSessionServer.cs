using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ParallelSystemsPlugin.LiveSession
{
    // Transport only: no Revit objects or API calls may enter this class.
    internal sealed class LiveSessionServer : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Func<JObject, Task<JObject>> _dispatch;
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly string _token;
        private TcpClient _client;
        private bool _stopped;
        public string DiscoveryPath { get; }

        public LiveSessionServer(Func<JObject, Task<JObject>> dispatch, string discoveryDirectory)
        {
            _dispatch = dispatch;
            var bytes = new byte[32];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);
            _token = Convert.ToBase64String(bytes);
            DiscoveryPath = Path.Combine(discoveryDirectory, Process.GetCurrentProcess().Id + ".json");
        }

        public void Start(string revitVersion)
        {
            try
            {
                _listener.Start(8);
                Directory.CreateDirectory(Path.GetDirectoryName(DiscoveryPath));
                File.WriteAllText(DiscoveryPath, new JObject
                {
                    ["protocolVersion"] = 1,
                    ["processId"] = Process.GetCurrentProcess().Id,
                    ["revitVersion"] = revitVersion,
                    ["host"] = "127.0.0.1",
                    ["port"] = ((IPEndPoint)_listener.LocalEndpoint).Port,
                    ["token"] = _token,
                    ["startedUtc"] = DateTime.UtcNow.ToString("O")
                }.ToString(), new UTF8Encoding(false));
                Task.Run((Func<Task>)AcceptLoop);
            }
            catch { Dispose(); throw; }
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch (ObjectDisposedException) { return; }
                catch (SocketException) { return; }
                catch (InvalidOperationException) { return; } // Stop can race the next accept.
                lock (_gate)
                {
                    if (_stopped) { client.Dispose(); return; }
                    _client = client;
                }
                using (client)
                {
                    try { await Handle(client).ConfigureAwait(false); }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (ObjectDisposedException) { }
                    finally { lock (_gate) _client = null; }
                }
            }
        }

        private async Task Handle(TcpClient client)
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            stream.WriteTimeout = 5000;
            JObject response;
            try
            {
                // One bounded UTF-8 JSON line per connection. A total read deadline
                // also prevents a slow sender from keeping the sole worker occupied.
                var clock = Stopwatch.StartNew();
                using (var buffer = new MemoryStream())
                {
                    while (true)
                    {
                        stream.ReadTimeout = Math.Max(1, 5000 - (int)clock.ElapsedMilliseconds);
                        int value = stream.ReadByte();
                        if (value < 0) throw new InvalidDataException("Expected a JSON line.");
                        if (value == '\n') break;
                        if (buffer.Length >= 65536 || clock.ElapsedMilliseconds >= 5000)
                            throw new InvalidDataException("Request exceeds the size or time limit.");
                        buffer.WriteByte((byte)value);
                    }
                    using (var reader = new JsonTextReader(new StringReader(
                        new UTF8Encoding(false, true).GetString(buffer.ToArray()))) { MaxDepth = 16, DateParseHandling = DateParseHandling.None })
                    {
                        var request = JObject.Load(reader);
                        if (reader.Read()) throw new InvalidDataException("Only one JSON object is allowed.");
                        if (!TokenMatches((string)request["token"]))
                            response = Error("unauthorized", "Invalid session token.");
                        else
                            response = await _dispatch(request).ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is InvalidDataException ||
                                       ex is DecoderFallbackException || ex is ArgumentException)
            { response = Error("invalid_request", "Expected a bounded JSON object with a valid command and arguments."); }
            byte[] output = Encoding.UTF8.GetBytes(response.ToString(Formatting.None) + "\n");
            stream.Write(output, 0, output.Length);
        }

        private bool TokenMatches(string candidate)
        {
            if (candidate == null || candidate.Length != _token.Length) return false;
            int difference = 0;
            for (int i = 0; i < candidate.Length; i++) difference |= candidate[i] ^ _token[i];
            return difference == 0;
        }

        internal static JObject Error(string code, string message) => new JObject
        { ["ok"] = false, ["error"] = new JObject { ["code"] = code, ["message"] = message } };

        public void Dispose()
        {
            lock (_gate)
            {
                if (_stopped) return;
                _stopped = true;
                _listener.Stop();
                _client?.Dispose();
            }
            try { File.Delete(DiscoveryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
