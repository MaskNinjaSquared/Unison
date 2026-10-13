// =============================================================================
// MissingMessageLedger
//
// The repairs still owed for messages that arrived as a placeholder. One lock
// covers the pending rows, the resend stanza they are waiting on, and the
// per-chat cooldown, because those three are updated together.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Unison.Core.Helpers;

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

        /// <summary>
        /// Copies the repairs a drain may ask about, peer and self first, and keeps only
        /// <paramref name="maxRequests"/> of them. <paramref name="totalEligible"/> is the full count.
        /// </summary>
        public List<MissingMessageCandidate> SelectForDrain(int maxRequests, out int totalEligible)
        {
            lock (_gate)
            {
                List<MissingMessageCandidate> pending = _pending
                    .Values
                    .SelectMany(byMessageId => byMessageId.Values)
                    .Where(candidate =>
                        candidate != null &&
                        !candidate.PlaceholderRequestInFlight &&
                        candidate.PlaceholderScheduleCts == null &&
                        candidate.PlaceholderRequestCount < 2)
                    .Select(candidate => new MissingMessageCandidate
                    {
                        ChatJid = candidate.ChatJid,
                        Participant = candidate.Participant,
                        MessageId = candidate.MessageId,
                        IsFromMe = candidate.IsFromMe,
                        MessageTimestamp = candidate.MessageTimestamp,
                        Reason = candidate.Reason,
                        FirstSeenUtc = candidate.FirstSeenUtc,
                        LastSeenUtc = candidate.LastSeenUtc,
                        PlaceholderRequestCount = candidate.PlaceholderRequestCount
                    })
                    .OrderBy(candidate => candidate.PlaceholderRequestCount)
                    .ThenByDescending(candidate => MissingMessagePriority.IsPeerOrSelf(candidate.IsFromMe, candidate.ChatJid))
                    .ThenByDescending(candidate => ChatMessageOrder.ToComparableUtc(candidate.MessageTimestamp))
                    .ThenByDescending(candidate => candidate.LastSeenUtc)
                    .ToList();

                totalEligible = pending.Count;
                if (maxRequests < 0)
                {
                    maxRequests = 0;
                }

                return pending.Take(maxRequests).ToList();
            }
        }

        /// <summary>
        /// Arms the timer on this row when a resend is allowed. The caller waits, then claims it.
        /// </summary>
        public CancellationTokenSource TryBeginSchedule(
            MissingMessageCandidate candidate,
            DateTime nowUtc,
            TimeSpan responseTimeout,
            TimeSpan dispatchDelay)
        {
            lock (_gate)
            {
                if (!PlaceholderResendPermit.MaySchedule(candidate, nowUtc, responseTimeout))
                {
                    return null;
                }

                var schedule = new CancellationTokenSource();
                candidate.PlaceholderScheduleCts = schedule;
                candidate.PlaceholderScheduledForUtc = nowUtc.Add(dispatchDelay);
                return schedule;
            }
        }

        /// <summary>
        /// The wait finished and this timer is still the one on the row. Marks the ask in flight.
        /// </summary>
        public bool TryClaimScheduled(
            string chatJid,
            string messageId,
            CancellationTokenSource schedule,
            out MissingMessageCandidate candidate)
        {
            candidate = null;
            lock (_gate)
            {
                if (!TryGet(chatJid, messageId, out candidate) ||
                    candidate.PlaceholderScheduleCts != schedule)
                {
                    candidate = null;
                    return false;
                }

                candidate.PlaceholderScheduleCts = null;
                candidate.PlaceholderScheduledForUtc = DateTime.MinValue;
                candidate.PlaceholderRequestInFlight = true;
                return true;
            }
        }

        /// <summary>
        /// Records that the stanza was handed to the socket. False when the row is already gone.
        /// </summary>
        public bool RecordSend(
            string chatJid,
            string messageId,
            string stanzaId,
            string trigger,
            DateTime nowUtc,
            out MissingMessageCandidate candidate)
        {
            candidate = null;
            lock (_gate)
            {
                if (!TryGet(chatJid, messageId, out candidate))
                {
                    candidate = null;
                    return false;
                }

                candidate.LastPlaceholderRequestUtc = nowUtc;
                candidate.PlaceholderRequestCount++;
                candidate.LastPlaceholderRequestId = stanzaId;
                _resends[stanzaId] = new PlaceholderResendRequestState
                {
                    ChatJid = candidate.ChatJid,
                    MessageId = candidate.MessageId,
                    RequestedAtUtc = nowUtc,
                    Trigger = trigger
                };
                return true;
            }
        }

        /// <summary>
        /// The answer window closed. Drops the stanza and clears in-flight when it still owns the row.
        /// </summary>
        public PlaceholderResendRequestState Expire(string stanzaId)
        {
            lock (_gate)
            {
                PlaceholderResendRequestState timedOut;
                if (!_resends.TryGetValue(stanzaId, out timedOut))
                {
                    return null;
                }

                _resends.Remove(stanzaId);
                MissingMessageCandidate candidate;
                if (TryGet(timedOut.ChatJid, timedOut.MessageId, out candidate) &&
                    string.Equals(candidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                {
                    candidate.PlaceholderRequestInFlight = false;
                }

                return timedOut;
            }
        }

        /// <summary>
        /// The send threw. Drops the stanza, clears in-flight, and gives back the try when this stanza owned it.
        /// </summary>
        public void FailSend(string chatJid, string messageId, string stanzaId)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(stanzaId))
                {
                    _resends.Remove(stanzaId);
                }

                MissingMessageCandidate candidate;
                if (!TryGet(chatJid, messageId, out candidate))
                {
                    return;
                }

                candidate.PlaceholderRequestInFlight = false;
                if (string.Equals(candidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                {
                    candidate.LastPlaceholderRequestId = null;
                    candidate.PlaceholderRequestCount = Math.Max(0, candidate.PlaceholderRequestCount - 1);
                }
            }
        }

        /// <summary>
        /// A peer-data answer arrived for this stanza. The row stops being in flight when the ids match.
        /// </summary>
        public PlaceholderResendRequestState TakePeerResponse(string stanzaId)
        {
            if (string.IsNullOrWhiteSpace(stanzaId))
            {
                return null;
            }

            lock (_gate)
            {
                PlaceholderResendRequestState request;
                _resends.TryGetValue(stanzaId, out request);
                _resends.Remove(stanzaId);
                MissingMessageCandidate candidate;
                if (request != null &&
                    TryGet(request.ChatJid, request.MessageId, out candidate) &&
                    string.Equals(candidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                {
                    candidate.PlaceholderRequestInFlight = false;
                }

                return request;
            }
        }

        /// <summary>
        /// An ack for a stanza we are waiting on. A rejection clears the row; an acceptance only marks the ack.
        /// </summary>
        public bool TryApplyAck(
            string stanzaId,
            bool rejected,
            DateTime nowUtc,
            out PlaceholderResendRequestState request)
        {
            request = null;
            lock (_gate)
            {
                if (!_resends.TryGetValue(stanzaId, out request))
                {
                    request = null;
                    return false;
                }

                if (rejected)
                {
                    _resends.Remove(stanzaId);
                    MissingMessageCandidate candidate;
                    if (TryGet(request.ChatJid, request.MessageId, out candidate) &&
                        string.Equals(candidate.LastPlaceholderRequestId, stanzaId, StringComparison.Ordinal))
                    {
                        candidate.PlaceholderRequestInFlight = false;
                    }
                }
                else
                {
                    request.AckAccepted = true;
                    request.AckAcceptedUtc = nowUtc;
                }

                return true;
            }
        }

        /// <summary>
        /// Starts the per-chat quiet period. False when a previous one is still running.
        /// Equality at the exact end does not skip.
        /// </summary>
        public bool TryBeginReconcile(string chatJid, DateTime nowUtc, TimeSpan cooldown)
        {
            lock (_gate)
            {
                DateTime until;
                if (_cooldowns.TryGetValue(chatJid, out until) && until > nowUtc)
                {
                    return false;
                }

                _cooldowns[chatJid] = nowUtc.Add(cooldown);
                return true;
            }
        }

        /// <summary>The ids owed for one chat, in dictionary order, capped at the budget.</summary>
        public List<string> CopyPendingIds(string chatJid, int maxRequests)
        {
            lock (_gate)
            {
                Dictionary<string, MissingMessageCandidate> byMessageId;
                if (!_pending.TryGetValue(chatJid, out byMessageId))
                {
                    return new List<string>();
                }

                return byMessageId.Keys.Take(maxRequests).ToList();
            }
        }
    }
}
