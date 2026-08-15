// =============================================================================
// MessageRetryManager
//
// Remembers everything needed to answer, or give up on, a failed decryption.
//
// Retries are the part of the protocol Unison implements most loosely today:
// counters live in two dictionaries with different key shapes, the sent-message
// cache is a hand-rolled list with its own TTL sweep, and the decision to reset
// a Signal session is taken inline from a magic error code. All of that is one
// concern, so it becomes one class - with the piece the current code is missing
// entirely: WhatsApp tells us *why* decryption failed, and a MAC error means the
// session is out of sync and must be rebuilt rather than retried.
//
// Ports: rc14 src/Utils/message-retry-manager.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Utils;

namespace Unison.Socket.Messages
{
    /// <summary>
    /// Why the other side could not read our message. These are WhatsApp Web's Signal error
    /// codes, sent in the "error" attribute of a retry receipt.
    /// </summary>
    public enum RetryReason
    {
        UnknownError = 0,
        SignalErrorNoSession = 1,
        SignalErrorInvalidKey = 2,
        SignalErrorInvalidKeyId = 3,

        /// <summary>MAC verification failed - the most common cause of decryption failures.</summary>
        SignalErrorInvalidMessage = 4,

        SignalErrorInvalidSignature = 5,
        SignalErrorFutureMessage = 6,

        /// <summary>Explicit MAC failure: the session is definitely out of sync.</summary>
        SignalErrorBadMac = 7,

        SignalErrorInvalidSession = 8,
        SignalErrorInvalidMsgKey = 9,
        BadBroadcastEphemeralSetting = 10,
        UnknownCompanionNoPrekey = 11,
        AdvFailure = 12,
        StatusRevokeDelay = 13
    }

    /// <summary>A message we sent recently, kept so we can re-encrypt it if the peer asks.</summary>
    public sealed class RecentMessage
    {
        public global::Proto.Message Message { get; set; }

        public DateTime StoredAtUtc { get; set; }
    }

    public sealed class SessionRecreateDecision
    {
        public bool Recreate { get; set; }

        public string Reason { get; set; }
    }

    public sealed class RetryStatistics
    {
        public int TotalRetries { get; set; }

        public int SuccessfulRetries { get; set; }

        public int FailedRetries { get; set; }

        public int SessionRecreations { get; set; }

        public int PhoneRequests { get; set; }
    }

    public sealed class MessageRetryManager
    {
        private const int RecentMessagesSize = 512;
        private const char MessageKeySeparator = '\0';

        // A peer that could not read us is usually a peer that was offline, and it complains when
        // it comes back rather than at once. Five minutes covered almost none of those; the cache
        // is capped at 512 entries either way, so the cost of the longer window is bounded.
        private static readonly TimeSpan RecentMessageLifetime = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan RecreateSessionTimeout = TimeSpan.FromHours(1);
        private static readonly TimeSpan RetryCounterLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan BaseKeyLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan PhoneRequestDelay = TimeSpan.FromSeconds(3);

        private static readonly HashSet<RetryReason> MacErrorCodes = new HashSet<RetryReason>
        {
            RetryReason.SignalErrorInvalidMessage,
            RetryReason.SignalErrorBadMac
        };

        private readonly TtlCache<RecentMessage> _recentMessages =
            new TtlCache<RecentMessage>(RecentMessageLifetime, RecentMessagesSize);

        private readonly Dictionary<string, string> _messageKeyIndex =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private readonly TtlCache<DateTime> _sessionRecreateHistory =
            new TtlCache<DateTime>(TimeSpan.FromTicks(RecreateSessionTimeout.Ticks * 2));

        private readonly TtlCache<int> _retryCounters = new TtlCache<int>(RetryCounterLifetime);

        private readonly TtlCache<byte[]> _baseKeys = new TtlCache<byte[]>(BaseKeyLifetime, 1024);

        private readonly Dictionary<string, CancellationTokenSource> _pendingPhoneRequests =
            new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);

        private readonly object _gate = new object();
        private readonly ISocketLog _log;
        private readonly int _maxRetryCount;

        public MessageRetryManager(int maxRetryCount = 5, ISocketLog log = null)
        {
            _maxRetryCount = maxRetryCount > 0 ? maxRetryCount : 5;
            _log = log ?? NullSocketLog.Instance;
        }

        public RetryStatistics Statistics { get; } = new RetryStatistics();

        public int MaxRetryCount
        {
            get { return _maxRetryCount; }
        }

        /// <summary>Keeps a copy of an outgoing message so a retry receipt can be answered.</summary>
        public void AddRecentMessage(string to, string id, global::Proto.Message message)
        {
            if (string.IsNullOrEmpty(to) || string.IsNullOrEmpty(id) || message == null)
            {
                return;
            }

            var key = BuildKey(to, id);
            _recentMessages.Set(key, new RecentMessage { Message = message, StoredAtUtc = DateTime.UtcNow });

            lock (_gate)
            {
                _messageKeyIndex[id] = key;
            }
        }

        /// <summary>
        /// The message we sent under this id, if it is still remembered.
        ///
        /// The address is a hint rather than part of the identity. A retry receipt names the
        /// device that complained, and that device may live in the other address space from the
        /// one we addressed - the clearest case being a message to our own number, where we sent
        /// to the phone number and our other device answers as a LID. The id is already unique
        /// per message, so a miss on the address falls back to it instead of losing the message.
        /// </summary>
        public RecentMessage GetRecentMessage(string to, string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return null;
            }

            if (!string.IsNullOrEmpty(to))
            {
                var direct = _recentMessages.Get(BuildKey(to, id));
                if (direct != null)
                {
                    return direct;
                }
            }

            string key;
            lock (_gate)
            {
                if (!_messageKeyIndex.TryGetValue(id, out key))
                {
                    return null;
                }
            }

            return _recentMessages.Get(key);
        }

        /// <param name="participant">
        /// Who is being counted. A message sent to a group is one message but many conversations,
        /// and each member that cannot read it complains on its own behalf. Counting them
        /// together means the first few members to ask exhaust the budget and everyone behind
        /// them is refused a resend they never received - a group where some members see the
        /// message and others never will. Null counts the message as a whole, which is what the
        /// receive side wants: there we are the only one asking.
        /// </param>
        public int IncrementRetryCount(string messageId, string participant = null)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return 0;
            }

            var next = GetRetryCount(messageId, participant) + 1;
            _retryCounters.Set(BuildCounterKey(messageId, participant), next);
            Statistics.TotalRetries++;
            return next;
        }

        public int GetRetryCount(string messageId, string participant = null)
        {
            int count;
            return !string.IsNullOrEmpty(messageId) &&
                   _retryCounters.TryGet(BuildCounterKey(messageId, participant), out count)
                ? count
                : 0;
        }

        public bool HasExceededMaxRetries(string messageId, string participant = null)
        {
            return GetRetryCount(messageId, participant) >= _maxRetryCount;
        }

        public void MarkRetrySuccess(string messageId)
        {
            Statistics.SuccessfulRetries++;
            Forget(messageId, null);
        }

        /// <summary>
        /// Gives up on a retry. When a participant is named only their counter is dropped and the
        /// message stays in the cache, because the other members of the group may still be waiting
        /// on it.
        /// </summary>
        public void MarkRetryFailed(string messageId, string participant = null)
        {
            Statistics.FailedRetries++;

            if (participant != null)
            {
                _retryCounters.Remove(BuildCounterKey(messageId, participant));
                return;
            }

            Forget(messageId, null);
        }

        /// <summary>
        /// Decides whether the Signal session with <paramref name="jid"/> should be thrown away
        /// and rebuilt. No session at all, or a MAC error, means yes immediately; otherwise we
        /// rebuild at most once an hour so a broken peer cannot make us churn sessions.
        /// </summary>
        public SessionRecreateDecision ShouldRecreateSession(string jid, bool hasSession, RetryReason? errorCode = null)
        {
            if (!hasSession)
            {
                RecordRecreation(jid);
                return new SessionRecreateDecision
                {
                    Recreate = true,
                    Reason = "we don't have a Signal session with them"
                };
            }

            if (IsMacError(errorCode))
            {
                RecordRecreation(jid);
                _log.Warn("[Retry] MAC error from " + jid + ", forcing immediate session recreation");
                return new SessionRecreateDecision
                {
                    Recreate = true,
                    Reason = "MAC error (code " + (int)errorCode.Value + "), immediate session recreation"
                };
            }

            DateTime previous;
            if (!_sessionRecreateHistory.TryGet(jid ?? string.Empty, out previous) ||
                DateTime.UtcNow - previous > RecreateSessionTimeout)
            {
                RecordRecreation(jid);
                return new SessionRecreateDecision
                {
                    Recreate = true,
                    Reason = "retry count > 1 and over an hour since last recreation"
                };
            }

            return new SessionRecreateDecision { Recreate = false, Reason = string.Empty };
        }

        /// <summary>Reads the error attribute of a retry node. Null when the peer did not say.</summary>
        public RetryReason? ParseRetryErrorCode(string errorAttribute)
        {
            if (string.IsNullOrEmpty(errorAttribute))
            {
                return null;
            }

            int code;
            if (!int.TryParse(errorAttribute, out code))
            {
                return null;
            }

            if (code >= (int)RetryReason.UnknownError && code <= (int)RetryReason.StatusRevokeDelay)
            {
                return (RetryReason)code;
            }

            return RetryReason.UnknownError;
        }

        public bool IsMacError(RetryReason? errorCode)
        {
            return errorCode.HasValue && MacErrorCodes.Contains(errorCode.Value);
        }

        /// <summary>
        /// Asks the phone for the message after a short delay, unless the retry succeeds first.
        /// The delay is what stops a burst of failures turning into a burst of phone requests.
        /// </summary>
        public void SchedulePhoneRequest(string messageId, Func<Task> request, TimeSpan? delay = null)
        {
            if (string.IsNullOrEmpty(messageId) || request == null)
            {
                return;
            }

            CancelPendingPhoneRequest(messageId);

            var cts = new CancellationTokenSource();
            lock (_gate)
            {
                _pendingPhoneRequests[messageId] = cts;
            }

            var wait = delay ?? PhoneRequestDelay;
            var _ = RunPhoneRequestAsync(messageId, request, wait, cts.Token);
        }

        public void CancelPendingPhoneRequest(string messageId)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            CancellationTokenSource cts;
            lock (_gate)
            {
                if (!_pendingPhoneRequests.TryGetValue(messageId, out cts))
                {
                    return;
                }

                _pendingPhoneRequests.Remove(messageId);
            }

            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// Records the base key of a retried message. A second retry carrying the same base key
        /// means the peer is stuck on a session it cannot open, so the session must be dropped.
        /// </summary>
        public void SaveBaseKey(string address, string messageId, byte[] baseKey)
        {
            if (baseKey != null)
            {
                _baseKeys.Set(address + ":" + messageId, baseKey);
            }
        }

        public bool HasSameBaseKey(string address, string messageId, byte[] baseKey)
        {
            byte[] stored;
            if (baseKey == null || !_baseKeys.TryGet(address + ":" + messageId, out stored) || stored == null)
            {
                return false;
            }

            if (stored.Length != baseKey.Length)
            {
                return false;
            }

            for (var i = 0; i < stored.Length; i++)
            {
                if (stored[i] != baseKey[i])
                {
                    return false;
                }
            }

            return true;
        }

        public void DeleteBaseKey(string address, string messageId)
        {
            _baseKeys.Remove(address + ":" + messageId);
        }

        public void Clear()
        {
            _recentMessages.Clear();
            _sessionRecreateHistory.Clear();
            _retryCounters.Clear();
            _baseKeys.Clear();

            List<string> pending;
            lock (_gate)
            {
                _messageKeyIndex.Clear();
                pending = new List<string>(_pendingPhoneRequests.Keys);
            }

            foreach (var messageId in pending)
            {
                CancelPendingPhoneRequest(messageId);
            }
        }

        private async Task RunPhoneRequestAsync(
            string messageId,
            Func<Task> request,
            TimeSpan delay,
            CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            lock (_gate)
            {
                _pendingPhoneRequests.Remove(messageId);
            }

            Statistics.PhoneRequests++;

            try
            {
                await request().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[Retry] Phone request failed for " + messageId, ex);
            }
        }

        private void RecordRecreation(string jid)
        {
            _sessionRecreateHistory.Set(jid ?? string.Empty, DateTime.UtcNow);
            Statistics.SessionRecreations++;
        }

        private static string BuildCounterKey(string messageId, string participant)
        {
            return string.IsNullOrEmpty(participant) ? messageId : messageId + ":" + participant;
        }

        private void Forget(string messageId, string participant)
        {
            if (string.IsNullOrEmpty(messageId))
            {
                return;
            }

            _retryCounters.Remove(BuildCounterKey(messageId, participant));
            CancelPendingPhoneRequest(messageId);

            string key;
            lock (_gate)
            {
                if (!_messageKeyIndex.TryGetValue(messageId, out key))
                {
                    return;
                }

                _messageKeyIndex.Remove(messageId);
            }

            _recentMessages.Remove(key);
        }

        private static string BuildKey(string to, string id)
        {
            return to + MessageKeySeparator + id;
        }
    }
}
