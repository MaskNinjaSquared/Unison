// =============================================================================
// HistoryOnDemandRegistry
//
// What history has been asked of the phone and is still owed: per-chat pages
// (HISTORY_SYNC_ON_DEMAND, anchored on the oldest message we hold) and the one
// account-wide catch-up (FULL_HISTORY_SYNC_ON_DEMAND).
//
// The two kinds are forgotten differently, and that is the part worth stating.
// A chat request is keyed by the chat and by the anchor it was sent from; the
// anchor outlives the request so the same page is not asked for twice. The full
// request is claimed once per connection; the claim outlives the request until
// something says the catch-up is over, so a quiet socket does not re-ask.
//
// Every transition happens under one lock. Sending, timers and logging stay with
// the caller; this type only answers what the bookkeeping says now.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.State
{
    public enum HistoryOnDemandKind
    {
        Chat,
        FullHistory
    }

    public enum HistoryOnDemandChatGate
    {
        Started,
        AlreadyInFlight,
        BackedOff,
        SameMarker
    }

    public enum HistoryOnDemandFullClear
    {
        Nothing,
        Tracked,
        Stale
    }

    /// <summary>
    /// One request sent and not yet answered. The registry mutates it under its lock;
    /// callers holding a reference read the fields set at registration, or ask the
    /// registry for a copy.
    /// </summary>
    public sealed class HistoryOnDemandRequest
    {
        public string RequestId { get; internal set; }
        public HistoryOnDemandKind Kind { get; internal set; }
        public string ChatJid { get; internal set; }
        public string Marker { get; internal set; }
        public DateTime RequestedAtUtc { get; internal set; }
        public int BaselineMessageCount { get; internal set; }
        public string TriggerReason { get; internal set; }
        public bool AckAccepted { get; internal set; }
        public DateTime AckAcceptedUtc { get; internal set; }

        /// <summary>Last tip/chunk/offline signal while waiting for the full catch-up.</summary>
        public DateTime LastProgressUtc { get; internal set; }

        public string LastProgressReason { get; internal set; }
        public int ProgressSignalCount { get; internal set; }

        internal bool TimeoutArmed { get; set; }

        /// <summary>The protocol name, as diagnostics have always printed it.</summary>
        public string TypeName => Kind == HistoryOnDemandKind.Chat
            ? "HistorySyncOnDemand"
            : "FullHistorySyncOnDemand";

        internal HistoryOnDemandRequest Copy()
        {
            return (HistoryOnDemandRequest)MemberwiseClone();
        }
    }

    public sealed class HistoryOnDemandRegistry
    {
        private static readonly TimeSpan RejectedBackoff = TimeSpan.FromMinutes(3);
        private static readonly TimeSpan RateLimitedBackoff = TimeSpan.FromMinutes(15);
        private const string RateLimitedError = "479";

        private readonly object _gate = new object();
        private readonly Dictionary<string, HistoryOnDemandRequest> _byId = new Dictionary<string, HistoryOnDemandRequest>();
        private readonly HashSet<string> _chatsInFlight = new HashSet<string>();
        private readonly Dictionary<string, string> _markerByChat = new Dictionary<string, string>();
        private readonly Dictionary<string, string> _lastRequestIdByChat = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> _rejectedUntilUtcByChat = new Dictionary<string, DateTime>();
        private bool _fullClaimed;
        private string _fullRequestId;
        private string _fullRepairRequestId;

        // --- Per-chat ----------------------------------------------------------

        /// <summary>
        /// Takes the chat's in-flight slot when nothing blocks it: no request already out,
        /// no server backoff, and not the anchor last asked about.
        /// </summary>
        public HistoryOnDemandChatGate TryBeginChat(string chatJid, string marker, DateTime nowUtc, out DateTime backoffUntilUtc)
        {
            backoffUntilUtc = DateTime.MinValue;
            lock (_gate)
            {
                if (_chatsInFlight.Contains(chatJid))
                {
                    return HistoryOnDemandChatGate.AlreadyInFlight;
                }

                if (_rejectedUntilUtcByChat.TryGetValue(chatJid, out DateTime until) && until > nowUtc)
                {
                    backoffUntilUtc = until;
                    return HistoryOnDemandChatGate.BackedOff;
                }

                if (_markerByChat.TryGetValue(chatJid, out string previous) &&
                    string.Equals(previous, marker, StringComparison.Ordinal))
                {
                    return HistoryOnDemandChatGate.SameMarker;
                }

                _chatsInFlight.Add(chatJid);
                return HistoryOnDemandChatGate.Started;
            }
        }

        public HistoryOnDemandRequest RegisterChat(
            string chatJid,
            string requestId,
            string marker,
            int baselineMessageCount,
            string triggerReason,
            DateTime nowUtc)
        {
            var request = new HistoryOnDemandRequest
            {
                RequestId = requestId,
                Kind = HistoryOnDemandKind.Chat,
                ChatJid = chatJid,
                Marker = marker,
                RequestedAtUtc = nowUtc,
                BaselineMessageCount = baselineMessageCount,
                TriggerReason = triggerReason
            };

            lock (_gate)
            {
                _markerByChat[chatJid] = marker;
                _byId[requestId] = request;
                _lastRequestIdByChat[chatJid] = requestId;
            }

            return request;
        }

        /// <summary>
        /// The send failed. Frees the chat; the anchor stays, so the same page is not
        /// retried until the next connect clears it.
        /// </summary>
        /// <param name="requestId">Null when the failure came before an id was registered.</param>
        public void FailChat(string chatJid, string requestId)
        {
            lock (_gate)
            {
                _chatsInFlight.Remove(chatJid);
                if (string.IsNullOrWhiteSpace(requestId))
                {
                    return;
                }

                _byId.Remove(requestId);
                if (_lastRequestIdByChat.TryGetValue(chatJid, out string last) &&
                    string.Equals(last, requestId, StringComparison.Ordinal))
                {
                    _lastRequestIdByChat.Remove(chatJid);
                }
            }
        }

        public bool IsChatPending(string chatJid)
        {
            lock (_gate)
            {
                return _chatsInFlight.Contains(chatJid) || _lastRequestIdByChat.ContainsKey(chatJid);
            }
        }

        public int ChatsInFlight
        {
            get
            {
                lock (_gate)
                {
                    return _chatsInFlight.Count;
                }
            }
        }

        /// <summary>
        /// History for these chats landed. Their requests and backoff go; the anchor stays.
        /// </summary>
        public void CompleteChats(IEnumerable<string> chatJids)
        {
            if (chatJids == null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (string chatJid in chatJids)
                {
                    if (string.IsNullOrWhiteSpace(chatJid))
                    {
                        continue;
                    }

                    _chatsInFlight.Remove(chatJid);
                    if (_lastRequestIdByChat.TryGetValue(chatJid, out string requestId))
                    {
                        _lastRequestIdByChat.Remove(chatJid);
                        _byId.Remove(requestId);
                    }

                    _rejectedUntilUtcByChat.Remove(chatJid);
                }
            }
        }

        /// <summary>
        /// The chat was deleted. Its anchor and backoff go; a request already out is left
        /// to be answered or time out.
        /// </summary>
        public void ForgetChat(string chatJid)
        {
            lock (_gate)
            {
                _markerByChat.Remove(chatJid);
                _lastRequestIdByChat.Remove(chatJid);
                _rejectedUntilUtcByChat.Remove(chatJid);
            }
        }

        // --- Full history ------------------------------------------------------

        /// <summary>Claimed this connection, whether or not a request is tracked behind it.</summary>
        public bool IsFullClaimed
        {
            get { lock (_gate) { return _fullClaimed; } }
        }

        public string FullRequestId
        {
            get { lock (_gate) { return _fullRequestId; } }
        }

        /// <summary>Claimed and a request id is out — what holds enrichment back.</summary>
        public bool IsFullInFlight
        {
            get { lock (_gate) { return _fullClaimed && !string.IsNullOrWhiteSpace(_fullRequestId); } }
        }

        public bool TryClaimFull()
        {
            lock (_gate)
            {
                if (_fullClaimed)
                {
                    return false;
                }

                _fullClaimed = true;
                return true;
            }
        }

        /// <param name="isRepair">A catch-up rather than the first bootstrap; tracked under both ids.</param>
        public HistoryOnDemandRequest RegisterFull(
            string stanzaId,
            bool isRepair,
            int baselineMessageCount,
            string reason,
            DateTime nowUtc)
        {
            var request = new HistoryOnDemandRequest
            {
                RequestId = stanzaId,
                Kind = HistoryOnDemandKind.FullHistory,
                RequestedAtUtc = nowUtc,
                BaselineMessageCount = baselineMessageCount,
                Marker = reason ?? string.Empty,
                TriggerReason = reason,
                LastProgressUtc = nowUtc,
                LastProgressReason = "request-sent"
            };

            lock (_gate)
            {
                _fullRequestId = stanzaId;
                if (isRepair)
                {
                    _fullRepairRequestId = stanzaId;
                }

                _byId[stanzaId] = request;
            }

            return request;
        }

        public void FailFull(bool isRepair)
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_fullRequestId))
                {
                    _byId.Remove(_fullRequestId);
                }

                _fullClaimed = false;
                _fullRequestId = null;
                if (isRepair)
                {
                    _fullRepairRequestId = null;
                }
            }
        }

        /// <summary>Drops the claim and both ids, leaving any tracked request in place.</summary>
        public void ReleaseFull()
        {
            lock (_gate)
            {
                ReleaseFullLocked();
            }
        }

        /// <summary>Drops the claim, both ids and whatever is tracked under them.</summary>
        public void ForceReleaseFull()
        {
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_fullRequestId))
                {
                    _byId.Remove(_fullRequestId);
                }

                if (!string.IsNullOrWhiteSpace(_fullRepairRequestId))
                {
                    _byId.Remove(_fullRepairRequestId);
                }

                ReleaseFullLocked();
            }
        }

        /// <summary>A claim with no id behind it is released so the next page can be asked.</summary>
        public void ReleaseFullIfUntracked()
        {
            lock (_gate)
            {
                if (_fullClaimed && string.IsNullOrWhiteSpace(_fullRequestId))
                {
                    _fullClaimed = false;
                }
            }
        }

        /// <summary>The catch-up is over: clears whatever full-history bookkeeping is left.</summary>
        public HistoryOnDemandFullClear ClearFull(out string requestId, out string repairRequestId)
        {
            lock (_gate)
            {
                requestId = _fullRequestId;
                repairRequestId = _fullRepairRequestId;

                if (!string.IsNullOrWhiteSpace(requestId) &&
                    _byId.TryGetValue(requestId, out HistoryOnDemandRequest tracked))
                {
                    ClearLocked(tracked);
                    return HistoryOnDemandFullClear.Tracked;
                }

                if (!_fullClaimed &&
                    string.IsNullOrWhiteSpace(_fullRequestId) &&
                    string.IsNullOrWhiteSpace(_fullRepairRequestId))
                {
                    return HistoryOnDemandFullClear.Nothing;
                }

                if (!string.IsNullOrWhiteSpace(_fullRequestId))
                {
                    _byId.Remove(_fullRequestId);
                }

                if (!string.IsNullOrWhiteSpace(_fullRepairRequestId))
                {
                    _byId.Remove(_fullRepairRequestId);
                }

                ReleaseFullLocked();
                return HistoryOnDemandFullClear.Stale;
            }
        }

        /// <summary>
        /// Stamps progress on the pending full request so the idle watchdog does not abort
        /// while lots are still arriving. Only lot progress counts as a signal.
        /// </summary>
        public bool NoteFullProgress(string reason, bool countsAsLot, DateTime nowUtc, out string requestId, out int signalCount)
        {
            lock (_gate)
            {
                HistoryOnDemandRequest pending = FindPendingFullLocked(out requestId);
                if (pending == null)
                {
                    signalCount = 0;
                    return false;
                }

                pending.LastProgressUtc = nowUtc;
                pending.LastProgressReason = reason ?? string.Empty;
                if (countsAsLot)
                {
                    pending.ProgressSignalCount++;
                }

                signalCount = pending.ProgressSignalCount;
                return true;
            }
        }

        /// <summary>
        /// A quiet SQLite lot during catch-up goes to the continue decision instead of
        /// ending it. Hands back a copy of the pending request, which is cleared.
        /// </summary>
        public bool TryHandOffQuietLot(bool catchUpActive, out string requestId, out HistoryOnDemandRequest pending)
        {
            lock (_gate)
            {
                HistoryOnDemandRequest live = FindPendingFullLocked(out requestId);
                if (!catchUpActive && live == null)
                {
                    pending = null;
                    return false;
                }

                if (live != null)
                {
                    pending = live.Copy();
                    ClearLocked(live);
                }
                else
                {
                    pending = null;
                    ReleaseFullLocked();
                }

                return true;
            }
        }

        /// <summary>
        /// Why avatar fetches should wait for history, or null. A full id with nothing
        /// tracked behind it is stale and is cleared here rather than deferring forever.
        /// </summary>
        /// <param name="staleCleared">What was cleared, for the log; null when nothing was.</param>
        public string ProbeAvatarDeferral(out string staleCleared)
        {
            staleCleared = null;
            lock (_gate)
            {
                if (!string.IsNullOrWhiteSpace(_fullRequestId))
                {
                    if (_byId.ContainsKey(_fullRequestId))
                    {
                        return "full-history-on-demand-in-flight:" + _fullRequestId;
                    }

                    staleCleared = "requestId=" + _fullRequestId;
                    ReleaseFullLocked();
                }
                else if (_fullClaimed)
                {
                    staleCleared = "requested-flag";
                    _fullClaimed = false;
                    _fullRepairRequestId = null;
                }

                return _chatsInFlight.Count > 0
                    ? "history-on-demand-in-flight:" + _chatsInFlight.Count
                    : null;
            }
        }

        public void ReadFullDiagnostics(out bool pending, out string requestId, out HistoryOnDemandRequest tracked)
        {
            lock (_gate)
            {
                requestId = _fullRequestId;
                pending = _fullClaimed || !string.IsNullOrWhiteSpace(requestId);
                tracked = !string.IsNullOrWhiteSpace(requestId) &&
                          _byId.TryGetValue(requestId, out HistoryOnDemandRequest live) &&
                          live != null
                    ? live.Copy()
                    : null;
            }
        }

        // --- Shared ------------------------------------------------------------

        public HistoryOnDemandRequest Find(string requestId)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return null;
            }

            lock (_gate)
            {
                return _byId.TryGetValue(requestId, out HistoryOnDemandRequest request) ? request : null;
            }
        }

        /// <summary>
        /// The answer to a request stanza. A rejection clears the request and, for a chat,
        /// backs it off (longer on 479); acceptance stamps it and keeps it pending.
        /// </summary>
        /// <returns>The request, or null when the id is not ours.</returns>
        public HistoryOnDemandRequest Ack(string requestId, string error, DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(requestId))
            {
                return null;
            }

            lock (_gate)
            {
                if (!_byId.TryGetValue(requestId, out HistoryOnDemandRequest request))
                {
                    return null;
                }

                if (string.IsNullOrWhiteSpace(error))
                {
                    request.AckAccepted = true;
                    request.AckAcceptedUtc = nowUtc;
                    return request;
                }

                ClearLocked(request);
                if (request.Kind == HistoryOnDemandKind.Chat)
                {
                    TimeSpan backoff = string.Equals(error, RateLimitedError, StringComparison.OrdinalIgnoreCase)
                        ? RateLimitedBackoff
                        : RejectedBackoff;
                    _rejectedUntilUtcByChat[request.ChatJid] = nowUtc.Add(backoff);
                }

                return request;
            }
        }

        /// <summary>True the first time for a request that is still current.</summary>
        public bool TryArmTimeout(HistoryOnDemandRequest request)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(request) || request.TimeoutArmed)
                {
                    return false;
                }

                request.TimeoutArmed = true;
                return true;
            }
        }

        /// <summary>A copy of the request while it is still current, else null.</summary>
        public HistoryOnDemandRequest TrySnapshot(HistoryOnDemandRequest request)
        {
            lock (_gate)
            {
                return IsCurrentLocked(request) ? request.Copy() : null;
            }
        }

        /// <summary>Clears the request if it is still current.</summary>
        public bool TryClear(HistoryOnDemandRequest request)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(request))
                {
                    return false;
                }

                ClearLocked(request);
                return true;
            }
        }

        /// <summary>Copies then clears the request if it is still current, else null.</summary>
        public HistoryOnDemandRequest TryExpire(HistoryOnDemandRequest request)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(request))
                {
                    return null;
                }

                HistoryOnDemandRequest copy = request.Copy();
                ClearLocked(request);
                return copy;
            }
        }

        /// <summary>Forgets every tracked request and chat anchor; backoff and the full claim stay.</summary>
        public void ClearRequests()
        {
            lock (_gate)
            {
                _markerByChat.Clear();
                _chatsInFlight.Clear();
                _byId.Clear();
                _lastRequestIdByChat.Clear();
            }
        }

        public void ClearBackoff()
        {
            lock (_gate)
            {
                _rejectedUntilUtcByChat.Clear();
            }
        }

        private bool IsCurrentLocked(HistoryOnDemandRequest request)
        {
            return request != null &&
                   !string.IsNullOrWhiteSpace(request.RequestId) &&
                   _byId.TryGetValue(request.RequestId, out HistoryOnDemandRequest current) &&
                   ReferenceEquals(current, request);
        }

        /// <summary>The full id, else the repair id, provided a full request is tracked under it.</summary>
        private HistoryOnDemandRequest FindPendingFullLocked(out string requestId)
        {
            requestId = _fullRequestId;
            HistoryOnDemandRequest request = null;
            if (!string.IsNullOrWhiteSpace(requestId))
            {
                _byId.TryGetValue(requestId, out request);
            }

            if (request == null && !string.IsNullOrWhiteSpace(_fullRepairRequestId))
            {
                requestId = _fullRepairRequestId;
                _byId.TryGetValue(requestId, out request);
            }

            return request != null && request.Kind == HistoryOnDemandKind.FullHistory ? request : null;
        }

        private void ClearLocked(HistoryOnDemandRequest request)
        {
            _byId.Remove(request.RequestId);

            if (request.Kind == HistoryOnDemandKind.Chat)
            {
                _chatsInFlight.Remove(request.ChatJid);
                if (_lastRequestIdByChat.TryGetValue(request.ChatJid, out string last) &&
                    string.Equals(last, request.RequestId, StringComparison.Ordinal))
                {
                    _lastRequestIdByChat.Remove(request.ChatJid);
                }

                if (_markerByChat.TryGetValue(request.ChatJid, out string marker) &&
                    string.Equals(marker, request.Marker, StringComparison.Ordinal))
                {
                    _markerByChat.Remove(request.ChatJid);
                }

                return;
            }

            if (string.Equals(_fullRequestId, request.RequestId, StringComparison.Ordinal))
            {
                _fullRequestId = null;
            }

            if (string.Equals(_fullRepairRequestId, request.RequestId, StringComparison.Ordinal))
            {
                _fullRepairRequestId = null;
            }

            _fullClaimed = false;
        }

        private void ReleaseFullLocked()
        {
            _fullClaimed = false;
            _fullRequestId = null;
            _fullRepairRequestId = null;
        }
    }
}
