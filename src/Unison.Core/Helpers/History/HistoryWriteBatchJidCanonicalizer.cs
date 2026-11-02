// =============================================================================
// HistoryWriteBatchJidCanonicalizer
//
// After history sync builds a write batch, fold PN/LID ChatJid variants onto the
// session canonical so reactions/pins/revokes land on the same key as message bodies.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class HistoryWriteBatchJidCanonicalizer
    {
        /// <summary>
        /// Rewrites every ChatJid in <paramref name="batch"/> through <paramref name="getCanonical"/>.
        /// Null/empty inputs are no-ops. Unknown JIDs stay as normalized input when the resolver
        /// returns them unchanged.
        /// </summary>
        public static void Apply(HistoryMessageWriteBatch batch, Func<string, string> getCanonical)
        {
            if (batch == null || getCanonical == null)
            {
                return;
            }

            for (int i = 0; i < batch.Messages.Count; i++)
            {
                HistoryMessage row = batch.Messages[i];
                if (row == null)
                {
                    continue;
                }

                row.ChatJid = CanonicalOrNormalized(row.ChatJid, getCanonical);
            }

            for (int i = 0; i < batch.Reactions.Count; i++)
            {
                HistoryMessageReaction row = batch.Reactions[i];
                if (row == null)
                {
                    continue;
                }

                row.ChatJid = CanonicalOrNormalized(row.ChatJid, getCanonical);
            }

            for (int i = 0; i < batch.Pins.Count; i++)
            {
                HistoryMessagePinUpdate row = batch.Pins[i];
                if (row == null)
                {
                    continue;
                }

                row.ChatJid = CanonicalOrNormalized(row.ChatJid, getCanonical);
            }

            for (int i = 0; i < batch.Revokes.Count; i++)
            {
                HistoryMessageRevoke row = batch.Revokes[i];
                if (row == null)
                {
                    continue;
                }

                row.ChatJid = CanonicalOrNormalized(row.ChatJid, getCanonical);
            }
        }

        private static string CanonicalOrNormalized(string jid, Func<string, string> getCanonical)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return jid;
            }

            string normalized = JidHelper.Normalize(jid) ?? jid.Trim();
            string canonical = getCanonical(normalized);
            return string.IsNullOrWhiteSpace(canonical) ? normalized : canonical;
        }
    }
}
