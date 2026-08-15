// =============================================================================
// ContactUpdate
//
// A partial contact, on the same "null means untouched" rule as ChatUpdate.
//
// Two of the fields deserve a warning. ImgUrl is not a URL here: a picture
// notification only says that the avatar changed, so the value is the sentinel
// "changed" or "removed" and the host is expected to refetch. And Name, Notify
// and VerifiedName are three different things - what the user saved, what the
// contact calls themselves, and what a business had verified - which is why they
// are kept apart instead of collapsed into one display name.
//
// Ports: rc14 Contact in src/Types/Contact.ts, as emitted by contacts.update
// =============================================================================
namespace Unison.Socket.Models
{
    public static class ContactImageState
    {
        /// <summary>The avatar changed; the current one has to be fetched again.</summary>
        public const string Changed = "changed";

        public const string Removed = "removed";
    }

    public sealed class ContactUpdate
    {
        public ContactUpdate()
        {
        }

        public ContactUpdate(string id)
        {
            Id = id;
        }

        public string Id { get; set; }

        /// <summary>The name from the user's own address book.</summary>
        public string Name { get; set; }

        /// <summary>The push name, which is what the contact calls themselves.</summary>
        public string Notify { get; set; }

        public string VerifiedName { get; set; }

        /// <summary>Either a real URL or one of the <see cref="ContactImageState"/> sentinels.</summary>
        public string ImgUrl { get; set; }

        public string Status { get; set; }

        /// <summary>The contact's LID, when the server discloses it alongside the phone number.</summary>
        public string Lid { get; set; }

        /// <summary>
        /// The phone-number jid, when the update names it separately from <see cref="Id"/>. The
        /// two together are what lets the host tie a conversation addressed by LID to a name it
        /// only knows under the number.
        /// </summary>
        public string PhoneNumber { get; set; }
    }
}
