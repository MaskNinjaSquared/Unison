using System;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// What the offline replay tips need from the client that still owns the open chat,
    /// the window, the catch-up tracker and the persist timer.
    /// </summary>
    public interface IOfflineReplayHost
    {
        bool IsActiveChat(string canonicalJid);

        bool IsWindowVisible { get; }

        void NoteCatchUpProgress(string reason);

        void SchedulePersist();

        void ReportFlushFailure(string reason, Exception error);
    }
}
