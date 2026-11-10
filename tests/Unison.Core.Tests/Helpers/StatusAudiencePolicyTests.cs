// =============================================================================
// StatusAudiencePolicy — one address space for a status@broadcast audience.
//
// The chat list stores some people under their LID and some under their phone
// number, and the resulting mixed participant list is what the server refuses
// with 400. These cover the collapse, the duplicate across both spaces, the
// phone-space preference a status asks for, and what happens when somebody
// cannot be expressed in the chosen space.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Socket.Messages;
using Unison.Socket.Signal;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StatusAudiencePolicyTests
    {
        private const string MeId = "5511999999999@s.whatsapp.net";
        private const string MeLid = "189215086260225@lid";

        /// <summary>A resolver over a fixed table; anything absent simply does not resolve.</summary>
        private static Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> Resolver(
            params LidMapping[] pairs)
        {
            return _ => Task.FromResult<IReadOnlyList<LidMapping>>(pairs.ToList());
        }

        private static Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> NoResolver()
        {
            return _ => Task.FromResult<IReadOnlyList<LidMapping>>(null);
        }

        [Fact]
        public async Task Mixed_recipients_collapse_into_lid_space_when_asked_for_the_widest_list()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "777777777777@lid" },
                MeId,
                MeLid,
                Resolver(new LidMapping("666666666666@lid", "5511888888888@s.whatsapp.net")),
                NoResolver(),
                StatusAddressSpace.WidestLidFirst);

            Assert.Equal("lid", audience.AddressingMode);
            Assert.Empty(audience.Dropped);
            Assert.Equal(
                new[] { "777777777777@lid", "666666666666@lid" },
                audience.Recipients.OrderByDescending(j => j).ToArray());
        }

        [Fact]
        public async Task One_person_stored_in_both_spaces_is_sent_once()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                NoResolver(),
                Resolver(new LidMapping("666666666666@lid", "5511888888888@s.whatsapp.net")),
                StatusAddressSpace.PhoneRequired);

            Assert.Equal("pn", audience.AddressingMode);
            Assert.Equal(new[] { "5511888888888@s.whatsapp.net" }, audience.Recipients.ToArray());
        }

        /// <summary>
        /// An all-LID status list is what the server answers 479 to, so a recipient with no
        /// phone number is left out rather than dragging everybody into LID space with them.
        /// </summary>
        [Fact]
        public async Task A_recipient_with_no_phone_number_is_dropped_rather_than_moving_everyone_to_lid()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                Resolver(new LidMapping("999999999999@lid", "5511888888888@s.whatsapp.net")),
                NoResolver(),
                StatusAddressSpace.PhoneRequired);

            Assert.Equal("pn", audience.AddressingMode);
            Assert.Equal("pn-required-partial", audience.Source);
            Assert.Equal(new[] { "5511888888888@s.whatsapp.net" }, audience.Recipients.ToArray());
            Assert.Single(audience.Dropped);
            Assert.Contains("666666666666", audience.Dropped[0]);
        }

        /// <summary>
        /// Phone space reaching nobody is not a kinder failure than LID, so that one case
        /// still falls back rather than publishing to an empty audience.
        /// </summary>
        [Fact]
        public async Task An_audience_phone_space_cannot_reach_at_all_falls_back_to_lid()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "666666666666@lid", "777777777777@lid" },
                MeId,
                MeLid,
                NoResolver(),
                NoResolver(),
                StatusAddressSpace.PhoneRequired);

            Assert.Equal("lid", audience.AddressingMode);
            Assert.Equal("lid-no-phone-space", audience.Source);
            Assert.Equal(2, audience.Recipients.Count);
        }

        [Fact]
        public async Task A_number_with_no_lid_falls_back_to_phone_space_when_that_is_complete()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                NoResolver(),
                Resolver(new LidMapping("666666666666@lid", "5511777777777@s.whatsapp.net")),
                StatusAddressSpace.PhoneRequired);

            Assert.Equal("pn", audience.AddressingMode);
            Assert.Empty(audience.Dropped);
            Assert.Equal(
                new[] { "5511777777777@s.whatsapp.net", "5511888888888@s.whatsapp.net" },
                audience.Recipients.OrderBy(j => j, StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// Both spaces can hold everyone here, so this is purely the preference of the mode:
        /// PhoneRequired lands in phone space, the way whatsmeow publishes one.
        /// </summary>
        [Fact]
        public async Task Phone_space_wins_when_either_space_would_hold_everyone()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                Resolver(new LidMapping("999999999999@lid", "5511888888888@s.whatsapp.net")),
                Resolver(new LidMapping("666666666666@lid", "5511777777777@s.whatsapp.net")),
                StatusAddressSpace.PhoneRequired);

            Assert.Equal("pn", audience.AddressingMode);
            Assert.Empty(audience.Dropped);
            Assert.All(audience.Recipients, jid => Assert.EndsWith("@s.whatsapp.net", jid));
        }

        /// <summary>
        /// The default is now the widest list with LID preferred, because that is the shape
        /// Baileys sends a status in: no group metadata means its addressing mode falls back
        /// to "lid", which is also what signs the sender key.
        /// </summary>
        [Fact]
        public async Task The_default_space_is_the_widest_list_with_lid_preferred()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                Resolver(new LidMapping("999999999999@lid", "5511888888888@s.whatsapp.net")),
                Resolver(new LidMapping("666666666666@lid", "5511777777777@s.whatsapp.net")));

            Assert.Equal("lid", audience.AddressingMode);
            Assert.Empty(audience.Dropped);
            Assert.All(audience.Recipients, jid => Assert.EndsWith("@lid", jid));
        }

        [Fact]
        public async Task Asking_for_the_widest_list_can_still_produce_lid_space()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                MeLid,
                Resolver(new LidMapping("999999999999@lid", "5511888888888@s.whatsapp.net")),
                Resolver(new LidMapping("666666666666@lid", "5511777777777@s.whatsapp.net")),
                StatusAddressSpace.WidestLidFirst);

            Assert.Equal("lid", audience.AddressingMode);
            Assert.Empty(audience.Dropped);
            Assert.All(audience.Recipients, jid => Assert.EndsWith("@lid", jid));
        }

        [Fact]
        public async Task An_unresolvable_recipient_is_named_rather_than_silently_lost()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid", "555555555555@lid" },
                MeId,
                MeLid,
                NoResolver(),
                NoResolver(),
                StatusAddressSpace.WidestLidFirst);

            // Neither space holds everyone, so the larger survivor wins and the loss is reported.
            Assert.Single(audience.Dropped);
            Assert.Contains("partial", audience.Source);
            Assert.Equal(2, audience.Recipients.Count);
            Assert.Equal("lid", audience.AddressingMode);
        }

        [Fact]
        public async Task Without_a_lid_of_our_own_the_audience_is_phone_space()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new[] { "5511888888888@s.whatsapp.net", "666666666666@lid" },
                MeId,
                null,
                Resolver(new LidMapping("999999999999@lid", "5511888888888@s.whatsapp.net")),
                Resolver(new LidMapping("666666666666@lid", "5511777777777@s.whatsapp.net")));

            Assert.Equal("pn", audience.AddressingMode);
            Assert.All(audience.Recipients, jid => Assert.EndsWith("@s.whatsapp.net", jid));
        }

        [Fact]
        public async Task An_empty_audience_resolves_to_nothing_without_asking_the_server()
        {
            StatusAudience audience = await StatusAudiencePolicy.ResolveAsync(
                new string[0],
                MeId,
                MeLid,
                _ => throw new InvalidOperationException("no lookup for an empty audience"),
                _ => throw new InvalidOperationException("no lookup for an empty audience"));

            Assert.Empty(audience.Recipients);
            Assert.Equal("empty", audience.Source);
        }
    }
}
