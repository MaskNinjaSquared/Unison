// =============================================================================
// IHistorySyncTuning
//
// History sync knobs the WhatsApp client executes with. Values come from the
// product layer (device tier, frugal mode); the client never sees those concepts.
// Values may change between reads (frugal flips at runtime).
// =============================================================================
using System;

namespace Unison.Core.Contracts.Tuning
{
    public interface IHistorySyncTuning
    {
        /// <summary>Newest bodies kept per conversation when a history chunk is written.</summary>
        int MaxMessagesPerConversation { get; }

        /// <summary>Whether history thumbs are materialized before the SQLite write.</summary>
        bool MaterializeThumbsDuringSync { get; }

        /// <summary>History rows one SQLite transaction may write.</summary>
        int PersistSubBatchSize { get; }

        /// <summary>Empty catch-up cycles tolerated before the continue loop stops.</summary>
        int CatchUpStagnantCyclesBeforeStop { get; }

        /// <summary>FULL_HISTORY catch-up rounds allowed per session.</summary>
        int MaxFullHistoryCatchUpRounds { get; }

        /// <summary>Whether a stale finish may start the repair cascade.</summary>
        bool AllowStagnantRepairCascade { get; }

        /// <summary>Gap between silent FULL_HISTORY lots.</summary>
        TimeSpan SilentFullHistoryDelay { get; }

        /// <summary>SQLite quiet before a history lot is finalized (at least 200 ms).</summary>
        int HistoryFinalizeQuietMs { get; }

        /// <summary>Delay before a FULL history lot is finalized (at least 200 ms).</summary>
        int HistoryFullFinalizeDelayMs { get; }
    }
}
