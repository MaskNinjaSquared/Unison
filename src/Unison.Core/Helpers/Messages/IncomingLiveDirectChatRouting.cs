// =============================================================================
// IncomingLiveDirectChatRouting
//
// Which conversation bucket a live DM envelope belongs to. Self-chat is its
// own lane; from-me prefers recipient / peer hints; otherwise from / sender LID.
// Canonicalization and self-link checks stay with the host via callbacks.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;

namespace Unison.Core.Helpers
{
    public readonly struct IncomingLiveDirectChatRoute
    {
        public IncomingLiveDirectChatRoute(string chatJid, string reason)
        {
            ChatJid = chatJid;
            Reason = reason ?? string.Empty;
        }

        public string ChatJid { get; }
        public string Reason { get; }
    }

    public static class IncomingLiveDirectChatRouting
    {
        public delegate bool TryCanonicalNonSelf(string jid, out string canonical);

        public static IncomingLiveDirectChatRoute Resolve(
            bool isFromMe,
            string fromJid,
            string recipientJid,
            string peerRecipientPn,
            string peerRecipientLid,
            string senderLid,
            Func<string, string> normalize,
            Func<string, bool> isSelfLinked,
            TryCanonicalNonSelf tryCanonicalNonSelf,
            Func<string> getCanonicalSelfPn,
            Func<string, string> getCanonical)
        {
            if (normalize == null)
            {
                throw new ArgumentNullException(nameof(normalize));
            }

            if (isSelfLinked == null)
            {
                throw new ArgumentNullException(nameof(isSelfLinked));
            }

            if (tryCanonicalNonSelf == null)
            {
                throw new ArgumentNullException(nameof(tryCanonicalNonSelf));
            }

            if (getCanonicalSelfPn == null)
            {
                throw new ArgumentNullException(nameof(getCanonicalSelfPn));
            }

            if (getCanonical == null)
            {
                throw new ArgumentNullException(nameof(getCanonical));
            }

            string reason = "fallback-from";
            string normalizedFrom = normalize(fromJid);
            string normalizedRecipient = normalize(recipientJid);

            // Self-chat is a distinct lane. When both the sender and recipient are already us,
            // ignore companion/device peer-recipient hints and force the canonical self PN bucket.
            if (isFromMe && isSelfLinked(normalizedFrom) && isSelfLinked(normalizedRecipient))
            {
                return new IncomingLiveDirectChatRoute(getCanonicalSelfPn(), "self-chat");
            }

            if (isFromMe)
            {
                string canonical;
                if (tryCanonicalNonSelf(recipientJid, out canonical))
                {
                    return new IncomingLiveDirectChatRoute(canonical, "recipient-jid");
                }

                if (tryCanonicalNonSelf(peerRecipientPn, out canonical))
                {
                    return new IncomingLiveDirectChatRoute(canonical, "peer-recipient-pn");
                }

                if (tryCanonicalNonSelf(peerRecipientLid, out canonical))
                {
                    return new IncomingLiveDirectChatRoute(canonical, "peer-recipient-lid");
                }
            }

            {
                string canonical;
                if (tryCanonicalNonSelf(fromJid, out canonical))
                {
                    return new IncomingLiveDirectChatRoute(canonical, "from-nonself");
                }

                if (tryCanonicalNonSelf(senderLid, out canonical))
                {
                    return new IncomingLiveDirectChatRoute(canonical, "sender-lid");
                }
            }

            List<string> identityCandidates = new[]
                {
                    normalize(fromJid),
                    normalize(recipientJid),
                    normalize(peerRecipientPn),
                    normalize(peerRecipientLid),
                    normalize(senderLid)
                }
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (identityCandidates.Count > 0 && identityCandidates.All(isSelfLinked))
            {
                return new IncomingLiveDirectChatRoute(getCanonicalSelfPn(), "self-chat");
            }

            string fallback = getCanonical(fromJid);
            if (!string.IsNullOrWhiteSpace(fallback))
            {
                return new IncomingLiveDirectChatRoute(fallback, reason);
            }

            return new IncomingLiveDirectChatRoute(getCanonicalSelfPn(), "self-chat-fallback");
        }
    }
}
