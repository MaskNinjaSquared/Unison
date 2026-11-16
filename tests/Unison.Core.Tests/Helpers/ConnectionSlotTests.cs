using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionSlotTests
    {
        private sealed class FakeSocket
        {
        }

        [Fact]
        public void A_new_slot_is_empty()
        {
            var slot = new ConnectionSlot<FakeSocket>();

            Assert.Null(slot.Current);
        }

        [Fact]
        public void Clear_if_only_empties_the_slot_for_the_socket_it_holds()
        {
            var held = new FakeSocket();
            var other = new FakeSocket();
            var slot = new ConnectionSlot<FakeSocket> { Current = held };

            Assert.False(slot.ClearIf(other));
            Assert.Same(held, slot.Current);

            Assert.True(slot.ClearIf(held));
            Assert.Null(slot.Current);
        }

        [Fact]
        public void Holds_reports_reference_identity()
        {
            var held = new FakeSocket();
            var slot = new ConnectionSlot<FakeSocket> { Current = held };

            Assert.True(slot.Holds(held));
            Assert.False(slot.Holds(new FakeSocket()));
            Assert.False(slot.Holds(null));
        }
    }
}
