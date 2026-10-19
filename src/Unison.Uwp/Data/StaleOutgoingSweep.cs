// =============================================================================
// StaleOutgoingSweep
//
// An outgoing message is settled as sent or failed by the send that created it,
// in the same process. A message still pending when the process starts was left
// by one that died mid-send, and nothing will ever settle it, so it is failed.
// A late server ack or receipt still promotes it (MessageStatusProgression).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using SQLite;
using Unison.Core.Models;

namespace Unison.Uwp.Data
{
    internal static class StaleOutgoingSweep
    {
        private static Task _once;

        /// <summary>
        /// Runs the sweep once per process. Every store over <c>unison.db</c> awaits it during
        /// its own initialization, so it always lands before this process writes a pending row.
        /// </summary>
        public static Task EnsureRanAsync(SQLiteAsyncConnection connection)
        {
            Task existing = Volatile.Read(ref _once);
            if (existing != null)
            {
                return existing;
            }

            var created = new TaskCompletionSource<bool>();
            existing = Interlocked.CompareExchange(ref _once, created.Task, null);
            if (existing != null)
            {
                return existing;
            }

            _ = RunAsync(connection, created);
            return created.Task;
        }

        private static async Task RunAsync(SQLiteAsyncConnection connection, TaskCompletionSource<bool> done)
        {
            int pending = (int)MessageSendState.Pending;
            int failed = (int)MessageSendState.Failed;

            int messages = await TryExecuteAsync(
                connection,
                "UPDATE history_message SET SendState = ? WHERE IsFromMe = 1 AND SendState = ?",
                failed,
                pending).ConfigureAwait(false);

            // Only tips whose message failed: a tip can read "pending" for a message sent from
            // the phone, which has no row of ours in that state.
            int previews = await TryExecuteAsync(
                connection,
                "UPDATE history_chat_preview SET LastMessageSendState = ? " +
                "WHERE LastMessageIsFromMe = 1 AND LastMessageSendState = ? AND EXISTS (" +
                "SELECT 1 FROM history_message m WHERE m.MessageId = history_chat_preview.LastMessageId " +
                "AND m.IsFromMe = 1 AND m.SendState = ?)",
                failed,
                pending,
                failed).ConfigureAwait(false);

            Debug.WriteLine("[StaleOutgoingSweep] Failed leftover pending sends: messages=" + messages + ", previews=" + previews);
            done.TrySetResult(true);
        }

        /// <summary>A table that does not exist yet has nothing to sweep.</summary>
        private static async Task<int> TryExecuteAsync(SQLiteAsyncConnection connection, string sql, params object[] args)
        {
            try
            {
                return await connection.ExecuteAsync(sql, args).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[StaleOutgoingSweep] Skipped: " + ex.Message);
                return 0;
            }
        }
    }
}
