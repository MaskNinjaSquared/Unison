// =============================================================================
// OfflineReplayTips
//
// Records each replayed message into the tip book and flushes the book onto the
// chat list. Unread is decided once, at record time, so opening or closing the
// chat mid-drain cannot drop the badge. The first tip arms one timer (throttle,
// not debounce) so the first recovered chat shows while a long replay continues.
// A failed flush puts the tips back.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class OfflineReplayTips
    {
        public const string ProgressiveReason = "replay-progressive";

        private readonly OfflineReplaySummaryBook _book = new OfflineReplaySummaryBook();
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly IJidResolver _jids;
        private readonly IOfflineReplayHost _host;
        private readonly Func<IReadOnlyList<OfflineReplayChatSummary>, string, Task> _apply;
        private readonly TimeSpan _throttle;

        public OfflineReplayTips(
            IJidResolver jids,
            IOfflineReplayHost host,
            Func<IReadOnlyList<OfflineReplayChatSummary>, string, Task> apply,
            TimeSpan throttle)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _throttle = throttle;
        }

        public void Record(OfflineReplayTipRequest request)
        {
            if (request == null)
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(JidHelper.Normalize(request.Jid));
            if (string.IsNullOrWhiteSpace(canonical))
            {
                return;
            }

            bool incrementUnread = request.CountUnread &&
                IncomingAttention.For(
                    request.IsFromMe,
                    _host.IsActiveChat(canonical),
                    _host.IsWindowVisible).CountsAsUnread;

            _book.Record(
                canonical,
                request.Preview,
                request.Timestamp,
                request.IsGroup,
                request.IsFromMe,
                request.Kind,
                request.Status,
                request.AuthorPrefix,
                incrementUnread,
                DateTime.UtcNow,
                request.ParticipantJid,
                request.SenderName,
                _throttle,
                OnThrottleElapsed);
        }

        public async Task FlushAsync(string reason)
        {
            await _flushLock.WaitAsync().ConfigureAwait(false);
            Dictionary<string, OfflineReplayChatSummary> snapshot = null;
            try
            {
                snapshot = _book.Take();
                if (snapshot == null)
                {
                    return;
                }

                List<OfflineReplayChatSummary> batch = snapshot.Values.ToList();
                await _apply(batch, reason).ConfigureAwait(false);

                _host.NoteCatchUpProgress("offline-summary:" + reason + ":chats=" + batch.Count);
                _host.SchedulePersist();
            }
            catch
            {
                if (snapshot != null)
                {
                    _book.Restore(snapshot);
                }

                throw;
            }
            finally
            {
                _flushLock.Release();
            }
        }

        // Fire-and-forget: the timer thread must not wait on the UI pass.
        private void OnThrottleElapsed()
        {
            FlushAsync(ProgressiveReason).ContinueWith(finished =>
            {
                if (finished.Exception != null)
                {
                    _host.ReportFlushFailure(ProgressiveReason, finished.Exception.GetBaseException());
                }
            });
        }
    }
}
