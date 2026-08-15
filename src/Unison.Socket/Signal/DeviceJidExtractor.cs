// =============================================================================
// DeviceJidExtractor
//
// Turns a usync device reply into the list of JIDs a message must be encrypted
// for.
//
// The filtering rules look arbitrary and are not: our own current device is
// excluded because we are the one sending, non-zero devices without a key index
// are excluded because addressing them produces a bad request, and a device
// flagged as hosted moves to the hosted server even though it was listed under
// the plain one. Getting any of these wrong shows up as a message that silently
// never arrives on one of the peer's devices.
//
// Ports: rc14 extractDeviceJids in src/Utils/signal.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;
using Unison.Socket.USync;
using Unison.Socket.USync.Protocols;
using Unison.Socket.WABinary;

namespace Unison.Socket.Signal
{
    /// <summary>One addressable device of one account.</summary>
    public sealed class DeviceJid
    {
        public string User { get; set; }

        public int Device { get; set; }

        public string Server { get; set; }

        /// <summary>The full JID to encrypt for.</summary>
        public string Jid { get; set; }
    }

    public static class DeviceJidExtractor
    {
        public static IReadOnlyList<DeviceJid> Extract(
            USyncQueryResult result,
            string myJid,
            string myLid,
            bool excludeZeroDevices)
        {
            var extracted = new List<DeviceJid>();
            if (result == null)
            {
                return extracted;
            }

            var myUser = JidUtils.GetUser(myJid);
            var myLidUser = JidUtils.GetUser(myLid);
            var myDevice = JidUtils.GetDevice(myJid);

            foreach (var entry in result.List)
            {
                ParsedDeviceInfo devices;
                if (!entry.TryGet("devices", out devices) || devices == null)
                {
                    continue;
                }

                var user = JidUtils.GetUser(entry.Id);
                var server = JidUtils.GetServer(entry.Id);
                if (user == null || server == null)
                {
                    continue;
                }

                var isLidSpace = server == JidUtils.ServerLid || server == JidUtils.ServerHostedLid;

                foreach (var device in devices.DeviceList)
                {
                    if (excludeZeroDevices && device.Id == 0)
                    {
                        continue;
                    }

                    // Skip the device we are sending from, but keep our other devices: they need
                    // their own copy of everything we send.
                    var isMe = user == myUser || user == myLidUser;
                    if (isMe && device.Id == myDevice)
                    {
                        continue;
                    }

                    // The server rejects a stanza addressed to a secondary device whose key index
                    // it never announced.
                    if (device.Id != 0 && device.KeyIndex == 0)
                    {
                        continue;
                    }

                    var targetServer = server;
                    if (device.IsHosted)
                    {
                        targetServer = isLidSpace ? JidUtils.ServerHostedLid : JidUtils.ServerHosted;
                    }

                    extracted.Add(new DeviceJid
                    {
                        User = user,
                        Device = device.Id,
                        Server = targetServer,
                        Jid = WA.JidEncode(user, targetServer, device.Id)
                    });
                }
            }

            return extracted;
        }
    }
}
