// =============================================================================
// MissingMessageLedger
//
// The repairs still owed for messages that arrived as a placeholder. One lock
// covers the pending rows, the resend stanza they are waiting on, and the
// per-chat cooldown, because those three are updated together.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;

namespace Unison.Core.State
{
    public sealed class MissingMessageCandidate
    {
        public string ChatJid { get; set; }
        public string Participant { get; set; }
        public string MessageId { get; set; }
        public bool IsFromMe { get; set; }
        public DateTime MessageTimestamp { get; set; }
        public string Reason { get; set; }
        public DateTime FirstSeenUtc { get; set; }
        public DateTime LastSeenUtc { get; set; }
        public DateTime LastPlaceholderRequestUtc { get; set; }
        public int PlaceholderRequestCount { get; set; }
        public string LastPlaceholderRequestId { get; set; }
        public DateTime PlaceholderScheduledForUtc { get; set; }
        public bool PlaceholderRequestInFlight { get; set; }
        public CancellationTokenSource PlaceholderScheduleCts { get; set; }
    }

    public sealed class PlaceholderResendRequestState
    {
        public string ChatJid { get; set; }
        public string MessageId { get; set; }
        public DateTime RequestedAtUtc { get; set; }
        public string Trigger { get; set; }
        public bool AckAccepted { get; set; }
        public DateTime AckAcceptedUtc { get; set; }
    }

    public struct MissingMessageRemoval
    {
        public CancellationTokenSource ScheduledResend { get; set; }
        public string RequestId { get; set; }
    }

    public sealed class MissingMessageLedger
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, Dictionary<string, MissingMessageCandidate>> _pending =
            new Dictionary<string, Dictionary<string, MissingMessageCandidate>>();
        private readonly Dictionary<string, PlaceholderResendRequestState> _resends =
            new Dictionary<string, PlaceholderResendRequestState>();
        private readonly Dictionary<string, DateTime> _cooldowns =
            new Dictionary<string, DateTime>();

        /// <summary>The same object the pump locks when it still edits a row in place.</summary>
        public object Gate => _gate;

        public Dictionary<string, Dictionary<string, MissingMessageCandidate>> Pending => _pending;

        public Dictionary<string, PlaceholderResendRequestState> Resends => _resends;

        public Dictionary<string, DateTime> Cooldowns => _cooldowns;

        public void Note(
            string chatJid,
            string messageId,
            string participant,
            bool isFromMe,
            DateTime messageTimestamp,
            string reason,
            DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            lock (_gate)
            {
                Dictionary<string, MissingMessageCandidate> byMessageId;
                if (!_pending.TryGetValue(chatJid, out byMessageId))
                {
                    byMessageId = new Dictionary<string, MissingMessageCandidate>(StringComparer.Ordinal);
                    _pending[chatJid] = byMessageId;
                }

                MissingMessageCandidate candidate;
                if (!byMessageId.TryGetValue(messageId, out candidate))
                {
                    candidate = new MissingMessageCandidate
                    {
                        ChatJid = chatJid,
                        MessageId = messageId,
                        FirstSeenUtc = nowUtc
                    };
                    byMessageId[messageId] = candidate;
                }

                candidate.Participant = participant;
                candidate.IsFromMe = isFromMe;
                candidate.MessageTimestamp = messageTimestamp;
                candidate.Reason = reason;
                candidate.LastSeenUtc = nowUtc;
            }
        }

        public bool TryGet(string chatJid, string messageId, out MissingMessageCandidate candidate)
        {
            candidate = null;
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return false;
            }

            lock (_gate)
            {
                Dictionary<string, MissingMessageCandidate> byMessageId;
                return _pending.TryGetValue(chatJid, out byMessageId) &&
                       byMessageId.TryGetValue(messageId, out candidate);
            }
        }

        public MissingMessageRemoval Resolve(string chatJid, string messageId)
        {
            var removal = new MissingMessageRemoval();
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return removal;
            }

            lock (_gate)
            {
                Dictionary<string, MissingMessageCandidate> byMessageId;
                if (_pending.TryGetValue(chatJid, out byMessageId))
                {
                    MissingMessageCandidate candidate;
                    if (byMessageId.TryGetValue(messageId, out candidate))
                    {
                        removal.ScheduledResend = candidate.PlaceholderScheduleCts;
                        removal.RequestId = candidate.LastPlaceholderRequestId;
                    }

                    byMessageId.Remove(messageId);
                    if (byMessageId.Count == 0)
                    {
                        _pending.Remove(chatJid);
                    }
                }

                if (!string.IsNullOrWhiteSpace(removal.RequestId))
                {
                    _resends.Remove(removal.RequestId);
                }
            }

            return removal;
        }

        public IReadOnlyList<MissingMessageCandidate> ForgetChat(string chatJid)
        {
            if (string.IsNullOrWhiteSpace(chatJid))
            {
                return new MissingMessageCandidate[0];
            }

            lock (_gate)
            {
                Dictionary<string, MissingMessageCandidate> byMessageId;
                if (!_pending.TryGetValue(chatJid, out byMessageId))
                {
                    return new MissingMessageCandidate[0];
                }

                var dropped = new List<MissingMessageCandidate>(byMessageId.Values);
                _pending.Remove(chatJid);
                foreach (MissingMessageCandidate candidate in dropped)
                {
                    if (candidate != null && !string.IsNullOrWhiteSpace(candidate.LastPlaceholderRequestId))
                    {
                        _resends.Remove(candidate.LastPlaceholderRequestId);
                    }
                }

                return dropped;
            }
        }
    }
}
