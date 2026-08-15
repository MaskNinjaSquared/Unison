// =============================================================================
// USyncDeviceProtocol
//
// The usync column that lists the devices behind an account.
//
// Every device is a separate Signal session, so this list decides how many
// copies of a message get encrypted and to whom. It is not used by the contact
// work in this phase, but it is the column the send path will need, and it costs
// nothing to carry it now that the columns compose.
//
// Ports: rc14 src/WAUSync/Protocols/USyncDeviceProtocol.ts
// =============================================================================
using System.Collections.Generic;
using Unison.Baileys.Protocol;

namespace Unison.Socket.USync.Protocols
{
    public sealed class DeviceListEntry
    {
        public int Id { get; set; }

        public int KeyIndex { get; set; }

        /// <summary>Hosted devices live on the "hosted" servers and are addressed differently.</summary>
        public bool IsHosted { get; set; }
    }

    public sealed class KeyIndexData
    {
        public long Timestamp { get; set; }

        public byte[] SignedKeyIndex { get; set; }

        public long? ExpectedTimestamp { get; set; }
    }

    public sealed class ParsedDeviceInfo
    {
        public ParsedDeviceInfo()
        {
            DeviceList = new List<DeviceListEntry>();
        }

        public IList<DeviceListEntry> DeviceList { get; private set; }

        public KeyIndexData KeyIndex { get; set; }
    }

    public sealed class USyncDeviceProtocol : IUSyncProtocol
    {
        public string Name
        {
            get { return "devices"; }
        }

        public BinaryNode GetQueryElement()
        {
            return new BinaryNode("devices", new Dictionary<string, string> { { "version", "2" } });
        }

        /// <summary>
        /// Always null: Baileys has not implemented device phashing, so no per-user node is sent
        /// and the server replies with the full list every time.
        /// </summary>
        public BinaryNode GetUserElement(USyncUser user)
        {
            return null;
        }

        public object Parse(BinaryNode node)
        {
            if (node == null || node.Tag != Name || node.GetChild("error") != null)
            {
                return null;
            }

            var info = new ParsedDeviceInfo();

            var deviceList = node.GetChild("device-list");
            if (deviceList != null)
            {
                foreach (var device in deviceList.GetChildren("device"))
                {
                    int id;
                    if (!int.TryParse(device.GetAttribute("id"), out id))
                    {
                        continue;
                    }

                    int keyIndex;
                    int.TryParse(device.GetAttribute("key-index"), out keyIndex);

                    info.DeviceList.Add(new DeviceListEntry
                    {
                        Id = id,
                        KeyIndex = keyIndex,
                        IsHosted = device.GetAttribute("is_hosted") == "true"
                    });
                }
            }

            var keyIndexNode = node.GetChild("key-index-list");
            if (keyIndexNode != null)
            {
                long timestamp;
                long.TryParse(keyIndexNode.GetAttribute("ts"), out timestamp);

                long expected;
                var hasExpected = long.TryParse(keyIndexNode.GetAttribute("expected_ts"), out expected);

                info.KeyIndex = new KeyIndexData
                {
                    Timestamp = timestamp,
                    SignedKeyIndex = keyIndexNode.GetContentBytes(),
                    ExpectedTimestamp = hasExpected ? (long?)expected : null
                };
            }

            return info;
        }
    }
}
