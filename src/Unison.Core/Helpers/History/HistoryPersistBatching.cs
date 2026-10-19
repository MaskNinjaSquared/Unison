// =============================================================================
// HistoryPersistBatching
//
// Splits a built history write so one SQLite transaction cannot hold an entire
// sync chunk. Side effects travel with the first slice (or alone when there are
// no bodies).
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class HistoryPersistBatching
    {
        public static IEnumerable<HistoryMessageWriteBatch> Split(
            HistoryMessageWriteBatch batch,
            int subBatchSize)
        {
            if (batch == null || batch.IsEmpty)
            {
                yield break;
            }

            int size = subBatchSize > 0 ? subBatchSize : 500;
            IList<HistoryMessage> messages = batch.Messages;
            int count = messages?.Count ?? 0;
            if (count == 0)
            {
                yield return batch;
                yield break;
            }

            bool sideEffectsAttached = false;
            for (int offset = 0; offset < count; offset += size)
            {
                int take = System.Math.Min(size, count - offset);
                var slice = new HistoryMessageWriteBatch
                {
                    PreferDeltaSkipExistingBodies = batch.PreferDeltaSkipExistingBodies,
                    ReplaceExistingReactions = batch.ReplaceExistingReactions
                };

                for (int i = 0; i < take; i++)
                {
                    slice.Messages.Add(messages[offset + i]);
                }

                if (!sideEffectsAttached)
                {
                    AttachSideEffects(slice, batch);
                    sideEffectsAttached = true;
                }

                yield return slice;
            }
        }

        private static void AttachSideEffects(HistoryMessageWriteBatch slice, HistoryMessageWriteBatch source)
        {
            if (source.Reactions != null)
            {
                for (int i = 0; i < source.Reactions.Count; i++)
                {
                    slice.Reactions.Add(source.Reactions[i]);
                }
            }

            if (source.Pins != null)
            {
                for (int i = 0; i < source.Pins.Count; i++)
                {
                    slice.Pins.Add(source.Pins[i]);
                }
            }

            if (source.Revokes != null)
            {
                for (int i = 0; i < source.Revokes.Count; i++)
                {
                    slice.Revokes.Add(source.Revokes[i]);
                }
            }
        }
    }
}
