using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryPersistBatchingTests
    {
        [Fact]
        public void Empty_batch_yields_nothing()
        {
            Assert.Empty(HistoryPersistBatching.Split(new HistoryMessageWriteBatch(), 80));
        }

        [Fact]
        public void Messages_are_sliced_and_side_effects_ride_the_first_slice()
        {
            var batch = new HistoryMessageWriteBatch { PreferDeltaSkipExistingBodies = true };
            for (int i = 0; i < 5; i++)
            {
                batch.Messages.Add(new HistoryMessage { MessageId = "m" + i, ChatJid = "a@s.whatsapp.net" });
            }

            batch.Reactions.Add(new HistoryMessageReaction { MessageId = "m0", ChatJid = "a@s.whatsapp.net" });
            batch.Pins.Add(new HistoryMessagePinUpdate { MessageId = "m1", ChatJid = "a@s.whatsapp.net" });

            List<HistoryMessageWriteBatch> slices = HistoryPersistBatching.Split(batch, 2).ToList();

            Assert.Equal(3, slices.Count);
            Assert.Equal(2, slices[0].Messages.Count);
            Assert.Equal(2, slices[1].Messages.Count);
            Assert.Equal(1, slices[2].Messages.Count);
            Assert.Single(slices[0].Reactions);
            Assert.Single(slices[0].Pins);
            Assert.Empty(slices[1].Reactions);
            Assert.Empty(slices[2].Pins);
            Assert.True(slices[0].PreferDeltaSkipExistingBodies);
        }

        [Fact]
        public void Side_effects_only_still_yield_the_source_batch()
        {
            var batch = new HistoryMessageWriteBatch();
            batch.Revokes.Add(new HistoryMessageRevoke { MessageId = "m0", ChatJid = "a@s.whatsapp.net" });

            List<HistoryMessageWriteBatch> slices = HistoryPersistBatching.Split(batch, 80).ToList();

            Assert.Single(slices);
            Assert.Same(batch, slices[0]);
        }
    }
}
