// =============================================================================
// PersonNamePersistGate
//
// Whether a remembered push name should hit SQLite. History sync repeats the
// same label on every message; AddressBook names stay sticky.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PersonNamePersistGate
    {
        public static bool ShouldPersist(Person cached, string sanitizedName)
        {
            if (string.IsNullOrWhiteSpace(sanitizedName))
            {
                return false;
            }

            if (cached != null &&
                string.Equals(cached.Name, sanitizedName, StringComparison.Ordinal))
            {
                return false;
            }

            if (cached != null && cached.Source == PersonSource.AddressBook)
            {
                return false;
            }

            return true;
        }
    }
}
