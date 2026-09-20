using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class IncomingLiveDirectChatRoutingTests
    {
        private const string SelfPn = "5511000000000@s.whatsapp.net";
        private const string SelfLid = "111111111111111@lid";
        private const string PeerPn = "5511999999999@s.whatsapp.net";
        private const string PeerLid = "222222222222222@lid";

        private static IncomingLiveDirectChatRoute Resolve(
            bool isFromMe,
            string from,
            string recipient,
            string peerPn = null,
            string peerLid = null,
            string senderLid = null)
        {
            return IncomingLiveDirectChatRouting.Resolve(
                isFromMe,
                from,
                recipient,
                peerPn,
                peerLid,
                senderLid,
                normalize: jid => string.IsNullOrWhiteSpace(jid) ? null : jid.Trim(),
                isSelfLinked: jid =>
                    string.Equals(jid, SelfPn, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(jid, SelfLid, StringComparison.OrdinalIgnoreCase),
                tryCanonicalNonSelf: (string jid, out string canonical) =>
                {
                    canonical = null;
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        return false;
                    }

                    string n = jid.Trim();
                    if (string.Equals(n, SelfPn, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(n, SelfLid, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    if (string.Equals(n, PeerLid, StringComparison.OrdinalIgnoreCase))
                    {
                        canonical = PeerPn;
                        return true;
                    }

                    if (string.Equals(n, PeerPn, StringComparison.OrdinalIgnoreCase))
                    {
                        canonical = PeerPn;
                        return true;
                    }

                    return false;
                },
                getCanonicalSelfPn: () => SelfPn,
                getCanonical: jid =>
                {
                    if (string.IsNullOrWhiteSpace(jid))
                    {
                        return null;
                    }

                    string n = jid.Trim();
                    if (string.Equals(n, PeerLid, StringComparison.OrdinalIgnoreCase))
                    {
                        return PeerPn;
                    }

                    return n;
                });
        }

        [Fact]
        public void From_me_with_both_sides_self_is_self_chat()
        {
            IncomingLiveDirectChatRoute route = Resolve(true, SelfPn, SelfLid);

            Assert.Equal(SelfPn, route.ChatJid);
            Assert.Equal("self-chat", route.Reason);
        }

        [Fact]
        public void From_me_prefers_recipient_over_peer_hints()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                true,
                SelfPn,
                PeerPn,
                peerPn: "5511888888888@s.whatsapp.net",
                peerLid: PeerLid);

            Assert.Equal(PeerPn, route.ChatJid);
            Assert.Equal("recipient-jid", route.Reason);
        }

        [Fact]
        public void From_me_uses_peer_recipient_pn_when_recipient_is_not_usable()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                true,
                SelfPn,
                null,
                peerPn: PeerPn);

            Assert.Equal(PeerPn, route.ChatJid);
            Assert.Equal("peer-recipient-pn", route.Reason);
        }

        [Fact]
        public void From_me_uses_peer_recipient_lid_when_pn_missing()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                true,
                SelfPn,
                null,
                peerLid: PeerLid);

            Assert.Equal(PeerPn, route.ChatJid);
            Assert.Equal("peer-recipient-lid", route.Reason);
        }

        [Fact]
        public void Inbound_uses_from_when_non_self()
        {
            IncomingLiveDirectChatRoute route = Resolve(false, PeerPn, SelfPn);

            Assert.Equal(PeerPn, route.ChatJid);
            Assert.Equal("from-nonself", route.Reason);
        }

        [Fact]
        public void Inbound_uses_sender_lid_when_from_is_self()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                false,
                SelfPn,
                SelfPn,
                senderLid: PeerLid);

            Assert.Equal(PeerPn, route.ChatJid);
            Assert.Equal("sender-lid", route.Reason);
        }

        [Fact]
        public void All_identity_candidates_self_is_self_chat()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                false,
                SelfPn,
                SelfLid,
                peerPn: SelfPn,
                peerLid: SelfLid,
                senderLid: SelfLid);

            Assert.Equal(SelfPn, route.ChatJid);
            Assert.Equal("self-chat", route.Reason);
        }

        [Fact]
        public void Canonical_from_fallback_keeps_fallback_from_reason()
        {
            IncomingLiveDirectChatRoute route = Resolve(
                false,
                "5511777777777@s.whatsapp.net",
                null);

            Assert.Equal("5511777777777@s.whatsapp.net", route.ChatJid);
            Assert.Equal("fallback-from", route.Reason);
        }

        [Fact]
        public void Empty_from_falls_back_to_self_chat()
        {
            IncomingLiveDirectChatRoute route = IncomingLiveDirectChatRouting.Resolve(
                isFromMe: false,
                fromJid: null,
                recipientJid: null,
                peerRecipientPn: null,
                peerRecipientLid: null,
                senderLid: null,
                normalize: _ => null,
                isSelfLinked: _ => false,
                tryCanonicalNonSelf: (string jid, out string canonical) =>
                {
                    canonical = null;
                    return false;
                },
                getCanonicalSelfPn: () => SelfPn,
                getCanonical: _ => null);

            Assert.Equal(SelfPn, route.ChatJid);
            Assert.Equal("self-chat-fallback", route.Reason);
        }
    }
}
