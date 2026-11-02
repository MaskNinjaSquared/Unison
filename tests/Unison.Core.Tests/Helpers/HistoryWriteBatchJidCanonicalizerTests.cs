using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryWriteBatchJidCanonicalizerTests
    {
        [Fact]
        public void Apply_rewrites_reaction_chat_jid_to_canonical()
        {
            const string lid = "1234567890@lid";
            const string pn = "5511999998888@s.whatsapp.net";
            var batch = new HistoryMessageWriteBatch();
            batch.Messages.Add(new HistoryMessage { ChatJid = pn, MessageId = "m1" });
            batch.Reactions.Add(new HistoryMessageReaction
            {
                ChatJid = lid,
                MessageId = "m1",
                ReactorJid = "other@s.whatsapp.net",
                Emoji = "👍"
            });
            batch.Pins.Add(new HistoryMessagePinUpdate { ChatJid = lid, MessageId = "m1", IsPinned = true });
            batch.Revokes.Add(new HistoryMessageRevoke { ChatJid = lid, MessageId = "m2" });

            var map = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase)
            {
                [lid] = pn
            };

            HistoryWriteBatchJidCanonicalizer.Apply(
                batch,
                jid =>
                {
                    string key = JidHelper.Normalize(jid) ?? jid;
                    string canonical;
                    return map.TryGetValue(key, out canonical) ? canonical : key;
                });

            Assert.Equal(pn, batch.Messages[0].ChatJid);
            Assert.Equal(pn, batch.Reactions[0].ChatJid);
            Assert.Equal(pn, batch.Pins[0].ChatJid);
            Assert.Equal(pn, batch.Revokes[0].ChatJid);
        }
    }
}
