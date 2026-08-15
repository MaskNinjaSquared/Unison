// =============================================================================
// JidUtils
//
// The predicates and conversions that decide what a JID actually is.
//
// Unison.Baileys already knows how to encode and decode a JID, but it has no
// opinion on the questions the LID work keeps asking: is this a phone number or
// a linked id, is it hosted, do two JIDs name the same user, and how do I move a
// device suffix from one identity to another. Those answers live here, in one
// place, because getting them subtly wrong is how a rewrite ends up with the
// same duplicated chats the old code has.
//
// Ports: rc14 src/WABinary/jid-utils.ts
// =============================================================================
using System;
using Unison.Baileys.Protocol;

namespace Unison.Socket.WABinary
{
    /// <summary>
    /// The address spaces a JID can belong to. The numeric values are the AD-JID domain
    /// types used on the wire, so they can be compared against decoded nodes directly.
    /// </summary>
    public enum WaJidDomain
    {
        WhatsApp = 0,
        Lid = 1,
        Hosted = 128,
        HostedLid = 129
    }

    public static class JidUtils
    {
        public const string ServerWhatsApp = "s.whatsapp.net";
        public const string ServerLid = "lid";
        public const string ServerHosted = "hosted";
        public const string ServerHostedLid = "hosted.lid";
        public const string ServerGroup = "g.us";
        public const string ServerLegacyUser = "c.us";
        public const string ServerBroadcast = "broadcast";
        public const string ServerNewsletter = "newsletter";
        public const string ServerBot = "bot";

        /// <summary>The JID of the status feed.</summary>
        public const string StatusBroadcast = "status@broadcast";

        /// <summary>The device id WhatsApp reserves for hosted (business) endpoints.</summary>
        public const int HostedDeviceId = 99;

        public static bool IsPnUser(string jid)
        {
            return EndsWithServer(jid, ServerWhatsApp);
        }

        public static bool IsLidUser(string jid)
        {
            return EndsWithServer(jid, ServerLid);
        }

        public static bool IsHostedPnUser(string jid)
        {
            return EndsWithServer(jid, ServerHosted);
        }

        public static bool IsHostedLidUser(string jid)
        {
            return EndsWithServer(jid, ServerHostedLid);
        }

        public static bool IsGroup(string jid)
        {
            return EndsWithServer(jid, ServerGroup);
        }

        public static bool IsBroadcast(string jid)
        {
            return EndsWithServer(jid, ServerBroadcast);
        }

        public static bool IsNewsletter(string jid)
        {
            return EndsWithServer(jid, ServerNewsletter);
        }

        /// <summary>The status feed, which is a broadcast list with special handling.</summary>
        public static bool IsStatusBroadcast(string jid)
        {
            return jid == StatusBroadcast;
        }

        /// <summary>Meta's assistant, which is addressed like a user but is not one.</summary>
        public static bool IsMetaAi(string jid)
        {
            return EndsWithServer(jid, ServerBot);
        }

        /// <summary>True for any identity that lives in LID space, hosted or not.</summary>
        public static bool IsAnyLid(string jid)
        {
            return IsLidUser(jid) || IsHostedLidUser(jid);
        }

        /// <summary>True for any identity that lives in phone-number space, hosted or not.</summary>
        public static bool IsAnyPn(string jid)
        {
            return IsPnUser(jid) || IsHostedPnUser(jid);
        }

        /// <summary>
        /// The user part without device or agent, or null when the JID cannot be read.
        /// This is the key both halves of a LID/PN pair are stored under.
        /// </summary>
        public static string GetUser(string jid)
        {
            string user, server;
            int? device;
            int domainType;
            if (!WA.TryDecodeJid(jid, out user, out server, out device, out domainType))
            {
                return null;
            }

            return string.IsNullOrEmpty(user) ? null : user;
        }

        /// <summary>The device id carried by the JID, or 0 when it addresses the account itself.</summary>
        public static int GetDevice(string jid)
        {
            string user, server;
            int? device;
            int domainType;
            if (!WA.TryDecodeJid(jid, out user, out server, out device, out domainType))
            {
                return 0;
            }

            return device.HasValue && device.Value > 0 ? device.Value : 0;
        }

        public static string GetServer(string jid)
        {
            string user, server;
            int? device;
            int domainType;
            if (!WA.TryDecodeJid(jid, out user, out server, out device, out domainType))
            {
                return null;
            }

            return server;
        }

        public static WaJidDomain GetDomain(string jid)
        {
            var server = GetServer(jid);
            if (server == ServerLid)
            {
                return WaJidDomain.Lid;
            }

            if (server == ServerHosted)
            {
                return WaJidDomain.Hosted;
            }

            if (server == ServerHostedLid)
            {
                return WaJidDomain.HostedLid;
            }

            return WaJidDomain.WhatsApp;
        }

        /// <summary>
        /// Strips the device suffix and folds the legacy "c.us" server onto "s.whatsapp.net",
        /// producing the form used as a chat id. Returns an empty string for unreadable input,
        /// matching Baileys so callers can compare results without null checks.
        /// </summary>
        public static string NormalizedUser(string jid)
        {
            string user, server;
            int? device;
            int domainType;
            if (!WA.TryDecodeJid(jid, out user, out server, out device, out domainType))
            {
                return string.Empty;
            }

            if (server == ServerLegacyUser)
            {
                server = ServerWhatsApp;
            }

            return user + "@" + server;
        }

        /// <summary>True when both JIDs name the same account, whatever device they address.</summary>
        public static bool AreSameUser(string first, string second)
        {
            var a = GetUser(first);
            var b = GetUser(second);
            return a != null && b != null && string.Equals(a, b, StringComparison.Ordinal);
        }

        /// <summary>
        /// Rewrites <paramref name="toJid"/> so it addresses the same device as
        /// <paramref name="fromJid"/>. Used when a session moves between address spaces:
        /// the identity changes, the device must not.
        /// </summary>
        public static string TransferDevice(string fromJid, string toJid)
        {
            var device = GetDevice(fromJid);

            string user, server;
            int? decodedDevice;
            int domainType;
            if (!WA.TryDecodeJid(toJid, out user, out server, out decodedDevice, out domainType))
            {
                return toJid;
            }

            return WA.JidEncode(user, server, device);
        }

        /// <summary>
        /// Builds the LID JID for a user, keeping the device the phone-number JID addressed.
        /// Device 99 is hosted, which lives on its own server.
        /// </summary>
        public static string BuildLidJid(string lidUser, int device)
        {
            var server = device == HostedDeviceId ? ServerHostedLid : ServerLid;
            return WA.JidEncode(lidUser, server, device);
        }

        /// <summary>Mirror of <see cref="BuildLidJid"/> for phone-number space.</summary>
        public static string BuildPnJid(string pnUser, int device)
        {
            var server = device == HostedDeviceId ? ServerHosted : ServerWhatsApp;
            return WA.JidEncode(pnUser, server, device);
        }

        private static bool EndsWithServer(string jid, string server)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return false;
            }

            var at = jid.LastIndexOf('@');
            if (at < 0 || at == jid.Length - 1)
            {
                return false;
            }

            return string.Equals(jid.Substring(at + 1), server, StringComparison.OrdinalIgnoreCase);
        }
    }
}
