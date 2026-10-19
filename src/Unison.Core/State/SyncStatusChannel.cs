// =============================================================================
// SyncStatusChannel
//
// The single status line the chat list shows while the app syncs. Startup,
// names, pictures, groups and history all write to it, so the rules about what
// may replace what live here rather than with whichever writer came last:
// - while history catch-up is shown, a clear puts its banner back, so a phase
//   finishing elsewhere cannot hide "Synchronizing history…";
// - list enrichment phases stay off an open conversation, because on Mobile
//   the line is painted in the status bar over the chat;
// - with the setting off, every status leaves as a clear.
// =============================================================================
using System;
using System.Threading;
using Unison.Core.Helpers;

namespace Unison.Core.State
{
    public sealed class SyncStatusChannel
    {
        private readonly ActiveChat _activeChat;
        private readonly Func<bool> _isShown;
        private int _historyCatchUpShown;

        public SyncStatusChannel(ActiveChat activeChat, Func<bool> isShown)
        {
            _activeChat = activeChat ?? throw new ArgumentNullException(nameof(activeChat));
            _isShown = isShown ?? throw new ArgumentNullException(nameof(isShown));
        }

        /// <summary>The text the list should show now; null clears it. Raised on the writer's thread.</summary>
        public event EventHandler<string> Changed;

        public bool IsHistoryCatchUpShown => Volatile.Read(ref _historyCatchUpShown) == 1;

        /// <summary>Shows <paramref name="status"/> under the rules above; null or empty clears.</summary>
        public void Raise(string status)
        {
            if (string.IsNullOrEmpty(status))
            {
                Publish(IsHistoryCatchUpShown ? HistoryCatchUpText() : null);
                return;
            }

            if (!string.IsNullOrWhiteSpace(_activeChat.Jid) && ListEnrichmentPhase.Matches(status))
            {
                return;
            }

            Publish(status);
        }

        /// <summary>Shows <paramref name="status"/> as is, honouring only the setting.</summary>
        public void Publish(string status)
        {
            Changed?.Invoke(this, IsShown() ? status : null);
        }

        public void BeginHistoryCatchUp()
        {
            Interlocked.Exchange(ref _historyCatchUpShown, 1);
            Publish(HistoryCatchUpText());
        }

        /// <summary>Clears the history banner. False, and nothing published, when it was not shown.</summary>
        public bool EndHistoryCatchUp()
        {
            if (Interlocked.Exchange(ref _historyCatchUpShown, 0) == 0)
            {
                return false;
            }

            Publish(null);
            return true;
        }

        private static string HistoryCatchUpText() => SyncPhaseStatus.Format(SyncPhaseStatus.HistoryCatchUp);

        private bool IsShown()
        {
            try
            {
                return _isShown();
            }
            catch (Exception)
            {
                return true;
            }
        }
    }
}
