using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PresenceJidMatchTests
    {
        private const string Pn = "15551234567@s.whatsapp.net";
        private const string Lid = "98765432101234@lid";
        private const string OtherPn = "15559876543@s.whatsapp.net";

        [Fact]
        public void Same_contact_matches_ignoring_case_and_device()
        {
            Assert.True(PresenceJidMatch.IsWatched(Pn, Pn, null));
            Assert.True(PresenceJidMatch.IsWatched("15551234567:3@s.whatsapp.net", Pn, null));
        }

        [Fact]
        public void Another_contact_does_not_match()
        {
            Assert.False(PresenceJidMatch.IsWatched(OtherPn, Pn, new PairResolver()));
        }

        [Fact]
        public void Lid_answer_matches_a_pn_subscription_through_the_alias()
        {
            Assert.True(PresenceJidMatch.IsWatched(Lid, Pn, new PairResolver()));
            Assert.True(PresenceJidMatch.IsWatched(Pn, Lid, new PairResolver()));
        }

        [Fact]
        public void Unknown_lid_does_not_match_a_pn()
        {
            Assert.False(PresenceJidMatch.IsWatched(Lid, Pn, null));
        }

        [Fact]
        public void Groups_and_channels_never_take_presence()
        {
            Assert.False(PresenceJidMatch.IsWatched(Pn, "120363000000000001@g.us", null));
            Assert.False(PresenceJidMatch.IsWatched(Pn, "120363000000000001@newsletter", null));
        }

        [Fact]
        public void Blank_jids_never_match()
        {
            Assert.False(PresenceJidMatch.IsWatched(null, Pn, null));
            Assert.False(PresenceJidMatch.IsWatched(Pn, " ", null));
        }

        private sealed class PairResolver : IJidResolver
        {
            public Profile Self => null;

            public string GetCanonicalJid(string jid) =>
                string.Equals(jid, Lid, StringComparison.OrdinalIgnoreCase) ? Pn : jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = string.Equals(jid, Pn, StringComparison.OrdinalIgnoreCase) ? Lid
                    : string.Equals(jid, Lid, StringComparison.OrdinalIgnoreCase) ? Pn
                    : null;
                return alias != null;
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
