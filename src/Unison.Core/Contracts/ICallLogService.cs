// =============================================================================
// ICallLogService
//
// In-process call log: Recents (one row per contact) + per-JID history.
// Live CallHandler terminals upsert here; chat list is never touched.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    public interface ICallLogService
    {
        event EventHandler Changed;

        /// <summary>Load persisted rows into memory (safe to call more than once).</summary>
        Task InitializeAsync();

        /// <summary>
        /// Insert or replace by CallId+ChromeKind. Normalizes JID via the host's
        /// canonical/alias rules before grouping. Persists when a store is attached.
        /// </summary>
        void Upsert(CallHistoryEntry entry);

        /// <summary>One aggregated row per contact, newest first.</summary>
        IReadOnlyList<CallRecentItem> GetRecents();

        /// <summary>All entries for a contact (and linked alias), newest first.</summary>
        IReadOnlyList<CallHistoryEntry> GetHistory(string jid);
    }
}

