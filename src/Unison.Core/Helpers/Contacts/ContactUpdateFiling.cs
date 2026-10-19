// =============================================================================
// ContactUpdateFiling
//
// How one contact from an app-state snapshot, a history chunk or a group's
// participant list is read before anything is filed: which LID/phone pair it
// names, which name it carries, and under which addresses that name goes.
//
// The contact's own Id fills whichever half the update left out, since the index
// carries whichever address the action was filed under. The name is filed under
// every address it is known by rather than trusting the alias to resolve in each
// direction: whichever one a chat happens to be keyed by, the lookup lands.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    public sealed class ContactUpdateReading
    {
        private static readonly IReadOnlyList<string> NoAddresses = new string[0];

        internal ContactUpdateReading(string lid, string phone, string name, IReadOnlyList<string> nameAddresses)
        {
            Lid = lid;
            Phone = phone;
            Name = name;
            NameAddresses = nameAddresses ?? NoAddresses;
        }

        public string Lid { get; }

        public string Phone { get; }

        /// <summary>The name to file, or null when the contact carries none.</summary>
        public string Name { get; }

        /// <summary>Whether both halves are known. Reported whether or not the contact is named.</summary>
        public bool HasAliasPair => !string.IsNullOrEmpty(Lid) && !string.IsNullOrEmpty(Phone);

        /// <summary>The addresses to file <see cref="Name"/> under; empty when there is no name.</summary>
        public IReadOnlyList<string> NameAddresses { get; }
    }

    public static class ContactUpdateFiling
    {
        public static ContactUpdateReading Read(string id, string lid, string phone, string name, string notify)
        {
            bool idIsLid = JidHelper.IsLidJid(id);

            if (string.IsNullOrEmpty(phone) && !idIsLid)
            {
                phone = id;
            }

            if (string.IsNullOrEmpty(lid) && idIsLid)
            {
                lid = id;
            }

            string chosen = string.IsNullOrWhiteSpace(name) ? notify : name;
            if (string.IsNullOrWhiteSpace(chosen))
            {
                return new ContactUpdateReading(lid, phone, null, null);
            }

            var addresses = new List<string>(3);
            if (!string.IsNullOrEmpty(id))
            {
                addresses.Add(id);
            }

            AddIfOther(addresses, lid, id);
            AddIfOther(addresses, phone, id);

            return new ContactUpdateReading(lid, phone, chosen, addresses);
        }

        private static void AddIfOther(List<string> addresses, string address, string id)
        {
            if (!string.IsNullOrEmpty(address) && !string.Equals(address, id, StringComparison.OrdinalIgnoreCase))
            {
                addresses.Add(address);
            }
        }
    }
}
