// =============================================================================
// SkmsgFanoutPolicy
//
// Where a status@broadcast send differs from a group send, per rc14
// relayMessage (src/Socket/messages-send.ts):
//
//   - The audience is whatever the caller passed in statusJidList, often a
//     whole address book. usync leaves out the people who are not on WhatsApp,
//     and rc14 sends only to the devices usync returned. Filling in device 0
//     for every one it left out meant hundreds of prekey fetches for accounts
//     that do not exist, and the send hung in assert-sessions.
//   - rc14 reads an empty sender-key-memory for status and gives the key to
//     every device each time. The audience changes from one status to the next,
//     and a device marked as holding the key that later lost it would never be
//     able to open our statuses again.
// =============================================================================
namespace Unison.Socket.Messages
{
    public static class SkmsgFanoutPolicy
    {
        /// <summary>Add device 0 for listed members that usync returned nothing for.</summary>
        public static bool FillsMissingPrimaryDevices(bool isStatus)
        {
            return !isStatus;
        }

        /// <summary>Skip devices already recorded as holding the sender key.</summary>
        public static bool UsesSenderKeyMemory(bool isStatus)
        {
            return !isStatus;
        }
    }
}
