using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Windows.Storage;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// Dev-only tooling: polls LocalState for a debug-send request file (written externally,
    /// e.g. by a test harness), validates it against a local allowlist, and sends it through
    /// the WhatsApp connection client. Only attached/started in DEBUG builds.
    ///
    /// Extracted from WhatsAppService (was <c>StartDebugSendWatcher</c> and friends) so the
    /// connection/session "client" doesn't carry test-only file-watching state.
    /// </summary>
    public sealed class DebugSendService : IDebugSendService
    {
        private const string DebugSendRequestFileName = "debug-send.json";
        private const string DebugSendAllowlistFileName = "debug-send-allowlist.json";
        private const string DebugSendResultFileName = "debug-send-result.json";

        private readonly IWhatsAppService _whatsAppService;
        private readonly SemaphoreSlim _debugSendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _debugSendCts;
        private string _lastDebugSendRequestId;

        public DebugSendService(IWhatsAppService whatsAppService)
        {
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
        }

        private sealed class DebugSendRequest
        {
            public string RequestId { get; set; }
            public string TargetJid { get; set; }
            public string Text { get; set; }
            public bool? Enabled { get; set; }
        }

        private sealed class DebugSendResult
        {
            public string RequestId { get; set; }
            public string TargetJid { get; set; }
            public string Status { get; set; }
            public string MessageId { get; set; }
            public string Error { get; set; }
            public string TimestampUtc { get; set; }
        }

        public void Start()
        {
            Stop("restart");
            _debugSendCts = new CancellationTokenSource();
            var token = _debugSendCts.Token;
            _ = Task.Run(async () => await WatcherLoopAsync(token));
            Debug.WriteLine($"[DebugSend] Watcher started. Request={DebugSendRequestFileName}, Allowlist={DebugSendAllowlistFileName}");
        }

        public void Stop(string reason)
        {
            var cts = _debugSendCts;
            _debugSendCts = null;
            if (cts == null)
            {
                return;
            }

            try
            {
                cts.Cancel();
                Debug.WriteLine($"[DebugSend] Watcher stopped: {reason}");
            }
            catch
            {
            }
            finally
            {
                cts.Dispose();
            }
        }

        private async Task WatcherLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), token);
                    await TryProcessDebugSendRequestAsync(token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[DebugSend] Watcher error: {ex.Message}");
                }
            }
        }

        private async Task TryProcessDebugSendRequestAsync(CancellationToken token)
        {
            if (!_whatsAppService.IsTransportReady)
            {
                return;
            }

            await _debugSendLock.WaitAsync(token);
            try
            {
                var request = await ReadDebugSendRequestAsync();
                if (request == null)
                {
                    return;
                }

                string requestId = (request.RequestId ?? string.Empty).Trim();
                string targetJid = JidHelper.Normalize(request.TargetJid);
                string text = request.Text ?? string.Empty;

                if (request.Enabled == false)
                {
                    return;
                }

                if (string.IsNullOrWhiteSpace(requestId))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing requestId");
                    return;
                }

                if (string.Equals(_lastDebugSendRequestId, requestId, StringComparison.Ordinal))
                {
                    return;
                }

                if (await IsDebugSendRequestAlreadyProcessedAsync(requestId))
                {
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                if (string.IsNullOrWhiteSpace(targetJid))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing targetJid");
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                if (string.IsNullOrWhiteSpace(text))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Missing text");
                    _lastDebugSendRequestId = requestId;
                    return;
                }

                var allowlist = await ReadDebugSendAllowlistAsync();
                if (!IsDebugSendTargetAllowed(targetJid, allowlist))
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "refused", null, "Target is not in debug-send allowlist");
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Refused request {requestId}: target not allowlisted ({targetJid})");
                    return;
                }

                await WriteDebugSendResultAsync(requestId, targetJid, "sending", null, null);
                Debug.WriteLine($"[DebugSend] Sending request {requestId} to {targetJid}, chars={text.Length}");

                try
                {
                    var sent = await _whatsAppService.SendTextMessageAsync(targetJid, text);
                    string messageId = sent?.Id;
                    await WriteDebugSendResultAsync(requestId, targetJid, "sent", messageId, null);
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Request {requestId} sent as {messageId}");
                }
                catch (Exception ex)
                {
                    await WriteDebugSendResultAsync(requestId, targetJid, "failed", null, ex.Message);
                    _lastDebugSendRequestId = requestId;
                    Debug.WriteLine($"[DebugSend] Request {requestId} failed: {ex}");
                }
            }
            finally
            {
                _debugSendLock.Release();
            }
        }

        private async Task<DebugSendRequest> ReadDebugSendRequestAsync()
        {
            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendRequestFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                return null;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonConvert.DeserializeObject<DebugSendRequest>(json);
        }

        private async Task<bool> IsDebugSendRequestAlreadyProcessedAsync(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return false;
            }

            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendResultFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                return false;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return false;
            }

            var result = JsonConvert.DeserializeObject<DebugSendResult>(json);
            return string.Equals(result?.RequestId, requestId, StringComparison.Ordinal);
        }

        private async Task<HashSet<string>> ReadDebugSendAllowlistAsync()
        {
            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var folder = ApplicationData.Current.LocalFolder;
            var item = await folder.TryGetItemAsync(DebugSendAllowlistFileName);
            var file = item as StorageFile;
            if (file == null)
            {
                Debug.WriteLine($"[DebugSend] No {DebugSendAllowlistFileName}; all debug sends refused.");
                return allowed;
            }

            string json = await FileIO.ReadTextAsync(file);
            if (string.IsNullOrWhiteSpace(json))
            {
                return allowed;
            }

            var token = JToken.Parse(json);
            if (token.Type == JTokenType.String)
            {
                string single = JidHelper.Normalize(token.Value<string>());
                if (!string.IsNullOrWhiteSpace(single))
                {
                    allowed.Add(single);
                }
                return allowed;
            }

            JToken listToken = token.Type == JTokenType.Array
                ? token
                : (token["allowedJids"] ?? token["AllowedJids"]);

            if (listToken == null)
            {
                return allowed;
            }

            foreach (var entry in listToken.Values<string>())
            {
                string normalized = JidHelper.Normalize(entry);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    allowed.Add(normalized);
                }
            }

            return allowed;
        }

        private bool IsDebugSendTargetAllowed(string targetJid, HashSet<string> allowlist)
        {
            if (allowlist == null || allowlist.Count == 0 || string.IsNullOrWhiteSpace(targetJid))
            {
                return false;
            }

            foreach (var candidate in GetDebugSendTargetCandidates(targetJid))
            {
                if (allowlist.Contains(candidate))
                {
                    return true;
                }
            }

            return false;
        }

        private IEnumerable<string> GetDebugSendTargetCandidates(string targetJid)
        {
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string> add = jid =>
            {
                string normalized = JidHelper.Normalize(jid);
                if (!string.IsNullOrWhiteSpace(normalized))
                {
                    candidates.Add(normalized);
                }
            };

            add(targetJid);
            add(_whatsAppService.GetCanonicalJid(targetJid));

            string normalizedTarget = JidHelper.Normalize(targetJid);
            if (!string.IsNullOrWhiteSpace(normalizedTarget) &&
                _whatsAppService.JidAlias.TryGetValue(normalizedTarget, out var alias))
            {
                add(alias);
                add(_whatsAppService.GetCanonicalJid(alias));
            }

            return candidates;
        }

        private async Task WriteDebugSendResultAsync(string requestId, string targetJid, string status, string messageId, string error)
        {
            var result = new DebugSendResult
            {
                RequestId = requestId,
                TargetJid = targetJid,
                Status = status,
                MessageId = messageId,
                Error = error,
                TimestampUtc = DateTime.UtcNow.ToString("O")
            };

            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.CreateFileAsync(DebugSendResultFileName, CreationCollisionOption.ReplaceExisting);
            string json = JsonConvert.SerializeObject(result, Formatting.Indented);
            await FileIO.WriteTextAsync(file, json);
        }
    }
}
