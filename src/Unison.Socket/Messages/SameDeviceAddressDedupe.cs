// =============================================================================
// SameDeviceAddressDedupe
//
// A Status audience is a free list of contacts, so one person can be in it
// under their phone number and under their LID. Usync then lists each of their
// devices twice. Both addresses open the same Signal session, so sealing for
// both burns the ratchet and puts one device in the stanza twice.
//
// The LID copy is the one kept: that is the address space the sender key is
// signed in.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Socket.Signal;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public static class SameDeviceAddressDedupe
    {
        /// <param name="pairedJid">The same device under the other address space, or null when unknown.</param>
        /// <returns>How many entries were removed.</returns>
        public static int Apply(List<DeviceJid> devices, Func<string, string> pairedJid)
        {
            if (devices == null || devices.Count == 0)
            {
                return 0;
            }

            var lids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                if (device != null && JidUtils.IsAnyLid(device.Jid))
                {
                    lids.Add(device.Jid);
                }
            }

            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int before = devices.Count;
            devices.RemoveAll(device =>
            {
                if (device == null || string.IsNullOrEmpty(device.Jid))
                {
                    return false;
                }

                if (!kept.Add(device.Jid))
                {
                    return true;
                }

                if (lids.Count == 0 || !JidUtils.IsAnyPn(device.Jid) || pairedJid == null)
                {
                    return false;
                }

                string lid = pairedJid(device.Jid);
                return !string.IsNullOrEmpty(lid) && lids.Contains(lid);
            });

            return before - devices.Count;
        }
    }
}
