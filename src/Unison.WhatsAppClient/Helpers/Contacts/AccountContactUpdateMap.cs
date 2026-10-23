// =============================================================================
// AccountContactUpdateMap
//
// Socket ContactUpdate → Core AccountContactUpdate at the bridge. Keeps Socket
// types out of IContactService while the session still publishes Socket models.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Socket.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class AccountContactUpdateMap
    {
        public static IReadOnlyList<AccountContactUpdate> From(ContactUpdate contact)
        {
            if (contact == null)
            {
                return new AccountContactUpdate[0];
            }

            return new[]
            {
                new AccountContactUpdate
                {
                    Id = contact.Id,
                    Lid = contact.Lid,
                    PhoneNumber = contact.PhoneNumber,
                    Name = contact.Name,
                    Notify = contact.Notify
                }
            };
        }

        public static IReadOnlyList<AccountContactUpdate> From(IReadOnlyList<ContactUpdate> contacts)
        {
            if (contacts == null || contacts.Count == 0)
            {
                return new AccountContactUpdate[0];
            }

            var mapped = new List<AccountContactUpdate>(contacts.Count);
            for (int i = 0; i < contacts.Count; i++)
            {
                ContactUpdate contact = contacts[i];
                if (contact == null)
                {
                    continue;
                }

                mapped.Add(new AccountContactUpdate
                {
                    Id = contact.Id,
                    Lid = contact.Lid,
                    PhoneNumber = contact.PhoneNumber,
                    Name = contact.Name,
                    Notify = contact.Notify
                });
            }

            return mapped;
        }
    }
}
