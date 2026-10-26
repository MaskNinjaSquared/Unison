using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.WhatsAppClient.Messages;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public class IncomingMessagePumpTests
    {
        [Fact]
        public void Enqueue_live_then_snapshot_shows_live_depth()
        {
            var pump = new IncomingMessagePump(() => null);

            pump.Enqueue(
                new IncomingDecryptedNotifyRequest
                {
                    MessageId = "m1",
                    FromJid = "1@s.whatsapp.net",
                    IsOffline = false
                });

            var snap = pump.Snapshot();
            Assert.Equal(1, snap.LiveDepth);
            Assert.Equal(0, snap.OfflineDepth);
            Assert.True(snap.Running);
        }

        [Fact]
        public async Task Drain_with_no_facade_still_finishes_item()
        {
            var pump = new IncomingMessagePump(() => null);
            pump.Enqueue(
                new IncomingDecryptedNotifyRequest
                {
                    MessageId = "m2",
                    FromJid = "1@s.whatsapp.net",
                    IsOffline = true
                });

            // Pump Task.Run starts drain; give it a moment to drop the missing-façade item.
            await pump.WaitForDrainAsync(2000);

            var snap = pump.Snapshot();
            Assert.Equal(0, snap.LiveDepth);
            Assert.Equal(0, snap.OfflineDepth);
        }
    }
}
