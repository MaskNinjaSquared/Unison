// =============================================================================
// SameDeviceAddressDedupe — one device reached under both its PN and its LID
// address must be sealed for only once.
// =============================================================================
using System.Collections.Generic;
using System.Linq;
using Unison.Socket.Messages;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Messages
{
    public sealed class SameDeviceAddressDedupeTests
    {
        private static readonly Dictionary<string, string> Pairs = new Dictionary<string, string>
        {
            { "5511@s.whatsapp.net", "900@lid" },
            { "5511:7@s.whatsapp.net", "900:7@lid" },
            { "900@lid", "5511@s.whatsapp.net" },
            { "900:7@lid", "5511:7@s.whatsapp.net" }
        };

        [Fact]
        public void Keeps_the_lid_address_when_both_forms_of_a_device_are_listed()
        {
            var devices = new List<DeviceJid>
            {
                Device("5511@s.whatsapp.net"),
                Device("5511:7@s.whatsapp.net"),
                Device("900@lid"),
                Device("900:7@lid"),
                Device("4422@s.whatsapp.net")
            };

            int dropped = SameDeviceAddressDedupe.Apply(devices, Paired);

            Assert.Equal(2, dropped);
            Assert.Equal(
                new[] { "900@lid", "900:7@lid", "4422@s.whatsapp.net" },
                devices.Select(d => d.Jid).ToArray());
        }

        [Fact]
        public void Leaves_a_single_form_alone_even_when_its_pair_is_known()
        {
            var devices = new List<DeviceJid> { Device("5511@s.whatsapp.net"), Device("77@lid") };

            int dropped = SameDeviceAddressDedupe.Apply(devices, Paired);

            Assert.Equal(0, dropped);
            Assert.Equal(2, devices.Count);
        }

        [Fact]
        public void Drops_exact_repeats()
        {
            var devices = new List<DeviceJid> { Device("77@lid"), Device("77@lid") };

            int dropped = SameDeviceAddressDedupe.Apply(devices, Paired);

            Assert.Equal(1, dropped);
            Assert.Single(devices);
        }

        private static string Paired(string jid)
        {
            string paired;
            return Pairs.TryGetValue(jid, out paired) ? paired : null;
        }

        private static DeviceJid Device(string jid) => new DeviceJid { Jid = jid };
    }
}
