// =============================================================================
// OfflineReplaySummaryBook
//
// The compact per-chat tips gathered while an offline replay is still running.
// Record and the failed-apply rollback share this book, so a tip cannot be
// applied and also left behind under a different set of fields.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class OfflineReplaySummaryBook
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, OfflineReplayChatSummary> _summaries =
            new Dictionary<string, OfflineReplayChatSummary>(StringComparer.OrdinalIgnoreCase);
        private Timer _timer;

        public void Record(
            string canonicalJid,
            string preview,
            DateTime timestamp,
            bool isGroup,
            bool isFromMe,
            ChatPreviewKind kind,
            string status,
            string authorPrefix,
            bool incrementUnread,
            DateTime nowUtc,
            string participantJid,
            string senderName,
            TimeSpan debounce,
            Action onDue)
        {
            if (string.IsNullOrWhiteSpace(canonicalJid))
            {
                return;
            }

            lock (_gate)
            {
                OfflineReplayChatSummary summary;
                if (!_summaries.TryGetValue(canonicalJid, out summary))
                {
                    summary = OfflineReplaySummaryMerge.Create(canonicalJid, isGroup);
                    _summaries[canonicalJid] = summary;
                }

                OfflineReplaySummaryMerge.Record(
                    summary,
                    preview,
                    timestamp,
                    isGroup,
                    isFromMe,
                    kind,
                    status,
                    authorPrefix,
                    incrementUnread,
                    nowUtc,
                    participantJid,
                    senderName);

                if (_timer == null && onDue != null)
                {
                    _timer = new Timer(
                        _ => onDue(),
                        null,
                        (int)debounce.TotalMilliseconds,
                        Timeout.Infinite);
                }
            }
        }

        /// <summary>The tips to apply, or null when there is nothing waiting.</summary>
        public Dictionary<string, OfflineReplayChatSummary> Take()
        {
            lock (_gate)
            {
                if (_summaries.Count == 0)
                {
                    return null;
                }

                var snapshot = new Dictionary<string, OfflineReplayChatSummary>(StringComparer.OrdinalIgnoreCase);
                foreach (KeyValuePair<string, OfflineReplayChatSummary> pair in _summaries)
                {
                    OfflineReplayChatSummary value = pair.Value;
                    snapshot[pair.Key] = new OfflineReplayChatSummary
                    {
                        Jid = value.Jid,
                        Preview = value.Preview,
                        Timestamp = value.Timestamp,
                        IsGroup = value.IsGroup,
                        IsFromMe = value.IsFromMe,
                        UnreadDelta = value.UnreadDelta,
                        Kind = value.Kind,
                        Status = value.Status,
                        AuthorPrefix = value.AuthorPrefix
                    };
                }

                _summaries.Clear();
                if (_timer != null)
                {
                    _timer.Dispose();
                    _timer = null;
                }

                return snapshot;
            }
        }

        public void Restore(Dictionary<string, OfflineReplayChatSummary> snapshot)
        {
            if (snapshot == null)
            {
                return;
            }

            lock (_gate)
            {
                foreach (KeyValuePair<string, OfflineReplayChatSummary> pair in snapshot)
                {
                    OfflineReplayChatSummary current;
                    if (!_summaries.TryGetValue(pair.Key, out current))
                    {
                        _summaries[pair.Key] = pair.Value;
                        continue;
                    }

                    OfflineReplaySummaryMerge.Reapply(current, pair.Value);
                }
            }
        }
    }
}
