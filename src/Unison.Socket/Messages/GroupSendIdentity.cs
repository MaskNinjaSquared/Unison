// =============================================================================
// GroupSendIdentity
//
// Which address space a group message travels in, and which of our identities
// signs the sender key.
//
// LID groups are the common case on current WhatsApp. A companion's Me.Lid is
// often stored without a device id (pairing or the success node), while Me.Id
// carries the real companion device. The sender-key name is (group, device):
// signing under the bare LID puts the key on device 0, while the server stamps
// the outgoing participant as our companion device — members look for a key that
// was never distributed. TransferDevice keeps the device and only changes the
// identity space, matching Baileys' transferDevice when mirroring PN ↔ LID.
//
// Ports: rc14 relayMessage addressing_mode / groupSenderIdentity in
// messages-send.ts, plus transferDevice in WABinary/jid-utils.ts.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.Models;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public static class GroupSendIdentity
    {
        /// <summary>
        /// Which addressing attribute the stanza should carry. Server wins; when the server
        /// said nothing, LID participants plus a LID of our own beat the bare default.
        /// </summary>
        public static string ResolveAddressingMode(
            GroupMetadata metadata,
            string meLid,
            out string source)
        {
            if (metadata != null && metadata.AddressingMode == GroupAddressingMode.Lid)
            {
                source = "server";
                return "lid";
            }

            if (metadata != null && metadata.AddressingMode == GroupAddressingMode.Pn)
            {
                source = "server";
                return "pn";
            }

            if (metadata != null &&
                metadata.Participants != null &&
                HasLidParticipant(metadata.Participants) &&
                !string.IsNullOrEmpty(meLid))
            {
                source = "inferred-lid-participants";
                return "lid";
            }

            if (string.IsNullOrEmpty(meLid))
            {
                source = "no-lid-of-our-own";
                return "pn";
            }

            source = "default";
            return "lid";
        }

        /// <summary>
        /// The JID the group's sender key is filed under. Always carries our companion device
        /// when we have one, even if Me.Lid was stored without it.
        /// </summary>
        public static string ResolveSigner(string addressingMode, string meId, string meLid)
        {
            if (string.Equals(addressingMode, "lid", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(meLid))
            {
                var withDevice = JidUtils.TransferDevice(meId, meLid);
                return WA.NormalizeDeviceJid(withDevice);
            }

            return WA.NormalizeDeviceJid(meId);
        }

        /// <summary>
        /// Our companion device id, even when Me.Id was stored without one (use Me.Lid's device).
        /// Zero means we cannot tell this companion apart from the phone.
        /// </summary>
        public static int ResolveCompanionDevice(string meId, string meLid)
        {
            var device = JidUtils.GetDevice(meId);
            return device > 0 ? device : JidUtils.GetDevice(meLid);
        }

        /// <summary>
        /// Device 0 of our account in the address space the group is using — the handset.
        /// Emitting both PN and LID for a LID group double-advances the same Signal session
        /// (PN remaps to LID), so the phone often cannot open either SKDM copy.
        /// </summary>
        /// <param name="addressingMode">
        /// "lid" or "pn". Null keeps both for callers that are not group-scoped.
        /// </param>
        public static IEnumerable<string> OwnOtherPrimaryJids(
            string meId,
            string meLid,
            string addressingMode = null)
        {
            var preferLid = string.Equals(addressingMode, "lid", StringComparison.OrdinalIgnoreCase);
            var preferPn = string.Equals(addressingMode, "pn", StringComparison.OrdinalIgnoreCase);

            var pnUser = JidUtils.GetUser(meId);
            if (!string.IsNullOrEmpty(pnUser) && !preferLid)
            {
                yield return WA.JidEncode(pnUser, JidUtils.ServerWhatsApp, 0);
            }

            var lidUser = JidUtils.GetUser(meLid);
            if (!string.IsNullOrEmpty(lidUser) && !preferPn)
            {
                yield return WA.JidEncode(lidUser, JidUtils.ServerLid, 0);
            }
        }

        /// <summary>True when the JID is our handset in either address space.</summary>
        public static bool IsOwnOtherPrimaryDevice(string deviceJid, string meId, string meLid)
        {
            if (string.IsNullOrEmpty(deviceJid))
            {
                return false;
            }

            foreach (var primary in OwnOtherPrimaryJids(meId, meLid, addressingMode: null))
            {
                if (string.Equals(deviceJid, primary, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLidParticipant(IEnumerable<GroupParticipant> participants)
        {
            foreach (var participant in participants)
            {
                if (participant != null && JidUtils.IsAnyLid(participant.Id))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
