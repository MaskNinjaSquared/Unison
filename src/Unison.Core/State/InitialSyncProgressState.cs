// =============================================================================
// InitialSyncProgressState
//
// How far the first sync after login has got. While it is active the list
// releases rows in batches and the sync budget can go frugal.
//
// The client owns when it starts and ends (SQLite history chunks and the quiet
// period after them); it only reports here, so the history façade and the sync
// budget can read it without reaching into the client.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class InitialSyncProgressState
    {
        /// <summary>The list shows at least this many rows while the first sync runs.</summary>
        private const int MinimumVisibleChats = 20;

        private volatile bool _isActive;
        private int _processed;
        private int _total;

        public bool IsActive => _isActive;

        public int ProcessedConversations => _processed;

        /// <summary>How many the first sync said there would be. Zero until it says.</summary>
        public int TotalConversations => _total;

        /// <summary>Raised on the publisher's thread after every <see cref="Publish"/>.</summary>
        public event EventHandler<InitialSyncProgressEventArgs> Changed;

        public InitialSyncProgressEventArgs Publish(bool active, bool completed, int processed, int total, string stage)
        {
            _processed = Math.Max(0, processed);
            _total = Math.Max(0, total);
            _isActive = active;

            var args = new InitialSyncProgressEventArgs
            {
                IsActive = active,
                IsCompleted = completed,
                ProcessedConversations = _processed,
                TotalConversations = _total,
                VisibleChatTarget = Math.Min(_total, Math.Max(MinimumVisibleChats, _processed)),
                Stage = stage
            };

            try
            {
                Changed?.Invoke(this, args);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[InitialSyncProgressState] Listener failed: " + ex.Message);
            }

            return args;
        }
    }
}
