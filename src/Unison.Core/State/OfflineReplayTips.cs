// =============================================================================
// OfflineReplayTips
//
// Records each replayed message into the tip book and flushes the book onto the
// chat list. Unread is decided once, at record time, so opening or closing the
// chat mid-drain cannot drop the badge. The first tip arms one timer (throttle,
// not debounce) so the first recovered chat shows while a long replay continues.
// After that first progressive flush the wider steady window applies until a
// settle flush (any other reason) ends the replay. A failed flush puts the tips back.
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
        private readonly TimeSpan _steadyThrottle;
        private readonly Func<TimeSpan> _postFlushQuiet;
        private readonly Func<bool> _deferTipPaintUntilSettle;
        private int _progressiveFlushed;
        private int _flushing;
        private long _quietUntilUtcTicks;

        /// <param name="throttle">Window for the first progressive flush of a replay.</param>
        /// <param name="steadyThrottle">
        /// Window for every later progressive flush until a non-progressive flush ends the replay.
        /// Replayed messages trickle in one by one, so a short window still moves one row per pass.
        /// Defaults to <paramref name="throttle"/>.
        /// </param>
        /// <param name="postFlushQuiet">
        /// Extra IsBusy window after a flush so silent UI cannot start between progressive batches.
        /// Defaults to zero (tests).
        /// </param>
        /// <param name="deferTipPaintUntilSettle">
        /// When true (PreferFrugal), Record keeps tips in the book and does not arm the
        /// progressive timer — settle <see cref="FlushAsync"/> paints once after persist.
        /// </param>
        public OfflineReplayTips(
            IJidResolver jids,
            IOfflineReplayHost host,
            Func<IReadOnlyList<OfflineReplayChatSummary>, string, Task> apply,
            TimeSpan throttle,
            TimeSpan? steadyThrottle = null,
            Func<TimeSpan> postFlushQuiet = null,
            Func<bool> deferTipPaintUntilSettle = null)
        {
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _apply = apply ?? throw new ArgumentNullException(nameof(apply));
            _throttle = throttle;
            _steadyThrottle = steadyThrottle ?? throttle;
            _postFlushQuiet = postFlushQuiet ?? (() => TimeSpan.Zero);
            _deferTipPaintUntilSettle = deferTipPaintUntilSettle ?? (() => false);
        }

        /// <summary>
        /// Tips waiting in the book, a progressive timer armed, a flush apply in flight,
        /// or the post-flush quiet that covers empty-book gaps between progressive batches.
        /// </summary>
        public bool IsBusy =>
            _book.HasPending ||
            Volatile.Read(ref _flushing) != 0 ||
            IsInPostFlushQuiet();

        private bool IsInPostFlushQuiet()
        {
            long until = Volatile.Read(ref _quietUntilUtcTicks);
            return until != 0 && DateTime.UtcNow.Ticks < until;
        }

        private void ArmPostFlushQuiet()
        {
            TimeSpan quiet = _postFlushQuiet();
            if (quiet <= TimeSpan.Zero)
            {
                Volatile.Write(ref _quietUntilUtcTicks, 0);
                return;
            }

            Volatile.Write(ref _quietUntilUtcTicks, DateTime.UtcNow.Add(quiet).Ticks);
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

            // PreferFrugal: no progressive timer — book only until settle flush.
            bool deferPaint = _deferTipPaintUntilSettle();
            Action onDue = null;
            if (!deferPaint)
            {
                onDue = OnThrottleElapsed;
            }

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
                Volatile.Read(ref _progressiveFlushed) == 0 ? _throttle : _steadyThrottle,
                onDue,
                request.HasCitation && incrementUnread);
        }

        public async Task FlushAsync(string reason)
        {
            // Belt: progressive reason must not Take+paint while PreferFrugal defers to settle.
            if (string.Equals(reason, ProgressiveReason, StringComparison.Ordinal) &&
                _deferTipPaintUntilSettle())
            {
                return;
            }

            await _flushLock.WaitAsync().ConfigureAwait(false);
            Dictionary<string, OfflineReplayChatSummary> snapshot = null;
            Interlocked.Exchange(ref _flushing, 1);
            try
            {
                Volatile.Write(
                    ref _progressiveFlushed,
                    string.Equals(reason, ProgressiveReason, StringComparison.Ordinal) ? 1 : 0);
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
                Interlocked.Exchange(ref _flushing, 0);
                ArmPostFlushQuiet();
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
