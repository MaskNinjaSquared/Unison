// =============================================================================
// RosterUiRefreshCoalescer
//
// One roster flush raises GroupMembers + HasGroupMembers + MentionLookup, and
// flushes arrive in bursts (messages, quotes, viewport, w:gp2). Each used to run
// RebuildParticipantLookup + RecomputeMessageRuns, so the timeline blinked.
// Shared by all ChatDetailView shells: one Low-priority refresh per quiet window.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Helpers;
using Windows.UI.Core;

namespace Unison.Uwp.UI.Helpers
{
    internal sealed class RosterUiRefreshCoalescer
    {
        public static readonly TimeSpan QuietWindow = TimeSpan.FromMilliseconds(300);

        private readonly CoreDispatcher _dispatcher;
        private readonly Func<bool> _isTimelineBusy;
        private readonly Func<string> _activeChatJid;
        private readonly Action _refresh;
        private readonly IRuntimeDiagnostics _diagnostics;
        private int _queued;
        private int _requests;
        private int _generation;

        /// <param name="refresh">Runs on the UI thread at Low priority; must be cheap to skip when nothing is loaded.</param>
        public RosterUiRefreshCoalescer(
            CoreDispatcher dispatcher,
            Func<bool> isTimelineBusy,
            Func<string> activeChatJid,
            Action refresh,
            IRuntimeDiagnostics diagnostics)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
            _isTimelineBusy = isTimelineBusy ?? (() => false);
            _activeChatJid = activeChatJid ?? (() => null);
            _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            _diagnostics = diagnostics;
        }

        /// <summary>Safe from any thread. Bursts inside <see cref="QuietWindow"/> collapse into one refresh.</summary>
        public void Request()
        {
            Interlocked.Increment(ref _requests);
            if (Interlocked.Exchange(ref _queued, 1) == 1)
            {
                return;
            }

            _ = RunAfterQuietWindowAsync(Volatile.Read(ref _generation));
        }

        /// <summary>Drops a pending refresh (chat switched or view unloaded).</summary>
        public void Cancel()
        {
            Interlocked.Increment(ref _generation);
            Interlocked.Exchange(ref _requests, 0);
            Interlocked.Exchange(ref _queued, 0);
        }

        private async Task RunAfterQuietWindowAsync(int generation)
        {
            await Task.Delay(QuietWindow).ConfigureAwait(false);
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            await _dispatcher.RunAsync(CoreDispatcherPriority.Low, () => RunOnUi(generation));
        }

        private void RunOnUi(int generation)
        {
            if (generation != Volatile.Read(ref _generation))
            {
                return;
            }

            if (_isTimelineBusy())
            {
                _ = RunAfterQuietWindowAsync(generation);
                return;
            }

            Interlocked.Exchange(ref _queued, 0);
            int coalesced = Interlocked.Exchange(ref _requests, 0);
            var watch = Stopwatch.StartNew();
            try
            {
                _refresh();
            }
            finally
            {
                watch.Stop();
                _diagnostics?.Write(
                    "roster",
                    "ui-refresh",
                    "chat=" + DiagnosticJidMask.Apply(_activeChatJid()) +
                    "; coalesced=" + coalesced +
                    "; ms=" + watch.ElapsedMilliseconds);
            }
        }
    }
}
