using Newtonsoft.Json;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ParallelSystemsPlugin.Timesheets
{
    internal sealed class LocalOutboxClient : IDisposable
    {
        private readonly TrackerSettings _settings;
        private readonly string _rootFolder;
        private readonly string _outboxFolder;
        private readonly string _diagnosticPath;
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private int _flushRequested;
        private bool _disposed;

        public LocalOutboxClient(TrackerSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _rootFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Parallel Systems",
                "Timesheet");
            _outboxFolder = Path.Combine(_rootFolder, "Outbox");
            _diagnosticPath = Path.Combine(_rootFolder, "tracker.log");

            Directory.CreateDirectory(_outboxFolder);

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch { }

            _httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(90)
            };
        }

        public string InstallationId
        {
            get
            {
                var path = Path.Combine(_rootFolder, "installation.id");
                try
                {
                    if (File.Exists(path))
                    {
                        Guid existing;
                        if (Guid.TryParse(File.ReadAllText(path).Trim(), out existing)) return existing.ToString("D");
                    }

                    var id = Guid.NewGuid().ToString("D");
                    Directory.CreateDirectory(_rootFolder);
                    File.WriteAllText(path, id);
                    return id;
                }
                catch
                {
                    // Stable enough for the current Revit process if LocalAppData is unavailable.
                    return Environment.MachineName + "-" + Environment.UserName;
                }
            }
        }

        public void Queue(TrackerCheckpointRequest checkpoint)
        {
            if (_disposed || checkpoint == null || !_settings.Enabled) return;

            try
            {
                Directory.CreateDirectory(_outboxFolder);
                var fileName = string.Format(
                    "{0:yyyyMMddHHmmssfff}_{1:D}.json",
                    checkpoint.OccurredAtUtc,
                    checkpoint.MessageId);
                var finalPath = Path.Combine(_outboxFolder, fileName);
                var temporaryPath = finalPath + ".tmp";
                var json = JsonConvert.SerializeObject(checkpoint, Formatting.None);

                File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
                if (File.Exists(finalPath)) File.Delete(finalPath);
                File.Move(temporaryPath, finalPath);

                EnforceOutboxLimit();
                RequestFlush();
            }
            catch (Exception ex)
            {
                WriteDiagnostic("Unable to persist tracker checkpoint: " + ex.Message);
            }
        }

        public void RequestFlush()
        {
            if (_disposed || !_settings.Enabled || string.IsNullOrWhiteSpace(_settings.ApiBaseUrl)) return;
            if (Interlocked.Exchange(ref _flushRequested, 1) == 1) return;

            Task.Run(async () =>
            {
                try { await FlushAsync().ConfigureAwait(false); }
                catch (Exception ex) { WriteDiagnostic("Outbox flush failed: " + ex.Message); }
                finally { Interlocked.Exchange(ref _flushRequested, 0); }
            });
        }

        private async Task FlushAsync()
        {
            if (!await _flushGate.WaitAsync(0).ConfigureAwait(false)) return;

            try
            {
                while (!_disposed)
                {
                    var file = Directory.GetFiles(_outboxFolder, "*.json")
                        .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                        .FirstOrDefault();

                    if (file == null) return;

                    string json;
                    try { json = File.ReadAllText(file, Encoding.UTF8); }
                    catch (Exception ex)
                    {
                        MoveToFailed(file, "Unreadable message: " + ex.Message);
                        continue;
                    }

                    var endpoint = _settings.ApiBaseUrl.TrimEnd('/') + "/api/tracker/checkpoints";
                    using (var request = new HttpRequestMessage(HttpMethod.Post, endpoint))
                    {
                        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                        if (!string.IsNullOrWhiteSpace(_settings.TrackerApiKey))
                            request.Headers.TryAddWithoutValidation("X-Tracker-Key", _settings.TrackerApiKey);

                        HttpResponseMessage response;
                        try
                        {
                            response = await _httpClient.SendAsync(request).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            WriteDiagnostic("Server unavailable; checkpoint remains queued. " + ex.Message);
                            return;
                        }

                        using (response)
                        {
                            if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.Conflict)
                            {
                                TryDelete(file);
                                continue;
                            }

                            // Authentication and malformed payloads will not fix themselves by retrying forever.
                            if (response.StatusCode == HttpStatusCode.BadRequest ||
                                response.StatusCode == HttpStatusCode.Unauthorized ||
                                response.StatusCode == HttpStatusCode.Forbidden ||
                                (int)response.StatusCode == 422)
                            {
                                var body = await SafeReadBody(response).ConfigureAwait(false);

                                // A tracker/server schema mismatch is a deployment-order issue,
                                // not a corrupt employee checkpoint. Keep it in Outbox so it can
                                // be delivered after the backend is upgraded instead of moving
                                // legitimate Revit work permanently into Failed.
                                if (response.StatusCode == HttpStatusCode.BadRequest &&
                                    body != null &&
                                    body.IndexOf("schema version", StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    WriteDiagnostic(
                                        "Server rejected the tracker schema; checkpoint remains queued. " +
                                        ((int)response.StatusCode) + " " + body);
                                    return;
                                }

                                MoveToFailed(file, ((int)response.StatusCode) + " " + body);
                                continue;
                            }

                            WriteDiagnostic("Server returned " + (int)response.StatusCode + "; checkpoint remains queued.");
                            return;
                        }
                    }
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }

        private void EnforceOutboxLimit()
        {
            try
            {
                var files = Directory.GetFiles(_outboxFolder, "*.json")
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var excess = files.Length - _settings.MaxPendingMessages;
                if (excess <= 0) return;

                var overflow = Path.Combine(_rootFolder, "Overflow");
                Directory.CreateDirectory(overflow);
                foreach (var file in files.Take(excess))
                {
                    var destination = Path.Combine(overflow, Path.GetFileName(file));
                    if (File.Exists(destination)) File.Delete(destination);
                    File.Move(file, destination);
                }

                WriteDiagnostic(excess + " old checkpoints moved to Overflow because the pending limit was reached.");
            }
            catch { }
        }

        private void MoveToFailed(string file, string reason)
        {
            try
            {
                var folder = Path.Combine(_rootFolder, "Failed");
                Directory.CreateDirectory(folder);
                var destination = Path.Combine(folder, Path.GetFileName(file));
                if (File.Exists(destination)) File.Delete(destination);
                File.Move(file, destination);
                File.WriteAllText(destination + ".error.txt", reason ?? "Unknown error");
                WriteDiagnostic("Checkpoint quarantined: " + reason);
            }
            catch { }
        }

        private static async Task<string> SafeReadBody(HttpResponseMessage response)
        {
            try { return await response.Content.ReadAsStringAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch { }
        }

        private void WriteDiagnostic(string message)
        {
            try
            {
                Directory.CreateDirectory(_rootFolder);
                if (File.Exists(_diagnosticPath) && new FileInfo(_diagnosticPath).Length > 1024 * 1024)
                {
                    var old = _diagnosticPath + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(_diagnosticPath, old);
                }

                File.AppendAllText(
                    _diagnosticPath,
                    DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
            catch { }
        }

        public void Dispose()
        {
            _disposed = true;
            try { _httpClient.Dispose(); } catch { }
            try { _flushGate.Dispose(); } catch { }
        }
    }
}
